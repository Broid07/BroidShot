using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

/// <summary>Metin şablonları: stored snippets, palette commands that paste into the previously focused window.</summary>
public sealed class SnippetsModule : UtilityModuleBase
{
    private readonly IClipboardService _clipboard;
    private SnippetStore? _store;
    private ForegroundTracker? _tracker;

    public SnippetsModule() : this(SystemClipboard.Instance) { }

    internal SnippetsModule(IClipboardService clipboard) { _clipboard = clipboard; }

    public override string Id => "snippets";
    public override string Title => "Metin şablonları";
    public override string Description => "Sık kullandığın metinleri kaydet; komut paletinden seçip açık pencereye yapıştır.";
    public override string Icon => "";
    public override IReadOnlyList<string> Keywords { get; } = new[] { "snippet", "şablon", "sablon", "metin", "hazır", "yapıştır", "imza", "template", "paste" };

    internal SnippetStore Store => _store ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal event Action? Changed;

    /// <summary>Open command, "new" command and one "Snippet: &lt;name&gt;" command per saved snippet.</summary>
    public override IReadOnlyList<ToolCommand> Commands
    {
        get
        {
            var list = new List<ToolCommand>
            {
                ToolCommand.Open("Metin şablonlarını aç", Id, "Şablonları düzenle, ara, yeni şablon ekle", new[] { "snippet", "şablon" }, "snippets.open"),
            };
            if (_store is null) return list;
            foreach (Snippet snippet in _store.All)
            {
                string id = snippet.Id;
                list.Add(new ToolCommand("Snippet: " + snippet.Name, token => PasteAsync(id, token))
                {
                    Id = "snippets.paste." + id,
                    Description = Preview(snippet),
                    Keywords = new[] { snippet.Trigger, snippet.Folder, "snippet", "şablon", "yapıştır" }.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray()
                });
            }
            return list;
        }
    }

    public override void Initialize(IToolHost host)
    {
        base.Initialize(host);
        string path = Path.Combine(host.DataFolder, "snippets.json");
        bool first = !File.Exists(path);
        _store = new SnippetStore(path);
        if (first && _store.All.Count == 0)
            _store.Save(new Snippet { Name = "Örnek: tarih ve saat", Trigger = "tarihsaat", Folder = "Örnekler", Content = "{tarih} {saat}" }, out _);
        _tracker = new ForegroundTracker();
    }

    public override FrameworkElement CreateView() => new SnippetsView(this);

    public override void Shutdown()
    {
        try { _tracker?.Dispose(); } catch { }
        _tracker = null;
    }

    internal void NotifyChanged() => Changed?.Invoke();

    internal SnippetExpander CreateExpander(Func<string?>? clipboardText = null) =>
        new(() => DateTime.Now, clipboardText ?? (() => { try { return _clipboard.GetText(); } catch { return null; } }));

    internal static string Preview(Snippet snippet)
    {
        string oneLine = string.Join(" ", snippet.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (oneLine.Length > 70) oneLine = oneLine[..70] + "…";
        return string.IsNullOrEmpty(snippet.Folder) ? oneLine : snippet.Folder + " · " + oneLine;
    }

    internal void CopyExpanded(Snippet snippet)
    {
        try
        {
            string text = CreateExpander().Expand(snippet.Content);
            _clipboard.SetText(text);
            Toast("Şablon kopyalandı", snippet.Name, ToastKind.Success);
        }
        catch (Exception ex)
        {
            LogError("Şablon kopyalanamadı", ex);
            Toast("Kopyalanamadı", "Pano şu an kullanılamıyor.", ToastKind.Error);
        }
    }

    /// <summary>
    /// Expands the snippet, puts it on the clipboard, returns focus to the window the user was in and presses Ctrl+V there.
    /// When no other window can be found the text stays on the clipboard.
    /// </summary>
    internal async Task PasteAsync(string id, CancellationToken token)
    {
        Snippet? snippet = Store.Get(id);
        if (snippet is null) { Toast("Şablon bulunamadı", "Silinmiş olabilir.", ToastKind.Warning); return; }
        try
        {
            // {pano} must read the clipboard before it is overwritten with the result.
            string text = CreateExpander().Expand(snippet.Content);
            _clipboard.SetText(text);

            IntPtr target = await FindPasteTargetAsync(token);
            if (target == IntPtr.Zero)
            {
                Toast("Şablon panoya kopyalandı", snippet.Name + " — yapıştırmak için Ctrl+V kullan.", ToastKind.Info);
                return;
            }
            if (Native.GetForeground() != target && !Native.ForceForeground(target))
            {
                Toast("Şablon panoya kopyalandı", "Pencere öne getirilemedi; Ctrl+V ile yapıştır.", ToastKind.Warning);
                return;
            }
            await Task.Delay(70, token);
            if (!Native.SendPaste())
                Toast("Şablon panoya kopyalandı", "Yapıştırma gönderilemedi; Ctrl+V ile yapıştır.", ToastKind.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogError("Şablon yapıştırılamadı", ex);
            Toast("Yapıştırılamadı", ex.Message, ToastKind.Error);
        }
    }

    /// <summary>
    /// After the palette closes Windows normally re-activates the previous window; wait for that. If BroidShot's own window
    /// is the active one there is nothing sensible to paste into.
    /// </summary>
    private async Task<IntPtr> FindPasteTargetAsync(CancellationToken token)
    {
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(50, token);
            IntPtr foreground = Native.GetForeground();
            if (foreground != IntPtr.Zero && !Native.IsOwnProcess(foreground)) return foreground;
        }
        Window? main = Application.Current?.MainWindow;
        bool ownWindowVisible = main is { IsVisible: true } && main.WindowState != WindowState.Minimized;
        if (ownWindowVisible) return IntPtr.Zero;
        IntPtr last = _tracker?.LastExternal ?? IntPtr.Zero;
        return Native.IsUsableWindow(last) ? last : IntPtr.Zero;
    }
}
