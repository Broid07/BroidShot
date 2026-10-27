using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BroidShot.Modules.Utilities;

public partial class TextToolsView : UserControl
{
    private readonly TextToolsModule _module;
    private string _group;

    internal TextToolsView(TextToolsModule module)
    {
        _module = module;
        InitializeComponent();
        _group = TextOperations.All[0].Group;
        Style? chip = Ui.Resource<Style>("FilterChip");
        foreach (string group in TextOperations.All.Select(o => o.Group).Distinct())
        {
            var button = new RadioButton
            {
                Content = group, Tag = group, Style = chip, IsChecked = group == _group,
                ToolTip = string.Join(", ", TextOperations.All.Where(o => o.Group == group).Select(o => o.Title))
            };
            button.Checked += GroupChecked;
            GroupPanel.Children.Add(button);
        }
        ShowOperations();
        UpdateStats();
    }

    /// <summary>Selects an operation group ("JSON", "Kodlama", "Harf"…) and shows its buttons.</summary>
    internal void SelectGroup(string group)
    {
        foreach (RadioButton chip in GroupPanel.Children.OfType<RadioButton>())
            if (Equals(chip.Tag, group)) chip.IsChecked = true;
    }

    private void GroupChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string group }) return;
        _group = group;
        ShowOperations();
    }

    private void ShowOperations()
    {
        OperationPanel.Children.Clear();
        Style? style = TryFindResource("OperationButton") as Style;
        foreach (TextOperation op in TextOperations.All.Where(o => o.Group == _group))
        {
            var button = new Button { Content = op.Title, ToolTip = op.Description, Tag = op, Style = style };
            button.Click += RunOperation;
            OperationPanel.Children.Add(button);
        }
    }

    private void RunOperation(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextOperation op }) return;
        ShowLastOperation(op);
        if (op.NeedsInput && InputBox.Text.Length == 0)
        {
            ShowError("Önce girdi kutusuna bir metin yaz.");
            return;
        }
        try
        {
            TextResult result = op.Run(InputBox.Text);
            if (result.IsError) { ShowError(result.Error!); return; }
            ErrorText.Visibility = Visibility.Collapsed;
            OutputBox.Text = result.Output ?? "";
            UpdateOutputStats();
        }
        catch (Exception ex)
        {
            ShowError("İşlem tamamlanamadı: " + ex.Message);
        }
    }

    private void ShowLastOperation(TextOperation? op)
    {
        LastOperationText.Text = op?.Title ?? "";
        LastOperationChip.Visibility = op is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        OutputBox.Text = "";
        UpdateOutputStats();
    }

    private void InputChanged(object sender, TextChangedEventArgs e) => UpdateStats();

    private void UpdateStats()
    {
        if (StatsText is null) return;
        TextStats s = TextOperations.Count(InputBox.Text);
        StatsText.Text = $"{s.Words} kelime · {s.Characters} karakter · {s.Lines} satır";
    }

    private void UpdateOutputStats()
    {
        TextStats s = TextOperations.Count(OutputBox.Text);
        OutputStats.Text = OutputBox.Text.Length == 0 ? "" : $"{s.Characters} karakter · {s.Lines} satır";
    }

    private void PasteInput(object sender, RoutedEventArgs e)
    {
        try { string? text = SystemClipboard.Instance.GetText(); if (text is not null) InputBox.Text = text; }
        catch (System.Runtime.InteropServices.ExternalException) { ShowError("Pano şu an kullanılamıyor."); }
    }

    private void ClearAll(object sender, RoutedEventArgs e)
    {
        InputBox.Text = "";
        OutputBox.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
        ShowLastOperation(null);
        UpdateOutputStats();
    }

    private void CopyOutput(object sender, RoutedEventArgs e)
    {
        if (OutputBox.Text.Length > 0) _module.CopyText(OutputBox.Text);
    }

    private void MoveToInput(object sender, RoutedEventArgs e)
    {
        if (OutputBox.Text.Length == 0) return;
        InputBox.Text = OutputBox.Text;
        OutputBox.Text = "";
        ShowLastOperation(null);
        UpdateOutputStats();
    }
}
