// Lists the game in Steam's VR section (the VR tab of the SteamVR dashboard library) by marking its Steam launch option
// as a VR launch, as real VR games have it: appinfo/config/launch/N/type = "vr" in Steam's local copy of the game's
// store info (Steamppcacheppinfo.vdf). steamclient decides "VR game" from the launch option types (vr / othervr /
// openxr), so this survives Steam rewriting the cache. Steam reads that file at startup and rewrites it on exit, so it
// is only edited while Steam is closed. Steam replaces the game's entry whenever Valve updates the game's store info;
// the mod checks at every launch and re-applies it after Steam closes.
//
// appinfo.vdf (v28/v29): u32 magic, u32 universe, [v29: i64 offset of the key string table], then per app:
// u32 appid, u32 size, u32 info state, u32 last updated, u64 access token, 20-byte text SHA-1, u32 change number,
// 20-byte SHA-1 of the binary data, binary KeyValues data. appid 0 ends the list. In v29 every key is a u32 index into
// the string table at the end of the file. Steam checks the binary SHA-1, which is recomputed here.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

static class SteamVRTag
{
    const uint AppId = 1038300;
    const uint MagicV28 = 0x07564428, MagicV29 = 0x07564429;

    class Kv { public byte Type; public string Key; public byte[] Raw; public List<Kv> Kids; }

    public static string SteamPath()
    {
        try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; return v == null ? null : v.Replace('/', '\\'); }
        catch { return null; }
    }

    public static string AppInfoPath()
    {
        string test = Environment.GetEnvironmentVariable("NSLTVR_APPINFO");   // testing against a copy
        if (!string.IsNullOrEmpty(test)) return test;
        string s = SteamPath();
        return s == null ? null : Path.Combine(s, @"appcache\appinfo.vdf");
    }

    public static bool SteamRunning() { return Process.GetProcessesByName("steam").Length > 0; }

    // null = file or game entry not found / unreadable.
    public static bool? IsTagged()
    {
        try
        {
            var launches = new AppInfoFile(File.ReadAllBytes(AppInfoPath())).Launches();
            if (launches == null) return null;
            foreach (var l in launches) if (l.Kids != null && TypeOf(l.Kids) == "vr") return true;
            return false;
        }
        catch { return null; }
    }

    // Steam must be closed. Returns true if the file was changed.
    public static bool Apply(bool tag)
    {
        string path = AppInfoPath();
        if (path == null || !File.Exists(path)) return false;
        var f = new AppInfoFile(File.ReadAllBytes(path));
        var launches = f.Launches();
        if (launches == null) return false;
        bool changed = false;
        foreach (var l in launches)
        {
            if (l.Kids == null) continue;
            string t = TypeOf(l.Kids);
            if (tag && (t == null || t == "default")) { SetString(l.Kids, "type", "vr"); changed = true; }
            else if (!tag && t == "vr") { SetString(l.Kids, "type", "default"); changed = true; }
        }
        if (!changed) return false;
        byte[] data = f.Save();
        string backup = path + ".nsltvr-backup";
        if (!File.Exists(backup)) File.Copy(path, backup);
        string tmp = path + ".nsltvr-tmp";
        File.WriteAllBytes(tmp, data);
        File.Copy(tmp, path, true);
        File.Delete(tmp);
        return true;
    }

    // Closes Steam, applies, starts Steam again.
    public static bool ApplyWithSteamRestart(bool tag, Action<string> log, Action pump)
    {
        string steam = SteamPath();
        if (steam == null) return false;
        bool wasRunning = SteamRunning();
        if (wasRunning)
        {
            log("Closing Steam...");
            Process.Start(Path.Combine(steam, "steam.exe"), "-shutdown");
            var sw = Stopwatch.StartNew();
            while (SteamRunning())
            {
                if (sw.Elapsed.TotalSeconds > 90) { log("Steam didn't close."); return false; }
                System.Threading.Thread.Sleep(300);
                if (pump != null) pump();
            }
            System.Threading.Thread.Sleep(1500);
        }
        bool changed = false;
        try { changed = Apply(tag); }
        catch (Exception e) { log("Couldn't update Steam's game info: " + e.Message); }
        if (wasRunning) { log("Starting Steam..."); try { Process.Start(Path.Combine(steam, "steam.exe")); } catch { } }
        return changed;
    }

    // Background helper (started at every game launch, and by the installer): if the game isn't listed, waits until
    // Steam has exited, then lists it. One instance at a time.
    public static int WaitAndTag()
    {
        if (IsTagged() != false) return 0;
        bool created;
        using (var m = new System.Threading.Mutex(true, @"Local\NSLTVR-SteamVRTag", out created))
        {
            if (!created) return 0;
            while (SteamRunning()) System.Threading.Thread.Sleep(2000);
            System.Threading.Thread.Sleep(2000);
            try { Apply(true); } catch { return 1; }
            return 0;
        }
    }

    // Started through WMI so it is not a child of the caller: Steam would think the game is still running while it
    // waits, and PowerShell's Start-Process -Wait (Scoop installs) would wait for it too. Note: it doesn't inherit
    // the caller's environment.
    public static void StartWaiter(string exe)
    {
        try
        {
            string cmd = "\"" + exe + "\" /VRTAG-WAIT";
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"([wmiclass]'Win32_Process').Create('" + cmd.Replace("'", "''").Replace("\"", "\\\"") + "') | Out-Null\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (var p = Process.Start(psi)) p.WaitForExit(15000);
        }
        catch { }
    }

    // ------------------------------------------------------------ KeyValues helpers

    static Kv Find(List<Kv> list, string key)
    {
        foreach (var k in list) if (k.Key == key) return k;
        return null;
    }

    static string TypeOf(List<Kv> launch)
    {
        var e = Find(launch, "type");
        if (e == null || e.Type != 1 || e.Raw == null) return null;
        return Encoding.UTF8.GetString(e.Raw, 0, e.Raw.Length - 1);
    }

    static void SetString(List<Kv> list, string key, string v)
    {
        var e = Find(list, key);
        if (e == null) { e = new Kv { Key = key }; list.Add(e); }
        var b = Encoding.UTF8.GetBytes(v);
        e.Type = 1; e.Raw = new byte[b.Length + 1]; Buffer.BlockCopy(b, 0, e.Raw, 0, b.Length); e.Kids = null;
    }

    class AppInfoFile
    {
        readonly byte[] d;
        readonly uint magic;
        readonly long tableOffset;
        readonly List<string> strings = new List<string>();
        readonly Dictionary<string, int> index = new Dictionary<string, int>();
        readonly int entryPos, entrySize;
        readonly List<Kv> root;

        public AppInfoFile(byte[] data)
        {
            d = data;
            magic = BitConverter.ToUInt32(d, 0);
            if (magic != MagicV28 && magic != MagicV29) throw new InvalidDataException("unknown appinfo.vdf version " + magic.ToString("x8"));
            int pos = 8;
            if (magic == MagicV29)
            {
                tableOffset = BitConverter.ToInt64(d, 8);
                pos = 16;
                int count = BitConverter.ToInt32(d, (int)tableOffset), p = (int)tableOffset + 4;
                for (int i = 0; i < count; i++)
                {
                    int e = Array.IndexOf(d, (byte)0, p);
                    string s = Encoding.UTF8.GetString(d, p, e - p);
                    strings.Add(s);
                    if (!index.ContainsKey(s)) index[s] = i;
                    p = e + 1;
                }
            }
            entryPos = -1;
            while (true)
            {
                uint appid = BitConverter.ToUInt32(d, pos);
                if (appid == 0) break;
                int size = BitConverter.ToInt32(d, pos + 4);
                if (appid == AppId) { entryPos = pos; entrySize = size; break; }
                pos += 8 + size;
            }
            if (entryPos >= 0)
            {
                int p = entryPos + 8 + 60;
                root = ReadMap(ref p);
                if (p != entryPos + 8 + entrySize) throw new InvalidDataException("appinfo entry size mismatch");
            }
        }

        // appinfo/config/launch entries, or null.
        public List<Kv> Launches()
        {
            if (root == null) return null;
            var app = Find(root, "appinfo");
            var list = app != null && app.Kids != null ? app.Kids : root;
            var config = Find(list, "config");
            if (config == null || config.Kids == null) return null;
            var launch = Find(config.Kids, "launch");
            return launch == null ? null : launch.Kids;
        }

        string ReadKey(ref int p)
        {
            if (magic == MagicV29) { int i = BitConverter.ToInt32(d, p); p += 4; return strings[i]; }
            int e = Array.IndexOf(d, (byte)0, p);
            string s = Encoding.UTF8.GetString(d, p, e - p);
            p = e + 1;
            return s;
        }

        List<Kv> ReadMap(ref int p)
        {
            var list = new List<Kv>();
            while (true)
            {
                byte t = d[p++];
                if (t == 8) return list;
                var n = new Kv { Type = t, Key = ReadKey(ref p) };
                switch (t)
                {
                    case 0: n.Kids = ReadMap(ref p); break;
                    case 1: { int e = Array.IndexOf(d, (byte)0, p); n.Raw = Sub(p, e + 1 - p); p = e + 1; break; }
                    case 2: case 3: case 4: case 6: n.Raw = Sub(p, 4); p += 4; break;   // int32, float, pointer, color
                    case 7: case 10: n.Raw = Sub(p, 8); p += 8; break;                 // uint64, int64
                    default: throw new InvalidDataException("unknown KeyValues type " + t);
                }
                list.Add(n);
            }
        }

        byte[] Sub(int p, int n) { var b = new byte[n]; Buffer.BlockCopy(d, p, b, 0, n); return b; }

        void WriteMap(BinaryWriter w, List<Kv> list, List<string> added)
        {
            foreach (var n in list)
            {
                w.Write(n.Type);
                if (magic == MagicV29)
                {
                    int i;
                    if (!index.TryGetValue(n.Key, out i)) { i = strings.Count + added.Count; index[n.Key] = i; added.Add(n.Key); }
                    w.Write(i);
                }
                else { w.Write(Encoding.UTF8.GetBytes(n.Key)); w.Write((byte)0); }
                if (n.Type == 0) WriteMap(w, n.Kids, added); else w.Write(n.Raw);
            }
            w.Write((byte)8);
        }

        public byte[] Save()
        {
            var added = new List<string>();
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) WriteMap(w, root, added);
            byte[] blob = ms.ToArray();
            byte[] header = Sub(entryPos + 8, 60);
            using (var sha = SHA1.Create()) Buffer.BlockCopy(sha.ComputeHash(blob), 0, header, 40, 20);

            var o = new MemoryStream();
            int oldEnd = entryPos + 8 + entrySize;
            int restEnd = magic == MagicV29 ? (int)tableOffset : d.Length;
            o.Write(d, 0, entryPos + 4);
            o.Write(BitConverter.GetBytes(header.Length + blob.Length), 0, 4);
            o.Write(header, 0, header.Length);
            o.Write(blob, 0, blob.Length);
            o.Write(d, oldEnd, restEnd - oldEnd);
            if (magic == MagicV29)
            {
                long newTable = o.Position;
                o.Write(BitConverter.GetBytes(strings.Count + added.Count), 0, 4);
                o.Write(d, (int)tableOffset + 4, d.Length - (int)tableOffset - 4);
                foreach (var s in added) { var b = Encoding.UTF8.GetBytes(s); o.Write(b, 0, b.Length); o.WriteByte(0); }
                byte[] r = o.ToArray();
                Buffer.BlockCopy(BitConverter.GetBytes(newTable), 0, r, 8, 8);
                return r;
            }
            return o.ToArray();
        }
    }
}
