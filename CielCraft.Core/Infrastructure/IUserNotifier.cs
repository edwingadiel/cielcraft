namespace CielCraft.Core;

/// <summary>What a notification means, so the plugin can pick a channel (chat, sound, speech) per kind (roadmap 7.20).</summary>
public enum NotificationKind
{
    /// <summary>Progress worth a chat line; never a sound.</summary>
    Info,

    /// <summary>A run finished on its own.</summary>
    Completed,

    /// <summary>A run stopped and needs the user (failed, paused).</summary>
    Attention,
}

/// <summary>
/// User-facing notifications (production complete, paused, failed). The plugin
/// prints to the game chat and, per settings, plays a sound or speaks; tests
/// capture the messages.
/// </summary>
public interface IUserNotifier
{
    void Notify(NotificationKind kind, string message);
}

public static class UserNotifierExtensions
{
    /// <summary>Chat-only line.</summary>
    public static void Print(this IUserNotifier notifier, string message) => notifier.Notify(NotificationKind.Info, message);
}

public sealed class ListNotifier : IUserNotifier
{
    public System.Collections.Generic.List<(NotificationKind Kind, string Message)> Messages { get; } = [];

    public void Notify(NotificationKind kind, string message) => Messages.Add((kind, message));
}

/// <summary>Discards notifications; for layers that should stay quiet.</summary>
public sealed class NullNotifier : IUserNotifier
{
    public static readonly NullNotifier Instance = new();

    public void Notify(NotificationKind kind, string message)
    {
    }
}
