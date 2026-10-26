using System;
using System.Collections.Generic;
using System.Linq;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Arranges the boxes as a "living Pokédex": the Pokémon with National Dex number N goes to slot N
/// (Box 1 slot 1 = #001, Box 1 slot 30 = #030, Box 2 slot 1 = #031, …). Each evolution stage has its own number,
/// so each gets its own slot. Missing species leave their slot empty.
/// Extra copies and eggs go into the free slots after the last Pokédex slot.
/// </summary>
public static class LivingDexSorter
{
    public sealed record Plan(PKM?[] Layout, int Total, int InDexSlots, int Extras, int ExtrasInGaps, int DexSlots, int MissingSpecies);

    /// <summary> Works out the new layout without changing the save. Returns null with a reason if it can't be done. </summary>
    public static Plan? CreatePlan(SaveFile sav, out string? problem)
    {
        problem = null;
        if (!sav.HasBox)
        {
            problem = "This save has no boxes.";
            return null;
        }
        int capacity = sav.BoxCount * sav.BoxSlotCount;
        for (int i = 0; i < capacity; i++)
        {
            if (sav.IsBoxSlotLocked(i))
            {
                problem = "Some box slots are locked (e.g. Battle Team); unlock them in the game first.";
                return null;
            }
        }

        // Current box contents in their current order
        var all = new List<(int Index, PKM Pk)>();
        for (int i = 0; i < capacity; i++)
        {
            var pk = sav.GetBoxSlotAtIndex(i);
            if (pk.Species != 0)
                all.Add((i, pk));
        }

        int dexSlots = Math.Min(sav.MaxSpeciesID, capacity);
        var layout = new PKM?[capacity];
        var extras = new List<PKM>();

        // A Pokémon that already sits in its own slot keeps it; otherwise the first one found gets the slot.
        var ordered = all.OrderBy(z => z.Index == z.Pk.Species - 1 ? 0 : 1).ThenBy(z => z.Index);
        foreach (var (_, pk) in ordered)
        {
            int target = pk.Species - 1;
            if (!pk.IsEgg && target < dexSlots && layout[target] is null)
                layout[target] = pk;
            else
                extras.Add(pk);
        }
        // Keep extras in their original order
        extras = [.. extras.OrderBy(pk => all.First(z => ReferenceEquals(z.Pk, pk)).Index)];

        int placedInDex = layout.Count(z => z is not null);
        int extrasInGaps = 0;
        int next = dexSlots;
        foreach (var pk in extras)
        {
            while (next < capacity && layout[next] is not null)
                next++;
            if (next < capacity)
            {
                layout[next++] = pk;
                continue;
            }
            // Not enough room after the Pokédex area: use the last empty Pokédex slots.
            var gap = Array.FindLastIndex(layout, z => z is null);
            layout[gap] = pk;
            extrasInGaps++;
        }

        return new Plan(layout, all.Count, placedInDex, extras.Count, extrasInGaps, dexSlots, dexSlots - placedInDex);
    }

    /// <summary> Writes the planned layout to the boxes. The Pokémon themselves are not modified. </summary>
    public static void Apply(SaveFile sav, Plan plan)
    {
        var settings = new EntityImportSettings(EntityImportOption.Disable, EntityImportOption.Disable, EntityImportOption.Disable);
        var blank = sav.BlankPKM;
        for (int i = 0; i < plan.Layout.Length; i++)
            sav.SetBoxSlotAtIndex(plan.Layout[i] ?? blank, i, settings);
        sav.State.Edited = true;
    }

    public static string Describe(SaveFile sav, Plan plan)
    {
        string Slot(int index)
        {
            sav.GetBoxSlotFromIndex(index, out var box, out var slot);
            return $"Box {box + 1}, slot {slot + 1}";
        }
        var text = $"{plan.Total} Pokémon will be arranged by National Pokédex number:\n" +
                   $"• {plan.InDexSlots} go into their own slot (#001 = Box 1, slot 1 … #{plan.DexSlots:000} = {Slot(plan.DexSlots - 1)}).\n" +
                   $"• {plan.MissingSpecies} species are missing, so their slots stay empty.";
        if (plan.Extras > 0)
            text += $"\n• {plan.Extras} extra copies/eggs go after #{plan.DexSlots:000}, starting at {Slot(plan.DexSlots)}.";
        if (plan.ExtrasInGaps > 0)
            text += $"\n• {plan.ExtrasInGaps} of them don't fit there and fill the last empty Pokédex slots instead.";
        text += "\n\nYour party isn't changed. Nothing is written to the file until you use File → Save.";
        return text;
    }
}
