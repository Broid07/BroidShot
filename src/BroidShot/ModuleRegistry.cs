using System.Collections.Generic;
using BroidShot.Core;

namespace BroidShot;

/// <summary>
/// The single list of tool modules shown by the shell (after the built-in "Ekran alıntısı" page).
/// Order defines sidebar order; a category appears where its first module appears.
/// Module projects never edit this file — registration happens at integration time: add a
/// ProjectReference in BroidShot.csproj and one line below.
/// </summary>
public static class ModuleRegistry
{
    public static IReadOnlyList<IToolModule> CreateModules() => new IToolModule[]
    {
        new BroidShot.Modules.Clipboard.ClipboardModule(),
        new BroidShot.Modules.Media.MediaModule(),
        new BroidShot.Modules.Convert.ConvertModule(),
        new BroidShot.Modules.Utilities.ColorPickerModule(),
        new BroidShot.Modules.Utilities.RulerModule(),
        new BroidShot.Modules.Utilities.SnippetsModule(),
        new BroidShot.Modules.Utilities.QrModule(),
        new BroidShot.Modules.Utilities.TextToolsModule(),
        new BroidShot.Modules.Utilities.BatchFilesModule(),
    };
}
