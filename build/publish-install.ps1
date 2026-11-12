<#
.SYNOPSIS
  Publishes the self-contained app for an installer (MSI, deb, rpm, Flatpak): the plain publish output, without the
  portable marker, so the installed app keeps its data in the user's profile (%APPDATA%\AllInOnePdf or
  ~/.config/AllInOnePdf).

.DESCRIPTION
  Same publish settings as publish.ps1 (version stamping, no debug symbols, English resources only), plus LICENSE and
  THIRD-PARTY-NOTICES.md beside the executable. The packaging scripts take the folder this writes:
    build/msi/build-msi.ps1          -PublishDir <folder> -Version <x.y.z> -Output <folder>
    build/linux/build-packages.ps1   -PublishDir <folder> -Version <x.y.z> -Output <folder>
    build/flatpak/build-flatpak.ps1  -PublishDir <folder> -Version <x.y.z> -Output <folder>

.EXAMPLE
  .\build\publish-install.ps1 -Rid win-x64 -Version 1.2.0 -Output publish\install-win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Rid,
    [string]$Configuration = "Release",
    [Parameter(Mandatory)]
    [string]$Output,
    # Semantic version to stamp on the binaries; defaults to Directory.Build.props.
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$project = "src/AllInOnePdf.Desktop/AllInOnePdf.Desktop.csproj"
$version = $Version
if (-not $version) {
    $version = ([xml](Get-Content "Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $version) { $version = "1.0.0" }
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$version' must look like 1.2.3 or 1.2.3-rc.1" }
$numericVersion = ($version -replace '-.*$', '') + ".0"

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null
Write-Host "Publishing $Rid $version -> $Output" -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r $Rid --self-contained true -o $Output --nologo `
    -p:DebugType=none -p:GenerateDocumentationFile=false -p:SatelliteResourceLanguages=en `
    "-p:Version=$version" "-p:FileVersion=$numericVersion" "-p:AssemblyVersion=$numericVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Rid" }

# Native debug symbols (e.g. libSkiaSharp.pdb, 85 MB) are useless to users; drop them.
Get-ChildItem $Output -Recurse -Include *.pdb, *.dbg, *.dSYM | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
# No portable marker: an installed app keeps its data in the user's profile.
Remove-Item (Join-Path $Output "portable.txt") -Force -ErrorAction SilentlyContinue
Copy-Item "LICENSE" (Join-Path $Output "LICENSE.txt") -Force
if (Test-Path "THIRD-PARTY-NOTICES.md") { Copy-Item "THIRD-PARTY-NOTICES.md" (Join-Path $Output "THIRD-PARTY-NOTICES.md") -Force }

$count = (Get-ChildItem $Output -Recurse -File | Measure-Object).Count
Write-Host "Published $count files." -ForegroundColor Green
$global:LASTEXITCODE = 0
exit 0
