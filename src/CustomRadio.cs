// CustomRadio: hidden background MP3 player for GTA San Andreas: Definitive Edition.
// The CLEO script custom_radio[fs].js only reports "in car" + a heartbeat through radio_bridge.ini;
// everything that could crash the game (audio, hotkeys, files) lives here, outside the game process.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class CustomRadio
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPrivateProfileIntW(string sec, string key, int def, string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPrivateProfileStringW(string sec, string key, string def, StringBuilder ret, int len, string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool WritePrivateProfileStringW(string sec, string key, string val, string path);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_NUM4 = 0x64, VK_NUM5 = 0x65, VK_NUM6 = 0x66, VK_ADD = 0x6B, VK_SUB = 0x6D;
    static readonly int[] Keys = { VK_PRIOR, VK_NEXT, VK_NUM4, VK_NUM5, VK_NUM6, VK_ADD, VK_SUB };

    static string musicDir, bridgeIni, stateIni, logFile;
    static List<string> playlist = new List<string>();
    static int track, volume = 800, nowId;
    static int positionMs;
    static bool opened, playing, userPaused;
    static int lastBeat = int.MinValue; static DateTime lastBeatChange = DateTime.MinValue;
    static DateTime lastSave = DateTime.MinValue, lastProcCheck = DateTime.MinValue;
    static int gamePid;
    static readonly Dictionary<int, bool> keyDown = new Dictionary<int, bool>();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") { SelfTest(); return; }
        bool fresh;
        using (var mutex = new Mutex(true, "CustomRadio_SingleInstance", out fresh))
        {
            if (!fresh) return;
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            SetPaths(exeDir, "CustomRadio.log");
            try { File.WriteAllText(logFile, ""); } catch { }
            Log("start, music dir " + musicDir);
            LoadState();
            InitPlayer();
            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (s, e) => { try { Tick(); } catch (Exception ex) { Log("tick error: " + ex.Message); SafeClose(); } };
            timer.Start();
            Application.Run();
        }
    }

    // Plays every song silently through the real OpenTrack/Seek/Pause/Resume paths; writes selftest.log.
    static void SelfTest()
    {
        string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
        SetPaths(exeDir, "selftest.log");
        string tmp = Path.GetTempPath(); // keep the real resume position / bridge untouched
        bridgeIni = Path.Combine(tmp, "cr_selftest_bridge.ini");
        stateIni = Path.Combine(tmp, "cr_selftest_state.ini");
        File.WriteAllText(logFile, "");
        Rescan(); InitPlayer(); volume = 0;
        int i = 0, step = 0;
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (s, e) =>
        {
            try
            {
                if (i >= playlist.Count) { Log("SELFTEST DONE"); t.Stop(); Application.Exit(); return; }
                switch (step++)
                {
                    case 0: OpenTrack(i, 0); break;
                    case 1: Log("  pos=" + PosMs() + " len=" + LenMs() + " opened=" + opened); Seek(30000); break;
                    case 2: Log("  after +30s pos=" + PosMs()); Pause(); break;
                    case 3: int before = PosMs(); Resume(); Log("  paused at " + before + ", resumed"); break;
                    default: Log("  after resume pos=" + PosMs() + (PosMs() > 30000 ? "  OK" : "  FAIL")); i++; step = 0; break;
                }
            }
            catch (Exception ex) { Log("  EXCEPTION " + ex.Message); i++; step = 0; }
        };
        t.Start();
        Application.Run();
    }

    // Everything lives next to the exe: <game>\...\Win64\CustomRadio\{CustomRadio.exe, Music\, *.ini}
    static void SetPaths(string exeDir, string logName)
    {
        musicDir = Path.Combine(exeDir, "Music");
        try { Directory.CreateDirectory(musicDir); } catch { }
        bridgeIni = Path.Combine(exeDir, "radio_bridge.ini");
        stateIni = Path.Combine(exeDir, "radio_state.ini");
        logFile = Path.Combine(exeDir, logName);
    }

    static void Log(string msg)
    {
        try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss ") + msg + Environment.NewLine); } catch { }
    }

    // WPF MediaPlayer (Media Foundation) decodes every MP3 we have; old MCI choked on 5 of 7 files (error 277).
    static System.Windows.Media.MediaPlayer mp;
    static int failStreak;

    static void InitPlayer()
    {
        mp = new System.Windows.Media.MediaPlayer();
        mp.MediaEnded += (s, e) => { Log("ended " + playlist[track]); OpenTrack(track + 1, 0); };
        mp.MediaFailed += (s, e) =>
        {
            Log("cannot play " + (track < playlist.Count ? playlist[track] : "?") + ": " + e.ErrorException.Message);
            opened = playing = false;
            if (++failStreak < playlist.Count) OpenTrack(track + 1, 0); else Log("no playable files");
        };
        mp.MediaOpened += (s, e) => { failStreak = 0; };
    }

    static int PosMs() { return (int)mp.Position.TotalMilliseconds; }
    static int LenMs() { return mp.NaturalDuration.HasTimeSpan ? (int)mp.NaturalDuration.TimeSpan.TotalMilliseconds : -1; }

    static void Rescan()
    {
        playlist = Directory.Exists(musicDir)
            ? Directory.GetFiles(musicDir, "*.mp3").Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
    }

    static void LoadState()
    {
        Rescan();
        var sb = new StringBuilder(512);
        GetPrivateProfileStringW("Playback", "TrackFile", "", sb, sb.Capacity, stateIni);
        int idx = playlist.FindIndex(n => n.Equals(sb.ToString(), StringComparison.OrdinalIgnoreCase));
        track = idx >= 0 ? idx : 0;
        positionMs = idx >= 0 ? Math.Max(0, GetPrivateProfileIntW("Playback", "PositionMs", 0, stateIni)) : 0;
        volume = Clamp(GetPrivateProfileIntW("Playback", "Volume", 800, stateIni), 0, 1000);
        Log("playlist " + playlist.Count + " files; resume " + (playlist.Count > 0 ? playlist[track] : "-") + " @" + positionMs + "ms");
    }

    static void SaveState()
    {
        if (playlist.Count == 0) return;
        WritePrivateProfileStringW("Playback", "TrackFile", playlist[track], stateIni);
        WritePrivateProfileStringW("Playback", "PositionMs", positionMs.ToString(), stateIni);
        WritePrivateProfileStringW("Playback", "Volume", volume.ToString(), stateIni);
        lastSave = DateTime.Now;
    }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

    static void SafeClose()
    {
        if (opened) mp.Close();
        opened = playing = false;
    }

    static bool OpenTrack(int index, int startMs)
    {
        SafeClose();
        string current = playlist.Count > 0 && track < playlist.Count ? playlist[track] : null;
        Rescan(); // new songs dropped into the folder are picked up without restarting
        if (playlist.Count == 0) { Log("no mp3 files"); return false; }
        if (current != null && index == track) { int i = playlist.IndexOf(current); if (i >= 0) index = i; }
        track = ((index % playlist.Count) + playlist.Count) % playlist.Count;
        string path = Path.Combine(musicDir, playlist[track]);
        mp.Open(new Uri(path));
        opened = true;
        mp.Volume = volume / 1000.0;
        positionMs = Math.Max(0, startMs);
        mp.Position = TimeSpan.FromMilliseconds(positionMs);
        mp.Play();
        playing = true;
        nowId++;
        string title = Path.GetFileNameWithoutExtension(playlist[track]);
        title = System.Text.RegularExpressions.Regex.Replace(title, @"^\d{1,3}\s*[-._]\s*", "");
        WritePrivateProfileStringW("Now", "Title", title, bridgeIni);
        WritePrivateProfileStringW("Now", "Id", nowId.ToString(), bridgeIni);
        Log("play " + playlist[track] + " @" + positionMs);
        SaveState();
        return true;
    }

    static void Pause()
    {
        if (!opened || !playing) return;
        positionMs = PosMs();
        mp.Pause();
        playing = false;
        SaveState();
    }

    static void Resume()
    {
        if (!opened) { OpenTrack(track, positionMs); return; }
        if (playing) return;
        mp.Position = TimeSpan.FromMilliseconds(positionMs);
        mp.Play();
        playing = true;
    }

    static void Seek(int deltaMs)
    {
        if (!opened) return;
        int len = LenMs();
        positionMs = Clamp(PosMs() + deltaMs, 0, len > 0 ? Math.Max(0, len - 500) : int.MaxValue);
        mp.Position = TimeSpan.FromMilliseconds(positionMs);
        SaveState();
    }

    static bool GameRunning()
    {
        if ((DateTime.Now - lastProcCheck).TotalSeconds >= 2)
        {
            lastProcCheck = DateTime.Now;
            var p = Process.GetProcessesByName("SanAndreas").FirstOrDefault();
            gamePid = p != null ? p.Id : 0;
        }
        return gamePid != 0;
    }

    static bool GameForeground()
    {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        return pid == (uint)gamePid;
    }

    static bool Pressed(int vk)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool was; keyDown.TryGetValue(vk, out was);
        keyDown[vk] = down;
        return down && !was;
    }

    static void Tick()
    {
        if (!GameRunning())
        {
            if (opened) { Pause(); SafeClose(); Log("game closed; state saved"); }
            lastBeat = int.MinValue;
            return;
        }

        // The CLEO script bumps Beat every ~250 ms while the game simulation runs.
        // No change for 1.5 s => game paused / in menu / loading => stop the music too.
        int beat = GetPrivateProfileIntW("Game", "Beat", 0, bridgeIni);
        if (beat != lastBeat) { lastBeat = beat; lastBeatChange = DateTime.Now; }
        bool alive = (DateTime.Now - lastBeatChange).TotalMilliseconds < 1500;
        bool inCar = GetPrivateProfileIntW("Game", "InCar", 0, bridgeIni) == 1;
        bool focused = GameForeground();
        bool shouldPlay = alive && inCar && focused && !userPaused;

        if (shouldPlay && !playing) Resume();
        else if (!shouldPlay && playing) Pause();

        bool keysActive = alive && inCar && focused;
        foreach (int vk in Keys)
        {
            if (!Pressed(vk) || !keysActive) continue;
            switch (vk)
            {
                case VK_NEXT: userPaused = false; OpenTrack(track + 1, 0); break;
                case VK_PRIOR: userPaused = false; OpenTrack(track - 1, 0); break;
                case VK_NUM6: Seek(10000); break;
                case VK_NUM4: Seek(-10000); break;
                case VK_NUM5: userPaused = !userPaused; if (userPaused) Pause(); else Resume(); break;
                case VK_ADD: volume = Clamp(volume + 100, 0, 1000); if (opened) mp.Volume = volume / 1000.0; SaveState(); break;
                case VK_SUB: volume = Clamp(volume - 100, 0, 1000); if (opened) mp.Volume = volume / 1000.0; SaveState(); break;
            }
        }

        if (playing && opened)
        {
            positionMs = PosMs(); // track end is handled by the MediaEnded event
            if ((DateTime.Now - lastSave).TotalSeconds >= 2) SaveState();
        }
    }
}
