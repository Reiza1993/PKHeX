using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using PKHeX.Core;

namespace PKHeX.Avalonia;

/// <summary>
/// Regenerates a legal PID after the IVs, nature, gender, ability or shininess were edited.
/// </summary>
public partial class MainWindow
{
    private async void Pid_Fix(object? sender, RoutedEventArgs e)
    {
        var pk = GetEditedEntity();
        if (pk.Species == 0)
            return;
        if (!PidFixer.IsSupported(pk))
        {
            await ShowMessage("Fix PID", "Generation 1 and 2 Pokémon have no PID.");
            return;
        }
        var result = await ResolvePid(pk, saving: false);
        if (result is null || ReferenceEquals(result, pk))
            return;
        LoadEditor(result);
        SetStatus("Legal PID loaded into the editor. Click \"Save to selected slot\" to keep it.");
    }

    /// <summary>
    /// Returns <paramref name="pk"/> if its PID is fine (or the user keeps it), a fixed copy, or null if the user cancelled.
    /// </summary>
    private async Task<PKM?> ResolvePid(PKM pk, bool saving)
    {
        SetStatus("Checking the PID…");
        if (!await Task.Run(() => PidFixer.HasProblem(pk)))
        {
            if (!saving)
                await ShowMessage("Fix PID", "The PID already matches this Pokémon's IVs, nature, gender and ability. Nothing to fix.");
            SetStatus(string.Empty);
            return pk;
        }

        SetStatus("Looking for a legal PID…");
        var fixes = await Task.Run(() => PidFixer.FindFixes(pk));
        SetStatus(string.Empty);
        if (fixes.Count == 0)
        {
            const string none = "No legal PID could be found for this Pokémon's encounter (the problem may be elsewhere — use Check legality to see the details).";
            if (!saving)
            {
                await ShowMessage("Fix PID", none);
                return null;
            }
            return await Confirm("Fix PID", none + "\n\nSave it anyway?") ? pk : null;
        }

        if (fixes[0].Level == PidFixer.Keep.Everything)
            return fixes[0].Entity;

        var sb = new StringBuilder();
        sb.AppendLine("In this game the PID and the IVs (and the nature, gender and ability that come from the PID) are generated together, " +
                      "so not every combination exists. This one doesn't for this encounter. Legal alternatives:");
        for (int i = 0; i < fixes.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"{i + 1}. Legal PID that {PidFixer.LevelName(fixes[i].Level)}:");
            sb.AppendLine(fixes[i].Changes);
        }
        var options = fixes.Select((_, i) => $"Use {i + 1}").ToList();
        if (saving)
            options.Add("Save unchanged (illegal)");
        var choice = await Ui.Choose(this, "Fix PID", sb.ToString().TrimEnd(), options);
        if (choice < 0)
            return null;
        return choice < fixes.Count ? fixes[choice].Entity : pk;
    }
}
