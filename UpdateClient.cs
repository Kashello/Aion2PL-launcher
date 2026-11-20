using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aion2PL;

internal record Release(
    string Product, string Version,
    [property: JsonPropertyName("steam_buildid")] string SteamBuildId,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    string Sha256,
    [property: JsonPropertyName("size_bytes")] long SizeBytes)
{
    internal static DateOnly Date(string value) => DateOnly.ParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture);
    internal bool IsNewerThan(string installed) => Date(Version) > Date(installed);
    internal void Validate()
    {
        if (Product != "aion2pl" || !DateOnly.TryParseExact(Version, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            SteamBuildId is null || !Regex.IsMatch(SteamBuildId, @"^\d+$") || Sha256 is null || !Regex.IsMatch(Sha256, @"\A[0-9a-fA-F]{64}\z") ||
            SizeBytes <= 0 || SizeBytes > UpdateClient.MaxDownload)
            throw new InvalidDataException("Serwer zwrócił nieprawidłowe informacje o wydaniu.");
        UpdateClient.Https(DownloadUrl);
    }
    internal void RequireBuild(GameInstallation game)
    {
        if (game.Updating) throw new InvalidOperationException("Poczekaj na zakończenie aktualizacji Steam.");
        if (game.Build != SteamBuildId) throw new InvalidOperationException($"Paczka pasuje do buildu {SteamBuildId}, a gra ma build {game.Build}. Poczekaj na zgodne wydanie.");
    }
}

internal record LatestResult(Release? Release, string Message);

internal sealed class UpdateClient : IDisposable
{
    internal const long MaxDownload = 256L * 1024 * 1024;
    internal static readonly Uri Endpoint = new("https://kashello.pl/api/aion2pl/latest");
    internal static readonly Uri LauncherEndpoint = new("https://api.github.com/repos/Kashello/Aion2PL-launcher/releases/latest");
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    readonly HttpClient http;
    LatestResult? cached;
    DateTime checkedAt;
    internal UpdateClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false });
        http.Timeout = TimeSpan.FromSeconds(90);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Aion2PL-Launcher/0.7.0");
    }
    internal static Uri Https(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Adres pobierania musi używać HTTPS.");
        return uri;
    }
    async Task<HttpResponseMessage> Get(Uri uri, CancellationToken ct, bool launcherDownload = false)
    {
        for (int redirects = 0; redirects < 4; redirects++)
        {
            Https(uri.AbsoluteUri);
            if (launcherDownload && !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Nieznany serwer pobierania launchera.");
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new HttpRequestException("Zbyt wiele przekierowań serwera.");
    }
    internal async Task<LatestResult> Latest(Action<string> log, CancellationToken ct)
    {
        if (DateTime.UtcNow - checkedAt < TimeSpan.FromHours(3))
            return cached ?? throw new HttpRequestException("Sprawdzenie wersji nie powiodło się. Spróbuj później lub uruchom aplikację ponownie.");
        checkedAt = DateTime.UtcNow;
        cached = null;
        log("Sprawdzam najnowsze wydanie na kashello.pl...");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Get(Endpoint, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NotFound)
            throw new HttpRequestException($"Serwer jest chwilowo niedostępny (HTTP {(int)response.StatusCode}). Spróbuj później.");
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidDataException("Serwer zwrócił odpowiedź w nieobsługiwanym formacie.");
        var content = await ReadLimited(response.Content, 64 * 1024, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using var document = JsonDocument.Parse(content);
            var message = document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()! : "Nie opublikowano jeszcze żadnej wersji Aion2PL.";
            log(message);
            return cached = new(null, message);
        }
        var release = JsonSerializer.Deserialize<Release>(content, Json) ?? throw new InvalidDataException("Brak informacji o wydaniu.");
        release.Validate();
        log($"Dostępne wydanie {release.Version}, Steam build {release.SteamBuildId}.");
        return cached = new(release, "Wydanie dostępne.");
    }
    static async Task<byte[]> ReadLimited(HttpContent content, int limit, CancellationToken ct)
    {
        using var result = new MemoryStream();
        using var input = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (result.Length + count > limit) throw new InvalidDataException("Odpowiedź serwera jest zbyt duża.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
    internal async Task Download(Release release, string destination, Action<string> log, IProgress<int>? progress, CancellationToken ct, bool launcherDownload = false)
    {
        release.Validate();
        bool created = false;
        try
        {
            log("Pobieram paczkę spolszczenia...");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var response = await Get(Https(release.DownloadUrl), timeout.Token, launcherDownload);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.SizeBytes)
                throw new InvalidDataException("Rozmiar pobieranej paczki nie zgadza się z wydaniem.");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                created = true;
                var buffer = new byte[64 * 1024];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += count;
                    if (total > release.SizeBytes) throw new InvalidDataException("Pobrano więcej danych niż oczekiwano.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    sha.AppendData(buffer, 0, count);
                    progress?.Report((int)(total * 100 / release.SizeBytes));
                }
                log("Sprawdzam rozmiar i sumę SHA-256...");
                if (total != release.SizeBytes || !Convert.ToHexString(sha.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Paczka nie przeszła kontroli poprawności. Pobierz ją ponownie później.");
            }
            log("Paczka pobrana i sprawdzona.");
        }
        catch { if (created) File.Delete(destination); throw; }
    }
    internal async Task<PackageFiles> VerifiedFiles(Release release, string cacheFolder, Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        release.Validate();
        Directory.CreateDirectory(cacheFolder);
        var zip = Path.Combine(cacheFolder, release.Sha256.ToLowerInvariant() + ".zip");
        bool cached = await Task.Run(() => File.Exists(zip) && new FileInfo(zip).Length == release.SizeBytes &&
            string.Equals(Installer.Hash(zip), release.Sha256, StringComparison.OrdinalIgnoreCase), ct);
        if (!cached)
        {
            var temporary = Path.Combine(cacheFolder, Guid.NewGuid().ToString("N") + ".zip");
            try { await Download(release, temporary, log, progress, ct); File.Move(temporary, zip, true); }
            finally { File.Delete(temporary); }
        }
        else log("Korzystam ze sprawdzonej kopii paczki na tym komputerze.");
        try { return await Task.Run(() => Package.Read(zip), ct); }
        catch (LauncherTooOldException) { throw; }
        catch (InvalidDataException) { File.Delete(zip); throw; }
    }
    internal Task DownloadLauncher(LauncherRelease release, string destination, Action<string> log, CancellationToken ct)
    {
        release.Validate();
        var download = new Release("aion2pl", "01.01.2000", "1", release.DownloadUrl, release.Sha256, release.SizeBytes);
        return Download(download, destination, message => log(message.Replace("paczkę spolszczenia", "nową wersję launchera").Replace("Paczka pobrana", "Nowa wersja launchera pobrana")), null, ct, true);
    }
    internal async Task<LauncherRelease?> LatestLauncher(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Get(LauncherEndpoint, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await ReadLimited(response.Content, 256 * 1024, timeout.Token));
        var obj = doc.RootElement;
        if (!obj.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !obj.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Nieprawidłowe wydanie launchera na GitHubie.");
        var version = tag.GetString()!.TrimStart('v');
        var candidates = assets.EnumerateArray().Where(a => a.TryGetProperty("name", out var name) && name.GetString() == "Aion2PL.exe").ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("Wydanie GitHub musi zawierać jeden plik Aion2PL.exe.");
        var asset = candidates[0];
        var digest = asset.GetProperty("digest").GetString();
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub nie podał sumy SHA-256 launchera.");
        var result = new LauncherRelease(version, asset.GetProperty("browser_download_url").GetString()!, digest[7..], asset.GetProperty("size").GetInt64());
        result.Validate();
        return result;
    }
    public void Dispose() => http.Dispose();
}

/// <summary>Game files from a release ZIP plus the original Steam hashes they replace (Aion2PL-instalacja.json).</summary>
internal sealed class PackageFiles : Dictionary<string, byte[]>
{
    internal PackageFiles() : base(StringComparer.OrdinalIgnoreCase) { }
    internal string Build { get; set; } = "";
    internal string? Version { get; set; }
    internal Dictionary<string, string> Originals { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The package uses a newer format than this launcher understands; the launcher itself must be updated.</summary>
internal sealed class LauncherTooOldException(string message) : IOException(message);

internal static class Package
{
    internal const string ManifestName = "Aion2PL-instalacja.json";
    internal const int SupportedFormat = 3;
    static readonly string[] ResourcePaths = new[] { "en", "en-US", "fr", "fr-FR" }
        .Select(code => $"Engine/Content/Localization/Engine/Platforms/Windows/{code}/Engine.locres").ToArray();
    internal static bool Allowed(string name) =>
        name.Equals(GameInstallation.FrPakPath, StringComparison.OrdinalIgnoreCase) || name.Equals(GameInstallation.ConfigPath, StringComparison.OrdinalIgnoreCase)
        || ResourcePaths.Contains(name, StringComparer.OrdinalIgnoreCase);
    static bool LayoutMatches(int format, Dictionary<string, byte[]> files) =>
        files.ContainsKey(GameInstallation.ConfigPath) && (format == 2
            ? files.Count == 2 && files.ContainsKey(GameInstallation.FrPakPath)
            : format == 3 && files.Count == ResourcePaths.Length + 1 && ResourcePaths.All(files.ContainsKey));
    internal static bool FilesMatch(GameInstallation game, Dictionary<string, byte[]> files)
    {
        if (game.Updating || !(LayoutMatches(2, files) || LayoutMatches(3, files))) return false;
        foreach (var (name, bytes) in files)
        {
            if (!Allowed(SafeName(name))) throw new InvalidDataException("Nieprawidłowa ścieżka lokalizacji.");
            var sha = Installer.Hash(Path.Combine(game.Root, name));
            if (!string.Equals(sha, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    internal static bool IsInstalled(GameInstallation game, Dictionary<string, byte[]> files) => FilesMatch(game, files);
    internal static string SafeName(string name)
    {
        name = name.Replace('\\', '/');
        var trimmed = name.TrimEnd('/');
        if (name.StartsWith('/') || trimmed.Length == 0 || trimmed.Split('/').Any(part =>
            part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("ZIP zawiera niebezpieczną ścieżkę.");
        return trimmed;
    }
    internal static PackageFiles Read(string file)
    {
        using var archive = ZipFile.OpenRead(file);
        if (archive.Entries.Count > 2000) throw new InvalidDataException("ZIP zawiera zbyt wiele plików.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new PackageFiles();
        byte[]? manifest = null;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = SafeName(entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("ZIP zawiera powtórzone nazwy lub dowiązania.");
            total += entry.Length;
            if (entry.Length > 128L * 1024 * 1024 || total > 256L * 1024 * 1024)
                throw new InvalidDataException("Rozpakowana paczka jest zbyt duża.");
            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            uint crc = uint.MaxValue;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (output.Length + count > entry.Length) throw new InvalidDataException("Nieprawidłowa długość pliku ZIP.");
                for (int i = 0; i < count; i++)
                {
                    crc ^= buffer[i];
                    for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xEDB88320U);
                }
                output.Write(buffer, 0, count);
            }
            if (output.Length != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException("Uszkodzony plik w ZIP.");
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) manifest = output.ToArray();
            else if (name.StartsWith("Aion2/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase))
            {
                if (name.StartsWith("Aion2/Content/L10N/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Paczka w starym formacie (luźny DAT). Po patchu 26429009 gra pokazuje z nią „???” — poczekaj na nowe wydanie.");
                if (!Allowed(name)) throw new InvalidDataException("Paczka próbuje zmienić pliki poza lokalizacją gry.");
                files.Add(name, output.ToArray());
            }
        }
        if (manifest is null) throw new InvalidDataException("Brak opisu instalacji w ZIP.");
        try
        {
            using var document = JsonDocument.Parse(manifest);
            var rootElement = document.RootElement;
            var format = rootElement.GetProperty("format").GetInt32();
            if (format > SupportedFormat)
                throw new LauncherTooOldException("Ta paczka spolszczenia wymaga nowszej wersji launchera. Zaktualizuj launcher (przycisk „Sprawdź launcher” albo https://github.com/Kashello/Aion2PL-launcher/releases/latest).");
            if (!LayoutMatches(format, files)) throw new InvalidDataException("Zestaw plików nie pasuje do formatu paczki.");
            if (rootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
            {
                files.Version = version.GetString();
                Release.Date(files.Version!);
            }
            files.Build = rootElement.GetProperty("steam_buildid").GetString() ?? "";
            if (!Regex.IsMatch(files.Build, @"^\d+$")) throw new InvalidDataException("Nieprawidłowy build w opisie paczki.");
            var described = rootElement.GetProperty("files");
            if (described.EnumerateObject().Count() != files.Count) throw new InvalidDataException("Opis paczki nie zgadza się z plikami.");
            foreach (var (name, bytes) in files)
            {
                var entry = described.GetProperty(name);
                if (!string.Equals(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Plik w paczce różni się od opisu: " + name);
                if (entry.TryGetProperty("original_sha256", out var original))
                {
                    var value = original.GetString() ?? "";
                    if (!Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z")) throw new InvalidDataException("Nieprawidłowa suma oryginału w opisie paczki.");
                    files.Originals[name] = value.ToUpperInvariant();
                }
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        { throw new InvalidDataException("Uszkodzony opis instalacji w paczce.", e); }
        if (files.ContainsKey(GameInstallation.FrPakPath) && !files.Originals.ContainsKey(GameInstallation.FrPakPath)) throw new InvalidDataException("Opis paczki nie podaje sumy oryginalnego PAK fr-FR.");
        return files;
    }
}
