using CielCraft.Core;
using Dalamud.Plugin.Services;

namespace CielCraft.Infrastructure;

/// <summary>Prints automation milestones to the game chat under the plugin's name.</summary>
public sealed class ChatNotifier : IUserNotifier
{
    private readonly IChatGui chat;

    public ChatNotifier(IChatGui chat)
    {
        this.chat = chat;
    }

    public void Print(string message) => chat.Print(message, "CielCraft");
}
