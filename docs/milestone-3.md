# MailVault Recovery — Relatório Técnico da Milestone 3

Este relatório consolida o status de conformidade da Milestone 3 (Session Indexing, Normalization & Search) e os gates de arquitetura definidos.

## Status do Projeto

- **Build**: `dotnet build` VERDE (100% de compilação sem alertas ou erros).
- **Testes Unitários**: `dotnet test` VERDE (22 testes unitários e de integração de CLI passando com sucesso).
- **Vulnerabilidades**: `dotnet list package --vulnerable --include-transitive` VERDE (0 vulnerabilidades transitivas ou diretas em todos os projetos da solution).

---

## Gates de Conformidade e Verificação Técnica

Abaixo estão descritos como cada um dos gates mandatórios definidos na Milestone 3 foi atendido e validado:

### 1. Microsoft.Data.Sqlite 10.x
* **Ação**: Adicionada a dependência estável `Microsoft.Data.Sqlite` na versão `10.0.8` (homologada no ecossistema do .NET 10 LTS) em [MailVault.Indexing.csproj](file:///c:/Github/mailvault_recovery/src/MailVault.Indexing/MailVault.Indexing.csproj).
* **Conformidade**: Atendido perfeitamente.

### 2. Ativação de Chaves Estrangeiras em Toda Conexão
* **Ação**: Toda conexão criada e manipulada pela persistência executa ativamente e em primeiro lugar a diretiva:
  `PRAGMA foreign_keys = ON;`
  Isso ocorre ativamente na inicialização física do store e no início de cada transação de escrita e blocos de leitura em [SqliteCaseIndexStore.cs](file:///c:/Github/mailvault_recovery/src/MailVault.Indexing/SqliteCaseIndexStore.cs), [SqliteCaseIndexReader.cs](file:///c:/Github/mailvault_recovery/src/MailVault.Indexing/SqliteCaseIndexReader.cs) e [SqliteCaseIndexWriter.cs](file:///c:/Github/mailvault_recovery/src/MailVault.Indexing/SqliteCaseIndexWriter.cs).
* **Conformidade**: Atendido perfeitamente.

### 3. Índices Mínimos do Schema v1
* **Ação**: O inicializador do banco em [IndexSchemaInitializer.cs](file:///c:/Github/mailvault_recovery/src/MailVault.Indexing/IndexSchemaInitializer.cs) cria e verifica os seguintes índices no schema:
  - `idx_messages_folder_id`
  - `idx_messages_subject`
  - `idx_messages_sender`
  - `idx_attachments_message_id`
  - `idx_issues_object_id`
* **Conformidade**: Atendido perfeitamente. A suite de testes valida a integridade e criação do schema com sucesso em `IndexSchemaInitializer_CreatesSchemaV1`.

### 4. Sanitização e Truncamento de `technical_details` em Issues
* **Ação**: Implementado o método robusto e profissional `SanitiseTechnicalDetails` em [SqliteCaseIndexWriter.cs](file:///c:/Github/mailvault_recovery/src/MailVault.Indexing/SqliteCaseIndexWriter.cs). Ele executa:
  - Mascaramento de caminhos absolutos privados do Windows contendo `C:\Users\username\...` substituindo por `C:\Users\<USER>`.
  - Mascaramento de endereços de e-mail na pilha técnica usando regex e trocando por `<email_masked>`.
  - Sumarização de dumps MAPI ou blocos longos de propriedades com mais de 200 caracteres.
  - Truncamento rígido do campo a no máximo 500 caracteres.
* **Conformidade**: Atendido perfeitamente. Não há vazamento de e-mails ou caminhos locais de operadores.

### 5. stats e search Localizados Exigindo case.db
* **Ação**: Os comandos `stats` e `search` da CLI no [Program.cs](file:///c:/Github/mailvault_recovery/src/MailVault.Cli/Program.cs) aceitam explicitamente a pasta de caso como argumento raiz e validam de forma restrita se o arquivo físico `./mailvault-cases/<case-id>/case.db` existe antes de qualquer tentativa de conexão.
* **Conformidade**: Atendido perfeitamente. Os testes de CLI `StatsCommand_WithCaseDb_PrintsSummary` e `SearchCommand_WithCaseDb_ReturnsMatches` atestam esse comportamento.

### 6. index --no-preview-cache
* **Ação**: Implementada a opção `--no-preview-cache` (do tipo `bool` na CLI) no System.CommandLine. Ela é mapeada como `!noPreviewCache` ao chamar `RunIndexAsync` no serviço de indexação.
* **Conformidade**: Atendido perfeitamente.

### 7. Pesquisa Limitada e sem Corpo Completo
* **Ação**: O comando `search` executa buscas apenas sobre metadados indexados (`messages.subject`, `messages.sender`, etc.) e o `body_preview` (que foi truncado de forma segura no pipeline e armazenado na coluna `body_preview` do banco). O corpo completo brutos nunca são expostos ou persistidos no banco.
* **Conformidade**: Atendido perfeitamente.

### 8. Organização Física no mailvault-cases/
* **Ação**: A pasta de saída padrão dos casos de uso na CLI foi definida como `./mailvault-cases/`, garantindo que o banco de dados do caso e os artefatos de auditoria fiquem isolados em:
  `./mailvault-cases/<case-id>/case.db`
  `./mailvault-cases/<case-id>/manifest.json`
  `./mailvault-cases/<case-id>/audit.log`
* **Conformidade**: Atendido perfeitamente.

### 9. .gitignore Atualizado
* **Ação**: Arquivos de bancos de dados locais temporários e transitórios do SQLite, bem como a pasta padrão de casos, foram estritamente ignorados em [.gitignore](file:///c:/Github/mailvault_recovery/.gitignore):
  ```
  *.db
  *.db-shm
  *.db-wal
  mailvault-cases/
  ```
* **Conformidade**: Atendido perfeitamente.

### 10. Clean Core e Isolação Estrita de Adapters
* **Ação**: O resolvedor dinâmico `IAdapterResolver` isola completamente qualquer tipo do SQLite do domínio (`MailVault.Domain`) e qualquer tipo do adapter do `XstReader` ou drivers externos de vazar para o Domain, Core, Audit ou CLI. A CLI carrega os assemblies em tempo de execução sem referências de projeto ou compilação diretas.
* **Conformidade**: Atendido perfeitamente.
