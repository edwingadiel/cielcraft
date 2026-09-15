using System;

namespace CielCraft.Core;

/// <summary>
/// Logging seam for the automation layers (roadmap 5.1). The plugin binds it
/// to Dalamud's logger plus the in-memory diagnostic log; tests bind a list.
/// </summary>
public interface ILog
{
    void Information(string message);

    void Warning(string message);

    void Error(string message);

    /// <summary>An exception thrown inside a per-frame tick; implementations rate-limit per source.</summary>
    void TickError(string source, Exception exception);
}

/// <summary>Collects log lines; for tests and for layers constructed before the plugin logger exists.</summary>
public sealed class ListLog : ILog
{
    public System.Collections.Generic.List<string> Lines { get; } = [];

    public void Information(string message) => Lines.Add("INF " + message);

    public void Warning(string message) => Lines.Add("WRN " + message);

    public void Error(string message) => Lines.Add("ERR " + message);

    public void TickError(string source, Exception exception) => Lines.Add($"ERR [{source}] tick: {exception.Message}");
}
