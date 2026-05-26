using System;
using System.Collections.ObjectModel;
using MailVault.Domain;
using ReactiveUI;

namespace MailVault.Desktop.ViewModels;

public class MessagePreviewViewModel : ViewModelBase
{
    private string _subject = "Selecione uma mensagem para visualizar.";
    private string _from = "";
    private string _to = "";
    private string _cc = "";
    private string _bcc = "";
    private string _dates = "";
    private string _bodyPreview = "";
    private bool _hasMessage;

    public string Subject
    {
        get => _subject;
        set => this.RaiseAndSetIfChanged(ref _subject, value);
    }

    public string From
    {
        get => _from;
        set => this.RaiseAndSetIfChanged(ref _from, value);
    }

    public string To
    {
        get => _to;
        set => this.RaiseAndSetIfChanged(ref _to, value);
    }

    public string Cc
    {
        get => _cc;
        set => this.RaiseAndSetIfChanged(ref _cc, value);
    }

    public string Bcc
    {
        get => _bcc;
        set => this.RaiseAndSetIfChanged(ref _bcc, value);
    }

    public string Dates
    {
        get => _dates;
        set => this.RaiseAndSetIfChanged(ref _dates, value);
    }

    public string BodyPreview
    {
        get => _bodyPreview;
        set => this.RaiseAndSetIfChanged(ref _bodyPreview, value);
    }

    public bool HasMessage
    {
        get => _hasMessage;
        set => this.RaiseAndSetIfChanged(ref _hasMessage, value);
    }

    public ObservableCollection<string> Attachments { get; } = new();

    public void SetMessage(MailItem? msg)
    {
        Attachments.Clear();
        if (msg == null)
        {
            Subject = "Selecione uma mensagem para visualizar.";
            From = "";
            To = "";
            Cc = "";
            Bcc = "";
            Dates = "";
            BodyPreview = "";
            HasMessage = false;
            return;
        }

        Subject = msg.Subject ?? "(Sem Assunto)";
        From = msg.From != null ? $"{msg.From.Name ?? "(Sem Nome)"} <{msg.From.Address ?? "(Sem Endereço)"}>" : "(Sem Remetente)";
        
        var toList = new System.Collections.Generic.List<string>();
        foreach (var r in msg.To)
        {
            if (r.Address != null) toList.Add(r.Address);
        }
        To = string.Join(", ", toList);

        var ccList = new System.Collections.Generic.List<string>();
        foreach (var r in msg.Cc)
        {
            if (r.Address != null) ccList.Add(r.Address);
        }
        Cc = string.Join(", ", ccList);

        var bccList = new System.Collections.Generic.List<string>();
        foreach (var r in msg.Bcc)
        {
            if (r.Address != null) bccList.Add(r.Address);
        }
        Bcc = string.Join(", ", bccList);

        Dates = $"Enviado em: {msg.SentAt?.ToString("g") ?? "N/A"} | Recebido em: {msg.ReceivedAt?.ToString("g") ?? "N/A"}";

        string origBody = msg.PlainTextBody ?? "";
        if (origBody.Length > 400)
        {
            BodyPreview = origBody.Substring(0, 400) + "... [CONTEÚDO TRUNCADO POR SEGURANÇA E PRIVACIDADE FORENSE]";
        }
        else
        {
            BodyPreview = origBody;
        }

        foreach (var att in msg.Attachments)
        {
            Attachments.Add($"{att.FileName} ({((double)(att.SizeBytes ?? 0) / 1024):N2} KB)");
        }


        HasMessage = true;
    }
}
