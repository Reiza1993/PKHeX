PKHeX
=====
<div>
  <span>English</span> / <a href=".github/README-es.md">Español</a> / <a href=".github/README-fr.md">Français</a> / <a href=".github/README-de.md">Deutsch</a> / <a href=".github/README-it.md">Italiano</a> / <a href=".github/README-ko.md">한국어</a> / <a href=".github/README-zh-Hant.md">繁體中文</a> / <a href=".github/README-zh-Hans.md">简体中文</a>
</div>

![License](https://img.shields.io/badge/License-GPLv3-blue.svg)

Pokémon core series save editor, programmed in [C#](https://en.wikipedia.org/wiki/C_Sharp_%28programming_language%29).

Supports the following files:
* Save files ("main", \*.sav, \*.dsv, \*.dat, \*.gci, \*.bin)
* GameCube Memory Card files (\*.raw, \*.bin) containing GC Pokémon savegames.
* Individual Pokémon entity files (.pk\*, \*.ck3, \*.xk3, \*.pb7, \*.sk2, \*.bk4, \*.rk4)
* Mystery Gift files (\*.pgt, \*.pcd, \*.pgf, .wc\*) including conversion to .pk\*
* Importing GO Park entities (\*.gp1) including conversion to .pb7
* Importing teams from Decrypted 3DS Battle Videos
* Transferring from one generation to another, converting formats along the way.

Data is displayed in a view which can be edited and saved.
The interface can be translated with resource/external text files so that different languages can be supported.

Pokémon Showdown sets and QR codes can be imported/exported to assist in sharing.

PKHeX expects save files that are not encrypted with console-specific keys. Use a savedata manager to import and export savedata from the console ([Checkpoint](https://github.com/FlagBrew/Checkpoint), save_manager, [JKSM](https://github.com/J-D-K/JKSM), or SaveDataFiler).

**We do not support or condone cheating at the expense of others. Do not use significantly hacked Pokémon in battle or in trades with those who are unaware hacked Pokémon are in use.**

## Screenshots

![Main Window](https://i.imgur.com/pIHdoTp.png)

## Building

PKHeX is a Windows Forms application which requires [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).

The executable can be built with any compiler that supports C# 14.

### Linux (Ubuntu) / macOS

`PKHeX.Avalonia` is a cross-platform front end built on [Avalonia](https://avaloniaui.net/) and `PKHeX.Core`. It runs natively on Linux without Wine and covers:

* Save files: open (or drag and drop), save with a `.bak` backup, browse boxes and the party.
* Pokémon editor: species, form, gender, nickname, level, nature and stat nature (mints), ability, item, ball, language, moves with PP Ups, relearn moves, legal move suggestions, IVs/EVs, Hyper Training, Tera types, OT/TID/SID, shiny, egg, met data (with suggestions), ribbons, memories, contest stats, friendship, Pokérus, PID/EC, trade evolutions; legality check.
* Tools: trainer card, bag/items, Pokédex, event flags and work values (Gen 1–7, Let's Go, BDSP), batch editor, box sorting/clearing/export, legality report, Showdown set import/export.
* Gen 3 tools: National Dex helper (where to find each missing species, add a legal copy), starter selection reset, legendary respawn and event-island unlock for FireRed/LeafGreen (Mystic/Aurora Ticket) and Emerald (Eon/Mystic/Aurora Ticket, Old Sea Map; Hoenn and Johto starters; re-run of the Latios/Latias TV news), roaming legendary editor, National Pokédex unlock.

Some game-specific editors from the Windows version (e.g. Gen 8+ block editors, Hall of Fame, mystery gift database) are not included.

On Ubuntu:

```bash
sudo apt install dotnet-sdk-10.0
./PKHeX.Avalonia/build-linux.sh --install   # builds a self-contained binary and adds a "PKHeX" launcher
pkhex /path/to/save                          # or open it from the applications menu
```

To run without installing: `dotnet run --project PKHeX.Avalonia -c Release`.

**Updating:** use *Help → Check for updates* in the app. If you installed with `build-linux.sh --install`, it pulls the latest commits from your checkout and rebuilds (needs `git` and the .NET SDK). Copies downloaded from the GitHub releases (built by `.github/workflows/pkhex-avalonia.yml`) download the newest release instead.

**Save file types:** `.sav`, RetroArch `.srm` (mGBA and VBA-Next/gpSP layouts), `.dsv`, `.gci` and the other formats PKHeX.Core recognises can be opened and saved back in the same format.

### Build Configurations

Use the Debug or Release build configurations when building. There isn't any platform specific code to worry about!

## Dependencies

PKHeX's QR code generation code is taken from [QRCoder](https://github.com/codebude/QRCoder), which is licensed under [the MIT license](https://github.com/codebude/QRCoder/blob/master/LICENSE.txt).

PKHeX's shiny sprite collection is taken from [pokesprite](https://github.com/msikma/pokesprite), which is licensed under [the MIT license](https://github.com/msikma/pokesprite/blob/master/LICENSE).

PKHeX's Pokémon Legends: Arceus sprite collection is taken from the [National Pokédex - Icon Dex](https://www.deviantart.com/pikafan2000/art/National-Pokedex-Version-Delta-Icon-Dex-824897934) project and its abundance of collaborators and contributors.

### IDE

PKHeX can be opened with IDEs such as [Visual Studio](https://visualstudio.microsoft.com/downloads/) by opening the .sln or .csproj file.
