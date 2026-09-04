<#
.SYNOPSIS
  Stop and remove the EmcOcrWorker Windows Service and its firewall rules. Data folders (the
  source-document store, the work folders) and the installed engine are LEFT IN PLACE: nothing
  under evidence-room accountability is deleted by an uninstall.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = 'EmcOcrWorker'
)

$ErrorActionPreference = 'Stop'
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped' -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop service')) {
        # A clean stop: the loop observes the stop request between jobs; a leased job's lease
        # is left to expire and is retried after the next start.
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Delete service')) {
        & sc.exe delete $ServiceName | Out-Null
    }
    Write-Host "$ServiceName removed."
} else {
    Write-Host "$ServiceName is not installed."
}

foreach ($name in @('EMC OCR Worker - block outbound', 'EMC OCR Worker - block outbound - allow SQL Server', 'EMC Tesseract - block outbound')) {
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | ForEach-Object {
        if ($PSCmdlet.ShouldProcess($name, 'Remove firewall rule')) { $_ | Remove-NetFirewallRule }
    }
}
