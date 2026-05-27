# Ferramentas Externas

MailVault Recovery usa bibliotecas e ferramentas externas em pontos bem delimitados. Este documento separa o que está implementado do que ainda é objetivo técnico.

## Resumo

| Ferramenta | Uso atual | Status |
| --- | --- | --- |
| `XstReader.Api` | Leitura PST/OST pelo adapter `MailVault.Adapters.XstReader` | Implementado. |
| `pffexport` / libpff | Motor externo experimental `LibpffExternal` e diagnóstico | Parcial/experimental. |
| `readpst` | Diagnóstico no Test Lab | Apenas detecção, sem pipeline de ingestão. |

## XstReader.Api

`MailVault.Adapters.XstReader` implementa `IMailStoreReader`, `ISessionAwareMailStoreReader`, `IExtractionIssueSource` e `IMetadataOnlyAware`.

Comportamento real:

- suporta `.pst` e `.ost`;
- é carregado dinamicamente por `ReflectionAdapterResolver`;
- inspeciona store, enumera pastas, mensagens e anexos;
- pode operar em modo metadata-only;
- registra issues para falhas parciais de leitura;
- evita vazar tipos do XstReader para Core/Domain.

Arquivos relevantes:

- `src/MailVault.Adapters.XstReader/`
- `src/MailVault.Core/ReflectionAdapterResolver.cs`
- `src/MailVault.Indexing/ReaderEngineStrategy.cs`

## pffexport/libpff

### Estado atual

O código contém duas integrações relacionadas ao libpff:

1. **Detector de ferramenta externa** em `ExternalToolDetector`.
2. **Motor experimental** `LibpffExternalEngine`.

O projeto `MailVault.Adapters.Libpff` existe e é copiado para o output, mas contém apenas uma classe vazia. Ele não implementa `IMailStoreReader`.

### Onde o pffexport é chamado

`LibpffExternalEngine.IndexAsync` detecta `pffexport` e executa:

```text
pffexport -o "<case-folder>\libpff-recovery" "<arquivo-pst-ou-ost>"
```

Após a execução:

- cria ou usa a pasta `libpff-recovery`;
- grava `libpff-diagnostic.log` com stdout, stderr e exit code;
- registra uma issue `MV-INFO-LIBPFF` ou `MV-ERR-LIBPFF` no `case.db`;
- retorna `Success` se o exit code for `0`;
- retorna `Failed` se o exit code for diferente de `0`.

> [!WARNING]
> Mesmo quando retorna `Success`, o motor atual registra `FoldersIndexed=1`, `MessagesIndexed=0`, `AttachmentsIndexed=0`. A saída do `pffexport` ainda não é parseada para preencher o índice.

### Como o caminho é resolvido

O detector procura o executável em:

1. `PATH`.
2. `AppContext.BaseDirectory`.
3. Diretório corrente.
4. `C:\Program Files\libpff`.
5. `C:\Program Files (x86)\libpff`.
6. `C:\libpff`.

No Windows, o nome procurado é `pffexport.exe`. Em outros sistemas, `pffexport`.

O detector tenta consultar versão com `-V` e timeout de 2 segundos.

### Fallback e erros

| Cenário | Comportamento |
| --- | --- |
| `pffexport` ausente | `LibpffExternalEngine` lança erro informando que o utilitário não está disponível. |
| `pffexport` presente, exit code `0` | Log diagnóstico e issue informativa; sem mensagens indexadas. |
| `pffexport` presente, exit code diferente de `0` | Log diagnóstico, issue warning e resultado `Failed`. |
| Timeout na execução | O processo é morto e exit code `-99` é retornado pelo helper. |
| Publish padrão | `pffexport.exe` não é copiado nem validado. |

## readpst

`readpst` é detectado pelo Test Lab para diagnóstico de ambiente e aviso de licença. O código atual não implementa pipeline de recuperação/indexação usando `readpst`.

## Plug and play esperado

O comportamento desejável para um produto publicado é:

```mermaid
flowchart LR
    A["Build/Publish"] --> B["Copiar Desktop + CLI"]
    B --> C["Copiar adapters .NET"]
    C --> D["Copiar ferramentas externas permitidas"]
    D --> E["Validar versões e licenças"]
    E --> F["Rodar smoke tests"]
    F --> G["Usuário executa sem downloads manuais"]
```

Estado atual:

- Desktop + CLI: implementado.
- XstReader adapter e `XstReader.Api.dll`: implementados e validados no script.
- `pffexport/libpff`: não empacotado.
- Licença/redistribuição do libpff: não formalizada no repositório.

## Próxima melhoria técnica recomendada

1. Criar pasta controlada para binários externos, por exemplo `tools/libpff/win-x64/`, se a licença permitir.
2. Incluir arquivo de licença e origem dos binários.
3. Atualizar `scripts/publish-windows.ps1` para copiar `pffexport.exe` e dependências nativas.
4. Validar `pffexport.exe -V` no publish.
5. Adicionar testes para ferramenta ausente/presente/timeout/exit code.
6. Implementar conversão da saída do `pffexport` para `case.db`.
7. Expor no Desktop um diagnóstico claro do motor selecionado e de suas limitações.
