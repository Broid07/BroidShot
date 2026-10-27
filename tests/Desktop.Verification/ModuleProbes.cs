using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BroidShot.Core;

// Test-only modules: one healthy probe, one whose Initialize throws, one whose CreateView throws.
internal sealed class ProbeModule : IToolModule
{
    public int InitializeCount, ShutdownCount;
    public IToolHost? Host;
    public string Id => "probe";
    public string Title => "Sonda modülü";
    public string Description => "Doğrulama için";
    public string Category => "Doğrulama";
    public string Icon => "";
    public IReadOnlyList<string> Keywords => new[] { "sonda", "probe" };
    public IReadOnlyList<ToolCommand> Commands => new[] { ToolCommand.Open("Sonda sayfasını aç", Id) };
    public void Initialize(IToolHost host) { Host = host; InitializeCount++; }
    public FrameworkElement CreateView() => new TextBlock { Text = "Sonda sayfası" };
    public void Shutdown() => ShutdownCount++;
}

internal sealed class BrokenModule : IToolModule
{
    public int ShutdownCount;
    public string Id => "broken";
    public string Title => "Bozuk modül";
    public string Description => "Initialize hata verir";
    public string Category => "Doğrulama";
    public string Icon => "";
    public IReadOnlyList<string> Keywords => Array.Empty<string>();
    public IReadOnlyList<ToolCommand> Commands => new[] { new ToolCommand("Bozuk komut", () => { }) };
    public void Initialize(IToolHost host) => throw new InvalidOperationException("Başlatma bilerek başarısız.");
    public FrameworkElement CreateView() => throw new InvalidOperationException("çağrılmamalı");
    public void Shutdown() => ShutdownCount++;
}

internal sealed class BrokenViewModule : IToolModule
{
    public string Id => "broken-view";
    public string Title => "Sayfası bozuk modül";
    public string Description => "CreateView hata verir";
    public string Category => "Doğrulama";
    public string Icon => "";
    public IReadOnlyList<string> Keywords => Array.Empty<string>();
    public IReadOnlyList<ToolCommand> Commands => Array.Empty<ToolCommand>();
    public void Initialize(IToolHost host) { }
    public FrameworkElement CreateView() => throw new InvalidOperationException("Sayfa bilerek oluşturulamadı.");
    public void Shutdown() { }
}
