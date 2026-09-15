namespace CielCraft.Core;

/// <summary>
/// Translates the (CRP-flavoured) action ids Raphael emits into the ids the
/// active job can use. The plugin resolves through the game sheets; tests
/// use an identity or table resolver.
/// </summary>
public interface IActionResolver
{
    uint? ResolveForJob(uint raphaelActionId, uint classJobId);
}

/// <summary>Returns the id unchanged; for tests and for CRP.</summary>
public sealed class IdentityActionResolver : IActionResolver
{
    public static readonly IdentityActionResolver Instance = new();

    public uint? ResolveForJob(uint raphaelActionId, uint classJobId) => raphaelActionId;
}
