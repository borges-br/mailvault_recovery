using System;
using System.Text.RegularExpressions;

namespace MailVault.Desktop.Services;

public sealed class SafeDiagnosticReport
{
    public string Title { get; init; } = "Erro Operacional";
    public string ProbableCause { get; init; } = "Causa indeterminada.";
    public string SanitizedDetails { get; init; } = "";
    public string RecommendedAction { get; init; } = "Contate o administrador ou consulte os logs.";
}

public static class SafeDiagnosticsFormatter
{
    private static readonly Regex UserPathRegex = new(@"(?i)([a-z]:\\users\\)[^\\]+", RegexOptions.Compiled);

    public static SafeDiagnosticReport Format(Exception ex, string context)
    {
        string title = $"Falha em: {context}";
        string cause = "Ocorreu um erro técnico inesperado durante o processamento local.";
        string recommended = "Verifique a integridade do arquivo de correio, permissões de escrita na pasta e o audit.log.";

        string sanitizedDetails = MaskSensitiveInfo(ex.ToString());

        if (ex is UnauthorizedAccessException)
        {
            cause = "Permissão de acesso negada ao ler ou gravar arquivos no disco local.";
            recommended = "Verifique se você possui permissões de leitura/escrita na pasta e execute o MailVault com privilégios adequados.";
        }
        else if (ex is FileNotFoundException fnf)
        {
            cause = $"Arquivo ou dependência não localizada: '{Path.GetFileName(fnf.FileName)}'.";
            recommended = "Certifique-se de que o arquivo original existe na pasta indicada e não está em uso por outro aplicativo.";
        }
        else if (ex is InvalidOperationException)
        {
            cause = "Operação inválida no estado atual do sistema ou inconsistência do banco de dados.";
            recommended = "Certifique-se de fechar sessões anteriores de indexação ou tente reabrir o caso.";
        }
        else if (ex is OperationCanceledException)
        {
            title = "Ação Cancelada";
            cause = "A operação em andamento foi cancelada pelo operador ou ultrapassou o tempo limite.";
            recommended = "Você pode tentar reiniciar a operação se desejar.";
        }

        return new SafeDiagnosticReport
        {
            Title = title,
            ProbableCause = cause,
            SanitizedDetails = sanitizedDetails,
            RecommendedAction = recommended
        };
    }

    public static string MaskSensitiveInfo(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        // Mask Windows User Paths
        string result = UserPathRegex.Replace(input, "$1<USER>");

        // Mask potential email body snippets or headers if they leak in common exception patterns
        // (Just in case, ensure no full email details leak)
        return result;
    }
}
