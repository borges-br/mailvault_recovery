<#
.SYNOPSIS
  Roda o motor de recuperação (recover-eml) contra todo o corpus e consolida os resultados.

.DESCRIPTION
  Milestone 2 — executa o caminho otimizado contra cada cenário gerado por make-corrupted-corpus.ps1,
  classifica o comportamento (Completed/PartialCompleted/Failed/Cancelled), registra se a falha foi
  CONTROLADA (gerou relatório, sem crash) e gera relatório consolidado JSON/MD/CSV.

  NÃO mascara falhas: cabeçalho destruído / B-Tree quebrada que o motor atual não recupera ficam
  registrados como limite conhecido (candidatos a Deep Scan/Carving no Milestone 3).

.PARAMETER CorpusRoot
  Raiz do corpus (a mesma usada em make-corrupted-corpus.ps1). Default: test-corpus

.PARAMETER MaxMessages
  Limite de mensagens por arquivo (para manter o run rápido — classificação não exige export total). Default: 150.

.PARAMETER PerFileTimeout
  Timeout de segurança por arquivo. Default: 4m.

.PARAMETER CliDll
  Caminho do mailvault.dll. Default: build Debug do CLI.

.EXAMPLE
  ./scripts/run-corpus-recovery.ps1 -CorpusRoot .\test-corpus
#>
[CmdletBinding()]
param(
    [string]$CorpusRoot = "test-corpus",
    [int]$MaxMessages = 150,
    [string]$PerFileTimeout = "4m",
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
Write-Host ("Cenários: {0} | max-messages={1} | timeout/arquivo={2}" -f $manifest.files.Count, $MaxMessages, $PerFileTimeout) -ForegroundColor Cyan
Write-Host ""

$results = [System.Collections.Generic.List[object]]::new()

foreach ($f in $manifest.files) {
    $filePath = Join-Path $root $f.relativePath
    $outDir = Join-Path $reportsDir ("{0}__{1}" -f $f.category, $f.scenario)
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    Write-Host ("-> {0,-16} {1}" -f $f.category, $f.scenario) -ForegroundColor White
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $exit = -999
    try {
        & dotnet $CliDll recover-eml $filePath --out $outDir --max-messages $MaxMessages --timeout $PerFileTimeout *> (Join-Path $outDir "_cli-stdout.log")
        $exit = $LASTEXITCODE
    } catch {
        $exit = -1
    }
    $sw.Stop()

    $repPath = Join-Path $outDir "_mailvault-export-report.json"
    $reportGenerated = Test-Path $repPath
    $status = "NoReport"; $exported = 0; $failed = 0; $attach = 0; $mainError = ""
    if ($reportGenerated) {
        try {
            $r = Get-Content $repPath -Raw | ConvertFrom-Json
            $status = "$($r.status)"
            $exported = [int]$r.exportedMessages
            $failed = [int]$r.failedMessages
            $attach = [int]$r.exportedAttachments
            if ($r.errorsSummary -and $r.errorsSummary.Count -gt 0) { $mainError = "$($r.errorsSummary[0])" }
        } catch { $status = "ReportParseError" }
    }

    # Falha controlada = motor produziu relatório estruturado OU saiu com código conhecido (não crashou).
    $knownExit = @(0,2,3,4,124,130)
    $controlled = $reportGenerated -or ($knownExit -contains $exit)
    $hardCrash = (-not $reportGenerated) -and ($exit -notin $knownExit)

    $rec = [ordered]@{
        category            = $f.category
        scenario            = $f.scenario
        fileName            = $f.fileName
        corruption          = $f.corruption
        sizeBytes           = $f.sizeBytes
        sha256              = $f.sha256
        exitCode            = $exit
        status              = $status
        exportedMessages    = $exported
        failedMessages      = $failed
        exportedAttachments = $attach
        mainError           = $mainError
        controlledFailure   = $controlled
        hardCrash           = $hardCrash
        reportGenerated     = $reportGenerated
        durationSeconds     = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    }
    $results.Add($rec)
    Write-Host ("   status={0} exit={1} exported={2} failed={3} controlado={4} relatório={5} ({6}s)" -f `
        $status, $exit, $exported, $failed, $controlled, $reportGenerated, $rec.durationSeconds) -ForegroundColor Gray
}

# ---- Consolidação ----
$summary = [ordered]@{
    generatedAt        = (Get-Date).ToString("o")
    corpusRoot         = $root
    original           = $manifest.original
    runParams          = [ordered]@{ maxMessages = $MaxMessages; perFileTimeout = $PerFileTimeout }
    totalScenarios     = $results.Count
    recoveredAny       = ($results | Where-Object { $_.exportedMessages -gt 0 }).Count
    failedToOpen       = ($results | Where-Object { $_.exportedMessages -eq 0 -and $_.status -eq "Failed" }).Count
    controlledFailures = ($results | Where-Object { $_.controlledFailure }).Count
    hardCrashes        = ($results | Where-Object { $_.hardCrash }).Count
    results            = $results
}

$jsonPath = Join-Path $reportsDir "corpus-results.json"
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding utf8

# CSV
$csvPath = Join-Path $reportsDir "corpus-results.csv"
$results | ForEach-Object { [pscustomobject]$_ } | Export-Csv -Path $csvPath -NoTypeInformation -Encoding utf8

# Markdown
$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine("# Corpus de Recuperação — Resultados Consolidados")
[void]$md.AppendLine("")
[void]$md.AppendLine("- Original: ``$($manifest.original.path)`` (sha256 ``$($manifest.original.sha256)``)")
[void]$md.AppendLine("- Cenários: $($results.Count) | recuperaram algo: $($summary.recoveredAny) | falha ao abrir: $($summary.failedToOpen) | falhas controladas: $($summary.controlledFailures) | crashes: $($summary.hardCrashes)")
[void]$md.AppendLine("- Run bounded: ``--max-messages $MaxMessages`` (classificação de comportamento, não export total)")
[void]$md.AppendLine("")
[void]$md.AppendLine("| Categoria | Cenário | Status | Exit | Export | Falhas | Controlada | Relatório | Erro principal |")
[void]$md.AppendLine("|---|---|---|---:|---:|---:|:---:|:---:|---|")
foreach ($r in $results) {
    $err = ($r.mainError -replace '\|','/') ; if ($err.Length -gt 60) { $err = $err.Substring(0,57) + "..." }
    [void]$md.AppendLine("| $($r.category) | $($r.scenario) | $($r.status) | $($r.exitCode) | $($r.exportedMessages) | $($r.failedMessages) | $(if($r.controlledFailure){'sim'}else{'NÃO'}) | $(if($r.reportGenerated){'sim'}else{'não'}) | $err |")
}
[void]$md.AppendLine("")
[void]$md.AppendLine("## Limites conhecidos (candidatos a Deep Scan/Carving — Milestone 3)")
[void]$md.AppendLine("")
$limits = $results | Where-Object { $_.exportedMessages -eq 0 -and $_.category -ne 'edge-cases' }
if ($limits.Count -eq 0) { [void]$md.AppendLine("_Nenhum cenário não-edge ficou com 0 mensagens._") }
else { foreach ($l in $limits) { [void]$md.AppendLine("- **$($l.category)/$($l.scenario)**: $($l.status) — $($l.mainError). Leitura estrutural não recupera; exigiria carving por assinatura.") } }
$mdPath = Join-Path $reportsDir "corpus-results.md"
Set-Content -Path $mdPath -Value $md.ToString() -Encoding utf8

Write-Host ""
Write-Host "Relatórios consolidados:" -ForegroundColor Cyan
Write-Host "  $jsonPath"
Write-Host "  $mdPath"
Write-Host "  $csvPath"
Write-Host ("Resumo: {0} cenários | {1} recuperaram algo | {2} falha-ao-abrir | {3} controladas | {4} crashes" -f `
    $summary.totalScenarios, $summary.recoveredAny, $summary.failedToOpen, $summary.controlledFailures, $summary.hardCrashes) -ForegroundColor Green

# O exit code do orquestrador reflete o sucesso da ORQUESTRAÇÃO (relatório gerado),
# não os códigos por arquivo (esses ficam registrados em corpus-results). Um hardCrash sinaliza falha real.
if ($summary.hardCrashes -gt 0) { exit 1 } else { exit 0 }
