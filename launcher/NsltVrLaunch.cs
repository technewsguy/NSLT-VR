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
        Log("start, args: " + string.Join(" ", args));
        if (args.Length > 0 && args[0].EndsWith("nslt.exe", StringComparison.OrdinalIgnoreCase))
            return Run(args[0], args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "");   // old launch option: flat

        string exe = System.IO.Path.Combine(GameDir, "nslt.exe");
        try
        {
            if (!ConnectStartingSteamVR()) { Log("SteamVR unavailable; starting the game in VR directly"); return Run(exe, "-nsltvr"); }
            try
            {
                if (!OpenVR.Applications.IsApplicationInstalled(AppKey))
                {
                    string manifest = System.IO.Path.Combine(GameDir, @"BepInEx\plugins\NSLTVR\nsltvr.vrmanifest");
                    Log("registering " + manifest + ": " + OpenVR.Applications.AddApplicationManifest(manifest, false));
                }
                // Right after SteamVR starts it may not accept launches yet (IPCFailed): retry for up to ~15 s.
                var e = OpenVR.Applications.LaunchApplication(AppKey);
                for (int i = 0; i < 10 && e == EVRApplicationError.IPCFailed; i++)
                {
                    Thread.Sleep(1500);
                    e = OpenVR.Applications.LaunchApplication(AppKey);
                }
                Log("LaunchApplication: " + e);
                if (e != EVRApplicationError.None) return Run(exe, "-nsltvr");
            }
            finally { OpenVR.Shutdown(); }
            return 0;
        }
        catch (Exception ex)
        {
            Log("error: " + ex);
            return Run(exe, "-nsltvr");
        }
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

    static int Run(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments);
        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
        psi.UseShellExecute = false;
        Process.Start(psi);
        return 0;
    }
}
