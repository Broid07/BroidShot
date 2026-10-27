using System;

namespace BroidShot.Modules.Clipboard.Native;

[Flags]
internal enum HookModifiers
{
    None = 0,
    Win = 1,
    Ctrl = 2,
    Alt = 4,
    Shift = 8,
}

internal enum HookAction
{
    /// <summary>Let the key through to Windows and the focused application.</summary>
    Pass,
    /// <summary>Hide the key from everyone (auto-repeat or the matching key-up of a swallowed V).</summary>
    Swallow,
    /// <summary>Win+V pressed: swallow V, keep the Start menu from opening and show the quick panel.</summary>
    SwallowAndOpen,
}

/// <summary>
/// Pure decision logic of the Win+V takeover: given one keyboard event and the modifiers that are held, says whether to
/// pass it on, swallow it or swallow it and open the panel. No Windows calls, so it runs the same in tests.
/// Not thread-safe: the hook thread is the only caller.
/// </summary>
internal sealed class QuickPanelKeyFilter
{
    private bool _vSwallowed;

    /// <param name="modifiers">Modifiers physically held; only needed (and only read) for a V key-down.</param>
    /// <param name="injected">Event came from SendInput (our own mask key, PasteHelper, other tools): never touched.</param>
    public HookAction Evaluate(uint vkCode, bool keyDown, bool injected, HookModifiers modifiers)
    {
        if (injected || vkCode != NativeMethods.VK_V) return HookAction.Pass;
        if (keyDown)
        {
            if (modifiers != HookModifiers.Win)
            {
                _vSwallowed = false; // Ctrl+Win+V, Win+Shift+V, plain V ... belong to Windows and applications
                return HookAction.Pass;
            }
            bool repeat = _vSwallowed;
            _vSwallowed = true;
            return repeat ? HookAction.Swallow : HookAction.SwallowAndOpen;
        }
        if (!_vSwallowed) return HookAction.Pass;
        _vSwallowed = false; // the application never saw the key-down, so it must not see the key-up either
        return HookAction.Swallow;
    }

    public void Reset() => _vSwallowed = false;
}
