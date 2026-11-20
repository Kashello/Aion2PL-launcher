using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace Aion2PL;

internal record GameInstallation(string Root, string Build, bool Updating, bool DatPresent, bool EnglishPakPresent)
{
    internal const string DatPath = "Aion2/Content/L10N/Text/en-US/L10NString.dat";
    internal const string PakPath = "Aion2/Content/Paks/L10N/Text/en-US/pakchunk502000-Windows_0_P.pak";
    internal const string FrPakPath = "Aion2/Content/Paks/L10N/Text/fr-FR/pakchunk507000-Windows_0_P.pak";
    internal const string ConfigPath = "Aion2/Config/UserGame.ini";

    internal static string Field(string text, string name) =>
        Regex.Match(text, "\"" + Regex.Escape(name) + "\"\\s+\"([^\"\\r\\n]*)\"").Groups[1].Value;

    internal static GameInstallation Read(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var manifest = Path.GetFullPath(Path.Combine(root, "../../appmanifest_3393110.acf"));
        const string Hint = " Wskaż folder z Steam → AION 2 → Zarządzaj → Przeglądaj pliki lokalne.";
        if (!File.Exists(manifest))
            throw new InvalidDataException($"Folder {root} nie leży w bibliotece Steam z AION 2 (brak steamapps\\appmanifest_3393110.acf). To może być kopia gry albo inna aplikacja, np. AION 2 Playtest." + Hint);
        var text = File.ReadAllText(manifest);
        var build = Field(text, "buildid");
        var installdir = Field(text, "installdir");
        if (Field(text, "appid") != "3393110" || !Regex.IsMatch(build, @"^\d+$"))
            throw new InvalidDataException("Manifest Steam AION 2 w tej bibliotece jest nieprawidłowy." + Hint);
        if (!installdir.Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Steam ma AION 2 w folderze „{installdir}”, a wybrano „{Path.GetFileName(root)}”. To może być kopia gry albo inna aplikacja, np. AION 2 Playtest." + Hint);
        if (!Directory.Exists(Path.Combine(root, "Aion2/Content/Paks")))
            throw new InvalidDataException("W wybranym folderze brakuje Aion2\\Content\\Paks — wskaż główny folder gry." + Hint);
        var target = Field(text, "TargetBuildID");
        bool updating = Field(text, "StateFlags") != "4" || (target.Length > 0 && target != build);
        return new(root, build, updating, File.Exists(Path.Combine(root, DatPath)), File.Exists(Path.Combine(root, PakPath)));
    }

    /// <summary>Game folder of a running ...\Aion2\Binaries\Win64\AION2.exe, or null for any other path.</summary>
    internal static string? RootFromExecutable(string? path)
    {
        if (path is null) return null;
        var win64 = Path.GetDirectoryName(Path.GetFullPath(path));
        var binaries = win64 is null ? null : Path.GetDirectoryName(win64);
        var aion2 = binaries is null ? null : Path.GetDirectoryName(binaries);
        if (aion2 is null || !Path.GetFileName(win64!).Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(binaries!).Equals("Binaries", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(aion2).Equals("Aion2", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetDirectoryName(aion2);
    }

    internal static IEnumerable<string> Libraries(string steamRoot)
    {
        yield return steamRoot;
        var file = Path.Combine(steamRoot, "steamapps/libraryfolders.vdf");
        if (!File.Exists(file)) yield break;
        foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"path\"\\s+\"([^\"\\r\\n]+)\""))
            yield return match.Groups[1].Value.Replace(@"\\", @"\");
    }

    internal static IEnumerable<string> SteamRoots()
    {
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            string? path = null;
            try { path = Registry.GetValue(key, key.StartsWith("HKEY_CURRENT_USER") ? "SteamPath" : "InstallPath", null) as string; }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    }

    internal static GameInstallation? Find()
    {
        foreach (var steam in SteamRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var library in Libraries(steam).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var manifest = Path.Combine(library, "steamapps/appmanifest_3393110.acf");
                        if (!File.Exists(manifest)) continue;
                        var name = Field(File.ReadAllText(manifest), "installdir");
                        if (name.Length == 0 || Path.GetFileName(name) != name) continue;
                        return Read(Path.Combine(library, "steamapps/common", name));
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }
}
