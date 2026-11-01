using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aion2PL;

internal record LauncherRelease(string Version, [property: JsonPropertyName("download_url")] string DownloadUrl,
    string Sha256, [property: JsonPropertyName("size_bytes")] long SizeBytes)
{
    internal void Validate()
    {
        if (!System.Version.TryParse(Version, out var version) || version.Major < 0 ||
            Sha256 is null || !Regex.IsMatch(Sha256, @"\A[0-9a-fA-F]{64}\z") || SizeBytes < 1024 || SizeBytes > UpdateClient.MaxDownload)
            throw new InvalidDataException("Nieprawidłowe metadane aktualizacji launchera.");
        var uri = UpdateClient.Https(DownloadUrl);
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/Kashello/Aion2PL-launcher/releases/download/", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.EndsWith("/Aion2PL.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Aktualizacja launchera musi pochodzić z właściwego wydania na GitHubie.");
    }
    internal bool IsNewer()
    {
        Validate();
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        return System.Version.Parse(Version).CompareTo(current) > 0;
    }
}

internal static class LauncherSelfUpdate
{
    internal static async Task<bool> Prepare(LauncherRelease release, UpdateClient client, Action<string> log, CancellationToken ct)
    {
        if (!release.IsNewer()) return false;
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Nie można ustalić ścieżki launchera.");
        if (!current.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Autoaktualizacja wymaga samodzielnego pliku EXE.");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aion2PL", "launcher-updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var staged = Path.Combine(dir, "new.exe");
        var helper = Path.Combine(dir, "helper.exe");
        await client.DownloadLauncher(release, staged, log, ct);
        using (var f = File.OpenRead(staged))
            if (f.ReadByte() != 'M' || f.ReadByte() != 'Z') throw new InvalidDataException("Aktualizacja launchera nie jest plikiem Windows EXE.");
        File.Copy(current, helper);
        var currentHash = Installer.Hash(current) ?? throw new IOException("Nie można sprawdzić bieżącego launchera.");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "--apply-launcher-update", Environment.ProcessId.ToString(), current, staged, release.Sha256, currentHash })
            start.ArgumentList.Add(value);
        if (Process.Start(start) is null) throw new IOException("Nie udało się uruchomić aktualizacji launchera.");
        log("Uruchamiam ponownie launcher, aby dokończyć jego aktualizację.");
        return true;
    }

    internal static void Apply(string[] args, string? helperPath = null, Action<string>? startTarget = null)
    {
        if (args.Length != 6 || !int.TryParse(args[1], out var pid) || pid <= 0 ||
            !Regex.IsMatch(args[4], @"\A[0-9a-fA-F]{64}\z") || !Regex.IsMatch(args[5], @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("Nieprawidłowe dane aktualizacji launchera.");
        var target = Path.GetFullPath(args[2]);
        var staged = Path.GetFullPath(args[3]);
        var parent = Path.GetDirectoryName(helperPath ?? Environment.ProcessPath!)!;
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !staged.Equals(Path.Combine(parent, "new.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nieprawidłowe położenie plików aktualizacji.");
        try { using var old = Process.GetProcessById(pid); if (!old.WaitForExit(30000)) throw new IOException("Launcher nie zakończył pracy."); }
        catch (ArgumentException) { }
        if (!string.Equals(Installer.Hash(staged), args[4], StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Installer.Hash(target), args[5], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pliki launchera zmieniły się podczas aktualizacji.");
        var next = target + ".aion2pl-new-" + Guid.NewGuid().ToString("N");
        var backup = target + ".aion2pl-backup-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(staged, next);
            if (!string.Equals(Installer.Hash(next), args[4], StringComparison.OrdinalIgnoreCase)) throw new IOException("Kopia aktualizacji jest niezgodna.");
            File.Move(target, backup);
            try { File.Move(next, target); }
            catch { File.Move(backup, target); throw; }
            try
            {
                if (startTarget is not null) startTarget(target);
                else if (Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! }) is null)
                    throw new IOException("Nie udało się uruchomić nowej wersji launchera.");
            }
            catch { File.Delete(target); File.Move(backup, target); throw; }
        }
        finally { if (File.Exists(next)) File.Delete(next); }
    }
}
