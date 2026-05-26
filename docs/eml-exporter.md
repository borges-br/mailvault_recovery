# Gerador de Arquivos EML (EmlExporter)

Esta documentação descreve o funcionamento técnico do módulo de exportação EML (`EmlExporter`) do MailVault Recovery, baseado na biblioteca MimeKit.

## 1. Visão Geral

O projeto `MailVault.Exporters.Eml` implementa a geração física de arquivos no formato RFC 822 / RFC 2822 (`.eml`). Ele utiliza a biblioteca de alta performance **MimeKit v4.16.0** para formatar cabeçalhos estruturados, codificar corpos de texto/HTML e embutir anexos físicos.

---

## 2. Princípios de Isolação Tecnológica

Conforme as diretrizes de Clean Core e gerenciamento de dependências:
- O pacote NuGet `MimeKit` é referenciado **estritamente** no projeto `MailVault.Exporters.Eml.csproj`.
- Nenhum tipo, classe ou namespace do MimeKit vaza para as camadas de núcleo (`MailVault.Core`), modelo de domínio (`MailVault.Domain`) ou interface de usuário/CLI (`MailVault.Cli`).
- O motor de exportação consome a abstração `IMessageExporter` definida no núcleo, mantendo o acoplamento baixíssimo.

---

## 3. Arquitetura Interna

O método principal é `ExportMessageAsync`, responsável por converter um `MailItem` de domínio em uma mensagem MIME (`MimeMessage`) e transmiti-la para um stream de saída.

```mermaid
graph LR
    A[MailItem de Domínio] --> B[EmlExporter]
    C[IAttachmentContentProvider] --> B
    B --> D[MimeMessage]
    D --> E[MimeMessage.WriteTo]
    E --> F[Filtro de Gravação / Stream de Destino]
```

### Cabeçalhos Estruturados
O gerador inicializa e preenche rigorosamente os metadados forenses da mensagem RFC 822:
- **Message-Id**: Preserva o ID original da mensagem de internet, ou gera um valor determinístico se nulo.
- **Subject**: Sanitizado e codificado (UTF-8) para evitar corrupção de caracteres especiais.
- **From / To / Cc / Bcc**: Converte os endereços de correio internos (`MailAddressRef`) para a estrutura do MimeKit (`MailboxAddress`), codificando nomes de exibição.
- **Datas**: Preenche `Date` (SentAt) e cabeçalhos técnicos de recepção (ReceivedAt) preservando fusos horários originais.

### Corpo da Mensagem
- Cria a estrutura multipart necessária se houver múltiplos componentes (como corpo e anexos).
- Adiciona o corpo em formato plain text (`TextPart`) ou HTML, se disponível na evidência original.

### Manipulação Eficiente de Anexos
Para anexos, o MimeKit utiliza streams binários passados pelo `IAttachmentContentProvider` sem alocar a totalidade dos dados na memória Heap do processo:
```csharp
var part = new MimePart(att.ContentType ?? "application/octet-stream")
{
    Content = new MimeContent(await provider.OpenAttachmentStreamAsync(messageId, attachmentId, ct)),
    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
    ContentTransferEncoding = ContentEncoding.Base64,
    FileName = AttachmentNameNormalizer.Normalize(att.FileName, att.InternalId)
};
```
Isso garante a capacidade do MailVault de exportar arquivos PST/OST de dezenas de gigabytes em hardware modesto sem sofrer de esgotamento de memória ou interrupções por Garbage Collection excessivo.
