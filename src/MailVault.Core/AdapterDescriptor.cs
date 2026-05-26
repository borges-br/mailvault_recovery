using System.Collections.Generic;

namespace MailVault.Core;

public record AdapterDescriptor(
    string Name,
    string Version,
    string AssemblyPath,
    IReadOnlyList<string> SupportedExtensions,
    int Priority,
    string HealthStatus
);
