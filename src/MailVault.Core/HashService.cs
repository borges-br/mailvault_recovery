using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

public sealed class HashService : IHashService
{
    public async Task<string> CalculateSha256Async(string filePath, IProgressReporter? progress, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo não encontrado para cálculo de hash.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        using var sha256 = SHA256.Create();

        long totalBytes = stream.Length;
        long totalBytesRead = 0;
        byte[] buffer = new byte[81920]; // 80 KB standard block size
        int bytesRead;

        progress?.ReportProgress(0.0, "Iniciando cálculo de hash SHA-256...");

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            totalBytesRead += bytesRead;

            if (totalBytes > 0)
            {
                double pct = (double)totalBytesRead / totalBytes * 100.0;
                progress?.ReportProgress(pct, $"Calculando hash SHA-256: {pct:F1}% concluído.");
            }
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        
        byte[]? hashBytes = sha256.Hash;
        if (hashBytes == null)
        {
            throw new InvalidOperationException("Falha ao calcular hash SHA-256.");
        }

        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        progress?.ReportProgress(100.0, $"Cálculo de hash concluído com sucesso: {hashHex}");

        return hashHex;
    }
}
