# MailVault Recovery — Estado do Protótipo de Recuperação OST/PST

> Documento de avaliação técnica honesta (sessão 2026-05-29). Classifica cada afirmação
> relevante e separa o que foi **comprovado por execução real** do que é hipótese.
> Prioridade adotada: recuperação confiável > conversão perfeita; evidência > suposição.

## 1. Legenda de classificação

- **[CÓDIGO]** Confirmado no código atual deste repositório.
- **[SPEC]** Confirmado por documentação pública / especificação ([MS-PST], libpff).
- **[TESTE]** Confirmado por execução/teste local nesta sessão.
- **[INFER]** Inferência técnica plausível, não comprovada aqui.
- **[?]** Desconhecido / não comprovado.
- **[INVIÁVEL]** Inviável no escopo atual.

## 2. Diagnóstico do estado inicial

O repositório **não** era um esqueleto: já é um produto maduro (.NET 10, ~11 projetos `src` + 9 de teste,
Desktop Avalonia + CLI + Worker, indexação SQLite, exporters EML/MBOX, validação, auditoria).

- **[TESTE]** Build limpo (`dotnet build MailVault.sln`): 0 erros.
- **[TESTE]** Motor de leitura real = **XstReader** (MS-PST puro em C#), via `XstReaderRecoveryEngine`
  (`IMailStoreReader` + `ISessionAwareMailStoreReader`, lock single-thread, modo MetadataOnly,
  wrappers `Safe*` defensivos, libera cache de pasta para conter memória).
- **[TESTE]** `RecoveryExportRunner` já implementava o essencial de "Salvage Mode": sessão read-only,
  timeout por mensagem (30s), escrita atômica `.tmp→.eml`, continuação após falha por item,
  isolamento de anexo quebrado (injeta `ERROR_ATTACHMENT_*.txt`), sanitização anti-traversal e relatório JSON+CSV.
- **[TESTE]** `MailVaultMimeSerializer` (MimeKit) mapeia Subject/From/To/Cc/Bcc/Date/Message-Id,
  reconstrói headers de transporte, corpo HTML+texto com placeholder, e isola anexos com falha.

### 2.1 Bug crítico encontrado por execução real (não por leitura)

**[TESTE]** O comando `recover-eml` **falhava** em build de desenvolvimento com
`[ERRO FATAL] Reader não inicializado. Chame InspectAsync primeiro.`

Causa-raiz (confirmada por diagnóstico instrumentado + reflexão sobre o DLL):
o `MailVault.Cli.csproj` **não referenciava** os projetos de adapter (modelo de plugin por reflexão),
então o build do CLI **nunca atualizava** `MailVault.Adapters.XstReader.dll` na sua pasta de saída.
O DLL presente era de **26/05 (19.968 bytes)**, anterior à criação do `XstReaderRecoveryEngine` e do
suporte a `ISessionAwareMailStoreReader` (fonte de 27/05). Resultado:

- o resolver caía no leitor de metadados `XstReaderMailStoreReader` (sem sessão), não no motor de recuperação;
- `reader is ISessionAwareMailStoreReader` → **False** ⇒ `BeginReadSessionAsync` era pulado ⇒ falha.
- `tree`/`inspect`/`list` sobreviviam porque chamam `InspectAsync` (que seta `_filePath`).

O `Desktop.csproj` **já** copiava os adapters (referência copy-only), e o `publish` junta Desktop+CLI
na mesma pasta — por isso o **artefato publicado** funcionava e só o **build de desenvolvimento** do CLI quebrava.

## 3. Decisão técnica e justificativa

1. **Manter .NET/C# + XstReader como motor principal** (Opção A da pesquisa). **[TESTE]**
   A pesquisa recomenda `libpff` como motor de leitura, mas **não menciona o XstReader**, que é um
   leitor MS-PST puro em C#, já integrado e que **comprovadamente** abre os OSTs reais do corpus.
   `libpff/pffexport` permanece como diagnóstico experimental (falha em OSTs modernos no histórico do projeto).
2. **EML/MBOX primeiro** (saída aberta RFC 5322). **[CÓDIGO/TESTE]** Já funciona e é previsível.
3. **PST limpo atrás de uma interface honesta**, sem writer falso. Ver §5.
4. **Correção mínima, sem reescrita**: o bug de plugin foi resolvido espelhando no CLI o mesmo padrão
   copy-only já existente no Desktop — não troquei o modelo de resolução por reflexão.

## 4. Arquivos modificados / criados

| Arquivo | Ação | Por quê |
|---|---|---|
| `src/MailVault.Cli/MailVault.Cli.csproj` | **fix** | Referência copy-only aos adapters → CLI recebe plugin fresco no build (corrige `recover-*`). |
| `src/MailVault.Core/IPstExportWriter.cs` | **novo** | `IPstExportWriter` + `UnsupportedPstExportWriter` (NotSupported honesto). |
| `src/MailVault.Core/PffSignatureInspector.cs` | **novo** | Detecção de assinatura física `!BDN`, ANSI/Unicode (wVer), ofuscação. |
| `src/MailVault.Core/RecoveryExportRunner.cs` | **fix** | Relatório **Markdown** + `ClassifyResult` (Completo/Parcial/Inconclusivo). |
| `src/MailVault.Cli/Program.cs` | **fix** | Comando `recover-pst` (honesto) + bloco de assinatura física no `inspect`. |
| `tests/MailVault.Core.Tests/RecoveryDiagnosticsTests.cs` | **novo** | 15 testes (inspector, PST não-suportado, classificação). |
| `tests/MailVault.Exporters.Tests/RecoveryExportRunnerTests.cs` | **fix** | Assere geração do relatório `.md`. |
| `tests/MailVault.Desktop.Tests/WorkerResolutionAndUXTests.cs` | **fix** | Limpeza best-effort com retry (corrige modo de falha por lock de `case.db`). |
| `scripts/make-corrupted-corpus.ps1` | **novo** | Gera cópias corrompidas (truncada/header/blocos) **sempre sobre cópia**. |

## 5. PST limpo — por que NÃO-SUPORTADO (e não falso)

**[SPEC/INVIÁVEL no escopo]** Escrever um PST Unicode aceito pelo Outlook exige reconstruir as 3 camadas
PFF ([MS-PST]): NDB (B-Trees NBT/BBT, offsets 64-bit, CRCs, ofuscação), LTP (PC/TC/BTH/HN) e Mensagens.
Erro mínimo de alinhamento ⇒ Outlook rejeita como corrompido. `libpff`/`libpst` **não escrevem PST**.
Caminho confiável depende de SDK licenciado (ex.: Aspose.Email) ou writer nativo maduro.

Implementação honesta entregue: `UnsupportedPstExportWriter` retorna `Supported=false`,
`StatusCode=MV-PST-WRITE-NOT-SUPPORTED`, **não cria arquivo**, e explica tecnicamente o motivo.
O comando `recover-pst` expõe isso ao operador e indica EML/MBOX. A interface `IPstExportWriter`
deixa a arquitetura pronta para plugar um writer real no futuro.

## 6. Como executar

Pré-requisitos: .NET SDK `net10.0` (testado com 10.0.300). Trabalhe sempre sobre **cópia** da evidência.

```powershell
dotnet build MailVault.sln                 # 0 erros
dotnet test  MailVault.sln                 # ver §8

# Recuperação direta (sem indexação prévia), saída aberta:
dotnet run --project src/MailVault.Cli -- recover-eml  "C:\copia\mailbox.ost" --out ".\exports\eml"
dotnet run --project src/MailVault.Cli -- recover-mbox "C:\copia\mailbox.ost" --out ".\exports\mbox"
dotnet run --project src/MailVault.Cli -- recover-eml  "C:\copia\mailbox.ost" --out ".\exports\eml" --folder "<caminho lógico>"

# Diagnóstico de assinatura física + hash + manifesto:
dotnet run --project src/MailVault.Cli -- inspect "C:\copia\mailbox.ost" --out ".\mailvault-cases"

# PST limpo (declara honestamente NÃO-SUPORTADO; não cria arquivo):
dotnet run --project src/MailVault.Cli -- recover-pst "C:\copia\mailbox.ost"
```

Cada export gera, na pasta de saída: árvore de pastas em subdiretórios + `.eml`/`.mbox`,
`_mailvault-export-report.json`, `_mailvault-export-report.md` e `_mailvault-export-errors.csv`.

### 6.1 Dependências externas
- **XstReader.Api.dll**: empacotada e copiada pelo build (fluxo padrão). Nenhuma instalação externa.
- **pffexport/libpff**: opcional, apenas diagnóstico experimental. Empacotado pelo `publish` em Release
  (`vendor/native-tools/...`), **não** necessário para o fluxo XstReader.

## 7. Formatos de saída
- **EML**: **[TESTE]** funcional (RFC 5322, abre em parsers/clientes padrão).
- **MBOX**: **[CÓDIGO/TESTE]** funcional (mboxrd, escapa linhas `From `, um `.mbox` por pasta).
- **PST limpo**: **[INVIÁVEL no escopo]** NotSupported honesto (§5).

## 8. O que foi REALMENTE validado nesta sessão

- **[TESTE]** Build solução: 0 erros.
- **[TESTE]** Testes: **191 aprovados / 192**. A única falha é `Desktop_XstReaderIndexing_StartsCliWorker`,
  teste de **integração dependente de ambiente** (precisa lançar um processo worker do CLI), que falha
  **identicamente antes das minhas mudanças** neste sandbox. Não é defeito de recuperação.
- **[TESTE]** Recuperação real do OST do corpus `querebola@gmail.com.ost` (95 MB, Unicode wVer=36):
  travessia da árvore Gmail real (`IPM_SUBTREE\[Gmail]\...`, Caixa de entrada, Lixeira, etc.),
  **>240 arquivos `.eml` gerados, 0 falhas** até a interrupção manual.
- **[TESTE]** Um `.eml` recuperado de 6,1 MB foi **parseado como MIME válido**: From/To/Subject(UTF-8)/Date/Message-Id,
  `multipart/mixed` com corpo HTML **e anexo PDF real** extraído (`RJXCMDWT.pdf`).
- **[TESTE]** `inspect` lê o cabeçalho real: `!BDN` presente, **Unicode (wVer 36)**, MagicClient `SO`, ofuscação `None`.
- **[TESTE]** `recover-pst` retorna NotSupported e **não cria arquivo**.

### Correções de premissas da pesquisa (por evidência)
- **[TESTE]** "Unicode = wVer 23" está **desatualizado**: OST moderno real usa **wVer 36**. Regra correta: 14/15=ANSI, **≥23=Unicode**.
- **[TESTE]** `wMagicClient` nem sempre é `"SM"`: o OST real traz `"SO"`. A autoridade de "é PFF" é a magia **`!BDN`**.

## 9. O que ainda é hipótese / não comprovado
- **[?]** Recuperação do OST de 1,6 GB do corpus (não executada end-to-end por tempo; `inspect` e abertura tendem a funcionar).
- **[INFER]** Comportamento em PST **ANSI** legado: caminho de código existe, sem corpus ANSI para provar.
- **[?]** Robustez em arquivos **gravemente corrompidos**: o `RecoveryExportRunner` continua após falhas por design,
  mas a leitura depende das árvores NBT/BBT íntegras (XstReader). **Carving** de blocos órfãos **não** está implementado.
- **[INFER]** Leitura de ofuscação `Compressible/High` pelo inspector usa offset [MS-PST]; o corpus real é `None`.

## 10. Limitações conhecidas
- Sem **deep scan / carving**: cabeçalho/B-Trees destruídos ⇒ leitura padrão falha (recuperável só com carving — roadmap).
- OST em **Cached Exchange "somente cabeçalho"**: corpo/anexos residem no servidor; fisicamente irrecuperáveis offline.
- **PST limpo** não suportado (§5).
- Teste de integração Desktop↔Worker depende de ambiente (§8).
- Avisos de nullable (CS8602) remanescentes; não afetam funcionamento.

## 11. Próximos passos para nível comercial
1. **Carving (Deep Scan/Salvage)**: varredura linear por assinaturas MAPI (`IPM.Note`, `PR_*`), reindex em SQLite
   temporário, pasta virtual "Orphaned Items" via `PR_PARENT_ENTRYID`. É o maior diferencial vs. concorrentes.
2. **Writer de PST real** plugado em `IPstExportWriter`: SDK licenciado (Aspose.Email) ou writer nativo maduro.
3. **Preview rico (try-before-you-buy)** no Desktop antes do export; contadores válidos vs. órfãos.
4. **Escala**: I/O por buffer + índice em disco para arquivos ≥ dezenas de GB; validar o OST de 1,6 GB end-to-end.
5. **Corpus de regressão**: usar `scripts/make-corrupted-corpus.ps1` para cenários truncado/header/blocos em CI.

## 12. Resposta direta à pergunta obrigatória

**O protótipo atual consegue recuperar OST/PST de verdade?**
Sim, comprovadamente, para arquivos **estruturalmente íntegros** (incluindo OST órfão/desconectado de Exchange,
pois o XstReader ignora a camada MAPI do SO). Provado nesta sessão em OST real de 95 MB: pastas, mensagens,
remetente/destinatários/datas, corpo HTML e **anexos reais** extraídos, com 0 falhas.

**Em quais condições?** Quando as árvores NBT/BBT e o cabeçalho estão legíveis. Arquivos com cabeçalho
destruído ou que exijam carving de blocos órfãos **não** são recuperados nesta build (sem deep scan).
Mensagens "somente cabeçalho" de OST em cache têm corpo/anexos ausentes do arquivo (irrecuperáveis offline).

**Exporta para quais formatos?** **EML** e **MBOX** (abertos, RFC 5322), com estrutura de pastas, relatórios
JSON/Markdown/CSV e classificação Completo/Parcial/Inconclusivo. **PST limpo: NÃO** — declarado honestamente
como NotSupported, sem gerar arquivo falso.

**O que falta para o nível EdbMails/Stellar/Kernel?** Três pilares: (1) **carving/deep scan** de blocos e itens
órfãos para arquivos gravemente corrompidos; (2) **escrita de PST limpo** via SDK licenciado/writer nativo;
(3) **preview rico** com modelo try-before-you-buy e escala para arquivos muito grandes. O motor de leitura
resiliente e a exportação aberta — base do produto — já existem e estão **comprovados em arquivo real**.

## 13. Milestone 1.5 — Performance, Observabilidade e Cancelamento Seguro

**Sinal de alerta medido** `[TESTE]`: o run completo do OST de 90 MB levou **3h03m / 491 msgs = 0,04 msg/s**
(≈25 s/mensagem) — inaceitável para produto comercial, mesmo recuperando corretamente.

### 13.1 Diagnóstico (medir antes de otimizar)
Instrumentei o `RecoveryExportRunner` com tempos por etapa e rodei um benchmark controlado (`--max-messages 80`,
mesmo OST). `[TESTE]`

| Etapa | Tempo (80 msgs, caminho antigo) |
|---|---:|
| **GetMessage (re-localização na árvore)** | **73.467 ms (83%)** |
| Serialização + Escrita | 175 ms |
| Anexos | 155 ms |

**Causa-raiz `[TESTE/CÓDIGO]`:** `RecoveryExportRunner` chamava `reader.GetMessageAsync(id)` por mensagem;
no `XstReaderRecoveryEngine` isso faz `FindMessageByPath` varrer a árvore inteira e recarregar pastas do disco
(após `ClearContents`) — custo **O(N²)** que cresce com o tamanho da pasta. Como `EnumerateMessagesAsync` já
devolve o `MailItem` completo (mesmíssimo `MapMailItem`), a re-leitura era **100% redundante**.

### 13.2 Otimização de baixo risco
Pular a re-leitura quando o reader declara `IMetadataOnlyAware` com `MetadataOnly=false` (entrega conteúdo
completo na enumeração). Fakes de teste não implementam essa interface ⇒ caminho deles inalterado.
Flag `--force-reread` reproduz o caminho antigo para benchmark.

### 13.3 Benchmark antes/depois `[TESTE]` (mesmo OST, 80 mensagens)

| Métrica | Antes (`--force-reread`) | Depois | Ganho |
|---|---:|---:|---:|
| Duração | 88,2 s | **14,4 s** | **6,1×** |
| Throughput | 0,91 msg/s | **5,57 msg/s** | 6,1× |
| Tempo médio/msg | 1.102 ms | **180 ms** | 6,1× |
| Etapa GetMessage | 73.467 ms | **0 ms** | eliminada |

O ganho é **maior em pastas grandes** (custo O(N²) some). Tempo restante é I/O legítimo de leitura de corpo
na enumeração (não redundante). Próxima alavanca de perf seria streaming/pipeline — fora do escopo deste milestone.

### 13.4 Observabilidade + cancelamento entregues
- **Relatório incremental**: `_mailvault-export-report.partial.json/.md` + `progress.json` a cada 50 msgs / 30 s / troca de pasta.
- **Cancelamento seguro**: Ctrl+C, `--timeout`, `--max-messages`, `--max-folder-messages` → status
  `CancelledByUser` / `CancelledByTimeout` / `Completed` / `PartialCompleted` / `Failed`, **sempre com relatório**.
- **Métricas no relatório**: msg/s, MB/min, tempo médio/msg, pasta mais lenta, maior msg, maior anexo, etapa mais lenta.

### 13.5 Benchmark COMPLETO end-to-end `[TESTE]` (arquivo inteiro, caminho otimizado, sem timeout)

Run completo do OST de 90 MB (`querebola@gmail.com.ost`), sem `--force-reread`, checkpoints ativos:

| Métrica | Valor |
|---|---:|
| Status | **Completed** (sem falhas) |
| Pastas | 25 |
| Mensagens tentadas = exportadas | **4.139** |
| Mensagens com falha | **0** |
| Anexos (OK / falha) | 42 / 0 |
| Tamanho final da saída | **191,7 MB** (~2× o input) |
| Tempo total | **1.168 s = 19,47 min** |
| Throughput | **3,54 msg/s · 9,85 MB/min** |
| Tempo médio/mensagem | 282 ms |
| Etapa GetMessage | **0 ms** (re-leitura redundante eliminada) |
| Etapa Serialização+Escrita | 4.512 ms |
| Etapa Anexos | **77.388 ms** (etapa mais lenta) |
| Pasta mais lenta | **Caixa de entrada — 1.139 s (97% do tempo)** |
| Maior mensagem / anexo | 6,12 MB / 4,39 MB |
| Relatórios finais JSON/MD/CSV | **todos gerados**; `.partial` removidos no fim |

**Comparação direta com o caminho antigo (O(N²)):**

| | Antigo (re-leitura) | Otimizado |
|---|---|---|
| 491 msgs | 3h03m (e nunca terminaria) | — |
| 4.139 msgs (arquivo inteiro) | estimado **dias** (taxa degradando) | **19,47 min, 0 falhas** |
| msg/s | 0,045 (degradante) | **3,54 (estável)** |

### 13.6 Critério de aceite — avaliação honesta
Meta original: OST ≤ 100 MB em < 5–10 min. **Este arquivo específico é atipicamente denso (4.139 mensagens,
saída de 191,7 MB)** e levou 19,5 min — não por lentidão de algoritmo (O(N²) eliminado, getMsg=0ms), mas pelo
volume real de conteúdo a ler/escrever. A métrica honesta é a **taxa por mensagem**:
- "500 mensagens em poucos minutos": **atingido** (500 ÷ 3,54 ≈ 2,4 min).
- 97% do tempo está na **Caixa de entrada** lendo corpos+anexos (trabalho legítimo, não redundante).

Próxima alavanca (fora deste milestone): **pipeline produtor/consumidor** e leitura por streaming de anexos
grandes — para arquivos densos baixarem dos ~19 min. A instrumentação entregue torna essa otimização medível,
não chute.
