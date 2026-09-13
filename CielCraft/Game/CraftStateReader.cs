using CielCraft.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CielCraft.Game;

/// <summary>
/// Reads the live craft state (spec §10/§54).
///
/// Numeric values (progress/quality/durability) come from the Synthesis addon's
/// named text nodes, so they match the game UI by construction. Step, condition,
/// and proc flags come from the structured CraftEventHandler.
/// </summary>
internal static unsafe class CraftStateReader
{
    public static CraftSnapshot? Read()
    {
        var addonPtr = Plugin.GameGui.GetAddonByName("Synthesis");
        if (addonPtr.IsNull || !addonPtr.IsVisible)
            return null;

        var addon = (AddonSynthesis*)addonPtr.Address;

        var handler = EventFramework.Instance()->GetCraftEventHandler();

        var step = handler != null ? handler->StepNumber : ParseInt(addon->StepNumber);
        var condition = handler != null ? MapCondition(handler->Condition) : Core.CraftCondition.Unknown;

        var player = Plugin.ObjectTable.LocalPlayer;

        return new CraftSnapshot(
            RecipeLevel: handler != null ? handler->RecipeLevelTable : (ushort)0,
            Step: step,
            Progress: ParseInt(addon->CurrentProgress),
            MaxProgress: ParseInt(addon->MaxProgress),
            Quality: ParseInt(addon->CurrentQuality),
            MaxQuality: ParseInt(addon->MaxQuality),
            Durability: ParseInt(addon->CurrentDurability),
            MaxDurability: ParseInt(addon->StartingDurability),
            CurrentCp: player?.CurrentCp ?? 0,
            MaxCp: player?.MaxCp ?? 0,
            Condition: condition);
    }

    private static int ParseInt(AtkTextNode* node)
    {
        if (node == null)
            return 0;

        var text = node->NodeText.ToString();
        var value = 0;
        var seenDigit = false;

        foreach (var c in text)
        {
            if (c is >= '0' and <= '9')
            {
                value = value * 10 + (c - '0');
                seenDigit = true;
            }
            else if (seenDigit)
            {
                // Stop at the first non-digit after the number so trailing
                // annotations (e.g. "%" or a second number) are ignored.
                break;
            }
        }

        return value;
    }

    private static Core.CraftCondition MapCondition(FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition condition) =>
        condition switch
        {
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Normal => Core.CraftCondition.Normal,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Good => Core.CraftCondition.Good,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Excellent => Core.CraftCondition.Excellent,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Poor => Core.CraftCondition.Poor,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Centered => Core.CraftCondition.Centered,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Sturdy => Core.CraftCondition.Sturdy,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Pliant => Core.CraftCondition.Pliant,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Malleable => Core.CraftCondition.Malleable,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.Primed => Core.CraftCondition.Primed,
            FFXIVClientStructs.FFXIV.Client.Game.Event.CraftCondition.GoodOmen => Core.CraftCondition.GoodOmen,
            _ => Core.CraftCondition.Unknown,
        };
}
