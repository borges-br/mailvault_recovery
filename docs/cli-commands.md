# Manual de Comandos CLI — MailVault Recovery

Este manual descreve a sintaxe, opções e exemplos de uso dos novos comandos de inspeção e leitura introduzidos na **Milestone 2** do **MailVault Recovery**.

---

## 1. `mailvault tree`

Exibe a estrutura hierárquica completa de pastas contidas em um arquivo de dados do Outlook (`.ost` ou `.pst`).

### Sintaxe
```bash
mailvault tree <file> [options]
```

### Argumentos
- `file` (Obrigatório): O caminho para o arquivo OST ou PST.

### Opções
- `--out <directory>`: O diretório de saída base para a pasta do caso de recuperação. Por padrão, cria a pasta `./mailvault-cases/`.
- `--max-depth <depth>`: Profundidade máxima de exibição de subpastas (padrão `99`).

### Exemplo
```bash
mailvault tree "C:\Evidencias\backup.pst" --max-depth 3
```

### Saída Típica
```text
================================================================================
                  MailVault Recovery — Árvore de Pastas                         
================================================================================
[*] Caso Inicializado: CASE-2026-05-26-093012
[*] Operador: nathan
[*] Arquivo: C:\Evidencias\backup.pst
[*] Calculando hash de integridade (SHA-256 por streaming)...
[*] SHA-256: 4e9c71a39f60d4b8f...

Estrutura Hierárquica de Pastas:
--------------------------------------------------------------------------------
├── Top of Outlook Data Store (Mensagens: 0)
│   ├── Inbox (Mensagens: 1240)
│   │   └── Financeiro (Mensagens: 120)
│   ├── Sent Items (Mensagens: 890)
│   └── Deleted Items (Mensagens: 42)
--------------------------------------------------------------------------------
[x] Varredura Concluída.
[*] Total de Pastas: 5
[*] Manifesto e trilha gravados em: ./mailvault-cases/CASE-2026-05-26-093012
================================================================================
```

---

## 2. `mailvault list`

Lista as mensagens de e-mail contidas em uma pasta específica do arquivo de dados, permitindo navegação tabular e alertas integrados.

### Sintaxe
```bash
mailvault list <file> --folder <folder-id-or-path> [options]
```

### Argumentos
- `file` (Obrigatório): O caminho para o arquivo OST ou PST.

### Opções
- `--folder <path>` (Obrigatório): O ID interno ou o caminho completo da pasta a ser listada (ex: `Inbox/Financeiro`).
- `--limit <number>`: Quantidade máxima de e-mails a exibir (padrão `50`).
- `--offset <number>`: Quantidade de e-mails a ignorar/pular na listagem para fins de paginação (padrão `0`).
- `--out <directory>`: O diretório de saída base para salvar a pasta do caso.

### Exemplo
```bash
mailvault list "C:\Evidencias\backup.pst" --folder "Inbox/Financeiro" --limit 10 --offset 20
```

### Saída Típica
```text
================================================================================
                  MailVault Recovery — Listagem de Mensagens                    
================================================================================
[*] Caso Inicializado: CASE-2026-05-26-093145
...
[*] Encontradas 120 mensagens no total. Exibindo 10 itens (limit=10, offset=20):

--------------------------------------------------------------------------------
   ID INTERNO   |      DATA      |      REMETENTE      | ASSUNTO
--------------------------------------------------------------------------------
msg-fin-021     | 2026-05-15 14:30 | Roberto Silva           | Relatório Trimestral | anexos: 1
msg-fin-022     | 2026-05-15 15:02 | contabilidade@corp.c... | NF-e de Serviços M... | anexos: 2
   --> ALERTA: [MV-WARN-DECODE] Falha ao decodificar codepage de anexo.
msg-fin-023     | 2026-05-16 09:12 | diretoria@corp.com      | Reunião Orçamento    | anexos: 0
...
--------------------------------------------------------------------------------
```

---

## 3. `mailvault preview`

Apresenta detalhes estruturados, cabeçalhos, alertas específicos e uma visualização parcial e **segura/truncada** do corpo da mensagem para fins de conformidade e LGPD.

### Sintaxe
```bash
mailvault preview <file> --message-id <id> [options]
```

### Argumentos
- `file` (Obrigatório): O caminho para o arquivo OST ou PST.

### Opções
- `--message-id <id>` (Obrigatório): O ID interno único da mensagem a visualizar.
- `--body-lines <number>`: Quantidade máxima de linhas do corpo a exibir no preview (padrão `30`), prevenindo vazamento massivo acidental de dados sensíveis no terminal.
- `--out <directory>`: O diretório de saída base para salvar a pasta do caso.

### Exemplo
```bash
mailvault preview "C:\Evidencias\backup.pst" --message-id "msg-fin-022" --body-lines 15
```

### Saída Típica
```text
================================================================================
                  MailVault Recovery — Visualização Segura                      
================================================================================
[*] Caso Inicializado: CASE-2026-05-26-093402
...
CABEÇALHOS E DADOS:
--------------------------------------------------------------------------------
Internal ID   : msg-fin-022
Message ID    : <NF-12345-2026@corp.com>
Assunto       : NF-e de Serviços Maio/2026
Remetente     : contabilidade@corp.com <contabilidade@corp.com>
Para          : financeiro@corp.com
Cc            : diretoria@corp.com
Data Envio    : 2026-05-15 15:00:00 -03:00
Data Recepção : 2026-05-15 15:02:00 -03:00
Possui HTML   : Sim
Possui Texto  : Sim
MAPI Props    : 84 propriedades indexadas.

ANEXOS:
--------------------------------------------------------------------------------
  - Anexo ID    : att-fin-022-1
    Nome        : NFe_Contabilidade_123.pdf
    Tamanho     : 145,200 bytes
    Inline      : Não

PREVIEW DO CORPO DA MENSAGEM (Truncado em no máximo 15 linhas):
--------------------------------------------------------------------------------
Prezada equipe do financeiro,

Segue em anexo a Nota Fiscal de Prestação de Serviços referente ao período
de faturamento de Maio de 2026.

Por favor, procedam com o agendamento do pagamento conforme as condições
pactuadas em contrato.

Qualquer dúvida, estou à disposição.

[... TEXTO TRUNCADO SEGURAMENTE PARA COMPLIANCE FORENSE - 4 LINHAS OCULTAS ...]
--------------------------------------------------------------------------------
```
