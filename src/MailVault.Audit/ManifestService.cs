using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Domain;

namespace MailVault.Audit;

public static class ManifestService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GenerateCaseId(DateTimeOffset timestamp)
    {
        return $"CASE-{timestamp:yyyy-MM-dd-HHmmss}";
    }

    public static async Task<string> SaveManifestAsync(string baseOutputDirectory, RecoveryManifest manifest, CancellationToken ct)
    {
        // Create the directory structure: baseOutputDirectory/CASE-YYYY-MM-DD-HHMMSS/
        string caseDirectory = Path.Combine(baseOutputDirectory, manifest.CaseId);
        Directory.CreateDirectory(caseDirectory);

        string manifestFilePath = Path.Combine(caseDirectory, "manifest.json");
        using var stream = new FileStream(manifestFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, manifest, Options, ct).ConfigureAwait(false);

        return manifestFilePath;
    }
}
