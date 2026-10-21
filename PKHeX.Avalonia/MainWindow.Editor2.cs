using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Additional Pokémon editor fields: gender, PP Ups, relearn moves, move suggestions, Hyper Training,
/// stat nature (mints), Tera types, met-data suggestions and trade evolutions.
/// </summary>
public partial class MainWindow
{
    private NumericUpDown[] PPUps = [];
    private ComboBox[] RelearnBoxes = [];
    private readonly CheckBox[] HyperBoxes = new CheckBox[6];

    private static readonly string[] HyperNames = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];

    private void InitEditorExtras()
    {
        PPUps = [NUD_PPUp1, NUD_PPUp2, NUD_PPUp3, NUD_PPUp4];
        RelearnBoxes = [CB_Relearn1, CB_Relearn2, CB_Relearn3, CB_Relearn4];
        for (int i = 0; i < HyperBoxes.Length; i++)
        {
            HyperBoxes[i] = new CheckBox { Content = HyperNames[i], Margin = new(0, 0, 12, 0) };
            HyperChecks.Children.Add(HyperBoxes[i]);
        }
        SetSource(CB_Gender, [new("♂ Male", 0), new("♀ Female", 1), new("Genderless", 2)]);

        var types = GameInfo.Strings.types;
        var tera = new List<ComboItem>();
        for (int i = 0; i <= TeraTypeUtil.MaxType && i < types.Length; i++)
            tera.Add(new ComboItem(types[i], i));
        tera.Add(new ComboItem("Stellar", TeraTypeUtil.Stellar));
        SetSource(CB_TeraOriginal, tera);
        SetSource(CB_TeraOverride, [new ComboItem("(not changed)", TeraTypeUtil.OverrideNone), .. tera]);
    }

    private void PopulateExtraSources()
    {
        var src = GameInfo.FilteredSources;
        foreach (var cb in RelearnBoxes)
            SetSource(cb, src.Relearn);
        SetSource(CB_StatNature, src.Natures);
    }

    private static int[] GetPPUps(PKM pk) => [pk.Move1_PPUps, pk.Move2_PPUps, pk.Move3_PPUps, pk.Move4_PPUps];

    private void LoadEditorExtras(PKM pk)
    {
        var format = pk.Format;

        // Gender: only editable when the species can be either gender.
        SetValue(CB_Gender, pk.Gender);
        CB_Gender.IsEnabled = format >= 2 && pk.PersonalInfo.IsDualGender;

        var ppUps = GetPPUps(pk);
        for (int i = 0; i < 4; i++)
            PPUps[i].Value = ppUps[i];

        RelearnPanel.IsVisible = format >= 6;
        if (format >= 6)
        {
            Span<ushort> relearn = stackalloc ushort[4];
            pk.GetRelearnMoves(relearn);
            for (int i = 0; i < 4; i++)
                SetValue(RelearnBoxes[i], relearn[i]);
        }

        HyperPanel.IsVisible = pk is IHyperTrain;
        if (pk is IHyperTrain ht)
        {
            bool[] flags = [ht.HT_HP, ht.HT_ATK, ht.HT_DEF, ht.HT_SPA, ht.HT_SPD, ht.HT_SPE];
            for (int i = 0; i < 6; i++)
                HyperBoxes[i].IsChecked = flags[i];
        }

        bool statNature = format >= 8;
        L_StatNature.IsVisible = CB_StatNature.IsVisible = statNature;
        if (statNature)
            SetValue(CB_StatNature, (int)pk.StatAlignment);

        bool tera = pk is ITeraType;
        L_TeraOriginal.IsVisible = CB_TeraOriginal.IsVisible = L_TeraOverride.IsVisible = CB_TeraOverride.IsVisible = tera;
        if (pk is ITeraType t)
        {
            SetValue(CB_TeraOriginal, (int)t.TeraTypeOriginal);
            SetValue(CB_TeraOverride, (int)t.TeraTypeOverride);
        }
        ExtraStatsGrid.IsVisible = statNature || tera;
    }

    /// <summary> Applies a gender change (may change the PID in Gen 3-5). </summary>
    private void ApplyGender(PKM pk, PKM original)
    {
        if (!CB_Gender.IsEnabled)
            return;
        var gender = (byte)GetValue(CB_Gender);
        if (gender != original.Gender)
            pk.SetGender(gender);
    }

    /// <summary> Applies moves and PP Ups; PP is refilled only when something changed. </summary>
    private void ApplyMoves(PKM pk, PKM original)
    {
        Span<ushort> moves = stackalloc ushort[4];
        Span<ushort> oldMoves = stackalloc ushort[4];
        original.GetMoves(oldMoves);
        for (int i = 0; i < 4; i++)
            moves[i] = (ushort)GetValue(MoveBoxes[i]);
        var ppUps = new int[4];
        for (int i = 0; i < 4; i++)
            ppUps[i] = moves[i] == 0 ? 0 : (int)(PPUps[i].Value ?? 0);

        if (moves.SequenceEqual(oldMoves) && ppUps.AsSpan().SequenceEqual(GetPPUps(original)))
            return;
        pk.SetMoves(moves);
        pk.Move1_PPUps = ppUps[0];
        pk.Move2_PPUps = ppUps[1];
        pk.Move3_PPUps = ppUps[2];
        pk.Move4_PPUps = ppUps[3];
        pk.HealPP();
    }

    /// <summary> Applies relearn moves, Hyper Training, stat nature and Tera types when changed. </summary>
    private void ApplyEditorExtras(PKM pk, PKM original)
    {
        if (pk.Format >= 6)
        {
            Span<ushort> relearn = stackalloc ushort[4];
            Span<ushort> old = stackalloc ushort[4];
            original.GetRelearnMoves(old);
            for (int i = 0; i < 4; i++)
                relearn[i] = (ushort)GetValue(RelearnBoxes[i]);
            if (!relearn.SequenceEqual(old))
                pk.SetRelearnMoves(relearn);
        }

        if (pk is IHyperTrain ht && original is IHyperTrain oht)
        {
            bool[] now = HyperBoxes.Select(z => z.IsChecked == true).ToArray();
            bool[] was = [oht.HT_HP, oht.HT_ATK, oht.HT_DEF, oht.HT_SPA, oht.HT_SPD, oht.HT_SPE];
            if (!now.SequenceEqual(was))
            {
                ht.HT_HP = now[0]; ht.HT_ATK = now[1]; ht.HT_DEF = now[2];
                ht.HT_SPA = now[3]; ht.HT_SPD = now[4]; ht.HT_SPE = now[5];
            }
        }

        if (pk.Format >= 8)
        {
            var statNature = (Nature)GetValue(CB_StatNature);
            if (statNature != original.StatAlignment)
                pk.StatAlignment = statNature;
        }

        if (pk is ITeraType t && original is ITeraType ot)
        {
            var orig = (MoveType)GetValue(CB_TeraOriginal);
            var over = (MoveType)GetValue(CB_TeraOverride);
            if (orig != ot.TeraTypeOriginal)
                t.TeraTypeOriginal = orig;
            if (over != ot.TeraTypeOverride)
                t.TeraTypeOverride = over;
        }
    }

    #region Suggestions

    private async void SuggestMoves(bool random)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
            return;
        Span<ushort> moves = stackalloc ushort[4];
        pk.GetMoveSet(moves, random);
        if (moves[0] == 0)
        {
            await ShowMessage("Moves", "No legal move set could be suggested for this Pokémon.");
            return;
        }
        for (int i = 0; i < 4; i++)
            SetValue(MoveBoxes[i], moves[i]);
        SetStatus("Legal moves filled in. Click \"Save to selected slot\" to keep them.");
    }

    private void Moves_Suggest(object? sender, RoutedEventArgs e) => SuggestMoves(false);
    private void Moves_Random(object? sender, RoutedEventArgs e) => SuggestMoves(true);

    private async void Moves_SuggestRelearn(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0 || pk.Format < 6)
            return;
        Span<ushort> moves = stackalloc ushort[4];
        new LegalityAnalysis(pk).GetSuggestedRelearnMoves(moves);
        for (int i = 0; i < 4; i++)
            SetValue(RelearnBoxes[i], moves[i]);
        await ShowMessage("Relearn moves", "Suggested relearn moves filled in.");
    }

    private async void Met_Suggest(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
            return;
        var encounter = EncounterSuggestion.GetSuggestedMetInfo(pk);
        if (encounter is null || (pk.Format >= 3 && encounter.Location == 0))
        {
            await ShowMessage("Met data", "No suggestion is available for this Pokémon.");
            return;
        }
        var level = encounter.LevelMin;
        int minLevel = EncounterSuggestion.GetLowestLevel(pk, level);
        if (minLevel == 0 || minLevel < level)
            minLevel = level;

        SetValue(CB_MetLocation, encounter.Location);
        NUD_MetLevel.Value = encounter.GetSuggestedMetLevel(pk);
        if ((NUD_Level.Value ?? 1) < minLevel)
            NUD_Level.Value = minLevel;
        var location = CB_MetLocation.SelectedItem is ComboItem c ? c.Text : encounter.Location.ToString();
        SetStatus($"Suggested met data: {location}, met level {NUD_MetLevel.Value}. Click \"Save to selected slot\" to keep it.");
    }

    private void Met_AsEgg(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0 || pk.Format < 4)
            return;
        SetValue(CB_EggLocation, EncounterSuggestion.GetSuggestedEncounterEggLocationEgg(pk));
        CHK_EggDate.IsChecked = true;
        DP_EggDate.SelectedDate = DateTime.Today;
        SetStatus("Egg data filled in (hatched from an egg received today).");
    }

    #endregion

    #region Trade evolution

    /// <summary> Evolutions that need a trade (optionally holding an item). </summary>
    private static readonly (Species From, string? Item, Species To)[] TradeEvolutions =
    [
        (Species.Kadabra, null, Species.Alakazam),
        (Species.Machoke, null, Species.Machamp),
        (Species.Graveler, null, Species.Golem),
        (Species.Haunter, null, Species.Gengar),
        (Species.Onix, "Metal Coat", Species.Steelix),
        (Species.Scyther, "Metal Coat", Species.Scizor),
        (Species.Seadra, "Dragon Scale", Species.Kingdra),
        (Species.Poliwhirl, "King's Rock", Species.Politoed),
        (Species.Slowpoke, "King's Rock", Species.Slowking),
        (Species.Porygon, "Up-Grade", Species.Porygon2),
        (Species.Clamperl, "Deep Sea Tooth", Species.Huntail),
        (Species.Clamperl, "Deep Sea Scale", Species.Gorebyss),
        (Species.Rhydon, "Protector", Species.Rhyperior),
        (Species.Electabuzz, "Electirizer", Species.Electivire),
        (Species.Magmar, "Magmarizer", Species.Magmortar),
        (Species.Porygon2, "Dubious Disc", Species.PorygonZ),
        (Species.Dusclops, "Reaper Cloth", Species.Dusknoir),
        (Species.Feebas, "Prism Scale", Species.Milotic),
        (Species.Boldore, null, Species.Gigalith),
        (Species.Gurdurr, null, Species.Conkeldurr),
        (Species.Spritzee, "Sachet", Species.Aromatisse),
        (Species.Swirlix, "Whipped Dream", Species.Slurpuff),
        (Species.Phantump, null, Species.Trevenant),
        (Species.Pumpkaboo, null, Species.Gourgeist),
    ];

    private async void Menu_TradeEvolve(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
        {
            await ShowMessage("Evolve by trade", "There is no Pokémon in the editor.");
            return;
        }
        if (pk.IsEgg)
        {
            await ShowMessage("Evolve by trade", "Eggs can't evolve.");
            return;
        }

        var items = GameInfo.Strings.GetItemStrings(pk.Context, pk.Version);
        var held = (uint)pk.HeldItem < items.Length ? items[pk.HeldItem] : string.Empty;
        var options = TradeEvolutions.Where(z => (ushort)z.From == pk.Species && (ushort)z.To <= pk.MaxSpeciesID).ToList();
        if (options.Count == 0)
        {
            await ShowMessage("Evolve by trade", $"{GameInfo.Strings.specieslist[pk.Species]} doesn't evolve by trading.");
            return;
        }

        static string Norm(string x) => new string(x.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var match = options.FirstOrDefault(z => z.Item is null || Norm(z.Item) == Norm(held));
        if (match == default)
        {
            var needed = string.Join(" or ", options.Select(z => $"{z.Item} (→ {GameInfo.Strings.specieslist[(int)z.To]})"));
            await ShowMessage("Evolve by trade", $"{GameInfo.Strings.specieslist[pk.Species]} must hold {needed} to evolve by trade. Give it the item on the Main tab first.");
            return;
        }

        bool nicknamed = pk.IsNicknamed;
        int abilitySlot = pk.AbilityNumber switch { 2 => 1, 4 => 2, _ => 0 };
        pk.Species = (ushort)match.To;
        if (match.Item is not null)
            pk.HeldItem = 0; // the item is used up
        if (pk.Format >= 3)
            pk.RefreshAbility(abilitySlot);
        if (!nicknamed)
            pk.ClearNickname();
        if (pk.PartyStatsPresent)
            pk.ResetPartyStats();
        pk.RefreshChecksum();
        LoadEditor(pk);
        SetStatus($"Evolved into {GameInfo.Strings.specieslist[pk.Species]}. Click \"Save to selected slot\" to keep it.");
    }

    #endregion
}
