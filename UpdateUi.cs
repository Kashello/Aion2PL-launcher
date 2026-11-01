using System.Diagnostics;
using System.Text.Json;

namespace Aion2PL;

internal sealed partial class LauncherWindow
{
    readonly UpdateClient client = new();
    readonly CancellationTokenSource lifetime = new();
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 3 * 60 * 60 * 1000 };
    readonly RichTextBox logBox = new() { Dock = DockStyle.Bottom, Height = 235, ReadOnly = true, Visible = false,
        BackColor = Color.FromArgb(15, 13, 25), ForeColor = Color.FromArgb(208, 202, 233),
        Font = new Font("Consolas", 8.5f), BorderStyle = BorderStyle.FixedSingle, DetectUrls = false };
    readonly CheckBox details = new() { Text = "Pokaż szczegóły działań", AutoSize = true, Margin = new Padding(18, 17, 0, 0) };
    readonly Label versions = new();
    readonly string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aion2PL");
    Button choose = null!, refresh = null!, install = null!, restore = null!;
    Release? release;
    InstallState? installed;
    Installer? installer;
    bool busy, pending;
    bool noPublishedRelease;
    Dictionary<string, byte[]>? manualFiles;
    static readonly Color Good = Color.FromArgb(110, 231, 183);
    static readonly Color Bad = Color.FromArgb(253, 164, 175);
    static readonly Color Waiting = Color.FromArgb(252, 211, 77);
    string serverMessage = "Sprawdzam dostępność wydania...";

    void ConfigureUpdates(bool checkNetwork)
    {
        details.ForeColor = Muted;
        details.CheckedChanged += (_, _) => { logBox.Visible = details.Checked; details.Text = details.Checked ? "Ukryj szczegóły działań" : "Pokaż szczegóły działań"; };
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
    internal void PreviewManualFiles(Dictionary<string, byte[]> files) { manualFiles = files; RenderStatus(); }
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
        versions.Text = $"Zainstalowano: {installed?.Version ?? (manualMatch ? release!.Version + " (ręcznie)" : game?.DatPresent == true ? "wykryto plik / trwa weryfikacja" : "—")}    •    Dostępne: {release?.Version ?? "—"}";
        bool intact = false;
        try { intact = installed is not null ? installer?.Intact(installed) == true : manualMatch && !game!.EnglishPakPresent && installer?.FontFixEnabled() == true; }
        catch (Exception e) when (Expected(e)) { Log(e.Message); }
        status.Text = pending ? "Wykryto przerwaną operację.\nZamknij grę i użyj przycisku cofnięcia operacji." : game switch
        {
            null => "Wybierz folder gry, aby sprawdzić jej stan.",
            { Updating: true } => "Poczekaj na zakończenie aktualizacji Steam.",
            _ when release is null => serverMessage,
            _ when release.SteamBuildId != game.Build => $"Czekamy na spolszczenie do buildu {game.Build}.\nDostępna paczka pasuje do buildu {release.SteamBuildId}.",
            _ when manualMatch && intact => "Spolszczenie jest już zainstalowane ręcznie i zgodne.\nMożesz uruchomić grę z językiem English.",
            _ when installed is not null && Release.Date(release.Version) < Release.Date(installed.Version) => "Masz nowsze wydanie niż obecnie dostępne na stronie.",
            _ when installed is not null && !release.IsNewerThan(installed.Version) && intact => "Spolszczenie jest aktualne.\nMożesz uruchomić grę z językiem English.",
            _ when installed is not null && release.IsNewerThan(installed.Version) => $"Nowe wydanie {release.Version} jest gotowe do pobrania.",
            _ when game.EnglishPakPresent && game.DatPresent => "Steam przywrócił angielskie pliki.\nZainstaluj zgodne spolszczenie ponownie.",
            _ => "Zgodne spolszczenie jest gotowe do instalacji.\nZamknij grę przed rozpoczęciem."
        };
        if (busy && installed is null && release is not null && compatible && game?.DatPresent == true && manualFiles is null)
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
        choose.Enabled = refresh.Enabled = !busy;
    }
    async Task CheckUpdates()
    {
        if (busy) return;
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
            if (release is not null && installed is null && installation is { DatPresent: true, Updating: false } && installation.Build == release.SteamBuildId)
            {
                RenderStatus();
                Log("Sprawdzam istniejące spolszczenie bez zmiany plików gry...");
                manualFiles = await client.VerifiedFiles(release, Path.Combine(dataRoot, "packages"), Log, null, lifetime.Token);
                Log(Package.IsInstalled(installation, manualFiles) && installer?.FontFixEnabled() == true ? "Wykryto zgodne spolszczenie i ustawienie polskich znaków." : "Dostępna instalacja lub naprawa spolszczenia i ustawień polskich znaków.");
            }
        }
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
            Installer.RequireClosed();
            var selected = release;
            var target = installer;
            var progress = new Progress<int>(value => { install.Text = $"Pobieranie: {value}%"; });
            var files = await client.VerifiedFiles(selected, Path.Combine(dataRoot, "packages"), Log, progress, lifetime.Token);
            install.Text = "Sprawdzanie i instalacja...";
            await Task.Run(() => target.Install(selected, files));
            MessageBox.Show(this, "Spolszczenie zostało zainstalowane. Uruchom grę z językiem English.", "Aion2PL", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (MessageBox.Show(this,
                "To spolszczenie wgrano wcześniej ręcznie lub zmienił się build gry. Launcher nie ma potwierdzonej kopii oryginalnego PAK.\n\nOtworzyć sprawdzanie plików w Steam? Po jego zakończeniu wróć tutaj, kliknij Odśwież, a następnie Przywróć oryginał.",
                "Przywracanie przez Steam", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                try
                {
                    Installer.RequireClosed();
                    Process.Start(new ProcessStartInfo("steam://validate/3393110") { UseShellExecute = true });
                    Log("Otwarto weryfikację Steam. Poczekaj na zakończenie, kliknij Odśwież i Przywróć oryginał. Pliki gry pozostają na razie bez zmian.");
                    ShowDetails();
                }
                catch (Exception e) when (Expected(e) || e is System.ComponentModel.Win32Exception) { Failure(e); }
            }
            return;
        }
        var question = pending ? "Cofnąć przerwaną operację?" : manualPak is not null ?
            $"Wykryto ręczną kopię: {Path.GetFileName(manualPak)}\nBuild gry: {installation!.Build}\n\nWersja tej kopii nie jest zapisana. Kopia sprzed patcha może nie pasować. Przywrócić ją i cofnąć spolszczenie?\n\nPlik kopii pozostanie zachowany." : "Cofnąć spolszczenie i przywrócić poprzednie pliki?";
        if (MessageBox.Show(this, question, "Aion2PL", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        busy = true;
        RenderStatus();
        try { var target = installer; var files = manualFiles; await Task.Run(() => target.Restore(files, manualPak)); }
        catch (Exception e) when (Expected(e)) { Failure(e); }
        finally { busy = false; RefreshGame(); }
    }
    void Failure(Exception e)
    {
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
        try { Installer.RequireClosed(); logs.Add("Sprawdzenie procesów gry: gra zamknięta."); }
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
