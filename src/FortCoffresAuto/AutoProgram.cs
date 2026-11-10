using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChestCounter;

namespace FortCoffres;

static class AutoProgram
{
    [STAThread]
    static int Main(string[] args)
    {
        string? Option(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string data = Option("--data") ?? Path.Combine(AppContext.BaseDirectory, "FortCoffresAuto-data");
        try
        {
            ApplicationConfiguration.Initialize();
            if (args.Contains("--self-test")) { Tests.Run(data); return 0; }
            var engine = new Engine(data, Option("--demos"));
            if (args.Contains("--scan-once"))
            {
                engine.Scan();
                var s = engine.Snapshot();
                Engine.Atomic(Path.Combine(data, "validation.json"), JsonSerializer.Serialize(new { s.Name, s.Total, s.Last, s.Average, s.Games, s.Status, Profiles = s.Profiles.Count, Errors = engine.Data.ProcessedFiles.Values.Count(v => v.StartsWith("error:")) }));
                return 0;
            }
            bool preview = args.Contains("--preview");
            if (preview)
            {
                using var f = new AutoOverlay(engine, true);
                using var bitmap = new Bitmap(192, 192);
                f.DrawToBitmap(bitmap, new Rectangle(0, 0, 192, 192));
                bitmap.Save(Option("--preview")!);
                return 0;
            }
            using var mutex = new Mutex(true, "Local\\FortCoffresAuto-v2", out bool owner);
            if (!owner) { MessageBox.Show("FortCoffres Auto est déjà ouvert. Son icône se trouve près de l’horloge."); return 0; }
            Application.Run(new AutoOverlay(engine, false));
            return 0;
        }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "startup-error.txt"), ex.ToString()); } catch { }
            if (!args.Contains("--scan-once") && !args.Contains("--self-test")) MessageBox.Show(ex.Message, "FortCoffres Auto · erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

public sealed class AutoOverlay : Form
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    readonly Engine engine;
    Summary summary;
    readonly CancellationTokenSource stop = new();
    readonly ContextMenuStrip menu = new();
    readonly NotifyIcon? tray;
    readonly ToolTip tip = new();
    readonly bool preview;
    Image? photo;
    bool locked;
    string shortcutWarning = "";
    readonly Color gold = Color.FromArgb(255, 215, 95), muted = Color.FromArgb(205, 219, 242);
    public AutoOverlay(Engine engine, bool preview)
    {
        this.engine = engine; this.preview = preview; summary = engine.Snapshot();
        AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(192, 192);
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        Location = new Point(Screen.PrimaryScreen!.WorkingArea.Left + 16, Screen.PrimaryScreen.WorkingArea.Top + 16);
        TopMost = true; ShowInTaskbar = false; DoubleBuffered = true;
        BackColor = Color.FromArgb(10, 13, 30); Opacity = 1; Text = "FortCoffres Auto";
        string background = Path.Combine(engine.Store.Folder, "background.png");
        if (File.Exists(background))
        {
            try { using var original = Image.FromFile(background); photo = new Bitmap(original); }
            catch (Exception ex) { ErrorLog.Write("Photo de fond illisible", ex); }
        }
        using (var outline = Rounded(new Rectangle(0, 0, 192, 192), 16)) Region = new Region(outline);
        menu.BackColor = Color.FromArgb(20, 24, 44); menu.ForeColor = Color.White;
        menu.Renderer = new ToolStripProfessionalRenderer(new NeonMenuColors());
        menu.Opening += (_, _) => BuildMenu(); ContextMenuStrip = menu;
        if (!preview)
        {
            tray = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application, Text = "FortCoffres Auto · replays", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (_, _) => { Show(); History(); };
            engine.Changed += UpdateSummary;
            Shown += (_, _) => _ = Task.Run(() => engine.Run(stop.Token));
        }
        MouseDown += (_, e) => { if (!locked && e.Button == MouseButtons.Left && e.Y < 158) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
        MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && e.Y >= 158) menu.Show(this, e.Location); };
        tip.SetToolTip(this, "Automatique après chaque partie. Clic droit : historique, profils, synchronisation. Ctrl+Maj+L : verrouiller.");
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (preview) return;
        bool showKey = RegisterHotKey(Handle, 1, 0x4006, 0x4F);
        bool lockKey = RegisterHotKey(Handle, 2, 0x4006, 0x4C);
        if (!showKey || !lockKey) shortcutWarning = "Raccourci occupé · menu disponible";
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312) { if (m.WParam.ToInt32() == 1) Toggle(); else LockToggle(); return; }
        base.WndProc(ref m);
    }
    void UpdateSummary(Summary s)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { summary = s; Invalidate(); tip.SetToolTip(this, s.Status + "\n" + s.Sync + "\nClic droit : menu · Ctrl+Maj+L : verrouiller"); }); } catch (InvalidOperationException) { }
    }
    void Enqueue(Action action) => engine.Commands.Enqueue(() => { try { action(); engine.Publish(); } catch (Exception ex) { ErrorLog.Write("Action", ex); if (!IsDisposed) BeginInvoke(() => MessageBox.Show(ex.Message, "FortCoffres Auto")); } });
    void Toggle() { if (Visible) Hide(); else Show(); }
    void LockToggle() { locked = !locked; int s = GetWindowLong(Handle, -20); SetWindowLong(Handle, -20, locked ? s | 0x80020 : s & ~0x20); Invalidate(); }
    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add(summary.Status).Enabled = false;
        menu.Items.Add(summary.Sync).Enabled = false;
        if (shortcutWarning.Length > 0) menu.Items.Add(shortcutWarning).Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Historique des parties…", null, (_, _) => History());
        menu.Items.Add("Choisir une photo de fond…", null, (_, _) => ChooseBackground());
        menu.Items.Add("Masquer la photo de fond", null, (_, _) => { photo?.Dispose(); photo = null; Invalidate(); });
        var profiles = new ToolStripMenuItem("Compte affiché");
        foreach (var p in summary.Profiles)
        {
            string key = p.Key;
            var item = new ToolStripMenuItem(p.Name) { Checked = key == summary.Selected };
            item.Click += (_, _) => Enqueue(() => { engine.Config.Profile = key; engine.SavePreferences(); }); profiles.DropDownItems.Add(item);
        }
        if (profiles.DropDownItems.Count == 0) profiles.DropDownItems.Add("Détecté au premier replay").Enabled = false;
        menu.Items.Add(profiles);
        menu.Items.Add("Choisir le dossier des replays…", null, (_, _) => ChooseDemos());
        menu.Items.Add("Retenter les replays illisibles", null, (_, _) => Enqueue(() =>
        {
            foreach (var key in engine.Data.ProcessedFiles.Where(p => p.Value.StartsWith("error:")).Select(p => p.Key).ToArray()) engine.Data.ProcessedFiles.Remove(key);
            engine.Store.SaveData(engine.Data);
        }));
        menu.Items.Add("Synchroniser entre PC…", null, (_, _) => ChooseSync());
        menu.Items.Add("Désactiver la synchronisation", null, (_, _) => Enqueue(() => { engine.Prefs.SyncFolder = null; engine.SavePreferences(); engine.Synchronize(); }));
        menu.Items.Add("Importer un historique…", null, (_, _) => Import());
        menu.Items.Add("Exporter l’historique…", null, (_, _) => Export());
        var destroyed = new ToolStripMenuItem("Inclure aussi les coffres détruits") { Checked = engine.Prefs.IncludeDestroyed };
        destroyed.Click += (_, _) => Enqueue(() => { engine.Prefs.IncludeDestroyed = !engine.Prefs.IncludeDestroyed; engine.SavePreferences(); }); menu.Items.Add(destroyed);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(locked ? "Déverrouiller · Ctrl+Maj+L" : "Verrouiller · Ctrl+Maj+L", null, (_, _) => LockToggle());
        menu.Items.Add("Afficher / masquer · Ctrl+Maj+O", null, (_, _) => Toggle());
        menu.Items.Add("Remettre en haut à gauche", null, (_, _) => { Location = new Point(16, 16); Show(); });
        menu.Items.Add("Ouvrir les sauvegardes", null, (_, _) => Process.Start(new ProcessStartInfo(engine.Store.Folder) { UseShellExecute = true }));
        menu.Items.Add("Mode d’emploi", null, (_, _) => Help());
        menu.Items.Add("Quitter", null, (_, _) => Close());
    }
    void ChooseDemos()
    {
        using var picker = new FolderBrowserDialog { Description = "Choisir le dossier contenant les fichiers .replay Fortnite", UseDescriptionForTitle = true };
        if (picker.ShowDialog() == DialogResult.OK) { string path = picker.SelectedPath; Enqueue(() => { engine.Config.DemosFolder = path; engine.SavePreferences(); }); }
    }
    void ChooseBackground()
    {
        using var picker = new OpenFileDialog { Filter = "Photo (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Title = "Photo locale · fond à 50 % d’opacité" };
        if (picker.ShowDialog() != DialogResult.OK) return;
        try
        {
            using var original = Image.FromFile(picker.FileName);
            var replacement = new Bitmap(original);
            try { replacement.Save(Path.Combine(engine.Store.Folder, "background.png"), ImageFormat.Png); }
            catch { replacement.Dispose(); throw; }
            photo?.Dispose(); photo = replacement; Invalidate();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Impossible de charger cette photo"); }
    }
    void ChooseSync()
    {
        MessageBox.Show("Choisis un dossier déjà synchronisé entre tes PC (par exemple OneDrive).\n\nL’application y déposera l’historique des parties avec les pseudos et identifiants Epic présents dans les replays. Elle fusionnera les parties sans doublon.\n\nAucun replay ni mot de passe n’est copié. Le transfert entre PC est assuré par ton logiciel de synchronisation. Ne choisis pas un dossier public.", "Synchronisation facultative");
        using var picker = new FolderBrowserDialog { Description = "Dossier partagé pour les historiques FortCoffres", UseDescriptionForTitle = true };
        if (picker.ShowDialog() == DialogResult.OK) { string path = picker.SelectedPath; Enqueue(() => { engine.Prefs.SyncFolder = path; engine.SavePreferences(); engine.Synchronize(); }); }
    }
    void Import()
    {
        using var picker = new OpenFileDialog { Filter = "Historique automatique (*.json)|*.json", Title = "Importer un historique FortCoffres Auto" };
        if (picker.ShowDialog() == DialogResult.OK) { string path = picker.FileName; Enqueue(() => engine.Import(path)); }
    }
    void Export()
    {
        using var picker = new SaveFileDialog { Filter = "Historique (*.json)|*.json", FileName = "FortCoffres-historique.json" };
        if (picker.ShowDialog() == DialogResult.OK) { string path = picker.FileName; Enqueue(() => Engine.Atomic(path, JsonSerializer.Serialize(new TrackerData { Matches = engine.Data.Matches, AnalyzerVersion = ReplayAnalyzer.CurrentVersion }))); }
    }
    void Help() => MessageBox.Show("FortCoffres Auto lit les replays locaux, en lecture seule, puis regroupe les parties par identifiant Epic.\n\nActive les rediffusions dans Fortnite et utilise le plein écran fenêtré pour voir le carré. Les chiffres arrivent après la partie : la clé du replay n’est généralement disponible qu’à la fin.\n\nTotal et moyenne portent sur les replays analysés, pas sur les anciennes parties supprimées. Les coffres ouverts ET cassés sont inclus par défaut. Les boîtes de munitions sont exclues.\n\nCtrl+Maj+O : afficher / masquer\nCtrl+Maj+L : verrouiller / laisser passer la souris\nClic droit ou icône près de l’horloge : menu\n\nLa synchronisation entre PC utilise un dossier de ton choix. Aucune connexion aux serveurs Epic.\n\nLecteur de replays : LED-N/Fortnite-ChestCounter et Shiqan/FortniteReplayDecompressor (MIT).", "FortCoffres Auto · aide");
    void History()
    {
        using var f = new Form { Text = "FortCoffres Auto · " + summary.Name, ClientSize = new Size(680, 430), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        f.BackColor = Color.FromArgb(13, 18, 35); f.ForeColor = Color.White;
        var info = new Label { Dock = DockStyle.Top, Height = 54, Padding = new Padding(8), Text = summary.Games + " partie(s) analysée(s) · " + summary.Total + " coffre(s)\n" + summary.Status + " · " + summary.Sync };
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        info.ForeColor = Color.FromArgb(142, 229, 255);
        grid.BackgroundColor = f.BackColor; grid.BorderStyle = BorderStyle.None; grid.GridColor = Color.FromArgb(42, 48, 77);
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersHeight = 32;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(38, 33, 73), ForeColor = Color.FromArgb(223, 214, 255), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(20, 27, 47), ForeColor = Color.White, SelectionBackColor = Color.FromArgb(79, 60, 139), SelectionForeColor = Color.White, Font = new Font("Segoe UI", 9) };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(26, 34, 57); grid.RowTemplate.Height = 28;
        grid.Columns.Add("date", "Date locale"); grid.Columns.Add("chests", "Coffres"); grid.Columns.Add("mode", "Mode"); grid.Columns.Add("place", "Classement");
        foreach (var m in summary.Matches) grid.Rows.Add((m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime().ToString("g"), engine.Count(m), Pretty.Playlist(m.Playlist), m.Placement);
        f.Controls.Add(grid); f.Controls.Add(info); f.ShowDialog();
    }
    void Draw(Graphics g, string text, float size, FontStyle weight, Color color, Rectangle rect, StringAlignment align = StringAlignment.Near)
    {
        using var font = new Font("Segoe UI", size, weight); using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
        g.DrawString(text, font, shadow, new Rectangle(rect.X, rect.Y + 1, rect.Width, rect.Height), fmt);
        g.DrawString(text, font, brush, rect, fmt);
    }
    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    static void Panel(Graphics g, Rectangle rect, int radius, Color color)
    { using var path = Rounded(rect, radius); using var b = new SolidBrush(color); g.FillPath(b, path); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // Cover crop, original photo embedded locally, exactly 50% image opacity.
        if (photo != null)
        {
            float side = Math.Min(photo.Width, photo.Height);
            var crop = new RectangleF((photo.Width - side) / 2, (photo.Height - side) / 2, side, side);
            using var attributes = new ImageAttributes();
            var matrix = new ColorMatrix { Matrix33 = .50f }; attributes.SetColorMatrix(matrix);
            g.DrawImage(photo, new Rectangle(0, 0, 192, 192), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attributes);
        }
        using (var shade = new LinearGradientBrush(new Rectangle(0, 0, 192, 192), Color.Transparent, Color.Black, 90f))
        {
            shade.InterpolationColors = new ColorBlend { Positions = new[] { 0f, .43f, 1f }, Colors = new[] { Color.FromArgb(180, 15, 13, 45), Color.FromArgb(10, 15, 13, 45), Color.FromArgb(210, 6, 12, 29) } };
            g.FillRectangle(shade, 0, 0, 192, 192);
        }
        using (var glow = new LinearGradientBrush(new Rectangle(0, 0, 192, 192), Color.FromArgb(120, 125, 73, 255), Color.FromArgb(0, 32, 218, 248), 35f)) g.FillEllipse(glow, -65, -75, 220, 170);
        using (var line = new LinearGradientBrush(new Point(0, 0), new Point(192, 192), Color.FromArgb(160, 99, 255), Color.FromArgb(51, 224, 244)))
        using (var pen = new Pen(line, 1.5f))
        using (var edge = Rounded(new Rectangle(1, 1, 189, 189), 15)) g.DrawPath(pen, edge);
        using (var accent = new LinearGradientBrush(new Point(14, 0), new Point(178, 0), Color.FromArgb(142, 92, 255), Color.FromArgb(52, 230, 255))) g.FillRectangle(accent, 19, 1, 154, 2);
        Draw(g, "FORTCOFFRES", 8, FontStyle.Bold, Color.White, new(13, 9, 124, 19));
        Panel(g, new(143, 12, 36, 15), 6, Color.FromArgb(170, 17, 95, 89));
        Draw(g, "AUTO", 6, FontStyle.Bold, Color.FromArgb(119, 255, 212), new(143, 12, 36, 15), StringAlignment.Center);
        Draw(g, summary.Name + (locked ? " · verrouillé" : ""), 7, FontStyle.Regular, muted, new(13, 30, 167, 17));
        // Tiny code-drawn chest keeps this an entirely native Windows interface.
        Panel(g, new(14, 64, 23, 17), 3, Color.FromArgb(238, 171, 46));
        using (var p = new Pen(Color.FromArgb(255, 224, 117), 2)) { g.DrawLine(p, 15, 70, 35, 70); g.DrawLine(p, 20, 64, 20, 80); g.DrawLine(p, 31, 64, 31, 80); }
        using (var b = new SolidBrush(Color.FromArgb(72, 42, 14))) g.FillRectangle(b, 24, 69, 4, 6);
        Draw(g, summary.Games == 0 ? "—" : summary.Total.ToString("N0", L.Culture), 29, FontStyle.Bold, gold, new(42, 48, 141, 46));
        Draw(g, engine.Prefs.IncludeDestroyed ? "OUVERTS + CASSÉS" : "COFFRES OUVERTS", 6.5f, FontStyle.Bold, Color.FromArgb(255, 229, 161), new(14, 93, 164, 14));
        Panel(g, new(12, 114, 80, 42), 8, Color.FromArgb(195, 27, 25, 65));
        Panel(g, new(100, 114, 80, 42), 8, Color.FromArgb(195, 10, 43, 62));
        Draw(g, "DERNIÈRE", 6, FontStyle.Bold, Color.FromArgb(194, 177, 255), new(20, 117, 67, 13));
        Draw(g, "MOY. / PARTIE", 6, FontStyle.Bold, Color.FromArgb(117, 226, 255), new(108, 117, 68, 13));
        Draw(g, summary.Last?.ToString() ?? "—", 14, FontStyle.Bold, Color.White, new(19, 129, 68, 24));
        Draw(g, summary.Average?.ToString("0.0", L.Culture) ?? "—", 14, FontStyle.Bold, Color.White, new(107, 129, 69, 24));
        using (var b = new SolidBrush(Color.FromArgb(99, 242, 183))) g.FillEllipse(b, 14, 165, 4, 4);
        Draw(g, summary.Status, 6.3f, FontStyle.Regular, muted, new(22, 158, 158, 17));
        Draw(g, summary.Games + " PARTIES", 6, FontStyle.Bold, Color.FromArgb(155, 175, 209), new(14, 176, 87, 12));
        Draw(g, "MENU  •••", 6, FontStyle.Bold, Color.FromArgb(125, 224, 255), new(105, 176, 73, 12), StringAlignment.Far);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        stop.Cancel(); engine.Changed -= UpdateSummary;
        UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2);
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        tip.Dispose(); menu.Dispose(); base.OnFormClosed(e);
    }
    protected override void Dispose(bool disposing) { if (disposing) photo?.Dispose(); base.Dispose(disposing); }
}

sealed class NeonMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(20, 24, 44);
    public override Color ImageMarginGradientBegin => Color.FromArgb(28, 29, 58);
    public override Color ImageMarginGradientMiddle => ImageMarginGradientBegin;
    public override Color ImageMarginGradientEnd => ImageMarginGradientBegin;
    public override Color MenuItemSelected => Color.FromArgb(70, 53, 124);
    public override Color MenuItemBorder => Color.FromArgb(133, 105, 246);
    public override Color MenuBorder => Color.FromArgb(99, 83, 167);
    public override Color SeparatorDark => Color.FromArgb(58, 63, 91);
    public override Color SeparatorLight => SeparatorDark;
}
