namespace CielCraft.Core;

/// <summary>
/// User-facing notifications (production complete, paused, failed). The plugin
/// prints to the game chat; tests capture the messages.
/// </summary>
public interface IUserNotifier
{
    void Print(string message);
}

public sealed class ListNotifier : IUserNotifier
{
    public System.Collections.Generic.List<string> Messages { get; } = [];

    public void Print(string message) => Messages.Add(message);
}

/// <summary>Discards notifications; for layers that should stay quiet.</summary>
public sealed class NullNotifier : IUserNotifier
{
    public static readonly NullNotifier Instance = new();

    public void Print(string message)
    {
    }
}
