# Corpus de Teste e Corrupção Controlada (Milestone 2)

Objetivo: **medir, de forma reproduzível, onde o motor atual de recuperação estruturada quebra** —
antes de qualquer Deep Scan/Carving. O corpus é sempre gerado a partir de **cópias**; o arquivo
original nunca é modificado.

> Não-objetivos deste milestone: Deep Scan/Carving e PST writer. As falhas em cabeçalho
> destruído / B-Tree quebrada são **registradas como limite conhecido**, não corrigidas aqui.

## Pré-requisitos
- `dotnet build MailVault.sln` (gera `src/MailVault.Cli/bin/Debug/net10.0/mailvault.dll`).
- Um PST/OST saudável de origem (fora do repo; ex.: `.local-corpus/ost/small/*.ost`).

## 1. Gerar o corpus

```powershell
./scripts/make-corrupted-corpus.ps1 -Source ".local-corpus\ost\small\mail.ost" -CorpusRoot "test-corpus"
# percentuais de truncamento configuráveis:
./scripts/make-corrupted-corpus.ps1 -Source "...\mail.ost" -TruncatePercents 5,25,50,80
```

Estrutura criada (toda sob `test-corpus/`, ignorada pelo git):

```text
test-corpus/
  source/                 cópia íntegra de referência
  generated/
    healthy/              cópia idêntica (deve recuperar)
    truncated/            cauda removida (10/30/60% por padrão)
    header-damaged/       header-zeroed (512B), magic-broken (!BDN)
    middle-damaged/       blocos contíguos sobrescritos no miolo
    corrupted/            bytes aleatórios espalhados (bit rot / CRC)
    edge-cases/           partial-copy (40%), empty (0B), tiny (8B)
  reports/                saída do runner
  corpus-manifest.json    SHA-256 do original e de cada cópia + parâmetros
```

**Segurança:** o gerador só **lê** o original (`Copy-Item` + `Get-FileHash`); recusa rodar se a raiz
do corpus contiver o original; o `corpus-manifest.json` registra o SHA-256 do original e de cada cópia
para auditoria/reprodutibilidade.

## 2. Rodar a recuperação contra todo o corpus

```powershell
./scripts/run-corpus-recovery.ps1 -CorpusRoot "test-corpus" -MaxMessages 150 -PerFileTimeout "4m"
```

O runner executa `mailvault recover-eml` contra cada cenário (bounded por `--max-messages` apenas para
manter o run rápido — a classificação de comportamento não exige export total) e lê o
`_mailvault-export-report.json` de cada um.

### 2.1 Sweep comparativo Estrutural × Deep Scan (`-DeepScan`)

```powershell
./scripts/run-corpus-recovery.ps1 -CorpusRoot "test-corpus" -MaxMessages 150 -PerFileTimeout "3m" -DeepScan
```

Com `-DeepScan`, para cada cenário o runner roda também o Deep Scan (`recover-eml --deep-scan` → `pffexport`)
em **pasta isolada** (`reports/<cenário>/_deepscan/`) — **não** mistura com os EMLs canônicos e **não** converte
para `MailItem`. Conta **mensagens-equivalentes** do libpff por `OutlookHeaders.txt` (1 por item; pffexport emite
vários arquivos por mensagem, então comparar "arquivos" engana). Gera `corpus-results-with-deepscan.{json,md,csv}`
com, por cenário: status/exportadas do XstReader, status/mensagens/arquivos/tempo do libpff, **valor do libpff**
(`AddsValue` / `NoValue` / `SameFailureAsStructured` / `DiagnosticsOnly` / `Inconclusive`) e **recomendação**
(`UseStructuredOnly` / `UseDeepScanFallback` / `NeedsCSharpCarver` / `DiagnosticsOnly`).

**Resultado medido (2026-05-29):** `AddsValue=0` — o libpff nunca recuperou mais mensagens que o XstReader.
Por isso a **Fase 3b (PffExportParser) NÃO foi iniciada**; libpff fica como diagnóstico/fallback e a próxima
rota é o carver C#. Detalhe em [RECOVERY_PROTOTYPE.md §15.4](RECOVERY_PROTOTYPE.md).

### 2.2 Carving por assinatura (Milestone 3c.1 — `carve`, somente-relatório)

Para os cenários onde XstReader **e** libpff falham ao abrir (cabeçalho destruído, truncamento severo), o
carver C# (`MailVault.Carving`) varre o arquivo fisicamente por assinaturas. **Somente-relatório**: lista
candidatos, **não exporta EML**.

```powershell
mailvault carve "test-corpus\generated\header-damaged\header-zeroed.ost" --out ".\carved"
# limites: --max-scan-bytes --max-candidates --max-candidates-per-mb --chunk-size --overlap-size
#          --max-preview-bytes --timeout --no-previews
```

Gera `_mailvault-carving-report.json/.md` com offsets/encoding/preview dos candidatos `IPM.Note`. O carver
**nunca** roda no `recover-eml` padrão. **Resultado medido (3c.1):** acha 121 candidatos em `header-zeroed`,
79 em `truncated-30%`, 14 em `truncated-60%` — exatamente onde estrutural+libpff retornam 0. Detalhe em
[RECOVERY_PROTOTYPE.md §16](RECOVERY_PROTOTYPE.md).

## 3. Saída consolidada (em `test-corpus/reports/`)
- `corpus-results.json` — registro completo por arquivo + resumo.
- `corpus-results.md` — tabela + seção "Limites conhecidos (candidatos a Deep Scan/Carving)".
- `corpus-results.csv` — mesma tabela para planilha.

Por arquivo são registrados: tipo de corrupção, tamanho, SHA-256, **status**
(`Completed`/`PartialCompleted`/`Failed`/`Cancelled...`), mensagens exportadas, falhas, anexos,
**erro principal**, **se falhou de forma controlada** (gerou relatório / sem crash) e **se gerou relatório**.

## 4. Como interpretar
- **Recuperou algo** (`exportedMessages > 0`): leitura estrutural funcionou (total ou parcial).
- **Failed ao abrir** (`0 exportadas`, `status=Failed`, com relatório): limite do motor atual —
  candidato a Deep Scan/Carving no Milestone 3.
- **Falha controlada** (`controlledFailure=true`): o motor não crashou e produziu relatório — requisito de produto.
- **edge-cases** (empty/tiny) servem para confirmar que entradas degeneradas falham de forma limpa.

A seção "Limites conhecidos" do `corpus-results.md` é a entrada direta para o planejamento do **Milestone 3**.
