using System.Diagnostics;
using System.Text.Json;

namespace Aion2PL;

internal sealed partial class LauncherWindow
{
    readonly UpdateClient client;
    readonly CancellationTokenSource lifetime = new();
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 3 * 60 * 60 * 1000 };
    readonly RichTextBox logBox = new() { Dock = DockStyle.Bottom, Height = 235, ReadOnly = true, Visible = false,
        BackColor = Color.FromArgb(15, 13, 25), ForeColor = Color.FromArgb(208, 202, 233),
        Font = new Font("Consolas", 8.5f), BorderStyle = BorderStyle.FixedSingle, DetectUrls = false };
    readonly CheckBox details = new() { Text = "Pokaż szczegóły", AutoSize = true, Margin = new Padding(12, 17, 0, 0) };
    readonly Label versions = new();
    readonly string dataRoot;
    bool localPackage;
    internal Release? CurrentRelease => release;
    Button choose = null!, refresh = null!, install = null!, restore = null!, checkLauncher = null!;
    Release? release;
    InstallState? installed;
    Installer? installer;
    bool busy, pending;
    bool noPublishedRelease;
    PackageFiles? manualFiles;
    static readonly Color Good = Color.FromArgb(110, 231, 183);
    static readonly Color Bad = Color.FromArgb(253, 164, 175);
    static readonly Color Waiting = Color.FromArgb(252, 211, 77);
    string serverMessage = "Sprawdzam dostępność wydania...";

    void ConfigureUpdates(bool checkNetwork)
    {
        details.ForeColor = Muted;
        details.CheckedChanged += (_, _) => { logBox.Visible = details.Checked; details.Text = details.Checked ? "Ukryj szczegóły" : "Pokaż szczegóły"; };
        Log(installation is null ? "Wskaż folder gry." : $"Wykryto AION 2, Steam build {installation.Build}.");
        if (checkNetwork)
        {
            Shown += async (_, _) => { await CheckUpdates(); updateTimer.Start(); };
            updateTimer.Tick += async (_, _) => { if (!busy) await CheckUpdates(); };
        }
        else { serverMessage = "Podgląd wyglądu — bez sprawdzania sieci."; RenderStatus(); }
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; Log("Poczekaj na zakończenie operacji przed zamknięciem."); } };
    }
    internal void ShowDetails() => details.Checked = true;
    internal void PreviewRelease(Release value) { release = value; RenderStatus(); }
    internal void PreviewManualFiles(PackageFiles files) { manualFiles = files; RenderStatus(); }
    /// <summary>Offline test of a locally built release ZIP before publishing (no kashello.pl, no GitHub).</summary>
    internal void UseLocalPackage(string zip)
    {
        var files = Package.Read(zip);
        if (files.Version is null) throw new InvalidDataException("Opis paczki nie podaje wersji wydania.");
        var bytes = File.ReadAllBytes(zip);
        var local = new Release("aion2pl", files.Version, files.Build, "https://localhost/aion2pl-local-package", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length);
        local.Validate();
        var cache = Path.Combine(dataRoot, "packages");
        Directory.CreateDirectory(cache);
        var cached = Path.Combine(cache, local.Sha256.ToLowerInvariant() + ".zip");
        if (!File.Exists(cached) || Installer.Hash(cached) != local.Sha256) Installer.AtomicWrite(cached, bytes);
        localPackage = true;
        release = local;
        manualFiles = files;
        noPublishedRelease = false;
        serverMessage = "Tryb testu lokalnej paczki.";
        Text += " — TEST LOKALNEJ PACZKI";
        Log($"Tryb testu lokalnej paczki {Path.GetFileName(zip)}: wersja {files.Version}, build {files.Build}. Bez połączenia z kashello.pl i GitHubem.");
        ShowDetails();
        RefreshGame();
    }
    internal object PreviewState => new { GameBuild = installation?.Build, PackageBuild = release?.SteamBuildId,
        BuildText = build.Text, BuildColor = ColorTranslator.ToHtml(build.ForeColor), Status = status.Text, InstallEnabled = install.Enabled };
    static bool Expected(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or FormatException or HttpRequestException or OperationCanceledException;
    void AddLink(FlowLayoutPanel panel, string name, string url)
    {
        var link = new LinkLabel { Text = name, AutoSize = true, LinkColor = Gold, ActiveLinkColor = Color.White, VisitedLinkColor = Gold, Margin = new Padding(0, 0, 24, 0) };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log("Nie udało się otworzyć przeglądarki."); }
        };
        panel.Controls.Add(link);
    }
    void AddLicenses(FlowLayoutPanel panel)
    {
        var link = new LinkLabel { Text = "Licencje", AutoSize = true, LinkColor = Muted, Margin = new Padding(0) };
        link.LinkClicked += (_, _) =>
        {
            using var dialog = new Form { Text = "Cinzel i Inter — licencje czcionek", Size = new Size(740, 580), StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = Color.White, Font = AppFonts.Ui(10) };
            var tabs = new TabControl { Dock = DockStyle.Fill };
            using var polishStream = typeof(Program).Assembly.GetManifestResourceStream("OFL-PL.txt")!;
            using var polishReader = new StreamReader(polishStream);
            foreach (var (title, text) in new[] { ("Po polsku", polishReader.ReadToEnd()), ("Oryginały", AppFonts.Licenses()) })
            {
                var page = new TabPage(title) { BackColor = Card, Padding = new Padding(12) };
                page.Controls.Add(new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Card, ForeColor = Color.White, Text = text, Font = AppFonts.Ui(10), BorderStyle = BorderStyle.None });
                tabs.TabPages.Add(page);
            }
            dialog.Controls.Add(tabs);
            dialog.ShowDialog(this);
        };
        panel.Controls.Add(link);
    }
    void Log(string message)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }
        if (logBox.TextLength > 64000) logBox.Clear();
        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        logBox.SelectionStart = logBox.TextLength;
        logBox.ScrollToCaret();
    }
    void RefreshGame()
    {
        try
        {
            installation = installation is null ? GameInstallation.Find() : GameInstallation.Read(installation.Root);
            installer = installation is null ? null : new Installer(installation.Root, dataRoot, Log);
            pending = installer?.HasPending == true;
            installed = pending ? null : installer?.State();
        }
        catch (Exception e) when (Expected(e)) { installation = null; installer = null; installed = null; pending = false; Log(e.Message); }
        RenderStatus();
    }
    void RenderStatus()
    {
        var game = installation;
        folder.Text = game?.Root ?? "Nie znaleziono gry. Wskaż folder AION 2.";
        bool compatible = game is { Updating: false } && release is not null && game.Build == release.SteamBuildId;
        var buildStatus = game is null ? "● WSKAŻ FOLDER GRY" : game.Updating ? "● AKTUALIZACJA STEAM W TOKU" :
            release is null ? noPublishedRelease ? "● BRAK ZGODNEJ PACZKI" : "● OCZEKIWANIE NA DANE" :
            compatible ? "● ZGODNE" : "● BRAK ZGODNEJ PACZKI";
        build.Text = $"Build Twojej gry:  {game?.Build ?? "—"}\nBuild spolszczenia:  {release?.SteamBuildId ?? "—"}\n{buildStatus}";
        build.ForeColor = compatible ? Good : game is { Updating: false } && (release is not null || noPublishedRelease) ? Bad : Waiting;
        bool manualMatch = false;
        try { manualMatch = compatible && installed is null && manualFiles is not null && Package.FilesMatch(game!, manualFiles); }
        catch (Exception e) when (Expected(e)) { Log(e.Message); }
        versions.Text = $"Zainstalowano: {installed?.Version ?? (manualMatch ? release!.Version + " (ręcznie)" : busy && manualFiles is null && compatible ? "trwa weryfikacja" : "—")}    •    Dostępne: {release?.Version ?? "—"}";
        bool intact = false;
        try { intact = installed is not null ? installer?.Intact(installed) == true : manualMatch; }
        catch (Exception e) when (Expected(e)) { Log(e.Message); }
        status.Text = pending ? "Wykryto przerwaną operację.\nZamknij grę i użyj przycisku cofnięcia operacji." : game switch
        {
            null => "Wybierz folder gry, aby sprawdzić jej stan.",
            { Updating: true } => "Poczekaj na zakończenie aktualizacji Steam.",
            _ when release is null => serverMessage,
            _ when release.SteamBuildId != game.Build => $"Czekamy na spolszczenie do buildu {game.Build}.\nDostępna paczka pasuje do buildu {release.SteamBuildId}.",
            _ when manualMatch && intact => "Spolszczenie jest już zainstalowane ręcznie i zgodne.\nW grze wybierz język Polski.",
            _ when installed is not null && Release.Date(release.Version) < Release.Date(installed.Version) => "Masz nowsze wydanie niż obecnie dostępne na stronie.",
            _ when installed is not null && !release.IsNewerThan(installed.Version) && intact => "Spolszczenie jest aktualne.\nW grze wybierz język Polski.",
            _ when installed is not null && release.IsNewerThan(installed.Version) => $"Nowe wydanie {release.Version} jest gotowe do pobrania.",
            _ when installed is not null && !intact => "Steam przywrócił oryginalne pliki.\nZainstaluj zgodne spolszczenie ponownie.",
            _ => "Zgodne spolszczenie jest gotowe do instalacji.\nZamknij grę przed rozpoczęciem."
        };
        try
        {
            if (!busy && !pending && game is { Updating: false } && installer?.LegacyLeftovers() is { Any: true } leftovers)
                status.Text += leftovers.LooseDat ? "\nWykryto luźny DAT starej metody — instalacja zaproponuje usunięcie." : "\nAngielski PAK jest wyłączony (stara metoda) — może być potrzebna weryfikacja Steam.";
        }
        catch (Exception e) when (Expected(e)) { Log(e.Message); }
        if (busy && installed is null && release is not null && compatible && manualFiles is null)
            status.Text = "Sprawdzam istniejące spolszczenie...\nPorównuję pliki z paczką dla Twojego buildu.";
        status.ForeColor = !pending && intact && compatible ? Good : game is { Updating: false } && !compatible && (release is not null || noPublishedRelease) ? Bad : Waiting;
        install.Text = intact && compatible && (installed is null || release?.Version == installed.Version) ? "Spolszczenie już zainstalowane" : installed is null ? "Zainstaluj spolszczenie" : "Zainstaluj / aktualizuj spolszczenie";
        install.Enabled = !busy && !pending && game is { Updating: false } && release is not null && release.SteamBuildId == game.Build &&
            (installed is null ? !intact : Release.Date(release.Version) >= Release.Date(installed.Version) && (release.IsNewerThan(installed.Version) || !intact));
        bool needsOriginal = false, hasManualCopy = false;
        try { needsOriginal = installer?.NeedsSteamVerification() == true; hasManualCopy = needsOriginal && installer!.ManualBackups().Count > 0; }
        catch (Exception e) when (Expected(e)) { Log(e.Message); needsOriginal = true; }
        restore.Text = pending ? "Cofnij przerwaną operację" : needsOriginal ? hasManualCopy ? "Przywróć z kopii PAK" : "Przywróć oryginał przez Steam" : "Przywróć oryginał";
        restore.Enabled = !busy && installer is not null && (installed is not null || pending || manualMatch);
        choose.Enabled = refresh.Enabled = checkLauncher.Enabled = !busy;
    }
    internal async Task CheckLauncherManually()
    {
        if (busy) return;
        if (localPackage) { Log("Tryb testu lokalnej paczki: nie sprawdzam aktualizacji launchera na GitHubie."); return; }
        busy = true;
        RenderStatus();
        try
        {
            Log("Sprawdzam aktualizacje launchera na GitHubie...");
            var latest = await client.LatestLauncher(lifetime.Token);
            if (latest is null)
            {
                MessageBox.Show(this, "Nie znaleziono jeszcze wydania launchera na GitHubie.", "Aion2PL", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (!latest.IsNewer())
            {
                MessageBox.Show(this, "Launcher jest aktualny.", "Aion2PL", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (await LauncherSelfUpdate.Prepare(latest, client, Log, lifetime.Token))
            {
                busy = false;
                Close();
                return;
            }
        }
        catch (Exception e) when (Expected(e) || e is System.ComponentModel.Win32Exception or KeyNotFoundException) { Failure(e); }
        finally { busy = false; if (!IsDisposed) RenderStatus(); }
    }
    internal async Task CheckUpdates()
    {
        if (busy) return;
        if (localPackage)
        {
            RefreshGame();
            Log("Tryb testu lokalnej paczki: odświeżono stan gry, bez łączenia z kashello.pl i GitHubem.");
            return;
        }
        busy = true;
        manualFiles = null;
        RefreshGame();
        try
        {
            try
            {
                var launcher = await client.LatestLauncher(lifetime.Token);
                if (launcher is not null && await LauncherSelfUpdate.Prepare(launcher, client, Log, lifetime.Token))
                {
                    busy = false;
                    Close();
                    return;
                }
            }
            catch (Exception e) when (Expected(e) || e is System.ComponentModel.Win32Exception or KeyNotFoundException) { Log("Nie udało się sprawdzić aktualizacji launchera: " + e.Message); }
            var result = await client.Latest(Log, lifetime.Token);
            release = result.Release;
            noPublishedRelease = release is null;
            serverMessage = result.Message;
            if (release is not null && installed is null && installation is { Updating: false } && installation.Build == release.SteamBuildId)
            {
                RenderStatus();
                Log("Sprawdzam istniejące spolszczenie bez zmiany plików gry...");
                manualFiles = await client.VerifiedFiles(release, Path.Combine(dataRoot, "packages"), Log, null, lifetime.Token);
                Log(Package.IsInstalled(installation, manualFiles) ? "Wykryto już zainstalowane zgodne spolszczenie. Ponowna instalacja nie jest potrzebna." : "Dostępna instalacja lub naprawa spolszczenia.");
            }
        }
        catch (LauncherTooOldException e) { noPublishedRelease = false; release = null; serverMessage = "Dostępna paczka wymaga nowszego launchera.\nKliknij „Sprawdź launcher”."; Log(e.Message); }
        catch (Exception e) when (Expected(e)) { release = null; noPublishedRelease = false; serverMessage = "Nie udało się sprawdzić aktualizacji. Spróbuj później.\nZainstalowane pliki pozostają bez zmian."; Log(e is OperationCanceledException ? "Przekroczono czas połączenia z serwerem." : e.Message); }
        finally { busy = false; RenderStatus(); }
    }
    async Task InstallUpdate()
    {
        if (busy || !install.Enabled || release is null || installation is null || installer is null) return;
        busy = true;
        RenderStatus();
        try
        {
            Installer.RequireClosed(installation.Root);
            var selected = release;
            var target = installer;
            var leftovers = target.LegacyLeftovers();
            if (leftovers.EnglishPakMissing && !leftovers.EnglishPakRestorable)
            {
                busy = false;
                OfferSteamValidation("Wcześniejsza metoda spolszczenia wyłączyła angielski PAK, a launcher nie ma jego kopii dla tego buildu. Po patchu daje to „???”.\n\nNajpierw sprawdź spójność plików w Steam (przywróci oryginały), potem kliknij Odśwież i zainstaluj spolszczenie ponownie.\n\nOtworzyć sprawdzanie plików w Steam?");
                return;
            }
            bool cleanup = leftovers.LooseDat && MessageBox.Show(this,
                "Wykryto luźny plik L10NString.dat ze starej metody spolszczenia (Content\\L10N\\Text\\en-US). Po patchu daje on „???”.\n\nUsunąć go podczas instalacji? Kopia zostanie w historii launchera.",
                "Sprzątanie starej metody", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            var progress = new Progress<int>(value => { install.Text = $"Pobieranie: {value}%"; });
            var files = await client.VerifiedFiles(selected, Path.Combine(dataRoot, "packages"), Log, progress, lifetime.Token);
            install.Text = "Sprawdzanie i instalacja...";
            await Task.Run(() => target.Install(selected, files, cleanup));
            MessageBox.Show(this, "Spolszczenie zostało zainstalowane.\n\nUruchom grę, wybierz Polski jako język tekstu i otwórz dziennik zadań.", "Aion2PL", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (Expected(e)) { Failure(e); }
        finally { busy = false; RefreshGame(); }
    }
    async Task RestoreUpdate()
    {
        if (busy || installer is null) return;
        string? manualPak = null;
        bool needsOriginal;
        List<string> manualCopies;
        try { needsOriginal = !pending && installer.NeedsSteamVerification(); manualCopies = needsOriginal ? installer.ManualBackups() : []; }
        catch (Exception e) when (Expected(e)) { Failure(e); return; }
        if (needsOriginal && manualCopies.Count > 0)
        {
            manualPak = manualCopies[0];
            if (manualCopies.Count > 1)
            {
                using var picker = new OpenFileDialog { Title = "Wybierz ręczną kopię PAK", CheckFileExists = true,
                    InitialDirectory = Path.GetDirectoryName(Path.Combine(installation!.Root, GameInstallation.PakPath)),
                    FileName = Path.GetFileName(manualPak), Filter = "Kopie PAK|*.pak.backup*;*.pak.aion2pl-*.backup", RestoreDirectory = true };
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                manualPak = Path.GetRelativePath(installation!.Root, picker.FileName).Replace('\\', '/');
                if (!manualCopies.Contains(manualPak, StringComparer.OrdinalIgnoreCase)) { Failure(new InvalidOperationException("Wybierz kopię PAK z folderu lokalizacji gry.")); return; }
            }
        }
        if (needsOriginal && manualPak is null)
        {
            OfferSteamValidation("To spolszczenie wgrano wcześniej ręcznie lub zmienił się build gry. Launcher nie ma potwierdzonej kopii oryginalnego angielskiego PAK.\n\nOtworzyć sprawdzanie plików w Steam? Po jego zakończeniu wróć tutaj, kliknij Odśwież, a następnie Przywróć oryginał.");
            return;
        }
        var question = pending ? "Cofnąć przerwaną operację?" : manualPak is not null ?
            $"Wykryto ręczną kopię: {Path.GetFileName(manualPak)}\nBuild gry: {installation!.Build}\n\nWersja tej kopii nie jest zapisana. Kopia sprzed patcha może nie pasować. Przywrócić ją i cofnąć spolszczenie?\n\nPlik kopii pozostanie zachowany." : "Cofnąć spolszczenie i przywrócić poprzednie pliki?";
        if (MessageBox.Show(this, question, "Aion2PL", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        busy = true;
        RenderStatus();
        bool steamNeeded = false;
        try { var target = installer; var files = manualFiles; steamNeeded = await Task.Run(() => target.Restore(files, manualPak)); }
        catch (Exception e) when (Expected(e)) { Failure(e); }
        finally { busy = false; RefreshGame(); }
        if (steamNeeded) OfferSteamValidation("Launcher nie ma kopii oryginalnego PAK fr-FR (spolszczenie wgrano ręcznie). Pozostałe pliki przywrócono.\n\nOtworzyć sprawdzanie plików w Steam, aby odzyskać oryginalny PAK?");
    }
    void OfferRunningGameFolder(GameRunningElsewhereException e)
    {
        Log(e.Message);
        ShowDetails();
        GameInstallation? running = null;
        try { if (e.RunningRoot is { } root) running = GameInstallation.Read(root); }
        catch (Exception error) when (Expected(error)) { Log("Folder działającej gry nie jest poprawną instalacją Steam: " + error.Message); }
        if (running is null)
        {
            MessageBox.Show(this, e.Message, "Wybrany inny folder gry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this, e.Message + $"\n\nUstawić folder gry na {running.Root}? Potem zamknij grę i kliknij Zainstaluj ponownie.",
            "Wybrany inny folder gry", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        UseGameFolder(running);
    }
    void OfferSteamValidation(string question)
    {
        if (MessageBox.Show(this, question, "Przywracanie przez Steam", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        try
        {
            Installer.RequireClosed(installation?.Root);
            Process.Start(new ProcessStartInfo("steam://validate/3393110") { UseShellExecute = true });
            Log("Otwarto weryfikację Steam. Poczekaj na zakończenie i kliknij Odśwież. Launcher nie zmienia teraz plików gry.");
            ShowDetails();
        }
        catch (Exception e) when (Expected(e) || e is System.ComponentModel.Win32Exception) { Failure(e); }
    }
    void Failure(Exception e)
    {
        if (e is GameRunningElsewhereException elsewhere) { OfferRunningGameFolder(elsewhere); return; }
        var message = e is UnauthorizedAccessException ? "Brak dostępu do zapisu. Zamknij launcher i uruchom go jako administrator, aby zmienić pliki w tym folderze gry." : e is OperationCanceledException ? "Przekroczono czas pobierania. Spróbuj później." : e.Message;
        Log(message);
        ShowDetails();
        MessageBox.Show(this, message, "Nie ukończono operacji", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

internal static class LiveCheck
{
    internal static async Task Run(string report)
    {
        using var client = new UpdateClient();
        var logs = new List<string>();
        try { Installer.RequireClosed(GameInstallation.Find()?.Root); logs.Add("Sprawdzenie procesów gry: gra zamknięta."); }
        catch (InvalidOperationException e) { logs.Add(e.Message); }
        var result = await client.Latest(logs.Add, CancellationToken.None);
        if (result.Release is not { } release) throw new InvalidOperationException(result.Message);
        var zip = report + ".zip";
        await client.Download(release, zip, logs.Add, null, CancellationToken.None);
        var files = Package.Read(zip);
        var game = GameInstallation.Find();
        var alreadyInstalled = game is not null && game.Build == release.SteamBuildId && Package.IsInstalled(game, files);
        File.WriteAllText(report, JsonSerializer.Serialize(new { release, files = files.Keys, gameBuild = game?.Build, alreadyInstalled, logs }, UpdateClient.Json));
    }
}
