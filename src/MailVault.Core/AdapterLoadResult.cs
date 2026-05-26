using System;

namespace MailVault.Core;

public record AdapterLoadResult(
    bool Success,
    IMailStoreReader? Reader,
    string? ErrorMessage,
    Exception? Exception
);
