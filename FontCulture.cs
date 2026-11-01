using System.Text;
using System.Text.RegularExpressions;

namespace Aion2PL;

internal static class FontCulture
{
    internal const string Path = "Aion2/Config/UserGame.ini";
    const string Begin = "; BEGIN AION2PL FONT CULTURE";
    const string End = "; END AION2PL FONT CULTURE";
    static readonly string[] Required = ["en-US;fr-FR", "en;fr"];
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly Regex Entry = new(@"^\s*\+?CultureMappings\s*=\s*""?([^"";\s]+)\s*;\s*([^"";\s]+)""?\s*(?:[;#].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static string Decode(byte[]? bytes)
    {
        if (bytes is null) return "";
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Plik UserGame.ini jest zbyt duży.");
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Nie można bezpiecznie odczytać UserGame.ini jako UTF-8."); }
    }
    static Dictionary<string, string> Mappings(string text)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var active = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[') && line.EndsWith(']'))
            { active = line.Equals("[Internationalization]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!active || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith("!CultureMappings", StringComparison.OrdinalIgnoreCase) || line.StartsWith("-CultureMappings", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UserGame.ini usuwa mapowania językowe; zachowano istniejącą konfigurację.");
            var match = Entry.Match(line);
            if (!match.Success) continue;
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value;
            if (found.TryGetValue(key, out var old) && !old.Equals(value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UserGame.ini zawiera sprzeczne mapowania języka " + key + ".");
            found[key] = value;
        }
        return found;
    }
    internal static bool Enabled(byte[]? bytes)
    {
        var found = Mappings(Decode(bytes));
        return Required.All(pair => { var p = pair.Split(';'); return found.TryGetValue(p[0], out var value) && value.Equals(p[1], StringComparison.OrdinalIgnoreCase); });
    }
    internal static byte[] Add(byte[]? bytes)
    {
        var text = Decode(bytes);
        var hasBegin = text.Contains(Begin, StringComparison.Ordinal);
        var hasEnd = text.Contains(End, StringComparison.Ordinal);
        if (hasBegin != hasEnd || (hasBegin && (text.IndexOf(Begin, StringComparison.Ordinal) > text.IndexOf(End, StringComparison.Ordinal) || text.IndexOf(Begin, text.IndexOf(Begin, StringComparison.Ordinal) + Begin.Length, StringComparison.Ordinal) >= 0)))
            throw new InvalidDataException("Uszkodzony fragment ustawień Aion2PL w UserGame.ini.");
        var mappings = Mappings(text);
        var missing = new List<string>();
        foreach (var pair in Required)
        {
            var parts = pair.Split(';');
            if (mappings.TryGetValue(parts[0], out var value))
            {
                if (!value.Equals(parts[1], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("UserGame.ini ma inne mapowanie " + parts[0] + "; zachowano istniejące ustawienia.");
            }
            else missing.Add(pair);
        }
        if (missing.Count == 0) return bytes ?? [];
        if (hasBegin) throw new InvalidDataException("Fragment ustawień Aion2PL został zmieniony; sprawdź UserGame.ini.");
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = text.Length > 0 && !text.EndsWith('\n') ? newline : "";
        return Utf8.GetBytes(text + prefix + Begin + newline + "[Internationalization]" + newline +
            string.Join(newline, missing.Select(pair => "+CultureMappings=\"" + pair + "\"")) + newline + End + newline);
    }
    internal static byte[]? Remove(byte[]? bytes)
    {
        if (bytes is null) return null;
        var text = Decode(bytes);
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 && end < 0) return bytes;
        if (start < 0 || end < start || text.IndexOf(Begin, start + Begin.Length, StringComparison.Ordinal) >= 0 || text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Uszkodzony fragment ustawień Aion2PL; nie zmieniono UserGame.ini.");
        if ((start > 0 && text[start - 1] != '\n') || (end + End.Length < text.Length && text[end + End.Length] != '\r' && text[end + End.Length] != '\n'))
            throw new InvalidDataException("Nieprawidłowe granice fragmentu ustawień Aion2PL.");
        var endOfBlock = end + End.Length;
        if (text.AsSpan(endOfBlock).StartsWith("\r\n")) endOfBlock += 2;
        else if (text.AsSpan(endOfBlock).StartsWith("\n")) endOfBlock++;
        var block = text[start..endOfBlock];
        var lines = block.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0).ToArray();
        if (lines.Length < 4 || lines[0] != Begin || lines[1] != "[Internationalization]" || lines[^1] != End ||
            lines[2..^1].Any(line => !Required.Any(pair => line == "+CultureMappings=\"" + pair + "\"")))
            throw new InvalidDataException("Fragment ustawień Aion2PL został zmieniony; zachowano UserGame.ini.");
        var rest = text.Remove(start, endOfBlock - start);
        return rest.Length == 0 ? null : Utf8.GetBytes(rest);
    }
}
