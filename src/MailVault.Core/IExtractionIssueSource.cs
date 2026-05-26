using System.Collections.Generic;
using MailVault.Domain;

namespace MailVault.Core;

public interface IExtractionIssueSource
{
    IReadOnlyList<ExtractionIssue> DrainIssues();
}
