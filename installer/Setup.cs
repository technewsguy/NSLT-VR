// NSLT-VR installer and uninstaller: one self-contained exe. The mod files are embedded as resources named
// "payload/<path inside the game folder>" (see build.ps1).
//
// Double-click: finds New Super Lucky's Tale in every Steam library (Windows Steam, or Linux Steam when the exe is run
// through Proton/Wine, where the Linux file system is drive Z:), installs, and puts "NSLT-VR Uninstall.exe" (a copy of
// this exe) in the game folder. After that, Steam's normal "New Super Lucky's Tale" entry plays in VR.
//
// Command line (Scoop etc.): /S = silent (log in %TEMP%\nsltvr-setup.log), /GAMEDIR=<folder>, /UNINSTALL.
// /VRTAG-WAIT = background helper (started by the mod): lists the game in Steam's VR section after Steam closes.
// Exit code 0 = success.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static partial class NsltVrSetup
{
    const string GameName = "New Super Lucky's Tale";
    const string SteamAppId = "1038300";
    const string GameExe = "nslt.exe";
    const string DefaultInstallDir = "New Super Luckys Tale";
    const string UninstallerName = "NSLT-VR Uninstall.exe";
    const string PluginRel = @"BepInEx\plugins\NSLTVR";


    internal static Action<string> Log = s => { };
    static bool Silent;

    // ---------------------------------------------------------------- entry point

    [STAThread]
    static int Main(string[] args)
    {
        string gameDir = null;
        bool uninstall = Path.GetFileName(SelfPath).IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
        foreach (var a in args)
        {
            if (a.Equals("/S", StringComparison.OrdinalIgnoreCase)) Silent = true;
            else if (a.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase)) uninstall = true;
            else if (a.Equals("/VRTAG-WAIT", StringComparison.OrdinalIgnoreCase)) return SteamVRTag.WaitAndTag();
            else if (a.StartsWith("/GAMEDIR=", StringComparison.OrdinalIgnoreCase)) gameDir = a.Substring(9).Trim('"');
        }
        if (uninstall && gameDir == null && File.Exists(Path.Combine(Path.GetDirectoryName(SelfPath), GameExe)))
            gameDir = Path.GetDirectoryName(SelfPath);

        if (Silent) return RunSilent(gameDir, uninstall);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(gameDir, uninstall));
        return 0;
    }

    static int RunSilent(string gameDir, bool uninstall)
    {
        string logPath = Path.Combine(Path.GetTempPath(), "nsltvr-setup.log");
        var sb = new StringBuilder();
        Log = s => sb.AppendLine(s);
        int code = 0;
        try
        {
            if (gameDir == null)
            {
                var found = FindGames();
                if (found.Count == 0) throw new Exception("Couldn't find " + GameName + ". Pass /GAMEDIR=<game folder>.");
                gameDir = found[0];
            }
            if (uninstall) Uninstall(gameDir); else Install(gameDir);
        }
        catch (Exception e) { Log("ERROR: " + e.Message); code = 1; }
        try { File.WriteAllText(logPath, sb.ToString()); } catch { }
        Console.Write(sb.ToString());
        return code;
    }

    static string SelfPath { get { return Assembly.GetExecutingAssembly().Location; } }

    // ---------------------------------------------------------------- Wine / Proton

    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

    static bool? _wine;
    internal static bool IsWine
    {
        get
        {
            if (_wine == null)
            {
                try { var h = GetModuleHandle("ntdll.dll"); _wine = h != IntPtr.Zero && GetProcAddress(h, "wine_get_version") != IntPtr.Zero; }
                catch { _wine = false; }
            }
            return _wine.Value;
        }
    }

    // "/home/me/.steam" -> "Z:\home\me\.steam" (Wine maps the Linux root to Z:). Windows paths pass through.
    static string FromUnix(string p)
    {
        if (string.IsNullOrEmpty(p) || !p.StartsWith("/")) return p;
        return "Z:" + p.Replace('/', '\\');
    }

    // ---------------------------------------------------------------- finding the game

    static List<string> SteamRoots()
    {
        var roots = new List<string>();
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
            foreach (var name in new[] { "SteamPath", "InstallPath" })
            {
                try { var v = Registry.GetValue(key, name, null) as string; if (!string.IsNullOrEmpty(v)) roots.Add(v.Replace('/', '\\')); } catch { }
            }
        if (IsWine)
        {
            var homes = new List<string>();
            string home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home) && home.StartsWith("/")) homes.Add(FromUnix(home));
            try { if (Directory.Exists(@"Z:\home")) homes.AddRange(Directory.GetDirectories(@"Z:\home")); } catch { }
            foreach (var h in homes)
                foreach (var rel in new[] { @".local\share\Steam", @".steam\steam", @".steam\root",
                                            @".var\app\com.valvesoftware.Steam\.local\share\Steam", @".var\app\com.valvesoftware.Steam\.steam\steam" })
                    roots.Add(Path.Combine(h, rel));
        }
        var result = new List<string>();
        foreach (var r in roots)
        {
            try
            {
                if (!Directory.Exists(Path.Combine(r, "steamapps"))) continue;
                string full = Path.GetFullPath(r).TrimEnd('\\');
                if (!result.Exists(x => x.Equals(full, StringComparison.OrdinalIgnoreCase))) result.Add(full);
            }
            catch { }
        }
        return result;
    }

    internal static List<string> FindGames()
    {
        var libs = new List<string>();
        foreach (var root in SteamRoots())
        {
            libs.Add(root);
            try
            {
                string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(FromUnix(m.Groups[1].Value.Replace("\\\\", "\\")));
            }
            catch { }
        }
        var games = new List<string>();
        foreach (var lib in libs)
        {
            try
            {
                string installDir = DefaultInstallDir;
                string acf = Path.Combine(lib, @"steamapps\appmanifest_" + SteamAppId + ".acf");
                if (File.Exists(acf))
                {
                    var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (m.Success) installDir = m.Groups[1].Value;
                }
                string dir = Path.GetFullPath(Path.Combine(lib, @"steamapps\common\" + installDir));
                if (File.Exists(Path.Combine(dir, GameExe)) && !games.Exists(g => g.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                    games.Add(dir);
            }
            catch { }
        }
        return games;
    }

    internal static bool IsInstalled(string gameDir)
    {
        return !string.IsNullOrEmpty(gameDir) && File.Exists(Path.Combine(gameDir, PluginRel + @"\NSLTVR.dll"));
    }

    // ---------------------------------------------------------------- install

    internal static void Install(string gameDir)
    {
        CheckGameDir(gameDir);
        Log("Installing NSLT-VR " + ModVersion + " into " + gameDir);

        // This game's Mono is too old for BepInEx 6: move an existing BepInEx 6 aside (the uninstaller restores it).
        if (File.Exists(Path.Combine(gameDir, @"BepInEx\core\BepInEx.Unity.Mono.dll")))
        {
            string bk = Path.Combine(gameDir, "_BepInEx6_backup");
            Directory.CreateDirectory(bk);
            Directory.Move(Path.Combine(gameDir, "BepInEx"), Path.Combine(bk, "BepInEx"));
            foreach (var f in new[] { "doorstop_config.ini", "winhttp.dll", ".doorstop_version" })
                if (File.Exists(Path.Combine(gameDir, f))) File.Move(Path.Combine(gameDir, f), Path.Combine(bk, f));
            Log("Moved the existing BepInEx 6 to _BepInEx6_backup.");
        }

        RemoveOldVersionLeftovers(gameDir, false);

        var asm = Assembly.GetExecutingAssembly();
        int n = 0;
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("payload/")) continue;
            string dest = Path.Combine(gameDir, res.Substring(8).Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            using (var s = asm.GetManifestResourceStream(res))
            using (var o = File.Create(dest)) s.CopyTo(o);
            n++;
        }
        Log("Copied " + n + " files (BepInEx 5 mod loader, NSLT-VR plugin, openvr_api.dll).");

        PatchGlobalGameManagers(Path.Combine(gameDir, @"nslt_Data\globalgamemanagers"));

        string uninst = Path.Combine(gameDir, UninstallerName);
        if (!string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(SelfPath, uninst, true);
            Log("Uninstaller: " + uninst);
        }

        if (!IsWine) ListInSteamVRSection(uninst);

        Log("");
        Log("Done. Start " + GameName + " from Steam as usual: it plays in VR (SteamVR starts by itself).");
        if (IsWine)
        {
            Log("");
            Log("Linux / Steam Deck: one more step. In Steam, right-click " + GameName + " > Properties > Launch Options, and enter:");
            Log("    " + NsltVrSetupLinux.LaunchOption);
            Log("(Without it, Proton doesn't load the mod and the game starts flat.)");
        }
    }

    // Steam's VR section: see SteamVRTag.cs. Needs Steam closed; if it's running, ask to restart it, or let the
    // background helper (the uninstaller copy with /VRTAG-WAIT) do it after Steam next closes.
    static void ListInSteamVRSection(string helperExe)
    {
        bool? tagged = SteamVRTag.IsTagged();
        if (tagged == null) { Log("Couldn't read Steam's info for the game, so it isn't listed in Steam's VR section."); return; }
        if (tagged == true) { Log("Listed in Steam's VR section."); return; }
        try
        {
            if (!SteamVRTag.SteamRunning())
            {
                if (SteamVRTag.Apply(true)) Log("Listed in Steam's VR section.");
                return;
            }
            if (!Silent && MessageBox.Show("To list " + GameName + " in Steam's VR section, Steam has to close and reopen once.\r\n\r\nRestart Steam now?\r\n" +
                    "(If you choose No, it's listed the next time Steam restarts.)", "NSLT-VR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                if (SteamVRTag.ApplyWithSteamRestart(true, Log, Application.DoEvents)) Log("Listed in Steam's VR section.");
                return;
            }
        }
        catch (Exception e) { Log("Couldn't list the game in Steam's VR section: " + e.Message); return; }
        SteamVRTag.StartWaiter(helperExe);
        Log("It appears in Steam's VR section after Steam next restarts.");
    }

    static void UnlistFromSteamVRSection()
    {
        // Stop a waiting helper first: it would list the game again, and it keeps the uninstaller file in use.
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UninstallerName)))
            try { if (p.Id != Process.GetCurrentProcess().Id) p.Kill(); } catch { }
        if (SteamVRTag.IsTagged() != true) return;
        try
        {
            if (!SteamVRTag.SteamRunning()) { if (SteamVRTag.Apply(false)) Log("Removed from Steam's VR section."); return; }
            if (!Silent && MessageBox.Show("To remove " + GameName + " from Steam's VR section, Steam has to close and reopen once.\r\n\r\nRestart Steam now?",
                    "NSLT-VR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                if (SteamVRTag.ApplyWithSteamRestart(false, Log, Application.DoEvents)) Log("Removed from Steam's VR section.");
                return;
            }
            Log("Note: the game stays in Steam's VR section (run the uninstaller with Steam closed to remove it).");
        }
        catch (Exception e) { Log("Couldn't remove the game from Steam's VR section: " + e.Message); }
    }

    static void CheckGameDir(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir) || !File.Exists(Path.Combine(gameDir, GameExe)))
            throw new Exception("That folder doesn't contain " + GameExe + ". Pick the " + GameName + " folder (steamapps\\common\\" + DefaultInstallDir + ").");
        if (Process.GetProcessesByName("nslt").Length > 0) throw new Exception(GameName + " is running. Close it first.");
    }

    // GgmPatcher (patcher\GgmPatcher.cs, compiled into this exe) lists OpenVR in the game's VR device list.
    static void PatchGlobalGameManagers(string path)
    {
        var m = typeof(GgmPatcher).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var outText = new StringWriter();
        var old = Console.Out;
        Console.SetOut(outText);
        int code;
        try { code = (int)m.Invoke(null, new object[] { new[] { path, "--write" } }); }
        finally { Console.SetOut(old); }
        if (code != 0) throw new Exception("Patching globalgamemanagers failed:\r\n" + outText);
        Log(outText.ToString().Contains("Already patched") ? "globalgamemanagers already patched." : "Patched globalgamemanagers (backup: globalgamemanagers.nsltvr-backup).");
    }

    // ---------------------------------------------------------------- uninstall

    internal static void Uninstall(string gameDir)
    {
        CheckGameDir(gameDir);
        Log("Removing NSLT-VR from " + gameDir);
        RemoveOldVersionLeftovers(gameDir, true);
        if (!IsWine) UnlistFromSteamVRSection();

        string ggm = Path.Combine(gameDir, @"nslt_Data\globalgamemanagers");
        if (File.Exists(ggm + ".nsltvr-backup"))
        {
            File.Copy(ggm + ".nsltvr-backup", ggm, true);
            File.Delete(ggm + ".nsltvr-backup");
            Log("Restored globalgamemanagers.");
        }
        TryDeleteFile(Path.Combine(gameDir, @"nslt_Data\Plugins\openvr_api.dll"));
        TryDeleteDir(Path.Combine(gameDir, PluginRel));

        string bep = Path.Combine(gameDir, "BepInEx");
        string bk6 = Path.Combine(gameDir, "_BepInEx6_backup");
        string[] loaderFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version" };
        if (Directory.Exists(Path.Combine(bk6, "BepInEx")))
        {
            TryDeleteDir(bep);
            foreach (var f in loaderFiles) TryDeleteFile(Path.Combine(gameDir, f));
            foreach (var e in Directory.GetFileSystemEntries(bk6))
            {
                string dest = Path.Combine(gameDir, Path.GetFileName(e));
                if (Directory.Exists(e)) Directory.Move(e, dest); else File.Move(e, dest);
            }
            Directory.Delete(bk6);
            Log("Restored the BepInEx 6 that was there before.");
        }
        else if (!OtherPluginsPresent(bep))
        {
            TryDeleteDir(bep);
            foreach (var f in loaderFiles) TryDeleteFile(Path.Combine(gameDir, f));
            Log("Removed the BepInEx mod loader.");
        }
        else Log("Kept the BepInEx mod loader: other mods use it.");

        string uninst = Path.Combine(gameDir, UninstallerName);
        if (string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
        {
            // A running exe can't delete itself: a short-lived cmd does it a moment after we exit.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del \"" + uninst + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }
        else TryDeleteFile(uninst);

        Log("");
        Log("Done. " + GameName + " is back to the normal flat game.");
    }

    static bool OtherPluginsPresent(string bep)
    {
        string plugins = Path.Combine(bep, "plugins");
        if (Directory.Exists(plugins) && Directory.GetFileSystemEntries(plugins).Length > 0) return true;
        string patchers = Path.Combine(bep, "patchers");
        return Directory.Exists(patchers) && Directory.GetFileSystemEntries(patchers).Length > 0;
    }

    // ---------------------------------------------------------------- leftovers of v1.0.x (separate VR entry)

    // Up to v1.0.2 VR was a separate "New Super Lucky's Tale VR" entry: desktop/Start menu shortcuts and a launcher
    // folder, an entry in SteamVR's library, and optionally a non-Steam game in Steam. Now Steam's normal entry plays
    // in VR, so all of that goes. (The optional non-Steam game entry is left alone.)
    static void RemoveOldVersionLeftovers(string gameDir, bool uninstalling)
    {
        TryDeleteDir(Path.Combine(gameDir, "NSLTVR-launcher"));
        string appIdFile = Path.Combine(gameDir, "steam_appid.txt");   // written by v1.0.x so SteamVR could start the game
        try { if (File.Exists(appIdFile) && File.ReadAllText(appIdFile).Trim() == SteamAppId) File.Delete(appIdFile); } catch { }
        if (!IsWine)
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
                TryDeleteFile(Path.Combine(dir, GameName + " VR.lnk"));

        // SteamVR library entry. SteamVR keeps its list in Steam\config\appconfig.json and rewrites that file when it
        // exits, so it can only be edited while SteamVR is closed. Otherwise the mod removes the entry itself the next
        // time the game runs in VR (it needs the manifest file for that, so it stays when installing).
        string manifest = Path.Combine(gameDir, PluginRel + @"\nsltvr.vrmanifest");
        if (File.Exists(manifest))
        {
            if (Process.GetProcessesByName("vrserver").Length == 0)
            {
                if (RemoveFromSteamVRConfig(manifest)) Log("Removed the old '" + GameName + " VR' entry from SteamVR's library.");
                foreach (var f in new[] { "nsltvr.vrmanifest", "nsltvr_header.jpg", "nsltvr_capsule.jpg" })
                    TryDeleteFile(Path.Combine(gameDir, PluginRel + @"\" + f));
            }
            else if (uninstalling)
                Log("Note: SteamVR is running, so the old '" + GameName + " VR' entry stays in SteamVR's library. Close SteamVR and run the uninstaller again to remove it.");
        }

    }

    static string WindowsSteamPath()
    {
        try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; return v == null ? null : v.Replace('/', '\\'); }
        catch { return null; }
    }

    static bool RemoveFromSteamVRConfig(string manifest)
    {
        try
        {
            string steam = WindowsSteamPath();
            if (steam == null) return false;
            string cfg = Path.Combine(steam, @"config\appconfig.json");
            if (!File.Exists(cfg)) return false;
            string text = File.ReadAllText(cfg);
            var arr = Regex.Match(text, "(\"manifest_paths\"\\s*:\\s*\\[)(.*?)(\\])", RegexOptions.Singleline);
            if (!arr.Success) return false;
            var keep = new List<string>();
            bool removed = false;
            string want = Path.GetFullPath(manifest);
            foreach (Match e in Regex.Matches(arr.Groups[2].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string p = e.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                bool same;
                try { same = string.Equals(Path.GetFullPath(p), want, StringComparison.OrdinalIgnoreCase); } catch { same = false; }
                if (same) removed = true; else keep.Add(e.Value);
            }
            if (!removed) return false;
            if (!File.Exists(cfg + ".nsltvr-backup")) File.Copy(cfg, cfg + ".nsltvr-backup");
            string body = keep.Count == 0 ? "" : "\n      " + string.Join(",\n      ", keep.ToArray()) + "\n   ";
            File.WriteAllText(cfg, text.Substring(0, arr.Index) + arr.Groups[1].Value + body + arr.Groups[3].Value + text.Substring(arr.Index + arr.Length));
            return true;
        }
        catch (Exception e) { Log("Couldn't edit SteamVR's app list: " + e.Message); return false; }
    }

    static void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (Exception e) { Log("Couldn't delete " + p + ": " + e.Message); } }
    static void TryDeleteDir(string p) { try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch (Exception e) { Log("Couldn't delete " + p + ": " + e.Message); } }
}

// ---------------------------------------------------------------- window

class SetupForm : Form
{
    readonly ComboBox _dir = new ComboBox();
    readonly TextBox _log = new TextBox();
    readonly Button _install = new Button(), _uninstall = new Button(), _close = new Button(), _browse = new Button(), _copy = new Button();

    public SetupForm(string gameDir, bool uninstallMode)
    {
        Text = "NSLT-VR " + NsltVrSetup.ModVersion + (uninstallMode ? " - Uninstall" : " - Setup");
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 400);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

        var title = new Label { Text = "VR mod for New Super Lucky's Tale (Steam)", Font = new Font("Segoe UI", 12f, FontStyle.Bold), AutoSize = true, Location = new Point(12, 10) };
        var lbl = new Label { Text = "Game folder:", AutoSize = true, Location = new Point(12, 48) };
        _dir.Location = new Point(12, 68); _dir.Width = 500; _dir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _browse.Text = "Browse..."; _browse.Location = new Point(520, 67); _browse.Width = 88; _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical; _log.BackColor = SystemColors.Window;
        _log.Location = new Point(12, 104); _log.Size = new Size(596, 244); _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _install.Text = "Install"; _uninstall.Text = "Uninstall"; _close.Text = "Close"; _copy.Text = "Copy launch option";
        foreach (var b in new[] { _install, _uninstall, _close }) { b.Width = 96; b.Height = 30; b.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; }
        _close.Location = new Point(512, 358); _uninstall.Location = new Point(408, 358); _install.Location = new Point(304, 358);
        _copy.Width = 150; _copy.Height = 30; _copy.Location = new Point(12, 358); _copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; _copy.Visible = false;
        Controls.AddRange(new Control[] { title, lbl, _dir, _browse, _log, _install, _uninstall, _close, _copy });

        NsltVrSetup.Log = s => { _log.AppendText(s + "\r\n"); _log.Update(); };
        _browse.Click += (s, e) =>
        {
            using (var d = new FolderBrowserDialog { Description = "Pick the New Super Lucky's Tale folder (the one with nslt.exe)" })
                if (d.ShowDialog(this) == DialogResult.OK) { _dir.Text = d.SelectedPath; UpdateButtons(); }
        };
        _dir.TextChanged += (s, e) => UpdateButtons();
        _close.Click += (s, e) => Close();
        _copy.Click += (s, e) => { try { Clipboard.SetText(NsltVrSetupLinux.LaunchOption); } catch { } };
        _install.Click += (s, e) => Run(() => NsltVrSetup.Install(_dir.Text.Trim()), true);
        _uninstall.Click += (s, e) => Run(() => NsltVrSetup.Uninstall(_dir.Text.Trim()), false);

        var found = NsltVrSetup.FindGames();
        foreach (var g in found) _dir.Items.Add(g);
        if (!string.IsNullOrEmpty(gameDir)) _dir.Text = gameDir;
        else if (found.Count > 0) _dir.SelectedIndex = 0;

        if (uninstallMode) NsltVrSetup.Log("Click Uninstall to remove NSLT-VR from this game folder.");
        else if (found.Count == 0) NsltVrSetup.Log("Couldn't find New Super Lucky's Tale in your Steam libraries. Click Browse and pick the game folder (the one with nslt.exe).");
        else
        {
            NsltVrSetup.Log("Found New Super Lucky's Tale" + (found.Count > 1 ? " (" + found.Count + " copies; pick one above)" : "") + ".");
            NsltVrSetup.Log("Click Install. Afterwards, Steam's normal New Super Lucky's Tale entry plays in VR.");
        }
        if (NsltVrSetup.IsWine) NsltVrSetup.Log("(Running under Proton/Wine.)");
        UpdateButtons();
        AcceptButton = uninstallMode ? _uninstall : _install;
    }

    void UpdateButtons()
    {
        string d = _dir.Text.Trim();
        bool hasGame = d.Length > 0 && File.Exists(Path.Combine(d, "nslt.exe"));
        _install.Enabled = hasGame;
        _install.Text = NsltVrSetup.IsInstalled(d) ? "Reinstall" : "Install";
        _uninstall.Enabled = hasGame && NsltVrSetup.IsInstalled(d);
    }

    void Run(Action action, bool installing)
    {
        _install.Enabled = _uninstall.Enabled = _browse.Enabled = _dir.Enabled = false;
        _log.Clear();
        Cursor = Cursors.WaitCursor;
        try
        {
            action();
            if (installing && NsltVrSetup.IsWine) _copy.Visible = true;
        }
        catch (Exception e) { NsltVrSetup.Log(""); NsltVrSetup.Log("Failed: " + e.Message); }
        finally { Cursor = Cursors.Default; _browse.Enabled = _dir.Enabled = true; UpdateButtons(); }
    }
}

static class NsltVrSetupLinux { public const string LaunchOption = "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"; }
