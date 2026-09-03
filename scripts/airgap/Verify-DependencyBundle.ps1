<#
.SYNOPSIS
  AIR-GAPPED: verify every file in the dependency bundle against MANIFEST.sha256 and
  manifest.json before anything is restored from it. No network access is used or needed.
#>
[CmdletBinding()]
param([string]$BundleRoot)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $BundleRoot) { $BundleRoot = Join-Path $repo 'dependency-bundle' }

$list = Join-Path $BundleRoot 'MANIFEST.sha256'
if (-not (Test-Path $list)) { throw "MANIFEST.sha256 not found in $BundleRoot" }

$failures = 0; $checked = 0
foreach ($line in Get-Content $list) {
    if (-not $line.Trim()) { continue }
    $expected, $rel = $line -split '\s{2}', 2
    $path = Join-Path $BundleRoot $rel
    if (-not (Test-Path $path)) { Write-Error "MISSING  $rel"; $failures++; continue }
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) { Write-Error "MISMATCH $rel"; $failures++ } else { $checked++ }
}

# Cross-check manifest.json against the plain list so the two cannot disagree.
$manifest = Get-Content (Join-Path $BundleRoot 'manifest.json') -Raw | ConvertFrom-Json
foreach ($a in $manifest.artifacts) {
    $path = Join-Path $BundleRoot $a.file
    if (-not (Test-Path $path)) { Write-Error "manifest.json names a missing file: $($a.file)"; $failures++; continue }
    if ((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $a.sha256) { Write-Error "manifest.json hash mismatch: $($a.file)"; $failures++ }
}

# Non-NuGet artifacts: each must carry a known kind and an approved review; each model must say
# what it is. A bundle without any is valid, and says so, because OCR then cannot be installed.
$kinds = @('ocr-engine', 'ocr-model', 'native-runtime', 'pdf-rasterizer')
$reviewed = @($manifest.artifacts | Where-Object { $_.kind })
foreach ($a in $reviewed) {
    if ($kinds -notcontains $a.kind) { Write-Error "artifact $($a.name): unknown kind '$($a.kind)'"; $failures++ }
    if ($a.reviewStatus -ne 'approved' -or -not $a.reviewedBy) { Write-Error "artifact $($a.name): not approved in staging"; $failures++ }
    if ($a.kind -eq 'ocr-model' -and -not $a.modelId) { Write-Error "artifact $($a.name): ocr-model without a modelId"; $failures++ }
    if (-not $a.license) { Write-Error "artifact $($a.name): no licence recorded"; $failures++ }
}
$engines = @($reviewed | Where-Object { $_.kind -eq 'ocr-engine' }).Count
$models  = @($reviewed | Where-Object { $_.kind -eq 'ocr-model' }).Count
if ($engines -eq 0 -or $models -eq 0) { Write-Warning "This bundle carries $engines OCR engine(s) and $models OCR model(s): OCR cannot be installed from it." }

$pinned = (Get-Content (Join-Path $repo 'global.json') | ConvertFrom-Json).sdk.version
if ($manifest.sdk.version -ne $pinned) { Write-Error "Bundle was built for SDK $($manifest.sdk.version); repository pins $pinned"; $failures++ }

if ($failures -gt 0) { throw "Bundle verification FAILED ($failures problem(s)). Do not restore from this bundle." }
Write-Host "Bundle verified: $checked files match; $($reviewed.Count) reviewed non-NuGet artifact(s). Audit report: $(Join-Path $BundleRoot $manifest.auditReport) (dated $($manifest.artifacts[0].auditDateUtc))."
