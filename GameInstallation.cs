using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace Aion2PL;

internal record GameInstallation(string Root, string Build, bool Updating, bool DatPresent, bool EnglishPakPresent)
{
    internal const string DatPath = "Aion2/Content/L10N/Text/en-US/L10NString.dat";
    internal const string PakPath = "Aion2/Content/Paks/L10N/Text/en-US/pakchunk502000-Windows_0_P.pak";

    internal static string Field(string text, string name) =>
        Regex.Match(text, "\"" + Regex.Escape(name) + "\"\\s+\"([^\"\\r\\n]*)\"").Groups[1].Value;

    internal static GameInstallation Read(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var manifest = Path.GetFullPath(Path.Combine(root, "../../appmanifest_3393110.acf"));
        var text = File.ReadAllText(manifest);
        var build = Field(text, "buildid");
        if (Field(text, "appid") != "3393110" || Field(text, "installdir") != Path.GetFileName(root) ||
            !Regex.IsMatch(build, @"^\d+$") || !Directory.Exists(Path.Combine(root, "Aion2/Content/Paks")))
            throw new InvalidDataException("Wskaż główny folder AION 2 w bibliotece Steam.");
        var target = Field(text, "TargetBuildID");
        bool updating = Field(text, "StateFlags") != "4" || (target.Length > 0 && target != build);
        return new(root, build, updating, File.Exists(Path.Combine(root, DatPath)), File.Exists(Path.Combine(root, PakPath)));
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
