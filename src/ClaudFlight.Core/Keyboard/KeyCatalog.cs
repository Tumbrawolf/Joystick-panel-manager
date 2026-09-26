namespace ClaudFlight.Core.Keyboard;

/// <summary>One pickable target key: its display name and Windows virtual-key code.</summary>
public sealed record KeyOption(string Name, int VKey);

/// <summary>Fixed catalog of keys the user can pick as a mapping target - covers letters, digits,
/// F-keys (including F13-F24, which real keyboards lack but are commonly used as "safe" bindings
/// that won't collide with normal typing), numpad, navigation, punctuation, and modifiers.</summary>
public static class KeyCatalog
{
    public static readonly IReadOnlyList<KeyOption> Keys = Build();

    private static KeyOption[] Build()
    {
        var list = new List<KeyOption>();
        void Add(int vk, string name) => list.Add(new KeyOption(name, vk));

        for (var f = 1; f <= 24; f++) Add(0x70 + (f - 1), $"F{f}");
        for (var c = 'A'; c <= 'Z'; c++) Add(0x41 + (c - 'A'), c.ToString());
        for (var d = 0; d <= 9; d++) Add(0x30 + d, d.ToString());
        for (var n = 0; n <= 9; n++) Add(0x60 + n, $"Numpad {n}");

        Add(0x6A, "Numpad *");
        Add(0x6B, "Numpad +");
        Add(0x6D, "Numpad -");
        Add(0x6E, "Numpad .");
        Add(0x6F, "Numpad /");

        Add(0x08, "Backspace");
        Add(0x09, "Tab");
        Add(0x0D, "Enter");
        Add(0x1B, "Escape");
        Add(0x20, "Space");
        Add(0x2D, "Insert");
        Add(0x2E, "Delete");
        Add(0x24, "Home");
        Add(0x23, "End");
        Add(0x21, "Page Up");
        Add(0x22, "Page Down");
        Add(0x25, "Left Arrow");
        Add(0x26, "Up Arrow");
        Add(0x27, "Right Arrow");
        Add(0x28, "Down Arrow");

        Add(0xBA, "; :");
        Add(0xBB, "= +");
        Add(0xBC, ", <");
        Add(0xBD, "- _");
        Add(0xBE, ". >");
        Add(0xBF, "/ ?");
        Add(0xC0, "` ~");
        Add(0xDB, "[ {");
        Add(0xDC, "\\ |");
        Add(0xDD, "] }");
        Add(0xDE, "' \"");

        Add(0xA0, "Left Shift");
        Add(0xA1, "Right Shift");
        Add(0xA2, "Left Ctrl");
        Add(0xA3, "Right Ctrl");
        Add(0xA4, "Left Alt");
        Add(0xA5, "Right Alt");

        return list.ToArray();
    }
}
