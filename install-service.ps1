#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

$ServiceName = "BranchCleanupService"
$PublishDir = Join-Path $PSScriptRoot "publish"
$ExePath = Join-Path $PublishDir "BranchCleanupService.exe"

dotnet publish (Join-Path $PSScriptRoot "BranchCleanupService.csproj") `
    -c Release `
    -o $PublishDir `
    --self-contained false

if (-not (Test-Path $ExePath)) {
    throw "Publish did not produce $ExePath"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists, stopping and removing it first."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $ServiceName binPath= "$ExePath" start= auto | Out-Null
sc.exe description $ServiceName "Daily local git branch cleanup (prunes branches whose remote tracking branch is gone)." | Out-Null

Start-Service -Name $ServiceName

Write-Host "Service '$ServiceName' installed and started. Logs will appear under $PublishDir\Logs"
