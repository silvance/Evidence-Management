<#
.SYNOPSIS
  Write the worker's appsettings.json from parameters - paths, the SQL Server instance (Windows
  authentication only) and the approved artifact hashes read from the reviewed artifact
  manifest. Accepts no password and writes none.

.PARAMETER ArtifactManifest
  The reviewed emc-artifact-manifest/1 file the bundle was exported with. The SHA-256 of
  tesseract.exe, eng.traineddata and osd.traineddata are copied from it into
  Ocr:ApprovedArtifactHashes, so what the worker verifies at start is what the reviewer approved.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Emc\OcrWorker',
    [Parameter(Mandatory = $true)][string]$SqlServerInstance,
    [string]$Database = 'Emc',
    [Parameter(Mandatory = $true)][string]$SourceDocumentsRoot,
    [Parameter(Mandatory = $true)][string]$RenderWorkRoot,
    [Parameter(Mandatory = $true)][string]$OcrWorkRoot,
    [string]$TesseractRoot = 'C:\Program Files\Tesseract-OCR',
    [Parameter(Mandatory = $true)][string]$ArtifactManifest
)

$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $InstallRoot 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json

$manifest = Get-Content $ArtifactManifest -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'emc-artifact-manifest/1') { throw "Not an emc-artifact-manifest/1 file: $ArtifactManifest" }
$hashes = [ordered]@{}
$approvedArtifacts = @($manifest.artifacts | Where-Object { $_.reviewStatus -eq 'approved' })

# The engine: the bundle carries the INSTALLER; the worker verifies the INSTALLED binary, whose
# hash the reviewer recorded under installedFiles after installing the engine in staging.
$engineEntry = $approvedArtifacts | Where-Object { $_.kind -eq 'ocr-engine' } | Select-Object -First 1
if (-not $engineEntry) { throw 'The manifest has no APPROVED ocr-engine entry.' }
$installed = @($engineEntry.installedFiles | Where-Object { $_.file -ieq 'tesseract.exe' }) | Select-Object -First 1
if (-not $installed -or $installed.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "The ocr-engine entry records no installedFiles hash for tesseract.exe. Install the engine in staging, hash the installed tesseract.exe, and record it in the manifest." }
$hashes['tesseract.exe'] = $installed.sha256.ToLowerInvariant()

# The models are copied as-is, so their artifact hash is their installed hash.
foreach ($name in @('eng.traineddata', 'osd.traineddata')) {
    $entry = $approvedArtifacts | Where-Object { $_.kind -eq 'ocr-model' -and (Split-Path $_.path -Leaf) -ieq $name } | Select-Object -First 1
    if (-not $entry) { throw "The manifest has no APPROVED ocr-model entry for $name." }
    if ($entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "The manifest entry for $name has no SHA-256." }
    $hashes[$name] = $entry.sha256.ToLowerInvariant()
}

$settings.ConnectionStrings.Emc = "Server=$SqlServerInstance;Database=$Database;Integrated Security=true;Encrypt=true;TrustServerCertificate=false"
$settings.SourceDocuments.RootPath = $SourceDocumentsRoot
$settings.SourceDocuments.RenderHelperPath = (Join-Path $InstallRoot 'Emc.OcrWorker.exe')
$settings.SourceDocuments.RenderWorkRoot = $RenderWorkRoot
$settings.Ocr.EnginePath = (Join-Path $TesseractRoot 'tesseract.exe')
$settings.Ocr.TessdataPath = (Join-Path $TesseractRoot 'tessdata')
$settings.Ocr.WorkRoot = $OcrWorkRoot
$settings.Ocr.RequireApprovedArtifactHashes = $true
$settings.Ocr.ApprovedArtifactHashes = [pscustomobject]$hashes

$settings | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsPath -Encoding UTF8
Write-Host "Wrote $settingsPath (Windows authentication; approved hashes for $($hashes.Keys -join ', '))."
