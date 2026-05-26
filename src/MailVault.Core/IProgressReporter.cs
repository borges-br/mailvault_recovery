namespace MailVault.Core;

public interface IProgressReporter
{
    void ReportProgress(double percentage, string status);
}
