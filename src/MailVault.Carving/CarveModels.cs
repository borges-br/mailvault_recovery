using System.Collections.Generic;

namespace MailVault.Carving;

/// <summary>Limites e opções do carver. Tudo bounded — o scanner nunca carrega o arquivo inteiro.</summary>
public sealed record CarveOptions(
    long MaxScanBytes = 0,               // 0 = arquivo inteiro
    int MaxCandidates = 200_000,
    int MaxCandidatesPerMb = 0,          // 0 = ilimitado; >0 aborta se densidade indicar lixo
    int ChunkSizeBytes = 4 * 1024 * 1024,
    int OverlapBytes = 64 * 1024,
    double? TimeoutSeconds = null,
    bool NoPreviews = false,
    int MaxPreviewBytes = 80);

/// <summary>Um sinal físico encontrado (não é uma mensagem — é evidência bruta num offset).</summary>
public sealed record CarveCandidate(
    long Offset,
    string Kind,        // ex.: "IPM.Note"
    string Encoding,    // "ascii" | "utf16le"
    int Confidence,     // 0-100 (heurístico)
    string? Preview);   // trecho curto sanitizado (se previews habilitados)

public enum CarveStatus
{
    Completed,
    StoppedByCandidateLimit,
    StoppedByScanLimit,
    StoppedByDensityLimit,
    Timeout,
    Failed
}

/// <summary>Resultado do scan de artefatos (Camada A+B). Report-only no 3c.1 — sem EML.</summary>
public sealed record CarveResult(
    string SourcePath,
    long FileSizeBytes,
    long BytesScanned,
    bool HeaderIsPff,
    string HeaderSummary,
    int TotalCandidates,
    IReadOnlyDictionary<string, int> CandidatesByKind,
    IReadOnlyList<CarveCandidate> Candidates,
    double ElapsedSeconds,
    CarveStatus Status,
    IReadOnlyList<string> Notes);
