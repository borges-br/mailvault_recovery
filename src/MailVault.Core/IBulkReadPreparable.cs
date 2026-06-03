using System.Threading;
using System.Threading.Tasks;

namespace MailVault.Core;

/// <summary>
/// Leitores que podem pré-construir índices internos para leitura em massa
/// (ex.: exportação de muitas mensagens), tornando GetMessageAsync O(1).
///
/// IMPORTANTE: NÃO chamar para leituras avulsas (ex.: preview de uma única
/// mensagem) — a construção do índice varre toda a árvore do PST/OST, o que
/// só compensa quando muitas mensagens serão lidas na mesma sessão.
/// </summary>
public interface IBulkReadPreparable
{
    Task PrepareBulkReadAsync(CancellationToken ct);
}
