# Interface Gráfica Desktop & Visual Inspection Hub

Este documento descreve a arquitetura, o design e o manual de operação da interface gráfica desktop profissional do **MailVault Recovery**.

## 1. Arquitetura e Stack Tecnológica

O módulo visual do MailVault Recovery é construído sob a pasta `src/MailVault.Desktop/` utilizando as seguintes premissas técnicas:
- **Framework UI:** **Avalonia UI** (v11.0.10) estável, compatível com .NET 10 LTS.
- **Design Pattern:** **MVVM (Model-View-ViewModel)** reativo com suporte de bindings nativos da engine do Avalonia.
- **Princípio Clean Core (Isolamento):** A camada desktop (`MailVault.Desktop`) é estritamente uma camada de apresentação forense. Ela **não** faz referências diretas a SQLite, `XstReader`, `MimeKit` ou driver de bancos de dados físico. Todo o acesso a dados se dá através dos contratos abstratos de consulta (`ICaseIndexReader`) do Core/Domain.

---

## 2. Visão Geral dos Módulos Forenses (Telas)

### 1. Home / Landing Page
- Uma tela inicial limpa que possibilita ao operador especificar a pasta física do caso gerada no terminal.
- Validação rápida do arquivo `case.db` para garantir que o caso relacional existe e é íntegro antes de inicializar o dashboard.

### 2. Case Overview (Dashboard do Caso)
- Apresentação de metadados gerais do caso: ID do caso, adaptador forense utilizado com versão e caminhos de arquivos desidentificados.
- Inventário de dados consolidado direto do SQLite: contagem total de diretórios, mensagens, anexos e quantidade de anomalias/divergências registradas.
- Assinatura Hash SHA-256 do arquivo original para auditoria imediata da integridade de cadeia de custódia.

### 3. Folder Tree & Navegador de Correio
- Layout de 3 painéis integrado no estilo cliente de e-mail de alta performance:
  - **Painel Esquerdo (Árvore de Pastas):** Exibição hierárquica completa dos diretórios reais do arquivo de correio indexados no SQLite (`case.db`).
  - **Painel Central (Lista de Mensagens):** Lista paginada (com limite dinâmico) das mensagens indexadas na pasta selecionada, exibindo assunto, remetente, data e badges de presença de anomalias (`ALERTA`) ou anexos (`📎`).
  - **Painel Direito (Message Preview Safe):** O visualizador seguro do corpo da mensagem.

### 4. Message Preview Safe (Visualizador Seguro)
- Exibe de forma organizada os metadados do e-mail (Assunto, Remetente, Destinatários, Cc, datas, lista de anexos extraídos com tamanhos).
- **Safe View Rule (Segurança Forense):** Para garantir o sigilo operacional de dados sensíveis e manter o banco SQLite leve, o corpo do e-mail é automaticamente truncado em **400 caracteres**, acompanhado de um proeminente banner de privacidade forense:
  > 🛡️ **AVISO DE SEGURANÇA E PRIVACIDADE FORENSE**  
  > *Pré-visualização de mensagem limitada. O corpo completo não reside no índice relacional (case.db) para assegurar o sigilo operacional. Use a exportação forense para obter a integridade total do conteúdo.*

### 5. Busca Integrada
- Interface para buscas rápidas em tempo real sobre metadados indexados e previews de corpos das mensagens.
- Apresentação de resultados em grade reativa, ligada ao visualizador reativo compartilhado.

### 6. Painéis de Exportação & Validação
- **Exportação:** Configuração do formato pretendido de exportação (EML/MBOX), extração de anexos e indicação do status do job.
- **Validação:** Visualização simplificada de status de integridade consolidada (Passed, PassedWithWarnings ou Failed), métricas estruturadas de correspondência e alertas técnicos mapeados.

---

## 3. Como Executar a Interface Desktop

Para rodar a interface gráfica localmente a partir da raiz do repositório:
```bash
dotnet run --project src/MailVault.Desktop/MailVault.Desktop.csproj
```
