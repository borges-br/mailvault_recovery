# MailVault Recovery - Script de Publicacao Unificado para Windows
# Este script compila e publica o Desktop e o CLI na mesma pasta de distribuicao side-by-side.

param (
    [switch]$SelfContained = $false,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================================================" -ForegroundColor Green
Write-Host "  MAILVAULT RECOVERY - SCRIPT DE PUBLICACAO UNIFICADO (WINDOWS)  " -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host "Configuracao: $Configuration" -ForegroundColor Yellow
Write-Host "Runtime:      $Runtime" -ForegroundColor Yellow
Write-Host "SelfContained: $SelfContained" -ForegroundColor Yellow
Write-Host "=====================================================================" -ForegroundColor Green

# 1. Definir caminhos e limpar saidas anteriores
$RepoRoot = Resolve-Path "$PSScriptRoot\.."
$PublishDir = "$RepoRoot\artifacts\publish\MailVaultRecovery"

if (Test-Path $PublishDir) {
    Write-Host "Limpando diretorio de publicacao existente em: $PublishDir..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $PublishDir
}

# Criar pasta de saida
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

# 2. Executar dotnet publish para ambos os projetos
$SelfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

Write-Host ""
Write-Host "[1/2] Publicando MailVault.Cli..." -ForegroundColor Cyan
dotnet publish "$RepoRoot\src\MailVault.Cli\MailVault.Cli.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained $SelfContainedValue `
    -o $PublishDir

Write-Host ""
Write-Host "[2/2] Publicando MailVault.Desktop..." -ForegroundColor Cyan
dotnet publish "$RepoRoot\src\MailVault.Desktop\MailVault.Desktop.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained $SelfContainedValue `
    -o $PublishDir

# 3. Verificacoes de Integridade
Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host "  VERIFICACAO DE INTEGRIDADE DOS ARTEFATOS  " -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Green

$DesktopExe = "$PublishDir\MailVault.Desktop.exe"
$CliExe = "$PublishDir\MailVault.Cli.exe"
$AdapterDll = "$PublishDir\MailVault.Adapters.XstReader.dll"
$XstApiDll = "$PublishDir\XstReader.Api.dll"

$AllChecksPassed = $true

if (Test-Path $DesktopExe) {
    Write-Host "[OK] MailVault.Desktop.exe localizado com sucesso." -ForegroundColor Green
} else {
    Write-Host "[FALHA] MailVault.Desktop.exe nao foi gerado!" -ForegroundColor Red
    $AllChecksPassed = $false
}

if (Test-Path $CliExe) {
    Write-Host "[OK] MailVault.Cli.exe localizado com sucesso." -ForegroundColor Green
} else {
    Write-Host "[FALHA] MailVault.Cli.exe nao foi gerado!" -ForegroundColor Red
    $AllChecksPassed = $false
}

if (Test-Path $AdapterDll) {
    Write-Host "[OK] MailVault.Adapters.XstReader.dll localizado com sucesso." -ForegroundColor Green
} else {
    Write-Host "[FALHA] MailVault.Adapters.XstReader.dll ausente na pasta publicada!" -ForegroundColor Red
    $AllChecksPassed = $false
}

if (Test-Path $XstApiDll) {
    Write-Host "[OK] XstReader.Api.dll localizado com sucesso." -ForegroundColor Green
} else {
    Write-Host "[FALHA] XstReader.Api.dll ausente na pasta publicada!" -ForegroundColor Red
    $AllChecksPassed = $false
}

if (-not $AllChecksPassed) {
    Write-Error "A publicacao tecnica falhou devido a arquivos ausentes na pasta de saida."
}

# 4. Executar testes funcionais basicos do CLI publicado
Write-Host ""
Write-Host "[TESTE FUNCIONAL CLI] Testando ajuda geral do CLI..." -ForegroundColor Cyan
& $CliExe --help | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "[OK] 'MailVault.Cli.exe --help' executado com código 0." -ForegroundColor Green
} else {
    Write-Warning "Falha ao executar 'MailVault.Cli.exe --help'. ExitCode: $LASTEXITCODE"
}

Write-Host "[TESTE FUNCIONAL CLI] Testando ajuda do subcomando index-worker..." -ForegroundColor Cyan
& $CliExe index-worker --help | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "[OK] 'MailVault.Cli.exe index-worker --help' executado com código 0." -ForegroundColor Green
} else {
    Write-Warning "Falha ao executar 'MailVault.Cli.exe index-worker --help'. ExitCode: $LASTEXITCODE"
}

Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host "  PUBLICACAO CONCLUIDA COM SUCESSO!  " -ForegroundColor Green
Write-Host "  Pasta de saida: $PublishDir" -ForegroundColor Yellow
Write-Host "=====================================================================" -ForegroundColor Green
