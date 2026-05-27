# MailVault Recovery - Script de Publicacao Unificado para Windows
# Este script compila e publica o Desktop e o CLI na mesma pasta de distribuicao side-by-side.
# Agora com empacotamento obrigatorio de ferramentas nativas e conformidade forense/licenca.

param (
    [switch]$SelfContained = $false,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$RequireNativeTools = $false,
    [switch]$SkipNativeTools = $false,
    [switch]$SkipCompile = $false
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================================================" -ForegroundColor Green
Write-Host "  MAILVAULT RECOVERY - SCRIPT DE PUBLICACAO UNIFICADO (WINDOWS)  " -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host "Configuracao:       $Configuration" -ForegroundColor Yellow
Write-Host "Runtime:            $Runtime" -ForegroundColor Yellow
Write-Host "SelfContained:      $SelfContained" -ForegroundColor Yellow
Write-Host "RequireNativeTools: $RequireNativeTools" -ForegroundColor Yellow
Write-Host "SkipNativeTools:    $SkipNativeTools" -ForegroundColor Yellow
Write-Host "=====================================================================" -ForegroundColor Green

# 1. Definir caminhos e limpar saidas anteriores
$RepoRoot = Resolve-Path "$PSScriptRoot\.."
$PublishDir = "$RepoRoot\artifacts\publish\MailVaultRecovery"
$VendorDir = "$RepoRoot\vendor\native-tools\win-x64\libpff"
$ToolsDestDir = "$PublishDir\tools\libpff"

# Enforce native tools requirements in Release by default, unless SkipNativeTools is explicitly passed
$MustHaveNative = $RequireNativeTools -or ($Configuration -eq "Release" -and -not $SkipNativeTools)

if ($MustHaveNative) {
    Write-Host "[VALIDACAO BUNDLE] Verificando presenca dos binarios reais compilados..." -ForegroundColor Cyan
    $pffExportReal = Join-Path $VendorDir "pffexport.exe"
    $pffInfoReal = Join-Path $VendorDir "pffinfo.exe"

    if (-not (Test-Path $pffExportReal) -or -not (Test-Path $pffInfoReal)) {
        Write-Error "ERRO DE BUILD: O build de producao (Release) exige ferramentas nativas reais do libpff em vendor/native-tools/win-x64/libpff/ pffexport.exe/pffinfo.exe. Compile-as primeiro executando .\scripts\build-libpff-windows.ps1."
        Exit 1
    }
}

if (-not $SkipCompile) {
    if (Test-Path $PublishDir) {
        Write-Host "Limpando diretorio de publicacao existente em: $PublishDir..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $PublishDir
    }
}

# Criar pasta de saida se ela nao existir
if (-not (Test-Path $PublishDir)) {
    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
}

# 2. Executar dotnet publish para ambos os projetos
if (-not $SkipCompile) {
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
}

# 3. Empacotamento de Ferramentas Nativas
if (-not $SkipNativeTools -and (Test-Path (Join-Path $VendorDir "pffexport.exe")) -and (Test-Path (Join-Path $VendorDir "pffinfo.exe"))) {
    Write-Host ""
    Write-Host "[3] Empacotando ferramentas nativas libpff..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $ToolsDestDir -Force | Out-Null
    
    Copy-Item (Join-Path $VendorDir "pffexport.exe") (Join-Path $ToolsDestDir "pffexport.exe") -Force
    Copy-Item (Join-Path $VendorDir "pffinfo.exe") (Join-Path $ToolsDestDir "pffinfo.exe") -Force
    Copy-Item (Join-Path $VendorDir "COPYING") (Join-Path $ToolsDestDir "COPYING") -Force
    Copy-Item (Join-Path $VendorDir "COPYING.LESSER") (Join-Path $ToolsDestDir "COPYING.LESSER") -Force
    Copy-Item (Join-Path $VendorDir "checksums.txt") (Join-Path $ToolsDestDir "checksums.txt") -Force

    Write-Host "[OK] Binarios, licenças e checksums copiados com sucesso para $ToolsDestDir" -ForegroundColor Green
} elseif ($MustHaveNative) {
    Write-Error "ERRO CRITICO: Ferramentas nativas ausentes durante fase de copia de release."
    Exit 1
} else {
    Write-Warning "Fase de empacotamento de ferramentas nativas ignorada (SkipNativeTools ou binarios ausentes em modo Dev)."
}

# 4. Verificacoes de Integridade
Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host "  VERIFICACAO DE INTEGRIDADE DOS ARTEFATOS  " -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Green

$DesktopExe = "$PublishDir\MailVault.Desktop.exe"
$CliExe = "$PublishDir\MailVault.Cli.exe"
$AdapterDll = "$PublishDir\MailVault.Adapters.XstReader.dll"
$XstApiDll = "$PublishDir\XstReader.Api.dll"

$AllChecksPassed = $true

if (-not $SkipCompile) {
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
}

# Verificacao das ferramentas nativas em caso de release obrigatorio
if ($MustHaveNative) {
    $PubPffExport = "$ToolsDestDir\pffexport.exe"
    $PubPffInfo = "$ToolsDestDir\pffinfo.exe"

    if (-not (Test-Path $PubPffExport) -or -not (Test-Path $PubPffInfo)) {
        Write-Host "[FALHA] Ferramentas nativas ausentes na pasta publicada do MailVault!" -ForegroundColor Red
        $AllChecksPassed = $false
    } else {
        Write-Host "[OK] Ferramentas nativas do libpff empacotadas com sucesso." -ForegroundColor Green
    }
}

if (-not $AllChecksPassed) {
    Write-Error "A publicacao tecnica falhou devido a arquivos ausentes na pasta de saida."
    Exit 1
}

# 5. Executar testes funcionais basicos do CLI publicado
if (-not $SkipCompile) {
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
}

# 6. Executar probes e gerar manifestos forenses/licencas se as ferramentas nativas estiverem presentes
$PubPffExport = "$ToolsDestDir\pffexport.exe"
$PubPffInfo = "$ToolsDestDir\pffinfo.exe"

if ((Test-Path $PubPffExport) -and (Test-Path $PubPffInfo)) {
    Write-Host ""
    Write-Host "=== Executando Probes de Validacao Forense e Licenciamento ===" -ForegroundColor Cyan
    
    $exportProbe = & $PubPffExport -V 2>&1 | Out-String
    $exportExit = $LASTEXITCODE
    $exportVersion = "Desconhecido"
    if ($exportProbe -match "pffexport\s+(\d{8})") {
        $exportVersion = $Matches[1]
    }
    Write-Host "[PROBE] pffexport.exe version: $exportVersion (Exit: $exportExit)" -ForegroundColor Green

    $infoProbe = & $PubPffInfo -V 2>&1 | Out-String
    $infoExit = $LASTEXITCODE
    $infoVersion = "Desconhecido"
    if ($infoProbe -match "pffinfo\s+(\d{8})") {
        $infoVersion = $Matches[1]
    }
    Write-Host "[PROBE] pffinfo.exe version: $infoVersion (Exit: $infoExit)" -ForegroundColor Green

    if ($exportExit -ne 0 -or $infoExit -ne 0) {
        Write-Error "FALHA DE SMOKE TEST: As ferramentas nativas publicadas nao passaram nos testes de execucao probe. ExitCode pffexport: $exportExit, pffinfo: $infoExit"
        Exit 1
    }

    # Gerar THIRD-PARTY-NOTICES.txt
    Write-Host "Gerando THIRD-PARTY-NOTICES.txt..." -ForegroundColor Cyan
    $copying = Get-Content -Path (Join-Path $ToolsDestDir "COPYING") -Raw
    $lesser = Get-Content -Path (Join-Path $ToolsDestDir "COPYING.LESSER") -Raw
    
    $notices = @"
========================================================================
THIRD-PARTY NOTICES AND LICENSES
========================================================================

This package includes compiled native tools from the libpff project.
Project page: https://github.com/libyal/libpff
Primary author: Joachim Metz <joachim.metz@gmail.com>

The libpff libraries and tools are licensed under the GNU Lesser General Public
License. See below for the full licensing terms.

------------------------------------------------------------------------
LICENSE TERMS FOR libpff (pffexport & pffinfo)
------------------------------------------------------------------------

$copying

========================================================================
ADDITIONAL TERMS (LGPL)
========================================================================

$lesser
"@
    Set-Content -Path "$PublishDir\THIRD-PARTY-NOTICES.txt" -Value $notices -Force

    # Gerar native-tools-manifest.json
    Write-Host "Gerando native-tools-manifest.json..." -ForegroundColor Cyan
    $exportHash = (Get-FileHash $PubPffExport -Algorithm SHA256).Hash.ToLower()
    $infoHash = (Get-FileHash $PubPffInfo -Algorithm SHA256).Hash.ToLower()
    $buildDate = (Get-Date -Format "yyyy-MM-ddTHH:mm:sszzz")

    $manifest = @"
[
  {
    "name": "pffexport",
    "version": "$exportVersion",
    "relativePath": "tools/libpff/pffexport.exe",
    "sha256": "$exportHash",
    "license": "LGPL-3.0-or-later",
    "origin": "https://github.com/libyal/libpff",
    "buildDate": "$buildDate",
    "architecture": "win-x64"
  },
  {
    "name": "pffinfo",
    "version": "$infoVersion",
    "relativePath": "tools/libpff/pffinfo.exe",
    "sha256": "$infoHash",
    "license": "LGPL-3.0-or-later",
    "origin": "https://github.com/libyal/libpff",
    "buildDate": "$buildDate",
    "architecture": "win-x64"
  }
]
"@
    Set-Content -Path "$PublishDir\native-tools-manifest.json" -Value $manifest -Force
    Write-Host "[OK] THIRD-PARTY-NOTICES.txt e native-tools-manifest.json gerados com sucesso." -ForegroundColor Green
}

Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host "  PUBLICACAO CONCLUIDA COM SUCESSO!  " -ForegroundColor Green
Write-Host "  Pasta de saida: $PublishDir" -ForegroundColor Yellow
Write-Host "=====================================================================" -ForegroundColor Green
