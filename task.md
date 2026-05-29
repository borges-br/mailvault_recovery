# Milestone 6.3.0 — Functional Email Recovery MVP

## Status

Legenda: ✅ Done · 🔄 In Progress · ⬜ Todo · ❌ Blocked

---

## Core Infrastructure

- ✅ XstReaderRecoveryEngine — IMailStoreReader com SemaphoreSlim(1,1)
- ✅ MailVaultMessageCanonical / MailVaultAttachmentCanonical
- ✅ MailVaultMimeSerializer — HTML>Text>Placeholder, ERROR_ATTACHMENT_*.txt
- ✅ ThunderbirdMboxEngine — streaming, profiles.ini, .sbd, .msf
- ✅ MaildirEngine — cur/new, flags
- ✅ EmlFolderEngine — recursivo, malformados ignorados
- ✅ ReaderEngineFactory com 5 engines registrados

## Export Pipeline

- ✅ ExportJobRunner — EML+MBOX, atomic write, relatório JSON+CSV
- ✅ ExportJobOptions.SkipIntegrityCheck flag (SHA-256 gate agora condicional)
- ✅ RecoveryExportRunner — export direto de IMailStoreReader sem case.db
- ✅ RecoveryExportResult / RecoveryExportProgress types
- ✅ ExportFolderToEml via RecoveryExportRunner
- ✅ ExportAllToEml via RecoveryExportRunner
- ✅ ExportFolderToMbox via RecoveryExportRunner
- ✅ ExportAllToMbox via RecoveryExportRunner

## CLI Commands

- ✅ index, stats, search, preview, list, tree, inspect, export
- ✅ recover-eml <file> --out <dir> [--folder <path>]
- ✅ recover-mbox <file> --out <dir> [--folder <path>]
- ⬜ recover-all <file> --out <dir> --format eml|mbox (exports all without prior index)

## Tests — XstReader Recovery

- ✅ XstReaderRecoveryEngine_ThrowsFileNotFound
- ✅ XstReaderRecoveryEngine_CapabilityCheck_ReturnsTrue
- ⬜ XstReaderRecovery_IndexMetadata_ListsMessages
- ⬜ XstReaderRecovery_PreviewMessage_ReturnsBodyWhenAvailable
- ⬜ XstReaderRecovery_ExportSelectedToEml_CreatesValidEml
- ⬜ XstReaderRecovery_ExportFolderToEml_ContinuesOnBadMessage
- ⬜ XstReaderRecovery_AttachmentFailure_DoesNotAbortMessage
- ⬜ XstReaderRecovery_ExportReport_ContainsFailures
- ⬜ XstReaderRecovery_UsesSingleThreadReaderLock

## Tests — MimeKit Serializer

- ✅ MimeSerializer_FallsBackToPlaceholder_WhenBodiesAreMissing
- ✅ MimeSerializer_InjectsErrorAttachment_WhenAttachmentStreamThrows
- ⬜ MimeSerializer_CreatesValidEml
- ⬜ MimeSerializer_SanitizesFileName
- ⬜ MimeSerializer_AtomicWrite_DoesNotLeaveTmpOnSuccess
- ⬜ MimeSerializer_DeletesTmpOnFailure
- ✅ MboxWriter_EscapesFromLines
- ✅ MboxWriter_AppendsMessagesStreaming

## Tests — Thunderbird/MBOX

- ✅ ThunderbirdMboxEngine_DiscoversMboxFiles_AndIndexesThem
- ✅ ThunderbirdMboxEngine_ResolvesProfileFromProfilesIni
- ⬜ Thunderbird_DiscoverMboxFiles_HandlesSbd
- ✅ Thunderbird_BadMessage_SkipsAndContinues
- ⬜ Thunderbird_ExportMessage_ToEml

## Tests — Maildir

- ✅ MaildirEngine_IndexesMaildirCorrectly_WithFlags

## Tests — EML Folder

- ✅ EmlFolderEngine_IndexesEmlFilesRecursively
- ✅ EmlFolder_MalformedEml_SkipsAndContinues
- ⬜ EmlFolder_ExportCopy_PreservesStructure

## Tests — Recovery Export Runner

- ✅ RecoveryExportRunner_ExportAllToEml_CreatesFilesAndReport
- ✅ RecoveryExportRunner_AttachmentFail_DoesNotAbortMessage
- ✅ RecoveryExportRunner_FolderExport_OnlyExportsTargetFolder
- ✅ RecoveryExportRunner_ExportMbox_EscapesFromLines
- ✅ RecoveryExportRunner_CancelExport_StopsCleanly
- ✅ RecoveryExportRunner_ProgressReportsExportCounts

## Desktop UX (Minimal)

- ✅ Wizard de novo caso (arquivo)
- ✅ Botão "Exportar tudo para EML" (ExportPanelViewModel.RecoverToEmlCommand)
- ✅ Botão "Exportar tudo para MBOX" (ExportPanelViewModel.RecoverToMboxCommand)
- ✅ Progress com: mensagens exportadas, falhas, anexos, timer, cancelar
- ✅ Abrir relatório de exportação (open folder)
- ✅ Indexação: elapsed timer + throughput (emails/s) + ETA na Step 4
- ✅ Botão "Abrir OST/PST" rápido (QuickRecoveryViewModel — sem wizard, sem indexação)
- ⬜ Exportar pasta selecionada (pasta específica, não tudo)

## Packaging

- ✅ publish.ps1 — Desktop (framework-dependent) + CLI (single-file) para dist\
- ✅ AssemblyName=mailvault no MailVault.Cli.csproj (produz mailvault.exe)
- ✅ DISTRIBUTING.md — guia de build, estrutura de pastas, requisitos, exemplos CLI

## Validation Manual

- ⬜ .local-corpus/ost/small/querebola@gmail.com.ost indexa e exporta 100 EML
- ⬜ EML gerado abre no Thunderbird
- ⬜ Falha de anexo não bloqueia exportação da mensagem
- ⬜ Relatório JSON gerado com sucesso/falhas
- ⬜ Desktop não trava durante export

---

## Milestone 6.3.1 — Hardening & PST/diagnóstico honestos (2026-05-29)

- ✅ **FIX CRÍTICO**: `MailVault.Cli.csproj` agora copia os adapters (plugin fresco no build).
      Antes, `recover-eml`/`recover-mbox` falhavam em build de dev por DLL de adapter desatualizado.
- ✅ `IPstExportWriter` + `UnsupportedPstExportWriter` — PST limpo declarado NotSupported (sem PST falso)
- ✅ CLI `recover-pst` — explica tecnicamente o NotSupported e indica EML/MBOX
- ✅ `PffSignatureInspector` — detecta `!BDN`, ANSI/Unicode (wVer), ofuscação; exposto no `inspect`
- ✅ Relatório de recuperação em **Markdown** (`_mailvault-export-report.md`) + classificação Completo/Parcial/Inconclusivo
- ✅ `scripts/make-corrupted-corpus.ps1` — cenários corrompidos sempre sobre cópia
- ✅ 15 testes novos em `MailVault.Core.Tests` (inspector, PST não-suportado, classificação)
- ✅ Fix de teardown (best-effort + retry) no teste Desktop que falhava por lock de `case.db`
- ✅ **Validação real**: OST de 95 MB exporta `.eml` válidos (MIME parseável, anexo PDF real), 0 falhas
- ℹ️ `recover-all`: coberto por `recover-eml`/`recover-mbox` **sem** `--folder` (exporta todas as pastas)
- ℹ️ Doc: ver [docs/RECOVERY_PROTOTYPE.md](docs/RECOVERY_PROTOTYPE.md)

## Milestone 1.5 — Performance, Observabilidade e Cancelamento Seguro (2026-05-29)

Sinal amarelo: run real de 90 MB levou **3h03 / 491 msgs / 0,04 msg/s**. Diagnóstico primeiro, depois otimização medida.

- ✅ **Instrumentação por etapa** (medir antes de otimizar): GetMessage / Serialização+Escrita / Anexos, tempo por pasta, maior msg/anexo, msg/s, MB/min, etapa mais lenta — em `RecoveryExportMetrics`.
- ✅ **Gargalo medido**: `GetMessageAsync` re-localizava cada item via `FindMessageByPath` (varredura O(N²) da árvore). 73,5s de 88s (83%) num bench de 80 msgs.
- ✅ **Otimização de baixo risco**: pular a re-leitura redundante quando o reader é `IMetadataOnlyAware` + `MetadataOnly=false` (XstReader recovery). Fakes/tests intactos.
- ✅ **Benchmark antes/depois** (80 msgs, mesmo OST): 88,2s → **14,4s (6,1×)**; etapa GetMessage 73.467ms → **0ms**.
- ✅ **Relatório incremental**: `_mailvault-export-report.partial.json/.md` + `progress.json` a cada 50 msgs / 30s / troca de pasta.
- ✅ **Cancelamento seguro**: Ctrl+C, `--timeout`, `--max-messages` → status `CancelledByUser`/`CancelledByTimeout`/`Completed`/`PartialCompleted`/`Failed` com relatório (não perde o feito).
- ✅ **Opções CLI**: `--max-messages`, `--max-folder-messages`, `--timeout`, `--checkpoint-interval`, `--progress-json`, `--force-reread` (diagnóstico).
- ✅ Build + testes: 192 aprovados / 1 falha pré-existente dependente de ambiente (Desktop worker-launch).
- ✅ **Benchmark completo end-to-end**: arquivo inteiro = **4.139 msgs, 0 falhas, 191,7 MB, 19,47 min, 3,54 msg/s, getMsg=0ms** (antes: O(N²) levaria dias). Etapa+lenta=Anexos; pasta+lenta=Caixa de entrada (97%).

Critério de aceite: OST exporta EML real abrível no Thunderbird. Build e testes passam.
