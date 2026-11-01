using System.Text.Json;
using System.Drawing.Drawing2D;

namespace Aion2PL;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--apply-launcher-update")
        {
            try { LauncherSelfUpdate.Apply(args); }
            catch (Exception e) { MessageBox.Show("Nie udało się zaktualizować launchera. Poprzedni plik zachowano.\n" + e.Message, "Aion2PL", MessageBoxButtons.OK, MessageBoxIcon.Warning); Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        using var fonts = AppFonts.Shared;
        if (args is ["--self-test", var output]) { try { SelfTest.Run(output); UpdateTests.Run(output).GetAwaiter().GetResult(); } catch (Exception e) { File.WriteAllText(output + ".error.txt", e.ToString()); Environment.ExitCode = 1; } return; }
        if (args is ["--verify-live", var liveReport]) { try { LiveCheck.Run(liveReport).GetAwaiter().GetResult(); } catch (Exception e) { File.WriteAllText(liveReport + ".error.txt", e.ToString()); Environment.ExitCode = 1; } return; }
        if (args is ["--inspect", var report])
        {
            File.WriteAllText(report, JsonSerializer.Serialize(GameInstallation.Find(), new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        using var window = new LauncherWindow(!args.Contains("--preview"));
        if (args.Length >= 2 && args[0] == "--preview")
        {
            window.Show();
            int releaseArg = Array.IndexOf(args, "--release");
            if (releaseArg >= 0 && releaseArg + 1 < args.Length)
            {
                var previewRelease = JsonSerializer.Deserialize<Release>(File.ReadAllText(args[releaseArg + 1]), UpdateClient.Json)!;
                previewRelease.Validate();
                window.PreviewRelease(previewRelease);
                int packageArg = Array.IndexOf(args, "--package");
                if (packageArg >= 0 && packageArg + 1 < args.Length)
                {
                    var packagePath = args[packageArg + 1];
                    if (new FileInfo(packagePath).Length != previewRelease.SizeBytes || !string.Equals(Installer.Hash(packagePath), previewRelease.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Nieprawidłowa paczka podglądu.");
                    window.PreviewManualFiles(Package.Read(packagePath));
                }
            }
            if (args.Contains("--details")) window.ShowDetails();
            Application.DoEvents();
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(args[1]);
            File.WriteAllText(args[1] + ".json", JsonSerializer.Serialize(window.PreviewState, UpdateClient.Json));
            return;
        }
        Application.Run(window);
    }
}

internal sealed partial class LauncherWindow : Form
{
    static readonly Color Background = ColorTranslator.FromHtml("#0a0a16");
    static readonly Color Card = ColorTranslator.FromHtml("#16162e");
    static readonly Color Muted = Color.FromArgb(166, 162, 188);
    static readonly Color Gold = ColorTranslator.FromHtml("#c4b5fd");
    readonly Label folder = new(), build = new(), status = new();
    readonly Image logo;
    readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aion2PL", "game-folder.txt");
    GameInstallation? installation;

    internal LauncherWindow(bool checkNetwork)
    {
        Text = "Aion2PL by kashello — 0.5.4";
        using var iconStream = typeof(Program).Assembly.GetManifestResourceStream("aion2pl.ico")!;
        using var loadedIcon = new Icon(iconStream);
        Icon = (Icon)loadedIcon.Clone();
        ClientSize = new Size(1100, 760);
        MinimumSize = new Size(1116, 799);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Background;
        ForeColor = Color.White;
        Font = AppFonts.Ui(10.5f);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(22) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        Controls.Add(layout);
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("kashello.png")!;
        logo = new Bitmap(stream);
        var picture = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom, Image = logo, TabStop = false, Margin = new Padding(0, 0, 22, 0) };
        var artPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 22, 0) };
        picture.Margin = Padding.Empty;
        artPanel.Controls.Add(picture);
        artPanel.Controls.Add(logBox);
        layout.Controls.Add(artPanel, 0, 0);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Margin = Padding.Empty };
        foreach (var height in new[] { 82, 40, 110, 88, 126, 56, 52, 64 }) right.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(right, 1, 0);
        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label { Text = "Aion2PL", ForeColor = Gold, Font = AppFonts.Title(30), AutoSize = true, Location = new Point(0, 0) });
        heading.Controls.Add(new Label { Text = "by kashello  ·  Warsztat Czarodzieja", ForeColor = Gold, AutoSize = true, Location = new Point(4, 57) });
        right.Controls.Add(heading, 0, 0);
        right.Controls.Add(new Label { Text = "Twoja przygoda w AION 2 — po polsku.", Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        var folderBox = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(14), ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
        folderBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        folderBox.Controls.Add(new Label { Text = "FOLDER GRY", AutoSize = true, ForeColor = Muted }, 0, 0);
        folder.Dock = DockStyle.Fill;
        folder.AutoEllipsis = true;
        folder.TextAlign = ContentAlignment.MiddleLeft;
        folderBox.Controls.Add(folder, 0, 1);
        choose = Button("Zmień folder", true);
        choose.Click += (_, _) => ChooseFolder();
        folderBox.Controls.Add(choose, 1, 1);
        right.Controls.Add(folderBox, 0, 2);
        build.Dock = DockStyle.Fill;
        build.ForeColor = Muted;
        build.BackColor = Card;
        build.Font = AppFonts.Ui(11, FontStyle.Bold);
        build.Padding = new Padding(12, 5, 12, 5);
        build.TextAlign = ContentAlignment.MiddleLeft;
        right.Controls.Add(build, 0, 3);
        status.Dock = DockStyle.Fill;
        status.BackColor = Card;
        status.Padding = new Padding(16);
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.ForeColor = Gold;
        right.Controls.Add(status, 0, 4);
        refresh = Button("Odśwież", true);
        refresh.Dock = DockStyle.None;
        refresh.Size = new Size(130, 38);
        refresh.Click += async (_, _) => await CheckUpdates();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.Add(refresh);
        actions.Controls.Add(details);
        right.Controls.Add(actions, 0, 5);
        install = Button("Zainstaluj spolszczenie", false, true);
        install.Click += async (_, _) => await InstallUpdate();
        right.Controls.Add(install, 0, 6);
        restore = Button("Przywróć oryginał", false);
        restore.Click += async (_, _) => await RestoreUpdate();
        right.Controls.Add(restore, 0, 7);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        versions.AutoSize = true;
        versions.ForeColor = Muted;
        versions.Font = AppFonts.Ui(9);
        footer.Controls.Add(versions);
        var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        AddLink(links, "kashello.pl", "https://kashello.pl/");
        AddLink(links, "Twitch", "https://www.twitch.tv/kashelloo");
        AddLink(links, "YouTube", "https://www.youtube.com/@Kashello");
        AddLink(links, "Discord", "https://discord.gg/YgpYQg45Mp");
        AddLicenses(links);
        footer.Controls.Add(links);
        var notice = new LinkLabel
        {
            Text = "Używasz na własną odpowiedzialność — przeczytaj informację",
            AutoSize = true, Margin = new Padding(0, 8, 0, 0),
            LinkColor = ColorTranslator.FromHtml("#FCD34D"),
            ActiveLinkColor = Color.White, VisitedLinkColor = ColorTranslator.FromHtml("#FCD34D")
        };
        notice.LinkClicked += (_, _) => MessageBox.Show(this,
            "Używasz na własną odpowiedzialność — nie mogę zagwarantować braku bana. " +
            "Dodajemy plik z polskimi tekstami i wyłączamy angielski PAK przez zmianę jego nazwy, aby gra wczytała spolszczenie. " +
            "Nie zmieniamy zawartości oryginalnych plików gry ani plików EXE/DLL.\n\n" +
            "Od premiery sam używam spolszczenia i dotychczas nie otrzymałem blokady konta. " +
            "To moje doświadczenie, a nie gwarancja, że blokada nie wystąpi w przyszłości lub u innych graczy.\n\n— kashello",
            "Aion2PL — ważna informacja", MessageBoxButtons.OK, MessageBoxIcon.Information);
        footer.Controls.Add(notice);
        right.Controls.Add(footer, 0, 8);
        try
        {
            if (File.Exists(settings)) installation = GameInstallation.Read(File.ReadAllText(settings).Trim());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
        RefreshGame();
        ConfigureUpdates(checkNetwork);
    }

    static Button Button(string text, bool enabled, bool primary = false) => new ReadableButton
    {
        Primary = primary,
        Font = AppFonts.Ui(10, FontStyle.Bold),
        Text = text, Enabled = enabled, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
        BackColor = enabled ? Color.FromArgb(77, 52, 113) : Color.FromArgb(30, 24, 45),
        ForeColor = Color.White, Margin = new Padding(0, 8, 0, 0), Cursor = enabled ? Cursors.Hand : Cursors.Default,
        AccessibleName = text, UseVisualStyleBackColor = false
    };

    void ChooseFolder()
    {
        using var picker = new FolderBrowserDialog { Description = "Wybierz główny folder AION2 (z folderem Aion2 w środku).", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (installation is not null) picker.InitialDirectory = installation.Root;
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var selected = GameInstallation.Read(picker.SelectedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, selected.Root);
            installation = selected;
            RefreshGame();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, e.Message, "Nie udało się wybrać folderu", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { updateTimer.Dispose(); client.Dispose(); lifetime.Dispose(); logo.Dispose(); Icon?.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class ReadableButton : Button
{
    internal bool Primary;
    bool hovered, pressed;
    internal ReadableButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatAppearance.BorderSize = 0;
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(10, 10, 22));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        if (bounds.Width < 24 || bounds.Height < 24) return;
        using var shape = new GraphicsPath();
        const int diameter = 20;
        shape.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        shape.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        shape.CloseFigure();
        var top = !Enabled ? Color.FromArgb(34, 29, 48) : Primary ? Color.FromArgb(116, 82, 188) : hovered ? Color.FromArgb(50, 40, 72) : Color.FromArgb(34, 29, 49);
        var bottom = !Enabled ? Color.FromArgb(27, 24, 39) : Primary ? Color.FromArgb(78, 55, 143) : Color.FromArgb(26, 23, 39);
        if (pressed) top = bottom;
        using var fill = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
        using var border = new Pen(Enabled && hovered ? Color.FromArgb(182, 150, 238) : Color.FromArgb(78, 64, 108));
        e.Graphics.FillPath(fill, shape);
        e.Graphics.DrawPath(border, shape);
        var textBounds = Rectangle.Inflate(bounds, -12, -3);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? Color.FromArgb(245, 239, 255) : Color.FromArgb(154, 145, 174),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -5, -5));
    }
}
