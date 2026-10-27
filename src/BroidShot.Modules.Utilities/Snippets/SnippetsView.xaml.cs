using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BroidShot.Modules.Utilities;

public partial class SnippetsView : UserControl
{
    private const string AllFolders = "Tüm klasörler";
    private readonly SnippetsModule _module;
    private string? _editingId;
    private bool _loading;

    internal SnippetsView(SnippetsModule module)
    {
        _module = module;
        InitializeComponent();
        _module.Changed += () => Reload(null);
        Reload(null);
        NewSnippet(this, new RoutedEventArgs());
    }

    private sealed record Row(Snippet Snippet)
    {
        public string Name => Snippet.Name;
        public string Subtitle => SnippetsModule.Preview(Snippet);
        public string Trigger => Snippet.Trigger.Trim();
    }

    private void Reload(string? selectId)
    {
        _loading = true;
        string? folder = FolderFilter.SelectedItem as string;
        var folders = new List<string> { AllFolders };
        folders.AddRange(_module.Store.Folders);
        FolderFilter.ItemsSource = folders;
        FolderFilter.SelectedItem = folder is not null && folders.Contains(folder) ? folder : AllFolders;
        string? activeFolder = FolderFilter.SelectedItem as string is { } f && f != AllFolders ? f : null;
        List<Row> rows = _module.Store.Search(SearchBox.Text, activeFolder).Select(s => new Row(s)).ToList();
        List.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        string? wanted = selectId ?? _editingId;
        if (wanted is not null && rows.FirstOrDefault(r => r.Snippet.Id == wanted) is { } row) List.SelectedItem = row;
        _loading = false;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!_loading && IsLoaded) Reload(null); }

    private void FolderChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) Reload(null); }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || List.SelectedItem is not Row row) return;
        Load(row.Snippet);
    }

    private void Load(Snippet snippet)
    {
        _editingId = snippet.Id;
        EditorTitle.Text = "Şablonu düzenle";
        NameBox.Text = snippet.Name;
        TriggerBox.Text = snippet.Trigger;
        FolderBox.Text = snippet.Folder;
        ContentBox.Text = snippet.Content;
        StatusText.Text = "";
        DeleteButton.IsEnabled = true;
        UpdatePreview();
    }

    private void NewSnippet(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        _loading = true;
        List.SelectedItem = null;
        _loading = false;
        EditorTitle.Text = "Yeni şablon";
        NameBox.Text = TriggerBox.Text = ContentBox.Text = "";
        FolderBox.Text = FolderFilter.SelectedItem as string is { } f && f != AllFolders ? f : "";
        StatusText.Text = "";
        DeleteButton.IsEnabled = false;
        UpdatePreview();
    }

    private Snippet ReadForm() => new()
    {
        Id = _editingId ?? Guid.NewGuid().ToString("N"),
        Name = NameBox.Text, Trigger = TriggerBox.Text, Folder = FolderBox.Text, Content = ContentBox.Text
    };

    private void SaveSnippet(object sender, RoutedEventArgs e)
    {
        Snippet snippet = ReadForm();
        if (!_module.Store.Save(snippet, out string? error)) { StatusText.Text = error ?? "Kaydedilemedi."; return; }
        _editingId = snippet.Id;
        StatusText.Text = "";
        EditorTitle.Text = "Şablonu düzenle";
        DeleteButton.IsEnabled = true;
        Reload(snippet.Id);
        _module.NotifyChanged();
    }

    private void DeleteSnippet(object sender, RoutedEventArgs e)
    {
        if (_editingId is null) return;
        Snippet? existing = _module.Store.Get(_editingId);
        if (existing is null) return;
        if (!Ui.Confirm(this, "Şablonu sil", $"“{existing.Name}” şablonu silinsin mi?")) return;
        _module.Store.Delete(_editingId);
        NewSnippet(this, new RoutedEventArgs());
        Reload(null);
        _module.NotifyChanged();
    }

    private void CopySnippet(object sender, RoutedEventArgs e)
    {
        Snippet snippet = ReadForm();
        if (string.IsNullOrEmpty(snippet.Content)) { StatusText.Text = "Kopyalanacak içerik yok."; return; }
        if (string.IsNullOrWhiteSpace(snippet.Name)) snippet.Name = "Şablon";
        _module.CopyExpanded(snippet);
    }

    private void InsertToken(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        int caret = ContentBox.CaretIndex;
        ContentBox.Text = ContentBox.Text.Insert(caret, token);
        ContentBox.CaretIndex = caret + token.Length;
        ContentBox.Focus();
    }

    private void ContentChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        try { PreviewText.Text = _module.CreateExpander().Expand(ContentBox.Text); }
        catch { PreviewText.Text = ContentBox.Text; }
    }
}
