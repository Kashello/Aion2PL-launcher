using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aion2PL;

internal static class UpdateTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.Method != HttpMethod.Get || request.Content is not null || request.Headers.Authorization is not null)
                throw new Exception("Dane wysyłane do serwera");
            return Task.FromResult(respond(request));
        }
    }
    static HttpResponseMessage Json(HttpStatusCode code, object value) => new(code) { Content = new StringContent(JsonSerializer.Serialize(value, UpdateClient.Json), System.Text.Encoding.UTF8, "application/json") };
    static byte[] Dat(int marker) { var bytes = new byte[32]; bytes[0] = 2; bytes[20] = (byte)marker; return bytes; }
    static byte[] Pak(int marker = 0)
    {
        var bytes = new byte[128];
        bytes[0] = (byte)marker;
        BitConverter.GetBytes(0x5A6F12E1U).CopyTo(bytes, 64);
        BitConverter.GetBytes(11).CopyTo(bytes, 68);
        BitConverter.GetBytes(0L).CopyTo(bytes, 72);
        BitConverter.GetBytes(32L).CopyTo(bytes, 80);
        return bytes;
    }
    static readonly byte[] PolishIni = Encoding.UTF8.GetBytes("\uFEFF; Aion2PL\n[/Script/Aion2.AionGameSettings]\n+TextLanguageList=(Code=\"fr-FR\",DisplayText=\"Polski\",ServiceRegion=\"Global\")\n");
    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var output = new MemoryStream();
        using (var z = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var s = z.CreateEntry(file.Name).Open(); s.Write(file.Data); }
        return output.ToArray();
    }
    static byte[] Description(string build, byte[] frPak, byte[] ini, string? originalSha, string? frSha = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = 2, version = "15.10.2026", steam_buildid = build,
        files = new Dictionary<string, object>
        {
            [GameInstallation.FrPakPath] = originalSha is null ? new { sha256 = frSha ?? Sha(frPak) } : new { sha256 = frSha ?? Sha(frPak), original_sha256 = originalSha },
            [GameInstallation.ConfigPath] = new { sha256 = Sha(ini) },
        }
    });
    static byte[] PackageZip(string build, byte[] frPak, byte[] original, byte[]? ini = null, params (string Name, byte[] Data)[] extra)
    {
        ini ??= PolishIni;
        return Zip([(GameInstallation.FrPakPath, frPak), (GameInstallation.ConfigPath, ini), (Package.ManifestName, Description(build, frPak, ini, Sha(original))), ("INSTRUKCJA.txt", [65]), .. extra]);
    }
    static Release Meta(byte[] bytes, string build = "25926599") => new("aion2pl", "15.10.2026", build, "https://kashello.pl/api/downloads/987/file", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);

    internal static async Task Run(string report, string? releaseZip = null)
    {
        var checks = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(report))!;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        async Task Reject(Func<Task> action, string name)
        {
            bool rejected = false;
            try { await action(); } catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or HttpRequestException or OperationCanceledException or FormatException) { rejected = true; }
            Check(rejected, name);
        }
        var scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "updater-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            using (var uiFont = AppFonts.Ui(11))
            using (var titleFont = AppFonts.Title(28))
                Check(uiFont.FontFamily.Name == "Inter" && titleFont.FontFamily.Name == "Cinzel" && AppFonts.Licenses().Contains("SIL OPEN FONT LICENSE"), "Osadzone Cinzel, Inter i licencje OFL dostępne bez instalacji czcionek");
            var gameFolder = Path.Combine(scratch, "Steam/steamapps/common/AION2");
            Installer.RunningProcess Proc(int id, string name, string? path) => new(id, name, path);
            Check(Installer.BlockingProcess([Proc(10, "Aion2PL (1)", @"C:\Users\gracz\Downloads\Aion2PL (1).exe"), Proc(11, "Aion2PL-0.6.0", null), Proc(12, "Aion2PL", null)], gameFolder, 99) is null,
                "Kopie launchera (Aion2PL (1), Aion2PL-0.6.0) nie blokują instalacji");
            Check(Installer.BlockingProcess([Proc(99, "AION2", null)], gameFolder, 99) is null, "Własny proces (PID) nie blokuje");
            Check(Installer.BlockingProcess([Proc(20, "AION2", Path.Combine(gameFolder, "Aion2/Binaries/Win64/AION2.exe"))], gameFolder, 99) is { Id: 20, Name: "AION2" }, "Proces gry AION2.exe blokuje instalację");
            Check(Installer.BlockingProcess([Proc(21, "AION2", null)], null, 99) is { Id: 21 }, "Proces gry blokuje także bez dostępu do ścieżki");
            Check(Installer.BlockingProcess([Proc(22, "CrashReportClient", Path.Combine(gameFolder, "Engine/Binaries/Win64/CrashReportClient.exe"))], gameFolder, 99) is { Id: 22 }, "Proces z folderu gry blokuje instalację");
            Check(Installer.BlockingProcess([Proc(23, "Aion2Foo", @"C:\Inne\Aion2Foo.exe"), Proc(24, "Aion2Helper", null)], gameFolder, 99) is null, "Nieznany proces Aion2Foo spoza folderu gry nie blokuje");
            var runningRoot = Path.Combine(scratch, "Inna/steamapps/common/AION2");
            var otherExe = Path.Combine(runningRoot, "Aion2/Binaries/Win64/AION2.exe");
            var elsewhere = Installer.ClosedError(Proc(30, "AION2", otherExe), gameFolder);
            Check(elsewhere is GameRunningElsewhereException away && away.RunningRoot == Path.GetFullPath(runningRoot) && away.Message.Contains(otherExe) && away.Message.Contains(Path.GetFullPath(gameFolder)) && away.Message.Contains("Zmień folder"),
                "Gra z innego folderu: komunikat z obiema ścieżkami i folderem działającej gry");
            var here = Installer.ClosedError(Proc(31, "AION2", Path.Combine(gameFolder, "Aion2/Binaries/Win64/AION2.exe")), gameFolder);
            Check(here is not GameRunningElsewhereException && here.Message.Contains("AION2.exe, PID 31"), "Gra z wybranego folderu: prośba o zamknięcie z nazwą i PID");
            Check(GameInstallation.RootFromExecutable(@"C:\Gry\X\Aion2\Binaries\Win64\AION2.exe") == @"C:\Gry\X" && GameInstallation.RootFromExecutable(@"C:\Inne\AION2.exe") is null, "Folder gry z ścieżki AION2.exe");
            var localZip = Path.Combine(scratch, "local-package.zip");
            File.WriteAllBytes(localZip, PackageZip("25926599", Pak(1), Pak(0)));
            var offline = new Fake(_ => throw new HttpRequestException("sieć w trybie lokalnym"));
            // Creating a Form would install a WinForms SynchronizationContext on this thread; later awaits
            // without a message loop would then deadlock.
            WindowsFormsSynchronizationContext.AutoInstall = false;
            using (var localWindow = new LauncherWindow(false, new UpdateClient(offline), Path.Combine(scratch, "local-window-data")))
            {
                localWindow.UseLocalPackage(localZip);
                var localRelease = localWindow.CurrentRelease;
                localWindow.CheckUpdates().GetAwaiter().GetResult();
                localWindow.CheckLauncherManually().GetAwaiter().GetResult();
                Check(offline.Calls == 0 && localRelease is not null && localWindow.CurrentRelease == localRelease && localRelease.SteamBuildId == "25926599" && localRelease.Version == "15.10.2026",
                    "Tryb --local-package: Odśwież i Sprawdź launcher bez sieci, lokalna paczka zostaje");
                Check(File.Exists(Path.Combine(scratch, "local-window-data/packages", localRelease!.Sha256.ToLowerInvariant() + ".zip")), "Tryb --local-package: paczka w cache wskazanego folderu danych");
            }
            var githubRelease = new { tag_name = "v9.1.2", assets = new[] { new { name = "Aion2PL.exe", browser_download_url = "https://github.com/Kashello/Aion2PL-launcher/releases/download/v9.1.2/Aion2PL.exe", digest = "sha256:" + new string('a', 64), size = 2048 } } };
            using (var github = new UpdateClient(new Fake(_ => Json(HttpStatusCode.OK, githubRelease))))
            {
                var found = await github.LatestLauncher(CancellationToken.None);
                Check(found is not null && found.Version == "9.1.2" && found.IsNewer(), "Wydanie GitHub: wersja i suma kontrolna launchera");
            }
            using (var github = new UpdateClient(new Fake(_ => new(HttpStatusCode.NotFound))))
                Check(await github.LatestLauncher(CancellationToken.None) is null, "Brak wydania launchera nie blokuje działania");
            await Reject(() => { new LauncherRelease("9.1.2", "https://example.com/Aion2PL.exe", new string('a', 64), 2048).Validate(); return Task.CompletedTask; }, "Autoaktualizacja odrzuca obce repozytorium");
            await Reject(() => { new LauncherRelease("9.1.2", "https://github.com/Kashello/Aion2PL-launcher/releases/download/v9.1.2/Aion2PL.exe", "bad", 2048).Validate(); return Task.CompletedTask; }, "Autoaktualizacja wymaga poprawnej sumy SHA-256");
            var updateDir = Path.Combine(scratch, "launcher-update");
            Directory.CreateDirectory(updateDir);
            var oldExe = Path.Combine(updateDir, "Aion2PL.exe");
            var newExe = Path.Combine(updateDir, "new.exe");
            File.WriteAllText(oldExe, "previous launcher");
            File.WriteAllText(newExe, "new launcher");
            var oldHash = Installer.Hash(oldExe)!;
            var newHash = Installer.Hash(newExe)!;
            var updateArgs = new[] { "--apply-launcher-update", int.MaxValue.ToString(), oldExe, newExe, newHash, oldHash };
            bool started = false;
            LauncherSelfUpdate.Apply(updateArgs, Path.Combine(updateDir, "helper.exe"), _ => started = true);
            Check(started && File.ReadAllText(oldExe) == "new launcher" && Directory.GetFiles(updateDir, "Aion2PL.exe.aion2pl-backup-*").Any(f => File.ReadAllText(f) == "previous launcher"), "Aktualizacja EXE zachowuje kopię i uruchamia nową wersję");
            await Reject(() => { LauncherSelfUpdate.Apply(updateArgs, Path.Combine(updateDir, "helper.exe"), _ => { }); return Task.CompletedTask; }, "Nieoczekiwana zmiana EXE przerywa aktualizację");

            var steamFr = Pak(0);
            var zipBytes = PackageZip("25926599", Pak(1), steamFr);
            var meta = Meta(zipBytes);
            Check((meta with { Version = "09.11.2026" }).IsNewerThan("15.10.2026"), "Data: listopad nowszy od października");
            Check(!meta.IsNewerThan("15.10.2026") && !meta.IsNewerThan("16.10.2026"), "Równa lub starsza data nie jest aktualizacją");
            var okHandler = new Fake(_ => Json(HttpStatusCode.OK, meta));
            using (var client = new UpdateClient(okHandler))
            {
                var result = await client.Latest(_ => { }, CancellationToken.None);
                Check(result.Release?.DownloadUrl.Contains("987") == true, "200: URL z odpowiedzi, bez stałego ID paczki");
                await client.Latest(_ => { }, CancellationToken.None);
                Check(okHandler.Calls == 1, "Cache latest przez trzy godziny");
            }
            using (var client = new UpdateClient(new Fake(_ => Json(HttpStatusCode.NotFound, new { error = "Jeszcze nie opublikowano." }))))
            {
                var result = await client.Latest(_ => { }, CancellationToken.None);
                Check(result.Release is null && result.Message == "Jeszcze nie opublikowano.", "404: komunikat serwera bez pobierania");
            }
            using (var client = new UpdateClient(new Fake(_ => Json(HttpStatusCode.InternalServerError, new { error = "awaria" }))))
                await Reject(async () => await client.Latest(_ => { }, CancellationToken.None), "500: błąd tymczasowy");
            using (var client = new UpdateClient(new Fake(_ => throw new HttpRequestException("offline"))))
                await Reject(async () => await client.Latest(_ => { }, CancellationToken.None), "Brak sieci");
            using (var client = new UpdateClient(new Fake(_ => throw new TaskCanceledException("timeout"))))
                await Reject(async () => await client.Latest(_ => { }, CancellationToken.None), "Timeout");
            using (var client = new UpdateClient(new Fake(_ => Json(HttpStatusCode.OK, meta with { DownloadUrl = "http://kashello.pl/paczka" }))))
                await Reject(async () => await client.Latest(_ => { }, CancellationToken.None), "Odrzucenie HTTP");
            using (var client = new UpdateClient(new Fake(_ => Json(HttpStatusCode.OK, meta with { Product = "starcitizen" }))))
                await Reject(async () => await client.Latest(_ => { }, CancellationToken.None), "Odrzucenie paczki innej gry");
            using (var client = new UpdateClient(new Fake(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) })))
            {
                var download = Path.Combine(scratch, "download.zip");
                await client.Download(meta, download, _ => { }, null, CancellationToken.None);
                var files = Package.Read(download);
                Check(files.Count == 2 && files.ContainsKey(GameInstallation.FrPakPath) && files.ContainsKey(GameInstallation.ConfigPath) && !files.ContainsKey("INSTRUKCJA.txt") &&
                    files.Build == "25926599" && files.Originals[GameInstallation.FrPakPath] == Sha(steamFr), "Paczka: PAK fr-FR, UserGame.ini i suma oryginału dla buildu");
                File.Delete(download);
                await Reject(() => client.Download(meta with { Sha256 = new string('0', 64) }, download, _ => { }, null, CancellationToken.None), "Zły SHA-256 odrzuca paczkę");
                Check(!File.Exists(download), "Błędne pobranie usunięte");
                await Reject(() => client.Download(meta with { SizeBytes = meta.SizeBytes + 1 }, download, _ => { }, null, CancellationToken.None), "Zły rozmiar odrzuca paczkę");
                File.WriteAllText(download, "existing");
                await Reject(() => client.Download(meta, download, _ => { }, null, CancellationToken.None), "Brak nadpisywania istniejącego pliku pobrania");
                Check(File.ReadAllText(download) == "existing", "Błąd pobierania nie usuwa cudzego pliku");
            }
            var badZip = Path.Combine(scratch, "bad.zip");
            async Task RejectZip(byte[] bytes, string name) { File.WriteAllBytes(badZip, bytes); await Reject(() => { Package.Read(badZip); return Task.CompletedTask; }, name); }
            File.WriteAllText(badZip, "not a zip");
            await Reject(() => { Package.Read(badZip); return Task.CompletedTask; }, "Uszkodzony ZIP");
            var crcZip = PackageZip("25926599", Pak(1), steamFr);
            for (int i = 0; i < crcZip.Length - 46; i++)
                if (BitConverter.ToUInt32(crcZip, i) == 0x02014B50) { crcZip[i + 16] ^= 1; break; }
            await RejectZip(crcZip, "Błędne CRC pliku wewnątrz ZIP");
            foreach (var name in new[] { "../outside.dat", "C:/outside.dat", "/outside.dat", "Aion2/Config/../outside.ini", "Aion2/Config/UserGame.ini:evil", "Aion2/Config/CON.ini" })
                await RejectZip(PackageZip("25926599", Pak(1), steamFr, null, (name, [1])), "ZIP odrzucony: " + name);
            await RejectZip(PackageZip("25926599", Pak(1), steamFr, null, ("Aion2/Binaries/evil.dll", [1])), "Ochrona plików EXE/DLL i katalogu gry");
            await RejectZip(PackageZip("25926599", Pak(1), steamFr, null, (GameInstallation.PakPath, Pak(9))), "Paczka nie może podmienić angielskiego PAK");
            await RejectZip(Zip((GameInstallation.DatPath, Dat(1)), ("INSTRUKCJA.txt", [65])), "Stara paczka z luźnym DAT odrzucona (po patchu daje ???)");
            await RejectZip(PackageZip("25926599", Pak(1), steamFr, null, (GameInstallation.DatPath, Dat(1))), "Luźny DAT nie jest instalowany razem z PAK");
            await RejectZip(Zip((GameInstallation.FrPakPath, Pak(1)), (GameInstallation.ConfigPath, PolishIni)), "Paczka bez opisu instalacji odrzucona");
            await RejectZip(Zip((GameInstallation.FrPakPath, Pak(1)), (GameInstallation.ConfigPath, PolishIni), (Package.ManifestName, Description("25926599", Pak(1), PolishIni, null))), "Opis bez sumy oryginału PAK odrzucony");
            await RejectZip(Zip((GameInstallation.FrPakPath, Pak(1)), (GameInstallation.ConfigPath, PolishIni), (Package.ManifestName, Description("25926599", Pak(1), PolishIni, Sha(steamFr), Sha(Pak(2))))), "Suma pliku niezgodna z opisem odrzuca paczkę");

            File.WriteAllBytes(badZip, Zip((GameInstallation.FrPakPath, Pak(1)), (GameInstallation.ConfigPath, PolishIni),
                (Package.ManifestName, JsonSerializer.SerializeToUtf8Bytes(new { format = 4, steam_buildid = "25926599", files = new { } }))));
            bool tooOld = false;
            try { Package.Read(badZip); } catch (LauncherTooOldException) { tooOld = true; }
            Check(tooOld, "Paczka w nowszym formacie prosi o aktualizację launchera");
            var root = Path.Combine(scratch, "Steam/steamapps/common/AION2");
            Directory.CreateDirectory(Path.Combine(root, "Aion2/Content/Paks"));
            var manifest = Path.GetFullPath(Path.Combine(root, "../../appmanifest_3393110.acf"));
            void WriteBuild(string build) => File.WriteAllText(manifest, $"\"appid\" \"3393110\" \"installdir\" \"AION2\" \"buildid\" \"{build}\" \"TargetBuildID\" \"{build}\" \"StateFlags\" \"4\"");
            WriteBuild("25926599");
            var fr = Path.Combine(root, GameInstallation.FrPakPath);
            var sig = Path.ChangeExtension(fr, ".sig");
            var ini = Path.Combine(root, GameInstallation.ConfigPath);
            var en = Path.Combine(root, GameInstallation.PakPath);
            var dat = Path.Combine(root, GameInstallation.DatPath);
            foreach (var path in new[] { fr, ini, en, dat }) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(fr, steamFr);
            File.WriteAllText(sig, "steam signature");
            File.WriteAllText(en, "english pak");
            var userIni = Encoding.UTF8.GetBytes("[Graphics]\r\nQuality=High\r\n");
            File.WriteAllBytes(ini, userIni);
            PackageFiles Files(byte[] zip) { File.WriteAllBytes(badZip, zip); return Package.Read(badZip); }
            var package = Files(zipBytes);
            bool Untouched() => File.ReadAllText(sig) == "steam signature" && File.ReadAllText(en) == "english pak" && !File.Exists(dat);

            var install = new Installer(root, Path.Combine(scratch, "user-data"), _ => { }, () => { });
            if (releaseZip is not null)
            {
                var playerFiles = Package.Read(releaseZip);
                if (playerFiles.Count != 5 || playerFiles.Version is null) throw new InvalidDataException("Oczekiwano wydania LocRes.");
                var playerRelease = Meta(File.ReadAllBytes(releaseZip), playerFiles.Build) with { Version = playerFiles.Version };
                install.Install(meta, package);
                WriteBuild(playerFiles.Build);
                File.WriteAllText(fr, "FR after Steam patch");
                install.Install(playerRelease, playerFiles);
                Check(Package.IsInstalled(GameInstallation.Read(root), playerFiles), "ZIP wydania: zgodność wszystkich pięciu plików po migracji i instalacji");
                install.Restore();
                Check(File.ReadAllBytes(ini).SequenceEqual(userIni) && File.ReadAllText(fr) == "FR after Steam patch" &&
                      playerFiles.Keys.Where(n => n.StartsWith("Engine/")).All(n => !File.Exists(Path.Combine(root, n))),
                      "ZIP wydania: cofnięcie przywraca INI, usuwa zasoby i zachowuje PAK nowego buildu");
                WriteBuild("25926599");
                File.WriteAllBytes(fr, steamFr);
            }
            await Reject(() => { install.Install(meta with { SteamBuildId = "999" }, package); return Task.CompletedTask; }, "Inny build blokuje instalację");
            WriteBuild("26000000");
            await Reject(() => { install.Install(Meta(zipBytes, "26000000"), package); return Task.CompletedTask; }, "Build w opisie paczki musi zgadzać się z wydaniem");
            WriteBuild("25926599");
            File.WriteAllBytes(fr, Pak(7));
            await Reject(() => { install.Install(meta, package); return Task.CompletedTask; }, "PAK fr-FR inny niż oryginał buildu blokuje instalację");
            Check(File.ReadAllBytes(fr).SequenceEqual(Pak(7)) && File.ReadAllBytes(ini).SequenceEqual(userIni) && install.State() is null && Untouched(), "Odrzucona instalacja nie zmienia gry");
            File.WriteAllBytes(fr, steamFr);
            install.Install(meta, package);
            Check(File.ReadAllBytes(fr).SequenceEqual(Pak(1)) && File.ReadAllBytes(ini).SequenceEqual(PolishIni) && install.Intact(install.State()!), "Instalacja podmienia PAK fr-FR i zapisuje UserGame.ini");
            Check(Untouched() && File.ReadAllBytes(Path.ChangeExtension(fr, ".sig")).Length > 0, "Instalacja nie kładzie DAT, nie wyłącza angielskiego PAK i nie rusza .sig");
            Check(!install.NeedsSteamVerification(), "Nowa metoda nie wymaga kopii angielskiego PAK");
            Check(install.Restore() == false && File.ReadAllBytes(fr).SequenceEqual(steamFr) && File.ReadAllBytes(ini).SequenceEqual(userIni) && install.State() is null && Untouched(), "Cofnięcie przywraca oryginalny PAK fr-FR i poprzedni INI");
            File.Delete(ini);
            install.Install(meta, package);
            install.Restore();
            Check(!File.Exists(ini) && File.ReadAllBytes(fr).SequenceEqual(steamFr), "Cofnięcie usuwa INI, którego wcześniej nie było");
            File.WriteAllBytes(ini, userIni);
            install.Install(meta, package);
            File.WriteAllBytes(fr, steamFr);
            Check(!install.Intact(install.State()!), "Weryfikacja Steam (oryginalny PAK) wykrywana jako brak spolszczenia");
            install.Install(meta, package);
            Check(File.ReadAllBytes(fr).SequenceEqual(Pak(1)) && install.Intact(install.State()!), "Naprawa po weryfikacji Steam");
            File.WriteAllBytes(fr, steamFr);
            install.Restore();
            Check(File.ReadAllBytes(fr).SequenceEqual(steamFr) && File.ReadAllBytes(ini).SequenceEqual(userIni) && install.State() is null, "Cofnięcie po weryfikacji Steam zostawia oryginał i przywraca INI");
            install.Install(meta, package);
            File.WriteAllText(ini, "user modification");
            await Reject(() => { install.Restore(); return Task.CompletedTask; }, "Przywracanie chroni późniejsze zmiany INI");
            Check(File.ReadAllText(ini) == "user modification" && File.ReadAllBytes(fr).SequenceEqual(Pak(1)), "Zmiany użytkownika zachowane");
            File.WriteAllBytes(ini, PolishIni);
            install.Restore();
            bool fault = true;
            var failing = new Installer(root, Path.Combine(scratch, "user-data"), message => { if (fault && message.StartsWith("Zapisuję:")) { fault = false; throw new IOException("injected failure"); } }, () => { });
            await Reject(() => { failing.Install(meta, package); return Task.CompletedTask; }, "Symulowany błąd zapisu");
            Check(!failing.HasPending && File.ReadAllBytes(fr).SequenceEqual(steamFr) && File.ReadAllBytes(ini).SequenceEqual(userIni), "Automatyczne cofnięcie nieudanej instalacji");
            var running = new Installer(root, Path.Combine(scratch, "user-data"), _ => { }, () => throw new InvalidOperationException("game running"));
            await Reject(() => { running.Install(meta, package); return Task.CompletedTask; }, "Uruchomiona gra blokuje zmiany");
            install.Install(meta, package);
            var newerZip = PackageZip("25926599", Pak(2), steamFr);
            var newer = Meta(newerZip) with { Version = "09.11.2026" };
            install.Install(newer, Files(newerZip));
            Check(install.State()!.Version == "09.11.2026" && File.ReadAllBytes(fr).SequenceEqual(Pak(2)), "Instalacja nowszej daty przez granicę miesiąca");
            await Reject(() => { install.Install(meta, package); return Task.CompletedTask; }, "Starsze wydanie nie nadpisuje nowszego");
            install.Restore();
            Check(File.ReadAllBytes(fr).SequenceEqual(steamFr) && File.ReadAllBytes(ini).SequenceEqual(userIni), "Po aktualizacji wydania cofnięcie daje oryginał sprzed pierwszej instalacji");

            File.WriteAllBytes(fr, Pak(1));
            var stateFolder = Path.GetDirectoryName(install.StatePath)!;
            var backupRel = "history/interrupted-before.bin";
            Installer.AtomicWrite(Path.Combine(stateFolder, backupRel), steamFr);
            var interrupted = new Journal("25926599", null, [new(GameInstallation.FrPakPath, Sha(steamFr), Sha(Pak(1)), backupRel)]);
            Installer.AtomicWrite(Path.Combine(stateFolder, "pending.json"), JsonSerializer.SerializeToUtf8Bytes(interrupted, UpdateClient.Json));
            Check(install.HasPending, "Wykrywanie operacji przerwanej wyłączeniem programu");
            install.Restore();
            Check(!install.HasPending && File.ReadAllBytes(fr).SequenceEqual(steamFr), "Cofnięcie przerwanej operacji po ponownym uruchomieniu");

            install.Install(meta, package);
            WriteBuild("26000000");
            var patchZip = PackageZip("26000000", Pak(3), Pak(5));
            var patchMeta = Meta(patchZip, "26000000") with { Version = "16.10.2026" };
            await Reject(() => { install.Install(patchMeta, Files(patchZip)); return Task.CompletedTask; }, "Kopia oryginału z innego buildu blokuje instalację po patchu");
            File.WriteAllBytes(fr, Pak(5));
            install.Install(patchMeta, Files(patchZip));
            Check(File.ReadAllBytes(fr).SequenceEqual(Pak(3)) && install.State()!.Files.Single(f => f.Path == GameInstallation.FrPakPath).OriginalSha256 == Sha(Pak(5)), "Po patchu Steam kopia nowego oryginału PAK fr-FR");
            install.Restore();
            Check(File.ReadAllBytes(fr).SequenceEqual(Pak(5)) && File.ReadAllBytes(ini).SequenceEqual(userIni), "Po patchu przywracamy nowy oryginał, nie stary");
            WriteBuild("25926599");
            File.WriteAllBytes(fr, steamFr);

            File.WriteAllBytes(fr, Pak(1));
            File.WriteAllBytes(ini, PolishIni);
            var manualInstaller = new Installer(root, Path.Combine(scratch, "manual-user-data"), _ => { }, () => { });
            manualInstaller.Install(meta, package);
            Check(manualInstaller.State() is null && !manualInstaller.HasPending && Package.IsInstalled(GameInstallation.Read(root), package), "Ręczna zgodna instalacja rozpoznana bez ponownego zapisu");
            Check(manualInstaller.Restore(package) && !File.Exists(ini) && File.ReadAllBytes(fr).SequenceEqual(Pak(1)), "Ręczna instalacja: usunięcie INI, PAK fr-FR tylko przez weryfikację Steam");
            File.WriteAllBytes(fr, steamFr);
            File.WriteAllBytes(ini, userIni);

            var legacyData = Path.Combine(scratch, "legacy-data");
            var legacy = new Installer(root, legacyData, _ => { }, () => { });
            var englishBackup = GameInstallation.PakPath + ".aion2pl-25926599-" + Guid.NewGuid().ToString("N") + ".backup";
            File.Move(en, Path.Combine(root, englishBackup));
            File.WriteAllBytes(dat, Dat(1));
            Installer.AtomicWrite(legacy.StatePath, JsonSerializer.SerializeToUtf8Bytes(new InstallState(Path.GetFullPath(root), "01.11.2026", "25926599", new string('a', 64),
                [new(GameInstallation.DatPath, Sha(Dat(1)), null, null)], new PakBackup(englishBackup, Sha(Encoding.UTF8.GetBytes("english pak")), "25926599")), UpdateClient.Json));
            var legacyZip = PackageZip("25926599", Pak(1), steamFr);
            legacy.Install(Meta(legacyZip) with { Version = "05.11.2026" }, Files(legacyZip));
            Check(!File.Exists(dat) && File.ReadAllText(en) == "english pak" && File.Exists(Path.Combine(root, englishBackup)) && File.ReadAllBytes(fr).SequenceEqual(Pak(1)) && legacy.State()!.Pak is null,
                "Migracja ze starej metody: usunięty DAT, przywrócony angielski PAK (kopia zostaje), podmieniony PAK fr-FR");
            legacy.Restore();
            Check(File.ReadAllBytes(fr).SequenceEqual(steamFr) && File.ReadAllBytes(ini).SequenceEqual(userIni) && File.ReadAllText(en) == "english pak" && !File.Exists(dat), "Cofnięcie po migracji nie przywraca starej metody");

            File.Delete(en);
            File.WriteAllBytes(en + ".backup", Pak());
            var fallback = new Installer(root, Path.Combine(scratch, "manual-backup-data"), _ => { }, () => { });
            fallback.Install(meta, package);
            Check(File.ReadAllBytes(fr).SequenceEqual(Pak(1)) && !File.Exists(en) && fallback.NeedsSteamVerification(), "Brak angielskiego PAK ze starej metody nie blokuje instalacji, ale cofnięcie prosi o kopię");
            var manualName = GameInstallation.PakPath + ".backup";
            Check(fallback.ManualBackups().Contains(manualName), "Wykrywanie istniejącej ręcznej kopii .pak.backup");
            fallback.Restore(null, manualName);
            Check(File.ReadAllBytes(en).SequenceEqual(Pak()) && File.Exists(en + ".backup") && File.ReadAllBytes(fr).SequenceEqual(steamFr), "Przywracanie wskazanej ręcznej kopii angielskiego PAK i oryginału fr-FR");

            File.WriteAllBytes(dat, Dat(1));
            var keeper = new Installer(root, Path.Combine(scratch, "keep-dat-data"), _ => { }, () => { });
            var left = keeper.LegacyLeftovers();
            Check(left.LooseDat && !left.EnglishPakMissing && left.Any, "Wykrywanie luźnego DAT starej metody");
            keeper.Install(meta, package);
            Check(File.Exists(dat) && File.ReadAllBytes(fr).SequenceEqual(Pak(1)), "Bez zgody użytkownika luźny DAT zostaje");
            keeper.Restore();
            var cleaner = new Installer(root, Path.Combine(scratch, "cleanup-data"), _ => { }, () => { });
            cleaner.Install(meta, package, cleanupLegacy: true);
            Check(!File.Exists(dat) && File.ReadAllBytes(fr).SequenceEqual(Pak(1)) && !cleaner.LegacyLeftovers().Any, "Instalacja ze sprzątaniem usuwa luźny DAT starej metody");
            cleaner.Restore();
            Check(!File.Exists(dat) && File.ReadAllBytes(fr).SequenceEqual(steamFr), "Cofnięcie nie przywraca luźnego DAT starej metody");
            File.Delete(en);
            Check(cleaner.LegacyLeftovers() is { EnglishPakMissing: true, EnglishPakRestorable: false }, "Wyłączony angielski PAK bez kopii wymaga weryfikacji Steam");
            File.WriteAllBytes(en, Pak());
            using (var cacheHandler =new Fake(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) }))
            using (var cacheClient = new UpdateClient(cacheHandler))
            {
                var cache = Path.Combine(scratch, "package-cache");
                var cachedFiles = await cacheClient.VerifiedFiles(meta, cache, _ => { }, null, CancellationToken.None);
                await cacheClient.VerifiedFiles(meta, cache, _ => { }, null, CancellationToken.None);
                Check(cacheHandler.Calls == 1 && cachedFiles.Count == 2, "Porównanie ręcznego spolszczenia używa sprawdzonej kopii bez ponownego pobierania");
                File.WriteAllText(Path.Combine(cache, meta.Sha256.ToLowerInvariant() + ".zip"), "bad cache");
                await cacheClient.VerifiedFiles(meta, cache, _ => { }, null, CancellationToken.None);
                Check(cacheHandler.Calls == 2, "Uszkodzona kopia lokalna jest ponownie pobierana i sprawdzana");
            }
            File.WriteAllText(report, JsonSerializer.Serialize(checks, UpdateClient.Json));
        }
        finally
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(report))!;
            if (!Path.GetFullPath(scratch).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test directory");
            Directory.Delete(scratch, true);
        }
    }
}
