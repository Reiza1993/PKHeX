using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PKHeX.Avalonia;

/// <summary>
/// Updates the app in one of two ways:
/// <list type="bullet">
/// <item><b>Source</b>: the app was built from a git checkout (build-linux.sh). Pull the latest commits and rebuild.</item>
/// <item><b>Release</b>: the app came from a GitHub release. Download the newest release archive and replace the files.</item>
/// </list>
/// </summary>
public static class Updater
{
    public const string Repository = "Reiza1993/PKHeX";
    public const string ReleaseTagPrefix = "avalonia-";

    public enum Mode { Source, Release }

    public sealed record UpdateInfo(bool Available, string Summary, string Details, ReleaseAsset? Asset = null);

    public sealed record ReleaseAsset(int BuildNumber, string Tag, string Name, string Url, string Notes);

    /// <summary> Folder containing the running app. </summary>
    public static string InstallDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary> Build number stamped by the GitHub Actions release workflow, or 0 for a local build. </summary>
    public static int BuildNumber { get; } = GetBuildNumber();

    /// <summary> Git checkout the app was built from, if known. </summary>
    public static string? SourceDirectory { get; } = FindSourceDirectory();

    public static Mode CurrentMode => BuildNumber == 0 && SourceDirectory is not null ? Mode.Source : Mode.Release;

    /// <summary> True when the app was installed by build-linux.sh --install (rather than run from the build output). </summary>
    public static bool IsInstalledCopy => File.Exists(Path.Combine(InstallDirectory, "source-dir"));

    public static string VersionText
    {
        get
        {
            var core = typeof(PKHeX.Core.SaveFile).Assembly.GetName().Version;
            var build = BuildNumber != 0 ? $"release build {BuildNumber}" : SourceDirectory is not null ? $"built from source ({GitCommit() ?? "unknown commit"})" : "local build";
            return $"PKHeX.Core {core} · {build}";
        }
    }

    private static int GetBuildNumber()
    {
        var value = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildNumber")?.Value;
        return int.TryParse(value, out var n) ? n : 0;
    }

    private static string? FindSourceDirectory()
    {
        // Written by build-linux.sh --install
        var marker = Path.Combine(InstallDirectory, "source-dir");
        if (File.Exists(marker))
        {
            var dir = File.ReadAllText(marker).Trim();
            if (IsCheckout(dir))
                return dir;
        }
        // Running from the build output inside the repository (e.g. dotnet run)
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (IsCheckout(dir.FullName))
                return dir.FullName;
        }
        return null;

        static bool IsCheckout(string dir) => Directory.Exists(Path.Combine(dir, ".git")) && File.Exists(Path.Combine(dir, "PKHeX.Avalonia", "PKHeX.Avalonia.csproj"));
    }

    private static string? GitCommit()
    {
        try { return RunCapture("git", ["-C", SourceDirectory!, "rev-parse", "--short", "HEAD"]).Trim(); }
        catch { return null; }
    }

    /// <summary> Removes leftovers from a previous release update (old files are renamed to *.old while the app runs). </summary>
    public static void CleanupOldFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(InstallDirectory, "*.old"))
                File.Delete(file);
        }
        catch { /* not important */ }
    }

    #region Check

    public static Task<UpdateInfo> CheckAsync(CancellationToken token = default)
        => CurrentMode == Mode.Source ? CheckSourceAsync(token) : CheckReleaseAsync(token);

    private static async Task<UpdateInfo> CheckSourceAsync(CancellationToken token)
    {
        var src = SourceDirectory!;
        await RunAsync("git", ["-C", src, "fetch", "--quiet"], null, token);
        string upstream;
        try
        {
            upstream = RunCapture("git", ["-C", src, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"]).Trim();
        }
        catch
        {
            return new UpdateInfo(false, "This checkout has no upstream branch to update from.", $"Source folder: {src}");
        }
        var log = RunCapture("git", ["-C", src, "log", "--oneline", "--no-decorate", "HEAD..@{u}"]).Trim();
        if (log.Length == 0)
            return new UpdateInfo(false, "You're up to date.", $"Source folder: {src}\nTracking: {upstream}");
        var count = log.Split('\n').Length;
        return new UpdateInfo(true, $"{count} new change(s) available on {upstream}.", log);
    }

    private static async Task<UpdateInfo> CheckReleaseAsync(CancellationToken token)
    {
        var asset = await GetLatestReleaseAsync(token);
        if (asset is null)
            return new UpdateInfo(false, $"No release for this system ({RuntimeInformation.RuntimeIdentifier}) was found on GitHub.", $"https://github.com/{Repository}/releases");
        if (asset.BuildNumber <= BuildNumber)
            return new UpdateInfo(false, "You're up to date.", $"Installed: build {BuildNumber} · Latest: build {asset.BuildNumber}");
        var current = BuildNumber == 0 ? "a local build" : $"build {BuildNumber}";
        return new UpdateInfo(true, $"Build {asset.BuildNumber} is available (you have {current}).", asset.Notes, asset);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PKHeX.Avalonia-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary> Name of the release archive for this operating system and CPU. </summary>
    public static string? AssetName => RuntimeInformation.RuntimeIdentifier switch
    {
        var r when r.StartsWith("linux") && RuntimeInformation.OSArchitecture == Architecture.Arm64 => "PKHeX.Avalonia-linux-arm64.tar.gz",
        var r when r.StartsWith("linux") => "PKHeX.Avalonia-linux-x64.tar.gz",
        var r when r.StartsWith("win") => "PKHeX.Avalonia-win-x64.zip",
        _ => null,
    };

    private static async Task<ReleaseAsset?> GetLatestReleaseAsync(CancellationToken token)
    {
        var name = AssetName;
        if (name is null)
            return null;
        using var client = CreateClient();
        var json = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", token);
        using var doc = JsonDocument.Parse(json);
        ReleaseAsset? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
                continue;
            var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!tag.StartsWith(ReleaseTagPrefix, StringComparison.Ordinal) || !int.TryParse(tag[ReleaseTagPrefix.Length..], out var number))
                continue;
            if (best is not null && number <= best.BuildNumber)
                continue;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name)
                    continue;
                var url = asset.GetProperty("browser_download_url").GetString()!;
                var notes = release.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
                best = new ReleaseAsset(number, tag, name, url, notes);
            }
        }
        return best;
    }

    #endregion

    #region Apply

    /// <summary>
    /// Downloads/builds and installs the update. Progress lines are reported through <paramref name="log"/>.
    /// Returns the path of the program to start afterwards.
    /// </summary>
    public static async Task<string> ApplyAsync(UpdateInfo info, Action<string> log, CancellationToken token = default)
    {
        // The app is a single-file bundle: once its file is replaced, parts of it that haven't been loaded yet
        // can't be loaded anymore. Make sure everything needed to start the new version is loaded first.
        await WarmUpProcessSupportAsync(token);
        return CurrentMode == Mode.Source
            ? await ApplySourceAsync(log, token)
            : await ApplyReleaseAsync(info.Asset!, log, token);
    }

    private static Task WarmUpProcessSupportAsync(CancellationToken token)
        => OperatingSystem.IsWindows()
            ? RunAsync("cmd.exe", ["/c", "exit", "0"], null, token)
            : RunAsync("/bin/sh", ["-c", "true"], null, token);

    private static async Task<string> ApplySourceAsync(Action<string> log, CancellationToken token)
    {
        var src = SourceDirectory!;
        log($"$ git pull --ff-only   (in {src})");
        await RunAsync("git", ["-C", src, "pull", "--ff-only"], log, token);

        if (IsInstalledCopy && OperatingSystem.IsLinux())
        {
            var script = Path.Combine(src, "PKHeX.Avalonia", "build-linux.sh");
            log("$ build-linux.sh --install   (this takes a few minutes)");
            await RunAsync("bash", [script, "--install"], log, token);
            return Path.Combine(InstallDirectory, "PKHeX.Avalonia");
        }

        log("$ dotnet build PKHeX.Avalonia -c Release");
        await RunAsync("dotnet", ["build", Path.Combine(src, "PKHeX.Avalonia", "PKHeX.Avalonia.csproj"), "-c", "Release"], log, token);
        return Environment.ProcessPath!;
    }

    private static async Task<string> ApplyReleaseAsync(ReleaseAsset asset, Action<string> log, CancellationToken token)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"pkhex-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var archive = Path.Combine(temp, asset.Name);
            log($"Downloading {asset.Name} (build {asset.BuildNumber})…");
            using (var client = CreateClient())
            await using (var http = await client.GetStreamAsync(asset.Url, token))
            await using (var file = File.Create(archive))
                await http.CopyToAsync(file, token);
            log($"Downloaded {new FileInfo(archive).Length / 1024 / 1024} MB.");

            var extracted = Path.Combine(temp, "files");
            Directory.CreateDirectory(extracted);
            log("Extracting…");
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archive, extracted);
            }
            else
            {
                await using var fs = File.OpenRead(archive);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, extracted, overwriteFiles: true, token);
            }

            var target = InstallDirectory;
            log($"Installing into {target}…");
            foreach (var source in Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(extracted, source);
                var dest = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // The running program can't be overwritten in place, but it can be renamed out of the way.
                if (File.Exists(dest))
                    File.Move(dest, dest + ".old", overwrite: true);
                File.Move(source, dest);
                if (!OperatingSystem.IsWindows() && Path.GetFileName(dest) == "PKHeX.Avalonia")
                    File.SetUnixFileMode(dest, File.GetUnixFileMode(dest) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            log("Done.");
            var exe = OperatingSystem.IsWindows() ? "PKHeX.Avalonia.exe" : "PKHeX.Avalonia";
            return Path.Combine(target, exe);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }

    /// <summary> Starts the (updated) program. The caller should then close this instance. </summary>
    public static void Restart(string program, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    #endregion

    #region Processes

    private static string RunCapture(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {file}.");
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed: {error.Trim()}");
        return output;
    }

    private static async Task RunAsync(string file, IEnumerable<string> args, Action<string>? log, CancellationToken token)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // never wait for a password prompt
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (line is null)
                return;
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 15)
                    tail.Dequeue();
            }
            log?.Invoke(line);
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        if (!p.Start())
            throw new InvalidOperationException($"Couldn't start {file}.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(token);
        if (p.ExitCode != 0)
        {
            string last;
            lock (tail)
                last = string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException($"{Path.GetFileName(file)} exited with code {p.ExitCode}.\n{last}");
        }
    }

    #endregion
}
