using System.Collections.Generic;
using System.Reflection;
using Avalonia.Media.Imaging;
using PKHeX.Core;
using PKHeX.Drawing.PokeSprite;

namespace PKHeX.Avalonia;

/// <summary>
/// Loads sprite images embedded from PKHeX.Drawing.PokeSprite's resources, using the same naming scheme.
/// </summary>
public static class Sprites
{
    private static readonly Assembly Asm = typeof(Sprites).Assembly;
    private static readonly Dictionary<string, Bitmap?> Cache = [];

    static Sprites() => SpriteName.AllowShinySprite = true;

    public static Bitmap? Get(string logicalName)
    {
        if (Cache.TryGetValue(logicalName, out var bmp))
            return bmp;
        using var stream = Asm.GetManifestResourceStream(logicalName);
        bmp = stream is null ? null : new Bitmap(stream);
        Cache[logicalName] = bmp;
        return bmp;
    }

    public static Bitmap? AppIcon => Get("app.icon");

    public static Bitmap? GetSprite(PKM pk)
    {
        if (pk.Species == 0)
            return null;
        if (pk.IsEgg)
            return Get("pk.b_egg") ?? Get("pk.b_unknown");

        var context = pk.Context;
        var shiny = pk.IsShiny;
        var name = SpriteName.GetResourceStringSprite(pk.Species, pk.Form, pk.Gender, pk is IFormArgument f ? f.FormArgument : 0, context, shiny);
        return Get($"pk.b{name}")
            ?? (shiny ? Get($"pk.b{SpriteName.GetResourceStringSprite(pk.Species, pk.Form, pk.Gender, 0, context)}") : null)
            ?? Get($"pk.b_{pk.Species}")
            ?? Get("pk.b_unknown");
    }

    public static Bitmap? GetBall(byte ball) => Get($"ball.{SpriteName.GetResourceStringBall(ball)}");
}
