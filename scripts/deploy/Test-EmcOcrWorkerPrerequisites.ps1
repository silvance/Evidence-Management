<#
.SYNOPSIS
  Check a worker installation before it is registered as a service. Prints PASS/FAIL per check
  and exits non-zero on any failure. Reads appsettings.json; never prints a connection string
  and never accepts or prints a secret.

.DESCRIPTION
  Checks:
   - the worker executable and appsettings.json exist under -InstallRoot;
   - SourceDocuments:RenderHelperPath names the worker's own executable (pages are rendered in
     a child process the worker starts, never in the service);
   - SourceDocuments:RootPath, SourceDocuments:RenderWorkRoot and Ocr:WorkRoot are absolute
     paths, none of them under -InstallRoot or a web site's physical path given by -WebRoot;
   - Ocr:EnginePath and Ocr:TessdataPath exist, with eng.traineddata and osd.traineddata;
   - Ocr:ApprovedArtifactHashes lists tesseract.exe, eng.traineddata and osd.traineddata and
     each installed file hashes (SHA-256) to its approved value - the same values recorded in
     the reviewed artifact manifest the bundle was exported with;
   - Ocr:RequireApprovedArtifactHashes is not false;
   - ConnectionStrings:Emc uses Integrated Security and carries no password;
   - Ocr:LeaseSeconds >= 2 x Ocr:PageTimeoutSeconds and SourceDocuments:RenderLeaseSeconds >=
     2 x SourceDocuments:RenderTimeoutSeconds (the worker refuses to start otherwise);
   - the worker executable is not signed by an unexpected publisher when -ExpectedSigner is given.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Emc\OcrWorker',
    [string]$TesseractRoot = 'C:\Program Files\Tesseract-OCR',
    [string]$WebRoot,
    [string]$ExpectedSigner
)

$ErrorActionPreference = 'Stop'
$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host ("PASS  {0}" -f $name) } else { Write-Host ("FAIL  {0}{1}" -f $name, ($(if ($detail) { " - $detail" } else { '' }))); $script:failures++ }
}

$exe = Join-Path $InstallRoot 'Emc.OcrWorker.exe'
$settingsPath = Join-Path $InstallRoot 'appsettings.json'
Check 'Worker executable present' (Test-Path $exe) $exe
Check 'appsettings.json present' (Test-Path $settingsPath) $settingsPath
if (-not (Test-Path $settingsPath)) { exit 1 }
$s = Get-Content $settingsPath -Raw | ConvertFrom-Json

# Render helper = this executable.
$helper = $s.SourceDocuments.RenderHelperPath
Check 'RenderHelperPath is the worker executable' ($helper -and ((Resolve-Path $helper -ErrorAction SilentlyContinue).Path -eq (Resolve-Path $exe).Path)) "RenderHelperPath=$helper"

# Folders: absolute, present, and not under the install folder or the web root.
function OutsideOf([string]$path, [string]$root) { if (-not $root) { return $true }; return -not $path.TrimEnd('\').StartsWith($root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) }
foreach ($pair in @(@('SourceDocuments:RootPath', $s.SourceDocuments.RootPath), @('SourceDocuments:RenderWorkRoot', $s.SourceDocuments.RenderWorkRoot), @('Ocr:WorkRoot', $s.Ocr.WorkRoot))) {
    $name, $path = $pair
    Check "$name is an absolute path" ($path -and [System.IO.Path]::IsPathRooted($path)) "$name=$path"
    Check "$name is outside the install folder" ($path -and (OutsideOf $path $InstallRoot))
    if ($WebRoot) { Check "$name is outside the web root" ($path -and (OutsideOf $path $WebRoot)) }
}
Check 'SourceDocuments:RootPath exists (created by the web deployment)' (Test-Path $s.SourceDocuments.RootPath)

# Engine and models.
$engine = $s.Ocr.EnginePath
$tessdata = $s.Ocr.TessdataPath
Check 'Ocr:EnginePath exists' ($engine -and (Test-Path $engine)) "EnginePath=$engine"
Check 'Ocr:EnginePath is under the Tesseract folder' ($engine -and -not (OutsideOf $engine $TesseractRoot))
Check 'Ocr:TessdataPath exists' ($tessdata -and (Test-Path $tessdata)) "TessdataPath=$tessdata"
foreach ($m in @('eng.traineddata', 'osd.traineddata')) { Check "Model $m present" ($tessdata -and (Test-Path (Join-Path $tessdata $m))) }

# Approved hashes: present, complete, and matching what is installed.
Check 'Ocr:RequireApprovedArtifactHashes is not disabled' ($null -eq $s.Ocr.RequireApprovedArtifactHashes -or $s.Ocr.RequireApprovedArtifactHashes -eq $true)
$approved = $s.Ocr.ApprovedArtifactHashes
$approvedNames = if ($approved) { $approved.PSObject.Properties.Name } else { @() }
foreach ($file in @($engine, (Join-Path $tessdata 'eng.traineddata'), (Join-Path $tessdata 'osd.traineddata'))) {
    $leaf = Split-Path $file -Leaf
    $expected = ($approvedNames | Where-Object { $_ -ieq $leaf } | ForEach-Object { $approved.$_ } | Select-Object -First 1)
    $listed = $expected -and ($expected -match '^[0-9a-fA-F]{64}$')
    Check "Approved SHA-256 listed for $leaf" $listed
    if ($listed -and (Test-Path $file)) {
        $actual = (Get-FileHash -Algorithm SHA256 -Path $file).Hash
        Check "Installed $leaf matches its approved SHA-256" ($actual -ieq $expected)
    }
}

# Connection string: Windows authentication, no password, never echoed.
$cs = $s.ConnectionStrings.Emc
Check 'ConnectionStrings:Emc present' ([bool]$cs)
if ($cs) {
    Check 'ConnectionStrings:Emc uses Integrated Security' ($cs -match '(?i)Integrated Security\s*=\s*(true|sspi)')
    Check 'ConnectionStrings:Emc carries no password' (-not ($cs -match '(?i)(password|pwd)\s*='))
    Check 'ConnectionStrings:Emc encrypts the connection' ($cs -match '(?i)Encrypt\s*=\s*true')
}

# Lease arithmetic the worker enforces at start.
$lease = [int]($s.Ocr.LeaseSeconds ?? 900); $page = [int]($s.Ocr.PageTimeoutSeconds ?? 60)
Check 'Ocr:LeaseSeconds >= 2 x Ocr:PageTimeoutSeconds' ($lease -ge 2 * $page) "LeaseSeconds=$lease PageTimeoutSeconds=$page"
$rlease = [int]($s.SourceDocuments.RenderLeaseSeconds ?? 900); $rpage = [int]($s.SourceDocuments.RenderTimeoutSeconds ?? 60)
Check 'SourceDocuments:RenderLeaseSeconds >= 2 x RenderTimeoutSeconds' ($rlease -ge 2 * $rpage) "RenderLeaseSeconds=$rlease RenderTimeoutSeconds=$rpage"

if ($ExpectedSigner -and (Test-Path $exe)) {
    $sig = Get-AuthenticodeSignature $exe
    Check "Worker executable signed by $ExpectedSigner" ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -like "*$ExpectedSigner*")
}

if ($failures -gt 0) { Write-Host "$failures check(s) failed."; exit 1 }
Write-Host 'All prerequisite checks passed.'
exit 0
