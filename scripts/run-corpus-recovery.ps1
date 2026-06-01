<#
.SYNOPSIS
  Roda o motor de recuperação (recover-eml) contra todo o corpus e consolida os resultados.
  Com -DeepScan, também roda libpff/pffexport em pasta isolada e compara o ganho real (em MENSAGENS).

.DESCRIPTION
  Milestone 2/3 — executa o caminho otimizado contra cada cenário gerado por make-corrupted-corpus.ps1,
  classifica o comportamento e gera relatório consolidado JSON/MD/CSV.

  -DeepScan (Milestone 3, sweep comparativo): para cada arquivo, roda recuperação estrutural normal E o
  Deep Scan (pffexport, via recover-eml --deep-scan). A saída do libpff fica ISOLADA em <out>/_deepscan
  (NÃO é misturada com os EMLs canônicos, NÃO é convertida para MailItem). Conta mensagens-equivalentes do
  libpff por OutlookHeaders.txt (1 por item) e classifica o VALOR real do libpff por cenário.

  NÃO implementa carving C#, NÃO implementa PST writer, NÃO converte saída libpff para o pipeline.

.PARAMETER CorpusRoot   Raiz do corpus. Default: test-corpus
.PARAMETER MaxMessages  Limite de mensagens por arquivo (0 = ilimitado). Default: 150.
.PARAMETER PerFileTimeout  Timeout de segurança por arquivo (estrutural e deep scan). Default: 4m.
.PARAMETER DeepScan     Habilita o sweep comparativo com libpff/pffexport.
.PARAMETER CliDll       Caminho do mailvault.dll. Default: build Debug do CLI.

.EXAMPLE
  ./scripts/run-corpus-recovery.ps1 -CorpusRoot .\test-corpus -DeepScan -PerFileTimeout 3m
#>
[CmdletBinding()]
param(
    [string]$CorpusRoot = "test-corpus",
    [int]$MaxMessages = 150,
    [string]$PerFileTimeout = "4m",
    [switch]$DeepScan,
    [string]$CliDll = "src/MailVault.Cli/bin/Debug/net10.0/mailvault.dll"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $CorpusRoot)) { Write-Error "Corpus não encontrado: $CorpusRoot. Rode make-corrupted-corpus.ps1 primeiro."; exit 1 }
$root = (Resolve-Path -LiteralPath $CorpusRoot).Path
$manifestPath = Join-Path $root "corpus-manifest.json"
if (-not (Test-Path $manifestPath)) { Write-Error "corpus-manifest.json ausente em $root."; exit 1 }
if (-not (Test-Path $CliDll)) { Write-Error "CLI não encontrado: $CliDll. Rode 'dotnet build' primeiro."; exit 1 }

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$reportsDir = Join-Path $root "reports"
New-Item -ItemType Directory -Path $reportsDir -Force | Out-Null

Write-Host "Corpus: $root" -ForegroundColor Cyan
Write-Host ("Cenários: {0} | max-messages={1} | timeout/arquivo={2} | DeepScan={3}" -f $manifest.files.Count, $MaxMessages, $PerFileTimeout, [bool]$DeepScan) -ForegroundColor Cyan
Write-Host ""

function Classify-LibpffValue($structStatus, $structMsgs, $structCapped, $pffStatus, $pffMsgs) {
    if ($pffStatus -eq 'ToolNotAvailable') { return @('Inconclusive', 'DiagnosticsOnly') }
    $structFailed = ($structStatus -eq 'Failed') -or ($structMsgs -eq 0)
    $pffOpened = $pffStatus -notin @('OpenFailed', 'ToolNotAvailable')
    if ($structFailed -and -not $pffOpened) { return @('SameFailureAsStructured', 'NeedsCSharpCarver') }
    if ($structFailed -and $pffMsgs -gt 0) { return @('AddsValue', 'UseDeepScanFallback') }
    if ($structCapped -or ($pffStatus -eq 'Timeout')) { return @('Inconclusive', 'UseStructuredOnly') }
    if ($pffMsgs -gt [int]($structMsgs * 1.1)) { return @('AddsValue', 'UseDeepScanFallback') }
    if ($pffMsgs -eq 0 -and $structMsgs -gt 0) { return @('NoValue', 'UseStructuredOnly') }
    if ([math]::Abs($pffMsgs - $structMsgs) -le [int]($structMsgs * 0.1)) { return @('NoValue', 'UseStructuredOnly') }
    if ($pffMsgs -lt $structMsgs) { return @('NoValue', 'UseStructuredOnly') }
    return @('Inconclusive', 'DiagnosticsOnly')
}

$results = [System.Collections.Generic.List[object]]::new()

foreach ($f in $manifest.files) {
    $filePath = Join-Path $root $f.relativePath
    $outDir = Join-Path $reportsDir ("{0}__{1}" -f $f.category, $f.scenario)
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    Write-Host ("-> {0,-16} {1}" -f $f.category, $f.scenario) -ForegroundColor White
    $cliArgs = @("recover-eml", $filePath, "--out", $outDir, "--timeout", $PerFileTimeout)
    if ($MaxMessages -gt 0) { $cliArgs += @("--max-messages", "$MaxMessages") }
    if ($DeepScan) { $cliArgs += "--deep-scan" }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $exit = -999
    try {
        & dotnet $CliDll @cliArgs *> (Join-Path $outDir "_cli-stdout.log")
        $exit = $LASTEXITCODE
    } catch { $exit = -1 }
    $sw.Stop()

    # ---- estrutural ----
    $repPath = Join-Path $outDir "_mailvault-export-report.json"
    $reportGenerated = Test-Path $repPath
    $status = "NoReport"; $exported = 0; $failed = 0; $attach = 0; $coverage = $null; $mainError = ""
    if ($reportGenerated) {
        try {
            $r = Get-Content $repPath -Raw | ConvertFrom-Json
            $status = "$($r.status)"; $exported = [int]$r.exportedMessages; $failed = [int]$r.failedMessages
            $attach = [int]$r.exportedAttachments
            if ($r.metrics) { $coverage = $r.metrics.coveragePercent }
            if ($r.errorsSummary -and $r.errorsSummary.Count -gt 0) { $mainError = "$($r.errorsSummary[0])" }
        } catch { $status = "ReportParseError" }
    }
    $knownExit = @(0, 2, 3, 4, 124, 130)
    $controlled = $reportGenerated -or ($knownExit -contains $exit)
    $hardCrash = (-not $reportGenerated) -and ($exit -notin $knownExit)
    $structCapped = ($MaxMessages -gt 0 -and $exported -ge $MaxMessages) -or ($status -in @('CancelledByTimeout', 'CancelledByUser'))

    $rec = [ordered]@{
        category = $f.category; scenario = $f.scenario; fileName = $f.fileName; corruption = $f.corruption
        sizeBytes = $f.sizeBytes; sha256 = $f.sha256
        xstStatus = $status; xstExported = $exported; xstFailed = $failed; xstAttachments = $attach
        xstCoveragePercent = $coverage; exitCode = $exit; mainError = $mainError
        controlledFailure = $controlled; hardCrash = $hardCrash; reportGenerated = $reportGenerated
        durationSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    }

    if ($DeepScan) {
        # ---- libpff (isolado em _deepscan) ----
        $dsPath = Join-Path $outDir "_mailvault-deepscan-report.json"
        $pffStatus = "NoReport"; $pffOpened = $false; $pffFiles = 0; $pffBytes = 0; $pffMs = 0; $pffErr = ""
        if (Test-Path $dsPath) {
            try {
                $d = Get-Content $dsPath -Raw | ConvertFrom-Json
                $pffStatus = "$($d.status)"; $pffOpened = [bool]$d.opened; $pffFiles = [int]$d.extractedFiles
                $pffBytes = [long]$d.extractedBytes; $pffMs = [long]$d.elapsedMs
                if ($d.errorSummary) { $pffErr = "$($d.errorSummary)" }
            } catch { $pffStatus = "ReportParseError" }
        }
        # mensagens-equivalentes do libpff = 1 OutlookHeaders.txt por item exportado
        $deepDir = Join-Path $outDir "_deepscan"
        $pffMsgs = 0
        if (Test-Path $deepDir) {
            $pffMsgs = (Get-ChildItem -Recurse -File -Filter "OutlookHeaders.txt" $deepDir -ErrorAction SilentlyContinue | Measure-Object).Count
        }
        $cls = Classify-LibpffValue $status $exported $structCapped $pffStatus $pffMsgs

        $rec["pffStatus"] = $pffStatus
        $rec["pffOpened"] = $pffOpened
        $rec["pffMessagesEquivalent"] = $pffMsgs
        $rec["pffFilesTotal"] = $pffFiles
        $rec["pffBytesTotal"] = $pffBytes
        $rec["pffElapsedMs"] = $pffMs
        $rec["pffError"] = $pffErr
        $rec["libpffValue"] = $cls[0]
        $rec["recommendation"] = $cls[1]

        Write-Host ("   xst={0}/{1}msgs | libpff={2}/{3}msgs ({4} arq) | valor={5} -> {6}" -f `
            $status, $exported, $pffStatus, $pffMsgs, $pffFiles, $cls[0], $cls[1]) -ForegroundColor Gray
    } else {
        Write-Host ("   status={0} exit={1} exported={2} failed={3} controlado={4} ({5}s)" -f `
            $status, $exit, $exported, $failed, $controlled, $rec.durationSeconds) -ForegroundColor Gray
    }
    $results.Add($rec)
}

# ---- Consolidação ----
$baseName = if ($DeepScan) { "corpus-results-with-deepscan" } else { "corpus-results" }
$summary = [ordered]@{
    generatedAt = (Get-Date).ToString("o"); corpusRoot = $root; original = $manifest.original
    deepScan = [bool]$DeepScan
    runParams = [ordered]@{ maxMessages = $MaxMessages; perFileTimeout = $PerFileTimeout }
    totalScenarios = $results.Count
    recoveredAny = ($results | Where-Object { $_.xstExported -gt 0 }).Count
    controlledFailures = ($results | Where-Object { $_.controlledFailure }).Count
    hardCrashes = ($results | Where-Object { $_.hardCrash }).Count
    results = $results
}
if ($DeepScan) {
    $summary["libpffValueCounts"] = [ordered]@{
        AddsValue = ($results | Where-Object { $_.libpffValue -eq 'AddsValue' }).Count
        NoValue = ($results | Where-Object { $_.libpffValue -eq 'NoValue' }).Count
        SameFailureAsStructured = ($results | Where-Object { $_.libpffValue -eq 'SameFailureAsStructured' }).Count
        DiagnosticsOnly = ($results | Where-Object { $_.libpffValue -eq 'DiagnosticsOnly' }).Count
        Inconclusive = ($results | Where-Object { $_.libpffValue -eq 'Inconclusive' }).Count
    }
}

$jsonPath = Join-Path $reportsDir "$baseName.json"
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding utf8
$csvPath = Join-Path $reportsDir "$baseName.csv"
$results | ForEach-Object { [pscustomobject]$_ } | Export-Csv -Path $csvPath -NoTypeInformation -Encoding utf8

$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine("# Corpus de Recuperação — $(if($DeepScan){'Estrutural × Deep Scan (libpff)'}else{'Resultados'})")
[void]$md.AppendLine("")
[void]$md.AppendLine("- Original: ``$($manifest.original.path)`` (sha256 ``$($manifest.original.sha256)``)")
[void]$md.AppendLine("- Cenários: $($results.Count) | recuperaram algo: $($summary.recoveredAny) | falhas controladas: $($summary.controlledFailures) | crashes: $($summary.hardCrashes)")
[void]$md.AppendLine("- Run bounded: ``--max-messages $MaxMessages`` / ``--timeout $PerFileTimeout``")
[void]$md.AppendLine("")
if ($DeepScan) {
    $vc = $summary.libpffValueCounts
    [void]$md.AppendLine("**Valor do libpff:** AddsValue=$($vc.AddsValue) · NoValue=$($vc.NoValue) · SameFailureAsStructured=$($vc.SameFailureAsStructured) · DiagnosticsOnly=$($vc.DiagnosticsOnly) · Inconclusive=$($vc.Inconclusive)")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| Categoria | Cenário | Dano | XstReader | msgs(xst) | libpff | msgs(pff) | arq(pff) | tempo(pff) | Valor | Recomendação |")
    [void]$md.AppendLine("|---|---|---|---|---:|---|---:|---:|---:|---|---|")
    foreach ($r in $results) {
        [void]$md.AppendLine("| $($r.category) | $($r.scenario) | $($r.corruption) | $($r.xstStatus) | $($r.xstExported) | $($r.pffStatus) | $($r.pffMessagesEquivalent) | $($r.pffFilesTotal) | $([math]::Round($r.pffElapsedMs/1000,1))s | **$($r.libpffValue)** | $($r.recommendation) |")
    }
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## Leitura")
    [void]$md.AppendLine("- **msgs(pff)** = itens exportados pelo libpff (contagem de ``OutlookHeaders.txt``, 1 por item) — comparável a msgs(xst).")
    [void]$md.AppendLine("- **AddsValue** = libpff recuperou mensagens que o estrutural não pegou (>10% a mais, ou abriu onde o estrutural falhou).")
    [void]$md.AppendLine("- **SameFailureAsStructured** = ambos falham ao abrir (cabeçalho/truncamento) → candidato a carver C# (Route C).")
    [void]$md.AppendLine("- **NoValue** = libpff recupera o mesmo conjunto (ou menos) que o estrutural.")
    [void]$md.AppendLine("- A saída do libpff fica isolada em ``_deepscan/`` (não misturada, não convertida para MailItem).")
} else {
    [void]$md.AppendLine("| Categoria | Cenário | Status | Exit | Export | Falhas | Controlada | Relatório | Erro |")
    [void]$md.AppendLine("|---|---|---|---:|---:|---:|:---:|:---:|---|")
    foreach ($r in $results) {
        $err = ($r.mainError -replace '\|', '/'); if ($err.Length -gt 50) { $err = $err.Substring(0, 47) + "..." }
        [void]$md.AppendLine("| $($r.category) | $($r.scenario) | $($r.xstStatus) | $($r.exitCode) | $($r.xstExported) | $($r.xstFailed) | $(if($r.controlledFailure){'sim'}else{'NÃO'}) | $(if($r.reportGenerated){'sim'}else{'não'}) | $err |")
    }
}
$mdPath = Join-Path $reportsDir "$baseName.md"
Set-Content -Path $mdPath -Value $md.ToString() -Encoding utf8

Write-Host ""
Write-Host "Relatórios consolidados:" -ForegroundColor Cyan
Write-Host "  $jsonPath"; Write-Host "  $mdPath"; Write-Host "  $csvPath"
if ($DeepScan) {
    $vc = $summary.libpffValueCounts
    Write-Host ("Valor libpff: AddsValue={0} NoValue={1} SameFailure={2} DiagnosticsOnly={3} Inconclusive={4}" -f `
        $vc.AddsValue, $vc.NoValue, $vc.SameFailureAsStructured, $vc.DiagnosticsOnly, $vc.Inconclusive) -ForegroundColor Green
}
if ($summary.hardCrashes -gt 0) { exit 1 } else { exit 0 }
