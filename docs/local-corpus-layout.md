# Estrutura Local de Pastas — Laboratório de Validação (Corpus Layout)

Este documento descreve a topologia física e a finalidade de cada diretório contido no laboratório de validação do MailVault Recovery (`.local-corpus/`).

---

## 1. Topologia de Diretórios

A pasta `.local-corpus/` deve ser instanciada na raiz da solução com a seguinte organização de subpastas (totalmente gitignored):

```text
.local-corpus/
├── README.md
├── ost/
│   ├── small/
│   ├── medium/
│   ├── large/
│   └── orphaned/
├── pst/
│   ├── small/
│   ├── medium/
│   ├── large/
│   └── password-protected/
├── thunderbird/
│   └── mbox/
├── expected/
│   ├── metadata/
│   └── notes/
└── results/
    ├── runs/
    └── summaries/
```

---

## 2. Finalidade dos Diretórios

### `ost/`
Diretório destinado a armazenar arquivos `.ost` reais originários de contas do Microsoft 365, Exchange Server ou IMAP:
- `small/`: Mídias de até 500 MB para testes de sanidade rápidos.
- `medium/`: Arquivos entre 500 MB e 5 GB para checagens de buffer.
- `large/`: Bancos volumosos acima de 5 GB para testes de exaustão de memória e streaming de arquivos de anexo gigantes.
- `orphaned/`: Arquivos desvinculados de contas ativas para homologação de resiliência.

### `pst/`
Destinado a arquivos de arquivos mortos (`.pst`) e backups do Outlook:
- `small/`, `medium/`, `large/`: Escalonamento volumétrico similar ao OST.
- `password-protected/`: Mídias protegidas por criptografia MAPI para testes de segurança e decodificação estrutural.

### `thunderbird/`
Destinado a arquivos no formato Unix MBOX extraídos da árvore física do Mozilla Thunderbird (`mbox/`), úteis para validação de delimitadores e escape `mboxrd`.

### `expected/`
Guarda planilhas de metadados (`metadata/`) e anotações manuais (`notes/`) contendo o inventário preciso e verificado de pastas e contagem total de itens de cada mídia do corpus para servir de base comparativa durante validações rigorosas.

### `results/`
Destinado ao arquivamento local dos resultados dos jobs de validação:
- `runs/`: Pasta de logs técnicos sequenciais gerados por scripts com timestamp.
- `summaries/`: Resumos estatísticos unificados de performance e compatibilidade operacional.
