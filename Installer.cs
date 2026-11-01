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
        this.ensureClosed = ensureClosed ?? RequireClosed;
    }
    internal static void RequireClosed()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                if (name.StartsWith("Aion2", StringComparison.OrdinalIgnoreCase) && !name.Equals("Aion2PL", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Zamknij AION 2 przed zmianą plików gry.");
            }
        }
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
        if (!name.StartsWith("Aion2/Content/L10N/", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals(FontCulture.Path, StringComparison.OrdinalIgnoreCase) && name != GameInstallation.PakPath && !IsBackupName(name))
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
    internal bool FontFixEnabled()
    {
        var path = GamePath(FontCulture.Path);
        return FontCulture.Enabled(File.Exists(path) ? File.ReadAllBytes(path) : null);
    }
    internal bool Intact(InstallState state) => GameInstallation.Read(root) is { Updating: false } game && game.Build == state.Build &&
        state.Files.All(f => Hash(GamePath(f.Path)) == f.Sha256) && !File.Exists(GamePath(GameInstallation.PakPath)) && FontFixEnabled();
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
    internal void Install(Release release, Dictionary<string, byte[]> files)
    {
        release.Validate();
        var game = GameInstallation.Read(root);
        release.RequireBuild(game);
        RequireReady(release.SteamBuildId);
        if (State() is null && Package.IsInstalled(game, files) && FontFixEnabled())
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
        var config = GamePath(FontCulture.Path);
        var existingConfig = File.Exists(config) ? File.ReadAllBytes(config) : null;
        var updatedConfig = FontCulture.Add(existingConfig);
        if (existingConfig is null || !existingConfig.SequenceEqual(updatedConfig)) changes.Add(FontCulture.Path, updatedConfig);
        var newFiles = new List<InstalledFile>();
        var history = "history/" + Guid.NewGuid().ToString("N");
        foreach (var (name, bytes) in files)
        {
            if (!name.StartsWith("Aion2/Content/L10N/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Plik nie należy do lokalizacji.");
            var path = GamePath(name);
            var old = previous?.Files.Find(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
            string? originalBackup = old?.OriginalBackup, originalSha = old?.OriginalSha256;
            bool wasTranslation = old?.OriginalWasTranslation == true || (old is not null && old.OriginalSha256 == old.Sha256);
            var currentSha = Hash(path);
            if (old is null || currentSha != old.Sha256)
            {
                originalSha = currentSha;
                wasTranslation = currentSha == Hash(bytes);
                originalBackup = currentSha is null ? null : history + "/original-" + newFiles.Count + ".bin";
                if (originalBackup is not null) AtomicWrite(SafeUnder(storage, originalBackup), File.ReadAllBytes(path));
            }
            newFiles.Add(new(name, Hash(bytes), originalBackup, originalSha, wasTranslation));
            changes.Add(name, bytes);
        }
        if (previous is not null)
            foreach (var file in previous.Files.Where(f => !files.ContainsKey(f.Path)))
            {
                if (Hash(GamePath(file.Path)) != file.Sha256) throw new IOException("Plik wcześniejszego wydania został zmieniony poza launcherem. Zachowano go bez zmian.");
                changes.Add(file.Path, file.OriginalWasTranslation || file.OriginalSha256 == file.Sha256 ? null : Original(file));
            }
        var pak = previous?.Pak;
        var activePak = GamePath(GameInstallation.PakPath);
        if (File.Exists(activePak))
        {
            var bytes = File.ReadAllBytes(activePak);
            var backupName = GameInstallation.PakPath + ".aion2pl-" + release.SteamBuildId + "-" + Guid.NewGuid().ToString("N") + ".backup";
            pak = new(backupName, Hash(bytes), release.SteamBuildId);
            changes.Add(backupName, bytes);
            changes.Add(GameInstallation.PakPath, null);
        }
        var next = new InstallState(root, release.Version, release.SteamBuildId, release.Sha256, newFiles, pak);
        Apply(changes, next, previous, release.SteamBuildId);
        log("Spolszczenie zainstalowane. Możesz uruchomić grę z językiem English.");
    }
    internal bool NeedsSteamVerification()
    {
        if (HasPending) return false;
        var game = GameInstallation.Read(root);
        if (game.EnglishPakPresent) return false;
        var pak = State()?.Pak;
        return pak is null || pak.Build != game.Build || Hash(GamePath(pak.Path)) != pak.Sha256;
    }
    internal void Restore(Dictionary<string, byte[]>? manualFiles = null, string? manualPak = null)
    {
        using var operation = Lock();
        if (HasPending) { Recover(); return; }
        var game = GameInstallation.Read(root);
        RequireReady(game.Build);
        var state = State();
        var changes = new Dictionary<string, byte[]?>();
        if (state is not null)
        {
            var config = GamePath(FontCulture.Path);
            var currentConfig = File.Exists(config) ? File.ReadAllBytes(config) : null;
            var restoredConfig = FontCulture.Remove(currentConfig);
            if (currentConfig is not null && (restoredConfig is null || !currentConfig.SequenceEqual(restoredConfig)))
                changes.Add(FontCulture.Path, restoredConfig);
        }
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
            foreach (var name in manualFiles.Keys) changes.Add(name, null);
            Apply(changes, null, null, game.Build);
            log("Cofnięto ręcznie wgrane spolszczenie. Kopie plików i aktywny PAK zostały zachowane.");
            return;
        }
        foreach (var file in state.Files)
        {
            var sha = Hash(GamePath(file.Path));
            if (sha is not null && sha != file.Sha256) throw new IOException("Plik spolszczenia został zmieniony poza launcherem. Przywracanie przerwane, aby go nie nadpisać.");
            changes.Add(file.Path, file.OriginalWasTranslation || file.OriginalSha256 == file.Sha256 ? null : Original(file));
        }
        Apply(changes, null, state, game.Build);
        log("Przywrócono poprzednie pliki. Kopie PAK pozostają zachowane.");
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
