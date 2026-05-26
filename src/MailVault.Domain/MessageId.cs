namespace MailVault.Domain;

public sealed record MessageId(string Value)
{
    public override string ToString() => Value;
}
