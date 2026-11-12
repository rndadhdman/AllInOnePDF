<#
.SYNOPSIS
  Builds portable, self-contained AllInOne PDF packages.

.DESCRIPTION
  Each package is one launcher at the root with everything else in a subfolder beside it:

  Windows  -> publish\AllInOnePdfPortable\              (PortableApps.com format)
                AllInOnePdfPortable.exe                   launcher (.NET Framework 4.8, which is part of Windows)
                App\AllInOnePdf\                          the self-contained app and its runtime
                App\AppInfo\                              appinfo.ini and icons for the PortableApps.com menu
                Data\                                     settings, themes, history, default output
              publish\AllInOnePdfPortable-win-x64.zip
  Linux    -> publish\AllInOnePdf-linux-x64\
                AllInOnePdf.sh                            launcher
                app\                                      the self-contained app and its runtime
                Data\
              publish\AllInOnePdf-linux-x64.tar.gz      (created with Docker so the executable bit is preserved;
                                                         without Docker the folder is still produced)
              publish\AllInOnePdf-linux-x64.zip         (same files; zip loses the executable bit, see README-Linux.txt)
  The .deb and .rpm are built from the app folder by build\linux\build-packages.ps1.
  The app finds Data through portable.txt in its own folder (..\..\Data on Windows, ../Data on Linux).

.EXAMPLE
  .\build\publish.ps1                 # both platforms
  .\build\publish.ps1 -Rid win-x64    # Windows only
  .\build\publish.ps1 -Version 1.2.0  # stamp a release version (the release workflow passes the tag's version)
#>
[CmdletBinding()]
param(
    [ValidateSet("win-x64", "linux-x64", "all")]
    [string]$Rid = "all",
    [string]$Configuration = "Release",
    [string]$Output = "publish",
    # Semantic version to stamp on the binaries and the package metadata; defaults to Directory.Build.props.
    [string]$Version,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$project = "src/AllInOnePdf.Desktop/AllInOnePdf.Desktop.csproj"
$launcherProject = "src/AllInOnePdf.Launcher/AllInOnePdf.Launcher.csproj"
$version = $Version
if (-not $version) {
    $version = ([xml](Get-Content "Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $version) { $version = "1.0.0" }
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$version' must look like 1.2.3 or 1.2.3-rc.1" }
# PortableApps.com and the Windows file version want four numeric parts; pre-release labels are dropped there.
$numericVersion = ($version -replace '-.*$', '') + ".0"
$versionProps = @("-p:Version=$version", "-p:FileVersion=$numericVersion", "-p:AssemblyVersion=$numericVersion")
$templates = Join-Path $PSScriptRoot "portable"
$notices = "THIRD-PARTY-NOTICES.md"
New-Item -ItemType Directory -Force $Output | Out-Null
Write-Host "Version $version" -ForegroundColor Cyan

function Remove-Tree([string]$path) {
    # Deep trees occasionally fail with "directory is not empty" on Windows while handles close; retry, then rmdir.
    for ($i = 0; $i -lt 4 -and (Test-Path $path); $i++) {
        try { Remove-Item $path -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 1 }
    }
    if (Test-Path $path) { & cmd /c rmdir /s /q "$path" | Out-Null }
    if (Test-Path $path) { throw "Could not remove $path (is the app still running from it?)" }
}

function New-TempDir([string]$prefix) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ($prefix + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $dir | Out-Null
    return $dir
}

function Publish-Rid([string]$rid, [string]$dest, [string]$dataRelative) {
    Write-Host "Publishing $rid -> $dest" -ForegroundColor Cyan
    Remove-Tree $dest
    dotnet publish $project -c $Configuration -r $rid --self-contained true -o $dest --nologo `
        -p:DebugType=none -p:GenerateDocumentationFile=false -p:SatelliteResourceLanguages=en @versionProps
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
    if (Test-Path $notices) { Copy-Item $notices (Join-Path $dest "THIRD-PARTY-NOTICES.md") -Force }
    # Native debug symbols (e.g. libSkiaSharp.pdb, 85 MB) are useless to users; drop them.
    Get-ChildItem $dest -Recurse -Include *.pdb, *.dbg, *.dSYM | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    # Portable marker: first non-comment line = data folder relative to the app folder.
    Set-Content -Path (Join-Path $dest "portable.txt") -Encoding ascii -Value @(
        "# AllInOne PDF portable mode. First non-empty line = data folder relative to this file.",
        $dataRelative)
}

function Build-Launcher([string]$destExe) {
    # Tiny .NET Framework 4.8 exe (see src/AllInOnePdf.Launcher): the only file at the root of the Windows package.
    Write-Host "Building launcher -> $destExe" -ForegroundColor Cyan
    $tmp = New-TempDir "aio-launcher-"
    dotnet build $launcherProject -c $Configuration -o $tmp --nologo -v quiet @versionProps
    if ($LASTEXITCODE -ne 0) { throw "launcher build failed" }
    Copy-Item (Join-Path $tmp "AllInOnePdfPortable.exe") $destExe -Force
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Rid -in @("win-x64", "all")) {
    $root = Join-Path $Output "AllInOnePdfPortable"
    $app = Join-Path $root "App\AllInOnePdf"
    Publish-Rid "win-x64" $app "..\..\Data"
    New-Item -ItemType Directory -Force (Join-Path $root "Data") | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $root "App\AppInfo") | Out-Null
    (Get-Content (Join-Path $templates "appinfo.ini")) -replace "{VERSION}", $numericVersion -replace "{DISPLAYVERSION}", $version | Set-Content (Join-Path $root "App\AppInfo\appinfo.ini") -Encoding ascii
    Copy-Item (Join-Path $templates "installer.ini") (Join-Path $root "App\AppInfo\installer.ini")
    if (Test-Path (Join-Path $app "wwwroot\app.ico")) { Copy-Item (Join-Path $app "wwwroot\app.ico") (Join-Path $root "App\AppInfo\appicon.ico") }
    if (Test-Path (Join-Path $app "wwwroot\app.png")) {
        Copy-Item (Join-Path $app "wwwroot\app.png") (Join-Path $root "App\AppInfo\appicon_256.png")
    }
    Copy-Item "LICENSE" (Join-Path $app "LICENSE.txt")
    Copy-Item (Join-Path $templates "README-Windows.txt") (Join-Path $root "README.txt")
    Build-Launcher (Join-Path $root "AllInOnePdfPortable.exe")
    # Earlier packages had a .cmd launcher at the root; only the exe belongs there now.
    Remove-Item (Join-Path $root "AllInOnePdfPortable.cmd") -Force -ErrorAction SilentlyContinue
    if (-not $NoZip) {
        $zip = Join-Path $Output "AllInOnePdfPortable-win-x64.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        # Zip a staging copy with an empty Data folder: a previous test run leaves settings and a browser cache there.
        $stage = New-TempDir "aio-win-"
        $stageRoot = Join-Path $stage "AllInOnePdfPortable"
        New-Item -ItemType Directory -Force $stageRoot | Out-Null
        & robocopy $root $stageRoot /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
        $stageData = Join-Path $stageRoot "Data"
        if (Test-Path $stageData) { Remove-Item $stageData -Recurse -Force }
        New-Item -ItemType Directory -Force $stageData | Out-Null
        # ZipFile rather than Compress-Archive: Windows PowerShell 5.1 writes backslash entry names, which other
        # unzip tools turn into files called "App\AllInOnePdf\...". .NET APIs need absolute paths (they ignore Set-Location).
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        # Windows PowerShell runs on .NET Framework, whose ZipFile still writes backslashes unless this switch is off (no-op on pwsh 7).
        [AppContext]::SetSwitch("Switch.System.IO.Compression.ZipFile.UseBackslash", $false)
        $zipFull = Join-Path (Resolve-Path $Output).Path (Split-Path $zip -Leaf)
        [IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $zipFull, [IO.Compression.CompressionLevel]::Optimal, $true)
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ("Windows zip: {0} ({1:n1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
    }
}

if ($Rid -in @("linux-x64", "all")) {
    $dest = Join-Path $Output "AllInOnePdf-linux-x64"
    Remove-Tree $dest
    $app = Join-Path $dest "app"
    Publish-Rid "linux-x64" $app "../Data"
    New-Item -ItemType Directory -Force (Join-Path $dest "Data") | Out-Null
    Copy-Item (Join-Path $templates "AllInOnePdf.sh") (Join-Path $dest "AllInOnePdf.sh")
    Copy-Item (Join-Path $templates "README-Linux.txt") (Join-Path $dest "README.txt")
    Copy-Item (Join-Path $templates "allinonepdf.desktop") (Join-Path $dest "allinonepdf.desktop")
    Copy-Item "LICENSE" (Join-Path $app "LICENSE.txt")
    # Normalise line endings for the text files (git on Windows may have checked them out as CRLF).
    foreach ($f in @("AllInOnePdf.sh", "allinonepdf.desktop", "README.txt", "app/portable.txt")) {
        $p = Join-Path $dest $f
        [IO.File]::WriteAllText($p, ([IO.File]::ReadAllText($p) -replace "`r`n", "`n"))
    }
    if (-not $NoZip) {
        $tar = Join-Path $Output "AllInOnePdf-linux-x64.tar.gz"
        if (Test-Path $tar) { Remove-Item $tar -Force }
        $docker = Get-Command docker -ErrorAction SilentlyContinue
        if ($docker) {
            # Stage under the system temp folder: Docker Desktop shares the system drive by default, but not every drive.
            $stage = New-TempDir "aio-linux-"
            Copy-Item $dest (Join-Path $stage "AllInOnePdf-linux-x64") -Recurse
            # Docker writes progress ("Unable to find image ... locally", pull status) to stderr; that is not an error.
            $previous = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            & docker run --rm -v "${stage}:/out" alpine:3.20 sh -c "cd /out && chmod +x AllInOnePdf-linux-x64/AllInOnePdf.sh AllInOnePdf-linux-x64/app/AllInOnePdf && tar -czf AllInOnePdf-linux-x64.tar.gz AllInOnePdf-linux-x64 && ls -l AllInOnePdf-linux-x64.tar.gz" 2>&1 | ForEach-Object { Write-Verbose "$_" }
            $tarExit = $LASTEXITCODE
            $ErrorActionPreference = $previous
            $staged = Join-Path $stage "AllInOnePdf-linux-x64.tar.gz"
            if ($tarExit -ne 0 -or -not (Test-Path $staged)) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue; throw "tar via docker failed (exit $tarExit)" }
            Move-Item $staged $tar -Force
            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host ("Linux tarball: {0} ({1:n1} MB)" -f $tar, ((Get-Item $tar).Length / 1MB)) -ForegroundColor Green
        } else {
            Write-Warning "Docker not found: skipped tar.gz (run 'chmod +x AllInOnePdf.sh app/AllInOnePdf' on Linux)."
        }
        # The same folder as a zip for people who cannot open a tar.gz. Zip entries carry no Unix permissions, so
        # README.txt tells them to run 'chmod +x AllInOnePdf.sh app/AllInOnePdf' (the launcher repairs the app's bit).
        $linuxZip = Join-Path $Output "AllInOnePdf-linux-x64.zip"
        if (Test-Path $linuxZip) { Remove-Item $linuxZip -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [AppContext]::SetSwitch("Switch.System.IO.Compression.ZipFile.UseBackslash", $false)
        $linuxZipFull = Join-Path (Resolve-Path $Output).Path (Split-Path $linuxZip -Leaf)
        [IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $dest).Path, $linuxZipFull, [IO.Compression.CompressionLevel]::Optimal, $true)
        Write-Host ("Linux zip: {0} ({1:n1} MB)" -f $linuxZip, ((Get-Item $linuxZip).Length / 1MB)) -ForegroundColor Green
    }
}

Write-Host "Done." -ForegroundColor Green
# robocopy reports "files copied" as exit code 1, and PowerShell 7.4+ hands a script's last native exit code to the
# caller; everything above threw on real failures, so leave with success explicitly (CI treats 1 as a failed job).
$global:LASTEXITCODE = 0
exit 0
