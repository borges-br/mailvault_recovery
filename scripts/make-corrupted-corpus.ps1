<#
.SYNOPSIS
  Gera um corpus de teste reproduzível com cópias controladamente corrompidas de um PST/OST.

.DESCRIPTION
  Milestone 2 — Corpus real + corrupção controlada. SEMPRE opera sobre CÓPIAS; o arquivo de
  origem nunca é aberto para escrita. Gera a estrutura recomendada e um manifesto JSON com
  SHA-256 do original e de cada cópia, para testes reproduzíveis do motor de recuperação.

  Estrutura criada (sob -CorpusRoot, default ./test-corpus):
    source/                      cópia íntegra do original (referência)
    generated/
      healthy/                   cópia idêntica (deve recuperar 100%)
      truncated/                 cauda removida (transferência interrompida)
      header-damaged/            primeiros bytes / assinatura !BDN destruídos
      middle-damaged/            blocos contíguos no meio sobrescritos (setor defeituoso)
      corrupted/                 bytes aleatórios espalhados (bit rot / CRC)
      edge-cases/                cópia parcial, arquivo vazio, arquivo minúsculo
    reports/                     (usado por run-corpus-recovery.ps1)
    corpus-manifest.json         hashes e metadados de cada cenário

.PARAMETER Source
  Caminho do PST/OST saudável de origem (somente leitura).

.PARAMETER CorpusRoot
  Raiz do corpus. Default: ./test-corpus

.PARAMETER TruncatePercents
  Percentuais de cauda a remover. Default: 10, 30, 60.

.EXAMPLE
  ./scripts/make-corrupted-corpus.ps1 -Source .\.local-corpus\ost\small\mail.ost
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Source,
    [string]$CorpusRoot = "test-corpus",
    [int[]]$TruncatePercents = @(10, 30, 60)
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    Write-Error "Origem não encontrada: $Source"; exit 1
}
$srcItem = Get-Item -LiteralPath $Source
$ext = if ([string]::IsNullOrEmpty($srcItem.Extension)) { ".pst" } else { $srcItem.Extension }
$resolvedSrc = $srcItem.FullName

# Proteção: a saída não pode conter a origem.
New-Item -ItemType Directory -Path $CorpusRoot -Force | Out-Null
$root = (Resolve-Path -LiteralPath $CorpusRoot).Path
if ($resolvedSrc.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "Recusado: -CorpusRoot contém o arquivo de origem. Escolha outra raiz."; exit 1
}

$dirs = @("source", "generated/healthy", "generated/truncated", "generated/header-damaged",
          "generated/middle-damaged", "generated/corrupted", "generated/edge-cases", "reports")
foreach ($d in $dirs) { New-Item -ItemType Directory -Path (Join-Path $root $d) -Force | Out-Null }

Write-Host "Origem (somente leitura): $resolvedSrc" -ForegroundColor Cyan
Write-Host "Corpus: $root" -ForegroundColor Cyan
$rng = [System.Random]::new(20260529)
$manifest = [System.Collections.Generic.List[object]]::new()

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower() }

function Register([string]$category, [string]$scenario, [string]$path, [string]$corruption, [string]$desc, $params) {
    $it = Get-Item -LiteralPath $path
    $rel = $it.FullName.Substring($root.Length).TrimStart('\','/').Replace('\','/')
    $script:manifest.Add([ordered]@{
        category    = $category
        scenario    = $scenario
        fileName    = $it.Name
        relativePath = $rel
        sizeBytes   = $it.Length
        sha256      = (Get-Sha256 $it.FullName)
        corruption  = $corruption
        description = $desc
        params      = $params
    })
    Write-Host ("  [OK] {0,-16} {1} ({2:N0} bytes)" -f $category, $it.Name, $it.Length) -ForegroundColor Green
}

# Cópia de referência íntegra em source/
$sourceCopy = Join-Path $root "source\original$ext"
Copy-Item -LiteralPath $resolvedSrc -Destination $sourceCopy -Force

function New-Copy([string]$category, [string]$name) {
    $dest = Join-Path $root "generated\$category\$name$ext"
    Copy-Item -LiteralPath $resolvedSrc -Destination $dest -Force
    return $dest
}

# 1. healthy — cópia idêntica
$h = New-Copy "healthy" "healthy-copy"
Register "healthy" "healthy-copy" $h "none" "Cópia idêntica do original; deve recuperar 100%." @{}

# 2. truncated — remove os últimos N% (configurável)
foreach ($pct in $TruncatePercents) {
    $t = New-Copy "truncated" "truncated-$($pct)pct"
    $len = (Get-Item -LiteralPath $t).Length
    $keep = [int64]([math]::Floor($len * (100 - $pct) / 100.0))
    $fs = [System.IO.File]::Open($t, 'Open', 'ReadWrite'); try { $fs.SetLength($keep) } finally { $fs.Dispose() }
    Register "truncated" "truncated-$($pct)pct" $t "tail-truncation" "Removidos os últimos $pct% de bytes (transferência/cópia interrompida)." @{ percentRemoved = $pct; keptBytes = $keep }
}

# 3a. header-damaged — primeiros 512 bytes zerados (cabeçalho NDB + magia)
$hz = New-Copy "header-damaged" "header-zeroed"
$fs = [System.IO.File]::Open($hz, 'Open', 'ReadWrite')
try { $z = New-Object byte[] 512; $fs.Position = 0; $fs.Write($z, 0, [Math]::Min(512, [int][Math]::Min($fs.Length,512))) } finally { $fs.Dispose() }
Register "header-damaged" "header-zeroed" $hz "header-zeroed-512" "Primeiros 512 bytes zerados (cabeçalho e assinatura destruídos)." @{ zeroedBytes = 512 }

# 3b. header-damaged — apenas a assinatura !BDN corrompida (4 primeiros bytes)
$hm = New-Copy "header-damaged" "magic-broken"
$fs = [System.IO.File]::Open($hm, 'Open', 'ReadWrite')
try { $b = [byte[]](0x00,0x00,0x00,0x00); $fs.Position = 0; $fs.Write($b, 0, 4) } finally { $fs.Dispose() }
Register "header-damaged" "magic-broken" $hm "magic-broken" "Assinatura mágica !BDN (4 bytes iniciais) zerada; resto intacto." @{ brokenBytes = 4 }

# 4. middle-damaged — regiões contíguas no meio sobrescritas (setor defeituoso)
$md = New-Copy "middle-damaged" "middle-blocks"
$fs = [System.IO.File]::Open($md, 'Open', 'ReadWrite')
try {
    $size = $fs.Length; $regions = 0
    if ($size -gt 16384) {
        $regionSize = 512; $regions = 16
        for ($i = 0; $i -lt $regions; $i++) {
            $minPos = [int64]($size * 0.25); $maxPos = [int64]($size * 0.75) - $regionSize
            $pos = [int64]($minPos + $rng.NextDouble() * ($maxPos - $minPos))
            $buf = New-Object byte[] $regionSize; $rng.NextBytes($buf)
            $fs.Position = $pos; $fs.Write($buf, 0, $regionSize)
        }
    }
} finally { $fs.Dispose() }
Register "middle-damaged" "middle-blocks" $md "middle-block-overwrite" "16 regiões de 512 bytes sobrescritas no miolo (25%-75%); cabeçalho intacto." @{ regions = 16; regionSize = 512 }

# 5. corrupted — bytes aleatórios espalhados (bit rot / CRC inválido)
$cr = New-Copy "corrupted" "random-bytes"
$fs = [System.IO.File]::Open($cr, 'Open', 'ReadWrite')
try {
    $size = $fs.Length
    if ($size -gt 8192) {
        $flips = 2000
        for ($i = 0; $i -lt $flips; $i++) {
            $pos = [int64](4096 + $rng.NextDouble() * ($size - 4097))
            $fs.Position = $pos; $fs.WriteByte([byte]$rng.Next(0, 256))
        }
    }
} finally { $fs.Dispose() }
Register "corrupted" "random-bytes" $cr "scattered-byte-flips" "2000 bytes aleatórios sobrescritos após o cabeçalho (bit rot / CRC)." @{ flips = 2000 }

# 6a. edge-cases — cópia parcial (somente primeiros 40%)
$pc = New-Copy "edge-cases" "partial-copy"
$len = (Get-Item -LiteralPath $pc).Length
$fs = [System.IO.File]::Open($pc, 'Open', 'ReadWrite'); try { $fs.SetLength([int64]([math]::Floor($len * 0.40))) } finally { $fs.Dispose() }
Register "edge-cases" "partial-copy" $pc "partial-copy-40pct" "Somente os primeiros 40% do arquivo (cópia interrompida cedo)." @{ percentKept = 40 }

# 6b. edge-cases — arquivo vazio
$ef = Join-Path $root "generated\edge-cases\empty$ext"
[System.IO.File]::WriteAllBytes($ef, @())
Register "edge-cases" "empty" $ef "empty-file" "Arquivo de 0 bytes." @{}

# 6c. edge-cases — arquivo minúsculo (menor que um cabeçalho)
$tf = Join-Path $root "generated\edge-cases\tiny$ext"
[System.IO.File]::WriteAllBytes($tf, [byte[]](0x21,0x42,0x44,0x4E,0x00,0x00,0x00,0x00))
Register "edge-cases" "tiny" $tf "tiny-file" "8 bytes (apenas a magia !BDN, sem cabeçalho completo)." @{ bytes = 8 }

# Manifesto
$manifestObj = [ordered]@{
    generatedAt = (Get-Date).ToString("o")
    original = [ordered]@{
        path = $resolvedSrc
        sizeBytes = $srcItem.Length
        sha256 = (Get-Sha256 $resolvedSrc)
    }
    truncatePercents = $TruncatePercents
    files = $manifest
}
$manifestPath = Join-Path $root "corpus-manifest.json"
$manifestObj | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding utf8

Write-Host ""
Write-Host "Manifesto: $manifestPath" -ForegroundColor Cyan
Write-Host ("Total de cenários gerados: {0} (original preservado e intacto)" -f $manifest.Count) -ForegroundColor Green
Write-Host "Próximo: ./scripts/run-corpus-recovery.ps1 -CorpusRoot `"$CorpusRoot`"" -ForegroundColor Yellow
