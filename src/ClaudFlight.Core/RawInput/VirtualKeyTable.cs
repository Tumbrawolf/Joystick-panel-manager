namespace ClaudFlight.Core.RawInput;

/// <summary>Fixed VK-code -&gt; (button index, friendly name) table. Macro pads and streaming panels
/// almost always send standard virtual keys - commonly F13-F24 specifically because those rarely
/// conflict with normal typing - so a fixed table covering the usual keyboard range is far simpler
/// than trying to discover a device's exact key set up front, and gives every raw keyboard device
/// the same stable button numbering.</summary>
internal static class VirtualKeyTable
{
    private static readonly (int VKey, string Name)[] Entries = BuildEntries();

    private static readonly Dictionary<int, int> IndexByVKey =
        Entries.Select((e, i) => (e.VKey, i)).ToDictionary(x => x.VKey, x => x.i);

    public static int Count => Entries.Length;

    public static string NameAt(int index) => Entries[index].Name;

    public static bool TryGetIndex(int vkey, out int index) => IndexByVKey.TryGetValue(vkey, out index);

    private static (int, string)[] BuildEntries()
    {
        var entries = new List<(int, string)>();

        void Add(int vkey, string name) => entries.Add((vkey, name));

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

        Add(0xAD, "Volume Mute");
        Add(0xAE, "Volume Down");
        Add(0xAF, "Volume Up");
        Add(0xB0, "Next Track");
        Add(0xB1, "Previous Track");
        Add(0xB2, "Media Stop");
        Add(0xB3, "Play/Pause");

        Add(0xA0, "Left Shift");
        Add(0xA1, "Right Shift");
        Add(0xA2, "Left Ctrl");
        Add(0xA3, "Right Ctrl");
        Add(0xA4, "Left Alt");
        Add(0xA5, "Right Alt");

        return entries.ToArray();
    }
}
