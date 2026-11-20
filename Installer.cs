using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aion2PL;

internal record InstalledFile(string Path, string Sha256, string? OriginalBackup, string? OriginalSha256, bool OriginalWasTranslation = false);
internal record PakBackup(string Path, string Sha256, string Build);
internal record InstallState(string Root, string Version, string Build, string PackageSha256, List<InstalledFile> Files, PakBackup? Pak);
internal record FileChange(string Path, string? BeforeSha256, string? AfterSha256, string? Backup);
internal record Journal(string Build, InstallState? PriorState, List<FileChange> Files);

/// <summary>The game runs from a different folder than the one chosen in the launcher (usually a wrong game folder).</summary>
internal sealed class GameRunningElsewhereException(Installer.RunningProcess game, string selected) : InvalidOperationException(
    $"AION 2 działa z innego folderu: {game.Path} (PID {game.Id}).\nWybrany folder: {selected}.\n\nKliknij Zmień folder i wskaż folder z Steam → AION 2 → Zarządzaj → Przeglądaj pliki lokalne.")
{
    internal Installer.RunningProcess Game { get; } = game;
    internal string? RunningRoot { get; } = GameInstallation.RootFromExecutable(game.Path);
}

internal sealed class Installer
{
    readonly string root, storage;
    readonly Action<string> log;
    readonly Action ensureClosed;
    internal string StatePath => Path.Combine(storage, "installed.json");
    string PendingPath => Path.Combine(storage, "pending.json");
    internal bool HasPending => File.Exists(PendingPath);

    internal Installer(string root, string dataRoot, Action<string> log, Action? ensureClosed = null)
    {
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(this.root.ToUpperInvariant())));
        storage = Path.Combine(dataRoot, "installations", id);
        this.log = log;
        this.ensureClosed = ensureClosed ?? (() => RequireClosed(this.root));
    }
    internal record RunningProcess(int Id, string Name, string? Path);
    // AION 2 (Steam) runs Aion2/Binaries/Win64/AION2.exe; other game executables are caught by their folder.
    static readonly string[] GameProcessNames = ["AION2", "AION2-Win64-Shipping"];
    /// <summary>The game process that blocks file changes, or null. Launcher copies (any "Aion2PL*" name, e.g. "Aion2PL (1)") never block.</summary>
    internal static RunningProcess? BlockingProcess(IEnumerable<RunningProcess> processes, string? gameRoot, int selfId)
    {
        var folder = gameRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)) + Path.DirectorySeparatorChar;
        foreach (var process in processes)
        {
            if (process.Id == selfId || process.Name.StartsWith("Aion2PL", StringComparison.OrdinalIgnoreCase) ||
                (process.Path is not null && Path.GetFileName(process.Path).StartsWith("Aion2PL", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (GameProcessNames.Contains(process.Name, StringComparer.OrdinalIgnoreCase)) return process;
            if (folder is not null && process.Path is not null && Path.GetFullPath(process.Path).StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return process;
        }
        return null;
    }
    internal static void RequireClosed(string? gameRoot = null)
    {
        var running = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                string? path = null;
                if (gameRoot is not null && process.Id != Environment.ProcessId)
                {
                    try { path = process.MainModule?.FileName; }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
                running.Add(new(process.Id, name, path));
            }
        }
        if (BlockingProcess(running, gameRoot, Environment.ProcessId) is { } game) throw ClosedError(game, gameRoot);
    }
    internal static InvalidOperationException ClosedError(RunningProcess game, string? gameRoot)
    {
        if (gameRoot is not null && game.Path is not null)
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
            if (!Path.GetFullPath(game.Path).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new GameRunningElsewhereException(game, folder);
        }
        return new InvalidOperationException($"Zamknij AION 2 ({game.Name}.exe, PID {game.Id}) przed zmianą plików gry. Jeśli gra jest już zamknięta, zakończ ten proces w Menedżerze zadań.");
    }
    internal static string? Hash(string path)
    {
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static string SafeUnder(string parent, string relative)
    {
        var name = Package.SafeName(relative);
        var full = Path.GetFullPath(Path.Combine(parent, name));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Nieprawidłowa ścieżka pliku.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Folder zawiera dowiązanie. Wybierz bezpośrednią ścieżkę instalacji.");
        }
        return full;
    }
    string GamePath(string name)
    {
        if (!name.StartsWith("Aion2/Content/L10N/", StringComparison.OrdinalIgnoreCase) && !Package.Allowed(name) &&
            name != GameInstallation.PakPath && !IsBackupName(name))
            throw new InvalidDataException("Nieprawidłowa ścieżka w zapisie instalacji.");
        return SafeUnder(root, name);
    }
    static bool IsBackupName(string name) => Regex.IsMatch(name,
        "^" + Regex.Escape(GameInstallation.PakPath) + @"(?:\.backup\d*|\.aion2pl-\d+-[0-9a-f]{32}\.backup)$", RegexOptions.IgnoreCase);
    internal List<string> ManualBackups()
    {
        var active = GamePath(GameInstallation.PakPath);
        var directory = Path.GetDirectoryName(active)!;
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, Path.GetFileName(active) + ".*")
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(IsBackupName).OrderByDescending(name => name.Equals(GameInstallation.PakPath + ".backup", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(name => File.GetLastWriteTimeUtc(GamePath(name))).ToList();
    }
    static bool LooksLikePak(byte[] bytes)
    {
        for (int i = Math.Max(0, bytes.Length - 512); i + 24 <= bytes.Length; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) != 0x5A6F12E1) continue;
            var version = BitConverter.ToUInt32(bytes, i + 4);
            long offset = BitConverter.ToInt64(bytes, i + 8), size = BitConverter.ToInt64(bytes, i + 16);
            if (version is >= 1 and <= 11 && offset >= 0 && offset <= i && size > 0 && size <= i - offset) return true;
        }
        return false;
    }
    internal InstallState? State()
    {
        if (!File.Exists(StatePath)) return null;
        var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllBytes(StatePath), UpdateClient.Json) ?? throw new InvalidDataException("Uszkodzony zapis instalacji.");
        if (!state.Root.Equals(root, StringComparison.OrdinalIgnoreCase) || state.Files is null || state.Files.Count == 0)
            throw new InvalidDataException("Zapis instalacji należy do innej gry.");
        Release.Date(state.Version);
        foreach (var file in state.Files)
        {
            GamePath(file.Path);
            if (file.OriginalBackup is not null) SafeUnder(storage, file.OriginalBackup);
        }
        if (state.Pak is not null) GamePath(state.Pak.Path);
        return state;
    }
    internal bool Intact(InstallState state) => GameInstallation.Read(root) is { Updating: false } game && game.Build == state.Build &&
        state.Files.All(f => Hash(GamePath(f.Path)) == f.Sha256);
    void RequireReady(string build)
    {
        ensureClosed();
        var game = GameInstallation.Read(root);
        if (game.Updating || game.Build != build) throw new InvalidOperationException("Wersja lub stan gry zmieniły się. Poczekaj na Steam i sprawdź ponownie.");
    }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".aion2pl-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
    void SaveState(InstallState? state)
    {
        if (state is null) File.Delete(StatePath);
        else AtomicWrite(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, UpdateClient.Json));
    }
    FileStream Lock()
    {
        SafeUnder(storage, "operation.lock");
        Directory.CreateDirectory(storage);
        return new FileStream(Path.Combine(storage, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    byte[]? Original(InstalledFile file)
    {
        if (file.OriginalBackup is null) return null;
        var bytes = File.ReadAllBytes(SafeUnder(storage, file.OriginalBackup));
        if (Hash(bytes) != file.OriginalSha256) throw new InvalidDataException("Kopia poprzedniego pliku nie przeszła kontroli poprawności.");
        return bytes;
    }
    // Replaced Steam files (the fr-FR text PAK) are never deleted: without a verified original only Steam can restore them.
    static bool SteamFile(string name) => name.StartsWith("Aion2/Content/Paks/", StringComparison.OrdinalIgnoreCase);
    static bool RestorableOriginal(InstalledFile file) => file.OriginalBackup is not null && !file.OriginalWasTranslation && file.OriginalSha256 != file.Sha256;
    internal record Leftovers(bool LooseDat, bool EnglishPakMissing, bool EnglishPakRestorable)
    {
        internal bool Any => LooseDat || EnglishPakMissing;
    }
    /// <summary>Remains of the pre-26429009 method (loose en-US DAT, disabled English PAK) that show "???" after the patch.</summary>
    internal Leftovers LegacyLeftovers()
    {
        var game = GameInstallation.Read(root);
        var pak = HasPending ? null : State()?.Pak;
        bool restorable = pak is not null && pak.Build == game.Build && Hash(GamePath(pak.Path)) == pak.Sha256;
        return new(game.DatPresent, !game.EnglishPakPresent, !game.EnglishPakPresent && restorable);
    }
    internal void Install(Release release, PackageFiles files, bool cleanupLegacy = false)
    {
        release.Validate();
        var game = GameInstallation.Read(root);
        release.RequireBuild(game);
        if (files.Build != release.SteamBuildId) throw new InvalidDataException($"Paczka jest opisana jako build {files.Build}, a wydanie jako {release.SteamBuildId}.");
        RequireReady(release.SteamBuildId);
        if (State() is null && Package.IsInstalled(game, files))
        {
            log("To spolszczenie jest już zainstalowane ręcznie. Pliki są zgodne — nie trzeba instalować ponownie.");
            return;
        }
        using var operation = Lock();
        if (HasPending) throw new InvalidOperationException("Najpierw cofnij przerwaną operację przyciskiem przywracania.");
        RequireReady(release.SteamBuildId);
        var previous = State();
        if (previous is not null && Release.Date(release.Version) < Release.Date(previous.Version))
            throw new InvalidOperationException("Serwer udostępnia starsze wydanie. Zainstalowana wersja pozostaje bez zmian.");
        if (previous is not null && release.Version == previous.Version && !release.Sha256.Equals(previous.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Zawartość paczki zmieniła się bez zmiany daty wydania. Poczekaj na nowe wydanie.");
        var changes = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var newFiles = new List<InstalledFile>();
        var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in files)
        {
            var path = GamePath(name);
            var old = previous?.Files.Find(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
            var currentSha = Hash(path);
            var newSha = Hash(bytes);
            files.Originals.TryGetValue(name, out var expected);
            if (old is not null && currentSha == old.Sha256)
            {
                if (expected is not null && RestorableOriginal(old) && !expected.Equals(old.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Zapisana kopia oryginalnego PAK pochodzi z innego buildu gry. Cofnij spolszczenie, sprawdź spójność plików w Steam i zainstaluj ponownie.");
                newFiles.Add(old with { Sha256 = newSha });
            }
            else
            {
                if (expected is not null && currentSha != newSha && !expected.Equals(currentSha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Plik {Path.GetFileName(name)} nie jest oryginałem Steam dla buildu {release.SteamBuildId}. Sprawdź spójność plików w Steam i spróbuj ponownie.");
                bool wasTranslation = currentSha == newSha;
                string? backup = null;
                if (currentSha is not null)
                {
                    backup = "history/" + Guid.NewGuid().ToString("N") + "/original-" + newFiles.Count + ".bin";
                    originals.Add(backup, File.ReadAllBytes(path));
                }
                newFiles.Add(new(name, newSha, backup, currentSha, wasTranslation));
            }
            if (currentSha != newSha) changes.Add(name, bytes);
        }
        if (previous is not null)
            foreach (var file in previous.Files.Where(f => !files.ContainsKey(f.Path)))
            {
                if (SteamFile(file.Path) && previous.Build != game.Build)
                {
                    var current = Hash(GamePath(file.Path));
                    if (current is null || current == file.Sha256)
                        throw new IOException("Steam musi najpierw przywrócić PAK dla aktualnego buildu. Sprawdź spójność plików gry.");
                    // Steam already replaced the previous translation. Keep the current build's file.
                    continue;
                }
                if (Hash(GamePath(file.Path)) != file.Sha256) throw new IOException("Plik wcześniejszego wydania został zmieniony poza launcherem. Zachowano go bez zmian.");
                if (SteamFile(file.Path) && !RestorableOriginal(file)) throw new IOException("Brak kopii oryginalnego pliku wcześniejszego wydania. Sprawdź spójność plików w Steam.");
                changes.Add(file.Path, RestorableOriginal(file) ? Original(file) : null);
            }
        // Older releases disabled the English PAK; it is needed again for the English language.
        var pak = previous?.Pak;
        if (!game.EnglishPakPresent && pak is not null && pak.Build == game.Build && Hash(GamePath(pak.Path)) == pak.Sha256)
        {
            changes.Add(GameInstallation.PakPath, File.ReadAllBytes(GamePath(pak.Path)));
            log("Przywracam angielski PAK wyłączony przez poprzednią wersję spolszczenia (kopia zostaje zachowana).");
            pak = null;
        }
        else if (!game.EnglishPakPresent)
            log("Angielski PAK jest wyłączony przez starą metodę spolszczenia. Aby grać po angielsku, sprawdź spójność plików w Steam.");
        if (game.DatPresent && !changes.ContainsKey(GameInstallation.DatPath))
        {
            if (cleanupLegacy)
            {
                changes.Add(GameInstallation.DatPath, null);
                log("Usuwam luźny L10NString.dat ze starej metody (kopia zostaje w historii launchera).");
            }
            else log("Wykryto luźny L10NString.dat ze starej metody. Po tym patchu daje „???” w języku angielskim.");
        }
        foreach (var (backup, bytes) in originals) AtomicWrite(SafeUnder(storage, backup), bytes);
        var next = new InstallState(root, release.Version, release.SteamBuildId, release.Sha256, newFiles, pak);
        Apply(changes, next, previous, release.SteamBuildId);
        log("Spolszczenie zainstalowane. Uruchom grę i w ustawieniach wybierz język Polski.");
    }
    internal bool NeedsSteamVerification()
    {
        if (HasPending) return false;
        var game = GameInstallation.Read(root);
        if (game.EnglishPakPresent) return false;
        var pak = State()?.Pak;
        return pak is null || pak.Build != game.Build || Hash(GamePath(pak.Path)) != pak.Sha256;
    }
    /// <returns>true when a replaced Steam file stays in place and must be restored by Steam file verification.</returns>
    internal bool Restore(Dictionary<string, byte[]>? manualFiles = null, string? manualPak = null)
    {
        using var operation = Lock();
        if (HasPending) { Recover(); return false; }
        var game = GameInstallation.Read(root);
        RequireReady(game.Build);
        var state = State();
        var changes = new Dictionary<string, byte[]?>();
        bool steamNeeded = false;
        if (!game.EnglishPakPresent)
        {
            byte[] bytes;
            if (state?.Pak is { } managed && managed.Build == game.Build && Hash(GamePath(managed.Path)) == managed.Sha256)
                bytes = File.ReadAllBytes(GamePath(managed.Path));
            else if (manualPak is not null && ManualBackups().Contains(manualPak, StringComparer.OrdinalIgnoreCase))
            {
                var source = GamePath(manualPak);
                if (new FileInfo(source).Length > UpdateClient.MaxDownload) throw new InvalidDataException("Kopia PAK jest zbyt duża.");
                bytes = File.ReadAllBytes(source);
                if (!LooksLikePak(bytes)) throw new InvalidDataException("Wybrana kopia nie jest poprawnym archiwum PAK.");
                log("Używam wskazanej ręcznej kopii PAK o nieznanym buildzie: " + manualPak);
            }
            else throw new InvalidOperationException("Wskaż ręczną kopię PAK albo przywróć oryginał przez Steam.");
            changes.Add(GameInstallation.PakPath, bytes);
        }
        if (state is null)
        {
            if (manualFiles is null || !Package.FilesMatch(game, manualFiles))
                throw new InvalidOperationException("Najpierw sprawdź zgodność ręcznie zainstalowanych plików.");
            foreach (var name in manualFiles.Keys)
            {
                if (SteamFile(name)) steamNeeded = true;
                else changes.Add(name, null);
            }
            Apply(changes, null, null, game.Build);
            log(steamNeeded ? "Usunięto dodane pliki. Podmieniony PAK przywróci weryfikacja plików w Steam." : "Cofnięto ręcznie wgrane spolszczenie. Kopie plików i aktywny PAK zostały zachowane.");
            return steamNeeded;
        }
        foreach (var file in state.Files)
        {
            var sha = Hash(GamePath(file.Path));
            if (sha is not null && sha == file.OriginalSha256 && sha != file.Sha256) continue;
            if (sha is not null && sha != file.Sha256) throw new IOException("Plik spolszczenia został zmieniony poza launcherem. Przywracanie przerwane, aby go nie nadpisać.");
            if (RestorableOriginal(file)) changes.Add(file.Path, Original(file));
            else if (SteamFile(file.Path)) steamNeeded = sha is not null;
            else changes.Add(file.Path, null);
        }
        Apply(changes, null, state, game.Build);
        log(steamNeeded ? "Przywrócono pozostałe pliki. Oryginalny PAK fr-FR przywróci weryfikacja plików w Steam." : "Przywrócono oryginalne pliki gry.");
        return steamNeeded;
    }
    void Apply(Dictionary<string, byte[]?> changes, InstallState? next, InstallState? previous, string build)
    {
        var record = new Journal(build, previous, []);
        var history = "history/" + Guid.NewGuid().ToString("N");
        log("Przygotowuję kopie plików przed zmianami...");
        foreach (var (name, data) in changes)
        {
            var path = GamePath(name);
            var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var backup = before is null ? null : history + "/" + record.Files.Count + ".bin";
            if (backup is not null) AtomicWrite(SafeUnder(storage, backup), before!);
            record.Files.Add(new(name, before is null ? null : Hash(before), data is null ? null : Hash(data), backup));
        }
        RequireReady(build);
        foreach (var file in record.Files)
            if (Hash(GamePath(file.Path)) != file.BeforeSha256) throw new IOException("Pliki gry zmieniły się podczas tworzenia kopii. Spróbuj ponownie.");
        AtomicWrite(PendingPath, JsonSerializer.SerializeToUtf8Bytes(record, UpdateClient.Json));
        try
        {
            foreach (var file in record.Files)
            {
                RequireReady(build);
                var path = GamePath(file.Path);
                if (Hash(path) != file.BeforeSha256) throw new IOException("Plik gry zmienił się podczas instalacji.");
                log((file.AfterSha256 is null ? "Wyłączam: " : "Zapisuję: ") + file.Path);
                if (changes[file.Path] is { } data) AtomicWrite(path, data);
                else File.Delete(path);
            }
            RequireReady(build);
            SaveState(next);
            File.Delete(PendingPath);
        }
        catch
        {
            log("Operacja nie została ukończona. Cofam wprowadzone zmiany...");
            Recover();
            throw;
        }
    }
    void Recover()
    {
        var record = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(PendingPath), UpdateClient.Json) ?? throw new InvalidDataException("Uszkodzony zapis operacji.");
        RequireReady(record.Build);
        foreach (var file in record.Files)
        {
            var current = Hash(GamePath(file.Path));
            if (current != file.BeforeSha256 && current != file.AfterSha256)
                throw new IOException("Steam lub inny program zmienił pliki po przerwaniu operacji. Zachowano kopie; potrzebne ręczne sprawdzenie.");
            if (file.Backup is not null && Hash(SafeUnder(storage, file.Backup)) != file.BeforeSha256)
                throw new InvalidDataException("Niepoprawna kopia przerwanej operacji.");
        }
        foreach (var file in Enumerable.Reverse(record.Files))
        {
            var path = GamePath(file.Path);
            if (Hash(path) == file.BeforeSha256) continue;
            if (file.Backup is null) File.Delete(path);
            else AtomicWrite(path, File.ReadAllBytes(SafeUnder(storage, file.Backup)));
        }
        SaveState(record.PriorState);
        File.Delete(PendingPath);
        log("Cofnięto przerwaną operację. Poprzednie pliki zostały zachowane.");
    }
}
