using System.Collections.Generic;
using MailVault.Domain;

namespace MailVault.Core;

public sealed class OperationResult<T>
{
    public bool Success { get; }
    public T? Value { get; }
    public IReadOnlyList<ExtractionIssue> Issues { get; }

    private OperationResult(bool success, T? value, IReadOnlyList<ExtractionIssue> issues)
    {
        Success = success;
        Value = value;
        Issues = issues;
    }

    public static OperationResult<T> Ok(T value, IReadOnlyList<ExtractionIssue>? issues = null)
    {
        return new OperationResult<T>(true, value, issues ?? System.Array.Empty<ExtractionIssue>());
    }

    public static OperationResult<T> Failure(IReadOnlyList<ExtractionIssue> issues)
    {
        return new OperationResult<T>(false, default, issues);
    }

    public static OperationResult<T> Failure(ExtractionIssue issue)
    {
        return new OperationResult<T>(false, default, new[] { issue });
    }
}
