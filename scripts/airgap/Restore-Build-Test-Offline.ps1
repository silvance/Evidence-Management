<#
.SYNOPSIS
  AIR-GAPPED: verify the bundle, then restore in LOCKED mode from it alone, build, and test.
  Zero network access. Fails if anything would have to be fetched.

.DESCRIPTION
  - NuGet.Offline.Config clears every inherited source and names only the bundle folder.
  - --locked-mode makes restore fail rather than resolve anything packages.lock.json does not name.
  - EMC_OFFLINE=true turns NuGetAudit off for this build ONLY, because the bundle folder carries
    no vulnerability data. The audit was performed in connected staging when the bundle was
    exported (audit-report.txt); this script prints that report's date so nobody mistakes an
    offline build for a current audit.
  - The SQL Server release-validation lane runs if EMC_SQLSERVER_TEST_CONNECTION is set to an
    approved local instance; otherwise it is skipped and says so.
#>
[CmdletBinding()]
param(
    [string]$BundleRoot,
    [switch]$SkipTests,

    # The locally installed OCR engine and its models (installed from the bundle's artifacts/
    # folder). When both are given the real-engine tests run - engine start-up, model hashes,
    # synthetic pages, zero network - which is the offline OCR validation (Phase 12). Without
    # them those tests are SKIPPED and this script says so; that is not a pass.
    [string]$TesseractPath,
    [string]$TessdataPath
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $BundleRoot) { $BundleRoot = Join-Path $repo 'dependency-bundle' }

& (Join-Path $PSScriptRoot 'Verify-DependencyBundle.ps1') -BundleRoot $BundleRoot

Push-Location $repo
try {
    $installed = (dotnet --version)
    $pinned = (Get-Content global.json | ConvertFrom-Json).sdk.version
    if ($installed -ne $pinned) { throw "Installed SDK $installed is not the pinned $pinned (rollForward is disabled). Install the SDK from the bundle's prerequisites folder." }

    dotnet restore Emc.sln --configfile NuGet.Offline.Config --locked-mode -p:EMC_OFFLINE=true
    if ($LASTEXITCODE -ne 0) { throw 'Offline restore failed. Nothing outside the bundle is consulted; a missing package means the bundle does not match the lock files.' }

    dotnet build Emc.sln --no-restore -c Release -p:EMC_OFFLINE=true
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    if (-not $SkipTests) {
        if ($TesseractPath -and $TessdataPath) {
            if (-not (Test-Path $TesseractPath)) { throw "Tesseract not found at $TesseractPath. Install it from the bundle's artifacts/ocr-engine folder." }
            foreach ($model in @('eng.traineddata', 'osd.traineddata')) {
                if (-not (Test-Path (Join-Path $TessdataPath $model))) { throw "OCR model $model not found in $TessdataPath. Copy it from the bundle's artifacts/ocr-model folder." }
            }
            $env:EMC_TESSERACT_PATH = $TesseractPath
            $env:EMC_TESSDATA_PATH = $TessdataPath
            Write-Host "Offline OCR validation: engine $TesseractPath, models $TessdataPath. The real-engine tests will run."
        } else {
            Write-Warning 'No -TesseractPath/-TessdataPath: the real-engine OCR tests are SKIPPED. Offline OCR is NOT validated by this run.'
        }

        dotnet test Emc.sln --no-build -c Release -p:EMC_OFFLINE=true
        if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
        if (-not $env:EMC_SQLSERVER_TEST_CONNECTION) {
            Write-Warning 'EMC_SQLSERVER_TEST_CONNECTION is not set: the SQL Server release-validation lane was SKIPPED. Set it to an approved local SQL Server before a release build.'
        }
    }

    $manifest = Get-Content (Join-Path $BundleRoot 'manifest.json') -Raw | ConvertFrom-Json
    Write-Host "Offline build complete. Vulnerability audit is from connected staging, dated $($manifest.artifacts[0].auditDateUtc); this host performed no audit."
} finally { Pop-Location }
