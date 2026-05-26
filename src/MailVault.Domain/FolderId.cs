namespace MailVault.Domain;

public sealed record FolderId(string Value)
{
    public override string ToString() => Value;
}
