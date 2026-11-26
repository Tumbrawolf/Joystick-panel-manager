using System.Text.RegularExpressions;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Keyboard;

/// <summary>Turns a key sequence into text and back, e.g. "G, wait 200, Ctrl+L, Enter": steps
/// separated by commas, keys pressed together joined with +, and "wait N" pausing N milliseconds.
/// Key names are KeyCatalog's (any case), plus short forms like Ctrl, Shift, Alt, Esc. The comma
/// and plus keys are written Comma and Plus (Numpad Plus for the keypad's).</summary>
public static partial class KeySequenceText
{
    public const int MaxSteps = 50;
    public const int MaxWaitMs = 10_000;

    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0xA2, ["Control"] = 0xA2, ["Shift"] = 0xA0, ["Alt"] = 0xA4,
        ["Esc"] = 0x1B, ["Return"] = 0x0D, ["Del"] = 0x2E, ["PgUp"] = 0x21, ["PgDn"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["Plus"] = 0xBB, ["Comma"] = 0xBC, ["Numpad Plus"] = 0x6B,
    };

    /// <summary>Names written out so they can't be mistaken for the separators.</summary>
    private static readonly Dictionary<int, string> SafeNames = new() { [0xBB] = "Plus", [0xBC] = "Comma", [0x6B] = "Numpad Plus" };

    /// <summary>The steps the text describes, or null with error saying what's wrong.</summary>
    public static List<KeyStep>? Parse(string text, out string? error)
    {
        error = null;
        var steps = new List<KeyStep>();
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("wait", StringComparison.OrdinalIgnoreCase))
            {
                var number = raw[4..].Trim();
                if (number.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) number = number[..^2].Trim();
                if (!int.TryParse(number, out var ms) || ms < 1 || ms > MaxWaitMs)
                {
                    error = $"\"{raw}\": a wait is \"wait\" and a number of milliseconds from 1 to {MaxWaitMs}, e.g. \"wait 200\".";
                    return null;
                }
                steps.Add(new KeyStep { WaitMs = ms });
                continue;
            }

            // "Numpad +" and a trailing/lone "+" mean the plus keys, not a join.
            var combo = NumpadPlus().Replace(raw, "Numpad Plus");
            if (combo.Trim() == "+") combo = "Plus";
            else if (combo.TrimEnd().EndsWith("++")) combo = combo.TrimEnd()[..^1] + "Plus";

            var keys = new List<int>();
            foreach (var name in combo.Split('+', StringSplitOptions.TrimEntries))
            {
                if (Find(name) is not { } vkey)
                {
                    error = name.Length == 0
                        ? $"\"{raw}\" has a + with no key on one side."
                        : $"\"{name}\" isn't a key name ClaudFlight knows. Use names like A, F5, Enter, Space, Ctrl, Left Shift, Numpad 1.";
                    return null;
                }
                if (!keys.Contains(vkey)) keys.Add(vkey);
            }
            steps.Add(new KeyStep { Keys = keys });
        }

        if (steps.Count == 0) { error = "Type at least one key, e.g. \"G\" or \"Ctrl+L, Enter\"."; return null; }
        if (steps.Count > MaxSteps) { error = $"A sequence can have at most {MaxSteps} steps."; return null; }
        if (steps.All(s => s.Keys.Count == 0)) { error = "The sequence only waits - add a key to press."; return null; }
        return steps;
    }

    public static string Format(IEnumerable<KeyStep> steps) => string.Join(", ", steps.Select(step =>
        step.Keys.Count == 0 ? $"wait {step.WaitMs}" : string.Join("+", step.Keys.Select(Name))));

    /// <summary>A key's name as Parse reads it back.</summary>
    public static string Name(int vkey) =>
        SafeNames.TryGetValue(vkey, out var safe) ? safe : KeyCatalog.Keys.FirstOrDefault(k => k.VKey == vkey)?.Name ?? $"0x{vkey:X2}";

    private static int? Find(string name)
    {
        if (name.Length == 0) return null;
        if (KeyCatalog.Keys.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) is { } key) return key.VKey;
        if (Aliases.TryGetValue(name, out var alias)) return alias;
        // A punctuation key by either of its characters, e.g. "/" for "/ ?".
        if (name.Length == 1 && KeyCatalog.Keys.FirstOrDefault(k => k.Name.Length == 3 && k.Name[1] == ' ' && (k.Name[0] == name[0] || k.Name[2] == name[0])) is { } punctuation)
            return punctuation.VKey;
        return null;
    }

    [GeneratedRegex(@"numpad\s*\+", RegexOptions.IgnoreCase)]
    private static partial Regex NumpadPlus();
}
