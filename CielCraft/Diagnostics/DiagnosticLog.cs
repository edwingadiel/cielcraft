using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace CielCraft.Diagnostics;

/// <summary>
/// Plugin log that also keeps the most recent entries in memory so a
/// diagnostic report can include them (spec §46). Every state-machine
/// transition, solver request, action execution and error passes through
/// here, which makes a pasted report enough to reconstruct what happened
/// without access to dalamud.log. Thread-safe (solver callbacks log from a
/// worker thread).
/// </summary>
public sealed class DiagnosticLog
{
    public const int Capacity = 500;
    private static readonly TimeSpan TickErrorRepeatInterval = TimeSpan.FromSeconds(10);

    public readonly record struct Entry(DateTime At, string Level, string Message)
    {
        public override string ToString() => $"{At:HH:mm:ss.fff} {Level} {Message}";
    }

    private readonly IPluginLog inner;
    private readonly object gate = new();
    private readonly Queue<Entry> entries = new(Capacity);
    private readonly Dictionary<string, (DateTime LastLogged, int Suppressed)> tickErrors = new();

    public DiagnosticLog(IPluginLog inner)
    {
        this.inner = inner;
    }

    public void Information(string message)
    {
        inner.Information(message);
        Record("INF", message);
    }

    public void Warning(string message)
    {
        inner.Warning(message);
        Record("WRN", message);
    }

    public void Error(string message)
    {
        inner.Error(message);
        Record("ERR", message);
    }

    public void Error(Exception exception, string message)
    {
        inner.Error(exception, message);
        Record("ERR", $"{message} :: {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>Kept in memory always; forwarded at debug level (hidden from dalamud.log unless enabled).</summary>
    public void Debug(string message)
    {
        inner.Debug(message);
        Record("DBG", message);
    }

    /// <summary>
    /// An exception escaping a per-frame tick. The first occurrence per
    /// source is logged with its stack trace; repeats within a short window
    /// are counted instead of flooding the log every frame.
    /// </summary>
    public void TickError(string source, Exception exception)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (tickErrors.TryGetValue(source, out var seen) && now - seen.LastLogged < TickErrorRepeatInterval)
            {
                tickErrors[source] = (seen.LastLogged, seen.Suppressed + 1);
                return;
            }

            var suffix = seen.Suppressed > 0 ? $" (repeated {seen.Suppressed} more times since the last report)" : "";
            tickErrors[source] = (now, 0);
            inner.Error(exception, $"[{source}] Unhandled exception in tick{suffix}.");
            RecordLocked("ERR", $"[{source}] Unhandled exception in tick{suffix} :: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
        }
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        lock (gate)
            return entries.ToArray();
    }

    private void Record(string level, string message)
    {
        lock (gate)
            RecordLocked(level, message);
    }

    private void RecordLocked(string level, string message)
    {
        if (entries.Count >= Capacity)
            entries.Dequeue();

        entries.Enqueue(new Entry(DateTime.UtcNow, level, message));
    }
}
