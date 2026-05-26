# Guia de Validação com Corpus Real (Real Corpus Validation Run)

Este documento descreve como preparar, rodar e analisar as rodadas de validação técnica automatizada contra arquivos de correio eletrônico reais locais e não confidenciais no projeto **MailVault Recovery**.

## 1. Como Preparar o Corpus Local

A pasta `.local-corpus/` é estritamente ignorada no controle de versão (`.gitignore`) para assegurar que nenhuma evidência ou arquivo real sensível de teste seja versionado no repositório público do Git.

### Estrutura de Diretórios Requerida:
```
.local-corpus/
├── ost/
│   ├── small/
│   ├── medium/
│   │   └── [heloisa.nogueira@backup.ost]  <-- Inserir arquivos reais aqui
│   ├── large/
│   └── orphaned/
├── pst/
│   ├── small/
│   ├── medium/
│   └── large/
├── thunderbird/
│   └── mbox/
│       └── [Wagner_Butinhao]             <-- Inserir arquivos MBOX reais aqui
├── expected/
├── results/
│   └── runs/                             <-- Pasta gerada automaticamente para os relatórios
└── runs/
```

> [!WARNING]
> **Segurança Forense Rígida:** Nunca inclua dados reais, arquivos `.pst`, `.ost`, `.db`, caixas MBOX, manifestos ou logs reais em commits do Git.

---

## 2. Como Executar o Script de Automação

O pipeline é completamente automatizado através de scripts multiplataforma. Ele garante a compilação limpa do projeto CLI, sincroniza assemblies dependentes e executa a esteira inteira (index, stats, search, export eml, export mbox, validate).

### No Windows (PowerShell):
Abra o console do PowerShell na raiz do repositório e execute:
```powershell
./scripts/validation/run-local-corpus.ps1
```

### No Linux/macOS (Bash):
Abra o terminal bash na raiz do repositório e execute:
```bash
chmod +x ./scripts/validation/run-local-corpus.sh
./scripts/validation/run-local-corpus.sh
```

---

## 3. Como Interpretar os Relatórios Consolidados (`summary.json` e `summary.md`)

Os resultados de cada rodada de validação são arquivados com marcação de data/hora sob `.local-corpus/results/runs/<timestamp>/`.

A rodada gera dois arquivos de sumário consolidados e relatórios de validação individuais para cada arquivo processado:
- `summary.json`: Métricas estruturadas de alto nível, ideais para ferramentas de processamento automático.
- `summary.md`: Um painel markdown amigável, apresentando:
  - Tamanho em disco total processado.
  - Tabela com mensagens indexadas vs. exportadas.
  - Tabela com anexos indexados vs. exportados.
  - Tempos precisos de performance por etapa (tempo de indexação, estatísticas, buscas, exportações e validação).
- `validation-report-<midiabase>.json`: Relatório de conformidade detalhado com mascaramento automático de caminhos locais para preservar a privacidade do operador (por exemplo, transformando `C:\Users\natha\...` em `C:\Users\<USER>\...`).

---

## 4. Limitações Observadas & Recomendações Técnicas

Durante o processo real de homologação de mídias reais de gigabytes, as seguintes diretrizes de robustez foram constatadas e integradas ao core:
- **Foreign Keys no SQLite:** As pastas virtuais ou ocultas do Outlook (`\Root`) que servem como pai mas não são persistidas na tabela de diretórios são automaticamente mapeadas como `NULL` na persistência relacional, prevenindo erros relacionais do SQLite.
- **Carregamento de Dependências de Plugins:** DLLs transitivas do adaptador como `XstReader.Api.dll` exigem amarração estrita ao evento `Resolving` do `AssemblyLoadContext.Default` para garantir que o runtime do .NET execute sua resolução dinâmica na pasta da CLI sem conflitos.
- **Safe View de E-mail:** A pré-visualização de mensagens na interface visual é rigidamente limitada a 400 caracteres com banner forense de destaque, preservando sigilo de dados em conformidade forense.
