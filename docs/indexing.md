# MailVault Recovery — Indexador Persistente (case.db)

Este documento detalha o design, a estrutura do banco de dados relacional e a estratégia de indexação por caso.

## Arquitetura de Persistência

Para evitar o reprocessamento dispendioso e lento de arquivos `.ost` ou `.pst` a cada comando (como listagens, visualizações em árvore ou pesquisas), o MailVault Recovery introduz um repositório local e persistente baseado em **SQLite (via Microsoft.Data.Sqlite 10.x)**.

### Isolamento de Sessão

Cada inspeção técnica gera um arquivo de banco de dados autocontido sob o diretório do caso correspondente:
`./mailvault-cases/<case-id>/case.db`

Isso assegura:
1. **Privacidade e Segregação**: Cada caso possui seus dados estritamente confinados à sua própria pasta física, facilitando exportações forenses consolidadas ou exclusão segura de dados de casos concluídos.
2. **Desempenho**: Bancos menores evitam lentidões causadas por contenção de gravação concorrente em um banco de dados global centralizado.

---

## Integridade Relacional e Configurações

O MailVault Recovery exige integridade relacional forte para evitar registros órfãos em deleções ou modificações de dados forenses:
- **Foreign Keys**: Toda conexão SQLite aberta executa ativamente e obrigatoriamente a diretiva:
  ```sql
  PRAGMA foreign_keys = ON;
  ```
- **Timezone Fallback**: Todos os carimbos de data/hora salvos em formato string no banco de dados (`TEXT` contendo ISO-8601) mantêm seus offsets originais ou aplicam fallbacks de timezone unificados.

---

## Schema do Banco de Dados

O banco de dados relacional `case.db` é versionado (atualmente **v3**) e composto pelas seguintes tabelas principais:

### 1. `case_info`
Armazena metadados forenses primários do arquivo inspecionado no caso.
```sql
CREATE TABLE case_info (
    case_id TEXT PRIMARY KEY,
    source_file TEXT,
    source_size INTEGER,
    source_sha256 TEXT,
    operator_name TEXT,
    started_at TEXT,
    completed_at TEXT
);
```

### 2. `folders`
Mapeia a árvore de diretórios internos da caixa de correio normalizada.
```sql
CREATE TABLE folders (
    folder_id TEXT PRIMARY KEY,
    parent_id TEXT,
    display_name TEXT,
    full_path TEXT,
    message_count INTEGER,
    FOREIGN KEY(parent_id) REFERENCES folders(folder_id)
);
```

### 3. `messages`
Contém metadados de e-mails, previews de texto higienizados e flags de mídias de anexo.
```sql
CREATE TABLE messages (
    message_id TEXT PRIMARY KEY,
    internet_message_id TEXT,
    folder_id TEXT,
    subject TEXT,
    sender TEXT,
    recipients_to TEXT,
    recipients_cc TEXT,
    recipients_bcc TEXT,
    sent_at TEXT,
    received_at TEXT,
    has_text_body INTEGER,
    has_html_body INTEGER,
    body_preview TEXT,
    attachment_count INTEGER,
    mapi_properties_count INTEGER,
    FOREIGN KEY(folder_id) REFERENCES folders(folder_id)
);
```

### 4. `attachments`
Metadados de arquivos anexados às mensagens.
```sql
CREATE TABLE attachments (
    attachment_id TEXT PRIMARY KEY,
    message_id TEXT,
    file_name TEXT,
    content_type TEXT,
    size_bytes INTEGER,
    content_id TEXT,
    is_inline INTEGER,
    FOREIGN KEY(message_id) REFERENCES messages(message_id)
);
```

### 5. `issues`
Erros, inconsistências forenses e avisos técnicos encontrados na extração.
```sql
CREATE TABLE issues (
    issue_code TEXT,
    severity TEXT,
    message TEXT,
    object_id TEXT,
    technical_details TEXT
);
```

### 6. `index_runs`
Log de auditoria das execuções de indexação técnica e taxas de transferência.
```sql
CREATE TABLE index_runs (
    run_id TEXT PRIMARY KEY,
    case_id TEXT,
    timestamp TEXT,
    status TEXT,
    duration_ms INTEGER,
    folders_indexed INTEGER,
    messages_indexed INTEGER,
    attachments_indexed INTEGER,
    issues_detected INTEGER,
    FOREIGN KEY(case_id) REFERENCES case_info(case_id)
);
```

---

## Índices de Alta Performance (Mandatórios)

Para otimizar buscas complexas e filtros em grandes volumes de dados de e-mail inspecionados, o schema implementa os seguintes índices mínimos:

```sql
CREATE INDEX idx_messages_folder_id ON messages(folder_id);
CREATE INDEX idx_messages_subject ON messages(subject);
CREATE INDEX idx_messages_sender ON messages(sender);
CREATE INDEX idx_attachments_message_id ON attachments(message_id);
CREATE INDEX idx_issues_object_id ON issues(object_id);
```

*Esses índices aceleram a exibição recursiva de estatísticas, navegação em pastas e consultas do mecanismo de busca sem causar degradação de gravação no processo em lote.*
