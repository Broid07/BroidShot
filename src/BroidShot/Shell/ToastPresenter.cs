using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BroidShot.Core;

namespace BroidShot.Shell;

/// <summary>
/// Bottom-right notifications. One non-activating, topmost tool window stacks up to four toasts;
/// each closes after its duration (paused while hovered), on ✕, or after its action runs.
/// </summary>
public sealed class ToastPresenter : IDisposable
{
    private const int MaxToasts = 4;
    private readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private Window? _window;
    private bool _disposed;

    public int VisibleCount => _stack.Children.Count;

    /// <summary>The panel that stacks the toast cards (exposed for rendering checks).</summary>
    public Panel Container => _stack;

    public void Show(ToastOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_disposed) return;
        Window window = EnsureWindow();
        while (_stack.Children.Count >= MaxToasts) _stack.Children.RemoveAt(0);
        _stack.Children.Add(CreateToast(options));
        if (!window.IsVisible) window.Show();
        Reposition();
    }

    private Window EnsureWindow()
    {
        if (_window is not null) return _window;
        var window = new Window
        {
            Title = "BroidShot bildirimleri",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = 404,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border { Padding = new Thickness(0, 0, 0, 0), Child = _stack }
        };
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            int style = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, style | 0x08000000 | 0x00000080); // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        };
        window.SizeChanged += (_, _) => Reposition();
        _window = window;
        return window;
    }

    private void Reposition()
    {
        if (_window is null) return;
        Rect area = SystemParameters.WorkArea;
        _window.UpdateLayout();
        _window.Left = area.Right - _window.Width - 4;
        _window.Top = area.Bottom - _window.ActualHeight - 8;
    }

    private FrameworkElement CreateToast(ToastOptions options)
    {
        (string glyph, string accentKey, string tintKey) = options.Kind switch
        {
            ToastKind.Success => (ShellIcons.Success, "SuccessBrush", "SuccessSubtleBrush"),
            ToastKind.Warning => (ShellIcons.Warning, "WarningBrush", "WarningSubtleBrush"),
            ToastKind.Error => (ShellIcons.Error, "DangerBrush", "DangerSubtleBrush"),
            _ => (ShellIcons.Info, "AccentTextBrush", "AccentSubtleBrush")
        };
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(14, 10, 14, 4),
            Padding = new Thickness(14, 12, 8, 14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black }
        };
        card.SetResourceReference(Border.BackgroundProperty, "OverlayBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        card.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        TextElement.SetFontSize(card, 14);
        AutomationName(card, options.Title);
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, tintKey);
        var icon = new TextBlock { Text = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, accentKey);
        badge.Child = icon;
        layout.Children.Add(badge);

        var body = new StackPanel { Margin = new Thickness(0, 4, 6, 0) };
        Grid.SetColumn(body, 1);
        var title = new TextBlock { Text = options.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        body.Children.Add(title);
        if (options.Message.Length > 0)
        {
            var message = new TextBlock { Text = options.Message, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            body.Children.Add(message);
        }
        layout.Children.Add(body);

        var close = new Button { Content = ShellIcons.Close, FontSize = 10, ToolTip = "Kapat", VerticalAlignment = VerticalAlignment.Top, MinWidth = 28, MinHeight = 28 };
        close.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(close, "Bildirimi kapat");
        Grid.SetColumn(close, 2);
        layout.Children.Add(close);
        card.Child = layout;

        var timer = new DispatcherTimer { Interval = options.Duration > TimeSpan.Zero ? options.Duration : TimeSpan.FromSeconds(6) };
        void Dismiss()
        {
            timer.Stop();
            _stack.Children.Remove(card);
            if (_stack.Children.Count == 0) _window?.Hide();
            else Reposition();
        }
        timer.Tick += (_, _) => Dismiss();
        close.Click += (_, _) => Dismiss();
        card.MouseEnter += (_, _) => timer.Stop();
        card.MouseLeave += (_, _) => { timer.Stop(); timer.Start(); };

        if (options.ActionLabel is { Length: > 0 } label && options.Action is { } action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 5, 14, 5), MinHeight = 30, HorizontalAlignment = HorizontalAlignment.Left };
            button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            button.Click += (_, _) =>
            {
                Dismiss();
                try { action(); }
                catch (Exception ex) { ErrorLog.Write("toast", "Bildirim eylemi başarısız: " + options.Title, ex); }
            };
            body.Children.Add(button);
        }
        timer.Start();
        return card;
    }

    private static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stack.Children.Clear();
        _window?.Close();
        _window = null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
