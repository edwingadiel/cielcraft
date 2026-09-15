using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// CielCraft's visual language: a warm gold accent over muted neutrals, with
/// consistent status colors. All windows draw through these helpers so the
/// plugin reads as one designed surface rather than stacked debug text.
/// </summary>
internal static class UiTheme
{
    public static readonly Vector4 Accent = new(0.95f, 0.77f, 0.41f, 1f);
    public static readonly Vector4 AccentDim = new(0.95f, 0.77f, 0.41f, 0.30f);
    public static readonly Vector4 Success = new(0.42f, 0.82f, 0.51f, 1f);
    public static readonly Vector4 Warning = new(0.97f, 0.70f, 0.28f, 1f);
    public static readonly Vector4 Danger = new(0.92f, 0.40f, 0.40f, 1f);
    public static readonly Vector4 Info = new(0.45f, 0.70f, 0.95f, 1f);
    public static readonly Vector4 Muted = new(0.60f, 0.63f, 0.68f, 1f);
    public static readonly Vector4 Faint = new(0.60f, 0.63f, 0.68f, 0.55f);

    /// <summary>Small-caps style section header with an accent rule under it.</summary>
    public static void SectionHeader(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(Accent, title.ToUpperInvariant());
        ImGui.PushStyleColor(ImGuiCol.Separator, AccentDim);
        ImGui.Separator();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    /// <summary>A colored status dot followed by a muted label, with a tooltip.</summary>
    public static void StatusDot(string label, Vector4 color, string tooltip)
    {
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0, 4);
        ImGui.TextColored(Muted, label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    /// <summary>Muted key, normal value, on one line.</summary>
    public static void KeyValue(string key, string value)
    {
        ImGui.TextColored(Muted, key);
        ImGui.SameLine(0, 6);
        ImGui.TextUnformatted(value);
    }

    /// <summary>An accent-colored progress bar with an overlay label.</summary>
    public static void ProgressBar(float fraction, string overlay, Vector4? color = null)
    {
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color ?? Accent);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(1f, 1f, 1f, 0.06f));
        ImGui.ProgressBar(fraction, new Vector2(-1, 0), overlay);
        ImGui.PopStyleColor(2);
    }

    /// <summary>A button tinted toward the given color.</summary>
    public static bool TintedButton(string label, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color with { W = 0.22f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color with { W = 0.40f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color with { W = 0.55f });
        ImGui.PushStyleColor(ImGuiCol.Text, color with { W = 1f });
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    /// <summary>A game icon inline, followed by SameLine; draws nothing for icon 0 or an unloaded texture.</summary>
    public static void GameIcon(ushort iconId, float size = 20f)
    {
        if (iconId == 0)
            return;

        var wrap = Plugin.TextureProvider
            .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(iconId))
            .GetWrapOrDefault();
        if (wrap == null)
            return;

        ImGui.Image(wrap.Handle, new Vector2(size, size));
        ImGui.SameLine(0, 5);
    }

    /// <summary>Colored state name followed by the wrapped status text; shared by every runner badge.</summary>
    public static void StateBadge(string state, bool paused, string statusText)
    {
        // Every state enum shares the terminal names Failed/Completed; the
        // badge keys off those names deliberately so one renderer serves all.
        var color = state switch
        {
            "Failed" => Danger,
            "Completed" => Success,
            _ when paused => Warning,
            _ => Info,
        };

        ImGui.TextColored(color, $"● {state}");
        ImGui.SameLine(0, 8);
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, statusText);
        ImGui.PopTextWrapPos();
    }

    /// <summary>Tint the next collapsing header (and its hover/active states); pair with <see cref="PopHeaderTint"/>.</summary>
    public static void PushHeaderTint(Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Header, color with { W = 0.18f });
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, color with { W = 0.30f });
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, color with { W = 0.40f });
    }

    public static void PopHeaderTint() => ImGui.PopStyleColor(3);

    /// <summary>State-appropriate color for status badges.</summary>
    public static Vector4 StateColor(bool running, bool paused, bool failed, bool completed) =>
        failed ? Danger : paused ? Warning : completed ? Success : running ? Info : Muted;
}
