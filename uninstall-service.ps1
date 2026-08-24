#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$ServiceName = "BranchCleanupService"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed."
    exit 0
}

Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
sc.exe delete $ServiceName | Out-Null

Write-Host "Service '$ServiceName' uninstalled."
