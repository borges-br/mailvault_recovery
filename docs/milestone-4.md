# Relatório de Conformidade Técnica — Milestone 4: Export Engine

Este documento resume as implementações técnicas, conformidades arquiteturais e resultados de homologação da **Milestone 4 — Export Engine: EML, MBOX & Attachments** no MailVault Recovery.

## 1. Escopo de Entrega

A Milestone 4 introduziu a capacidade de exportação forense de alto desempenho da evidência digital preservada em formato Microsoft Outlook (`.pst`/`.ost`) para formatos padrão de preservação de e-mails de mercado.

### Componentes Entregues:
1. **Schema Relacional v2**: Atualização do banco relacional por caso (`case.db`) para persistir o nome e a versão do adaptador utilizado para extrair a evidência, mitigando incompatibilidades.
2. **Orquestrador de Exportação (`ExportJobRunner`)**: Motor centralizado no Core que executa a orquestração técnica, paginação, sanitização física de diretórios e geração do manifesto final.
3. **Módulo de Exportação EML (`MailVault.Exporters.Eml`)**: Implementação de alto desempenho baseada em streams com **MimeKit v4.16.0** para empacotar e-mails RFC 822 e anexos embutidos.
4. **Módulo de Exportação MBOX (`MailVault.Exporters.Mbox`)**: Implementação nativa sem dependências terceiras que concatena e-mails de forma incremental utilizando o robusto padrão de escape **mboxrd**.
5. **Integração CLI e Auditoria Forense**: Integração completa do comando `mailvault export` com auditoria rigorosa no arquivo `audit.log` por caso.

---

## 2. Gates e Controles de Qualidade Homologados

### Gate 1: Schema v2 e Metadados do Adaptador (Conforme)
O inicializador de banco de dados (`IndexSchemaInitializer`) migrou a estrutura padrão para a versão 2, criando a tabela `case_info` com suporte obrigatório às colunas `adapter_name` e `adapter_version`. Novos bancos criados nascem como schema 2.

### Gate 2: Validação de Hash Mandatória (Conforme)
A engine de exportação exige a presença do arquivo físico original de evidência e faz o recálculo do seu hash SHA-256 via streaming em disco em tempo de execução. O job de exportação é imediatamente interrompido se houver divergência em relação ao hash SHA-256 original cadastrado no manifesto do caso.

### Gate 3: Isolação Limpa de Exporters e Dependências (Conforme)
- A dependência do `MimeKit` reside estritamente em `MailVault.Exporters.Eml`. Nenhum tipo vaza para `MailVault.Core`, `MailVault.Domain` ou `MailVault.Cli`.
- O exportador MBOX não referencia o `MimeKit`, mas consome a abstração MIME em memória de forma limpa.
- Nenhuma referência ou tipo da biblioteca SQLite vaza para fora do projeto `MailVault.Indexing`.
- Nenhuma referência ou tipo do adaptador `XstReader` vaza para os exportadores.

### Gate 4: Streaming Seguro contra Exaustão de RAM (Conforme)
Em vez de carregar anexos volumosos na memória Heap, a engine expõe o método de streaming `OpenAttachmentStreamAsync` no `IMailStoreReader` e injeta `IAttachmentContentProvider` no exportador, transmitindo os bytes em blocos do arquivo original direto para o arquivo final no disco.

### Gate 5: Escaping mboxrd Robusto (Conforme)
A gravação incremental do formato MBOX escapa sequencialmente as linhas correspondentes ao padrão de envelope `From ` e sequências recursivas de escape (ex: `>From ` torna-se `>>From `), garantindo a reversibilidade forense perfecta e compatibilidade com leitores modernos.

### Gate 6: Sanitização Física contra Path Traversal (Conforme)
A engine implementa barreiras de proteção ativas contra Path Traversal:
- `SanitiseFilename`: Substitui sequências de retrocesso de diretório como `../` por sublinhados (`_`) de forma preventiva e alinhada com o `AttachmentNameNormalizer`.
- `EnsureSafeWritePath`: Valida se o caminho absoluto final resolvido reside rigorosamente dentro do diretório base de exportação homologado, lançando exceção técnica imediata em caso de tentativa de escrita externa.

### Gate 7: Minimização de Dados e Compliance Forense (Conforme)
- Nenhum corpo completo de e-mail ou dump MAPI binário é gravado no banco relacional `case.db`, nos logs de auditoria ou no `export-manifest.json`.
- O manifesto final da exportação registra apenas o mapeamento forense, contagens, status das mensagens, arquivos gerados e hashes relativos para auditoria de cadeia de custódia.

---

## 3. Cobertura de Testes Automatizados

A Milestone 4 foi homologada com a criação de **10 testes de unidade e integração detalhados** no projeto `MailVault.Exporters.Tests`, cobrindo todos os cenários exigidos:
1. `ExportCommand_DryRun_DoesNotWriteMessages`
2. `EmlExporter_WritesValidEmlFile`
3. `EmlExporter_IncludesAttachments`
4. `MboxExporter_WritesMboxWithEscapedFromLines`
5. `ExportJob_FiltersByFolder`
6. `ExportJob_RespectsLimitAndOffset`
7. `ExportManifest_RecordsCounts`
8. `ExportCommand_AbortsWhenSourceHashMismatch`
9. `AttachmentNameNormalizer_PreventsPathTraversal`
10. `Exporters_DoNotReferenceXstReaderTypes`

Todos os **32 testes automatizados** da solução estão passando com status 100% verde (build e execução limpa).

### Análise de Segurança Forense de Dependências:
```bash
dotnet list package --vulnerable --include-transitive
```
Status: **Nenhuma vulnerabilidade transitiva ou direta localizada** na solução.
