using System;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Plugin.Services;

namespace CielCraft.Infrastructure;

/// <summary>
/// Prints automation milestones to the game chat under the plugin's name and,
/// per settings, plays an in-game sound effect or speaks them for completion
/// and attention notifications (roadmap 7.20).
/// </summary>
public sealed class ChatNotifier : IUserNotifier, IDisposable
{
    private readonly IChatGui chat;
    private readonly Configuration configuration;
    private readonly IGameBridge gameBridge;
    private readonly WindowsSpeech speech;

    public ChatNotifier(IChatGui chat, Configuration configuration, IGameBridge gameBridge, ILog log)
    {
        this.chat = chat;
        this.configuration = configuration;
        this.gameBridge = gameBridge;
        speech = new WindowsSpeech(log);
    }

    public void Notify(NotificationKind kind, string message)
    {
        chat.Print(message, "CielCraft");
        if (kind == NotificationKind.Info)
            return;

        Alert(message);
    }

    /// <summary>Sound and/or speech for a message, as configured; also used by the settings "Test" button.</summary>
    public void Alert(string message)
    {
        if (configuration.AlertSoundEffect > 0)
            gameBridge.PlaySoundEffect(configuration.AlertSoundEffect);
        if (configuration.SpeakAlerts)
            speech.Speak(message);
    }

    public void Dispose() => speech.Dispose();
}
