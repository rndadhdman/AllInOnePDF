<#
.SYNOPSIS
  Builds AllInOne PDF as a single-file Flatpak bundle (AllInOnePdf-<version>.flatpak) with flatpak-builder in Docker.

.DESCRIPTION
  1. Stages the manifest (build/flatpak/com.therandomadmin.AllInOnePdf.yml) with its sources in a temp folder: the
     linux-x64 publish folder (portable.txt is left out), the launcher, the shared desktop file and metainfo from
     build/linux/share ({VERSION} and {DATE} filled in a staged copy) and the 256x256 icon.
  2. Builds the image docker/Dockerfile.flatpak for the manifest's runtime-version unless it already exists (the GNOME
     runtime and SDK, about 1.5 GB, are downloaded from Flathub into an image layer once).
  3. Runs build/flatpak/build-in-container.sh in a --privileged container (bubblewrap needs it): validates the desktop
     file and metainfo, runs flatpak-builder and flatpak build-bundle, then installs the bundle and checks --help,
     --check-assets, --paths, flatpak info and the installed files.
  Everything the container prints goes to <Output>/AllInOnePdf-<version>.flatpak.log.

.EXAMPLE
  .\build\publish.ps1 -Rid linux-x64 -Version 1.2.0 -NoZip
  .\build\flatpak\build-flatpak.ps1 -PublishDir publish\AllInOnePdf-linux-x64\app -Version 1.2.0 -Output publish
#>
[CmdletBinding()]
param(
    # The self-contained linux-x64 publish folder (the one holding the AllInOnePdf executable).
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$Output = "publish",
    # Release date for the metainfo (yyyy-MM-dd); defaults to today (UTC).
    [string]$Date = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd"),
    # Skip installing and checking the bundle inside the container.
    [switch]$NoValidate,
    # Rebuild the Docker image even when it exists.
    [switch]$RebuildImage
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$appId = "com.therandomadmin.AllInOnePdf"
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' must look like 1.2.3 or 1.2.3-rc.1" }
if ($Date -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "Date '$Date' must look like 2026-11-07" }
$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir "AllInOnePdf"))) { throw "$PublishDir has no AllInOnePdf executable: pass the linux-x64 publish folder" }
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker is needed to build the Flatpak" }

$manifest = Join-Path $PSScriptRoot "$appId.yml"
$gnome = [regex]::Match((Get-Content $manifest -Raw), "(?m)^runtime-version:\s*'?([0-9.]+)'?").Groups[1].Value
if (-not $gnome) { throw "No runtime-version in $manifest" }
$image = "allinonepdf-flatpak:$gnome"
$log = Join-Path $Output "AllInOnePdf-$Version.flatpak.log"
Set-Content -Path $log -Value "AllInOne PDF Flatpak $Version, GNOME $gnome, $(Get-Date -Format o)" -Encoding utf8

function Invoke-Logged([string[]]$arguments) {
    # Docker writes progress to stderr; that is not an error. Everything goes to the log, nothing to the pipeline.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & docker @arguments *>> $log
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    return $code
}

$started = Get-Date
& docker image inspect $image *> $null
if ($RebuildImage -or $LASTEXITCODE -ne 0) {
    Write-Host "Building image $image (downloads the GNOME $gnome runtime and SDK once)" -ForegroundColor Cyan
    $code = Invoke-Logged @("build", "--progress=plain", "-f", (Join-Path $repo "docker/Dockerfile.flatpak"),
        "--build-arg", "GNOME_VERSION=$gnome", "-t", $image, (Join-Path $repo "docker"))
    if ($code -ne 0) { Get-Content $log -Tail 30 | Write-Host; throw "docker build failed (exit $code), see $log" }
}

# Stage under the system temp folder: Docker Desktop shares the system drive, not necessarily every drive.
$stage = Join-Path ([IO.Path]::GetTempPath()) ("aio-flatpak-" + [Guid]::NewGuid().ToString("N"))
$src = Join-Path $stage "src"
$out = Join-Path $stage "out"
New-Item -ItemType Directory -Force $src, $out | Out-Null
try {
    Write-Host "Staging $PublishDir" -ForegroundColor Cyan
    # portable.txt and debug symbols stay out. robocopy on Windows (the staged paths pass 260 characters, which
    # Windows PowerShell's Copy-Item cannot handle); Copy-Item elsewhere (CI runs this on Linux with pwsh).
    $stagedPublish = Join-Path $src "publish"
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        & robocopy $PublishDir $stagedPublish /E /NFL /NDL /NJH /NJS /NP /XF portable.txt *.pdb *.dbg | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    } else {
        Copy-Item $PublishDir $stagedPublish -Recurse
        Get-ChildItem $stagedPublish -Recurse -Force -Include portable.txt, *.pdb, *.dbg | Remove-Item -Force
    }
    $share = Join-Path $repo "build/linux/share"
    $text = @{
        "$appId.yml"          = Get-Content $manifest -Raw
        "allinonepdf.sh"      = Get-Content (Join-Path $PSScriptRoot "allinonepdf.sh") -Raw
        "build.sh"            = Get-Content (Join-Path $PSScriptRoot "build-in-container.sh") -Raw
        "$appId.desktop"      = Get-Content (Join-Path $share "$appId.desktop") -Raw
        "$appId.metainfo.xml" = (Get-Content (Join-Path $share "$appId.metainfo.xml") -Raw).Replace("{VERSION}", $Version).Replace("{DATE}", $Date)
    }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    foreach ($name in $text.Keys) {
        [IO.File]::WriteAllText((Join-Path $src $name), ($text[$name] -replace "`r`n", "`n"), $utf8)
    }
    Copy-Item (Join-Path $repo "src/AllInOnePdf.Desktop/wwwroot/app.png") (Join-Path $src "$appId.png")

    Write-Host "Running flatpak-builder in $image (log: $log)" -ForegroundColor Cyan
    $run = @("run", "--rm", "--privileged", "-v", "${src}:/work/src", "-v", "${out}:/work/out", $image,
        "sh", "/work/src/build.sh", $Version)
    if ($NoValidate) { $run += "--no-validate" }
    $code = Invoke-Logged $run
    $bundle = Join-Path $out "AllInOnePdf-$Version.flatpak"
    if ($code -ne 0 -or -not (Test-Path $bundle)) {
        Get-Content $log -Tail 40 | Write-Host
        throw "Flatpak build failed (exit $code), see $log"
    }
    $target = Join-Path $Output "AllInOnePdf-$Version.flatpak"
    Move-Item $bundle $target -Force
    $elapsed = (Get-Date) - $started
    Write-Host ("Flatpak: {0} ({1:n1} MB, {2:mm\:ss})" -f $target, ((Get-Item $target).Length / 1MB), $elapsed) -ForegroundColor Green
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
exit 0
