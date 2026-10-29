using System;
using System.Collections.Generic;
using System.Linq;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Finds a new PID (and the values tied to it) that the game could have generated for the Pokémon's encounter,
/// keeping the edited IVs, nature, gender, ability and shininess when possible.
/// </summary>
/// <remarks>
/// In Generation 3-5 the PID and IVs come from the same random number sequence, and from Generation 6 on many encounters
/// derive PID, IVs, nature, size, etc. from a single seed; editing one of them breaks that link.
/// The encounter templates in PKHeX.Core can generate a matching set from <see cref="EncounterCriteria"/>; this uses them.
/// </remarks>
public static class PidFixer
{
    private static readonly CheckIdentifier[] Related =
    [
        CheckIdentifier.PID, CheckIdentifier.EC, CheckIdentifier.IVs, CheckIdentifier.Nature,
        CheckIdentifier.Gender, CheckIdentifier.Ability, CheckIdentifier.Shiny, CheckIdentifier.Encounter,
    ];

    /// <summary> What has to be kept, from strict to relaxed. </summary>
    public enum Keep
    {
        Everything,
        AllButShiny,
        IVsAndNature,
        IVs,
        NatureGenderAbility,
    }

    public sealed record Fix(PKM Entity, Keep Level, string Changes);

    /// <summary> True when the PID, IVs or something generated with them makes the Pokémon illegal. </summary>
    public static bool HasProblem(PKM pk) => HasProblem(new LegalityAnalysis(pk));

    private static bool HasProblem(LegalityAnalysis la) => la.Results.Any(r => !r.Valid && Related.Contains(r.Identifier));

    /// <summary> Whether this format has a PID at all (Generation 1/2 only have DVs). </summary>
    public static bool IsSupported(PKM pk) => pk.Format >= 3;

    /// <summary>
    /// Returns the best fix for each level of strictness that works, the strictest first (empty if none works).
    /// Stops at the first level that keeps everything.
    /// </summary>
    public static List<Fix> FindFixes(PKM pk)
    {
        var result = new List<Fix>();
        if (!IsSupported(pk))
            return result;
        var la = new LegalityAnalysis(pk);
        if (!HasProblem(la))
            return result;
        var encounters = new[] { la.EncounterMatch, la.EncounterOriginal }
            .OfType<IEncounterConvertible>()
            .Where(e => e is not EncounterInvalid)
            .Distinct()
            .ToList();
        if (encounters.Count == 0)
            return result;

        foreach (var level in Enum.GetValues<Keep>())
        {
            if (level == Keep.AllButShiny && !pk.IsShiny)
                continue; // same as Everything
            var criteria = GetCriteria(pk, level);
            var fix = level == Keep.NatureGenderAbility && !pk.IsShiny
                ? TryNearestIVs(pk, encounters, criteria) ?? TryLevel(pk, encounters, criteria, level)
                : TryLevel(pk, encounters, criteria, level);
            if (fix is null)
                continue;
            result.Add(fix);
            if (level == Keep.Everything)
                break;
        }
        return result;
    }

    private static Fix? TryLevel(PKM pk, List<IEncounterConvertible> encounters, EncounterCriteria criteria, Keep level)
    {
        // Generators that can't satisfy everything fall back to random values, so try a few times.
        const int attempts = 12;
        foreach (var enc in encounters)
        {
            var trainer = new Trainer(pk, enc is IContext ctx ? ctx.Context : pk.Context);
            for (int i = 0; i < attempts; i++)
            {
                PKM gen;
                try
                {
                    gen = enc.ConvertToPKM(trainer, criteria);
                }
                catch (Exception)
                {
                    break; // this encounter can't be generated here
                }
                var candidate = Apply(pk, gen);
                if (!Satisfies(candidate, pk, level))
                    continue;
                if (HasProblem(new LegalityAnalysis(candidate)))
                    continue;
                return new Fix(candidate, level, Describe(pk, candidate));
            }
        }
        return null;
    }

    /// <summary>
    /// Keeps nature, gender and ability, and looks for the legal IV spread closest to the edited one
    /// (the smallest total change first), within a few seconds.
    /// </summary>
    private static Fix? TryNearestIVs(PKM pk, List<IEncounterConvertible> encounters, EncounterCriteria criteria)
    {
        Span<int> target = stackalloc int[6];
        pk.GetIVs(target);
        var wanted = target.ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var enc in encounters)
        {
            var trainer = new Trainer(pk, enc is IContext ctx ? ctx.Context : pk.Context);
            for (int distance = 1; distance <= 12; distance++)
            {
                foreach (var ivs in Neighbors(wanted, distance))
                {
                    if (sw.ElapsedMilliseconds > 4000)
                        return null;
                    var c = criteria with
                    {
                        IV_HP = (sbyte)ivs[0], IV_ATK = (sbyte)ivs[1], IV_DEF = (sbyte)ivs[2],
                        IV_SPE = (sbyte)ivs[3], IV_SPA = (sbyte)ivs[4], IV_SPD = (sbyte)ivs[5],
                    };
                    PKM gen;
                    try
                    {
                        gen = enc.ConvertToPKM(trainer, c);
                    }
                    catch (Exception)
                    {
                        goto nextEncounter;
                    }
                    if (gen.IV_HP != ivs[0] || gen.IV_ATK != ivs[1] || gen.IV_DEF != ivs[2] || gen.IV_SPE != ivs[3] || gen.IV_SPA != ivs[4] || gen.IV_SPD != ivs[5])
                        continue;
                    var candidate = Apply(pk, gen);
                    if (!Satisfies(candidate, pk, Keep.NatureGenderAbility))
                        continue;
                    if (HasProblem(new LegalityAnalysis(candidate)))
                        continue;
                    return new Fix(candidate, Keep.NatureGenderAbility, Describe(pk, candidate));
                }
            }
            nextEncounter:;
        }
        return null;
    }

    /// <summary> All IV spreads (in GetIVs order) whose total difference from <paramref name="start"/> is exactly <paramref name="distance"/>. </summary>
    private static IEnumerable<int[]> Neighbors(int[] start, int distance)
    {
        var current = (int[])start.Clone();
        return Walk(0, distance);

        IEnumerable<int[]> Walk(int stat, int left)
        {
            if (stat == current.Length)
            {
                if (left == 0)
                    yield return (int[])current.Clone();
                yield break;
            }
            for (int delta = -left; delta <= left; delta++)
            {
                var value = start[stat] + delta;
                if ((uint)value > 31)
                    continue;
                current[stat] = value;
                foreach (var r in Walk(stat + 1, left - Math.Abs(delta)))
                    yield return r;
            }
            current[stat] = start[stat];
        }
    }

    private static EncounterCriteria GetCriteria(PKM pk, Keep level)
    {
        bool ivs = level is Keep.Everything or Keep.AllButShiny or Keep.IVsAndNature or Keep.IVs;
        bool nature = level is Keep.Everything or Keep.AllButShiny or Keep.IVsAndNature or Keep.NatureGenderAbility;
        bool other = level is Keep.Everything or Keep.AllButShiny or Keep.NatureGenderAbility;
        bool shiny = level is Keep.Everything or Keep.NatureGenderAbility;
        var c = new EncounterCriteria
        {
            Nature = nature ? pk.Nature : Nature.Random,
            Gender = other && pk.Gender < 2 ? (Gender)pk.Gender : Gender.Random,
            Ability = other && pk.AbilityNumber is 1 or 2 or 4 ? (AbilityPermission)pk.AbilityNumber : AbilityPermission.Any12H,
            Shiny = shiny ? (pk.IsShiny ? Shiny.Always : Shiny.Never) : Shiny.Random,
        };
        if (ivs)
        {
            c = c with
            {
                IV_HP = (sbyte)pk.IV_HP, IV_ATK = (sbyte)pk.IV_ATK, IV_DEF = (sbyte)pk.IV_DEF,
                IV_SPA = (sbyte)pk.IV_SPA, IV_SPD = (sbyte)pk.IV_SPD, IV_SPE = (sbyte)pk.IV_SPE,
            };
        }
        return c;
    }

    /// <summary> The generators may ignore criteria they can't meet, so check what actually came out. </summary>
    private static bool Satisfies(PKM c, PKM pk, Keep level)
    {
        bool ivs = level is Keep.Everything or Keep.AllButShiny or Keep.IVsAndNature or Keep.IVs;
        bool nature = level is Keep.Everything or Keep.AllButShiny or Keep.IVsAndNature or Keep.NatureGenderAbility;
        bool other = level is Keep.Everything or Keep.AllButShiny or Keep.NatureGenderAbility;
        bool shiny = level is Keep.Everything or Keep.NatureGenderAbility;
        if (ivs && c.IV32Compare(pk) is false)
            return false;
        if (nature && c.Nature != pk.Nature)
            return false;
        if (other && (c.Gender != pk.Gender || c.AbilityNumber != pk.AbilityNumber))
            return false;
        if (shiny && c.IsShiny != pk.IsShiny)
            return false;
        return true;
    }

    private static bool IV32Compare(this PKM a, PKM b) =>
        a.IV_HP == b.IV_HP && a.IV_ATK == b.IV_ATK && a.IV_DEF == b.IV_DEF &&
        a.IV_SPA == b.IV_SPA && a.IV_SPD == b.IV_SPD && a.IV_SPE == b.IV_SPE;

    /// <summary> Copies the PID and everything generated together with it onto a copy of <paramref name="pk"/>. </summary>
    private static PKM Apply(PKM pk, PKM gen)
    {
        var c = pk.Clone();
        c.PID = gen.PID;
        if (c.Format >= 6)
            c.EncryptionConstant = gen.Format >= 6 ? gen.EncryptionConstant : gen.PID;
        Span<int> ivs = stackalloc int[6];
        gen.GetIVs(ivs);
        c.SetIVs(ivs);
        bool minted = pk.StatAlignment != pk.Nature;
        c.Nature = gen.Nature;
        if (!minted)
            c.StatAlignment = gen.Nature;
        c.Gender = gen.Gender;
        c.RefreshAbility(gen.AbilityNumber >> 1);
        if (gen is IScaledSize gs && c is IScaledSize cs)
        {
            cs.HeightScalar = gs.HeightScalar;
            cs.WeightScalar = gs.WeightScalar;
        }
        if (gen is IScaledSize3 g3 && c is IScaledSize3 c3)
            c3.Scale = g3.Scale;
        if (gen is ITeraType gt && c is ITeraType ct)
            ct.TeraTypeOriginal = gt.TeraTypeOriginal;
        if (pk.PartyStatsPresent)
            c.ResetPartyStats();
        c.RefreshChecksum();
        return c;
    }

    /// <summary> Human-readable list of what differs apart from the PID. </summary>
    public static string Describe(PKM before, PKM after)
    {
        var s = GameInfo.Strings;
        var lines = new List<string>();
        if (before.Nature != after.Nature)
            lines.Add($"Nature: {s.natures[(int)before.Nature]} → {s.natures[(int)after.Nature]}");
        if (before.Gender != after.Gender)
            lines.Add($"Gender: {GenderName(before.Gender)} → {GenderName(after.Gender)}");
        if (before.Ability != after.Ability)
            lines.Add($"Ability: {s.abilitylist[before.Ability]} → {s.abilitylist[after.Ability]}");
        if (before.IsShiny != after.IsShiny)
            lines.Add($"Shiny: {(before.IsShiny ? "yes" : "no")} → {(after.IsShiny ? "yes" : "no")}");
        if (!before.IV32Compare(after))
            lines.Add($"IVs: {IVText(before)} → {IVText(after)}");
        return lines.Count == 0 ? "Nothing else changes (only the PID)." : string.Join(Environment.NewLine, lines);
    }

    public static string LevelName(Keep level) => level switch
    {
        Keep.Everything => "keeps everything",
        Keep.AllButShiny => "keeps IVs, nature, gender and ability, but not shiny",
        Keep.IVsAndNature => "keeps IVs and nature",
        Keep.IVs => "keeps the IVs",
        _ => "keeps nature, gender and ability, with the closest IVs possible",
    };

    private static string GenderName(byte g) => g switch { 0 => "♂", 1 => "♀", _ => "—" };
    private static string IVText(PKM p) => $"{p.IV_HP}/{p.IV_ATK}/{p.IV_DEF}/{p.IV_SPA}/{p.IV_SPD}/{p.IV_SPE}";

    /// <summary> The Pokémon's original trainer, so shininess and trainer-dependent values match. </summary>
    private sealed class Trainer(PKM pk, EntityContext context) : ITrainerInfo
    {
        public string OT { get; } = pk.OriginalTrainerName;
        public byte Gender { get; } = pk.OriginalTrainerGender;
        public GameVersion Version { get; } = pk.Version;
        public int Language { get; } = pk.Language;
        public byte Generation { get; } = pk.Generation;
        public EntityContext Context { get; } = context;
        public ushort TID16 { get; } = pk.TID16;
        public ushort SID16 { get; } = pk.SID16;
        public uint ID32 { get; } = pk.ID32;
    }
}
