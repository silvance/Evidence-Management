<#
.SYNOPSIS
  RELEASE BUNDLE: verify an EMC release bundle (a folder, or a .zip which is expanded to a
  temporary folder) before anything in it is deployed. No network access is used or needed.

.DESCRIPTION
  - every file matches MANIFEST.sha256 and release-manifest.json (both, so they cannot disagree);
  - the entry points, the IIS web.config, the schema script and the deploy scripts are present;
  - the tests ran and none failed; a staging dry run or a dirty working tree is called out;
  - no published configuration file carries a credential-like value.
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
$temp = $null
if ((Test-Path $Path -PathType Leaf) -and $Path -like '*.zip') {
    $sidecar = "$Path.sha256"
    if (Test-Path $sidecar) {
        $expected = ((Get-Content $sidecar -Raw) -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { throw "Archive hash does not match $sidecar" }
        Write-Host "archive hash matches $(Split-Path $sidecar -Leaf)"
    }
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("emc-release-verify-" + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $Path -DestinationPath $temp
    $Path = (Get-ChildItem $temp -Directory | Select-Object -First 1).FullName
}
$root = (Resolve-Path $Path).Path
if (-not (Test-Path (Join-Path $root 'MANIFEST.sha256')) -or -not (Test-Path (Join-Path $root 'release-manifest.json'))) { throw "not a release bundle: MANIFEST.sha256 or release-manifest.json missing in $root" }

$failures = 0; $checked = 0
foreach ($line in Get-Content (Join-Path $root 'MANIFEST.sha256')) {
    if (-not $line.Trim()) { continue }
    $expected, $rel = $line -split '\s{2}', 2
    $file = Join-Path $root $rel
    if (-not (Test-Path $file)) { Write-Error "MISSING  $rel"; $failures++; continue }
    if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected.ToLowerInvariant()) { Write-Error "MISMATCH $rel"; $failures++ } else { $checked++ }
}
$manifest = Get-Content (Join-Path $root 'release-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'emc-release-bundle/1') { throw "unsupported schema $($manifest.schema)" }
foreach ($f in $manifest.files) {
    $file = Join-Path $root $f.path
    if (-not (Test-Path $file)) { Write-Error "release-manifest.json names a missing file: $($f.path)"; $failures++; continue }
    if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $f.sha256) { Write-Error "release-manifest.json hash mismatch: $($f.path)"; $failures++ }
}
$onDisk = @(Get-ChildItem $root -Recurse -File | Where-Object { $_.Name -notin @('MANIFEST.sha256', 'release-manifest.json') }).Count
if ($onDisk -ne $manifest.files.Count) { Write-Error "release-manifest.json lists $($manifest.files.Count) files; the bundle holds $onDisk"; $failures++ }

foreach ($c in $manifest.components) { if (-not (Test-Path (Join-Path $root $c.entryPoint))) { Write-Error "entry point missing: $($c.entryPoint)"; $failures++ } }
foreach ($rel in @('web/appsettings.json', 'worker/appsettings.json', 'db/schema-v1.sql', 'scripts/deploy/Install-EmcOcrWorker.ps1', 'scripts/deploy/Set-EmcOcrWorkerConfig.ps1', 'docs/release-bundle.md')) {
    if (-not (Test-Path (Join-Path $root $rel))) { Write-Error "required file missing: $rel"; $failures++ }
}
$isDeploymentRuntime = $manifest.runtime.identifier -eq 'win-x64'
if ($isDeploymentRuntime) {
    if (-not (Test-Path (Join-Path $root 'web/web.config'))) { Write-Error 'required file missing: web/web.config'; $failures++ }
    elseif ((Get-Content (Join-Path $root 'web/web.config') -Raw) -notmatch 'aspNetCore processPath=') { Write-Error 'web/web.config carries no aspNetCore handler: not a published IIS site'; $failures++ }
} else {
    Write-Warning "runtime $($manifest.runtime.identifier) is the test lane, not a deployment target."
}
foreach ($rel in (@('web/appsettings.json', 'worker/appsettings.json') + $(if (Test-Path (Join-Path $root 'web/web.config')) { @('web/web.config') } else { @() }))) {
    if ((Get-Content (Join-Path $root $rel) -Raw) -match '(?i)password=|pwd=|user id=|accesskey|secret') { Write-Error "$rel carries a credential-like value"; $failures++ }
}
$forbidden = Get-ChildItem $root -Recurse -File | Where-Object { $_.Name -eq 'appsettings.Development.json' -or $_.Name -like '*.Local.json' -or $_.Name -eq 'secrets.json' -or $_.Extension -in @('.pfx', '.key') }
if ($forbidden) { Write-Error "development-only or secret-bearing files are in the bundle: $($forbidden.Name -join ', ')"; $failures++ }

if ($manifest.tests.failed -gt 0) { Write-Error "the manifest records $($manifest.tests.failed) failed test(s): not a release"; $failures++ }
if (-not $manifest.tests.ran) { Write-Warning 'tests were not run for this bundle (tests.ran=false). It is not a release.' }
if ($manifest.dirtyWorkingTree) { Write-Warning 'built from a dirty working tree; the commit does not describe it. Not a release.' }
if ($manifest.restore.source -match 'STAGING DRY RUN') { Write-Warning 'staging dry run restored from nuget.org, not from the verified dependency bundle. Not a release.' }

if ($temp) { Remove-Item $temp -Recurse -Force }
if ($failures -gt 0) { throw "Release bundle verification FAILED ($failures problem(s)). Do not deploy from it." }
Write-Host "Release bundle OK: $($manifest.product) $($manifest.informationalVersion), runtime $($manifest.runtime.identifier), built $($manifest.builtUtc) from commit $($manifest.commit); $checked files match."
Write-Host "Tests: ran=$($manifest.tests.ran) passed=$($manifest.tests.passed) failed=$($manifest.tests.failed) skipped=$($manifest.tests.skipped); SQL Server lane: $($manifest.tests.sqlServerLane)"
