using System;
using System.Collections.Generic;
using System.Linq;
using MailVault.Domain;

namespace MailVault.Core.Normalization;

public static class RecipientNormalizer
{
    public static string NormalizeSingle(MailAddressRef? addr)
    {
        if (addr == null) return "N/A";
        string name = addr.Name?.Trim() ?? string.Empty;
        string email = addr.Address?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(email))
        {
            return $"{name} <{email}>";
        }
        return !string.IsNullOrEmpty(name) ? name : (!string.IsNullOrEmpty(email) ? email : "N/A");
    }

    public static string NormalizeList(IEnumerable<MailAddressRef>? list)
    {
        if (list == null) return string.Empty;
        var normalized = list.Select(NormalizeSingle).Where(s => s != "N/A" && !string.IsNullOrEmpty(s));
        return string.Join("; ", normalized);
    }
}
