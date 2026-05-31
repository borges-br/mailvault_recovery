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
    int MaxPreviewBytes = 80,
    // --- 3c.2/3c.3: classificação + exportação parcial ---
    bool Export = false,                 // OFF por padrão: report-only. --export habilita EML parcial.
    int MinConfidence = 50,              // score mínimo para classificar como Mail (exporta em Partial/)
    bool ExportOrphans = true,           // exporta cluster Orphan (score 20..min) em Orphaned Items/
    int PreWindowBytes = 8 * 1024,       // janela física antes do marcador
    int PostWindowBytes = 24 * 1024,     // janela física depois do marcador
    int MaxBodyChars = 64 * 1024);

/// <summary>Sinal físico bruto (Camada A+B / 3c.1) — um marcador num offset, não uma mensagem.</summary>
public sealed record CarveCandidate(
    long Offset,
    string Kind,        // ex.: "IPM.Note"
    string Encoding,    // "ascii" | "utf16le"
    int Confidence,
    string? Preview);

public enum CarveStatus
{
    Completed,
    StoppedByCandidateLimit,
    StoppedByScanLimit,
    StoppedByDensityLimit,
    Timeout,
    Failed
}

/// <summary>Resultado do scan bruto (Camada A+B). Phase 1 — apenas sinais físicos.</summary>
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

/// <summary>Classificação de um cluster após extração de campos (Camada C+D / 3c.2).</summary>
public enum CarveClass
{
    Mail,        // evidência suficiente → exporta em Partial/
    Orphan,      // alguma evidência, insuficiente → Orphaned Items/ (claramente parcial)
    System,      // item interno do OST (denylist) → NÃO é e-mail, descartado
    LocateOnly   // só o marcador, sem campos legíveis → só consta no relatório
}

/// <summary>Candidato classificado com campos extraídos da janela física (3c.2).</summary>
public sealed record ClassifiedCandidate(
    long Offset,
    string Encoding,
    CarveClass Classification,
    int Score,
    string? Subject,
    string? FromEmail,
    string? ToEmail,
    string? DateText,
    string? BodySnippet,
    string Reason,
    string? ExportedRelativePath);

/// <summary>Resultado do pipeline completo de carving (scan + classificação + exportação opcional).</summary>
public sealed record CarvePipelineResult(
    string SourcePath,
    long FileSizeBytes,
    long BytesScanned,
    bool HeaderIsPff,
    string HeaderSummary,
    int TotalCandidates,
    IReadOnlyDictionary<string, int> CandidatesByKind,
    IReadOnlyDictionary<string, int> ClassificationCounts,
    int ExportedCount,
    bool ExportEnabled,
    IReadOnlyList<ClassifiedCandidate> Candidates,
    double ElapsedSeconds,
    CarveStatus Status,
    IReadOnlyList<string> Notes);
