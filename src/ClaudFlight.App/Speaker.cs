using System.Reflection;
using System.Runtime.InteropServices;

namespace ClaudFlight.App;

/// <summary>Says short phrases with Windows' built-in voice (SAPI), without waiting for it to finish.
/// Quietly does nothing if no voice is available.</summary>
internal sealed class Speaker
{
    // SVSFlagsAsync | SVSFPurgeBeforeSpeak | SVSFIsNotXML: don't block, cut off whatever is still
    // being said (a quick double flip says only the latest), and never read the text as markup.
    private const int SpeakFlags = 1 | 2 | 16;

    private object? _voice;
    private bool _unavailable;

    public void Say(string text)
    {
        if (_unavailable || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            if (_voice is null)
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type is null)
                {
                    _unavailable = true;
                    return;
                }
                _voice = Activator.CreateInstance(type);
            }
            _voice!.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, _voice, [text, SpeakFlags]);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException or MissingMethodException)
        {
            _unavailable = true;
        }
    }
}
