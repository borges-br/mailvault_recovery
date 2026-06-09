# Roadmap Técnico

Este roadmap separa o que está implementado, o que está parcial e o que é próximo passo recomendado. Ele deve ser atualizado junto com o código.

## Implementado

| Área | Estado | Observação |
| --- | --- | --- |
| Solução modular .NET | Done | Projetos separados para Domain, Core, Audit, Indexing, Exporters, Validation, Adapters, CLI e Desktop. |
| CLI operacional | Done | Inspect, tree, list, preview, index, stats, search, export, validate e corpus scan. |
| Desktop Avalonia | Done | Wizard, workspace, overview, browser, busca, exportação, validação, settings, recentes e Test Lab. |
| XstReader PST/OST | Done | Adapter funcional carregado dinamicamente. |
| SQLite case index | Done | Schema versionado e tabelas para caso, runs, pastas, mensagens, anexos e issues. |
| Exportação EML | Done | Geração com MimeKit e suporte a anexos via provider. |
| Exportação MBOX | Done | Geração por pasta com escape mboxrd. |
| Validação | Done | Checks de EML, MBOX, anexos, manifest e relatórios JSON. |
| Worker Desktop/CLI | Done | Subprocesso CLI com protocolo JSON e diagnóstico de resolução. |
| Testes automatizados | Done | Cobertura em Core, Indexing, Exporters, Validation, Desktop, Audit, Domain e Adapters. |

## Parcial ou experimental

| Área | Estado | Lacuna |
| --- | --- | --- |
| `pffexport/libpff` | Partial | Detecta e executa ferramenta, mas não indexa a saída em mensagens/pastas/anexos. |
| Adapter `MailVault.Adapters.Libpff` | Partial | Projeto existe, mas não implementa `IMailStoreReader`. |
| Plug and play completo | Partial | Publish inclui Desktop/CLI/XstReader, mas não empacota ferramentas externas nativas. |
| MBOX/EML como entrada | Partial | Corpus scan e validação reconhecem; não há ingestão/indexação completa. |
| MSG como entrada | Planned | Arquivos `.msg` são ignorados pelo `.gitignore`, mas não há reader implementado. |
| Rastreabilidade (hash + trilha) | Partial | Há hash SHA-256, `manifest.json` e `audit.log`; cadeia de custódia formal continua externa ao app. |

## Próximos passos recomendados

### 1. Libpff plug and play

Status: Planned

- Definir política de licença e redistribuição.
- Incluir binários nativos no publish quando permitido.
- Validar `pffexport -V` no script.
- Criar testes de layout publicado com ferramenta presente/ausente.
- Implementar parser da saída do `pffexport`.
- Mapear resultado para `case.db` com issues rastreáveis.

### 2. Reader MBOX/EML/MSG

Status: Planned

- Criar contratos ou adapters específicos de ingestão.
- Separar claramente entrada MBOX/EML de exportação MBOX/EML.
- Definir comportamento para anexos, encoding e mensagens malformadas.
- Adicionar testes com corpus sintético e real controlado.

### 3. Robustez operacional

Status: Planned

- Melhorar mensagens de erro do Desktop para cenários de ferramenta externa ausente.
- Adicionar health check completo do publish.
- Expor relatório de ambiente no Desktop.
- Criar comando CLI dedicado para diagnóstico de engines.

### 4. Observabilidade e manutenção

Status: Planned

- Padronizar códigos de issue.
- Documentar schema SQLite com migrações.
- Assinatura de código do release Windows (remover o aviso do SmartScreen).
- Adicionar validação automatizada de Markdown/Mermaid em CI quando houver workflow.

## Critérios para marcar uma feature como Done

Uma feature só deve sair de Partial/Planned para Done quando houver:

- implementação no código;
- caminho operacional documentado;
- testes cobrindo sucesso e falha relevante;
- comportamento de erro claro;
- impacto de segurança/documentação revisado;
- validação no artefato publicado quando a feature depender de publish.
