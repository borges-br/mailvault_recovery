using System.Text;

namespace MailVault.Carving;

/// <summary>
/// Assinaturas físicas procuradas no 3c.1. Foco no marcador de classe de mensagem `IPM.Note`
/// em ASCII e UTF-16LE (em arquivos Encryption=None as strings ficam em claro). Conjunto
/// deliberadamente pequeno e preciso para o gate de viabilidade.
/// </summary>
internal static class CarveSignatures
{
    public static readonly byte[] IpmNoteAscii = Encoding.ASCII.GetBytes("IPM.Note");
    public static readonly byte[] IpmNoteUtf16 = Encoding.Unicode.GetBytes("IPM.Note"); // UTF-16LE, 16 bytes

    public const string KindIpmNote = "IPM.Note";

    // Maior assinatura (UTF-16LE = 16 bytes). O overlap entre chunks deve ser >= isto.
    public const int MaxSignatureLength = 16;
}
