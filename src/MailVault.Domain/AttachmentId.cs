namespace MailVault.Domain;

public sealed record AttachmentId(string Value)
{
    public override string ToString() => Value;
}
