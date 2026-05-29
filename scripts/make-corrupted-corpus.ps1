<#
.SYNOPSIS
  Gera um corpus de validação com cópias controladamente corrompidas de um PST/OST saudável.

.DESCRIPTION
  Cria cenários de corrupção reais (descritos na pesquisa MS-PST) SEMPRE sobre CÓPIAS.
  O arquivo de origem nunca é aberto para escrita nem modificado. Útil para validar, de
  forma honesta, o comportamento de "falha controlada" e "recuperação parcial" do MailVault.

  Cenários gerados:
    - truncated-tail.<ext>     : cópia com os últimos N% de bytes removidos (transferência interrompida).
    - damaged-header.<ext>     : cópia com os primeiros 512 bytes zerados (assinatura !BDN destruída).
    - corrupted-blocks.<ext>   : cópia com regiões internas embaralhadas (setores defeituosos / CRC inválido).

.PARAMETER Source
  Caminho do PST/OST saudável de origem (somente leitura).

.PARAMETER OutDir
  Pasta de saída para as cópias corrompidas. Default: test-corpus/corrupted ao lado do repo.

.PARAMETER TruncatePercent
  Percentual da cauda a remover no cenário truncated-tail (default 15).

.EXAMPLE
  ./scripts/make-corrupted-corpus.ps1 -Source .\.local-corpus\ost\small\mail.ost -OutDir .\test-corpus\corrupted
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Source,
    [string]$OutDir = "test-corpus/corrupted",
    [ValidateRange(1, 90)] [int]$TruncatePercent = 15
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    Write-Error "Origem não encontrada: $Source"
    exit 1
}

$srcItem = Get-Item -LiteralPath $Source
$ext = $srcItem.Extension
if ([string]::IsNullOrEmpty($ext)) { $ext = ".pst" }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$resolvedSrc = $srcItem.FullName
$resolvedOut = (Resolve-Path -LiteralPath $OutDir).Path

if ($resolvedSrc.StartsWith($resolvedOut, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "Recusado: a pasta de saída contém o arquivo de origem. Escolha outro -OutDir para proteger o original."
    exit 1
}

Write-Host "Origem (somente leitura): $resolvedSrc" -ForegroundColor Cyan
Write-Host "Saída: $resolvedOut" -ForegroundColor Cyan
$rng = [System.Random]::new(20260529)

function New-CorruptCopy([string]$name) {
    $dest = Join-Path $resolvedOut $name
    Copy-Item -LiteralPath $resolvedSrc -Destination $dest -Force
    return $dest
}

# 1. truncated-tail — remove os últimos N% de bytes
$truncated = New-CorruptCopy "truncated-tail$ext"
$len = (Get-Item -LiteralPath $truncated).Length
$keep = [int64]([math]::Floor($len * (100 - $TruncatePercent) / 100.0))
$fs = [System.IO.File]::Open($truncated, 'Open', 'ReadWrite')
try { $fs.SetLength($keep) } finally { $fs.Dispose() }
Write-Host "[OK] truncated-tail$ext  ($len -> $keep bytes, -$TruncatePercent%)" -ForegroundColor Green

# 2. damaged-header — zera os primeiros 512 bytes (destrói a magia !BDN e o cabeçalho NDB)
$damaged = New-CorruptCopy "damaged-header$ext"
$fs = [System.IO.File]::Open($damaged, 'Open', 'ReadWrite')
try {
    $zeros = New-Object byte[] 512
    $fs.Position = 0
    $fs.Write($zeros, 0, [Math]::Min(512, [int][Math]::Min($fs.Length, 512)))
} finally { $fs.Dispose() }
Write-Host "[OK] damaged-header$ext  (primeiros 512 bytes zerados)" -ForegroundColor Green

# 3. corrupted-blocks — embaralha pequenas regiões internas (após o cabeçalho)
$blocks = New-CorruptCopy "corrupted-blocks$ext"
$fs = [System.IO.File]::Open($blocks, 'Open', 'ReadWrite')
try {
    $size = $fs.Length
    if ($size -gt 8192) {
        $regionCount = 12
        $regionSize = 256
        for ($i = 0; $i -lt $regionCount; $i++) {
            # mantém o cabeçalho intacto (começa após 4 KB); só danifica blocos de dados
            $minPos = 4096
            $maxPos = $size - $regionSize - 1
            $pos = [int64]($minPos + $rng.NextDouble() * ($maxPos - $minPos))
            $buf = New-Object byte[] $regionSize
            $rng.NextBytes($buf)
            $fs.Position = $pos
            $fs.Write($buf, 0, $regionSize)
        }
        Write-Host "[OK] corrupted-blocks$ext  ($regionCount regiões de $regionSize bytes embaralhadas)" -ForegroundColor Green
    } else {
        Write-Warning "Arquivo pequeno demais para o cenário corrupted-blocks; pulado."
    }
} finally { $fs.Dispose() }

Write-Host ""
Write-Host "Cópias corrompidas geradas. Original preservado e intacto." -ForegroundColor Green
Write-Host "Valide com: mailvault inspect <copia> --out .\mailvault-cases" -ForegroundColor Yellow
Write-Host "         e: mailvault recover-eml <copia> --out .\exports\<cenario>" -ForegroundColor Yellow
