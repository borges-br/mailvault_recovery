# Troubleshooting

Este guia parte do comportamento real do CLI, Desktop, workers, SQLite e ferramentas externas.

## Diagnóstico rápido

| Sintoma | Causa provável | Como diagnosticar | Como corrigir ou mitigar |
| --- | --- | --- | --- |
| `pffexport` não está disponível | Libpff não instalado ou fora dos caminhos procurados | Verificar `native-tool-diagnostic-report.json`, Test Lab ou `Get-Command pffexport` | Instalar libpff localmente, adicionar ao `PATH` ou usar XstReader. O publish atual não empacota `pffexport`. |
| Motor `LibpffExternal` falha imediatamente | `pffexport` não foi localizado | Mensagem: utilitário externo não disponível | Selecionar `XstReader` ou instalar `pffexport`. |
| `LibpffExternal` termina com `Failed` | `pffexport` retornou exit code diferente de zero | Ler `libpff-diagnostic.log` no case folder | Revisar integridade da evidência, permissões e argumentos. O fluxo é experimental. |
| Publicado sem leitura PST/OST | `MailVault.Adapters.XstReader.dll` ou `XstReader.Api.dll` ausente | Conferir pasta publicada e logs do script | Rodar `.\scripts\publish-windows.ps1` e corrigir erros de publish. |
| Desktop não encontra CLI worker | `MailVault.Cli.exe` não está ao lado do Desktop nem em layout de dev | Erro de `WorkerExecutableResolver` com caminhos testados | Compilar CLI ou publicar com o script oficial. Opcionalmente usar `MAILVAULT_CLI_PATH`. |
| Worker produz stdout não JSON | Processo externo ou wrapper contaminou stdout | UI registra issue de protocolo; procurar mensagem de contaminação | Corrigir o worker/ferramenta para emitir JSON em stdout e logs em stderr. |
| Workspace não abre | `case.db` ausente, schema inválido ou pasta incorreta | Desktop mostra diagnóstico; verificar `case.db`, `manifest.json`, `audit.log` | Selecionar a pasta do caso, não o arquivo original. Recriar caso se o DB estiver ausente. |
| Workspace abre em modo limitado | `manifest.json` ausente ou journal residual | Diagnóstico do Desktop lista warnings | Preservar arquivos existentes e abrir limitado para inspeção; recriar manifest apenas com procedimento controlado. |
| Exportação aborta por hash divergente | Evidência original mudou, foi substituída ou caminho aponta para arquivo diferente | Export recalcula SHA-256 e compara com `case_info` | Localizar a cópia original usada na indexação ou criar novo caso. |
| Exportação não escreve arquivos | `--dry-run` ativo ou pasta sem mensagens selecionadas | Conferir opções do comando e `export-manifest.json` | Remover `--dry-run`, revisar filtros `--folder`, `--limit` e `--offset`. |
| Validação aponta EML vazio | Arquivo exportado ficou com 0 bytes | `validation-report.json` inclui `VAL-ERR-EMPTYEML` | Reexportar, revisar espaço em disco e permissões. |
| Validação aponta MBOX com `From ` interno | Linha do corpo não foi escapada como mboxrd | `validation-report.json` inclui warning/erro de MBOX | Reexportar com exporter atual; preservar relatório para investigação se persistir. |
| Processamento muito longo | Evidência grande, arquivo corrompido ou reader lento | Observar progresso/heartbeat no Desktop e `index_runs` | Aguardar, cancelar se necessário e guardar logs. Use `--limit` em testes. |
| UI em estado "Não Respondendo" | Job pesado, I/O lento ou bloqueio externo | Verificar se worker segue ativo e se há crescimento em `case.db` | Evitar rede/sync folders, usar disco local e cancelar pelo Desktop se necessário. |
| Falha de indexação parcial | Pasta/mensagem corrompida ou limitação do reader | Conferir tabela `issues`, `audit.log` e mensagens do CLI | Exportar o que foi indexado, preservar issues e testar fallback experimental se aplicável. |
| Logs ausentes | Caso criado manualmente ou processo interrompido cedo | Conferir `audit.log` e `manifest.json` | Recriar caso pelo CLI/Desktop. Não inventar logs retroativos em evidência sensível. |
| Artefato publicado incompleto | Publish interrompido ou pasta antiga reutilizada | Rodar checks do script e `Test-Path` nos binários | Remover output e rodar `.\scripts\publish-windows.ps1` novamente. |
| Permissão negada na evidência | Arquivo bloqueado por Outlook/antivírus/sistema | Testar cópia local e permissões de leitura | Fechar aplicações que usam o arquivo e processar uma cópia. |
| Falha por caminho de trabalho incorreto | Caminhos relativos executados de outra pasta | Verificar `Get-Location`, caminhos no manifest e no comando | Usar caminhos absolutos para evidência, caso e exportação. |

## Arquivos úteis durante diagnóstico

| Arquivo | Quando consultar |
| --- | --- |
| `manifest.json` | Confirmar evidência, hash, operador, ações e warnings. |
| `audit.log` | Reconstruir eventos operacionais do caso. |
| `case.db` | Ver contagens, runs, issues e dados indexados. |
| `export-manifest.json` | Confirmar mensagens/anexos exportados e status por item. |
| `validation-report.json` | Entender falhas de validação. |
| `libpff-diagnostic.log` | Investigar execução experimental do `pffexport`. |
| `native-tool-diagnostic-report.json` | Ver disponibilidade de ferramenta externa diagnosticada. |
| `reader-repair-failed.json` | Investigar falha de reparo do DB após cancelamento/stall do worker. |

## Comandos úteis

Ver ajuda:

```powershell
dotnet run --project src/MailVault.Cli/MailVault.Cli.csproj -- --help
```

Ver estatísticas de um caso:

```powershell
dotnet run --project src/MailVault.Cli/MailVault.Cli.csproj -- stats ".\mailvault-cases\CASE-001"
```

Buscar mensagens:

```powershell
dotnet run --project src/MailVault.Cli/MailVault.Cli.csproj -- search ".\mailvault-cases\CASE-001" --query "invoice" --include-preview
```

Validar exportação:

```powershell
dotnet run --project src/MailVault.Cli/MailVault.Cli.csproj -- validate ".\mailvault-cases\CASE-001" --export-folder ".\exports\CASE-001-eml" --format eml --json --out ".\exports\CASE-001-validation"
```

## Quando recriar um caso

Recrie o caso quando:

- `case.db` não existe ou está estruturalmente inválido;
- a evidência original mudou e o hash não confere;
- o adapter usado originalmente estava ausente/incompleto;
- o workspace foi criado por versão antiga e não abre nem em modo limitado;
- a indexação foi interrompida antes de criar tabelas mínimas.

Preserve o caso antigo antes de recriar, especialmente `audit.log`, `manifest.json`, `case.db` e relatórios.
