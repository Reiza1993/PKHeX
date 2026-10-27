using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Generation 3 helpers: starter reset, legendary respawn, roamer editor, event islands,
/// National Pokédex unlock and a National Dex completion helper.
/// </summary>
/// <remarks>
/// Flag and variable numbers come from the pret/pokefirered and pret/pokeemerald decompilations.
/// Variables are addressed by their index (game variable 0x4000 + index).
/// </remarks>
public static partial class Gen3Tools
{
    public static Window Create(SAV3 sav, Action onChanged)
    {
        var window = new Window
        {
            Title = $"Gen 3 Tools — {GameInfo.GetVersionName(sav.Version)}",
            Width = 900,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ctx = new Context(sav, window, onChanged);
        var tabs = new TabControl();
        ctx.Tabs = tabs;
        tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, tabs)) // ignore selections bubbling up from lists inside the tabs
                ctx.RefreshAll();
        };
        tabs.Items.Add(new TabItem { Header = "Dex helper", Content = DexHelper(ctx) });
        tabs.Items.Add(new TabItem { Header = "Starters", Content = Starters(ctx) });
        tabs.Items.Add(new TabItem { Header = "Legendaries", Content = Legendaries(ctx) });
        tabs.Items.Add(new TabItem { Header = "Roamer", Content = Roamer(ctx) });
        tabs.Items.Add(new TabItem { Header = "Islands & National Dex", Content = Islands(ctx) });

        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        close.Click += (_, _) => window.Close();
        var panel = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(tabs);
        window.Content = panel;
        return window;
    }

    private sealed record Context(SAV3 Sav, Window Window, Action OnChanged)
    {
        public bool IsFRLG => Sav is SAV3FRLG;
        public bool IsEmerald => Sav is SAV3E;
        public List<Action> Refreshers { get; } = [];
        public TabControl? Tabs { get; set; }

        public void RefreshAll()
        {
            foreach (var r in Refreshers)
                r();
        }

        public void SelectTab(string header)
        {
            if (Tabs is null)
                return;
            foreach (var item in Tabs.Items)
            {
                if (item is TabItem { Header: string h } t && h == header)
                    Tabs.SelectedItem = t;
            }
        }

        public void Changed()
        {
            Sav.State.Edited = true;
            OnChanged();
        }
    }

    #region Shared UI

    private static TextBlock Explain(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new(0, 0, 0, 8),
    };

    private static Control Page(params Control[] children)
    {
        var sp = new StackPanel { Margin = new(8), Spacing = 6 };
        sp.Children.AddRange(children);
        return new ScrollViewer { Content = sp };
    }

    private static Control OnlyFRLG(string feature) => Page(Explain($"{feature} is only available for FireRed / LeafGreen and Emerald saves: the flag numbers it uses are specific to those games."));

    #endregion

    #region Starters

    private const int VarStarter = 0x31;   // VAR_STARTER_MON (0 Bulbasaur, 1 Squirtle, 2 Charmander)
    private const int VarOaksLab = 0x55;   // VAR_MAP_SCENE_PALLET_TOWN_PROFESSOR_OAKS_LAB
    private const int VarCelio = 0x76;     // VAR_MAP_SCENE_ONE_ISLAND_POKEMON_CENTER_1F
    private const int FlagGotPokemon = 0x828;    // FLAG_SYS_POKEMON_GET: received a starter
    private const int FlagGotPokedex = 0x829;    // FLAG_SYS_POKEDEX_GET
    private const int FlagNationalDex = 0x840;   // FLAG_SYS_NATIONAL_DEX
    private const int FlagBeatLabRival = 0x258;  // FLAG_BEAT_RIVAL_IN_OAKS_LAB
    private const int FlagRecoveredSapphire = 0x2DC; // FLAG_RECOVERED_SAPPHIRE
    private const int FlagHideCelio = 0x073;     // FLAG_HIDE_ONE_ISLAND_POKECENTER_CELIO
    private const int ItemSapphire = 374;        // ITEM_SAPPHIRE

    // An earlier version of this tool kept a copy of the original progress in these two variables,
    // which turned out to be VAR_MYSTERY_GIFT_1/2. Clean them up if that copy is still there.
    private const int VarMysteryGift1 = 0xB6, VarMysteryGift2 = 0xB7;

    private static readonly string[] StarterNames = ["Bulbasaur", "Squirtle", "Charmander"];
    private static readonly string[] BeastNames = ["Entei", "Raikou", "Suicune"]; // per starter
    private static readonly ushort[] BeastSpecies = [(ushort)Species.Entei, (ushort)Species.Raikou, (ushort)Species.Suicune];

    private static readonly string[] LabStates =
    [
        "0: not visited yet", "1: Oak walks you in", "2: choosing a starter", "3: starter chosen (rival battle next)",
        "4: rival beaten", "5", "6: Pokédex received", "7", "8", "9: all events done",
    ];

    private static string StarterName(int value) => (uint)value < StarterNames.Length ? StarterNames[value] : $"value {value}";
    private static string BeastFor(int starter) => (uint)starter < BeastNames.Length ? BeastNames[starter] : "Raikou";
    private static string LabName(int value) => (uint)value < LabStates.Length ? LabStates[value] : value.ToString();

    /// <summary> The Oak's Lab value that matches the player's story progress, worked out from the game's own flags. </summary>
    private static int GetProgressLabValue(SAV3 sav)
    {
        if (sav.GetEventFlag(FlagNationalDex))
            return 9;
        if (sav.GetEventFlag(FlagGotPokedex))
            return 6;
        if (sav.GetEventFlag(FlagBeatLabRival))
            return 4;
        return 3;
    }

    private static void CleanupOldStash(SAV3 sav)
    {
        foreach (var v in new[] { VarMysteryGift1, VarMysteryGift2 })
        {
            if ((sav.GetWork(v) & 0xFF00) == 0xA500)
                sav.SetWork(v, 0);
        }
    }

    private static Control Starters(Context ctx)
    {
        if (ctx.IsEmerald)
            return StartersEmerald(ctx);
        if (!ctx.IsFRLG)
            return OnlyFRLG("The starter reset");
        var sav = ctx.Sav;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var finish = new Button { Content = "Finish: restore my story progress" };
        var reset = new Button { Content = "Reset starter selection" };

        void Refresh()
        {
            var lab = sav.GetWork(VarOaksLab);
            var starter = sav.GetWork(VarStarter);
            var target = GetProgressLabValue(sav);
            bool inReset = sav.GetEventFlag(FlagGotPokemon) && lab < target;
            status.Text = $"Oak's Lab: {LabName(lab)}\nStarter on record: {StarterName(starter)} (roaming beast for this starter: {BeastFor(starter)})" +
                          (inReset ? $"\nReset in progress — \"Finish\" will set Oak's Lab back to {LabName(target)}." : string.Empty);
            finish.IsVisible = inReset;
        }
        ctx.Refreshers.Add(Refresh);

        reset.Click += async (_, _) =>
        {
            if (!sav.GetEventFlag(FlagGotPokemon))
            {
                await Ui.ShowMessage(ctx.Window, "Starter reset", "You haven't received a starter yet, so there is nothing to reset. Just pick one in Oak's Lab.");
                return;
            }
            CleanupOldStash(sav);
            sav.SetWork(VarOaksLab, 2);         // starter selection open
            sav.SetEventFlag(0x028, false);       // show Bulbasaur's ball
            sav.SetEventFlag(0x029, false);       // show Squirtle's ball
            sav.SetEventFlag(0x02A, false);       // show Charmander's ball
            sav.SetEventFlag(0x02B, false);       // show Prof. Oak in his lab
            sav.SetEventFlag(0x02D, false);       // show Blue in the lab (he takes a ball after you)
            ctx.Changed();
            ctx.RefreshAll();
            await Ui.ShowMessage(ctx.Window, "Starter reset", "The three Poké Balls are back on Oak's table. Now:\n\n" +
                "1. Click File → Save, and load the save in your game.\n" +
                "2. Go into Oak's Lab (if you're already inside, leave and come back in).\n" +
                "3. Pick a ball. Blue takes another one, and battles you on your way out.\n" +
                "4. Don't talk to Prof. Oak until you've pressed \"Finish\" (he would hand out the Pokédex again).\n" +
                "5. Save in-game, reopen the save here, and press Reset again for the next starter (3 rounds gets you all three).\n" +
                "6. When you're done, press \"Finish\". The starter you picked LAST counts as your starter from then on " +
                "(Blue's team and the roaming beast follow it).");
        };

        finish.Click += async (_, _) =>
        {
            var target = GetProgressLabValue(sav);
            sav.SetWork(VarOaksLab, (ushort)target);
            CleanupOldStash(sav);
            ctx.Changed();
            ctx.RefreshAll();
            var starter = sav.GetWork(VarStarter);
            await Ui.ShowMessage(ctx.Window, "Starter reset", $"Story progress restored (Oak's Lab: {LabName(target)}).\n\n" +
                $"Your starter on record is now {StarterName(starter)}, so the roaming beast for you is {BeastFor(starter)}. " +
                "To get that beast roaming, see the Roamer tab. Click File → Save.");
        };

        Refresh();
        return Page(
            Explain("Lets you pick the starters again so you can collect all three (Bulbasaur, Squirtle and Charmander), " +
                    "or switch your starter to change which legendary beast roams (Bulbasaur → Entei, Squirtle → Raikou, Charmander → Suicune).\n\n" +
                    "How it works: Prof. Oak's Lab has a progress value; at 2 the Poké Balls can be picked. Reset puts it back to 2 and makes the balls, Oak and Blue reappear. " +
                    "Each pick gives one starter (Blue takes another ball). \"Finish\" puts your story progress back, worked out from the game's own story flags (Pokédex, National Pokédex, rival beaten), so nothing extra is stored in your save. " +
                    "Try it on a copy of your save first."),
            status, Ui.Row(reset, finish));
    }

    #endregion

    #region Legendaries

    private sealed record Legend(string Name, string Where, string Needs, int[] DoneFlags, int[] Flags);

    // DoneFlags: flags meaning "caught / beaten". Flags: all flags cleared to make it reappear.
    private static readonly Legend[] Legends =
    [
        new("Mewtwo", "Cerulean Cave B1F", "After becoming Champion", [0x2BC], [0x2BC, 0x081]),
        new("Moltres", "Mt. Ember summit", "", [0x2BD], [0x2BD, 0x052]),
        new("Articuno", "Seafoam Islands B4F", "", [0x2BE], [0x2BE, 0x082]),
        new("Zapdos", "Power Plant", "", [0x2BF], [0x2BF, 0x05D]),
        new("Electrode (south)", "Power Plant", "", [0x2D0], [0x2D0, 0x085]),
        new("Electrode (north)", "Power Plant", "", [0x2D1], [0x2D1, 0x086]),
        new("Snorlax", "Route 12", "Poké Flute", [0x054], [0x054, 0x253]),
        new("Snorlax", "Route 16", "Poké Flute", [0x080], [0x080]),
        new("Lugia", "Navel Rock (base)", "Mystic Ticket", [0x2F2, 0x2F5], [0x2F2, 0x2F5, 0x09B]),
        new("Ho-Oh", "Navel Rock (summit)", "Mystic Ticket", [0x2F3, 0x2F6], [0x2F3, 0x2F6, 0x09C]),
        new("Deoxys", "Birth Island", "Aurora Ticket", [0x2E4, 0x2F7], [0x2E4, 0x2F7, 0x099]),
    ];

    private static Control Legendaries(Context ctx)
    {
        if (!ctx.IsFRLG && !ctx.IsEmerald)
            return OnlyFRLG("Legendary respawn");
        var sav = ctx.Sav;
        var legends = ctx.IsEmerald ? LegendsEmerald : Legends;
        var list = new TextBlock { FontFamily = new FontFamily("monospace"), Margin = new(0, 4) };
        void Refresh()
        {
            var lines = legends.Select(l =>
            {
                var done = l.DoneFlags.Any(sav.GetEventFlag);
                var state = done ? "gone (caught/beaten)" : "available";
                var needs = l.Needs.Length == 0 ? string.Empty : $"  needs: {l.Needs}";
                return $"{l.Name,-18} {l.Where,-22} {state,-21}{needs}";
            });
            list.Text = string.Join(Environment.NewLine, lines);
        }

        var button = new Button { Content = "Respawn all legendaries & special Pokémon" };
        button.Click += async (_, _) =>
        {
            foreach (var l in legends)
            {
                foreach (var f in l.Flags)
                    sav.SetEventFlag(f, false);
            }
            ctx.Changed();
            Refresh();
            await Ui.ShowMessage(ctx.Window, "Legendaries", "All of them are back. Click File → Save, then in your game walk into each area again (leave and re-enter the map) for them to appear.\n\n" +
                (ctx.IsEmerald ? "They live on the event islands, which need their tickets (see the \"Islands & National Dex\" tab)." : "Lugia, Ho-Oh and Deoxys also need the event islands to be unlocked (see the \"Islands & National Dex\" tab)."));
        };

        Refresh();
        if (ctx.IsEmerald)
        {
            return Page(
                Explain("Makes the event-island Pokémon available again: Latios/Latias on Southern Island, Deoxys on Birth Island, Mew on Faraway Island, and Lugia and Ho-Oh at Navel Rock.\n\n" +
                        "How it works: the game marks each one with a \"caught\" and a \"beaten\" flag. Clearing them makes it reappear the next time you enter its map. " +
                        "Southern Island always has the Lati that is NOT roaming (Latias roaming → Latios on the island, and the other way round). " +
                        "The other one-time Pokémon of Emerald (Groudon, Kyogre, Rayquaza, the Regis…) aren't covered here."),
                button, Ui.Header("Current state"), list);
        }
        return Page(
            Explain("Makes every one-time Pokémon encounter available again: Mewtwo, Moltres, Articuno, Zapdos, the two Power Plant Electrode, both Snorlax, Lugia, Ho-Oh and Deoxys.\n\n" +
                    "How it works: the game marks each one with a \"fought\" flag (and Lugia/Ho-Oh/Deoxys with a \"flew away\" flag if beaten instead of caught). Clearing those flags makes the game put the Pokémon back the next time you enter its map. Pokémon you already caught stay in your boxes."),
            button, Ui.Header("Current state"), list);
    }

    #endregion

    #region Roamer

    private static Control Roamer(Context ctx)
    {
        var sav = ctx.Sav;
        var roamer = new Roamer3(sav.LargeBlock);
        var form = new Ui.Form();

        var species = form.Add("Species", new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        var level = form.Add("Level", Ui.Number(1, 100));
        var hp = form.Add("Current HP", Ui.Number(0, 999));
        var active = form.Add("Roaming (active)", new CheckBox());
        var pid = form.Add("PID (hex)", new TextBox { MaxLength = 8 });
        var shiny = form.Add("Shiny", new TextBlock { VerticalAlignment = VerticalAlignment.Center });
        var ivs = new NumericUpDown[6];
        string[] ivNames = ["HP", "Attack", "Defense", "Speed", "Sp. Atk", "Sp. Def"];
        for (int i = 0; i < 6; i++)
            ivs[i] = form.Add($"IV {ivNames[i]}", Ui.Number(0, 31));
        var glitch = form.AddWide(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

        int[] ToRoamerOrder(NumericUpDown[] n) => [(int)(n[0].Value ?? 0), (int)(n[1].Value ?? 0), (int)(n[2].Value ?? 0), (int)(n[3].Value ?? 0), (int)(n[4].Value ?? 0), (int)(n[5].Value ?? 0)];

        void UpdateShiny()
        {
            var ok = uint.TryParse(pid.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p);
            shiny.Text = !ok ? "(invalid PID)" : Roamer3.IsShiny(p, sav) ? "★ Yes" : "No";
        }

        void UpdateGlitch()
        {
            if (!roamer.IsGlitched)
            {
                glitch.Text = "This game loads the roamer's IVs correctly.";
                return;
            }
            var tmp = new Roamer3(new byte[Roamer3.SIZE], true) { IVs = ToRoamerOrder(ivs) };
            var g = tmp.IVsGlitch;
            glitch.Text = $"IVs you will actually get (game bug in FireRed/LeafGreen and Ruby/Sapphire — only the first byte of the IVs is used): " +
                          $"HP {g[0]}, Atk {g[1]}, Def {g[2]}, Spe {g[3]}, SpA {g[4]}, SpD {g[5]}.";
        }

        void Load()
        {
            var names = GameInfo.Strings.specieslist;
            species.Text = roamer.Species == 0 ? "(none yet — the roaming event hasn't happened)" : names[roamer.Species];
            level.Value = Math.Max((byte)1, roamer.CurrentLevel);
            hp.Value = roamer.HP_Current;
            active.IsChecked = roamer.IsActive;
            pid.Text = roamer.PID.ToString("X8");
            var v = roamer.IVs;
            for (int i = 0; i < 6; i++)
                ivs[i].Value = v[i];
            UpdateShiny();
            UpdateGlitch();
        }

        foreach (var nud in ivs)
            nud.ValueChanged += (_, _) => UpdateGlitch();
        pid.TextChanged += (_, _) => UpdateShiny();

        ushort MaxHP()
        {
            var pk = new PK3 { Species = roamer.Species };
            pk.CurrentLevel = (byte)(level.Value ?? 50);
            pk.SetIVs(ToRoamerOrder(ivs));
            return pk.GetStats(pk.PersonalInfo)[0];
        }

        var heal = Ui.Button("Full HP", (_, _) => hp.Value = MaxHP());
        var reroll = Ui.Button("New random PID & IVs", (_, _) =>
        {
            pid.Text = Util.Rand32().ToString("X8");
            var rnd = Util.Rand;
            foreach (var nud in ivs)
                nud.Value = rnd.Next(32);
        });
        var makeShiny = Ui.Button("Make shiny", (_, _) =>
        {
            uint low = (uint)Util.Rand.Next(0x10000);
            uint high = (uint)(sav.TID16 ^ sav.SID16 ^ low) & 0xFFF8u | (uint)Util.Rand.Next(8);
            pid.Text = ((high << 16) | low).ToString("X8");
        });
        var save = Ui.Button("Save roamer", async (_, _) =>
        {
            if (roamer.Species == 0)
            {
                await Ui.ShowMessage(ctx.Window, "Roamer", "There is no roaming Pokémon yet. It's created by the game when the roaming event happens.");
                return;
            }
            if (!uint.TryParse(pid.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p))
            {
                await Ui.ShowMessage(ctx.Window, "Roamer", "The PID must be 8 hexadecimal digits.");
                return;
            }
            roamer.CurrentLevel = (byte)(level.Value ?? 50);
            roamer.PID = p;
            roamer.IVs = ToRoamerOrder(ivs);
            var max = MaxHP();
            var newHp = (ushort)(hp.Value ?? 0);
            if (active.IsChecked == true && (newHp == 0 || newHp > max))
                newHp = max; // a roaming Pokémon must have HP left
            roamer.HP_Current = newHp;
            roamer.Status = 0;
            roamer.IsActive = active.IsChecked == true;
            ctx.Changed();
            Load();
            await Ui.ShowMessage(ctx.Window, "Roamer", "Roamer saved. Click File → Save.");
        });

        Load();
        ctx.Refreshers.Add(Load);

        if (ctx.IsEmerald)
            return RoamerEmerald(ctx, form.Grid, Ui.Row(save, heal, reroll, makeShiny));
        if (!ctx.IsFRLG)
        {
            return Page(
                Explain("The roaming Pokémon (Latios or Latias) appears after the Hall of Fame. Its species is set by the game. " +
                        "To make it roam again after you caught or knocked it out, tick \"Roaming\" and save — HP is refilled automatically."),
                form.Grid, Ui.Row(save, heal, reroll, makeShiny));
        }

        // FireRed/LeafGreen: change the beast the way the game does it.
        var beastStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var toStarters = Ui.Button("Step 1: Reset starter selection (opens the Starters tab)", (_, _) => ctx.SelectTab("Starters"));
        var redoSapphire = new Button { Content = "Step 2: Let me give Celio the Sapphire again" };

        void RefreshBeast()
        {
            var starter = sav.GetWork(VarStarter);
            var celio = sav.GetWork(VarCelio);
            var current = roamer.Species == 0 ? "none yet" : GameInfo.Strings.specieslist[roamer.Species];
            string sapphire = celio >= 6
                ? "Sapphire already given to Celio (the beast was created then)."
                : sav.GetEventFlag(FlagRecoveredSapphire) ? "Sapphire recovered — give it to Celio in the One Island Pokémon Center." : "Sapphire not recovered yet.";
            beastStatus.Text = $"Starter on record: {StarterName(starter)} → the game will create: {BeastFor(starter)}\n" +
                               $"Roaming now: {current}{(roamer.IsActive ? " (roaming)" : roamer.Species != 0 ? " (caught / not roaming)" : string.Empty)}\n" +
                               sapphire;
            redoSapphire.IsEnabled = celio >= 6;
        }
        ctx.Refreshers.Add(RefreshBeast);

        redoSapphire.Click += async (_, _) =>
        {
            sav.SetWork(VarCelio, 5);                   // "Received Rainbow Pass": Celio is waiting for the Sapphire again
            sav.SetEventFlag(FlagRecoveredSapphire, true);
            sav.SetEventFlag(FlagHideCelio, false);       // make sure Celio is there
            var item = GiveKeyItems(sav, (ItemSapphire, "Sapphire"));
            ctx.Changed();
            ctx.RefreshAll();
            var starter = sav.GetWork(VarStarter);
            await Ui.ShowMessage(ctx.Window, "Roaming beast", $"Celio is waiting for the Sapphire again. {item}\n\n" +
                "1. Click File → Save and load the save in your game.\n" +
                "2. Go to the One Island Pokémon Center and talk to Celio — you hand over the Sapphire again.\n" +
                $"3. The game then creates a new roaming beast from your starter on record: {BeastFor(starter)} ({StarterName(starter)}). " +
                "It replaces the one that was roaming before.");
        };

        RefreshBeast();
        return Page(
            Explain("Which legendary beast roams Kanto is decided by the game when you give Celio the Sapphire, based on your starter: " +
                    "Bulbasaur → Entei, Squirtle → Raikou, Charmander → Suicune.\n\n" +
                    "To get a different beast: (1) reset the starter selection and pick the starter for the beast you want, press Finish; " +
                    "(2) press \"Let me give Celio the Sapphire again\", then hand him the Sapphire in your game."),
            beastStatus, toStarters, redoSapphire,
            Ui.Header("Current roaming Pokémon"),
            Explain("To make it roam again after you caught or knocked it out, tick \"Roaming\" and save — HP is refilled automatically."),
            form.Grid, Ui.Row(save, heal, reroll, makeShiny));
    }

    #endregion

    #region Islands & National Dex

    private static Control Islands(Context ctx)
    {
        if (ctx.IsEmerald)
            return IslandsEmerald(ctx);
        var sav = ctx.Sav;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };

        void Refresh()
        {
            var lines = new List<string> { $"National Pokédex: {(sav.NationalDex ? "unlocked" : "locked")}" };
            if (ctx.IsFRLG)
            {
                lines.Add($"Navel Rock (Lugia, Ho-Oh): {(sav.GetEventFlag(0x84A) ? "unlocked" : "locked")}");
                lines.Add($"Birth Island (Deoxys): {(sav.GetEventFlag(0x84B) ? "unlocked" : "locked")}");
                var network = sav.GetWork(0x76);
                lines.Add($"Rainbow Pass (needed to sail there): {(network >= 5 ? "yes" : "not yet — continue the Sevii Islands story")}");
            }
            status.Text = string.Join(Environment.NewLine, lines);
        }

        var dex = new Button { Content = "Unlock National Pokédex" };
        dex.Click += async (_, _) =>
        {
            sav.NationalDex = true;
            ctx.Changed();
            Refresh();
            await Ui.ShowMessage(ctx.Window, "National Pokédex", "National Pokédex unlocked. Click File → Save.");
        };

        var islands = new Button { Content = "Unlock Navel Rock & Birth Island (Mystic and Aurora Ticket)", IsVisible = ctx.IsFRLG };
        islands.Click += async (_, _) =>
        {
            sav.SetEventFlag(0x2A7, true); // FLAG_RECEIVED_AURORA_TICKET
            sav.SetEventFlag(0x2A8, true); // FLAG_RECEIVED_MYSTIC_TICKET
            sav.SetEventFlag(0x84A, true); // FLAG_ENABLE_SHIP_NAVEL_ROCK
            sav.SetEventFlag(0x84B, true); // FLAG_ENABLE_SHIP_BIRTH_ISLAND
            var added = GiveKeyItems(sav, (370, "Mystic Ticket"), (371, "Aurora Ticket"));
            ctx.Changed();
            Refresh();
            await Ui.ShowMessage(ctx.Window, "Event islands", $"Unlocked. {added}\n\nClick File → Save. In your game, go to the Vermilion City harbor and show a ticket to the sailor to take the Seagallop to Navel Rock or Birth Island. You need the Rainbow Pass first.");
        };

        Refresh();
        return Page(
            Explain("National Pokédex: needed to register and evolve Pokémon from outside Kanto (for example Golbat → Crobat, or breeding Pichu). " +
                    "Normally Prof. Oak gives it after the Hall of Fame once you've caught 60 Pokémon."),
            dex,
            Explain(ctx.IsFRLG
                ? "\nEvent islands: Lugia, Ho-Oh and Deoxys were only obtainable with the Mystic Ticket and Aurora Ticket, given out at Nintendo events. " +
                  "This sets the same flags the event did and puts both tickets in your Key Items."
                : "\nEvent islands (Mystic/Aurora Ticket) are only handled for FireRed/LeafGreen and Emerald here."),
            islands, Ui.Header("Current state"), status);
    }

    /// <summary> Adds key items by their Gen 3 item number (Mystic Ticket = 370, Aurora Ticket = 371). </summary>
    private static string GiveKeyItems(SAV3 sav, params (int Id, string Name)[] items)
    {
        var bag = sav.Inventory;
        var pouch = bag.Pouches.FirstOrDefault(p => p.Type == InventoryType.KeyItems);
        if (pouch is null)
            return "Couldn't find the Key Items pocket; add the tickets in Tools → Bag.";
        var result = new List<string>();
        foreach (var (id, name) in items)
        {
            if (pouch.Items.Any(i => i.Index == id))
            {
                result.Add($"{name}: already in your bag.");
                continue;
            }
            var slot = Array.FindIndex(pouch.Items, i => i.Index == 0);
            if (slot < 0)
            {
                result.Add($"{name}: Key Items pocket is full.");
                continue;
            }
            pouch.Items[slot] = pouch.GetEmpty(id, 1);
            result.Add($"{name}: added to Key Items.");
        }
        bag.CopyTo(sav);
        return string.Join(" ", result);
    }

    #endregion

    #region Dex helper

    private sealed record DexRow(ushort Species, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record EncRow(IEncounterable Encounter, string Text)
    {
        public override string ToString() => Text;
    }

    private static readonly GameVersion[] Gen3Versions = [GameVersion.FR, GameVersion.LG, GameVersion.R, GameVersion.S, GameVersion.E, GameVersion.CXD];

    private static Control DexHelper(Context ctx)
    {
        var sav = ctx.Sav;
        var names = GameInfo.Strings.specieslist;
        var summary = new TextBlock { FontWeight = FontWeight.SemiBold };
        var filter = Ui.Combo([new("Not caught yet", 0), new("Seen but not caught", 1), new("All", 2)]);
        filter.SelectedIndex = 0;
        filter.Width = 200;
        var speciesList = new ListBox { MinWidth = 260 };
        var title = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 15 };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var encList = new ListBox();
        var add = new Button { Content = "Add a legal one to my boxes", IsEnabled = false };

        string State(ushort s) => sav.GetCaught(s) ? "✓" : sav.GetSeen(s) ? "seen" : "—";

        void RefreshList()
        {
            int caught = 0, kanto = 0;
            for (ushort s = 1; s <= 386; s++)
            {
                if (!sav.GetCaught(s))
                    continue;
                caught++;
                if (s <= 151)
                    kanto++;
            }
            summary.Text = $"Caught: {caught} / 386 National · {kanto} / 151 Kanto";
            var mode = Ui.GetValue(filter);
            var rows = new List<DexRow>();
            for (ushort s = 1; s <= 386; s++)
            {
                bool c = sav.GetCaught(s), seen = sav.GetSeen(s);
                if (mode == 0 && c) continue;
                if (mode == 1 && (c || !seen)) continue;
                rows.Add(new DexRow(s, $"{s:000} {names[s]}  {State(s)}"));
            }
            speciesList.ItemsSource = rows;
        }

        filter.SelectionChanged += (_, _) => RefreshList();

        speciesList.SelectionChanged += async (_, _) =>
        {
            if (speciesList.SelectedItem is not DexRow row)
                return;
            var s = row.Species;
            title.Text = $"#{s:000} {names[s]}";
            hint.Text = "Looking up where to get it…";
            encList.ItemsSource = null;
            add.IsEnabled = false;
            var rows = await Task.Run(() => FindEncounters(sav, s));
            if (!ReferenceEquals(speciesList.SelectedItem, row))
                return; // selection changed meanwhile
            encList.ItemsSource = rows;
            hint.Text = BuildHint(sav, s, rows);
        };

        encList.SelectionChanged += (_, _) => add.IsEnabled = encList.SelectedItem is EncRow;

        add.Click += async (_, _) =>
        {
            if (encList.SelectedItem is not EncRow er)
                return;
            var message = AddToBoxes(sav, er.Encounter);
            ctx.Changed();
            RefreshList();
            await Ui.ShowMessage(ctx.Window, "Dex helper", message);
        };

        RefreshList();

        var left = new DockPanel();
        var leftTop = new StackPanel { Spacing = 4, Margin = new(0, 0, 0, 4), Children = { summary, filter } };
        DockPanel.SetDock(leftTop, Dock.Top);
        left.Children.Add(leftTop);
        left.Children.Add(speciesList);

        var right = new DockPanel { Margin = new(12, 0, 0, 0) };
        var rightTop = new StackPanel { Spacing = 4, Children = { title, hint, Ui.Header("Where to get it (select one to add a legal copy)") } };
        var rightBottom = new StackPanel { Spacing = 4, Margin = new(0, 6, 0, 0), Children =
        {
            add,
            new TextBlock
            {
                Text = "\"Add\" creates a legal Pokémon from the selected encounter with your trainer details and puts it in the first empty box slot (this also marks it caught in the Pokédex). " +
                       "Encounters from other games (Ruby, Sapphire, Emerald, Colosseum/XD) are what you'd normally trade over.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            },
        } };
        DockPanel.SetDock(rightTop, Dock.Top);
        DockPanel.SetDock(rightBottom, Dock.Bottom);
        right.Children.Add(rightTop);
        right.Children.Add(rightBottom);
        right.Children.Add(encList);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static List<EncRow> FindEncounters(SAV3 sav, ushort species)
    {
        var rows = new List<EncRow>();
        var seen = new HashSet<string>();
        var names = GameInfo.Strings.specieslist;
        try
        {
            var pk = sav.BlankPKM;
            pk.Species = species;
            pk.Form = 0;
            pk.SetGender(pk.GetSaneGender());
            EncounterMovesetGenerator.OptimizeCriteria(pk, sav);
            foreach (var enc in EncounterMovesetGenerator.GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, Gen3Versions))
            {
                var game = enc.Version == GameVersion.CXD ? "Colosseum/XD" : GameInfo.GetVersionName(enc.Version);
                string where;
                try { where = enc.Location == 0 ? "—" : GameInfo.GetLocationName(false, enc.Location, 3, 3, enc.Version); }
                catch { where = $"location {enc.Location}"; }
                var lvl = enc.LevelMin == enc.LevelMax ? $"Lv. {enc.LevelMin}" : $"Lv. {enc.LevelMin}–{enc.LevelMax}";
                var as_ = enc.Species != species ? $" as {names[enc.Species]} (then evolve)" : string.Empty;
                var text = $"{game}: {enc.LongName}{as_} — {where}, {lvl}";
                if (!seen.Add(text))
                    continue;
                rows.Add(new EncRow(enc, text));
                if (rows.Count >= 300)
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return [.. rows.OrderBy(r => Rank(sav, species, r.Encounter)).ThenBy(r => r.Text, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 0: catch it in your game · 1: catch a pre-evolution in your game · 2: other Kanto version
    /// 3: Ruby/Sapphire/Emerald/Colosseum/XD · 4: event distribution · 5: breeding only
    /// </summary>
    private static int Rank(SAV3 sav, ushort species, IEncounterable enc)
    {
        if (enc.IsEgg)
            return 5;
        bool own = enc.Version == sav.Version || (enc.Version == GameVersion.FRLG && sav.Version is GameVersion.FR or GameVersion.LG)
                   || (enc.Version == GameVersion.RS && sav.Version is GameVersion.R or GameVersion.S);
        if (enc is MysteryGift)
            return 4;
        if (enc is IFatefulEncounterReadOnly { FatefulEncounter: true })
            return own ? 1 : 4; // e.g. Deoxys / Lugia / Ho-Oh on the event islands of your own game
        if (own)
            return enc.Species == species ? 0 : 1;
        if (enc.Version is GameVersion.FR or GameVersion.LG)
            return 2;
        return 3;
    }

    private static string BuildHint(SAV3 sav, ushort species, List<EncRow> rows)
    {
        var gameName = GameInfo.GetVersionName(sav.Version);
        var best = rows.Count == 0 ? -1 : rows.Min(r => Rank(sav, species, r.Encounter));
        string text = best switch
        {
            -1 => "No Generation 3 encounter found.",
            0 => $"You can catch it in your game ({gameName}).",
            1 => $"In {gameName}, catch an earlier evolution and evolve it.",
            2 => $"Not in {gameName} — it's exclusive to the other Kanto version (FireRed vs. LeafGreen).",
            3 => $"Not obtainable in {gameName}: it normally comes from Ruby/Sapphire/Emerald or Colosseum/XD by trade.",
            4 => "Event-only: it was given out at Nintendo events.",
            _ => "Only obtainable by breeding — you need one of its family first.",
        };
        if (species is (ushort)Species.Alakazam or (ushort)Species.Machamp or (ushort)Species.Golem or (ushort)Species.Gengar
            or (ushort)Species.Steelix or (ushort)Species.Scizor or (ushort)Species.Kingdra or (ushort)Species.Politoed
            or (ushort)Species.Slowking or (ushort)Species.Porygon2 or (ushort)Species.Huntail or (ushort)Species.Gorebyss)
            text += " It evolves by trading: catch the pre-evolution, then use Pokémon → Evolve by trade in the editor.";
        if (species > 151 && !sav.NationalDex)
            text += " Tip: unlock the National Pokédex (Islands & National Dex tab) so non-Kanto Pokémon can be registered and evolve.";
        return text;
    }

    private static string AddToBoxes(SAV3 sav, IEncounterable enc)
    {
        PKM created;
        try
        {
            created = enc.ConvertToPKM(sav);
        }
        catch (Exception ex)
        {
            return $"Couldn't create this encounter: {ex.Message}";
        }
        var pk = EntityConverter.ConvertToType(created, sav.PKMType, out var result);
        if (pk is null)
            return result.GetDisplayString(created, sav.PKMType);
        sav.AdaptToSaveFile(pk);
        pk.RefreshChecksum();

        var index = sav.NextOpenBoxSlot();
        if (index < 0)
            return "Your boxes are full.";
        sav.SetBoxSlotAtIndex(pk, index);
        sav.GetBoxSlotFromIndex(index, out var box, out var slot);
        var la = new LegalityAnalysis(pk);
        var name = GameInfo.Strings.specieslist[pk.Species];
        return $"{name} (Lv. {pk.CurrentLevel}) added to Box {box + 1}, slot {slot + 1}. Legality: {(la.Valid ? "legal ✓" : "not legal — " + la.Report())}\n\nClick File → Save.";
    }

    #endregion
}
