using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Core;
using MailVault.Domain;

namespace MailVault.Desktop.Services;

/// <summary>
/// Lê o conteúdo completo de UMA mensagem ao vivo da evidência original (OST/PST),
/// mantendo uma sessão de leitura aberta enquanto o caso estiver carregado. Usado
/// para o preview sob demanda no Navegador — o índice (case.db) guarda só metadados
/// leves; remetente/destinatários/corpo/anexos completos vêm daqui ao clicar.
///
/// Reusa o padrão já existente no Desktop (ReflectionAdapterResolver in-process,
/// como na "Recuperação Direta"). Leitura de UMA mensagem é barata o suficiente para
/// rodar no processo da UI (em thread de fundo), com try/catch — sem o overhead de
/// abrir um worker por clique.
/// </summary>
public sealed class DesktopMessagePreviewService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMailStoreReader? _reader;
    private string? _sourcePath;

    /// <summary>True quando há uma sessão de leitura aberta (fonte baseada em arquivo).</summary>
    public bool IsAvailable => _reader != null;

    public string? SourcePath => _sourcePath;

    /// <summary>
    /// Abre a sessão de leitura para a evidência informada. Hoje cobre fontes em
    /// arquivo (OST/PST). Retorna false (degrada para metadados do índice) se a fonte
    /// não existir, não for arquivo, ou não houver adapter.
    /// </summary>
    public async Task<bool> OpenAsync(string? sourcePath, CancellationToken ct)
    {
        await CloseAsync();

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return false;
        }

        try
        {
            var resolver = new ReflectionAdapterResolver();
            var res = resolver.ResolveAdapter(Path.GetExtension(sourcePath));
            if (!res.Success || res.Reader == null)
            {
                return false;
            }

            var reader = res.Reader;
            if (reader is IMetadataOnlyAware metadataAware)
            {
                metadataAware.MetadataOnly = false; // queremos corpo + destinatários completos
            }

            await reader.InspectAsync(sourcePath, ct);
            if (reader is ISessionAwareMailStoreReader session)
            {
                await session.BeginReadSessionAsync(sourcePath, ct);
            }

            _reader = reader;
            _sourcePath = sourcePath;
            return true;
        }
        catch
        {
            await CloseAsync();
            return false;
        }
    }

    /// <summary>
    /// Lê a mensagem completa por ID. Serializa chamadas (uma de cada vez) e roda em
    /// thread de fundo. Retorna null em falha (o chamador mantém os metadados do índice).
    /// </summary>
    public async Task<MailItem?> GetFullMessageAsync(string messageId, CancellationToken ct)
    {
        var reader = _reader;
        if (reader == null || string.IsNullOrEmpty(messageId))
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(async () =>
            {
                try
                {
                    var result = await reader.GetMessageAsync(new MessageId(messageId), ct);
                    return result.Success ? result.Value : null;
                }
                catch
                {
                    return null;
                }
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        var reader = _reader;
        _reader = null;
        _sourcePath = null;
        if (reader is ISessionAwareMailStoreReader session)
        {
            try { await session.EndReadSessionAsync(CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    public void Dispose()
    {
        try { CloseAsync().GetAwaiter().GetResult(); } catch { /* best-effort */ }
        _gate.Dispose();
    }
}
