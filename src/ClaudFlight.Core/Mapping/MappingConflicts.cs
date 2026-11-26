using ClaudFlight.Core.Keyboard;
using ClaudFlight.Core.Models;

namespace ClaudFlight.Core.Mapping;

/// <summary>One output that more than one input writes to, and the mappings doing it.</summary>
public sealed record MappingConflict(string Output, IReadOnlyList<object> Mappings);

/// <summary>Finds outputs fought over by mappings on different inputs: a vJoy button, hat or key
/// pressed from two places (usually a copy-paste slip), or a vJoy axis driven by two axes, or by an
/// axis and a button moving it. Mappings in two different modes never run together, so they don't
/// clash; several buttons moving one axis (trim up/down/center) is normal and doesn't count.</summary>
public static class MappingConflicts
{
    public static IReadOnlyList<MappingConflict> Find(Profile profile)
    {
        var conflicts = new List<MappingConflict>();

        foreach (var group in profile.Bindings.GroupBy(b => b.Target))
        {
            var target = group.Key;
            var name = target.Kind switch
            {
                ControlKind.Axis => $"vJoy {target.VJoyDeviceId} {(VJoyAxis)target.Index}",
                ControlKind.Pov => $"vJoy {target.VJoyDeviceId} hat {target.Index + 1}",
                _ => $"vJoy {target.VJoyDeviceId} button {target.Index + 1}",
            };
            var items = group.Select(b => new Item(b, Slot(b.Source, b.Part), b.ModeName, b.Nudge != AxisNudge.None)).ToList();
            Add(conflicts, name, items);
        }

        foreach (var group in profile.KeyBindings.Where(k => k.Sequence is not { Count: > 0 }).GroupBy(k => k.VirtualKeyCode))
        {
            var keyName = KeyCatalog.Keys.FirstOrDefault(k => k.VKey == group.Key)?.Name ?? $"0x{group.Key:X2}";
            Add(conflicts, $"key {keyName}", group.Select(k => new Item(k, Slot(k.Source, InputParts.Of(k)), k.ModeName, false)).ToList());
        }
        return conflicts;
    }

    private sealed record Item(object Mapping, (Guid, ControlKind, int, InputPart) Slot, string? ModeName, bool IsNudge);

    private static (Guid, ControlKind, int, InputPart) Slot(InputRef source, InputPart part) => (source.DeviceGuid, source.Kind, source.Index, part);

    private static void Add(List<MappingConflict> conflicts, string output, List<Item> items)
    {
        if (items.Count < 2) return;
        var clashing = items.Where(a => items.Any(b => Clash(a, b))).Select(i => i.Mapping).ToList();
        if (clashing.Count > 0) conflicts.Add(new MappingConflict(output, clashing));
    }

    private static bool Clash(Item a, Item b) =>
        !ReferenceEquals(a, b) &&
        a.Slot != b.Slot &&
        !(a.IsNudge && b.IsNudge) &&
        ModesOverlap(a.ModeName, b.ModeName);

    /// <summary>Whether mappings of these two modes (null = any mode) can be in use at the same time.</summary>
    public static bool ModesOverlap(string? a, string? b) =>
        a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
