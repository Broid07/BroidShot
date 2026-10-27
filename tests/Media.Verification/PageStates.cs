using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BroidShot.Modules.Media;

internal static partial class Program
{
    private static void PageStates()
    {
        var (module, host, runner, tools) = CreateModule("page-states", toolsInstalled: false);
        try
        {
            var view = (MediaView)module.CreateView();
            T Named<T>(string name) where T : FrameworkElement => (T)view.FindName(name);
            void Click(string name) => Named<ButtonBase>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            void Call(string method, params object[] args) => typeof(MediaView)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, args);
            void Render(string name, double width = 1100, double height = 760)
            {
                PageRenderer.RenderPage(view, name, width, height);
                Assert(PageRenderer.MissingIconGlyphs(view).Count == 0, name + ": all icon glyphs exist.");
                Assert(PageRenderer.GlyphsInTextFont(view).Count == 0, name + ": icons use the icon font.");
            }

            Assert(Named<StackPanel>("QueueEmpty").Visibility == Visibility.Visible && Named<TextBlock>("QueueCount").Text == "0", "A new queue shows the empty state and zero count.");
            Render("media-1-empty.png");
            Render("media-1-empty-narrow.png", 560, 650);
            Click("HistoryTab");
            Assert(Named<ScrollViewer>("HistoryPage").Visibility == Visibility.Visible && Named<ScrollViewer>("DownloadPage").Visibility == Visibility.Collapsed, "The history tab opens its own page.");
            Click("HistoryTab");
            Assert(Named<ToggleButton>("HistoryTab").IsChecked == true, "Clicking the selected tab keeps it selected.");
            Render("media-2-history-empty.png");
            Click("SettingsTab");
            var cookies = Named<CheckBox>("CookiesBox");
            cookies.IsChecked = true;
            Assert(module.Settings.UseBrowserCookies && Named<ComboBox>("BrowserBox").IsEnabled, "The cookie switch saves the setting and enables browser selection.");
            cookies.IsChecked = false;
            Assert(!module.Settings.UseBrowserCookies && !Named<ComboBox>("BrowserBox").IsEnabled, "Turning cookies off saves the setting and disables browser selection.");
            Render("media-3-settings.png");
            module.OpenWithUrl("https://youtu.be/abc");
            Assert(Named<ScrollViewer>("DownloadPage").Visibility == Visibility.Visible && Named<ToggleButton>("DownloadTab").IsChecked == true, "An incoming link switches from settings to the download page.");
            Assert(Named<Border>("ConsentCard").Visibility == Visibility.Visible && runner.Specs.Count == 0 && tools.Calls.Count == 0, "Consent remains required before any tool is started.");
            Render("media-4-consent.png");
            Named<Border>("ConsentCard").Visibility = Visibility.Collapsed;

            var entries = new[]
            {
                new MediaEntry { Index = 1, Title = "Tasarım sistemine giriş", Url = "https://youtu.be/one", Duration = 725 },
                new MediaEntry { Index = 2, Title = "Renkler, yazı tipleri ve bileşenler", Url = "https://youtu.be/two", Duration = 940 },
            };
            var formats = new[] { new MediaFormat("137", "mp4", 1080, 30, "avc1", "none", null, null, null) };
            Call("ShowInfo", new MediaInfo { SourceUrl = VideoUrl, Title = "Tasarım atölyesi · Bölüm 1", Uploader = "BroidShot", Duration = 725, Formats = formats });
            Assert(Named<Border>("InfoCard").Visibility == Visibility.Visible && Named<StackPanel>("PlaylistPanel").Visibility == Visibility.Collapsed, "A single video shows info without playlist selection.");
            Named<ComboBox>("PresetBox").SelectedItem = YtDlpArguments.PresetOptions.Single(p => p.Preset == DownloadPreset.Advanced);
            Assert(Named<FrameworkElement>("AdvancedPanel").Visibility == Visibility.Visible, "The advanced preset reveals format controls.");
            Named<ComboBox>("FormatList").SelectedIndex = 0;
            Assert(Named<TextBox>("FormatBox").Text == "137", "Selecting a format fills the selector.");
            Render("media-5-video-advanced.png", height: 1000);

            Call("ShowInfo", new MediaInfo { SourceUrl = VideoUrl, Title = "Tasarım atölyesi", IsPlaylist = true, Entries = entries, Formats = formats });
            Assert(Named<StackPanel>("PlaylistPanel").Visibility == Visibility.Visible && Named<TextBlock>("PlaylistCount").Text == "2 / 2 öğe seçili", "Playlist entries start selected.");
            Call("SelectNone", view, new RoutedEventArgs());
            Assert(entries.All(e => !e.IsSelected) && Named<TextBlock>("PlaylistCount").Text == "0 / 2 öğe seçili", "Select none updates the entries and count.");
            Click("DownloadButton");
            Assert(Named<TextBlock>("DownloadStatus").Visibility == Visibility.Visible && module.Queue.Items.Count == 0, "An empty playlist selection shows a message and starts no download.");
            Call("SelectAll", view, new RoutedEventArgs());
            Assert(entries.All(e => e.IsSelected), "Select all restores every entry.");
            Render("media-6-playlist.png", height: 1180);

            Named<Border>("InfoCard").Visibility = Visibility.Collapsed;
            Named<TextBlock>("FetchStatus").Visibility = Visibility.Collapsed;
            var request = new DownloadRequest { Url = VideoUrl, Title = "Tasarım atölyesi · Bölüm 1", Preset = DownloadPreset.VideoBest, OutputFolder = root, FileNameTemplate = MediaSettings.DefaultFileNameTemplate };
            DownloadItem Item(DownloadState state, string title)
            {
                var item = new DownloadItem(request with { Title = title }, action => action());
                item.SetState(state, state == DownloadState.Failed ? "Bağlantı kesildi. Tekrar deneyebilirsin." : state.ToString(), state == DownloadState.Completed ? Path.Combine(root, "tasarım atölyesi.mp4") : null);
                return item;
            }
            var items = new[] { Item(DownloadState.Running, "Renkler ve tipografi"), Item(DownloadState.Completed, "Tasarım sistemine giriş"), Item(DownloadState.Failed, "Bileşen kütüphanesi") };
            Named<ItemsControl>("QueueList").ItemsSource = items;
            Named<StackPanel>("QueueEmpty").Visibility = Visibility.Collapsed;
            Named<TextBlock>("QueueCount").Text = "3";
            Render("media-7-queue.png", height: 1000);
            var buttons = PageRenderer.Descendants<Button>(view).ToArray();
            Assert(buttons.Any(b => ReferenceEquals(b.DataContext, items[0]) && Equals(b.Content, "İptal") && b.Visibility == Visibility.Visible), "A running row offers cancel.");
            Assert(buttons.Any(b => ReferenceEquals(b.DataContext, items[1]) && Equals(b.Content, "Dosyayı aç") && b.Visibility == Visibility.Visible), "A completed row offers open.");
            Assert(buttons.Any(b => ReferenceEquals(b.DataContext, items[2]) && Equals(b.Content, "Tekrar dene") && b.Visibility == Visibility.Visible), "A failed row offers retry.");
            module.History.Add(new HistoryEntry { Title = "Tasarım sistemine giriş", Url = VideoUrl, FilePath = Path.Combine(root, "tasarım atölyesi.mp4"), CompletedAt = DateTimeOffset.Now });
            Call("RefreshHistory");
            Click("HistoryTab");
            Assert(Named<ItemsControl>("HistoryList").Items.Count == 1 && Named<StackPanel>("HistoryEmpty").Visibility == Visibility.Collapsed, "History shows completed entries and hides the empty state.");
            Render("media-8-history.png");
            Assert(runner.Specs.Count == 0 && tools.Calls.Count == 0, "Page rendering never starts a network request or external tool.");
            Console.WriteLine("  render: " + PageRenderer.OutputFolder);
        }
        finally { module.Shutdown(); }
    }
}
