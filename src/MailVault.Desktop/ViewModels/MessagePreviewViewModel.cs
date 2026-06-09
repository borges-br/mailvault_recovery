using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailVault.Desktop.Services;
using MailVault.Domain;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class MessagePreviewViewModel : ViewModelBase
{
    private const int MaxBodyChars = 100_000;

    private string _subject = "Selecione uma mensagem para visualizar.";
    private string _from = "";
    private string _to = "";
    private string _cc = "";
    private string _bcc = "";
    private string _dates = "";
    private string _bodyPreview = "";
    private bool _hasMessage;
    private bool _isLoadingFull;
    private string _liveError = "";

    private DesktopMessagePreviewService? _previewService;
    private CancellationTokenSource? _loadCts;

    public string Subject { get => _subject; set => this.RaiseAndSetIfChanged(ref _subject, value); }
    public string From { get => _from; set => this.RaiseAndSetIfChanged(ref _from, value); }
    public string To { get => _to; set => this.RaiseAndSetIfChanged(ref _to, value); }
    public string Cc { get => _cc; set => this.RaiseAndSetIfChanged(ref _cc, value); }
    public string Bcc { get => _bcc; set => this.RaiseAndSetIfChanged(ref _bcc, value); }
    public string Dates { get => _dates; set => this.RaiseAndSetIfChanged(ref _dates, value); }
    public string BodyPreview { get => _bodyPreview; set => this.RaiseAndSetIfChanged(ref _bodyPreview, value); }
    public bool HasMessage { get => _hasMessage; set => this.RaiseAndSetIfChanged(ref _hasMessage, value); }

    /// <summary>Indica que o conteúdo completo está sendo lido da evidência ao vivo.</summary>
    public bool IsLoadingFull
    {
        get => _isLoadingFull;
        set => this.RaiseAndSetIfChanged(ref _isLoadingFull, value);
    }

    public string LiveError
    {
        get => _liveError;
        set
        {
            this.RaiseAndSetIfChanged(ref _liveError, value);
            this.RaisePropertyChanged(nameof(HasLiveError));
        }
    }

    public bool HasLiveError => !string.IsNullOrEmpty(_liveError);

    public ObservableCollection<string> Attachments { get; } = new();

    /// <summary>
    /// Liga/desliga o leitor ao vivo. Quando há sessão aberta, o preview busca o
    /// conteúdo completo (remetente/destinatários/corpo/anexos) ao selecionar a mensagem.
    /// </summary>
    public void AttachPreviewService(DesktopMessagePreviewService? service)
    {
        _previewService = service;
    }

    public void SetMessage(MailItem? indexedMsg)
    {
        // Cancela qualquer carregamento anterior (seleção mais nova vence).
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        Attachments.Clear();
        LiveError = "";

        if (indexedMsg == null)
        {
            Subject = "Selecione uma mensagem para visualizar.";
            From = To = Cc = Bcc = Dates = BodyPreview = "";
            HasMessage = false;
            IsLoadingFull = false;
            return;
        }

        // 1. Metadados do índice — exibidos imediatamente.
        Subject = indexedMsg.Subject ?? "(Sem Assunto)";
        From = FormatFrom(indexedMsg.From);
        To = FormatAddresses(indexedMsg.To);
        Cc = FormatAddresses(indexedMsg.Cc);
        Bcc = FormatAddresses(indexedMsg.Bcc);
        Dates = FormatDates(indexedMsg);
        foreach (var att in indexedMsg.Attachments) Attachments.Add(FormatAttachment(att));
        HasMessage = true;

        // 2. Conteúdo completo ao vivo (se houver sessão de leitura aberta).
        if (_previewService is { IsAvailable: true } service && !string.IsNullOrEmpty(indexedMsg.InternalId))
        {
            IsLoadingFull = true;
            BodyPreview = "Lendo conteúdo completo da evidência original...";
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            _ = LoadFullAsync(service, indexedMsg, cts.Token);
        }
        else
        {
            IsLoadingFull = false;
            BodyPreview = BuildBody(indexedMsg);
        }
    }

    private async Task LoadFullAsync(DesktopMessagePreviewService service, MailItem indexedMsg, CancellationToken ct)
    {
        try
        {
            var full = await service.GetFullMessageAsync(indexedMsg.InternalId, ct);
            if (ct.IsCancellationRequested) return;

            if (full != null)
            {
                From = FormatFrom(full.From);
                To = FormatAddresses(full.To);
                Cc = FormatAddresses(full.Cc);
                Bcc = FormatAddresses(full.Bcc);
                BodyPreview = BuildBody(full);

                Attachments.Clear();
                foreach (var att in full.Attachments) Attachments.Add(FormatAttachment(att));
            }
            else
            {
                LiveError = "Não foi possível ler o conteúdo completo da evidência; exibindo apenas metadados do índice.";
                BodyPreview = BuildBody(indexedMsg);
            }
        }
        catch (OperationCanceledException)
        {
            // Substituída por uma seleção mais recente.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                LiveError = $"Erro ao ler conteúdo da evidência: {ex.Message}";
                BodyPreview = BuildBody(indexedMsg);
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingFull = false;
            }
        }
    }

    private static string FormatFrom(MailAddressRef? from)
    {
        if (from == null) return "(Sem Remetente)";
        return $"{from.Name ?? "(Sem Nome)"} <{from.Address ?? "(Sem Endereço)"}>";
    }

    private static string FormatAddresses(IEnumerable<MailAddressRef> addrs)
    {
        return string.Join(", ", addrs
            .Select(r => string.IsNullOrWhiteSpace(r.Address) ? r.Name : r.Address)
            .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string FormatDates(MailItem msg)
        => $"Enviado em: {msg.SentAt?.ToString("g") ?? "N/A"} | Recebido em: {msg.ReceivedAt?.ToString("g") ?? "N/A"}";

    private static string FormatAttachment(AttachmentRef att)
        => $"{att.FileName ?? "(anexo)"} ({((double)(att.SizeBytes ?? 0) / 1024):N2} KB)";

    private static string BuildBody(MailItem msg)
    {
        string body;
        if (!string.IsNullOrEmpty(msg.PlainTextBody))
        {
            body = msg.PlainTextBody;
        }
        else if (!string.IsNullOrEmpty(msg.HtmlBody))
        {
            body = HtmlToText(msg.HtmlBody);
        }
        else
        {
            return "(Mensagem sem corpo de texto recuperável. Use a exportação para obter o conteúdo MIME completo.)";
        }

        if (body.Length > MaxBodyChars)
        {
            body = body.Substring(0, MaxBodyChars) + "\n\n[...] (corpo truncado na visualização; use a exportação para o conteúdo completo.)";
        }
        return body;
    }

    private static string HtmlToText(string html)
    {
        // Conversão simples HTML→texto para a visualização (o EML exportado preserva o HTML original).
        string text = Regex.Replace(html, "(?is)<(script|style).*?</\\1>", string.Empty);
        text = Regex.Replace(text, "(?i)<br\\s*/?>", "\n");
        text = Regex.Replace(text, "(?i)</p>", "\n\n");
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \\t]+", " ");
        text = Regex.Replace(text, "\\n{3,}", "\n\n");
        return text.Trim();
    }
}
