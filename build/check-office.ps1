<#
.SYNOPSIS
  Opens every .docx, .xlsx and .pptx in a folder with LibreOffice (headless, in Docker) and converts it to PDF.

.DESCRIPTION
  A file that LibreOffice cannot open is reported as FAIL with the last lines of its log; the PDFs land in
  <Folder>\lo so they can be looked at. This is the independent check that exported Office files are well formed;
  opening them in Microsoft Office is still the final word. Needs Docker Desktop. The image is built on first use
  from docker/Dockerfile.libreoffice.

.EXAMPLE
  .\build\check-office.ps1 -Folder C:\exports
#>
[CmdletBinding()]
param(
    # Folder holding the .docx, .xlsx and .pptx files to check ($Input is reserved by PowerShell, hence the name).
    [Parameter(Mandatory = $true)]
    [string]$Folder,
    [string]$Image = "allinonepdf-libreoffice"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker is not available." }
$inputFull = (Resolve-Path $Folder).Path
# -Include needs a wildcard path when -Recurse is off.
$files = Get-ChildItem (Join-Path $inputFull "*") -File -Include *.docx, *.xlsx, *.pptx
if ($files.Count -eq 0) { throw "No .docx, .xlsx or .pptx files in $inputFull" }

if (-not (docker image inspect $Image 2>$null)) {
    Write-Host "Building $Image (LibreOffice in Alpine, about 400 MB)..." -ForegroundColor Cyan
    docker build -q -f (Join-Path $repo "docker\Dockerfile.libreoffice") -t $Image $repo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
}

# Stage under the system temp folder: Docker Desktop shares the system drive by default, but not every drive.
$stage = Join-Path ([IO.Path]::GetTempPath()) ("aio-office-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $stage | Out-Null
$files | Copy-Item -Destination $stage
$script = @'
cd /data || exit 1
mkdir -p lo
for f in *.docx *.xlsx *.pptx; do
  [ -e "$f" ] || continue
  base="${f%.*}"
  if timeout 180 soffice --headless --norestore --convert-to pdf --outdir lo "$f" >"lo/$f.log" 2>&1 && [ -s "lo/$base.pdf" ]; then
    echo "OK   $f -> $(stat -c %s "lo/$base.pdf") bytes"
  else
    echo "FAIL $f: $(tail -n 3 "lo/$f.log" | tr '\n' ' ')"
  fi
done
'@
[IO.File]::WriteAllText((Join-Path $stage "convert.sh"), ($script -replace "`r`n", "`n"))

$previous = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$lines = & docker run --rm -v "${stage}:/data" $Image sh /data/convert.sh 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match '^(OK|FAIL)' }
$ErrorActionPreference = $previous

$lines | ForEach-Object { if ($_ -like "FAIL*") { Write-Host $_ -ForegroundColor Red } else { Write-Host $_ -ForegroundColor Green } }
$out = Join-Path $inputFull "lo"
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $stage "lo\*") $out -Force
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$failed = @($lines | Where-Object { $_ -like "FAIL*" }).Count
Write-Host ("{0} file(s) checked, {1} failed; PDFs in {2}" -f $lines.Count, $failed, $out) -ForegroundColor ($(if ($failed) { "Red" } else { "Green" }))
exit $(if ($failed) { 1 } else { 0 })
