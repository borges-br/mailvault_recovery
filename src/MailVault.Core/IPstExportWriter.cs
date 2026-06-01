using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

/// <summary>
/// Pedido de geração de um PST de destino limpo a partir de um IMailStoreReader.
/// </summary>
public sealed record PstExportRequest(
    string SourcePath,
    string OutputPstPath,
    string? TargetFolderPath = null);

/// <summary>
/// Resultado honesto de uma tentativa de escrita de PST.
/// <see cref="Supported"/> = false indica que a operação NÃO é suportada nesta build —
/// nenhum arquivo é criado e <see cref="Explanation"/> traz a justificativa técnica.
/// </summary>
public sealed record PstExportOutcome(
    bool Supported,
    bool Success,
    string? OutputPath,
    int ExportedMessages,
    string StatusCode,
    string Explanation,
    IReadOnlyList<RecoveryExportIssue> Issues);

/// <summary>
/// Contrato para um escritor de PST limpo. Mantido como abstração para permitir,
/// no futuro, uma implementação confiável (SDK licenciado ou writer nativo maduro)
/// sem alterar os chamadores. Não deve, em hipótese alguma, gerar um PST inválido
/// ou apenas renomeado.
/// </summary>
public interface IPstExportWriter
{
    string WriterName { get; }

    /// <summary>Indica se este writer consegue, de fato, produzir um PST válido.</summary>
    bool IsSupported { get; }

    Task<PstExportOutcome> WriteAsync(
        IMailStoreReader reader,
        PstExportRequest request,
        IProgress<RecoveryExportProgress>? progress,
        CancellationToken ct);
}

/// <summary>
/// Implementação honesta padrão: declara explicitamente que a geração de PST limpo
/// não é suportada nesta build e explica o porquê em termos técnicos. Nunca cria arquivo.
/// </summary>
public sealed class UnsupportedPstExportWriter : IPstExportWriter
{
    public const string NotSupportedCode = "MV-PST-WRITE-NOT-SUPPORTED";

    public string WriterName => "UnsupportedPstExportWriter";

    public bool IsSupported => false;

    public static string TechnicalExplanation =>
        "A geração de um arquivo PST/OST de destino válido não é suportada nesta build, " +
        "por decisão técnica deliberada (sem falsa promessa).\n\n" +
        "Motivo: escrever um PST Unicode aceito pelo Microsoft Outlook exige reconstruir, byte a byte, " +
        "as três camadas do formato PFF [MS-PST]:\n" +
        "  - NDB: árvores binárias NBT (nós) e BBT (blocos), offsets físicos de 64 bits, " +
        "CRCs por página/bloco e o esquema de ofuscação (Compressible/High);\n" +
        "  - LTP: Property Contexts (PC), Table Contexts (TC), BTree-on-Heap (BTH) e Heap-on-Node (HN);\n" +
        "  - Messages: mapeamento das propriedades MAPI de pastas, mensagens, destinatários e anexos.\n" +
        "Qualquer erro mínimo de alinhamento ou de balanceamento das B-Trees faz o Outlook rejeitar o " +
        "arquivo como corrompido. Um writer próprio incompleto produziria PST inválido — exatamente o que " +
        "este projeto se recusa a entregar.\n\n" +
        "Bibliotecas open-source de leitura usadas no mercado (libpff/pffexport, libpst/readpst) " +
        "NÃO escrevem PST. Um caminho confiável depende de um SDK comercial licenciado (ex.: Aspose.Email) " +
        "ou de um writer nativo maduro, a ser plugado nesta mesma interface quando licenciado.\n\n" +
        "Saída recomendada agora: EML ou MBOX (RFC 5322) — formatos abertos, previsíveis e imunes a falhas " +
        "estruturais secundárias. Use 'recover-eml' ou 'recover-mbox'.";

    public Task<PstExportOutcome> WriteAsync(
        IMailStoreReader reader,
        PstExportRequest request,
        IProgress<RecoveryExportProgress>? progress,
        CancellationToken ct)
    {
        return Task.FromResult(new PstExportOutcome(
            Supported: false,
            Success: false,
            OutputPath: null,
            ExportedMessages: 0,
            StatusCode: NotSupportedCode,
            Explanation: TechnicalExplanation,
            Issues: Array.Empty<RecoveryExportIssue>()));
    }
}
