namespace MailVault.Domain;

public sealed record RawMapiProperty(
    string TagOrName,
    string Value
);
