using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Emerald versions of the Gen 3 tools: starter reset (Hoenn and Johto), Latios/Latias re-pick and the event islands.
/// </summary>
/// <remarks>
/// Flag and variable numbers come from the pret/pokeemerald decompilation (include/constants/flags.h and vars.h, and the map scripts).
/// </remarks>
public static partial class Gen3Tools
{
    #region Emerald constants

    private const int EFlagPokemonGet = 0x860;   // FLAG_SYS_POKEMON_GET
    private const int EFlagPokedexGet = 0x861;   // FLAG_SYS_POKEDEX_GET
    private const int EFlagGameClear = 0x864;    // FLAG_SYS_GAME_CLEAR: entered the Hall of Fame

    private const int EVarStarter = 0x23;        // VAR_STARTER_MON (0 Treecko, 1 Torchic, 2 Mudkip)
    private const int EVarRoute101 = 0x60;       // VAR_ROUTE101_STATE (0 new, 1 entered, 2 rescue scene, 3 starter chosen)
    private const int EVarBirchLab = 0x84;       // VAR_BIRCH_LAB_STATE (2 chose starter … 5 received Pokédex)
    private const int EVarHousesMay = 0x82;      // VAR_LITTLEROOT_HOUSES_STATE_MAY
    private const int EVarHousesBrendan = 0x8C;  // VAR_LITTLEROOT_HOUSES_STATE_BRENDAN (3 entered Hall of Fame, 4 Lati TV seen, 5 left house)
    private const int EVarJohto = 0xD3;          // VAR_DEX_UPGRADE_JOHTO_STARTER_STATE (4 offered, 5 choosing, 6 chosen)
    private const int EVarRoamer = 0xD5;         // VAR_ROAMER_POKEMON (0 Latias, 1 Latios)

    private const int EFlagHideBag = 0x2BC;          // FLAG_HIDE_ROUTE_101_BIRCH_STARTERS_BAG
    private const int EFlagHideBattleBirch = 0x2D0;  // FLAG_HIDE_ROUTE_101_BIRCH_ZIGZAGOON_BATTLE
    private const int EFlagHideZigzagoon = 0x2EE;    // FLAG_HIDE_ROUTE_101_ZIGZAGOON
    private const int EFlagHideDad = 0x2DE;          // FLAG_HIDE_PLAYERS_HOUSE_DAD
    private static readonly int[] EFlagHideJohtoBalls = [0x32B, 0x32C, 0x346]; // Cyndaquil, Totodile, Chikorita Poké Balls

    private static readonly string[] HoennStarterNames = ["Treecko", "Torchic", "Mudkip"];
    private static readonly string[] LatiNames = ["Latias", "Latios"];

    private static string HoennStarterName(int value) => (uint)value < HoennStarterNames.Length ? HoennStarterNames[value] : $"value {value}";
    private static string LatiName(int value) => (uint)value < LatiNames.Length ? LatiNames[value] : $"value {value}";
    private static string OtherLati(int value) => value == 0 ? "Latios" : "Latias";

    // DoneFlags: caught / beaten. Flags: everything cleared to bring it back (the map scripts re-derive the "hide" flags).
    private static readonly Legend[] LegendsEmerald =
    [
        new("Latios / Latias", "Southern Island", "Eon Ticket", [0x1C8, 0x1C9], [0x1C8, 0x1C9]),
        new("Deoxys", "Birth Island", "Aurora Ticket", [0x1AC, 0x1AD], [0x1AC, 0x1AD]),
        new("Mew", "Faraway Island", "Old Sea Map", [0x1C7, 0x1CA], [0x1C7, 0x1CA]),
        new("Lugia", "Navel Rock (base)", "Mystic Ticket", [0x091, 0x1DD], [0x091, 0x1DD]),
        new("Ho-Oh", "Navel Rock (summit)", "Mystic Ticket", [0x092, 0x1DC], [0x092, 0x1DC]),
    ];

    private sealed record Island(string Name, string Pokemon, int ShipFlag, int ReceivedFlag, int Item, string ItemName);

    private static readonly Island[] IslandsE =
    [
        new("Southern Island", "Latios / Latias", 0x8B3, -1, 275, "Eon Ticket"),       // FLAG_ENABLE_SHIP_SOUTHERN_ISLAND
        new("Navel Rock", "Lugia, Ho-Oh", 0x8E0, 0x13B, 370, "Mystic Ticket"),          // FLAG_ENABLE_SHIP_NAVEL_ROCK, FLAG_RECEIVED_MYSTIC_TICKET
        new("Birth Island", "Deoxys", 0x8D5, 0x13A, 371, "Aurora Ticket"),              // FLAG_ENABLE_SHIP_BIRTH_ISLAND, FLAG_RECEIVED_AURORA_TICKET
        new("Faraway Island", "Mew", 0x8D6, 0x13C, 376, "Old Sea Map"),                 // FLAG_ENABLE_SHIP_FARAWAY_ISLAND, FLAG_RECEIVED_OLD_SEA_MAP
    ];

    #endregion

    #region Emerald starters

    private static Control StartersEmerald(Context ctx)
    {
        var sav = ctx.Sav;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var reset = new Button { Content = "Reset starter selection (Route 101)" };
        var finish = new Button { Content = "Finish: restore my story progress" };
        var johtoStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var johto = new Button { Content = "Let Birch offer the Johto starters (again)" };

        bool InReset() => sav.GetEventFlag(EFlagPokedexGet) && (sav.GetWork(EVarRoute101) < 3 || sav.GetWork(EVarBirchLab) < 5);

        void Refresh()
        {
            var route = sav.GetWork(EVarRoute101);
            var lab = sav.GetWork(EVarBirchLab);
            var starter = sav.GetWork(EVarStarter);
            string scene = route >= 3 ? "starter already chosen" : "Birch's bag is waiting — walk into Route 101 from Littleroot";
            status.Text = $"Route 101: {route} ({scene})\nBirch's Lab: {lab}{(lab >= 5 ? " (Pokédex received)" : string.Empty)}\nStarter on record: {HoennStarterName(starter)}" +
                          (InReset() ? "\nReset in progress — press \"Finish\" when you're done." : string.Empty);
            finish.IsVisible = InReset();

            var j = sav.GetWork(EVarJohto);
            johtoStatus.Text = j switch
            {
                0 or 1 => "Johto starters: not yet — they need the National Pokédex, which Birch gives after the Hall of Fame.",
                2 or 3 => "Johto starters: National Pokédex received; Birch normally offers them once you've caught all 200 Hoenn Pokémon.",
                4 or 5 => "Johto starters: on Birch's table now — go into his lab and pick one.",
                _ => "Johto starters: one already chosen.",
            };
            johto.IsEnabled = j >= 2;
        }
        ctx.Refreshers.Add(Refresh);

        reset.Click += async (_, _) =>
        {
            if (!sav.GetEventFlag(EFlagPokemonGet))
            {
                await Ui.ShowMessage(ctx.Window, "Starter reset", "You haven't received a starter yet, so there is nothing to reset. Just help Prof. Birch on Route 101.");
                return;
            }
            if (!sav.GetEventFlag(EFlagPokedexGet))
            {
                await Ui.ShowMessage(ctx.Window, "Starter reset", "Get the Pokédex from Prof. Birch first (beat your rival on Route 103 and go back to the lab), then come back here. " +
                                                                   "Resetting earlier would mix up that part of the story.");
                return;
            }
            sav.SetWork(EVarRoute101, 0);              // Route 101 as on a new game: the rescue scene plays again
            sav.SetEventFlag(EFlagHideBag, false);      // Birch's bag
            sav.SetEventFlag(EFlagHideBattleBirch, false); // Birch being chased
            sav.SetEventFlag(EFlagHideZigzagoon, false);   // the Zigzagoon chasing him
            ctx.Changed();
            ctx.RefreshAll();
            await Ui.ShowMessage(ctx.Window, "Starter reset", "Prof. Birch is in trouble on Route 101 again. Now:\n\n" +
                "1. In your game, save somewhere that is NOT Route 101 (Littleroot Town is best). Then click File → Save here and load the save in your game.\n" +
                "2. Walk north from Littleroot into Route 101. Birch is chased by a Zigzagoon; open his bag and pick a starter.\n" +
                "   The starter joins your party (or goes to the PC if your party is full). Your FIRST party Pokémon fights the Zigzagoon (level 2).\n" +
                "3. You're taken to Birch's Lab. When he asks about a nickname, answer NO — the game would rename your first party Pokémon, not the new starter.\n" +
                "4. Save in-game, reopen the save here and press Reset again for the next starter (3 rounds gets you all three).\n" +
                "5. When you're done, press \"Finish\". The starter you picked LAST counts as your starter from then on.");
        };

        finish.Click += async (_, _) =>
        {
            sav.SetWork(EVarRoute101, 3);
            sav.SetEventFlag(EFlagHideBag, true);
            sav.SetEventFlag(EFlagHideBattleBirch, true);
            sav.SetEventFlag(EFlagHideZigzagoon, true);
            sav.SetWork(EVarBirchLab, 5);
            ctx.Changed();
            ctx.RefreshAll();
            await Ui.ShowMessage(ctx.Window, "Starter reset", $"Story progress restored. Your starter on record is now {HoennStarterName(sav.GetWork(EVarStarter))}. Click File → Save.");
        };

        johto.Click += async (_, _) =>
        {
            sav.SetWork(EVarJohto, 4);                  // "entered lab after catching all Hoenn Pokémon": Birch puts the three balls out
            foreach (var f in EFlagHideJohtoBalls)
                sav.SetEventFlag(f, false);
            ctx.Changed();
            ctx.RefreshAll();
            await Ui.ShowMessage(ctx.Window, "Johto starters", "Click File → Save and load the save in your game, then walk into Birch's Lab: " +
                "he tells you to pick one of Cyndaquil, Totodile and Chikorita.\n\nOne per round: after picking, save in-game, reopen the save here and press the button again for the next one.");
        };

        Refresh();
        return Page(
            Explain("Lets you pick the Hoenn starters again so you can collect all three (Treecko, Torchic and Mudkip), and the Johto starters (Cyndaquil, Totodile, Chikorita) as many times as you like.\n\n" +
                    "How it works: Route 101 has a progress value; at 0 the \"Birch is chased by a Zigzagoon\" scene plays again and his bag can be opened. " +
                    "\"Finish\" puts Route 101 and Birch's Lab back to where they were after you got the Pokédex. " +
                    "For the Johto starters, Birch's Lab is set back to the moment he offers them (normally after catching all Hoenn Pokémon). " +
                    "Try it on a copy of your save first."),
            status, Ui.Row(reset, finish),
            Ui.Header("Johto starters"), johtoStatus, johto);
    }

    #endregion

    #region Emerald roamer

    private static Control RoamerEmerald(Context ctx, Control editor, Control editorButtons)
    {
        var sav = ctx.Sav;
        var roamer = new Roamer3(sav.LargeBlock);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var again = new Button { Content = "Let Mom ask me the color again (re-run the TV news)" };

        void Refresh()
        {
            var houses = Math.Max(sav.GetWork(EVarHousesMay), sav.GetWork(EVarHousesBrendan));
            var choice = sav.GetWork(EVarRoamer);
            var current = roamer.Species == 0 ? "none yet" : GameInfo.Strings.specieslist[roamer.Species];
            string scene = houses switch
            {
                < 3 => "TV news: after the Hall of Fame.",
                3 => "TV news: ready — go into your house in Littleroot.",
                _ => "TV news: already seen.",
            };
            status.Text = $"{scene}\n" +
                          $"Roaming now: {current}{(roamer.IsActive ? " (roaming)" : roamer.Species != 0 ? " (caught / not roaming)" : string.Empty)}\n" +
                          (houses >= 4 ? $"Choice on record: {LatiName(choice)} roams, so Southern Island has {OtherLati(choice)}." : string.Empty);
            again.IsEnabled = sav.GetEventFlag(EFlagGameClear);
        }
        ctx.Refreshers.Add(Refresh);

        again.Click += async (_, _) =>
        {
            if (!sav.GetEventFlag(EFlagGameClear))
            {
                await Ui.ShowMessage(ctx.Window, "Roaming Lati", "The roaming Lati appears after the Hall of Fame. Beat the Elite Four first.");
                return;
            }
            sav.SetWork(EVarHousesMay, 3);           // "entered Hall of Fame": the S.S. Ticket / Lati TV scene plays when entering the house
            sav.SetWork(EVarHousesBrendan, 3);
            sav.SetEventFlag(EFlagHideDad, false);    // Dad has to be in the house for the scene
            ctx.Changed();
            ctx.RefreshAll();
            await Ui.ShowMessage(ctx.Window, "Roaming Lati", "The TV news scene is ready again.\n\n" +
                "1. Click File → Save and load the save in your game.\n" +
                "2. Go into your house in Littleroot Town. Dad brings the S.S. Ticket again (you may end up with two; that's harmless), then the TV news plays.\n" +
                "3. Mom asks which color it was: RED makes Latias roam, BLUE makes Latios roam.\n\n" +
                "This creates a brand-new roaming Lati (level 40, new PID and IVs) that replaces the one roaming before. " +
                "Southern Island always has the other one, so switching colors also switches the island Pokémon " +
                "(use the Legendaries tab if you already caught the island one).");
        };

        Refresh();
        return Page(
            Explain("Which Lati roams Hoenn is decided after the Hall of Fame: back home, Mom asks which color the Pokémon on the TV news was. " +
                    "Red → Latias, Blue → Latios. This lets you see that scene again to pick again, or just to get a fresh roamer."),
            status, again,
            Ui.Header("Current roaming Pokémon"),
            Explain("To make it roam again after you caught or knocked it out without re-running the scene, tick \"Roaming\" and save — HP is refilled automatically."),
            editor, editorButtons);
    }

    #endregion

    #region Emerald islands

    private static Control IslandsEmerald(Context ctx)
    {
        var sav = ctx.Sav;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };

        void Refresh()
        {
            var lines = new List<string>
            {
                $"National Pokédex: {(sav.NationalDex ? "unlocked" : "locked")}",
                $"Hall of Fame (needed for the ferry to the islands): {(sav.GetEventFlag(EFlagGameClear) ? "yes" : "not yet")}",
            };
            foreach (var i in IslandsE)
                lines.Add($"{i.Name} ({i.Pokemon}): {(sav.GetEventFlag(i.ShipFlag) ? "unlocked" : "locked")}");
            status.Text = string.Join(Environment.NewLine, lines);
        }
        ctx.Refreshers.Add(Refresh);

        var dex = new Button { Content = "Unlock National Pokédex" };
        dex.Click += async (_, _) =>
        {
            sav.NationalDex = true;
            ctx.Changed();
            Refresh();
            await Ui.ShowMessage(ctx.Window, "National Pokédex", "National Pokédex unlocked. Click File → Save.");
        };

        var events = new Button { Content = "Enable all event islands (Eon, Mystic, Aurora Ticket and Old Sea Map)" };
        events.Click += async (_, _) =>
        {
            var items = new List<(int, string)>();
            foreach (var i in IslandsE)
            {
                sav.SetEventFlag(i.ShipFlag, true);
                if (i.ReceivedFlag >= 0)
                    sav.SetEventFlag(i.ReceivedFlag, true);
                items.Add((i.Item, i.ItemName));
            }
            var added = GiveKeyItems(sav, items.ToArray());
            ctx.Changed();
            Refresh();
            var clear = sav.GetEventFlag(EFlagGameClear) ? string.Empty : "\n\nNote: the ferry only sails there after you've entered the Hall of Fame.";
            await Ui.ShowMessage(ctx.Window, "Event islands", $"Enabled. {added}\n\nClick File → Save. In your game, go to the Lilycove City harbor and talk to the ferry attendant: " +
                "showing a ticket takes you to Southern Island, Navel Rock, Birth Island or Faraway Island." + clear);
        };

        Refresh();
        return Page(
            Explain("National Pokédex: needed to register and evolve Pokémon from outside Hoenn. Normally Prof. Birch gives it after the Hall of Fame."),
            dex,
            Explain("\nEvents enabler: Latios/Latias (Southern Island), Lugia and Ho-Oh (Navel Rock), Deoxys (Birth Island) and Mew (Faraway Island) were only reachable with tickets " +
                    "given out at Nintendo events. This sets the same flags the event did and puts the Eon Ticket, Mystic Ticket, Aurora Ticket and Old Sea Map in your Key Items. " +
                    "Each Pokémon can be caught once; the Legendaries tab brings them back."),
            events, Ui.Header("Current state"), status);
    }

    #endregion
}
