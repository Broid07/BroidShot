using System.Windows;
using System.Windows.Controls;

namespace BroidShot.Modules.Sample;

public partial class SampleView : UserControl
{
    private readonly SampleModule _module;

    internal SampleView(SampleModule module)
    {
        _module = module;
        InitializeComponent();
        InfoText.Text = "Komut paletinde \"örnek\" yazarak bu modülün komutlarını bulabilirsin. Ayarlar şurada saklanır: "
            + _module.Host.Settings.FilePath;
    }

    private void ShowToast(object sender, RoutedEventArgs e) => _module.ShowGreeting();

    private async void StartJob(object sender, RoutedEventArgs e) => await _module.RunDemoJobAsync();
}
