using System.Text.Json;

namespace Aion2PL;

internal static class SelfTest
{
    internal static void Run(string report)
    {
        var scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "launcher-test-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        Directory.CreateDirectory(scratch);
        try
        {
            var steam = Path.Combine(scratch, "Steam");
            var library = Path.Combine(scratch, "Biblioteka z odstępem");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            File.WriteAllText(Path.Combine(steam, "steamapps/libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" \"" + library.Replace(@"\", @"\\") + "\" } }");
            Check(GameInstallation.Libraries(steam).Contains(library), "Biblioteka Steam na innej ścieżce ze spacjami");
            var root = Path.Combine(library, "steamapps/common/AION2");
            Directory.CreateDirectory(Path.Combine(root, "Aion2/Content/Paks"));
            var manifest = Path.Combine(library, "steamapps/appmanifest_3393110.acf");
            string Manifest(string app = "3393110", string target = "25926599", string state = "4") =>
                $"\"AppState\" {{ \"appid\" \"{app}\" \"installdir\" \"AION2\" \"buildid\" \"25926599\" \"TargetBuildID\" \"{target}\" \"StateFlags\" \"{state}\" }}";
            File.WriteAllText(manifest, Manifest());
            var game = GameInstallation.Read(root);
            Check(game.Build == "25926599" && !game.Updating && !game.DatPresent, "Build i brak DAT");
            var dat = Path.Combine(root, GameInstallation.DatPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dat)!);
            File.WriteAllText(dat, "fixture");
            var pak = Path.Combine(root, GameInstallation.PakPath);
            Directory.CreateDirectory(Path.GetDirectoryName(pak)!);
            File.WriteAllText(pak, "fixture");
            game = GameInstallation.Read(root);
            Check(game.DatPresent && game.EnglishPakPresent, "Wykrywanie DAT i aktywnego PAK");
            File.Move(pak, pak + ".backup");
            Check(!GameInstallation.Read(root).EnglishPakPresent, "Backup nie jest aktywnym PAK");
            File.WriteAllText(manifest, Manifest(target: "26000000"));
            Check(GameInstallation.Read(root).Updating, "Aktualizacja do innego buildu");
            File.WriteAllText(manifest, Manifest(state: "6"));
            Check(GameInstallation.Read(root).Updating, "Niepełny stan instalacji Steam");
            File.WriteAllText(manifest, Manifest(app: "123"));
            bool rejected = false;
            try { GameInstallation.Read(root); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Odrzucenie innej gry");
            Check(File.ReadAllText(dat) == "fixture" && File.ReadAllText(pak + ".backup") == "fixture", "Odczyt nie zmienia plików");
            File.WriteAllText(report, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (!Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetDirectoryName(report)!) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test directory");
            Directory.Delete(scratch, true);
        }
    }
}
