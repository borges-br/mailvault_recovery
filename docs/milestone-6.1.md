# Milestone 6.1 — Desktop UX Recovery, Case Loading Fix & Vulnerability Cleanup

## Objetivo

Corrigir os problemas funcionais e de segurança detectados após o lançamento inicial da UI Desktop (Milestone 6):

1. Eliminar a vulnerabilidade NU1903 (`Tmds.DBus.Protocol` 0.15.0 — GHSA-xrw6-gwf8-vvr9).
2. Corrigir a tela "Carregando..." infinita ao abrir casos.
3. Corrigir a falsa mensagem "case.db não encontrado" para pastas que têm o arquivo.
4. Tratar `case.db-journal` como warning não fatal.
5. Tratar `manifest.json` ausente como modo limitado, não erro fatal.
6. Implementar `CaseWorkspaceDiagnosticService`, `CaseWorkspaceService` e `RecentCasesService`.
7. Implementar `LoadableViewModelBase` com timeout de 15 segundos e estados de loading claros.
8. Adicionar 10 testes mandatórios.

---

## Correção de Vulnerabilidade

**Pacote afetado**: `Tmds.DBus.Protocol` 0.15.0  
**Severidade**: Alta (GHSA-xrw6-gwf8-vvr9)  
**Causa**: Puxado transitivamente por `Avalonia.Desktop` 11.0.10 no Linux.  
**Solução**: Override direto de versão nos arquivos `.csproj`:

```xml
<!-- NU1903: Override transitivamente de Tmds.DBus.Protocol para versão corrigida -->
<PackageReference Include="Tmds.DBus.Protocol" Version="0.21.3" />
```

Aplicado em:
- `src/MailVault.Desktop/MailVault.Desktop.csproj`
- `tests/MailVault.Desktop.Tests/MailVault.Desktop.Tests.csproj`

**Resultado**: `dotnet list package --vulnerable --include-transitive` → zero vulnerabilidades em todos os 19 projetos.

---

## Novos Serviços

### `CaseWorkspaceDiagnosticService`

Localização: `src/MailVault.Desktop/Services/CaseWorkspaceDiagnosticService.cs`

Retorna um `CaseValidationResult` com:
- `DirectoryExists`, `CaseDbExists`, `ManifestExists`, `AuditLogExists`
- `JournalFileExists` (warning, não erro fatal)
- `CaseDbReadable`, `SchemaVersion`, `CaseInfoExists`, `TablesExist`
- `ErrorMessage`, `SuggestedAction`
- `IsHealthy` (todos os campos OK)
- `CanOpenLimited` (diretório + DB acessível, mesmo sem manifest)

**Regras críticas**:
- Se `File.Exists("case.db")` → `CaseDbExists = true`. Nunca reporta "não encontrado" para arquivo presente.
- Journal é warning; a conexão SQLite é aberta em modo ReadOnly — o próprio SQLite recupera o journal.
- Manifest ausente → `ManifestExists = false`, mas `CanOpenLimited = true`.

### `CaseWorkspaceService`

Localização: `src/MailVault.Desktop/Services/CaseWorkspaceService.cs`

- Délega diagnóstico ao `CaseWorkspaceDiagnosticService`.
- Retorna `CaseOpenResult(CaseFolderPath, Store, OpenMode, WarningMessage)`.
- Modos: `Full`, `LimitedNoManifest`, `LimitedJournal`.
- Implementa `OpenInputAsync(path)` para roteamento automático: pasta de caso, `.ost`, `.pst`.

### `RecentCasesService`

Localização: `src/MailVault.Desktop/Services/RecentCasesService.cs`

- Persiste histórico em `%AppData%\MailVault\recent-cases.json`.
- Armazena apenas: `caseId`, `caseFolderPath`, `openMode`, `lastOpenedAt`, `schemaVersion`.
- **Nunca armazena**: corpo de e-mails, anexos, hashes completos, headers, caminhos privados sensíveis.
- Mantém no máximo 10 entradas (FIFO).

---

## `LoadableViewModelBase`

Localização: `src/MailVault.Desktop/ViewModels/LoadableViewModelBase.cs`

Estados via enum `LoadingState`:
- `Idle`, `Loading`, `Loaded`, `Empty`, `Error`, `Cancelled`

Método central: `ExecuteLoadAsync(Func<CancellationToken, Task>, string)`:
- Cancela operações anteriores
- Aplica timeout de 15 segundos via `CancellationTokenSource.CreateLinkedTokenSource`
- Define `State = Error` com `ErrorMessage` em caso de exceção
- Define `State = Cancelled` se o token de cancelamento for acionado pelo timeout
- Nunca deixa o VM em estado `Loading` permanentemente

**ViewModels refatoradas**:
- `HomeViewModel` → `LoadableViewModelBase`
- `CaseOverviewViewModel` → `LoadableViewModelBase`

---

## Correções de UX

### HomeViewModel

- `OpenCaseCommand` agora usa `ExecuteLoadAsync` com diagnóstico real.
- Falsa mensagem "case.db não encontrado" eliminada — só aparece se `File.Exists` retorna `false`.
- `WarningBanner` exibe avisos de journal e manifest ausente.
- Adicionados `OpenMboxCaseCommand` e `RecentCases` (ObservableCollection).

### MainWindowViewModel

- Integra `CaseWorkspaceDiagnosticService`, `CaseWorkspaceService` e `RecentCasesService`.
- Exibe `WarningBanner` quando caso é aberto em modo limitado.
- Registra caso em `RecentCasesService` após abertura bem-sucedida.
- Refresca lista de recentes ao fechar caso.

---

## Testes

### Novos Testes (10 mandatórios)

| Teste | Resultado |
|---|---|
| `CaseWorkspaceService_DetectsExistingCaseDb` | ✅ |
| `CaseWorkspaceService_DetectsMissingManifestButAllowsLimitedOpen` | ✅ |
| `CaseWorkspaceService_DetectsJournalFile` | ✅ |
| `CaseWorkspaceService_DoesNotReportMissingCaseDbWhenItExists` | ✅ |
| `CaseWorkspaceService_DetectsMissingCaseDb` | ✅ |
| `CaseOverviewViewModel_DoesNotStayLoadingOnError` | ✅ |
| `CaseOverviewViewModel_LoadsStatsFromSyntheticCaseDb` | ✅ |
| `HomeViewModel_ExposesOpenCaseCreateCaseMboxAndRecentCases` | ✅ |
| `RecentCasesService_SavesAndLoadsRecentCases` | ✅ |
| `DesktopDependencyAudit_NoKnownVulnerabilities` | ✅ |

### Total da Suíte

```
Aprovado! – Com falha: 0, Aprovado: 58, Ignorado: 0, Total: 58
```

(Era 48 na Milestone 6.)

---

## Validação de Segurança

```
dotnet list package --vulnerable --include-transitive
→ Nenhum pacote vulnerável detectado em todos os 19 projetos.
```
