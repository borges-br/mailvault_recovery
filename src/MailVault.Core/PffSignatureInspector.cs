using System;
using System.IO;
using System.Text;

namespace MailVault.Core;

/// <summary>
/// Resultado da inspeção de assinatura física do cabeçalho PFF (PST/OST/PAB).
/// Todos os campos derivam da leitura dos primeiros bytes do arquivo conforme [MS-PST],
/// validados empiricamente contra OST reais (wVer moderno = 36).
/// </summary>
public sealed record PffSignatureInfo(
    bool IsPff,
    string FormatFamily,
    string Architecture,
    int RawVersion,
    string MagicClient,
    string Encryption,
    int RawEncryptionByte,
    string Notes);

/// <summary>
/// Leitor de assinatura física de arquivos PFF (Personal Folder File: PST/OST/PAB).
/// Lê somente o cabeçalho (read-only, não modifica o arquivo) para detectar:
///   - assinatura mágica !BDN (autoridade primária de "é um PFF");
///   - arquitetura ANSI (32-bit, wVer 14/15) vs Unicode (64-bit, wVer &gt;= 23, inclui 36/37 modernos);
///   - método de ofuscação de blocos (None / Compressible / High), best-effort por offset [MS-PST].
/// </summary>
public static class PffSignatureInspector
{
    private static readonly byte[] PffMagic = { 0x21, 0x42, 0x44, 0x4E }; // "!BDN"

    private const int AnsiCryptOffset = 0x1CD;    // 461
    private const int UnicodeCryptOffset = 0x201; // 513
    private const int HeaderReadLength = 600;

    public static PffSignatureInfo Inspect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return NotPff("Caminho de arquivo vazio.", filePath);

        if (!File.Exists(filePath))
            return NotPff("Arquivo não encontrado.", filePath);

        byte[] buffer;
        int read;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            buffer = new byte[HeaderReadLength];
            read = fs.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            return NotPff($"Falha ao ler cabeçalho: {ex.Message}", filePath);
        }

        return Analyze(buffer, read, GetExtension(filePath));
    }

    public static PffSignatureInfo Inspect(Stream stream, string? extensionHint = null)
    {
        if (stream == null) return NotPff("Stream nulo.", extensionHint);
        var buffer = new byte[HeaderReadLength];
        int read = stream.Read(buffer, 0, buffer.Length);
        return Analyze(buffer, read, NormalizeExtension(extensionHint));
    }

    private static PffSignatureInfo Analyze(byte[] buffer, int read, string ext)
    {
        if (read < 16)
            return NotPff($"Arquivo curto demais ({read} bytes) para conter cabeçalho PFF.", ext);

        bool isPff = HasMagic(buffer);
        if (!isPff)
        {
            return new PffSignatureInfo(
                IsPff: false,
                FormatFamily: ext == ".ost" ? "OST?" : ext == ".pst" ? "PST?" : "Não-PFF",
                Architecture: "Desconhecida",
                RawVersion: -1,
                MagicClient: ReadMagicClient(buffer),
                Encryption: "N/A",
                RawEncryptionByte: -1,
                Notes: "Assinatura mágica !BDN ausente nos primeiros 4 bytes. " +
                       "O arquivo não é um PFF íntegro OU teve o cabeçalho sobrescrito/corrompido. " +
                       "Recuperação ainda pode ser viável por carving (não implementado nesta build).");
        }

        int wVer = BitConverter.ToUInt16(buffer, 10);
        string magicClient = ReadMagicClient(buffer);

        bool isAnsi = wVer is 14 or 15;
        bool isUnicode = wVer >= 23;
        string arch = isAnsi ? "ANSI (32-bit, legado)"
                    : isUnicode ? "Unicode (64-bit, moderno)"
                    : "Indeterminada";

        string family = ext switch
        {
            ".ost" => "OST (Offline Outlook Data)",
            ".pst" => "PST (Personal Storage Table)",
            ".pab" => "PAB (Personal Address Book)",
            _ => "PFF (Personal Folder File)"
        };

        int cryptOffset = isAnsi ? AnsiCryptOffset : UnicodeCryptOffset;
        int rawCrypt = -1;
        string encryption = "Desconhecida";
        if (isAnsi || isUnicode)
        {
            if (read > cryptOffset)
            {
                rawCrypt = buffer[cryptOffset];
                encryption = rawCrypt switch
                {
                    0x00 => "None (sem ofuscação)",
                    0x01 => "Compressible (NDB_CRYPT_PERMUTE)",
                    0x02 => "High (NDB_CRYPT_CYCLIC)",
                    _ => $"Desconhecida (byte 0x{rawCrypt:X2})"
                };
            }
            else
            {
                encryption = "Indeterminada (cabeçalho incompleto)";
            }
        }

        var notes = new StringBuilder();
        notes.Append("Assinatura !BDN confirmada. ");
        if (!isAnsi && !isUnicode)
            notes.Append($"wVer={wVer} fora das faixas conhecidas (ANSI 14/15, Unicode>=23); arquitetura indeterminada. ");
        notes.Append("Método de ofuscação lido por offset [MS-PST] (best-effort; pode variar em revisões recentes do formato).");

        return new PffSignatureInfo(
            IsPff: true,
            FormatFamily: family,
            Architecture: arch,
            RawVersion: wVer,
            MagicClient: magicClient,
            Encryption: encryption,
            RawEncryptionByte: rawCrypt,
            Notes: notes.ToString());
    }

    private static bool HasMagic(byte[] buffer)
    {
        for (int i = 0; i < PffMagic.Length; i++)
            if (buffer[i] != PffMagic[i]) return false;
        return true;
    }

    private static string ReadMagicClient(byte[] buffer)
    {
        if (buffer.Length < 10) return "?";
        var sb = new StringBuilder(2);
        foreach (var b in new[] { buffer[8], buffer[9] })
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        return sb.ToString();
    }

    private static PffSignatureInfo NotPff(string note, string? ext) => new(
        IsPff: false,
        FormatFamily: "Desconhecido",
        Architecture: "Desconhecida",
        RawVersion: -1,
        MagicClient: "?",
        Encryption: "N/A",
        RawEncryptionByte: -1,
        Notes: note);

    private static string GetExtension(string filePath)
        => NormalizeExtension(Path.GetExtension(filePath));

    private static string NormalizeExtension(string? ext)
        => string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
}
