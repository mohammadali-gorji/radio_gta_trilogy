// Easy installer for Custom Radio (GTA San Andreas: Definitive Edition).
// One self-contained exe: CLEO Redux, the ASI loader, the CLEO script and the helper are embedded resources.
// Written for the C# 5 compiler that ships with every Windows (csc.exe in .NET Framework 4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Custom Radio Easy Installer")]
[assembly: AssemblyVersion("1.0.0.0")]

class Options
{
    public string Win64;          // folder that contains SanAndreas.exe
    public bool HasLoader;        // user already has the Ultimate ASI Loader (version.dll)
    public bool HasCleo;          // user already has CLEO Redux 1.4.2 x64
    public bool Autostart = true;
    public bool StartNow = true;
    public string MusicSrc;       // optional folder to import .mp3 files from
}

static class Core
{
    public const string Version = "1.0.0";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "CustomRadio";
    public static Action<string> Log = delegate { };

    // ---------- embedded resources ----------
    static byte[] Res(string name)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new InvalidOperationException("Installer is damaged: missing resource " + name);
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    // ---------- game detection ----------
    public static string ResolveWin64(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var found = new List<string>();
        Scan(dir, 4, found);
        return found.FirstOrDefault();
    }

    public static List<string> FindGames()
    {
        var found = new List<string>();
        var roots = new List<string>();
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string steam = k == null ? null : k.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(steam)) roots.Add(Path.Combine(steam.Replace('/', '\\'), @"steamapps\common"));
            }
        }
        catch { }
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string root = d.RootDirectory.FullName;
                roots.Add(root);
                foreach (string rel in new[] { @"Program Files\Rockstar Games", @"Program Files (x86)\Rockstar Games",
                    @"Program Files\Steam\steamapps\common", @"Program Files (x86)\Steam\steamapps\common",
                    @"Program Files\Epic Games", @"SteamLibrary\steamapps\common", "Games" })
                    roots.Add(Path.Combine(root, rel));
            }
            catch { }
        }
        foreach (string r in roots)
        {
            // drive roots are scanned shallowly, known game folders a bit deeper
            Scan(r, r.Length <= 3 ? 2 : 4, found);
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Scan(string dir, int depth, List<string> found)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            string a = Path.Combine(dir, @"Gameface\Binaries\Win64");
            if (File.Exists(Path.Combine(a, "SanAndreas.exe"))) { found.Add(a); return; }
            if (File.Exists(Path.Combine(dir, "SanAndreas.exe"))) { found.Add(dir); return; }
            if (depth <= 0) return;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string n = Path.GetFileName(sub);
                if (n.Equals("Windows", StringComparison.OrdinalIgnoreCase) || n.StartsWith("$") ||
                    n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                Scan(sub, depth - 1, found);
            }
        }
        catch { }
    }

    public static bool LoaderPresent(string win64) { return File.Exists(Path.Combine(win64, "version.dll")); }
    public static bool CleoPresent(string win64)
    {
        return File.Exists(Path.Combine(win64, "cleo_redux64.asi")) &&
               File.Exists(Path.Combine(win64, @"CLEO\CLEO_PLUGINS\IniFiles64.cleo"));
    }

    // ---------- file helpers ----------
    static string backupRoot;

    static bool SameBytes(string path, byte[] data)
    {
        FileInfo fi = new FileInfo(path);
        if (fi.Length != data.Length) return false;
        byte[] cur = File.ReadAllBytes(path);
        for (int i = 0; i < cur.Length; i++) if (cur[i] != data[i]) return false;
        return true;
    }

    static void Put(string win64, string dest, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        string rel = dest.Substring(win64.Length).TrimStart('\\');
        if (File.Exists(dest))
        {
            if (SameBytes(dest, data)) { Log("  unchanged: " + rel); return; }
            string b = Path.Combine(backupRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(b));
            File.Copy(dest, b, true);
            Log("  backed up existing " + rel);
        }
        File.WriteAllBytes(dest, data);
        Log("  installed: " + rel);
    }

    static void ExtractZip(string win64, byte[] zip, Func<string, string> map)
    {
        using (var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry e in za.Entries)
            {
                if (e.FullName.EndsWith("/")) continue;
                string rel = map(e.FullName);
                if (rel == null) continue;
                string dest = Path.GetFullPath(Path.Combine(win64, rel.Replace('/', '\\')));
                if (!dest.StartsWith(win64, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("bad zip entry " + e.FullName);
                byte[] data;
                using (Stream s = e.Open()) { var ms = new MemoryStream(); s.CopyTo(ms); data = ms.ToArray(); }
                Put(win64, dest, data);
            }
        }
    }

    static void StopHelper()
    {
        foreach (Process p in Process.GetProcessesByName("CustomRadio"))
        {
            try { p.Kill(); p.WaitForExit(3000); Log("  stopped running CustomRadio helper"); } catch { }
        }
    }

    // ---------- install ----------
    public static void Install(Options o)
    {
        string win64 = Path.GetFullPath(o.Win64).TrimEnd('\\');
        if (!File.Exists(Path.Combine(win64, "SanAndreas.exe"))) throw new InvalidOperationException("SanAndreas.exe not found in " + win64);
        string app = Path.Combine(win64, "CustomRadio");
        backupRoot = Path.Combine(app, @"backup\" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Log("Game folder: " + win64);
        if (Process.GetProcessesByName("SanAndreas").Length > 0)
            Log("NOTE: the game is running. If a file is locked, close the game and run the installer again.");
        try
        {
            StopHelper();
            Directory.CreateDirectory(app);

            // 1. ASI loader
            if (o.HasLoader)
            {
                if (!LoaderPresent(win64)) throw new InvalidOperationException("You ticked \"I already have the ASI Loader\" but version.dll was not found next to SanAndreas.exe. Untick it and the installer will add it.");
                Log("[1/5] ASI Loader: using your existing version.dll");
            }
            else
            {
                Log("[1/5] Installing Ultimate ASI Loader (version.dll)...");
                ExtractZip(win64, Res("asi.zip"), delegate(string n) { return n.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase) ? "version.dll" : null; });
            }

            // 2. CLEO Redux
            if (o.HasCleo)
            {
                if (!File.Exists(Path.Combine(win64, "cleo_redux64.asi"))) throw new InvalidOperationException("You ticked \"I already have CLEO Redux\" but cleo_redux64.asi was not found. Untick it and the installer will add it.");
                Log("[2/5] CLEO Redux: using your existing install");
                if (!File.Exists(Path.Combine(win64, @"CLEO\CLEO_PLUGINS\IniFiles64.cleo")))
                {
                    Log("  the required IniFiles plugin is missing, adding it");
                    ExtractZip(win64, Res("cleo.zip"), delegate(string n) { return n.EndsWith("IniFiles64.cleo", StringComparison.OrdinalIgnoreCase) ? n : null; });
                }
            }
            else
            {
                Log("[2/5] Installing CLEO Redux 1.4.2 x64...");
                ExtractZip(win64, Res("cleo.zip"), delegate(string n) { return n; });
            }

            // 3. our files
            Log("[3/5] Installing Custom Radio...");
            Put(win64, Path.Combine(win64, @"CLEO\custom_radio[fs].js"), Res("custom_radio.js"));
            Put(win64, Path.Combine(app, "CustomRadio.exe"), Res("CustomRadio.exe"));
            Put(win64, Path.Combine(app, @"licenses\CLEO-Redux-LICENSE.txt"), Res("CLEO-Redux-LICENSE.txt"));
            Put(win64, Path.Combine(app, @"licenses\Ultimate-ASI-Loader-LICENSE.txt"), Res("Ultimate-ASI-Loader-LICENSE.txt"));
            string music = Path.Combine(app, "Music");
            Directory.CreateDirectory(music);

            // 4. music
            Log("[4/5] Music");
            if (!string.IsNullOrEmpty(o.MusicSrc) && Directory.Exists(o.MusicSrc))
            {
                int n = 0;
                foreach (string f in Directory.GetFiles(o.MusicSrc, "*.mp3"))
                {
                    string dest = Path.Combine(music, Path.GetFileName(f));
                    if (File.Exists(dest)) continue;
                    File.Copy(f, dest);
                    n++;
                }
                Log("  copied " + n + " mp3 file(s) from " + o.MusicSrc);
            }
            int total = Directory.GetFiles(music, "*.mp3").Length;
            Log("  " + total + " mp3 file(s) in " + music);
            if (total == 0) Log("  WARNING: no songs yet. Drop .mp3 files into that folder (name them 01 - ..., 02 - ... to set the order).");

            // 5. autostart + start
            Log("[5/5] Helper");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (o.Autostart) { k.SetValue(RunValue, "\"" + Path.Combine(app, "CustomRadio.exe") + "\""); Log("  will start with Windows"); }
                else { k.DeleteValue(RunValue, false); Log("  autostart is off (start CustomRadio.exe yourself before playing)"); }
            }
            if (o.StartNow)
            {
                Process.Start(new ProcessStartInfo(Path.Combine(app, "CustomRadio.exe")) { WorkingDirectory = app, UseShellExecute = false });
                Log("  helper started (runs hidden in the background)");
            }
            Log("DONE. In the game: set the radio to OFF, get in a car, music plays.");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Access denied (" + ex.Message + "). Close the game, or right-click easy_installer.exe and choose \"Run as administrator\".");
        }
    }

    // ---------- uninstall ----------
    public static void Uninstall(Options o)
    {
        string win64 = Path.GetFullPath(o.Win64).TrimEnd('\\');
        string app = Path.Combine(win64, "CustomRadio");
        Log("Game folder: " + win64);
        StopHelper();
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey)) { k.DeleteValue(RunValue, false); }
        Log("  removed autostart entry");
        string js = Path.Combine(win64, @"CLEO\custom_radio[fs].js");
        if (File.Exists(js)) { File.Delete(js); Log("  removed CLEO\\custom_radio[fs].js"); }
        if (Directory.Exists(app))
        {
            foreach (string f in Directory.GetFiles(app)) { try { File.Delete(f); } catch (Exception ex) { Log("  could not delete " + f + ": " + ex.Message); } }
            foreach (string d in new[] { "licenses", "backup" })
            {
                string p = Path.Combine(app, d);
                if (Directory.Exists(p)) try { Directory.Delete(p, true); } catch { }
            }
            string music = Path.Combine(app, "Music");
            if (Directory.Exists(music) && Directory.GetFileSystemEntries(music).Length == 0) Directory.Delete(music);
            if (Directory.Exists(music)) Log("  kept your songs in " + music + " (delete that folder yourself if you want)");
            else try { Directory.Delete(app); } catch { }
        }
        Log("DONE. CLEO Redux and the ASI Loader were left in place (other mods may use them).");
    }
}

class MainForm : Form
{
    TextBox txtGame, txtMusic, txtLog;
    CheckBox chkLoader, chkCleo, chkAuto, chkStart;
    Button btnInstall, btnUninstall, btnOpen, btnClose, btnGameBrowse, btnMusicBrowse;
    Label lblStatus;
    bool busy;

    public MainForm()
    {
        Text = "Custom Radio for GTA San Andreas: Definitive Edition - Easy Installer " + Core.Version;
        ClientSize = new Size(680, 540);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        int y = 12;
        Controls.Add(new Label { Text = "1. Game folder (contains SanAndreas.exe, or the folder above \"Gameface\"):", Left = 12, Top = y, Width = 650 });
        y += 22;
        txtGame = new TextBox { Left = 12, Top = y, Width = 560 };
        btnGameBrowse = new Button { Text = "Browse...", Left = 580, Top = y - 1, Width = 88 };
        Controls.Add(txtGame); Controls.Add(btnGameBrowse);
        y += 26;
        lblStatus = new Label { Left = 12, Top = y, Width = 650, Height = 18, ForeColor = Color.DimGray, Text = "Looking for the game..." };
        Controls.Add(lblStatus);
        y += 30;

        var gb = new GroupBox { Text = "2. Prerequisites (bundled in this installer - nothing to download)", Left = 12, Top = y, Width = 656, Height = 96 };
        chkLoader = new CheckBox { Text = "I already have the Ultimate ASI Loader (version.dll) - skip installing it", Left = 12, Top = 22, Width = 630 };
        chkCleo = new CheckBox { Text = "I already have CLEO Redux 1.4.2 x64 - skip installing it", Left = 12, Top = 46, Width = 630 };
        var hint = new Label { Text = "Leave a box UNticked and the installer installs that part for you. Ticks are set automatically when it is already found.", Left = 12, Top = 70, Width = 630, ForeColor = Color.DimGray };
        gb.Controls.Add(chkLoader); gb.Controls.Add(chkCleo); gb.Controls.Add(hint);
        Controls.Add(gb);
        y += 106;

        Controls.Add(new Label { Text = "3. Music (optional): copy the .mp3 files from this folder now", Left = 12, Top = y, Width = 650 });
        y += 22;
        txtMusic = new TextBox { Left = 12, Top = y, Width = 560 };
        btnMusicBrowse = new Button { Text = "Browse...", Left = 580, Top = y - 1, Width = 88 };
        Controls.Add(txtMusic); Controls.Add(btnMusicBrowse);
        y += 26;
        Controls.Add(new Label { Text = "You can also add songs later: drop .mp3 files into <game>\\...\\Win64\\CustomRadio\\Music (name them 01 - ..., 02 - ... to set the order).", Left = 12, Top = y, Width = 656, Height = 32, ForeColor = Color.DimGray });
        y += 38;

        chkAuto = new CheckBox { Text = "Start the radio helper with Windows (recommended)", Left = 12, Top = y, Width = 400, Checked = true };
        chkStart = new CheckBox { Text = "Start it now", Left = 430, Top = y, Width = 200, Checked = true };
        Controls.Add(chkAuto); Controls.Add(chkStart);
        y += 32;

        btnInstall = new Button { Text = "Install", Left = 12, Top = y, Width = 110, Height = 32 };
        btnUninstall = new Button { Text = "Uninstall", Left = 130, Top = y, Width = 110, Height = 32 };
        btnOpen = new Button { Text = "Open music folder", Left = 248, Top = y, Width = 140, Height = 32 };
        btnClose = new Button { Text = "Close", Left = 558, Top = y, Width = 110, Height = 32 };
        Controls.Add(btnInstall); Controls.Add(btnUninstall); Controls.Add(btnOpen); Controls.Add(btnClose);
        y += 42;

        txtLog = new TextBox { Left = 12, Top = y, Width = 656, Height = ClientSize.Height - y - 12, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = Color.White };
        Controls.Add(txtLog);

        btnGameBrowse.Click += delegate { Browse(txtGame, "Select the game folder"); };
        btnMusicBrowse.Click += delegate { Browse(txtMusic, "Select a folder with .mp3 files"); };
        btnClose.Click += delegate { Close(); };
        txtGame.TextChanged += delegate { RefreshGame(); };
        btnInstall.Click += delegate { Run(true); };
        btnUninstall.Click += delegate { Run(false); };
        btnOpen.Click += delegate
        {
            string w = Core.ResolveWin64(txtGame.Text);
            if (w == null) { MessageBox.Show(this, "Pick the game folder first.", Text); return; }
            string m = Path.Combine(w, @"CustomRadio\Music");
            Directory.CreateDirectory(m);
            Process.Start("explorer.exe", "\"" + m + "\"");
        };
        Shown += delegate { DetectAsync(); };
    }

    void Browse(TextBox target, string title)
    {
        using (var d = new FolderBrowserDialog { Description = title })
        {
            if (Directory.Exists(target.Text)) d.SelectedPath = target.Text;
            if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
        }
    }

    void DetectAsync()
    {
        var t = new Thread(delegate()
        {
            List<string> games = Core.FindGames();
            BeginInvoke((MethodInvoker)delegate
            {
                if (games.Count > 0 && string.IsNullOrEmpty(txtGame.Text)) txtGame.Text = games[0];
                else if (games.Count == 0 && string.IsNullOrEmpty(txtGame.Text)) lblStatus.Text = "Game not found automatically - click Browse and pick its folder.";
                if (games.Count > 1) AppendLog("Found " + games.Count + " installs, using the first:\r\n  " + string.Join("\r\n  ", games.ToArray()));
            });
        });
        t.IsBackground = true;
        t.Start();
    }

    void RefreshGame()
    {
        string w = Core.ResolveWin64(txtGame.Text);
        if (w == null)
        {
            lblStatus.ForeColor = Color.Firebrick;
            lblStatus.Text = string.IsNullOrEmpty(txtGame.Text) ? "" : "SanAndreas.exe not found in this folder.";
            return;
        }
        lblStatus.ForeColor = Color.SeaGreen;
        lblStatus.Text = "Found the game: " + w;
        chkLoader.Checked = Core.LoaderPresent(w);
        chkCleo.Checked = Core.CleoPresent(w);
    }

    void AppendLog(string s)
    {
        if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { AppendLog(s); }); return; }
        txtLog.AppendText(s + "\r\n");
    }

    void Run(bool install)
    {
        if (busy) return;
        string w = Core.ResolveWin64(txtGame.Text);
        if (w == null) { MessageBox.Show(this, "Could not find SanAndreas.exe. Pick the game folder first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!install && MessageBox.Show(this, "Remove Custom Radio? Your songs are kept.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var o = new Options { Win64 = w, HasLoader = chkLoader.Checked, HasCleo = chkCleo.Checked, Autostart = chkAuto.Checked, StartNow = chkStart.Checked, MusicSrc = txtMusic.Text.Trim() };
        busy = true; SetEnabled(false);
        txtLog.Clear();
        Core.Log = AppendLog;
        var t = new Thread(delegate()
        {
            string error = null;
            try { if (install) Core.Install(o); else Core.Uninstall(o); }
            catch (Exception ex) { error = ex.Message; }
            BeginInvoke((MethodInvoker)delegate
            {
                busy = false; SetEnabled(true);
                if (error != null) { AppendLog("ERROR: " + error); MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                else if (install)
                    MessageBox.Show(this, "Installed!\r\n\r\nIn the game: set the radio to OFF and get in a car.\r\n\r\nKeys (while driving):\r\n  Page Down / Page Up   next / previous song\r\n  Numpad 6 / Numpad 4   forward / back 10 s\r\n  Numpad 5              pause / play\r\n  Numpad + / -          volume\r\n\r\nGetting out of the car pauses the song; getting back in resumes it.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                else MessageBox.Show(this, "Custom Radio was removed.", Text);
            });
        });
        t.IsBackground = true;
        t.Start();
    }

    void SetEnabled(bool on)
    {
        foreach (Control c in new Control[] { btnInstall, btnUninstall, btnOpen, btnGameBrowse, btnMusicBrowse, txtGame, txtMusic, chkAuto, chkStart, chkLoader, chkCleo }) c.Enabled = on;
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var o = new Options();
        bool silent = false, uninstall = false;
        string gameArg = null;
        bool? skipAsi = null, skipCleo = null;
        foreach (string a in args)
        {
            string l = a.ToLowerInvariant();
            if (l == "/silent") silent = true;
            else if (l == "/uninstall") uninstall = true;
            else if (l == "/skipasi") skipAsi = true;
            else if (l == "/skipcleo") skipCleo = true;
            else if (l == "/noautostart") o.Autostart = false;
            else if (l == "/nostart") o.StartNow = false;
            else if (l.StartsWith("/gamedir:")) gameArg = a.Substring(9).Trim('"');
            else if (l.StartsWith("/music:")) o.MusicSrc = a.Substring(7).Trim('"');
            else if (l == "/?" || l == "/help")
            {
                MessageBox.Show("easy_installer.exe [/silent] [/uninstall] [/gamedir:\"path\"] [/skipasi] [/skipcleo] [/music:\"folder\"] [/noautostart] [/nostart]\r\n\r\nWithout /silent a window opens.", "Custom Radio installer");
                return 0;
            }
        }

        if (!silent)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        // silent mode: used for automated tests; log goes to %TEMP%
        string logPath = Path.Combine(Path.GetTempPath(), "custom_radio_installer.log");
        var sb = new StringBuilder();
        Core.Log = delegate(string s) { sb.AppendLine(s); };
        int code = 0;
        try
        {
            string w = Core.ResolveWin64(gameArg);
            if (w == null) { List<string> g = Core.FindGames(); w = g.FirstOrDefault(); }
            if (w == null) throw new InvalidOperationException("game not found; pass /gamedir:\"...\"");
            o.Win64 = w;
            o.HasLoader = skipAsi.HasValue ? skipAsi.Value : Core.LoaderPresent(w);
            o.HasCleo = skipCleo.HasValue ? skipCleo.Value : Core.CleoPresent(w);
            if (uninstall) Core.Uninstall(o); else Core.Install(o);
        }
        catch (Exception ex) { sb.AppendLine("ERROR: " + ex.Message); code = 1; }
        File.WriteAllText(logPath, sb.ToString());
        return code;
    }
}
