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

Critério de aceite: OST exporta EML real abrível no Thunderbird. Build e testes passam.
