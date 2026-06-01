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

## 14. Milestone 2 — Corpus real + corrupção controlada `[TESTE]`

Objetivo: **medir onde o motor estrutural atual quebra**, antes de Deep Scan/Carving. Corpus gerado
sempre a partir de **cópias** (`scripts/make-corrupted-corpus.ps1`), original preservado, manifesto com
SHA-256. Recuperação rodada contra todos os cenários (`scripts/run-corpus-recovery.ps1`, bounded
`--max-messages 150`). Detalhe operacional em [CORPUS_TESTING.md](CORPUS_TESTING.md).

**Endurecimento entregue:** `BeginReadSessionAsync` movido para dentro do try do `RecoveryExportRunner`
⇒ arquivo que não abre agora gera **relatório com status=Failed** (falha controlada), em vez de exceção crua.

### 14.1 Resultados (OST real de 90 MB, 11 cenários, seed de corrupção fixo ⇒ reprodutível)

| Categoria | Cenário | Status | Exportadas | Erro principal (XstReader) | Limite? |
|---|---|---|---:|---|---|
| healthy | healthy-copy | PartialCompleted¹ | 150 | — | não |
| truncated | 10% | PartialCompleted¹ | 150 | — | não |
| truncated | 30% | **Failed** | 0 | `Node block does not exist` | **sim** |
| truncated | 60% | **Failed** | 0 | `Node block does not exist` | **sim** |
| header-damaged | header-zeroed (512B) | **Failed** | 0 | `magic cookie is missing` | **sim** |
| header-damaged | magic-broken (!BDN) | **Failed** | 0 | `magic cookie is missing` | **sim** |
| middle-damaged | middle-blocks | Completed | 142 | (glitch de anexo: `stream not expandable`) | parcial |
| corrupted | random-bytes | Completed | 40 | — (sub-recuperação silenciosa²) | parcial |
| edge-cases | partial-copy 40% | Failed | 0 | `Node block does not exist` | — |
| edge-cases | empty (0B) | Failed | 0 | `magic cookie is missing` | — |
| edge-cases | tiny (8B) | Failed | 0 | `Unrecognised header type` | — |

Resumo: **11 cenários · 4 recuperaram algo · 7 falha-ao-abrir · 11 falhas controladas · 0 crashes.**

¹ `PartialCompleted` aqui = atingiu o cap de `--max-messages 150`, **não** falha (0 falhas). O arquivo íntegro
exporta 4.139 (ver §13.5). Truncar só 10% da cauda não impede a leitura estrutural.

² **Achado importante (honesto):** em `corrupted` o motor reportou `Completed` com apenas **40** mensagens
(de 4.139 reais). A corrupção não gera "falhas" contadas — ela faz pastas/mensagens **não serem enumeradas**
(perda silenciosa). O leitor estrutural recupera o que consegue navegar e ignora o resto sem sinalizar.

### 14.2 Limites conhecidos do motor atual (entrada para o Milestone 3 — Deep Scan/Carving)
1. **Cabeçalho destruído** (!BDN ausente / primeiros bytes zerados) ⇒ não abre. Carving por assinatura
   (`IPM.Note`, propriedades MAPI) recuperaria, pois os blocos de dados posteriores seguem intactos.
2. **Truncamento severo** (≥30%) ⇒ `Node block does not exist`: a NBT/BBT referencia nós na cauda removida.
   Carving recuperaria as mensagens cujos blocos estão na fração salva.
3. **Sub-recuperação silenciosa** em corrupção de blocos/bytes: o motor não quantifica o que perdeu.
   Deep Scan + reconciliação por contagem física daria visibilidade real.

Conclusão do milestone: o motor estrutural é **robusto a danos leves** (truncamento ≤10%, blocos no miolo)
e **falha de forma controlada e auditável** em danos severos — sem nunca crashar. Os 3 limites acima são o
escopo objetivo do Deep Scan/Carving.

## 15. Milestone 3 — Probe de viabilidade do libpff `[TESTE]` (de-risk R1)

Antes de implementar Deep Scan, probe empírico do `pffexport`/`pffinfo` vendorizado (build 20260526)
contra o mesmo corpus, para decidir a rota. **Comparação direta libpff × XstReader:**

| Cenário | XstReader | libpff (`pffinfo`/`pffexport`) | libpff ajuda? |
|---|---|---|---|
| healthy (wVer 36) | abre, 4.139 | **abre; `pffexport` extraiu 5.466 arquivos em 75s** | sim (extrai) |
| corrupted / middle-damaged | abre (parcial/silencioso) | **abre** (header íntegro) | **sim** (carving de apagados/órfãos) |
| header-zeroed / magic-broken | Failed | **Failed** — `invalid file signature` (mesmo `-m recovered`) | **não** |
| truncated 30%/60% | Failed | **Failed** — `index node at offset 0x534d000` (mesmo `-m recovered`) | **não** |

**Achados decisivos:**
1. ✅ A nota "libpff falha em OSTs modernos" está **desatualizada**: o build atual lê `64-bit with 4k page`
   (wVer 36) e **extrai conteúdo real** (5.466 arquivos do saudável em 75s — rápido, em C).
2. ❌ O modo carving `-m recovered` **NÃO contorna** cabeçalho destruído nem truncamento — falha nos
   **mesmos offsets** que o XstReader. libpff "recovery" resgata itens **apagados de um arquivo que abre**;
   não faz carving bruto de arquivo que não abre.

**Reorientação do escopo (baseada em dados):**
- **Categoria #3 (arquivo abre, perda silenciosa)** → libpff **agrega valor** (resgata apagados/órfãos +
  tolerância a falha por item). É o alvo do `--deep-scan` (opt-in + auto-fallback). **Esforço médio.**
- **Categorias #1 (cabeçalho) e #2 (truncamento)** → libpff **não resolve**. Exigem **carving por assinatura
  bruta** (Route C, carver C# próprio: scan de `IPM.Note`/`PR_*`, decode de bloco). **Esforço muito alto** —
  agora justificado por dados (libpff descartado para esses casos), não por suposição.

### 15.1 Implementação entregue (Fase 3a) `[TESTE]`
- **Detector de sub-recuperação silenciosa** (`RecoveryExportRunner`): soma `ContentCount` esperado por pasta,
  compara com exportado; se cobertura < 90% em run **não-limitado**, emite `MV-WARN-REC-UNDER-RECOVERY`,
  marca `PartialCompleted` e recomenda `--deep-scan`. Métricas `ExpectedMessages`/`CoveragePercent` no relatório.
  Fica **fora do hot loop** (acúmulo por pasta + checagem pós-run) → sem impacto no caminho rápido.
- **`PffDeepScanRunner`** (`MailVault.Indexing`): roda `pffexport -m all` (allocated + orphan + recovered) em
  **processo separado**, com timeout; distingue honestamente `OpenFailed` × `Extracted`/`PartialExtracted`.
- **CLI `recover-eml/recover-mbox --deep-scan`**: **opt-in** + **auto-fallback** apenas quando o estrutural
  `Failed`/0. Grava `_mailvault-deepscan-report.json`. `ExternalToolDetector` agora acha o libpff vendorizado
  em dev (`MAILVAULT_LIBPFF_DIR` + walk-up por `vendor/native-tools/win-x64/libpff`).

### 15.2 Validação em runtime `[TESTE]`
- Suíte: **196 aprovados / 1** (falha pré-existente do Desktop worker-launch, dependente de ambiente). +4 testes do detector.
- `recover-eml` header-zeroed → estrutural `Failed` → auto-fallback Deep Scan → `pffexport` **OpenFailed**
  (`Error opening file`) → relatório honesto + NOTA "exige carving". (libpff também não abre — confirma §15.)
- `recover-eml --deep-scan` corrupted/random-bytes → estrutural 20 (cap) → Deep Scan **PartialExtracted: 189
  arquivos, 695 KB, 376 ms** (com warnings de checksum deflate; tolerante a falha). Categoria #3 = valor real.
- **Performance preservada**: healthy **sem** `--deep-scan` NÃO invoca Deep Scan; benchmark 80 msgs `getMsg=0ms`,
  ~7 msg/s (igual ao pré-M3). O fast path não foi tocado.

### 15.3 Limitação de ambiente observada (não-código)
Durante o desenvolvimento, o **Smart App Control (enforce)** do Windows passou a bloquear DLLs recém-compiladas
não-assinadas (`0x800711C7`), impedindo build/test até ser desativado. Não é defeito do projeto; afeta o ciclo
de dev de qualquer binário .NET não-assinado. Para distribuição comercial, **assinar os binários** evita isso.

### 15.4 Sweep comparativo Estrutural × Deep Scan `[TESTE]` (decisão sobre Fase 3b)
`run-corpus-recovery.ps1 -DeepScan` rodou estrutural + libpff em pasta isolada (`_deepscan/`, sem mistura, sem
converter p/ MailItem) em todo o corpus. **Mensagens-equivalentes do libpff = contagem de `OutlookHeaders.txt`**
(1 por item; o `pffexport` emite vários arquivos por mensagem, então "arquivos" engana — o que importa é msgs).

| Cenário | XstReader (msgs) | libpff (msgs) | Valor |
|---|---:|---:|---|
| middle-damaged | 142 | **142** | **NoValue** (mesmo conjunto) |
| corrupted (random) | 40 | **40** | **NoValue** (mesmo conjunto) |
| header-zeroed / magic-broken | 0 (Failed) | 0 (OpenFailed) | **SameFailureAsStructured** → carver C# |
| truncated 30% / 60% | 0 (Failed) | 0 (OpenFailed) | **SameFailureAsStructured** → carver C# |
| edge (partial/empty/tiny) | 0 | 0 | **SameFailureAsStructured** |
| healthy / truncated-10% | 150 (cap)¹ | 4139 | Inconclusive¹ |

Contagem: **AddsValue=0 · NoValue=2 · SameFailureAsStructured=7 · Inconclusive=2.**

¹ Inconclusive = artefato do `--max-messages 150` no estrutural; libpff rodou sem cap de mensagens e pegou 4139.
**Não** indica libpff superior: o XstReader sem limite também faz 4139 (provado no §13.5, 19,47 min). Onde o
estrutural **abre**, libpff recupera o **mesmo conjunto de mensagens** — nunca mais.

**Decisão (critério do milestone): NÃO avançar para a Fase 3b (`PffExportParser`).**
- **AddsValue = 0**: em nenhum cenário o libpff recuperou mensagens que o estrutural não pegasse.
- Em arquivos que abrem (middle/corrupted), libpff = estrutural (142=142, 40=40) → integrar a saída do libpff
  no pipeline canônico **não traria ganho de recuperação**, só custo de parser/dedup.
- Em cabeçalho destruído / truncamento severo, libpff **falha igual** ao estrutural → **só carving por
  assinatura bruta (Route C, carver C#) resolve** — agora confirmado por dados.
- **libpff permanece como diagnóstico/fallback honesto** (`--deep-scan`), não como fonte de recuperação incremental.

**Próxima rota técnica:** planejar o **carver C# (Route C)** para `header destruído` (7 cenários
`NeedsCSharpCarver`) e `truncamento ≥30%`. Observação lateral (não decisiva): o `pffexport` extraiu 4139 itens
do saudável em ≤3 min (vs 19 min do XstReader) — sinal de **velocidade** de I/O, porém sem ganho de recuperação
e em formato não-canônico; só relevante se algum dia o gargalo de I/O justificar, exigindo o parser de qualquer modo.

## 16. Milestone 3c.1 — Carver C# (Raw Artifact Scanner, somente-relatório) `[TESTE]`

Projeto novo **`src/MailVault.Carving/`** (referencia só o Core; **isolado** do motor estrutural → fast path
intocado). `RawArtifactScanner`: leitura **read-only**, **streaming em chunks com overlap**, busca por bytes
(`IPM.Note` ASCII + UTF-16LE) — **sem regex global, sem carregar o arquivo todo**. **SOMENTE-RELATÓRIO**: lista
candidatos físicos (offset/encoding/confiança/preview) em `_mailvault-carving-report.json/.md`; **não exporta EML**
⇒ impossível gerar recuperação falsa nesta fase. Limites: `--max-scan-bytes`/`--max-candidates`/
`--max-candidates-per-mb`/`--chunk-size`/`--overlap-size`/`--max-preview-bytes`/`--timeout`/`--no-previews`.
Comando dedicado `carve <file> --out <dir>` (carver **nunca** roda no `recover-eml` padrão).

### 16.1 Prova de viabilidade `[TESTE]` (corpus — cenários onde estrutural+libpff = 0)
| Cenário | XstReader | libpff | **Carver 3c.1** |
|---|---:|---:|---:|
| header-zeroed (512B zerados) | 0 | 0 | **121 candidatos IPM.Note** |
| magic-broken (!BDN) | 0 | 0 | **121** |
| truncated-30% | 0 | 0 | **79** |
| truncated-60% | 0 | 0 | **14** (menos arquivo salvo → menos sinais) |

→ O carver encontra **sinais físicos de mensagem exatamente onde XstReader e libpff falham ao abrir**, sem crash,
em centésimos de segundo nos truncados. **Viabilidade do carver C# comprovada.** Abre header-destruído sem crash;
streaming O(chunk); fast path do `recover-eml` confirmado intacto (getMsg=0, ~6 msg/s, nenhuma invocação de carving).

### 16.2 Testes `[TESTE]`
`MailVault.Carving.Tests` (**7/7**), com buffers sintéticos: acha `IPM.Note` UTF-16LE/ASCII; 0 sem sinal;
**assinatura na fronteira de chunk é achada exatamente 1×** (valida overlap sem recontagem); respeita
`--max-scan-bytes`; varre **arquivo sem header** (!BDN ausente); `--no-previews` omite preview.
Suíte total: **203 aprovados / 1** (Desktop worker-launch, flaky de ambiente, pré-existente).

### 16.3 Próximos submilestones (gate por etapa, não iniciados)
3c.2 clustering de candidatos · 3c.3 builder de EML **parcial** (headers sintéticos, pasta `Partial/`) ·
3c.4 `Orphaned Items` + dedup · 3c.5 integração `recover-eml --carve` · 3c.6 benchmark/precisão no corpus.
O gate de viabilidade (3c.1) passou ⇒ seguiu-se para 3c.2/3c.3 (ver §16.4).

### 16.4 Classificação + builder de EML parcial (3c.2/3c.3) — achado honesto `[TESTE]`
Implementados: `CarveFieldExtractor` (extrai assunto/email/data/corpo da janela física + classifica
Mail/Orphan/System/LocateOnly com score 0–100 + denylist de itens de sistema), `CarvedMessageBuilder`
(EML **parcial** com headers sintéticos `X-MailVault-*` + corpo "NÃO É CÓPIA FIEL", pasta `Recovered/Carved/Partial`
ou `Orphaned Items`), `RawPffCarver` (orquestra scan→classifica→export). Export é **opt-in** (`--export`,
`--min-confidence`), default **report-only**. Testes 11/11 (e-mail sintético real → Mail+export; system → System;
marker-only → LocateOnly; report-only não exporta).

**Régua funciona; o sinal `IPM.Note` é que NÃO basta — medido no corpus:**
1. **Recall ~3%**: o healthy (4.139 msgs) tem só **121** `IPM.Note` UTF-16LE → o formato **deduplica** a string de
   classe (não a grava por mensagem). A assinatura localiza uma fração mínima das mensagens.
2. **Precisão da camada Mail ≈ 0 no corpus**: dos 121, **118 = System** (itens internos do OST) e os **3 "Mail"
   são falsos positivos** (fragmentos de pasta + endereço do próprio dono da conta), não correspondência real.

**Decisão (critério de parada da régua acionado):** o carver por `IPM.Note` **não entrega recuperação útil** neste
corpus (recall baixo + Mail = falso positivo). Mantém-se **report-only/diagnóstico por padrão**; `--export` fica
**opt-in e experimental** (EMLs fortemente disclaimerados, nunca apresentados como completos). **Não** ligar export
por padrão. O caminho para recuperação real de mensagens em arquivo com cabeçalho/índice destruído seria um
**parser de bloco/heap MS-PST** (decodificar PC/HN para extrair propriedades por mensagem) — esforço muito alto,
ROI incerto (depende dos blocos intactos na fração salva), a decidir como milestone próprio antes de investir.

### 16.5 Gate 0 de viabilidade do parser de bloco `[TESTE]` (grátis, antes de qualquer parser)
Pergunta: o **conteúdo real** (assunto/corpo) existe como **texto UTF-16LE em claro** no OST bruto (o que um carver
de scan conseguiria pegar)? Método: 30 assuntos reais (ground truth do XstReader) + palavras distintivas, procurados
nos bytes crus do healthy (Encryption=none).

| Categoria | Presente em claro (UTF-16LE)? |
|---|---|
| Assunto/corpo (Steam, Discord, Reserva, Movida, Wacom, Confirmação…) | **0 / 30 — NÃO** |
| Metadados (Borges 1046×, BitMart 6×, Renata 18×, querebola 15381×, nomes de pasta) | **sim** |

**Verdito (Gate 0 FALHA para conteúdo):** o scan de texto **funciona** (acha o que está em claro), mas
**assunto/corpo NÃO estão em claro** — só metadados. O conteúdo vive na **heap estruturada/comprimida** que XstReader
e pffexport **decodificam**, mas scan não alcança. Implicações:
1. O carver de assinatura/texto (3c.1–3c.3) é **confirmadamente locate/diagnóstico** — não recupera conteúdo.
2. Recuperar conteúdo de arquivo com cabeçalho/índice destruído exige **decode completo de heap/PC + provável
   descompressão**, e **localizar blocos válidos sem o índice NBT/BBT** — ou seja, reimplementar o núcleo do
   XstReader/libpff porém SEM o índice. Esforço enorme, ROI ruim (e blocos podem estar fisicamente perdidos).
3. **Recomendação:** NÃO investir no parser de bloco como rota de carving. Carving permanece diagnóstico; a
   recuperação real continua sendo estrutural (XstReader, arquivos que abrem) + libpff fallback. Posição honesta:
   conteúdo de arquivos com cabeçalho/índice destruído é **largamente irrecuperável** sem reconstrução de bloco de
   nível comercial (grande projeto à parte, fora do escopo atual).
