// NSLT-VR globalgamemanagers patcher
// Adds VR devices to BuildSettings.enabledVRDevices in a Unity 2017.4 serialized file (format v17, no type tree).
// Usage: GgmPatcher.exe <path-to-globalgamemanagers> [--write]
//   Without --write it only parses and prints what it finds (dry run).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

static class GgmPatcher
{
    const int BuildSettingsClassId = 141;
    static readonly string[] DevicesToEnable = { "None", "OpenVR" };   // like VookaRaylee: start flat, mod loads OpenVR

    static uint ReadU32BE(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
    static void WriteU32BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static int Align4(int x) { return (x + 3) & ~3; }

    class ObjInfo { public long PathId; public int EntryOffset; public uint ByteStart; public uint ByteSize; public int TypeIndex; }

    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: GgmPatcher <globalgamemanagers> [--write]"); return 2; }
        string path = args[0];
        bool write = args.Length > 1 && args[1] == "--write";
        byte[] f = File.ReadAllBytes(path);

        uint metaSize = ReadU32BE(f, 0);
        uint fileSize = ReadU32BE(f, 4);
        uint version = ReadU32BE(f, 8);
        uint dataOffset = ReadU32BE(f, 12);
        byte endian = f[16];
        Console.WriteLine("metaSize={0} fileSize={1} version={2} dataOffset={3} bigEndian={4}", metaSize, fileSize, version, dataOffset, endian);
        if (version != 17 || endian != 0 || fileSize != f.Length) { Console.WriteLine("Unexpected header; aborting."); return 1; }

        var r = new BinaryReader(new MemoryStream(f));
        r.BaseStream.Position = 20;
        var sb = new StringBuilder(); byte c; while ((c = r.ReadByte()) != 0) sb.Append((char)c);
        string unityVersion = sb.ToString();
        int platform = r.ReadInt32();
        bool typeTree = r.ReadBoolean();
        Console.WriteLine("unity={0} platform={1} typeTree={2}", unityVersion, platform, typeTree);
        if (typeTree) { Console.WriteLine("File has a type tree; this simple patcher does not handle that."); return 1; }

        int typeCount = r.ReadInt32();
        var classIds = new List<int>();
        for (int i = 0; i < typeCount; i++)
        {
            int classId = r.ReadInt32();
            r.ReadByte();            // isStrippedType
            r.ReadInt16();           // scriptTypeIndex
            if (classId == 114) r.ReadBytes(16); // script id
            r.ReadBytes(16);         // old type hash
            classIds.Add(classId);
        }
        int objCount = r.ReadInt32();
        var objs = new List<ObjInfo>();
        for (int i = 0; i < objCount; i++)
        {
            r.BaseStream.Position = Align4((int)r.BaseStream.Position);
            var o = new ObjInfo();
            o.EntryOffset = (int)r.BaseStream.Position;
            o.PathId = r.ReadInt64();
            o.ByteStart = r.ReadUInt32();
            o.ByteSize = r.ReadUInt32();
            o.TypeIndex = r.ReadInt32();
            objs.Add(o);
        }
        Console.WriteLine("types={0} objects={1}", typeCount, objCount);

        ObjInfo bs = objs.Find(o => o.TypeIndex >= 0 && o.TypeIndex < classIds.Count && classIds[o.TypeIndex] == BuildSettingsClassId);
        if (bs == null) { Console.WriteLine("BuildSettings object not found."); return 1; }
        int start = (int)(dataOffset + bs.ByteStart);
        Console.WriteLine("BuildSettings pathId={0} start={1} size={2}", bs.PathId, start, bs.ByteSize);

        // Parse: scenes, preloadedPlugins, enabledVRDevices, buildTags (vector<string>), buildGUID (string)
        int pos = start;
        Func<List<string>> readVec = () =>
        {
            var list = new List<string>();
            int n = BitConverter.ToInt32(f, pos); pos += 4;
            if (n < 0 || n > 10000) throw new Exception("bad vector length " + n + " at " + pos);
            for (int i = 0; i < n; i++)
            {
                int len = BitConverter.ToInt32(f, pos); pos += 4;
                if (len < 0 || len > 4096) throw new Exception("bad string length " + len + " at " + pos);
                list.Add(Encoding.UTF8.GetString(f, pos, len)); pos = Align4(pos + len);
            }
            return list;
        };
        var scenes = readVec();
        var plugins = readVec();
        int vrStart = pos;
        var vrDevices = readVec();
        int vrEnd = pos;
        var tags = readVec();
        // buildGUID is a 16-byte binary GUID here, followed by bool flags and then m_Version (string).
        string guid = BitConverter.ToString(f, pos, 16).Replace("-", "");
        int versionAt = -1;
        byte[] verBytes = Encoding.ASCII.GetBytes(unityVersion);
        for (int p = pos + 16; p < pos + 16 + 64; p++)
        {
            if (BitConverter.ToInt32(f, p) != verBytes.Length) continue;
            if (Encoding.ASCII.GetString(f, p + 4, verBytes.Length) == unityVersion) { versionAt = p; break; }
        }

        Console.WriteLine("scenes ({0}): {1}", scenes.Count, string.Join(", ", scenes.GetRange(0, Math.Min(4, scenes.Count)).ToArray()) + (scenes.Count > 4 ? ", ..." : ""));
        Console.WriteLine("preloadedPlugins ({0}): {1}", plugins.Count, string.Join(", ", plugins.ToArray()));
        Console.WriteLine("enabledVRDevices ({0}): {1}", vrDevices.Count, string.Join(", ", vrDevices.ToArray()));
        Console.WriteLine("buildTags ({0}): {1}", tags.Count, string.Join(", ", tags.ToArray()));
        Console.WriteLine("buildGUID: {0}", guid);
        Console.WriteLine("m_Version found at +{0}", versionAt < 0 ? -1 : versionAt - start);

        bool layoutOk = scenes.Count > 0 && scenes[0].EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && versionAt > 0;
        if (!layoutOk) { Console.WriteLine("Layout check FAILED; not patching."); return 1; }
        Console.WriteLine("Layout check OK.");

        bool already = vrDevices.Count == DevicesToEnable.Length;
        for (int i = 0; already && i < DevicesToEnable.Length; i++) already = vrDevices[i] == DevicesToEnable[i];
        if (already) { Console.WriteLine("Already patched."); return 0; }
        if (!write) { Console.WriteLine("Dry run; pass --write to patch."); return 0; }

        // Build the new enabledVRDevices array.
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(DevicesToEnable.Length);
        foreach (var d in DevicesToEnable)
        {
            byte[] sbytes = Encoding.UTF8.GetBytes(d);
            w.Write(sbytes.Length); w.Write(sbytes);
            while (ms.Length % 4 != 0) w.Write((byte)0);
        }
        byte[] newVr = ms.ToArray();

        int objEnd = start + (int)bs.ByteSize;
        var body = new MemoryStream();
        body.Write(f, start, vrStart - start);
        body.Write(newVr, 0, newVr.Length);
        body.Write(f, vrEnd, objEnd - vrEnd);
        byte[] newBody = body.ToArray();

        // Append the rewritten object at the end of the file (8-byte aligned relative to the data section)
        // and repoint the object table entry at it. Everything else stays where it was.
        int relEnd = f.Length - (int)dataOffset;
        int newRelStart = (relEnd + 7) & ~7;
        int newFileLen = (int)dataOffset + newRelStart + newBody.Length;
        byte[] outF = new byte[newFileLen];
        Buffer.BlockCopy(f, 0, outF, 0, f.Length);
        Buffer.BlockCopy(newBody, 0, outF, (int)dataOffset + newRelStart, newBody.Length);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)newRelStart), 0, outF, bs.EntryOffset + 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)newBody.Length), 0, outF, bs.EntryOffset + 12, 4);
        WriteU32BE(outF, 4, (uint)newFileLen);

        string backup = path + ".nsltvr-backup";
        if (!File.Exists(backup)) File.Copy(path, backup);
        File.WriteAllBytes(path, outF);
        Console.WriteLine("Patched. enabledVRDevices = {0}. Backup at {1}", string.Join(", ", DevicesToEnable), backup);
        return 0;
    }
}
