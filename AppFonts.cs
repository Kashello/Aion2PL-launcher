using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Aion2PL;

internal sealed class AppFonts : IDisposable
{
    internal static readonly AppFonts Shared = new();
    readonly PrivateFontCollection collection = new();
    readonly List<nint> memory = [], resources = [];
    [DllImport("gdi32.dll")]
    static extern nint AddFontMemResourceEx(nint data, uint size, nint reserved, ref uint count);
    [DllImport("gdi32.dll")]
    static extern bool RemoveFontMemResourceEx(nint handle);
    AppFonts()
    {
        foreach (var name in new[] { "Cinzel-Bold.ttf", "Inter-Regular.ttf", "Inter-Bold.ttf" })
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream(name)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var data = bytes.ToArray();
            var pointer = Marshal.AllocHGlobal(data.Length);
            memory.Add(pointer);
            Marshal.Copy(data, 0, pointer, data.Length);
            collection.AddMemoryFont(pointer, data.Length);
            uint count = 0;
            var handle = AddFontMemResourceEx(pointer, (uint)data.Length, 0, ref count);
            if (handle == 0) throw new InvalidOperationException("Nie udało się wczytać czcionek aplikacji.");
            resources.Add(handle);
        }
    }
    FontFamily Family(string name) => collection.Families.First(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    internal static Font Ui(float size, FontStyle style = FontStyle.Regular) => new(Shared.Family("Inter"), size, style);
    internal static Font Title(float size) => new(Shared.Family("Cinzel"), size, FontStyle.Bold);
    internal static string Licenses()
    {
        var texts = new List<string>();
        foreach (var name in new[] { "Cinzel-OFL.txt", "Inter-OFL.txt" })
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            texts.Add(reader.ReadToEnd());
        }
        return string.Join("\r\n\r\n==============================\r\n\r\n", texts);
    }
    public void Dispose()
    {
        collection.Dispose();
        foreach (var handle in resources) RemoveFontMemResourceEx(handle);
        foreach (var pointer in memory) Marshal.FreeHGlobal(pointer);
    }
}
