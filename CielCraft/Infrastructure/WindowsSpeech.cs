using System;
using System.Reflection;

namespace CielCraft.Infrastructure;

/// <summary>
/// Text-to-speech through the Windows SAPI COM voice (roadmap 7.20), reached
/// by late binding so the plugin carries no speech assembly. Speaks
/// asynchronously; failures are logged once and then ignored, since a missing
/// voice must never break a run.
/// </summary>
public sealed class WindowsSpeech : IDisposable
{
    private const int SpeakAsync = 1; // SVSFlagsAsync

    private readonly Core.ILog log;
    private object? voice;
    private bool broken;

    public WindowsSpeech(Core.ILog log)
    {
        this.log = log;
    }

    public void Speak(string text)
    {
        if (broken || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            voice ??= Activator.CreateInstance(
                Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("SAPI.SpVoice is not registered"));
            voice!.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [text, SpeakAsync]);
        }
        catch (Exception e)
        {
            broken = true;
            log.Warning($"[Notify] Speech unavailable ({e.GetType().Name}: {e.Message}); alerts stay silent.");
        }
    }

    public void Dispose()
    {
        if (voice != null && System.Runtime.InteropServices.Marshal.IsComObject(voice))
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice);
        voice = null;
    }
}
