# Documentação do MailVault Recovery

Este diretório contém documentação consolidada e registros históricos de milestones.

## Guias canônicos

Os documentos abaixo foram revisados contra o código atual do repositório e devem ser usados como referência principal:

| Documento | Uso |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Arquitetura real, camadas, boundaries, workspace e limitações. |
| [BUILD_AND_PUBLISH.md](BUILD_AND_PUBLISH.md) | Build, testes, publish Windows e layout publicado. |
| [EXTERNAL_TOOLS.md](EXTERNAL_TOOLS.md) | XstReader, pffexport/libpff, readpst e estado plug and play. |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Diagnóstico operacional de falhas comuns. |
| [FORENSIC_SAFETY.md](FORENSIC_SAFETY.md) | Cuidados de segurança, integridade e operação técnica. |
| [ROADMAP.md](ROADMAP.md) | Itens implementados, parciais e planejados. |

## Documentos técnicos específicos

Alguns documentos descrevem módulos individuais e continuam úteis para manutenção:

| Documento | Tema |
| --- | --- |
| [adapter-resolver.md](adapter-resolver.md) | Resolução dinâmica de adapters. |
| [xstreader-adapter.md](xstreader-adapter.md) | Boundary do adapter XstReader. |
| [indexing.md](indexing.md) | Indexação e `case.db`. |
| [exporting.md](exporting.md) | Pipeline de exportação. |
| [eml-exporter.md](eml-exporter.md) | Exportador EML. |
| [mbox-exporter.md](mbox-exporter.md) | Exportador MBOX. |
| [validation.md](validation.md) | Validação de exportações. |
| [desktop-ui.md](desktop-ui.md) | Notas da UI Desktop. |
| [dependency-policy.md](dependency-policy.md) | Política de dependências. |
| [test-corpus-policy.md](test-corpus-policy.md) | Política de corpus local. |

## Registros históricos

Arquivos como `milestone-*.md`, `compatibility-matrix.md`, `real-corpus-validation-run.md` e `technical-debt.md` preservam contexto de evolução do projeto. Eles podem refletir objetivos ou estados intermediários. Quando houver divergência, prefira o `README.md` da raiz e os guias canônicos listados acima.

## Regra de manutenção

Ao alterar comportamento do código, atualize pelo menos:

1. `README.md`, se a mudança afetar onboarding, comandos, estado do projeto ou limitações.
2. O guia canônico correspondente nesta pasta.
3. O roadmap, se uma feature mudar de planejada/parcial para implementada.
4. Troubleshooting, se a mudança alterar mensagens de erro ou diagnóstico.
