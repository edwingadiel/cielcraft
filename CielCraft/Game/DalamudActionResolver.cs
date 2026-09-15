using CielCraft.Core;

namespace CielCraft.Game;

/// <summary>Plugin-side <see cref="IActionResolver"/>: forwards to the sheet-backed <see cref="CraftActionResolver"/>.</summary>
public sealed class DalamudActionResolver : IActionResolver
{
    public uint? ResolveForJob(uint raphaelActionId, uint classJobId) =>
        CraftActionResolver.ResolveForJob(raphaelActionId, classJobId);
}
