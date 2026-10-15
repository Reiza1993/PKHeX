using System;
using Avalonia.Controls;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Trainer card editor: name, IDs, gender, money, coins, language and play time.
/// </summary>
public static class TrainerEditor
{
    public static Window Create(SaveFile sav)
    {
        var form = new Ui.Form();
        var gen = sav.Generation;
        bool sixDigit = sav.TrainerIDDisplayFormat == TrainerIDFormat.SixDigit;

        var name = form.Add("Trainer name", new TextBox { Text = sav.OT, MaxLength = sav.MaxStringLengthTrainer });
        var gender = form.Add("Gender", Ui.Combo([new("Male", 0), new("Female", 1)]));
        Ui.SetValue(gender, sav.Gender);
        gender.IsEnabled = gen > 1;

        var tid = form.Add(sixDigit ? "Trainer ID (6-digit)" : "Trainer ID", Ui.Number(0, sixDigit ? 999_999 : ushort.MaxValue, sav.DisplayTID));
        var sid = form.Add(sixDigit ? "Secret ID (4-digit)" : "Secret ID", Ui.Number(0, sixDigit ? 4294 : ushort.MaxValue, sav.DisplaySID));
        sid.IsEnabled = gen > 2;

        var money = form.Add("Money", Ui.Number(0, sav.MaxMoney, Math.Min(sav.Money, (uint)sav.MaxMoney)));
        form.AddWide(Ui.Button("Max money", (_, _) => money.Value = sav.MaxMoney));

        NumericUpDown? coins = sav switch
        {
            SAV1 s1 => form.Add("Game Corner coins", Ui.Number(0, sav.MaxCoins, Math.Min(s1.Coin, (uint)sav.MaxCoins))),
            SAV2 s2 => form.Add("Game Corner coins", Ui.Number(0, sav.MaxCoins, Math.Min(s2.Coin, (uint)sav.MaxCoins))),
            _ => null,
        };

        ComboBox? language = null;
        if (sav.Language > 0)
        {
            language = form.Add("Language", Ui.Combo(GameInfo.FilteredSources.Languages));
            Ui.SetValue(language, sav.Language);
        }

        form.AddWide(Ui.Header("Play time"));
        var hours = form.Add("Hours", Ui.Number(0, sav.MaxGameHours(), sav.PlayedHours));
        var minutes = form.Add("Minutes", Ui.Number(0, 59, sav.PlayedMinutes));
        var seconds = form.Add("Seconds", Ui.Number(0, 59, sav.PlayedSeconds));

        form.AddWide(new TextBlock
        {
            Text = $"Game: {GameInfo.GetVersionName(sav.Version)} (Gen {gen})",
            Opacity = 0.7,
            Margin = new(0, 12, 0, 0),
        });

        var content = new ScrollViewer { Content = form.Grid };
        return Ui.ToolWindow("Trainer", content, () =>
        {
            var newName = name.Text ?? string.Empty;
            if (newName != sav.OT)
                sav.OT = newName;
            if (gen > 1)
                sav.Gender = (byte)Ui.GetValue(gender);
            sav.DisplayTID = (uint)(tid.Value ?? 0);
            if (gen > 2)
                sav.DisplaySID = (uint)(sid.Value ?? 0);
            sav.Money = (uint)(money.Value ?? 0);
            switch (sav)
            {
                case SAV1 s1 when coins is not null: s1.Coin = (uint)(coins.Value ?? 0); break;
                case SAV2 s2 when coins is not null: s2.Coin = (uint)(coins.Value ?? 0); break;
            }
            if (language is not null)
                sav.Language = Ui.GetValue(language);
            sav.PlayedHours = (int)(hours.Value ?? 0);
            sav.PlayedMinutes = (int)(minutes.Value ?? 0);
            sav.PlayedSeconds = (int)(seconds.Value ?? 0);
            sav.State.Edited = true;
        }, 480, 600);
    }

    private static int MaxGameHours(this SaveFile sav) => sav.Generation switch
    {
        <= 2 => 255,
        3 => 999,
        _ => 9999,
    };
}
