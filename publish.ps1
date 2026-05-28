#!/usr/bin/env pwsh
# MailVault Recovery — Windows distribution build
param(
    [string]$OutputDir = ".\dist",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true
)

$ErrorActionPreference = "Stop"

Write-Host "==> Building MailVault Recovery distribution..." -ForegroundColor Cyan
Write-Host "    Runtime: $Runtime"
Write-Host "    Output:  $OutputDir"

# Clean output
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

# Publish Desktop app
Write-Host "`n==> Publishing Desktop (GUI)..." -ForegroundColor Yellow
dotnet publish src/MailVault.Desktop/MailVault.Desktop.csproj `
    -r $Runtime `
    --self-contained:$SelfContained `
    -c Release `
    -p:PublishSingleFile=false `
    -o "$OutputDir\MailVaultRecovery" `
    --nologo

# Publish CLI
Write-Host "`n==> Publishing CLI..." -ForegroundColor Yellow
dotnet publish src/MailVault.Cli/MailVault.Cli.csproj `
    -r $Runtime `
    --self-contained:$SelfContained `
    -c Release `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$OutputDir\MailVaultCli" `
    --nologo

Write-Host "`n==> Distribution built successfully!" -ForegroundColor Green
Write-Host "    Desktop: $OutputDir\MailVaultRecovery\MailVault.Desktop.exe"
Write-Host "    CLI:     $OutputDir\MailVaultCli\mailvault.exe"

# Show output sizes
$desktopSize = (Get-ChildItem "$OutputDir\MailVaultRecovery" -Recurse | Measure-Object Length -Sum).Sum / 1MB
$cliSize = (Get-ChildItem "$OutputDir\MailVaultCli" -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host "`n==> Sizes:"
Write-Host "    Desktop: $([math]::Round($desktopSize, 1)) MB"
Write-Host "    CLI:     $([math]::Round($cliSize, 1)) MB"
