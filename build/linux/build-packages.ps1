<#
.SYNOPSIS
  Builds the AllInOne PDF .deb and .rpm packages from a self-contained linux-x64 publish folder, using Docker.

.DESCRIPTION
  Stages the inputs in a temporary folder (under the system temp folder, which Docker Desktop shares), then:
    1. scales the 256 px icon to 128/64/48/32/16 px   (docker/Dockerfile.deb-build, ubuntu:24.04 + Pillow)
    2. dpkg-deb --build --root-owner-group             (same image)       -> allinonepdf_<version>_amd64.deb
    3. rpmbuild -bb build/linux/allinonepdf.spec.in    (docker/Dockerfile.rpm-build, fedora:41) -> allinonepdf-<version>-1.x86_64.rpm
  Both packages install the app to /opt/allinonepdf, the launcher to /usr/bin/allinonepdf, and the shared desktop
  entry, AppStream metainfo and icons from build/linux/share under /usr/share. The tree is assembled inside each
  container (build/linux/stage-tree.sh) so file permissions are right even though the inputs come from Windows.
  A pre-release version (1.2.0-rc.1) becomes 1.2.0~rc.1, which sorts before 1.2.0 in both dpkg and rpm.

.EXAMPLE
  .\build\publish.ps1 -Rid linux-x64 -Version 1.2.0
  .\build\linux\build-packages.ps1 -PublishDir publish\AllInOnePdf-linux-x64\app -Version 1.2.0 -Output publish
#>
[CmdletBinding()]
param(
    # The linux-x64 publish output (the folder holding the AllInOnePdf executable). The portable folder made by
    # publish.ps1 (publish\AllInOnePdf-linux-x64, with the app in app\) is accepted too.
    [Parameter(Mandatory = $true)][string]$PublishDir,
    # x.y.z or x.y.z-label; defaults to Directory.Build.props.
    [string]$Version,
    [string]$Output = "publish",
    # Release date written to the AppStream metainfo and the changelogs (ISO yyyy-MM-dd); defaults to today.
    [string]$Date = (Get-Date -Format "yyyy-MM-dd"),
    # Keep the Docker logs here; by default they are written to the staging folder and shown only on failure.
    [string]$LogDir,
    [switch]$SkipDeb,
    [switch]$SkipRpm
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $here)
$inv = [Globalization.CultureInfo]::InvariantCulture
$id = "com.therandomadmin.AllInOnePdf"

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $repo "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' must look like 1.2.3 or 1.2.3-rc.1" }
$pkgVersion = $Version -replace '-', '~'
$releaseDate = [DateTime]::ParseExact($Date, "yyyy-MM-dd", $inv)

$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir "AllInOnePdf")) -and (Test-Path (Join-Path $PublishDir "app/AllInOnePdf"))) {
    $PublishDir = Join-Path $PublishDir "app"
}
if (-not (Test-Path (Join-Path $PublishDir "AllInOnePdf"))) { throw "$PublishDir does not hold a linux-x64 publish (no AllInOnePdf executable)." }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker is required to build the Linux packages." }
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path

function Write-Lf([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, ($text -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
}
function Read-Text([string]$path) { [IO.File]::ReadAllText($path) -replace "`r`n", "`n" }

# Summary and description come from the shared AppStream metainfo, so every package says the same thing.
$metainfoTemplate = Read-Text (Join-Path $here "share/$id.metainfo.xml")
$meta = [xml]$metainfoTemplate
$summary = $meta.component.summary.Trim()
$paragraphs = @($meta.component.description.p | ForEach-Object { ($_ -replace '\s+', ' ').Trim() })
function Format-Wrapped([string]$text, [int]$width, [string]$indent) {
    $lines = @(); $line = ""
    foreach ($word in $text -split ' ') {
        if ($line -and ($line.Length + 1 + $word.Length) -gt $width) { $lines += $indent + $line; $line = $word }
        elseif ($line) { $line += " " + $word } else { $line = $word }
    }
    if ($line) { $lines += $indent + $line }
    return $lines -join "`n"
}
$debDescription = ($paragraphs | ForEach-Object { Format-Wrapped $_ 78 " " }) -join "`n .`n"
$rpmDescription = ($paragraphs | ForEach-Object { Format-Wrapped $_ 78 "" }) -join "`n`n"

# --- stage the inputs ---
$stage = Join-Path ([IO.Path]::GetTempPath()) ("aio-pkg-" + [Guid]::NewGuid().ToString("N").Substring(0, 12))
$in = Join-Path $stage "in"
$out = Join-Path $stage "out"
New-Item -ItemType Directory -Force $in, $out, (Join-Path $in "icons") | Out-Null
if (-not $LogDir) { $LogDir = Join-Path $stage "logs" }
New-Item -ItemType Directory -Force $LogDir | Out-Null
$LogDir = (Resolve-Path $LogDir).Path

$succeeded = $false
try {
    Write-Host "Staging $PublishDir -> $stage" -ForegroundColor Cyan
    if (Get-Command robocopy -ErrorAction SilentlyContinue) {
        # robocopy copes with the long wwwroot paths that Copy-Item on Windows PowerShell 5.1 does not.
        & robocopy $PublishDir (Join-Path $in "app") /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
        $global:LASTEXITCODE = 0
    } else {
        Copy-Item $PublishDir (Join-Path $in "app") -Recurse
    }

    Write-Lf (Join-Path $in "share/$id.desktop") (Read-Text (Join-Path $here "share/$id.desktop"))
    Write-Lf (Join-Path $in "share/$id.metainfo.xml") ($metainfoTemplate -replace '\{VERSION\}', $Version -replace '\{DATE\}', $Date)
    Write-Lf (Join-Path $in "allinonepdf") (Read-Text (Join-Path $here "allinonepdf"))
    Write-Lf (Join-Path $in "stage-tree.sh") (Read-Text (Join-Path $here "stage-tree.sh"))
    Write-Lf (Join-Path $in "allinonepdf.1") (Read-Text (Join-Path $here "allinonepdf.1"))
    Write-Lf (Join-Path $in "build-deb.sh") (Read-Text (Join-Path $here "build-deb.sh"))
    Write-Lf (Join-Path $in "LICENSE") (Read-Text (Join-Path $repo "LICENSE"))
    Write-Lf (Join-Path $in "THIRD-PARTY-NOTICES.md") (Read-Text (Join-Path $repo "THIRD-PARTY-NOTICES.md"))
    Copy-Item (Join-Path $repo "src/AllInOnePdf.Desktop/wwwroot/app.png") (Join-Path $in "icons/256.png")

    # deb metadata
    Write-Lf (Join-Path $in "deb/control") ((Read-Text (Join-Path $here "control.in")) `
        -replace '\{VERSION\}', $pkgVersion -replace '\{SUMMARY\}', $summary -replace '\{DESCRIPTION\}', $debDescription.Replace('$', '$$'))
    Write-Lf (Join-Path $in "deb/copyright") (@(
        "AllInOne PDF",
        "Source: https://github.com/rndadhdman/AllInOnePDF",
        "",
        "The program is distributed under the MIT License below. It bundles the .NET runtime and third-party",
        "components under their own licences; they are listed in /usr/share/doc/allinonepdf/THIRD-PARTY-NOTICES.md.",
        "",
        (Read-Text (Join-Path $repo "LICENSE")).TrimEnd()) -join "`n")
    $trailer = " -- AllInOne PDF contributors <noreply@github.com>  " + $releaseDate.ToString("ddd, dd MMM yyyy", $inv) + " 00:00:00 +0000"
    Write-Lf (Join-Path $in "deb/changelog") (@(
        "allinonepdf ($pkgVersion) stable; urgency=medium",
        "",
        "  * New upstream release $Version. Release notes:",
        "    https://github.com/rndadhdman/AllInOnePDF/blob/main/CHANGELOG.md",
        "",
        $trailer,
        "") -join "`n")
    Write-Lf (Join-Path $in "deb/lintian-overrides") (Read-Text (Join-Path $here "lintian-overrides"))

    # rpm spec
    Write-Lf (Join-Path $in "allinonepdf.spec") ((Read-Text (Join-Path $here "allinonepdf.spec.in")) `
        -replace '\{VERSION\}', $pkgVersion -replace '\{SUMMARY\}', $summary -replace '\{RPMDATE\}', $releaseDate.ToString("ddd MMM dd yyyy", $inv) `
        -replace '\{DESCRIPTION\}', $rpmDescription.Replace('$', '$$'))

    # --- run the containers ---
    function Invoke-Docker([string]$step, [string[]]$dockerArgs) {
        $log = Join-Path $LogDir "$step.log"
        Write-Host "  $step" -ForegroundColor DarkCyan
        # Docker writes pull progress to stderr; that is not an error, so do not let it stop the script.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        & docker @dockerArgs *> $log
        $code = $LASTEXITCODE
        $ErrorActionPreference = $previous
        if ($code -ne 0) {
            Get-Content $log -Tail 40 | ForEach-Object { Write-Host "    $_" }
            throw "$step failed (exit $code); log: $log"
        }
    }
    $dockerDir = Join-Path $repo "docker"
    $mount = "${stage}:/work"
    $deb = "allinonepdf_${pkgVersion}_amd64.deb"
    $rpm = "allinonepdf-${pkgVersion}-1.x86_64.rpm"

    Write-Host "Building Linux packages $pkgVersion" -ForegroundColor Cyan
    Invoke-Docker "image-deb" @("build", "-t", "aio-pkg-deb", "-f", (Join-Path $dockerDir "Dockerfile.deb-build"), $dockerDir)
    Invoke-Docker "icons" @("run", "--rm", "-v", $mount, "aio-pkg-deb", "python3", "-c",
        "from PIL import Image; im = Image.open('/work/in/icons/256.png').convert('RGBA'); assert im.size == (256, 256), im.size; [im.resize((s, s), Image.LANCZOS).save('/work/in/icons/%d.png' % s, optimize=True) for s in (128, 64, 48, 32, 16)]")

    if (-not $SkipDeb) {
        # Shell work lives in .sh files: Windows PowerShell 5.1 drops embedded quotes from native arguments.
        Invoke-Docker "deb" @("run", "--rm", "-v", $mount, "aio-pkg-deb", "sh", "/work/in/build-deb.sh", $deb)
        Move-Item (Join-Path $out $deb) (Join-Path $Output $deb) -Force
        Write-Host ("deb: {0} ({1:n1} MB)" -f (Join-Path $Output $deb), ((Get-Item (Join-Path $Output $deb)).Length / 1MB)) -ForegroundColor Green
    }
    if (-not $SkipRpm) {
        Invoke-Docker "image-rpm" @("build", "-t", "aio-pkg-rpm", "-f", (Join-Path $dockerDir "Dockerfile.rpm-build"), $dockerDir)
        $script = "set -eu; rpmbuild -bb --define '_topdir /tmp/rpmbuild' /work/in/allinonepdf.spec; cp /tmp/rpmbuild/RPMS/x86_64/$rpm /work/out/"
        Invoke-Docker "rpm" @("run", "--rm", "-v", $mount, "aio-pkg-rpm", "sh", "-c", $script)
        Move-Item (Join-Path $out $rpm) (Join-Path $Output $rpm) -Force
        Write-Host ("rpm: {0} ({1:n1} MB)" -f (Join-Path $Output $rpm), ((Get-Item (Join-Path $Output $rpm)).Length / 1MB)) -ForegroundColor Green
    }
    $succeeded = $true
}
finally {
    # The staged copy of the app always goes; logs inside the staging folder stay only after a failure.
    if ($succeeded -or -not $LogDir.StartsWith($stage)) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
    else {
        Remove-Item $in, $out -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Logs kept in $LogDir" -ForegroundColor Yellow
    }
}
$global:LASTEXITCODE = 0
