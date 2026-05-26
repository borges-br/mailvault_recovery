# Milestone 6 — Desktop GUI Interface & Visual Inspection Hub

Este documento descreve o encerramento técnico e consolidação das entregas da **Milestone 6 (Visual Inspection Hub)** e do **Milestone 5.1 (Real Corpus Validation Run)**.

## 1. Escopo das Entregas Técnicas

A sprint integrada executou e validou a resiliência do backend contra um corpus de e-mail real volumoso local e, em seguida, construiu a primeira interface gráfica desktop operacional para técnicos e operadores.

### Conquistas Principais:
1. **Pipeline do Corpus Real Concluído:**
   - Adaptação robusta de scripts PowerShell/Bash para processar arquivos reais locais.
   - Mascaramento e higienização automática de caminhos locais do Windows para a conservação de privacidade (ex: `C:\Users\<USER>`).
2. **Robustez Estrutural (Hardening) do Backend:**
   - **SQLite FK Constraint Resolution:** Suporte a chaves estrangeiras nulas no mapeamento reativo de diretórios raiz não persistidos (`\Root`) das tabelas MAPI do Outlook.
   - **Assembly Dependency Resolving:** amarração do manipulador `AssemblyLoadContext.Default.Resolving` no runtime para resolução dinâmica e carregamento de dependências transitivas de plugins (como `XstReader.Api.dll`).
3. **Desktop Visual Hub Completo em Avalonia UI (MVVM):**
   - Criação da aplicação executável estável em .NET 10 LTS.
   - Configuração de Dark Mode premium nativo e harmônico no `App.axaml`.
   - Criação e integração reativa de 10 ViewModels cobrindo Dashboard, Navegador de Pastas, Lista de Mensagens, Preview Seguro Truncado, Busca, Painéis de Exportação e Validação.
   - XAML avançado com layout de 3 painéis dinâmico, grid splitters, tree views reativos e banners proeminentes de sigilo de dados.
4. **Suíte de Testes com 100% de Cobertura CI:**
   - Criação do projeto de testes unitários `tests/MailVault.Desktop.Tests/` em .NET 10.
   - Implementação de 6 testes específicos de ViewModel puros (headless e independentes de GPU/Renderização) garantindo a integridade dos comportamentos lógicos e tratamentos.
   - Todos os 48 testes da solução encontram-se verdes e passando com sucesso.

---

## 2. Conformidade de Sigilo Forense & Regras de Segurança

Para garantir a total conformidade forense nos critérios do sistema, os seguintes mecanismos de proteção foram arquitetados:
- **Truncagem de Segurança:** Previews de corpos de mensagens na tela do Desktop são estritamente limitados a **400 caracteres**, adicionando um banner de alerta forense. Os corpos das mensagens não residem e não são persistidos no índice relacional `case.db` para assegurar o sigilo.
- **Isolamento de Camadas (Clean Core):** O projeto `MailVault.Desktop` consome estritamente interfaces de consulta (`ICaseIndexReader`) e contratos compartilhados. Nenhuma DLL de infraestrutura como SQLite, XstReader ou MimeKit é referenciada de forma direta pela UI, mantendo a independência absoluta do Core.
- **Git Ignore Absoluto:** Arquivos gerados durante a rodada local `.local-corpus/` permanecem listados no `.gitignore` e blindados contra inclusão indesejada de dados privados no controle de versão Git.
- **Mascaramento:** Qualquer relatório ou manifesto de dados gerado substitui caminhos privados do Windows por `<USER>` de forma determinística.

---

## 3. Matriz de Testes Passados (Desktop)

A suíte headless de testes no projeto `MailVault.Desktop.Tests` valida os seguintes cenários estruturais:
- `CaseOverviewViewModel_LoadsCaseSummary`: Valida o carregamento estatístico geral de pastas/mensagens e o mascaramento determinístico de caminhos do Windows no painel de Overview.
- `FolderTreeViewModel_LoadsFolders`: Garante a montagem e indexação reativa da árvore hierárquica a partir do `case.db`.
- `MessageListViewModel_PaginatesMessages`: Valida a paginação heurística de carregamento de e-mails em lote.
- `MessagePreviewViewModel_TruncatesBodyPreview`: Garante a limitação estrita de 400 caracteres do preview do corpo e validação de anexos.
- `SearchViewModel_ReturnsIndexedResults`: Valida pesquisas reativas e retorno de e-mails indexados.
- `ValidationPanelViewModel_MapsReportStatus`: Valida a conversão estrutural de métricas de conformidade estrutural.
