using System;
using System.Collections.Generic;
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

        var step = handler != null ? handler->StepNumber : AtkTextParser.ParseInt(addon->StepNumber);
        var condition = handler != null ? MapCondition(handler->Condition) : Core.CraftCondition.Unknown;

        var player = Plugin.ObjectTable.LocalPlayer;

        return new CraftSnapshot(
            RecipeLevel: handler != null ? handler->RecipeLevelTable : (ushort)0,
            Step: step,
            Progress: AtkTextParser.ParseInt(addon->CurrentProgress),
            MaxProgress: AtkTextParser.ParseInt(addon->MaxProgress),
            Quality: AtkTextParser.ParseInt(addon->CurrentQuality),
            MaxQuality: AtkTextParser.ParseInt(addon->MaxQuality),
            Durability: AtkTextParser.ParseInt(addon->CurrentDurability),
            MaxDurability: AtkTextParser.ParseInt(addon->StartingDurability),
            CurrentCp: player?.CurrentCp ?? 0,
            MaxCp: player?.MaxCp ?? 0,
            Condition: condition)
        {
            Buffs = ReadBuffs(),
            RequiredQuality = handler != null ? (int)handler->RequiredQuality : 0,
        };
    }

    private static IReadOnlyList<CraftBuff> ReadBuffs()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return [];

        var buffs = new List<CraftBuff>();
        foreach (var status in player.StatusList)
        {
            // Crafting statuses have no timer: the number on the icon (remaining
            // steps for Waste Not / Manipulation / Innovation..., stacks for Inner
            // Quiet) is the status parameter. RemainingTime read 0 in game and left
            // the Poor-step Observe rule and the mid-craft re-solve effects blind.
            if (status.StatusId != 0 && Array.IndexOf(CraftBuffIds.All, status.StatusId) >= 0)
            {
                var steps = status.RemainingTime > 0.5f ? (int)MathF.Round(status.RemainingTime) : status.Param;
                buffs.Add(new CraftBuff(status.StatusId, status.Param, steps));
            }
        }

        return buffs;
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
