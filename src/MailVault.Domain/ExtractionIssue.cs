namespace MailVault.Domain;

public sealed record ExtractionIssue(
    string Code,
    string Severity, // e.g. "Warning", "Error", "Info"
    string Message,
    string? ObjectId,
    string? TechnicalDetails
)
{
    public static class Codes
    {
        public const string ReaderStallDetected = "MV-ERR-STALL";
        public const string ReaderKilledByUser = "MV-ERR-KILLED-USER";
        public const string ReaderKilledByWatchdog = "MV-ERR-KILLED-WATCHDOG";
        public const string FolderEnumerationTimeout = "MV-ERR-FOLDER-TIMEOUT";
        public const string MessageEnumerationTimeout = "MV-ERR-MSG-TIMEOUT";
        public const string AdapterException = "MV-ERR-ADAPTER-EXCEPTION";
        public const string UnsupportedModernOstFeature = "MV-ERR-UNSUPPORTED-FEATURE";
        public const string PotentialCorruption = "MV-ERR-CORRUPTION";
        public const string ProtocolError = "MV-ERR-PROTOCOL-ERROR";
    }
}
