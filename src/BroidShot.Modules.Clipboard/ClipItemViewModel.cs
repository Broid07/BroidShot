using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BroidShot.Modules.Clipboard;

/// <summary>One row in the page list or the quick panel. Sensitive text stays masked until <see cref="Reveal"/>.</summary>
internal sealed class ClipItemViewModel : INotifyPropertyChanged
{
    private readonly ClipboardModule _module;
    private ClipEntry _entry;
    private string _tagNames = "";
    private string? _revealedText;
    private BitmapSource? _thumbnail;
    private bool _thumbnailRequested;

    public ClipItemViewModel(ClipboardModule module, ClipEntry entry, string tagNames)
    {
        _module = module;
        _entry = entry;
        _tagNames = tagNames;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClipEntry Entry => _entry;
    public string TagNames => _tagNames;
    public long Id => _entry.Id;
    public bool IsSensitive => _entry.Sensitive;
    public bool IsPinned => _entry.Pinned;
    public bool IsRevealed => _revealedText is not null;
    public string KindGlyph => _entry.Sensitive ? "" : ClipClassifier.KindGlyph(_entry.Kind);
    public string PinGlyph => _entry.Pinned ? "" : "";
    public string PinToolTip => _entry.Pinned ? "Sabitlemeyi kaldır" : "Sabitle";
    public string RevealGlyph => IsRevealed ? "" : "";
    public string RevealToolTip => IsRevealed ? "Gizle" : "Göster";

    public bool ShowImage => _entry.Kind == ClipKind.Image && (!_entry.Sensitive || IsRevealed);
    public bool ShowText => !ShowImage;

    public string PreviewText
    {
        get
        {
            if (_entry.Sensitive)
            {
                if (_entry.Kind == ClipKind.Image) return IsRevealed ? "" : "Görsel " + SensitiveDetector.Mask(null);
                return _revealedText ?? _entry.Preview;
            }
            return _entry.Kind == ClipKind.Image ? _entry.Preview : OneParagraph(_entry.Preview);
        }
    }

    public string MetaText
    {
        get
        {
            string kind = ClipClassifier.KindLabel(_entry.Kind) + (_entry.IsVideoLink ? " · video" : "");
            string text = kind + " · " + RelativeTime(_entry.LastUsedAt, _module.Time.GetUtcNow());
            if (!string.IsNullOrEmpty(_entry.SourceApp)) text += " · " + _entry.SourceApp;
            if (_tagNames.Length > 0) text += " · #" + _tagNames;
            return text;
        }
    }

    /// <summary>Type label for the card ("Bağlantı · video").</summary>
    public string KindText => ClipClassifier.KindLabel(_entry.Kind) + (_entry.IsVideoLink ? " · video" : "");

    /// <summary>Relative time and source app, without the type and tags ("5 dk önce · chrome").</summary>
    public string DetailText
    {
        get
        {
            string text = RelativeTime(_entry.LastUsedAt, _module.Time.GetUtcNow());
            return string.IsNullOrEmpty(_entry.SourceApp) ? text : text + " · " + _entry.SourceApp;
        }
    }

    /// <summary>Tag names of the item, for chips.</summary>
    public IReadOnlyList<string> Tags => _tagNames.Length == 0 ? Array.Empty<string>() : _tagNames.Split(" #");
    public bool HasTags => _tagNames.Length > 0;

    /// <summary>True while the content is replaced by the mask.</summary>
    public bool IsMasked => _entry.Sensitive && !IsRevealed;

    public string SensitiveReasonText => _entry.Sensitive ? SensitiveDetector.ReasonLabel(_entry.Reason) : "";

    /// <summary>Countdown until the sensitive item is deleted ("52 dk sonra silinecek"); empty without an expiry.</summary>
    public string ExpiryText
    {
        get
        {
            if (!_entry.Sensitive || _entry.ExpiresAt is not { } at) return "";
            TimeSpan left = at - _module.Time.GetUtcNow();
            return left <= TimeSpan.Zero ? "birazdan silinecek" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} dk sonra silinecek";
        }
    }

    public bool HasExpiry => ExpiryText.Length > 0;

    private string? _sectionTitle;

    /// <summary>Group header drawn above the first item of a section on the page ("Sabitlenenler"); null for other rows.</summary>
    public string? SectionTitle
    {
        get => _sectionTitle;
        set
        {
            if (_sectionTitle == value) return;
            _sectionTitle = value;
            Raise();
            Raise(nameof(HasSectionTitle));
        }
    }

    public bool HasSectionTitle => _sectionTitle is not null;

    public string SensitiveNote
    {
        get
        {
            if (!_entry.Sensitive) return "";
            string reason = SensitiveDetector.ReasonLabel(_entry.Reason);
            if (_entry.ExpiresAt is not { } at) return "Hassas · " + reason;
            TimeSpan left = at - _module.Time.GetUtcNow();
            string when = left <= TimeSpan.Zero ? "birazdan silinecek" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} dk sonra silinecek";
            return $"Hassas · {reason} · {when}";
        }
    }

    public bool HasSwatch => Swatch is not null;

    public Brush? Swatch
    {
        get
        {
            if (_entry.Kind != ClipKind.Color || _entry.Sensitive) return null;
            string? text = _entry.Payload?.Text?.Trim();
            if (string.IsNullOrEmpty(text) || !text.StartsWith('#')) return null;
            try
            {
                string hex = text.Length is 4 or 5 ? "#" + string.Concat(text[1..].Select(c => new string(c, 2))) : text;
                if (hex.Length == 9) hex = "#" + hex[7..9] + hex[1..7]; // CSS #RRGGBBAA → WPF #AARRGGBB
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
            catch (FormatException) { return null; }
        }
    }

    /// <summary>Thumbnail, decrypted and decoded off the UI thread on first use.</summary>
    public BitmapSource? Thumbnail
    {
        get
        {
            if (!ShowImage) return null;
            if (_thumbnail is null && !_thumbnailRequested)
            {
                _thumbnailRequested = true;
                long id = Id;
                ClipboardHistory? history = _module.History;
                Task.Run(() =>
                {
                    byte[]? png = history?.GetImagePng(id);
                    return png is null ? null : ClipboardModule.DecodePng(png, 320);
                }).ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion && task.Result is { } image) { _thumbnail = image; Raise(nameof(Thumbnail)); }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return _thumbnail;
        }
    }

    public void Update(ClipEntry entry, string tagNames)
    {
        bool imageChanged = entry.ImageFile != _entry.ImageFile;
        _entry = entry;
        _tagNames = tagNames;
        if (imageChanged) { _thumbnail = null; _thumbnailRequested = false; }
        RaiseAll();
    }

    /// <summary>Explicit user action: decrypts and shows a sensitive item until hidden again.</summary>
    public void Reveal(bool show)
    {
        if (!_entry.Sensitive) return;
        _revealedText = show ? (_entry.Kind == ClipKind.Image ? "" : _module.RevealText(Id) ?? "") : null;
        if (!show) { _thumbnail = null; _thumbnailRequested = false; }
        RaiseAll();
    }

    /// <summary>Refreshes relative times and expiry countdowns.</summary>
    public void Tick()
    {
        Raise(nameof(MetaText));
        Raise(nameof(DetailText));
        if (_entry.Sensitive) { Raise(nameof(SensitiveNote)); Raise(nameof(ExpiryText)); Raise(nameof(HasExpiry)); }
    }

    private static string OneParagraph(string text)
    {
        string[] lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Take(4).ToArray();
        return string.Join("\n", lines);
    }

    internal static string RelativeTime(DateTimeOffset at, DateTimeOffset now)
    {
        TimeSpan age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return "az önce";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} dk önce";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} sa önce";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} gün önce";
        return at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.GetCultureInfo("tr-TR"));
    }

    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class TagViewModel
{
    public TagViewModel(ClipTag tag, int count, bool selected)
    {
        Tag = tag;
        Count = count;
        SelectedTag = selected ? "selected" : null;
    }

    public ClipTag Tag { get; }
    public long Id => Tag.Id;
    public string Name => Tag.Name;
    public int Count { get; }
    public string? SelectedTag { get; }
}
