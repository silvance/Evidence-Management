<#
.SYNOPSIS
  RELEASE BUNDLE (docs/release-bundle.md): build, test, publish and package EMC as ONE archive
  with the executables, from the verified dependency bundle, inside the air-gapped environment.

.DESCRIPTION
  Produces <OutputRoot>/emc-<version>-<rid>-<commit>/ and beside it the .zip and its .sha256:
    web/      Emc.Web published for the runtime (Emc.Web.exe and the IIS web.config)
    worker/   Emc.OcrWorker published for the runtime (the Windows Service executable, also
              the render child process)
    db/       schema-v1.sql and its README (applied by the DBA; the application never migrates)
    scripts/  deploy/ (worker service scripts) and verify/ (release-bundle verifiers)
    docs/     the deployment documents and the slice reports
    release-manifest.json (what it is, what it was built from, what was proved, every file's
    SHA-256) and MANIFEST.sha256 (a plain list of every file).

  Default is the AIR-GAPPED path: the dependency bundle is verified, restore is LOCKED and
  reads only the bundle (NuGet.Offline.Config, EMC_OFFLINE=true), then build, test, publish,
  package. -Connected is a staging dry run over nuget.org (still locked mode); its archive is
  named "-staging-dryrun" and its manifest says so, because it is not a release.

  The build is never self-contained: the server's ASP.NET Core Hosting Bundle (from the
  dependency bundle's prerequisites) supplies the runtime. Nothing is downloaded.

.PARAMETER Rid
  win-x64 (the deployment target; default) or linux-x64 (the test lane only).
.PARAMETER OutputRoot
  Where the bundle folder and archive are written. Default: <repo>/release (git-ignored).
.PARAMETER BundleRoot
  The verified dependency bundle. Default: <repo>/dependency-bundle.
.PARAMETER Connected
  Staging dry run: restore from nuget.org (NuGet.Config) instead of the dependency bundle.
.PARAMETER SkipTests
  Do not run the test suites. The manifest records tests.ran=false; the result is not a release.
.PARAMETER AllowDirty
  Build from a working tree with uncommitted changes, for a local trial only. The manifest
  records dirtyWorkingTree=true; the result is not a release.
.PARAMETER TesseractPath / TessdataPath
  The locally installed OCR engine and models, so the real-engine tests run (offline OCR
  validation). Without them those tests are skipped and this script says so.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')][string]$Rid = 'win-x64',
    [string]$OutputRoot,
    [string]$BundleRoot,
    [switch]$Connected,
    [switch]$SkipTests,
    [switch]$AllowDirty,
    [string]$TesseractPath,
    [string]$TessdataPath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release' }
if (-not $BundleRoot) { $BundleRoot = Join-Path $repo 'dependency-bundle' }

$props = Get-Content (Join-Path $repo 'Directory.Build.props') -Raw
if ($props -notmatch "<RuntimeIdentifiers>[^<]*$([regex]::Escape($Rid))") { throw "$Rid is not pinned in Directory.Build.props RuntimeIdentifiers" }
$version = [regex]::Match($props, '<VersionPrefix>([^<]+)</VersionPrefix>').Groups[1].Value
if (-not $version) { throw 'No <VersionPrefix> in Directory.Build.props' }

Push-Location $repo
try {
    $pinned = (Get-Content global.json | ConvertFrom-Json).sdk.version
    $installed = (dotnet --version)
    if ($installed -ne $pinned) { throw "Installed SDK $installed is not the pinned $pinned (rollForward is disabled). Install the SDK from the dependency bundle's prerequisites folder." }

    $shaFull = (git rev-parse HEAD).Trim(); $sha = (git rev-parse --short HEAD).Trim(); $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    $dirty = [bool](git status --porcelain)
    if ($dirty -and -not $AllowDirty) { throw 'The working tree has uncommitted changes. A release is built from a commit. Commit, or pass -AllowDirty for a local trial (the manifest will say dirtyWorkingTree=true).' }

    $name = "emc-$version-$Rid-$sha"; if ($Connected) { $name += '-staging-dryrun' }
    $stage = Join-Path $OutputRoot $name
    $zip = Join-Path $OutputRoot "$name.zip"
    foreach ($p in @($stage, $zip, "$zip.sha256")) { if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Write-Host "Release bundle $name ($(if ($Connected) { 'connected STAGING DRY RUN' } else { 'offline' }), SDK $pinned, commit $shaFull on $branch)"

    # 1. Restore - locked, and offline unless this is a staging dry run.
    $bundleManifestSha = $null; $bundleAudit = $null
    if ($Connected) {
        $msbuildProps = @()
        Write-Host 'Restore (connected STAGING DRY RUN, locked, nuget.org)'
        dotnet restore Emc.sln --configfile NuGet.Config --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'restore failed' }
    } else {
        & (Join-Path $repo 'scripts\airgap\Verify-DependencyBundle.ps1') -BundleRoot $BundleRoot
        $bundleManifestSha = (Get-FileHash (Join-Path $BundleRoot 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        $bundleManifest = Get-Content (Join-Path $BundleRoot 'manifest.json') -Raw | ConvertFrom-Json
        $bundleAudit = ($bundleManifest.artifacts | Where-Object { $_.auditDateUtc } | Select-Object -First 1).auditDateUtc
        $msbuildProps = @('-p:EMC_OFFLINE=true')
        Write-Host "Restore (offline, locked, source: $BundleRoot\packages)"
        # NuGet.Offline.Config names the repository's own dependency-bundle\packages; -source makes
        # the SAME folder the only source when -BundleRoot points elsewhere. Nothing else is consulted.
        dotnet restore Emc.sln --configfile NuGet.Offline.Config --source (Join-Path $BundleRoot 'packages') --locked-mode -p:EMC_OFFLINE=true
        if ($LASTEXITCODE -ne 0) { throw "Offline restore failed: something the lock files name is not in the bundle (the apphost pack for $Rid included). Nothing outside the bundle was consulted." }
    }

    # 2. Build and test, Release configuration, nothing restored from here on.
    Write-Host 'Build (Release)'
    dotnet build Emc.sln --no-restore -c Release @msbuildProps
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    $tests = [ordered]@{ ran = $false; passed = 0; failed = 0; skipped = 0; sqlServerLane = $(if ($env:EMC_SQLSERVER_TEST_CONNECTION) { 'ran' } else { 'skipped (EMC_SQLSERVER_TEST_CONNECTION not set)' }) }
    if (-not $SkipTests) {
        if ($TesseractPath -and $TessdataPath) {
            $env:EMC_TESSERACT_PATH = $TesseractPath; $env:EMC_TESSDATA_PATH = $TessdataPath
            Write-Host "Offline OCR validation: engine $TesseractPath, models $TessdataPath. The real-engine tests will run."
        } else {
            Write-Warning 'No -TesseractPath/-TessdataPath: the real-engine OCR tests are SKIPPED. Offline OCR is NOT validated by this run.'
        }
        $trx = Join-Path $OutputRoot "$name.tests"
        if (Test-Path $trx) { Remove-Item $trx -Recurse -Force }
        Write-Host 'Test (Release, no build)'
        dotnet test Emc.sln --no-build -c Release @msbuildProps --logger trx --results-directory $trx
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed. No bundle is produced from a failing tree.' }
        $tests.ran = $true
        foreach ($file in Get-ChildItem $trx -Filter *.trx) {
            [xml]$x = Get-Content $file.FullName -Raw
            $c = $x.TestRun.ResultSummary.Counters
            $tests.passed += [int]$c.passed; $tests.failed += [int]$c.failed; $tests.skipped += ([int]$c.total - [int]$c.executed)
        }
        Write-Host "Tests: $($tests.passed) passed, $($tests.failed) failed, $($tests.skipped) skipped (SQL Server lane: $($tests.sqlServerLane))"
        if ($tests.failed -gt 0) { throw 'tests failed' }
    } else {
        Write-Warning 'Tests SKIPPED on request: the manifest will say so; this is not a release.'
    }

    # 3. Publish the two executables for the runtime. Framework-dependent; the commit is
    #    stamped into the informational version.
    foreach ($pair in @(@('Emc.Web', 'web'), @('Emc.OcrWorker', 'worker'))) {
        $project, $dir = $pair
        Write-Host "Publish $project ($Rid) -> $dir/"
        dotnet publish (Join-Path 'src' $project) --no-restore -c Release -r $Rid --self-contained false @msbuildProps "-p:SourceRevisionId=$sha" -o (Join-Path $stage $dir)
        if ($LASTEXITCODE -ne 0) { throw "publish of $project failed" }
    }
    # Nothing that is development-only or could hold a secret leaves with the bundle.
    Get-ChildItem $stage -Recurse -File | Where-Object {
        $_.Name -eq 'appsettings.Development.json' -or $_.Name -like 'appsettings.*.Local.json' -or $_.Name -eq 'appsettings.Local.json' -or
        $_.Name -eq 'secrets.json' -or $_.Extension -in @('.pfx', '.key', '.db')
    } | ForEach-Object { Write-Host "removed: $($_.FullName.Substring($stage.Length + 1))"; Remove-Item $_.FullName -Force }
    # The IIS web.config is emitted by the publish for Windows runtimes only; the test lane has none.
    $configFiles = @('web\appsettings.json', 'worker\appsettings.json') + $(if ($Rid -eq 'win-x64') { @('web\web.config') } else { @() })
    foreach ($rel in $configFiles) {
        if ((Get-Content (Join-Path $stage $rel) -Raw) -match '(?i)password=|pwd=|user id=|accesskey|secret') { throw "A published configuration file carries a credential-like value ($rel). Not packaged." }
    }
    $webExe = if ($Rid -eq 'win-x64') { 'web/Emc.Web.exe' } else { 'web/Emc.Web' }
    $workerExe = if ($Rid -eq 'win-x64') { 'worker/Emc.OcrWorker.exe' } else { 'worker/Emc.OcrWorker' }
    $required = @($webExe, $workerExe, 'web/appsettings.json', 'worker/appsettings.json', 'web/Emc.Web.dll', 'worker/Emc.OcrWorker.dll') + $(if ($Rid -eq 'win-x64') { @('web/web.config') } else { @() })
    foreach ($rel in $required) {
        if (-not (Test-Path (Join-Path $stage $rel))) { throw "Expected publish output missing: $rel" }
    }

    # 4. Everything else the deployment needs, from the same commit.
    foreach ($d in @('db', 'scripts\deploy', 'scripts\verify', 'docs\slice-reports')) { New-Item -ItemType Directory -Force -Path (Join-Path $stage $d) | Out-Null }
    Copy-Item 'db\schema-v1.sql', 'db\README.md' (Join-Path $stage 'db')
    Copy-Item 'scripts\deploy\*.ps1' (Join-Path $stage 'scripts\deploy')
    Copy-Item 'scripts\release\Verify-ReleaseBundle.ps1', 'scripts\release\verify-release-bundle.sh' (Join-Path $stage 'scripts\verify')
    Copy-Item 'docs\release-bundle.md', 'docs\ocr-worker-deployment.md', 'docs\air-gapped-build-and-maintenance.md', 'docs\dependency-advisories.md' (Join-Path $stage 'docs')
    Copy-Item 'docs\slice-reports\*.md' (Join-Path $stage 'docs\slice-reports')

    # 5. Manifests: what this is, what it was built from, what was proved, and every file's hash.
    $files = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -notin @('release-manifest.json', 'MANIFEST.sha256') } |
        Sort-Object { $_.FullName.Substring($stage.Length + 1).Replace('\', '/') } -Culture 'en-US' |
        ForEach-Object { [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length } }
    $manifest = [ordered]@{
        schema               = 'emc-release-bundle/1'
        product              = 'EMC (Evidence Management Companion)'
        version              = $version
        informationalVersion = "$version+$sha"
        commit               = $shaFull
        branch               = $branch
        dirtyWorkingTree     = $dirty
        builtUtc             = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        buildHost            = "$([Environment]::OSVersion.Platform) $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
        sdk                  = [ordered]@{ version = $pinned; rollForward = 'disable' }
        runtime              = [ordered]@{ identifier = $Rid; purpose = $(if ($Rid -eq 'win-x64') { 'deployment target' } else { 'test lane only; not a deployment target' }); selfContained = $false; requires = 'ASP.NET Core Hosting Bundle for the pinned runtime, from the dependency bundle prerequisites' }
        configuration        = 'Release'
        restore              = $(if ($Connected) { [ordered]@{ mode = 'connected-locked'; source = 'nuget.org (STAGING DRY RUN - NOT A RELEASE)'; dependencyBundleManifestSha256 = $null; dependencyAuditDateUtc = $null } }
                                 else { [ordered]@{ mode = 'offline-locked'; source = 'dependency bundle'; dependencyBundleManifestSha256 = $bundleManifestSha; dependencyAuditDateUtc = $bundleAudit } })
        tests                = $tests
        components           = @(
            [ordered]@{ name = 'Emc.Web'; path = 'web'; entryPoint = $webExe; hosting = "$(if ($Rid -eq 'win-x64') { 'IIS in-process (web/web.config)' } else { 'Kestrel (test lane; no IIS web.config)' }); Windows Authentication; never migrates the database (AUD-012)" },
            [ordered]@{ name = 'Emc.OcrWorker'; path = 'worker'; entryPoint = $workerExe; hosting = 'Windows Service EmcOcrWorker via scripts/deploy/Install-EmcOcrWorker.ps1; also the render child process' })
        database             = [ordered]@{ schemaScript = 'db/schema-v1.sql'; appliedBy = 'the DBA, from the script, before the first start; the application holds no DDL rights' }
        files                = $files
    }
    $manifest | ConvertTo-Json -Depth 6 | Out-File (Join-Path $stage 'release-manifest.json') -Encoding utf8
    $lines = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -ne 'MANIFEST.sha256' } |
        Sort-Object { $_.FullName.Substring($stage.Length + 1).Replace('\', '/') } -Culture 'en-US' |
        ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.FullName.Substring($stage.Length + 1).Replace('\', '/'))" }
    $lines | Out-File (Join-Path $stage 'MANIFEST.sha256') -Encoding ascii

    # 6. One archive, and its hash beside it.
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $name.zip" | Out-File "$zip.sha256" -Encoding ascii
    & (Join-Path $repo 'scripts\release\Verify-ReleaseBundle.ps1') -Path $stage | Out-Null
    Write-Host "Release bundle: $zip"
    Write-Host "SHA-256:        $zipHash"
    Write-Host 'Verify on the receiving side with scripts/verify/Verify-ReleaseBundle.ps1 (or verify-release-bundle.sh) from inside the archive.'
} finally { Pop-Location }
