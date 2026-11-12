<#
.SYNOPSIS
  Builds the AllInOne PDF Windows installer (AllInOnePdf-<version>-win-x64.msi) with WiX Toolset v5.

.DESCRIPTION
  Packs a self-contained win-x64 publish folder into a per-machine MSI. WiX is the repo-local dotnet tool in
  .config/dotnet-tools.json (this script runs `dotnet tool restore`); its UI and Util extensions are pinned below and
  cached in build/msi/.wix (ignored by git), so nothing is installed machine-wide.

  The publish folder may be a plain `dotnet publish` output or the portable package's App\AllInOnePdf folder:
  portable.txt and *.pdb are excluded by the authoring, so the installed app keeps its data in %APPDATA%\AllInOnePdf.

.EXAMPLE
  dotnet publish src/AllInOnePdf.Desktop -c Release -r win-x64 --self-contained -o publish/win-x64 -p:Version=1.1.0 -p:DebugType=none
  .\build\msi\build-msi.ps1 -PublishDir publish/win-x64 -Version 1.1.0 -Output publish
  # -> publish\AllInOnePdf-1.1.0-win-x64.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PublishDir,
    # Semantic version (1.2.3 or 1.2.3-rc.1); defaults to Directory.Build.props. The MSI gets 1.2.3.0.
    [string]$Version,
    [string]$Output = "publish",
    # Skip the ICE validation pass (it needs the Windows Installer service; on by default).
    [switch]$NoValidate
)

$ErrorActionPreference = "Stop"
$wixVersion = "5.0.2"
$here = $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $here)

function Invoke-Native([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

$publish = (Resolve-Path $PublishDir).Path.TrimEnd('\')
if (-not (Test-Path (Join-Path $publish "AllInOnePdf.exe"))) { throw "$publish does not contain AllInOnePdf.exe (publish with -r win-x64 first)" }
if (-not (Test-Path (Join-Path $publish "wwwroot\app.ico"))) { throw "$publish does not contain wwwroot\app.ico" }

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $repo "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' must look like 1.2.3 or 1.2.3-rc.1" }
$numeric = ($Version -replace '-.*$', '') + ".0"
$parts = $numeric.Split('.') | ForEach-Object { [int]$_ }
if ($parts[0] -gt 255 -or $parts[1] -gt 255 -or $parts[2] -gt 65535) { throw "MSI versions are limited to 255.255.65535 ($Version)" }

New-Item -ItemType Directory -Force $Output | Out-Null
$outDir = (Resolve-Path $Output).Path
$msi = Join-Path $outDir "AllInOnePdf-$Version-win-x64.msi"
Write-Host "Building $msi (MSI version $numeric) from $publish" -ForegroundColor Cyan

# The licence dialog wants RTF: one paragraph per blank-line-separated block of LICENSE.
$work = Join-Path ([IO.Path]::GetTempPath()) ("aio-msi-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $text = [IO.File]::ReadAllText((Join-Path $repo "LICENSE")) -replace "`r`n", "`n"
    $paragraphs = ($text.Trim() -split "`n\s*`n") | ForEach-Object {
        $p = ($_ -replace "`n", " ").Trim() -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}'
        # Anything outside ASCII as \uN? escapes.
        $sb = New-Object Text.StringBuilder
        foreach ($c in $p.ToCharArray()) { if ([int]$c -lt 128) { [void]$sb.Append($c) } else { [void]$sb.Append("\u$([int]$c)?") } }
        $sb.ToString()
    }
    $rtf = "{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}\viewkind4\uc1\pard\sa160\f0\fs18 " +
        (($paragraphs | Select-Object -First 1) -replace '^', '\b ') + "\b0\par`r`n" +
        ((($paragraphs | Select-Object -Skip 1) | ForEach-Object { "$_\par" }) -join "`r`n") + "`r`n}"
    $licenseRtf = Join-Path $work "License.rtf"
    [IO.File]::WriteAllText($licenseRtf, $rtf, [Text.Encoding]::ASCII)

    Push-Location $here
    try {
        Invoke-Native "dotnet tool restore" { dotnet tool restore }
        # Per-folder extension cache (build/msi/.wix/extensions); never -g.
        Invoke-Native "wix extension add" { dotnet wix extension add "WixToolset.UI.wixext/$wixVersion" "WixToolset.Util.wixext/$wixVersion" }
        $wixArgs = @(
            "build", "AllInOnePdf.wxs", "UI.wxs",
            "-arch", "x64",
            "-ext", "WixToolset.UI.wixext/$wixVersion",
            "-ext", "WixToolset.Util.wixext/$wixVersion",
            "-culture", "en-US", "-loc", "Package.en-us.wxl",
            "-d", "PublishDir=$publish",
            "-d", "Version=$numeric",
            "-d", "LicenseRtf=$licenseRtf",
            "-d", "RepoRoot=$repo",
            "-intermediatefolder", (Join-Path $work "obj"),
            "-pdbtype", "none",
            "-wx",
            "-o", $msi
        )
        Invoke-Native "wix build" { dotnet wix @wixArgs }
        if (-not $NoValidate) {
            # ICE60 flags the app's bundled .ttf fonts (Inter, Literata, JetBrains Mono in wwwroot) because they carry a
            # version but are not in the Font table. They are private app files, not fonts installed into Windows, so
            # the rule does not apply; everything else must pass.
            Invoke-Native "wix msi validate" { dotnet wix msi validate -wx -sice ICE60 $msi }
        }
    } finally {
        Pop-Location
    }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ("MSI: {0} ({1:n1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB)) -ForegroundColor Green
$global:LASTEXITCODE = 0
exit 0
