using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudFlight.Core.Models;

/// <summary>Copies of mappings with every setting kept - so features added later (triggers,
/// zones, sequences...) aren't lost by code that copies a mapping field by field.</summary>
public static class ModelCopy
{
    /// <summary>A full copy of a Binding or KeyBinding, reading from another device.</summary>
    public static T WithSourceDevice<T>(T mapping, Guid deviceGuid) where T : class
    {
        var node = JsonSerializer.SerializeToNode(mapping)!;
        node["Source"]!["DeviceGuid"] = JsonValue.Create(deviceGuid);
        return node.Deserialize<T>() ?? throw new InvalidOperationException("The mapping could not be copied.");
    }
}
