// "New Super Lucky's Tale VR" desktop / Start menu shortcut.
// Starts SteamVR if it isn't running, then asks SteamVR to launch its "New Super Lucky's Tale VR" library entry
// (nsltvr.mod.game, which starts nslt.exe with -nsltvr). Launched by SteamVR itself, the game gets no "Resume Game"
// menu. If SteamVR can't be reached it starts the game in VR directly.
// Leftover use as a Steam launch option ("...nsltvr-launch.exe" %command%) just starts the game normally (flat).
using System;
using System.Diagnostics;
using System.Threading;
using Valve.VR;

static class NsltVrLaunch
{
    const string AppKey = "nsltvr.mod.game";
    static readonly string GameDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));

    static void Log(string s)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nsltvr-launch.log"), DateTime.Now.ToString("HH:mm:ss.f ") + s + "\r\n"); }
        catch (Exception) { }
    }

    static int Main(string[] args)
    {
        Log("start, args: " + string.Join(" ", args) + "  steam: " + Environment.GetEnvironmentVariable("SteamGameId"));
        // SteamVR's library entry points here with --noop: SteamVR "launching" it is what closes its dashboard. It stays
        // connected as the scene app until the game takes over, because with no scene app SteamVR brings its dashboard
        // back after ~10 s, and the game only connects at its title screen (~25 s).
        if (args.Length > 0 && args[0] == "--noop") return HoldUntilGameConnects();
        if (args.Length > 0 && args[0].EndsWith("nslt.exe", StringComparison.OrdinalIgnoreCase))
            return Run(args[0], args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "", false);   // old launch option: flat

        string exe = System.IO.Path.Combine(GameDir, "nslt.exe");

        // Gamepads (Steam Input: PS4/Xbox/etc.) only reach games that Steam started. If we weren't started by Steam
        // and the Steam library entry exists, hand over to it so Steam starts us again.
        bool fromSteam = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamGameId"));
        string gidFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_gameid.txt");
        if (!fromSteam && System.IO.File.Exists(gidFile))
        {
            string gid = System.IO.File.ReadAllText(gidFile).Trim();
            Log("handing over to the Steam entry " + gid);
            Process.Start("steam://rungameid/" + gid);
            return 0;
        }

        try
        {
            if (ConnectStartingSteamVR())
            {
                try
                {
                    if (!OpenVR.Applications.IsApplicationInstalled(AppKey))
                    {
                        string manifest = System.IO.Path.Combine(GameDir, @"BepInEx\plugins\NSLTVR\nsltvr.vrmanifest");
                        Log("registering " + manifest + ": " + OpenVR.Applications.AddApplicationManifest(manifest, false));
                    }
                    // SteamVR closes its dashboard when it launches an app. The entry just runs this launcher with --noop.
                    // Right after SteamVR starts it may not accept launches yet (IPCFailed): retry for up to ~15 s.
                    var e = OpenVR.Applications.LaunchApplication(AppKey);
                    for (int i = 0; i < 10 && e == EVRApplicationError.IPCFailed; i++)
                    {
                        Thread.Sleep(1500);
                        e = OpenVR.Applications.LaunchApplication(AppKey);
                    }
                    Log("LaunchApplication (closes the dashboard): " + e);
                }
                finally { OpenVR.Shutdown(); }
            }
            else Log("SteamVR unavailable");
        }
        catch (Exception ex) { Log("error: " + ex); }

        // Start the game ourselves and stay running until it exits, so Steam keeps treating it as the running game.
        return Run(exe, "-nsltvr", true);
    }

    // Connects to SteamVR as a background app, starting SteamVR through Steam first if it isn't running.
    static bool ConnectStartingSteamVR()
    {
        var err = EVRInitError.None;
        if (Process.GetProcessesByName("vrserver").Length > 0)
        {
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
            if (err == EVRInitError.None) return true;
        }
        Log("starting SteamVR");
        try { Process.Start("steam://rungameid/250820"); } catch (Exception) { return false; }
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(2000);
            if (Process.GetProcessesByName("vrserver").Length == 0) continue;
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
            if (err == EVRInitError.None) { Thread.Sleep(4000); return true; }   // let SteamVR finish starting up
        }
        return false;
    }

    static int HoldUntilGameConnects()
    {
        var err = EVRInitError.None;
        OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
        if (err != EVRInitError.None) { Log("hold: init failed " + err); return 0; }
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 180)
            {
                Thread.Sleep(500);
                var game = Process.GetProcessesByName("nslt");
                if (game.Length == 0 && sw.Elapsed.TotalSeconds > 60) { Log("hold: no game after 60 s"); break; }
                uint focus = OpenVR.Compositor != null ? OpenVR.Compositor.GetCurrentSceneFocusProcess() : 0;
                bool gameHasFocus = false;
                foreach (var p in game) if (p.Id == focus) gameHasFocus = true;
                if (gameHasFocus) { Log("hold: game connected after " + sw.Elapsed.TotalSeconds.ToString("0") + " s"); break; }
            }
        }
        finally { OpenVR.Shutdown(); }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static int Run(string exe, string arguments, bool wait)
    {
        var psi = new ProcessStartInfo(exe, arguments);
        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
        psi.UseShellExecute = false;
        if (wait)
        {
            // Started by Steam as the "New Super Lucky's Tale VR" shortcut, SteamAppId is that shortcut's id, so the
            // game's Steam login failed and it couldn't find its saves. It is New Super Lucky's Tale (1038300).
            psi.EnvironmentVariables["SteamAppId"] = "1038300";
            AllowSetForegroundWindow(-1);   // ASFW_ANY: let the game take the foreground
        }
        var p = Process.Start(psi);
        Log("started " + exe + " " + arguments);
        if (!wait || p == null) return 0;

        // Big Picture stays in front, and Steam sends the gamepad to whatever has focus: bring the game window forward.
        var sw = Stopwatch.StartNew();
        while (!p.HasExited && sw.Elapsed.TotalSeconds < 90)
        {
            Thread.Sleep(1000);
            p.Refresh();
            IntPtr h = p.MainWindowHandle;
            if (h == IntPtr.Zero) continue;
            if (GetForegroundWindow() == h) { Log("game window has focus after " + sw.Elapsed.TotalSeconds.ToString("0") + " s"); break; }
            ShowWindow(h, 9);   // SW_RESTORE
            SetForegroundWindow(h);
        }
        p.WaitForExit();
        return 0;
    }
}
