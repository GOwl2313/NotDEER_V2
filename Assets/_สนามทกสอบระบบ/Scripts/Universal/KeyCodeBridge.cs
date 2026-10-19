using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

/// <summary>
/// KEYCODE BRIDGE (v2.0)
///
/// Two jobs, both shared by the whole project:
///
/// 1) UNIVERSAL INTERACTION-KEY ARBITRATION (always compiled, works on BOTH
///    input backends). The game has ONE interaction key (E) used by several
///    systems — items AND interaction points. One PRESS must perform ONE
///    action, no matter how many systems are eligible at the same time:
///      • the system that acted calls ConsumeKey(key) that frame;
///      • every other system checks WasKeyConsumedThisFrame(key) BEFORE
///        acting and skips the press if it was already consumed.
///    The claim is frame-based (Time.frameCount), so it expires by itself —
///    nothing to reset, safe across scene reloads and play-mode restarts.
///    ItemPickup runs first ([DefaultExecutionOrder(-50)]), so a picked-up
///    item wins the press over a Press-Key interaction point standing at the
///    same spot; interaction points then consume the press themselves, so
///    one press also only ever activates ONE point.
///
/// 2) LEGACY-KEYCODE → NEW-INPUT-SYSTEM BRIDGE (only compiled when the
///    project uses the new Input System). Lets Inspector KeyCode fields
///    genuinely drive both input backends.
///
/// Keep this file in the project on BOTH input backends — since v2.0 the
/// arbitration API is always compiled (on legacy-Input-Manager-only projects
/// the class simply has no bridge methods).
///
/// v1.0 — input bridge only (compiled away without the new Input System).
/// v2.0 — added the always-compiled interaction-key arbitration API and
///        moved it OUT of the #if so it exists on every input backend.
/// </summary>
public static class KeyCodeBridge
{
    // ------------------------------------------------------------------ //
    // Universal interaction-key arbitration (v2.0 — always compiled)
    // ------------------------------------------------------------------ //

    private static int s_ConsumedFrame = -1;
    private static KeyCode s_ConsumedKey = KeyCode.None;

    /// <summary>
    /// Claims the given key for THIS frame. Call it the moment your system
    /// actually used the press (e.g. right after picking an item up). Every
    /// other system then skips the same press via
    /// <see cref="WasKeyConsumedThisFrame"/>. Self-expiring — no cleanup.
    /// </summary>
    public static void ConsumeKey(KeyCode key)
    {
        s_ConsumedFrame = Time.frameCount;
        s_ConsumedKey = key;
    }

    /// <summary>
    /// True when the given key was already consumed by another system THIS
    /// frame (an item pickup or another interaction point). Check this
    /// before acting on a key press so one press performs one action.
    /// </summary>
    public static bool WasKeyConsumedThisFrame(KeyCode key)
    {
        return s_ConsumedFrame == Time.frameCount && s_ConsumedKey == key;
    }

    /// <summary>Play-mode entry reset — correct even with Domain Reload disabled.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetArbitration()
    {
        s_ConsumedFrame = -1;
        s_ConsumedKey = KeyCode.None;
    }

#if ENABLE_INPUT_SYSTEM
    // ------------------------------------------------------------------ //
    // Legacy KeyCode → new Input System bridge (new Input System only)
    // ------------------------------------------------------------------ //

    /// <summary>True while the given key is held down (null-safe).</summary>
    public static bool IsKeyPressed(Keyboard keyboard, KeyCode code)
    {
        KeyControl control = FindKeyControl(keyboard, code);
        return control != null && control.isPressed;
    }

    /// <summary>True only on the frame the given key was pressed down (null-safe).</summary>
    public static bool WasKeyPressedThisFrame(Keyboard keyboard, KeyCode code)
    {
        KeyControl control = FindKeyControl(keyboard, code);
        return control != null && control.wasPressedThisFrame;
    }

    private static KeyControl FindKeyControl(Keyboard keyboard, KeyCode code)
    {
        if (keyboard == null) return null;

        Key key = MapKey(code);
        if (key == Key.None) return null;

        return keyboard[key];
    }

    /// <summary>
    /// Maps a legacy KeyCode onto the new Input System Key enum.
    /// Exotic keys without a direct equivalent return Key.None (they simply
    /// never trigger – pick a common key instead).
    /// </summary>
    private static Key MapKey(KeyCode code)
    {
        switch (code)
        {
            // Control / whitespace keys
            case KeyCode.Space: return Key.Space;
            case KeyCode.Return: return Key.Enter;
            case KeyCode.KeypadEnter: return Key.NumpadEnter;
            case KeyCode.Escape: return Key.Escape;
            case KeyCode.Tab: return Key.Tab;
            case KeyCode.Backspace: return Key.Backspace;
            case KeyCode.Delete: return Key.Delete;
            case KeyCode.Insert: return Key.Insert;
            case KeyCode.CapsLock: return Key.CapsLock;

            // Navigation
            case KeyCode.Home: return Key.Home;
            case KeyCode.End: return Key.End;
            case KeyCode.PageUp: return Key.PageUp;
            case KeyCode.PageDown: return Key.PageDown;
            case KeyCode.UpArrow: return Key.UpArrow;
            case KeyCode.DownArrow: return Key.DownArrow;
            case KeyCode.LeftArrow: return Key.LeftArrow;
            case KeyCode.RightArrow: return Key.RightArrow;

            // Modifiers
            case KeyCode.LeftShift: return Key.LeftShift;
            case KeyCode.RightShift: return Key.RightShift;
            case KeyCode.LeftControl: return Key.LeftCtrl;
            case KeyCode.RightControl: return Key.RightCtrl;
            case KeyCode.LeftAlt: return Key.LeftAlt;
            case KeyCode.RightAlt: return Key.RightAlt;

            // Punctuation
            case KeyCode.BackQuote: return Key.Backquote;
            case KeyCode.Minus: return Key.Minus;
            case KeyCode.Comma: return Key.Comma;
            case KeyCode.Period: return Key.Period;
            case KeyCode.Semicolon: return Key.Semicolon;
            case KeyCode.Quote: return Key.Quote;
            case KeyCode.LeftBracket: return Key.LeftBracket;
            case KeyCode.RightBracket: return Key.RightBracket;
            case KeyCode.Backslash: return Key.Backslash;
            case KeyCode.Slash: return Key.Slash;

            // Letters
            case KeyCode.A: return Key.A;
            case KeyCode.B: return Key.B;
            case KeyCode.C: return Key.C;
            case KeyCode.D: return Key.D;
            case KeyCode.E: return Key.E;
            case KeyCode.F: return Key.F;
            case KeyCode.G: return Key.G;
            case KeyCode.H: return Key.H;
            case KeyCode.I: return Key.I;
            case KeyCode.J: return Key.J;
            case KeyCode.K: return Key.K;
            case KeyCode.L: return Key.L;
            case KeyCode.M: return Key.M;
            case KeyCode.N: return Key.N;
            case KeyCode.O: return Key.O;
            case KeyCode.P: return Key.P;
            case KeyCode.Q: return Key.Q;
            case KeyCode.R: return Key.R;
            case KeyCode.S: return Key.S;
            case KeyCode.T: return Key.T;
            case KeyCode.U: return Key.U;
            case KeyCode.V: return Key.V;
            case KeyCode.W: return Key.W;
            case KeyCode.X: return Key.X;
            case KeyCode.Y: return Key.Y;
            case KeyCode.Z: return Key.Z;

            // Number row
            case KeyCode.Alpha0: return Key.Digit0;
            case KeyCode.Alpha1: return Key.Digit1;
            case KeyCode.Alpha2: return Key.Digit2;
            case KeyCode.Alpha3: return Key.Digit3;
            case KeyCode.Alpha4: return Key.Digit4;
            case KeyCode.Alpha5: return Key.Digit5;
            case KeyCode.Alpha6: return Key.Digit6;
            case KeyCode.Alpha7: return Key.Digit7;
            case KeyCode.Alpha8: return Key.Digit8;
            case KeyCode.Alpha9: return Key.Digit9;

            // Numpad digits
            case KeyCode.Keypad0: return Key.Numpad0;
            case KeyCode.Keypad1: return Key.Numpad1;
            case KeyCode.Keypad2: return Key.Numpad2;
            case KeyCode.Keypad3: return Key.Numpad3;
            case KeyCode.Keypad4: return Key.Numpad4;
            case KeyCode.Keypad5: return Key.Numpad5;
            case KeyCode.Keypad6: return Key.Numpad6;
            case KeyCode.Keypad7: return Key.Numpad7;
            case KeyCode.Keypad8: return Key.Numpad8;
            case KeyCode.Keypad9: return Key.Numpad9;

            // Function keys
            case KeyCode.F1: return Key.F1;
            case KeyCode.F2: return Key.F2;
            case KeyCode.F3: return Key.F3;
            case KeyCode.F4: return Key.F4;
            case KeyCode.F5: return Key.F5;
            case KeyCode.F6: return Key.F6;
            case KeyCode.F7: return Key.F7;
            case KeyCode.F8: return Key.F8;
            case KeyCode.F9: return Key.F9;
            case KeyCode.F10: return Key.F10;
            case KeyCode.F11: return Key.F11;
            case KeyCode.F12: return Key.F12;

            default: return Key.None;
        }
    }
#endif
}