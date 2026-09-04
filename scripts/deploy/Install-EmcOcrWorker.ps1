<#
.SYNOPSIS
  AIR-GAPPED, NON-INTERACTIVE: install Emc.OcrWorker as a Windows Service under a dedicated
  low-privilege identity, with the folder rights it needs and nothing more, automatic restart,
  and outbound network access blocked for the worker and the OCR engine.

.DESCRIPTION
  Run as an administrator on the worker host AFTER the published worker folder has been copied
  to -InstallRoot and its appsettings.json has been completed (Set-EmcOcrWorkerConfig.ps1 or by
  hand from docs/ocr-worker-deployment.md). The script:

   1. runs Test-EmcOcrWorkerPrerequisites.ps1 and stops on any failure (paths, approved hashes,
      Integrated Security connection string, render helper = this executable);
   2. creates the data folders the worker writes (render work, OCR work) if absent;
   3. grants the service identity: Read & Execute on the install folder and the Tesseract
      folder; Modify on the source-document store, the render work folder and the OCR work
      folder. It grants nothing to any other principal and removes nothing you granted;
   4. registers the service "EmcOcrWorker" (Automatic start, delayed) running the worker
      executable under the identity; configures recovery: restart after 60 s on the first,
      second and every later failure, counter reset daily;
   5. adds outbound-block Windows Firewall rules for the worker executable (it is also the
      render child) and tesseract.exe: the worker needs no network but SQL Server, which is
      reached through the SQL client in-process - see the note on -SqlServerHost below;
   6. starts the service and prints its status.

  NO CREDENTIAL IS ACCEPTED ON THE COMMAND LINE. Use a group Managed Service Account
  (DOMAIN\name$) - it has no password to handle - or, for a conventional service account, the
  script prompts through Get-Credential. Nothing is written to disk or to the log.

.PARAMETER InstallRoot
  Folder holding the published worker (Emc.OcrWorker.exe, appsettings.json). Default C:\Emc\OcrWorker.
.PARAMETER ServiceAccount
  The dedicated identity, e.g. DOMAIN\gMSA-EmcOcr$ (recommended) or DOMAIN\svc-emc-ocr.
.PARAMETER TesseractRoot
  Folder of the installed engine (tesseract.exe and tessdata\). Default C:\Program Files\Tesseract-OCR.
.PARAMETER SqlServerHost
  Host name of the SQL Server the worker connects to. When given, the outbound-block rules
  are scoped to exclude that host; when omitted the block is "all remote addresses" and you
  must confirm that SQL Server is local to this host. Never an IP literal in source control;
  pass it at install time.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallRoot = 'C:\Emc\OcrWorker',
    [Parameter(Mandatory = $true)][string]$ServiceAccount,
    [string]$TesseractRoot = 'C:\Program Files\Tesseract-OCR',
    [string]$SqlServerHost,
    [string]$ServiceName = 'EmcOcrWorker'
)

$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run as an administrator.'
}

$exe = Join-Path $InstallRoot 'Emc.OcrWorker.exe'
$settingsPath = Join-Path $InstallRoot 'appsettings.json'

# 1. Prerequisites - every check must pass; the checks print what failed, never a secret.
& (Join-Path $PSScriptRoot 'Test-EmcOcrWorkerPrerequisites.ps1') -InstallRoot $InstallRoot -TesseractRoot $TesseractRoot
if ($LASTEXITCODE -ne 0) { throw 'Prerequisite checks failed. Nothing was installed.' }

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$documentsRoot = $settings.SourceDocuments.RootPath
$renderWork    = $settings.SourceDocuments.RenderWorkRoot
$ocrWork       = $settings.Ocr.WorkRoot

# 2. Data folders the worker writes.
foreach ($folder in @($renderWork, $ocrWork)) {
    if (-not (Test-Path $folder)) {
        if ($PSCmdlet.ShouldProcess($folder, 'Create folder')) { New-Item -ItemType Directory -Path $folder | Out-Null }
    }
}
if (-not (Test-Path $documentsRoot)) { throw "The source-document store $documentsRoot does not exist. It is created by the web deployment and shared with the worker." }

# 3. Least-privilege ACLs for the identity. (OI)(CI) = files and subfolders inherit.
$grants = @(
    @{ Path = $InstallRoot;   Rights = 'RX' },
    @{ Path = $TesseractRoot; Rights = 'RX' },
    @{ Path = $documentsRoot; Rights = 'M'  },
    @{ Path = $renderWork;    Rights = 'M'  },
    @{ Path = $ocrWork;       Rights = 'M'  }
)
foreach ($g in $grants) {
    if ($PSCmdlet.ShouldProcess($g.Path, "Grant $($g.Rights) to $ServiceAccount")) {
        & icacls.exe $g.Path /grant "$($ServiceAccount):(OI)(CI)$($g.Rights)" /T /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls failed on $($g.Path)." }
    }
}

# 4. The service. A gMSA needs no credential; anything else is prompted for, never passed in.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) { throw "Service $ServiceName already exists. Run Uninstall-EmcOcrWorker.ps1 first." }

$serviceParams = @{
    Name           = $ServiceName
    BinaryPathName = "`"$exe`""
    DisplayName    = 'EMC OCR and Render Worker'
    Description    = 'Evidence Management Companion: renders companion-copy pages and runs local OCR in an isolated process. No network use.'
    StartupType    = 'AutomaticDelayedStart'
}
if ($ServiceAccount.EndsWith('$')) {
    $serviceParams.Credential = New-Object System.Management.Automation.PSCredential($ServiceAccount, (New-Object System.Security.SecureString))
} else {
    $serviceParams.Credential = Get-Credential -UserName $ServiceAccount -Message "Password for the service identity $ServiceAccount (not stored)"
}
if ($PSCmdlet.ShouldProcess($ServiceName, 'Create service')) {
    New-Service @serviceParams | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null
}

# 5. No outbound network for the worker (which is also the render child) or the engine.
$blockTargets = @(
    @{ Name = 'EMC OCR Worker - block outbound'; Program = $exe },
    @{ Name = 'EMC Tesseract - block outbound';  Program = (Join-Path $TesseractRoot 'tesseract.exe') }
)
foreach ($t in $blockTargets) {
    Get-NetFirewallRule -DisplayName $t.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($PSCmdlet.ShouldProcess($t.Name, 'Create outbound block rule')) {
        $rule = @{ DisplayName = $t.Name; Direction = 'Outbound'; Program = $t.Program; Action = 'Block'; Profile = 'Any'; Enabled = 'True' }
        if ($SqlServerHost -and $t.Program -eq $exe) {
            # Block everything except the SQL Server host; resolved now, at install time, on this host.
            $sql = [System.Net.Dns]::GetHostAddresses($SqlServerHost) | ForEach-Object { $_.IPAddressToString }
            New-NetFirewallRule @rule -RemoteAddress 'Any' | Out-Null
            New-NetFirewallRule -DisplayName ($t.Name + ' - allow SQL Server') -Direction Outbound -Program $t.Program -Action Allow -RemoteAddress $sql -Profile Any | Out-Null
        } else {
            New-NetFirewallRule @rule | Out-Null
        }
    }
}
if (-not $SqlServerHost) { Write-Warning 'No -SqlServerHost given: outbound is blocked to every remote address. This is correct only when SQL Server is on this host.' }

# 6. Start it and show what happened. The worker refuses to start on an unapproved engine,
#    a missing render helper or a bad lease configuration - by design; read the event log.
if ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 5
    $svc = Get-Service -Name $ServiceName
    Write-Host "$ServiceName is $($svc.Status)."
    if ($svc.Status -ne 'Running') {
        Write-Warning 'The service did not stay running. Check the Application event log (source EmcOcrWorker); the start-up check that failed is named there by category.'
        exit 1
    }
}
