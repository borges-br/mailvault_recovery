# 💻 Manual do CLI — MailVault Recovery

O `mailvault` é a interface de linha de comando do MailVault Recovery: ideal para automação, lotes grandes e scripts. Tudo que o Desktop faz, o CLI também faz.

```text
mailvault <comando> [argumentos] [opções]
mailvault --help
mailvault <comando> --help
```

> No release publicado o executável é **`mailvault.exe`**. Em desenvolvimento, troque `mailvault` por:
> `dotnet run --project src/MailVault.Cli/MailVault.Cli.csproj -- <comando> ...`

## 🧭 Dois jeitos de recuperar

```mermaid
flowchart TB
    subgraph Rapido["⚡ Recuperação direta — 1 comando"]
        R1["recover-eml / recover-mbox"] --> R2["arquivos EML/MBOX + relatório"]
    end
    subgraph Caso["🗂️ Fluxo de caso — navegar, buscar, validar"]
        C1["index"] --> C2["search / stats"] --> C3["export"] --> C4["validate"]
    end

    classDef a fill:#9283F4,stroke:#7C6BEF,color:#15132A,stroke-width:2px;
    classDef b fill:#2A2350,stroke:#7C6BEF,color:#E9E5FF,stroke-width:1.5px;
    class R1,R2 a;
    class C1,C2,C3,C4 b;
```

- **Recuperação direta** — você só quer os e-mails para fora, rápido. Não cria banco nem índice.
- **Fluxo de caso** — você quer navegar, buscar, exportar subconjuntos e validar a integridade do que saiu.

## 📋 Índice de comandos

| Comando | Grupo | O que faz |
| :--- | :--- | :--- |
| [`recover-eml`](#recover-eml) | ⚡ Direto | OST/PST → arquivos `.eml`, sem indexar antes. |
| [`recover-mbox`](#recover-mbox) | ⚡ Direto | OST/PST → arquivos `.mbox`, sem indexar antes. |
| [`recover-pst`](#recover-pst) | ⚡ Direto | Geração de PST — **não suportado** nesta build (explica o porquê). |
| [`inspect`](#inspect) | 🔎 Inspeção | Calcula SHA-256 e cria manifesto/trilha do arquivo. |
| [`tree`](#tree) | 🔎 Inspeção | Mostra a árvore de pastas do arquivo. |
| [`list`](#list) | 🔎 Inspeção | Lista mensagens de uma pasta. |
| [`preview`](#preview) | 🔎 Inspeção | Mostra os detalhes de uma mensagem. |
| [`index`](#index) | 🗂️ Caso | Indexa metadados em `case.db`. |
| [`stats`](#stats) | 🗂️ Caso | Estatísticas do índice. |
| [`search`](#search) | 🗂️ Caso | Busca mensagens no índice. |
| [`export`](#export) | 🗂️ Caso | Exporta do índice para EML/MBOX. |
| [`validate`](#validate) | 🗂️ Caso | Valida a integridade da exportação. |
| [`carve`](#carve) | 🧪 Avançado | Varredura física por assinaturas (somente relatório). |
| [`corpus scan`](#corpus-scan) | 🧪 Avançado | Inventaria uma pasta de arquivos de e-mail. |

---

## ⚡ Recuperação direta

### `recover-eml`

Recupera e-mails direto de um `OST/PST` para arquivos `.eml` (um por mensagem), **sem indexação prévia**.

```text
mailvault recover-eml <file> --out <dir> [--folder <path>] [opções avançadas]
```

| Argumento / Opção | Obrigatório | Descrição |
| :--- | :---: | :--- |
| `file` | ✅ | Caminho do `.ost`/`.pst` de origem. |
| `--out <dir>` | ✅ | Pasta de destino dos `.eml`. |
| `--folder <path>` | — | Recupera apenas uma pasta lógica (ex.: `Inbox/Financeiro`). |

```powershell
mailvault recover-eml "C:\backup\caixa.ost" --out "C:\recuperados\eml"
```

### `recover-mbox`

Igual ao anterior, mas gera arquivos `.mbox` (uma caixa por pasta, com escape mboxrd).

```text
mailvault recover-mbox <file> --out <dir> [--folder <path>] [opções avançadas]
```

```powershell
mailvault recover-mbox "C:\backup\caixa.pst" --out "C:\recuperados\mbox" --folder "Inbox"
```

#### Opções avançadas (válidas em `recover-eml` e `recover-mbox`)

| Opção | Descrição |
| :--- | :--- |
| `--max-messages <n>` | Limite global de mensagens (escopo/teste). |
| `--max-folder-messages <n>` | Limite por pasta. |
| `--timeout <dur>` | Cancela a sessão após a duração (ex.: `30m`, `90s`, `1h`). |
| `--checkpoint-interval <s>` | Segundos entre checkpoints de relatório parcial (padrão `30`). |
| `--progress-json <path>` | Grava `progress.json` continuamente. |
| `--deep-scan` | Após a leitura estrutural, aciona o Deep Scan (`libpff/pffexport`) para resgatar itens órfãos. Dispara automaticamente quando a leitura estrutural falha. Experimental. |

> [!NOTE]
> A recuperação é **resiliente**: mensagens ou anexos problemáticos são registrados e ignorados sem abortar o lote. Ao final, um relatório (`.json`/`.md`) lista totais, falhas e classificação (Completo / Parcial / Inconclusivo).

### `recover-pst`

```text
mailvault recover-pst <file> [--out <dir>]
```

> [!WARNING]
> **Não suportado** nesta build. Gerar um PST de saída exigiria um writer de PST próprio. O comando existe para ser honesto: ele explica a limitação e recomenda `recover-eml`/`recover-mbox`, que produzem formatos abertos e portáveis.

---

## 🔎 Inspeção (sem criar caso)

### `inspect`

Calcula o SHA-256 do arquivo de origem e grava o manifesto/trilha — o ponto de partida para rastreabilidade.

```text
mailvault inspect <file> [--out <dir>]
```

### `tree`

Exibe a hierarquia de pastas do arquivo.

```text
mailvault tree <file> [--out <dir>] [--max-depth <n>]
```

```powershell
mailvault tree "C:\backup\caixa.pst" --max-depth 3
```

### `list`

Lista as mensagens de uma pasta, com paginação.

```text
mailvault list <file> --folder <path> [--limit <n>] [--offset <n>] [--out <dir>]
```

| Opção | Padrão | Descrição |
| :--- | :---: | :--- |
| `--folder <path>` | (obrigatório) | ID ou caminho da pasta (ex.: `Inbox/Financeiro`). |
| `--limit <n>` | `50` | Máximo de itens exibidos. |
| `--offset <n>` | `0` | Quantos itens pular (paginação). |

### `preview`

Mostra os detalhes de uma mensagem específica (cabeçalhos, anexos e um trecho do corpo).

```text
mailvault preview <file> --message-id <id> [--body-lines <n>] [--out <dir>]
```

---

## 🗂️ Fluxo de caso

### `index`

Lê o arquivo via adapter e indexa os metadados de pastas, mensagens e anexos num `case.db` (SQLite) persistente.

```text
mailvault index <file> [--out <dir>] [--case-id <id>] [--force] [--limit <n>] [--no-preview-cache]
```

| Opção | Descrição |
| :--- | :--- |
| `--out <dir>` | Pasta base dos casos (padrão `./mailvault-cases/`). |
| `--case-id <id>` | Define um ID de caso em vez do gerado automaticamente. |
| `--force` | Recria o índice se o caso já existir. |
| `--limit <n>` | Limita as mensagens indexadas por pasta (útil em testes). |
| `--no-preview-cache` | Não gera/armazena o preview do corpo no índice. |

```powershell
mailvault index "C:\backup\caixa.ost" --out ".\mailvault-cases" --case-id "CASE-001"
```

### `stats`

```text
mailvault stats <case-folder>
```

Mostra contagens consolidadas (pastas, mensagens, anexos, issues) do `case.db`.

### `search`

```text
mailvault search <case-folder> --query <texto> [--folder <path>] [--limit <n>] [--offset <n>] [--include-preview]
```

```powershell
mailvault search ".\mailvault-cases\CASE-001" --query "nota fiscal" --include-preview
```

### `export`

Exporta as mensagens indexadas para EML ou MBOX. Antes de gravar, **recalcula o SHA-256 da origem** e aborta se o arquivo mudou desde a indexação.

```text
mailvault export <case-folder> --format <eml|mbox> --out <dir> [opções]
```

| Opção | Padrão | Descrição |
| :--- | :---: | :--- |
| `--format <eml\|mbox>` | (obrigatório) | Formato de saída. |
| `--out <dir>` | (obrigatório) | Pasta de destino. |
| `--folder <path>` | — | Exporta apenas uma pasta. |
| `--limit` / `--offset` | — | Paginação global. |
| `--include-attachments` | `true` | Incorpora anexos na estrutura MIME. |
| `--extract-attachments` | `false` | Também salva anexos como arquivos avulsos. |
| `--overwrite` | `false` | Sobrescreve uma exportação anterior. |
| `--dry-run` | `false` | Simula e valida sem gravar em disco. |

```powershell
mailvault export ".\mailvault-cases\CASE-001" --format eml --out ".\exports\CASE-001" --extract-attachments
```

### `validate`

Compara a exportação física com o índice e o manifesto, apontando arquivos ausentes, vazios, duplicados, MBOX mal-escapado e divergência de anexos.

```text
mailvault validate <case-folder> [--export-folder <dir>] [--format <eml|mbox|auto>] [--json] [--strict] [--out <dir>]
```

| Opção | Padrão | Descrição |
| :--- | :---: | :--- |
| `--export-folder <dir>` | — | Pasta com os arquivos exportados a validar. |
| `--format` | `auto` | `eml`, `mbox` ou detecção automática. |
| `--json` | `false` | Saída em JSON bruto. |
| `--strict` | `false` | Qualquer warning estrutural vira falha. |
| `--check-eml-parse` | `true` | Parse estrutural profundo dos EML. |
| `--check-mbox-structure` | `true` | Auditoria de layout dos MBOX. |
| `--check-attachments` | `true` | Validação cruzada física dos anexos. |
| `--sample-size <n>` | — | Limita a amostragem física validada. |

```powershell
mailvault validate ".\mailvault-cases\CASE-001" --export-folder ".\exports\CASE-001" --format eml --json --out ".\exports\CASE-001-validation"
```

---

## 🧪 Avançado

### `carve`

Varredura física (carving) por assinaturas em arquivos severamente corrompidos, **quando a leitura estrutural falha**. É **somente relatório**: lista candidatos físicos, não exporta e-mails por padrão.

```text
mailvault carve <file> --out <dir> [--timeout <dur>] [--export] [--min-confidence <0-100>] [...]
```

> [!NOTE]
> O `carve` é uma ferramenta de diagnóstico/localização. Em arquivos com cabeçalho ou índice destruídos, o conteúdo costuma ser largamente irrecuperável sem reconstrução de bloco de nível comercial. Use-o para entender o que ainda existe fisicamente no arquivo.

### `corpus scan`

```text
mailvault corpus scan <corpus-folder> [--out <dir>]
```

Inventaria e categoriza os arquivos de e-mail de uma pasta — útil para preparar lotes de teste/validação.

---

## ⚙️ Comandos internos

`index-worker` e `worker --job <job.json>` são usados **pelo Desktop** para rodar indexação, exportação, preview e validação em subprocesso, sem travar a interface. Em uso normal, prefira os comandos públicos acima.
