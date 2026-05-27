# Scripts to build libpff on Windows using WSL cross-compilation
# Enforces native-built authentic win-x64 binaries

$ErrorActionPreference = "Stop"

$workspaceRoot = Resolve-Path "$PSScriptRoot\.."
$libPffDir = Join-Path $workspaceRoot ".libpff"
$vendorDir = Join-Path $workspaceRoot "vendor\native-tools\win-x64\libpff"

Write-Host "=== Building libpff for MailVault Recovery ===" -ForegroundColor Cyan

# 1. Verify .libpff exists
if (-not (Test-Path $libPffDir)) {
    Write-Error "Pasta .libpff não encontrada na raiz do repositório em $libPffDir. Certifique-se de que o submódulo/referência foi clonado."
    Exit 1
}

# 2. Verify WSL is available
Write-Host "Verificando se o WSL está disponível..."
$wslCheck = wsl --status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "WSL não está instalado ou configurado no sistema. Não é possível compilar de forma cruzada."
    Exit 1
}

# 3. Check if Ubuntu-24.04 is available
Write-Host "Verificando distribuição Ubuntu-24.04..."
$wslList = wsl -l -v | Out-String
$wslListClean = $wslList -replace "`0", ""
if ($wslListClean -notmatch "Ubuntu-24.04") {
    Write-Error "Distribuição WSL Ubuntu-24.04 necessária não está instalada. Output: $wslListClean"
    Exit 1
}

# 4. Prepare build environment inside WSL
Write-Host "Configurando permissões do Git no WSL..."
wsl -d Ubuntu-24.04 -u root bash -c "git config --global --add safe.directory '*'"

# Install dependencies if missing
Write-Host "Verificando e instalando dependências no WSL (build-essential, autotools, cross-compiler, gettext)..."
wsl -d Ubuntu-24.04 -u root bash -c "apt-get update && apt-get install -y build-essential automake autoconf libtool pkg-config mingw-w64 git gettext autopoint"

# 5. Run build steps inside WSL
Write-Host "Sincronizando dependências de bibliotecas C..."
wsl -d Ubuntu-24.04 -u root bash -c "cd /mnt/c/Github/mailvault_recovery/.libpff && ./synclibs.sh"

Write-Host "Executando autogen..."
wsl -d Ubuntu-24.04 -u root bash -c "cd /mnt/c/Github/mailvault_recovery/.libpff && ./autogen.sh"

Write-Host "Executando configure para cross-compilation win-x64..."
wsl -d Ubuntu-24.04 -u root bash -c "cd /mnt/c/Github/mailvault_recovery/.libpff && ./configure --host=x86_64-w64-mingw32 --enable-static-executables --enable-shared=no --enable-static=yes"

Write-Host "Compilando binários nativos..."
wsl -d Ubuntu-24.04 -u root bash -c 'cd /mnt/c/Github/mailvault_recovery/.libpff && make -j$(nproc)'

# 6. Copy compiled binaries and licenses to vendor/
Write-Host "Preparando pasta vendor..."
if (-not (Test-Path $vendorDir)) {
    New-Item -ItemType Directory -Path $vendorDir -Force | Out-Null
}

$pffExportSrc = Join-Path $libPffDir "pfftools\pffexport.exe"
$pffInfoSrc = Join-Path $libPffDir "pfftools\pffinfo.exe"

if (-not (Test-Path $pffExportSrc) -or -not (Test-Path $pffInfoSrc)) {
    Write-Error "Compilação concluída, mas os arquivos binários esperados (pffexport.exe/pffinfo.exe) não foram encontrados em .libpff/pfftools/."
    Exit 1
}

Write-Host "Copiando binários compilados..."
Copy-Item $pffExportSrc (Join-Path $vendorDir "pffexport.exe") -Force
Copy-Item $pffInfoSrc (Join-Path $vendorDir "pffinfo.exe") -Force

Write-Host "Copiando licenças..."
Copy-Item (Join-Path $libPffDir "COPYING") (Join-Path $vendorDir "COPYING") -Force
Copy-Item (Join-Path $libPffDir "COPYING.LESSER") (Join-Path $vendorDir "COPYING.LESSER") -Force

# 7. Executing probes and validating
Write-Host "Executando probes de validação..."
$pffExportDest = Join-Path $vendorDir "pffexport.exe"
$pffInfoDest = Join-Path $vendorDir "pffinfo.exe"

$probeExport = & $pffExportDest -V 2>&1 | Out-String
$exitCodeExport = $LASTEXITCODE
Write-Host "Probe pffexport: $probeExport"
if ($exitCodeExport -ne 0 -or $probeExport -notmatch "pffexport") {
    Write-Error "Falha ao validar pffexport.exe compilado. ExitCode: $exitCodeExport"
    Exit 1
}

$probeInfo = & $pffInfoDest -V 2>&1 | Out-String
$exitCodeInfo = $LASTEXITCODE
Write-Host "Probe pffinfo: $probeInfo"
if ($exitCodeInfo -ne 0 -or $probeInfo -notmatch "pffinfo") {
    Write-Error "Falha ao validar pffinfo.exe compilado. ExitCode: $exitCodeInfo"
    Exit 1
}

# 8. Generate checksums.txt
Write-Host "Gerando checksums.txt..."
$exportHash = (Get-FileHash $pffExportDest -Algorithm SHA256).Hash.ToLower()
$infoHash = (Get-FileHash $pffInfoDest -Algorithm SHA256).Hash.ToLower()

$checksumContent = @"
$exportHash  pffexport.exe
$infoHash  pffinfo.exe
"@
Set-Content -Path (Join-Path $vendorDir "checksums.txt") -Value $checksumContent

Write-Host "=== Compilação e Integração Concluídas com Sucesso! ===" -ForegroundColor Green
