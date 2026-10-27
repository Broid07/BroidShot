namespace BroidShot.Capture;

// This state contains only the F12 held/consumed bits; it records no typed keys.
internal sealed class F12KeyState
{
    private bool held;
    private bool consume;

    internal KeyDecision Process(bool keyDown, bool modifiersHeld)
    {
        if (keyDown)
        {
            if (held) return new KeyDecision(consume, false);
            held = true;
            consume = !modifiersHeld;
            return new KeyDecision(consume, consume);
        }
        bool wasConsumed = consume;
        held = false;
        consume = false;
        return new KeyDecision(wasConsumed, false);
    }

    internal void Reset() { held = false; consume = false; }
}

internal readonly record struct KeyDecision(bool Consume, bool Trigger);
