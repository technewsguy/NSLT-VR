// Adds (or updates) one non-Steam game in Steam's shortcuts.vdf, keeping every other entry byte-for-byte.
// Usage: SteamShortcut.exe <shortcuts.vdf> <AppName> <exe> <startDir> <icon> <launchOptions> <openvr 0|1>
// Prints the shortcut's 32-bit app id (used for the artwork file names in config\grid). Steam must be closed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

static class SteamShortcut
{
    class Node
    {
        public byte Type;          // 0 = map, 1 = string, 2 = int32, other = raw bytes we keep as-is
        public string Name;
        public string Str;
        public int Int;
        public byte[] Raw;
        public List<Node> Kids = new List<Node>();
        public Node Find(string n) { foreach (var k in Kids) if (string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase)) return k; return null; }
    }

    static string ReadZ(BinaryReader r)
    {
        var b = new List<byte>();
        for (byte c; (c = r.ReadByte()) != 0;) b.Add(c);
        return Encoding.UTF8.GetString(b.ToArray());
    }

    static void WriteZ(BinaryWriter w, string s) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); }

    static List<Node> ReadMap(BinaryReader r)
    {
        var list = new List<Node>();
        while (true)
        {
            byte t = r.ReadByte();
            if (t == 8) return list;
            var n = new Node { Type = t, Name = ReadZ(r) };
            switch (t)
            {
                case 0: n.Kids = ReadMap(r); break;
                case 1: n.Str = ReadZ(r); break;
                case 2: n.Int = r.ReadInt32(); break;
                case 3: n.Raw = r.ReadBytes(4); break;   // float
                case 7: n.Raw = r.ReadBytes(8); break;   // uint64
                default: throw new InvalidDataException("unknown VDF type " + t);
            }
            list.Add(n);
        }
    }

    static void WriteMap(BinaryWriter w, List<Node> list)
    {
        foreach (var n in list)
        {
            w.Write(n.Type);
            WriteZ(w, n.Name);
            switch (n.Type)
            {
                case 0: WriteMap(w, n.Kids); break;
                case 1: WriteZ(w, n.Str ?? ""); break;
                case 2: w.Write(n.Int); break;
                default: w.Write(n.Raw); break;
            }
        }
        w.Write((byte)8);
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    static void Set(Node e, byte type, string name, string s, int i)
    {
        var n = e.Find(name);
        if (n == null) { n = new Node { Type = type, Name = name }; e.Kids.Add(n); }
        n.Type = type; n.Str = s; n.Int = i;
    }

    static int Main(string[] a)
    {
        for (int k = 0; k < a.Length; k++) if (a[k] == "-") a[k] = "";   // "-" = empty (PowerShell drops empty args)
        string path = a[0], name = a[1], exe = "\"" + a[2] + "\"", start = "\"" + a[3] + "\"", icon = a[4], opts = a[5];
        int openvr = int.Parse(a[6]);

        var root = new List<Node>();
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            using (var r = new BinaryReader(File.OpenRead(path))) root = ReadMap(r);
        var shortcuts = root.Count > 0 ? root[0] : null;
        if (shortcuts == null) { shortcuts = new Node { Type = 0, Name = "shortcuts" }; root.Add(shortcuts); }

        Node entry = null;
        foreach (var e in shortcuts.Kids)
        {
            var an = e.Find("AppName") ?? e.Find("appname");
            if (an != null && string.Equals(an.Str, name, StringComparison.OrdinalIgnoreCase)) { entry = e; break; }
        }
        uint appid = Crc32(Encoding.UTF8.GetBytes(exe + name)) | 0x80000000;
        if (entry == null)
        {
            entry = new Node { Type = 0, Name = shortcuts.Kids.Count.ToString() };
            shortcuts.Kids.Add(entry);
            Set(entry, 2, "appid", null, unchecked((int)appid));
        }
        else
        {
            var id = entry.Find("appid");
            if (id != null) appid = unchecked((uint)id.Int); else Set(entry, 2, "appid", null, unchecked((int)appid));
        }
        Set(entry, 1, "AppName", name, 0);
        Set(entry, 1, "Exe", exe, 0);
        Set(entry, 1, "StartDir", start, 0);
        Set(entry, 1, "icon", icon, 0);
        if (entry.Find("ShortcutPath") == null) Set(entry, 1, "ShortcutPath", "", 0);
        Set(entry, 1, "LaunchOptions", opts, 0);
        if (entry.Find("IsHidden") == null) Set(entry, 2, "IsHidden", null, 0);
        if (entry.Find("AllowDesktopConfig") == null) Set(entry, 2, "AllowDesktopConfig", null, 1);
        if (entry.Find("AllowOverlay") == null) Set(entry, 2, "AllowOverlay", null, 1);
        Set(entry, 2, "OpenVR", null, openvr);
        if (entry.Find("Devkit") == null) Set(entry, 2, "Devkit", null, 0);
        if (entry.Find("DevkitGameID") == null) Set(entry, 1, "DevkitGameID", "", 0);
        if (entry.Find("DevkitOverrideAppID") == null) Set(entry, 2, "DevkitOverrideAppID", null, 0);
        if (entry.Find("LastPlayTime") == null) Set(entry, 2, "LastPlayTime", null, 0);
        if (entry.Find("tags") == null) entry.Kids.Add(new Node { Type = 0, Name = "tags" });

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) WriteMap(w, root);
        File.WriteAllBytes(path, ms.ToArray());
        Console.WriteLine(appid);
        return 0;
    }
}
