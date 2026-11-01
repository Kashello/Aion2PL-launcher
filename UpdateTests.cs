using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
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
    static byte[] Pak()
    {
        var bytes = new byte[128];
        BitConverter.GetBytes(0x5A6F12E1U).CopyTo(bytes, 64);
        BitConverter.GetBytes(11).CopyTo(bytes, 68);
        BitConverter.GetBytes(0L).CopyTo(bytes, 72);
        BitConverter.GetBytes(32L).CopyTo(bytes, 80);
        return bytes;
    }
    static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var output = new MemoryStream();
        using (var z = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var s = z.CreateEntry(file.Name).Open(); s.Write(file.Data); }
        return output.ToArray();
    }
    static Release Meta(byte[] bytes) => new("aion2pl", "15.10.2026", "25926599", "https://kashello.pl/api/downloads/987/file", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);

    internal static async Task Run(string report)
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
            var priorConfig = System.Text.Encoding.UTF8.GetBytes("[Graphics]\r\nQuality=High\r\n");
            var mergedConfig = FontCulture.Add(priorConfig);
            Check(FontCulture.Enabled(mergedConfig) && FontCulture.Remove(mergedConfig)!.SequenceEqual(priorConfig), "Mapowanie fontu zachowuje ustawienia i daje się usunąć");
            var userEdit = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(mergedConfig) + "[User]\r\nValue=1\r\n");
            Check(System.Text.Encoding.UTF8.GetString(FontCulture.Remove(userEdit)!).Contains("Value=1"), "Cofanie mapowania zachowuje późniejsze ustawienia użytkownika");
            await Reject(() => { FontCulture.Add(System.Text.Encoding.UTF8.GetBytes("[Internationalization]\n+CultureMappings=\"en;de\"\n")); return Task.CompletedTask; }, "Sprzeczne mapowanie chroni ustawienia użytkownika");
            Check(FontCulture.Add(mergedConfig).SequenceEqual(mergedConfig), "Ponowna instalacja nie powiela mapowań");
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
            var zipBytes = Zip((GameInstallation.DatPath, Dat(1)), ("ZMIANY.txt", [65]), ("Aion2/Content/L10N/Text/en-US/additional.dat", [66]));
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
                Check(files.Count == 2 && !files.ContainsKey("ZMIANY.txt"), "Poprawny ZIP i dodatkowy plik lokalizacji bez stałej listy");
                File.Delete(download);
                await Reject(() => client.Download(meta with { Sha256 = new string('0', 64) }, download, _ => { }, null, CancellationToken.None), "Zły SHA-256 odrzuca paczkę");
                Check(!File.Exists(download), "Błędne pobranie usunięte");
                await Reject(() => client.Download(meta with { SizeBytes = meta.SizeBytes + 1 }, download, _ => { }, null, CancellationToken.None), "Zły rozmiar odrzuca paczkę");
                File.WriteAllText(download, "existing");
                await Reject(() => client.Download(meta, download, _ => { }, null, CancellationToken.None), "Brak nadpisywania istniejącego pliku pobrania");
                Check(File.ReadAllText(download) == "existing", "Błąd pobierania nie usuwa cudzego pliku");
            }
            var badZip = Path.Combine(scratch, "bad.zip");
            File.WriteAllText(badZip, "not a zip");
            await Reject(() => { Package.Read(badZip); return Task.CompletedTask; }, "Uszkodzony ZIP");
            var crcZip = Zip((GameInstallation.DatPath, Dat(1)));
            for (int i = 0; i < crcZip.Length - 46; i++)
                if (BitConverter.ToUInt32(crcZip, i) == 0x02014B50) { crcZip[i + 16] ^= 1; break; }
            File.WriteAllBytes(badZip, crcZip);
            await Reject(() => { Package.Read(badZip); return Task.CompletedTask; }, "Błędne CRC pliku wewnątrz ZIP");
            foreach (var name in new[] { "../outside.dat", "C:/outside.dat", "/outside.dat", "Aion2/Content/L10N/../outside.dat", "Aion2/Content/L10N/test.dat:evil", "Aion2/Content/L10N/CON.dat" })
            {
                File.WriteAllBytes(badZip, Zip((GameInstallation.DatPath, Dat(1)), (name, [1])));
                await Reject(() => { Package.Read(badZip); return Task.CompletedTask; }, "ZIP odrzucony: " + name);
            }
            File.WriteAllBytes(badZip, Zip((GameInstallation.DatPath, Dat(1)), ("Aion2/Binaries/evil.dll", [1])));
            await Reject(() => { Package.Read(badZip); return Task.CompletedTask; }, "Ochrona plików EXE/DLL i katalogu gry");

            var root = Path.Combine(scratch, "Steam/steamapps/common/AION2");
            Directory.CreateDirectory(Path.Combine(root, "Aion2/Content/Paks"));
            var manifest = Path.GetFullPath(Path.Combine(root, "../../appmanifest_3393110.acf"));
            void WriteBuild(string build) => File.WriteAllText(manifest, $"\"appid\" \"3393110\" \"installdir\" \"AION2\" \"buildid\" \"{build}\" \"TargetBuildID\" \"{build}\" \"StateFlags\" \"4\"");
            WriteBuild("25926599");
            var dat = Path.Combine(root, GameInstallation.DatPath);
            var pak = Path.Combine(root, GameInstallation.PakPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dat)!);
            Directory.CreateDirectory(Path.GetDirectoryName(pak)!);
            File.WriteAllBytes(dat, Dat(1));
            var config = Path.Combine(root, FontCulture.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllBytes(config, FontCulture.Add(null));
            var manualInstaller = new Installer(root, Path.Combine(scratch, "manual-user-data"), _ => { }, () => { });
            manualInstaller.Install(meta, new Dictionary<string, byte[]> { [GameInstallation.DatPath] = Dat(1) });
            Check(manualInstaller.State() is null && !manualInstaller.HasPending && File.ReadAllBytes(dat).SequenceEqual(Dat(1)), "Ręczna zgodna instalacja jest rozpoznana bez ponownego instalowania i tworzenia kopii");
            File.WriteAllText(config, "[Graphics]\nQuality=High\n");
            Check(manualInstaller.RepairFont() && FontCulture.Enabled(File.ReadAllBytes(config)) && File.ReadAllBytes(dat).SequenceEqual(Dat(1)), "Osobna naprawa znaków bez pobierania i zmiany ręcznego DAT");
            Check(!manualInstaller.RepairFont(), "Ponowna naprawa rozpoznaje obecne ustawienie");
            File.AppendAllText(config, "[User]\nValue=1\n");
            File.WriteAllText(pak + ".backup", "unverified manual backup");
            Check(manualInstaller.NeedsSteamVerification(), "Ręczna kopia bez potwierdzenia buildu prowadzi do weryfikacji Steam");
            File.WriteAllText(pak, "steam verified original");
            Check(!manualInstaller.NeedsSteamVerification(), "Aktywny oryginalny PAK pozwala dokończyć przywracanie");
            manualInstaller.Restore(new Dictionary<string, byte[]> { [GameInstallation.DatPath] = Dat(1) });
            Check(!File.Exists(dat) && File.ReadAllText(pak) == "steam verified original" && File.ReadAllText(pak + ".backup") == "unverified manual backup", "Cofnięcie ręcznej instalacji zachowuje oryginał Steam i nieużywaną ręczną kopię");
            Check(File.ReadAllText(config).Contains("Value=1") && !FontCulture.Enabled(File.ReadAllBytes(config)), "Cofnięcie ręcznej instalacji usuwa poprawkę znaków i zachowuje ustawienia użytkownika");
            File.WriteAllText(dat, "original dat");
            File.WriteAllText(pak, "original pak");
            File.Delete(config);
            var filesToInstall = new Dictionary<string, byte[]> { [GameInstallation.DatPath] = Dat(1) };
            Check(!Package.FilesMatch(GameInstallation.Read(root), filesToInstall), "Inny DAT nie jest oznaczany jako aktualne spolszczenie");
            File.WriteAllBytes(dat, Dat(1));
            Check(Package.FilesMatch(GameInstallation.Read(root), filesToInstall) && !Package.IsInstalled(GameInstallation.Read(root), filesToInstall), "Zgodny DAT przy aktywnym angielskim PAK wymaga naprawy");
            File.WriteAllText(dat, "original dat");
            var install = new Installer(root, Path.Combine(scratch, "user-data"), _ => { }, () => { });
            await Reject(() => { install.Install(meta with { SteamBuildId = "999" }, filesToInstall); return Task.CompletedTask; }, "Inny build blokuje instalację");
            Check(File.ReadAllText(dat) == "original dat" && File.ReadAllText(pak) == "original pak", "Niezgodny build nie zmienia gry");
            install.Install(meta, filesToInstall);
            Check(!File.Exists(pak) && File.ReadAllBytes(dat).SequenceEqual(Dat(1)) && install.Intact(install.State()!), "Instalacja z kopią i wyłączeniem angielskiego PAK");
            Check(FontCulture.Enabled(File.ReadAllBytes(config)), "Instalacja dodaje mapowanie dla polskich znaków");
            var originalBackup = install.State()!.Pak!;
            Check(File.ReadAllText(Path.Combine(root, originalBackup.Path)) == "original pak", "Kopia PAK zachowuje zawartość");
            File.WriteAllText(pak, "steam restored pak");
            install.Install(meta, filesToInstall);
            Check(!File.Exists(pak) && install.State()!.Pak!.Path != originalBackup.Path, "Naprawa po odtworzeniu PAK przez Steam zachowuje nową kopię");
            install.Restore();
            Check(File.ReadAllText(dat) == "original dat" && File.ReadAllText(pak) == "steam restored pak" && install.State() is null, "Przywracanie aktualnego PAK i poprzedniego DAT");
            Check(!File.Exists(config), "Przywracanie usuwa wyłącznie dodaną konfigurację");
            install.Install(meta, filesToInstall);
            File.WriteAllText(pak, "new patch pak");
            WriteBuild("26000000");
            install.Restore();
            Check(File.ReadAllText(pak) == "new patch pak", "Przywracanie nigdy nie zastępuje aktywnego PAK Steam starą kopią");
            WriteBuild("25926599");
            bool fault = true;
            var failing = new Installer(root, Path.Combine(scratch, "user-data"), message => { if (fault && message.StartsWith("Zapisuję:")) { fault = false; throw new IOException("injected failure"); } }, () => { });
            await Reject(() => { failing.Install(meta, filesToInstall); return Task.CompletedTask; }, "Symulowany błąd zapisu");
            Check(!failing.HasPending && File.ReadAllText(dat) == "original dat" && File.ReadAllText(pak) == "new patch pak", "Automatyczne cofnięcie nieudanej instalacji");
            var running = new Installer(root, Path.Combine(scratch, "user-data"), _ => { }, () => throw new InvalidOperationException("game running"));
            await Reject(() => { running.Install(meta, filesToInstall); return Task.CompletedTask; }, "Uruchomiona gra blokuje zmiany");
            install.Install(meta, filesToInstall);
            File.WriteAllText(dat, "user modification");
            await Reject(() => { install.Restore(); return Task.CompletedTask; }, "Przywracanie chroni późniejsze zmiany użytkownika");
            Check(File.ReadAllText(dat) == "user modification", "Zmiany użytkownika zachowane");
            File.WriteAllBytes(dat, Dat(1));
            install.Restore();
            install.Install(meta, filesToInstall);
            File.WriteAllText(pak, "steam new active");
            var newer = meta with { Version = "09.11.2026" };
            install.Install(newer, new Dictionary<string, byte[]> { [GameInstallation.DatPath] = Dat(2) });
            Check(install.State()!.Version == "09.11.2026" && File.ReadAllBytes(dat).SequenceEqual(Dat(2)), "Instalacja nowszej daty przez granicę miesiąca");
            await Reject(() => { install.Install(meta, filesToInstall); return Task.CompletedTask; }, "Starsze wydanie nie nadpisuje nowszego");
            install.Restore();
            File.WriteAllText(dat, "before interrupted operation");
            var stateFolder = Path.GetDirectoryName(install.StatePath)!;
            var backupRel = "history/interrupted-before.bin";
            var backupFile = Path.Combine(stateFolder, backupRel);
            Installer.AtomicWrite(backupFile, File.ReadAllBytes(dat));
            var beforeHash = Installer.Hash(dat);
            File.WriteAllBytes(dat, Dat(9));
            var interrupted = new Journal("25926599", null,
                [new(GameInstallation.DatPath, beforeHash, Installer.Hash(dat), backupRel)]);
            Installer.AtomicWrite(Path.Combine(stateFolder, "pending.json"), JsonSerializer.SerializeToUtf8Bytes(interrupted, UpdateClient.Json));
            Check(install.HasPending, "Wykrywanie operacji przerwanej wyłączeniem programu");
            install.Restore();
            Check(!install.HasPending && File.ReadAllText(dat) == "before interrupted operation", "Cofnięcie przerwanej operacji po ponownym uruchomieniu");
            install.Install(meta, filesToInstall);
            var originalState = install.State()!;
            WriteBuild("26000000");
            await Reject(() => { install.Restore(); return Task.CompletedTask; }, "Bez aktywnego PAK nie przywracamy kopii z innego buildu");
            Check(File.ReadAllBytes(dat).SequenceEqual(Dat(1)) && install.State()?.PackageSha256 == originalState.PackageSha256, "Stan instalacji pozostaje dostępny po zablokowaniu cofnięcia");
            WriteBuild("25926599");
            File.WriteAllText(pak, "steam original after verification");
            var legacy = install.State()!;
            var legacyFile = legacy.Files.Single();
            Installer.AtomicWrite(install.StatePath, JsonSerializer.SerializeToUtf8Bytes(legacy with { Pak = null,
                Files = [legacyFile with { OriginalBackup = null, OriginalSha256 = legacyFile.Sha256 }] }, UpdateClient.Json));
            install.Restore();
            Check(!File.Exists(dat) && File.ReadAllText(pak) == "steam original after verification", "Stan ze starego launchera nie przywraca spolszczenia jako oryginalnego DAT");
            File.WriteAllBytes(dat, Dat(1));
            File.Delete(pak);
            File.WriteAllBytes(pak + ".backup", Pak());
            var fallback = new Installer(root, Path.Combine(scratch, "manual-backup-data"), _ => { }, () => { });
            var manualName = GameInstallation.PakPath + ".backup";
            Check(fallback.ManualBackups().Contains(manualName), "Wykrywanie istniejącej ręcznej kopii .pak.backup");
            fallback.Restore(filesToInstall, manualName);
            Check(File.ReadAllBytes(pak).SequenceEqual(Pak()) && File.ReadAllBytes(pak + ".backup").SequenceEqual(Pak()) && !File.Exists(dat), "Przywracanie wskazanej ręcznej kopii bez jej usuwania");
            File.WriteAllBytes(dat, Dat(1));
            File.WriteAllText(pak, "steam current pak");
            fallback.Restore(filesToInstall, manualName);
            Check(File.ReadAllText(pak) == "steam current pak", "Aktywny nowy PAK Steam ma pierwszeństwo przed ręcznym backupem");
            File.WriteAllBytes(dat, Dat(1));
            File.Delete(pak);
            File.WriteAllText(pak + ".backup", "invalid backup");
            await Reject(() => { fallback.Restore(filesToInstall, manualName); return Task.CompletedTask; }, "Niepoprawna ręczna kopia PAK jest odrzucana przed zmianami");
            Check(!File.Exists(pak) && File.ReadAllBytes(dat).SequenceEqual(Dat(1)), "Błędny backup nie zmienia instalacji");
            File.WriteAllText(pak, "steam original before patch");
            var patches = new Installer(root, Path.Combine(scratch, "patch-test-data"), _ => { }, () => { });
            patches.Install(meta, filesToInstall);
            var beforePatchBackup = patches.State()!.Pak!;
            File.WriteAllText(pak, "steam original after new patch");
            WriteBuild("26000000");
            var patchMeta = Meta(Zip((GameInstallation.DatPath, Dat(2)))) with { Version = "16.10.2026", SteamBuildId = "26000000" };
            patches.Install(patchMeta, new Dictionary<string, byte[]> { [GameInstallation.DatPath] = Dat(2) });
            var afterPatchBackup = patches.State()!.Pak!;
            Check(afterPatchBackup.Build == "26000000" && afterPatchBackup.Path != beforePatchBackup.Path &&
                File.ReadAllText(Path.Combine(root, beforePatchBackup.Path)) == "steam original before patch" &&
                File.ReadAllText(Path.Combine(root, afterPatchBackup.Path)) == "steam original after new patch", "Nowy patch tworzy nową kopię PAK dla nowego buildu i zachowuje starą");
            patches.Restore();
            Check(File.ReadAllText(pak) == "steam original after new patch" && !File.Exists(dat), "Po patchu przywracamy nowy oryginał zamiast starego backupu i ręcznego DAT");
            using (var cacheHandler = new Fake(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) }))
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
