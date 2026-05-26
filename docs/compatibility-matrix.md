# Matriz de Compatibilidade Forense (Compatibility Matrix)

Esta matriz mapeia o suporte e a conformidade técnica do MailVault Recovery contra mídias reais do Microsoft Outlook e Thunderbird no Laboratório de Validação.

| Categoria | Arquivo Local Esperado | Tamanho Esperado | Status do Teste | Observações | Último Resultado Agregado |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **PST Unicode pequeno** | `pst/small/sample.pst` | < 100 MB | *Pendente* | Teste estrutural simples e validação de codepage. | N/A |
| **PST Unicode grande** | `pst/medium/archive.pst` | ~ 2 GB | *Pendente* | Validação de buffers e exaustão de heap. | N/A |
| **PST com muitos anexos**| `pst/large/attachments.pst` | ~ 5 GB | *Pendente* | Homologação de streaming em lote (`IAttachmentContentProvider`). | N/A |
| **OST Microsoft 365 médio** | `ost/medium/heloisa.nogueira@backup.ost` | 1.6 GB | *Sucesso* | Validado com XstReader, com FK virtual de \Root resolvida. | Passed (M6) |
| **OST Microsoft 365 grande** | `ost/large/m365_massive.ost` | ~ 15 GB | *Pendente* | Homologação de grandes tabelas MAPI com XstReader. | N/A |
| **OST órfão** | `ost/orphaned/disconnected.ost` | ~ 1 GB | *Pendente* | Leitura de contas desconectadas. | N/A |
| **OST parcialmente corrompido**| `ost/orphaned/corrupted.ost` | ~ 500 MB | *Pendente* | Resiliência a blocos MAPI ausentes ou nulos. | N/A |
| **Thunderbird MBOX médio** | `thunderbird/mbox/wagner_butinhao` | 300 MB | *Sucesso* | Validação estrutural de cabeçalhos e mboxrd com MimeKit. | Passed (M6) |
| **Thunderbird MBOX grande** | `thunderbird/mbox/archive_large`| ~ 4 GB | *Pendente* | Performance de leitura sequencial incremental. | N/A |
| **MBOX com anexos** | `thunderbird/mbox/attachments` | ~ 300 MB | *Pendente* | Separação física e decodificação base64 de anexos no MIME. | N/A |
| **MBOX com linha From** | `thunderbird/mbox/internal_from` | < 10 MB | *Pendente* | Validação estrita contra falsos envelopes no corpo. | N/A |

---

> [!IMPORTANT]
> **Notas Operacionais**:
> - Esta tabela é atualizada de forma manual ou por scripts de agregação técnica locais no laboratório de validação do operador.
> - Nomes de arquivos reais listados na coluna "Arquivo Local Esperado" representam apenas placeholders exemplares de arquivos mantidos fisicamente no ambiente local isolado e gitignored do operador.
