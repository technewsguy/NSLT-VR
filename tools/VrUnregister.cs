// Removes a .vrmanifest's apps from SteamVR's library. Usage: VrUnregister.exe <manifest path>
using System;
using Valve.VR;

static class VrUnregister
{
    static int Main(string[] args)
    {
        var err = EVRInitError.None;
        OpenVR.Init(ref err, EVRApplicationType.VRApplication_Utility);
        if (err != EVRInitError.None) { Console.WriteLine("SteamVR not reachable: " + err); return 1; }
        try { Console.WriteLine("Removed SteamVR library entry: " + OpenVR.Applications.RemoveApplicationManifest(args[0])); }
        finally { OpenVR.Shutdown(); }
        return 0;
    }
}
