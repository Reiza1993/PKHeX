using System;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// RetroArch's VBA-Next / VBA-M / gpSP cores write GBA saves (.srm) as a fixed 0x22000-byte buffer:
/// 128 KB of flash save data followed by an 8 KB EEPROM area. Pokémon Gen 3 games only use the flash part.
/// </summary>
/// <remarks>
/// The trailing block is kept as a footer and written back unchanged, so the file stays loadable by the emulator.
/// RetroArch's mGBA core writes plain 128 KB .srm files, which PKHeX.Core already handles like .sav files.
/// </remarks>
public sealed class SaveHandlerRetroArchGBA : ISaveHandler
{
    private const int SizeFlash = SaveUtil.SIZE_G3RAW; // 0x20000
    private const int SizeTotal = SizeFlash + 0x2000;   // 0x22000

    public bool IsRecognized(long size) => size == SizeTotal;

    public SaveHandlerSplitResult TrySplit(Memory<byte> input)
    {
        var data = input[..SizeFlash];
        var footer = input[SizeFlash..];
        return new SaveHandlerSplitResult(data, default, footer, this);
    }

    public void Finalize(Span<byte> data) { }

    /// <summary> Registers the handler with PKHeX.Core (once). </summary>
    public static void Register()
    {
        if (!SaveUtil.Handlers.Exists(z => z is SaveHandlerRetroArchGBA))
            SaveUtil.Handlers.Add(new SaveHandlerRetroArchGBA());
    }
}
