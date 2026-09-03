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

$pinned = (Get-Content (Join-Path $repo 'global.json') | ConvertFrom-Json).sdk.version
if ($manifest.sdk.version -ne $pinned) { Write-Error "Bundle was built for SDK $($manifest.sdk.version); repository pins $pinned"; $failures++ }

if ($failures -gt 0) { throw "Bundle verification FAILED ($failures problem(s)). Do not restore from this bundle." }
Write-Host "Bundle verified: $checked files match. Audit report: $(Join-Path $BundleRoot $manifest.auditReport) (dated $($manifest.artifacts[0].auditDateUtc))."
