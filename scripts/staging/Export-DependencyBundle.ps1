<#
.SYNOPSIS
  CONNECTED STAGING: resolve the locked dependency graph, audit it, and export a verifiable
  dependency bundle for the air-gapped environment.

.DESCRIPTION
  Runs only where Internet access is permitted, on a machine that holds NO EMC evidence data.

  1. Restores every project with lock-file generation (NuGet.Config: nuget.org only).
  2. Runs the NuGet vulnerability audit (direct + transitive, every severity) and FAILS on any
     finding. The report is saved into the bundle.
  3. Reads every packages.lock.json and copies EXACTLY the packages they name - direct and
     transitive - out of the NuGet global packages folder. It does not copy the whole cache.
  4. Optionally copies the .NET SDK installer/archive and the ASP.NET Core Hosting Bundle.
  5. Writes manifest.json (name, version, file, SHA-256, origin, retrieval date, licence,
     classification runtime/build/test, audit status/date) and MANIFEST.sha256.

  The bundle is then transferred by the organization's approved media process and verified
  on the air-gapped side with scripts/airgap/Verify-DependencyBundle.ps1.

.PARAMETER BundleRoot
  Output folder. Default: <repo>/dependency-bundle.
.PARAMETER SdkInstaller
  Path to the exact .NET SDK installer/archive pinned in global.json, to include.
.PARAMETER HostingBundleInstaller
  Path to the ASP.NET Core Hosting Bundle installer for the pinned runtime, to include.
#>
[CmdletBinding()]
param(
    [string]$BundleRoot,
    [string]$SdkInstaller,
    [string]$HostingBundleInstaller
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $BundleRoot) { $BundleRoot = Join-Path $repo 'dependency-bundle' }

$packagesOut = Join-Path $BundleRoot 'packages'
$prereqOut   = Join-Path $BundleRoot 'prerequisites'
$lockOut     = Join-Path $BundleRoot 'lockfiles'
foreach ($d in @($packagesOut, $prereqOut, $lockOut)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

$sdkVersion = (Get-Content (Join-Path $repo 'global.json') | ConvertFrom-Json).sdk.version
Write-Host "Pinned SDK: $sdkVersion (rollForward disabled)"

# 1. Restore with lock files, from nuget.org only.
Push-Location $repo
try {
    dotnet restore Emc.sln --configfile NuGet.Config --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

    # 2. Audit. Every severity, transitives included. Any finding fails the export.
    $auditReport = Join-Path $BundleRoot 'audit-report.txt'
    $audit = dotnet list Emc.sln package --vulnerable --include-transitive 2>&1
    $audit | Out-File -FilePath $auditReport -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'audit command failed' }
    if ($audit -match 'has the following vulnerable packages') {
        throw "Vulnerable packages found. See $auditReport. Resolve or record an assessment in docs/dependency-advisories.md before exporting."
    }
    $auditDate = (Get-Date).ToUniversalTime().ToString('o')
} finally { Pop-Location }

# 3. Exactly the locked graph.
$globalPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
$lockFiles = Get-ChildItem -Path $repo -Recurse -Filter 'packages.lock.json' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|dependency-bundle)[\\/]' }

$entries = @{}
foreach ($lock in $lockFiles) {
    Copy-Item $lock.FullName (Join-Path $lockOut ($lock.Directory.Name + '.packages.lock.json'))
    $classification = if ($lock.FullName -match '[\\/]tests[\\/]') { 'test-only' }
                      elseif ($lock.Directory.Name -eq 'Emc.Infrastructure' -or $lock.Directory.Name -eq 'Emc.Web') { 'runtime' }
                      else { 'runtime' }
    $json = Get-Content $lock.FullName -Raw | ConvertFrom-Json
    foreach ($tfm in $json.dependencies.PSObject.Properties) {
        foreach ($dep in $tfm.Value.PSObject.Properties) {
            if ($dep.Value.type -eq 'Project') { continue }
            $id = $dep.Name; $version = $dep.Value.resolved
            $key = "$id/$version".ToLowerInvariant()
            if (-not $entries.ContainsKey($key)) {
                $entries[$key] = [ordered]@{ id = $id; version = $version; classification = $classification;
                                             buildOnly = ($dep.Value.type -eq 'Direct' -and $id -match 'Design|Test|xunit|coverlet') }
            } elseif ($classification -eq 'runtime') { $entries[$key].classification = 'runtime' }
        }
    }
}

$manifest = @()
foreach ($e in $entries.Values | Sort-Object id, version) {
    $src = Join-Path $globalPackages ($e.id.ToLowerInvariant()) $e.version ("$($e.id).$($e.version).nupkg".ToLowerInvariant())
    if (-not (Test-Path $src)) { throw "Package not in global cache after restore: $src" }
    $dst = Join-Path $packagesOut (Split-Path $src -Leaf)
    Copy-Item $src $dst -Force

    # Licence from the nuspec, where declared.
    $licence = $null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($dst)
        $nuspec = $zip.Entries | Where-Object { $_.Name -like '*.nuspec' } | Select-Object -First 1
        if ($nuspec) {
            $reader = New-Object IO.StreamReader($nuspec.Open())
            [xml]$x = $reader.ReadToEnd(); $reader.Dispose()
            $licence = $x.package.metadata.license.'#text'
            if (-not $licence) { $licence = $x.package.metadata.licenseUrl }
        }
        $zip.Dispose()
    } catch { $licence = $null }

    $manifest += [ordered]@{
        name            = $e.id
        version         = $e.version
        file            = "packages/$(Split-Path $dst -Leaf)"
        sha256          = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
        origin          = 'https://api.nuget.org/v3/index.json'
        retrievedUtc    = (Get-Date).ToUniversalTime().ToString('o')
        license         = $licence
        classification  = if ($e.buildOnly) { 'build-only' } else { $e.classification }
        auditStatus     = 'no findings'
        auditDateUtc    = $auditDate
        reviewStatus    = 'pending import review'
    }
}

# 4. Prerequisites, as supplied.
foreach ($pair in @(@('sdk', $SdkInstaller), @('hosting-bundle', $HostingBundleInstaller))) {
    $kind, $path = $pair
    if ($path) {
        if (-not (Test-Path $path)) { throw "$kind installer not found: $path" }
        $dst = Join-Path $prereqOut (Split-Path $path -Leaf)
        Copy-Item $path $dst -Force
        $manifest += [ordered]@{
            name = "dotnet-$kind"; version = $sdkVersion; file = "prerequisites/$(Split-Path $dst -Leaf)"
            sha256 = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
            origin = 'https://dotnet.microsoft.com (record the exact download page in reviewStatus)'
            retrievedUtc = (Get-Date).ToUniversalTime().ToString('o'); license = 'MIT'
            classification = if ($kind -eq 'sdk') { 'build-only' } else { 'runtime' }
            auditStatus = 'n/a'; auditDateUtc = $null; reviewStatus = 'pending import review'
        }
    } else {
        Write-Warning "No $kind installer supplied; the bundle will not carry it. The air-gapped host must already have it."
    }
}

# 5. Manifests.
$manifestJson = [ordered]@{
    schema      = 'emc-dependency-bundle/1'
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    sdk         = @{ version = $sdkVersion; rollForward = 'disable' }
    auditReport = 'audit-report.txt'
    artifacts   = $manifest
}
$manifestJson | ConvertTo-Json -Depth 6 | Out-File (Join-Path $BundleRoot 'manifest.json') -Encoding utf8

$lines = Get-ChildItem -Path $BundleRoot -Recurse -File |
    Where-Object { $_.Name -ne 'MANIFEST.sha256' } |
    ForEach-Object {
        $rel = $_.FullName.Substring($BundleRoot.Length + 1).Replace('\', '/')
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $rel"
    }
$lines | Out-File (Join-Path $BundleRoot 'MANIFEST.sha256') -Encoding ascii

Write-Host "Exported $($manifest.Count) artifacts to $BundleRoot"
Write-Host "Transfer the folder by the approved media process; verify on import with scripts/airgap/Verify-DependencyBundle.ps1."
