using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MailVault.Desktop.Views;

public partial class NewCaseWizardView : UserControl
{
    private ListBox? _logsListBox;
    private INotifyCollectionChanged? _logsCollection;

    public NewCaseWizardView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void LogsListBox_AttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            _logsListBox = listBox;
            listBox.DataContextChanged += LogsListBox_DataContextChanged;
            SubscribeToItemsSource();
        }
    }

    public void LogsListBox_DetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromItemsSource();
        if (_logsListBox != null)
        {
            _logsListBox.DataContextChanged -= LogsListBox_DataContextChanged;
            _logsListBox = null;
        }
    }

    private void LogsListBox_DataContextChanged(object? sender, EventArgs e)
    {
        SubscribeToItemsSource();
    }

    private void SubscribeToItemsSource()
    {
        UnsubscribeFromItemsSource();

        if (_logsListBox?.ItemsSource is INotifyCollectionChanged observable)
        {
            _logsCollection = observable;
            _logsCollection.CollectionChanged += LogLines_CollectionChanged;

            // Proactively scroll to end if collection has existing elements
            if (_logsListBox.ItemsSource is System.Collections.IList list && list.Count > 0)
            {
                var lastItem = list[list.Count - 1];
                if (lastItem != null)
                {
                    _logsListBox.ScrollIntoView(lastItem);
                }
            }
        }
    }

    private void UnsubscribeFromItemsSource()
    {
        if (_logsCollection != null)
        {
            _logsCollection.CollectionChanged -= LogLines_CollectionChanged;
            _logsCollection = null;
        }
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_logsListBox != null && sender is System.Collections.ICollection collection && collection.Count > 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_logsListBox != null && sender is System.Collections.IList list && list.Count > 0)
                {
                    var lastItem = list[list.Count - 1];
                    if (lastItem != null)
                    {
                        _logsListBox.ScrollIntoView(lastItem);
                    }
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
