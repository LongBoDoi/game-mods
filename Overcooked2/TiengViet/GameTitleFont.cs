using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace TiengViet
{
    /// <summary>
    /// The game's title font with the Vietnamese letters added (the NaughtySquirrel TitleFont
    /// choice). Naughty Squirrel is a commercial font, so the mod does not carry it: the first
    /// time it is needed, the font file is read out of the player's own game (the font bundle in
    /// StreamingAssets), <see cref="VietGlyphs"/> adds the letters, and the result is kept in
    /// BepInEx\cache\TiengViet for later starts. Unity 2017 can only load a font from an asset
    /// bundle, so it is then copied into title.bundle.lz4 (built into the DLL by tools/fonts.py:
    /// the game's Header_Font asset with room for the font file) and that bundle is loaded.
    /// </summary>
    internal static class GameTitleFont
    {
        private const string Asset = "assets/tiengviet/title.ttf";
        private const string Family = "Naughty Squirrel";
        /// <summary>Must match tools/fonts.py.</summary>
        private static readonly byte[] Mark = Encoding.ASCII.GetBytes("TiengVietTitleFontRoom");
        /// <summary>Where the loaded bundle is kept for a newer copy of the mod (see HeaderFont.LoadedBefore).</summary>
        private const string BundleSlot = "com.manhl.overcooked2.tiengviet.title";

        private static Font font;
        private static bool tried;

        /// <summary>The font, or null when it could not be made (the reason is logged once).</summary>
        internal static Font Get()
        {
            if (tried) return font;
            tried = true;
            try { font = Load(); }
            catch (Exception error) { Plugin.Log.LogWarning("Could not make the game's title font with Vietnamese letters: " + error.Message); }
            return font;
        }

        private static Font Load()
        {
            AssetBundle bundle = LoadedBefore();
            if (bundle == null)
            {
                byte[] file = FontFile();
                byte[] template = Lz4.UnpackResource("TiengViet.title.bundle.lz4");
                Fill(template, file);
                bundle = AssetBundle.LoadFromMemory(template);
                if (bundle == null) throw new InvalidOperationException("Unity would not open the title font bundle");
                AppDomain.CurrentDomain.SetData(BundleSlot, bundle);
            }
            Font loaded = bundle.LoadAsset<Font>(Asset);
            if (loaded == null) throw new InvalidOperationException("the title font bundle has no font");
            return loaded;
        }

        /// <summary>Unity opens a bundle only once, so a copy of the mod loaded into the running
        /// game by Mod Updater uses the one the older copy opened.</summary>
        private static AssetBundle LoadedBefore()
        {
            var kept = AppDomain.CurrentDomain.GetData(BundleSlot) as AssetBundle;
            if (kept != null) return kept;
            foreach (AssetBundle loaded in Resources.FindObjectsOfTypeAll<AssetBundle>())
                if (loaded != null && loaded.Contains(Asset)) return loaded;
            return null;
        }

        /// <summary>Copies the font file into the room the template keeps for it.</summary>
        private static void Fill(byte[] template, byte[] file)
        {
            int at = IndexOf(template, Mark, 0, template.Length);
            if (at < 4) throw new InvalidOperationException("title.bundle.lz4 has no room for the font");
            int room = BitConverter.ToInt32(template, at - 4);
            if (file.Length > room) throw new InvalidOperationException("the font (" + file.Length + " bytes) is bigger than its room (" + room + ")");
            Array.Copy(file, 0, template, at, file.Length);
            for (int i = at + file.Length; i < at + Mark.Length; i++) template[i] = 0;
        }

        // --------------------------------------------------------------------------------------

        /// <summary>The finished font file: from the cache when the game's font bundle and this
        /// version's letters are unchanged, otherwise made again.</summary>
        private static byte[] FontFile()
        {
            string folder = Path.Combine(Application.streamingAssetsPath, "Windows");
            string cacheDir = Path.Combine(BepInEx.Paths.CachePath, "TiengViet");
            string cached = Path.Combine(cacheDir, "title-vi.otf"), keyFile = cached + ".key";

            string preferred = Path.Combine(folder, "bundle22");
            if (File.Exists(preferred))
            {
                string key = Key(preferred);
                try
                {
                    if (File.Exists(cached) && File.Exists(keyFile) && File.ReadAllText(keyFile) == key)
                        return File.ReadAllBytes(cached);
                }
                catch (Exception) { }
            }

            var watch = Stopwatch.StartNew();
            string source;
            byte[] original = FindGameFont(folder, preferred, out source);
            int added;
            byte[] built = VietGlyphs.Build(original, out added);
            if (added == 0) throw new InvalidOperationException("the game's title font already has the Vietnamese letters or none could be made");
            Plugin.Log.LogInfo("Made the game's title font with " + added + " Vietnamese letters from " + Path.GetFileName(source) +
                " in " + watch.ElapsedMilliseconds + " ms.");
            try
            {
                Directory.CreateDirectory(cacheDir);
                File.WriteAllBytes(cached, built);
                File.WriteAllText(keyFile, Key(source));
            }
            catch (Exception error) { Plugin.Log.LogWarning("Could not keep the title font in " + cacheDir + ": " + error.Message); }
            return built;
        }

        private static string Key(string source)
        {
            var info = new FileInfo(source);
            return VietGlyphs.Revision + "|" + info.Name + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }

        /// <summary>The font bundle (bundle22), or any other bundle of the game, that has Naughty Squirrel.</summary>
        private static byte[] FindGameFont(string folder, string preferred, out string source)
        {
            if (File.Exists(preferred))
            {
                byte[] found = Bundles.FindFont(preferred, Family);
                if (found != null) { source = preferred; return found; }
            }
            if (Directory.Exists(folder))
                foreach (string file in Directory.GetFiles(folder))
                {
                    if (file == preferred || Path.GetExtension(file) != "") continue;
                    byte[] found = null;
                    try { found = Bundles.FindFont(file, Family); }
                    catch (Exception) { }
                    if (found != null) { source = file; return found; }
                }
            throw new FileNotFoundException("no " + Family + " in the game's bundles");
        }

        internal static int IndexOf(byte[] data, byte[] what, int start, int end)
        {
            for (int i = start; i <= end - what.Length; i++)
            {
                if (data[i] != what[0]) continue;
                int k = 1;
                while (k < what.Length && data[i + k] == what[k]) k++;
                if (k == what.Length) return i;
            }
            return -1;
        }

        // --------------------------------------------------------------------------------------

        /// <summary>Reads Unity asset bundles (UnityFS, LZ4 or uncompressed) far enough to find a font file.</summary>
        internal static class Bundles
        {
            private static readonly byte[] OpenType = { (byte)'O', (byte)'T', (byte)'T', (byte)'O' };

            /// <summary>The first OpenType font with this family name in the bundle's assets, or null.</summary>
            internal static byte[] FindFont(string path, string family)
            {
                byte[] data;
                int start, end;
                if (!Unpack(path, out data, out start, out end)) return null;
                for (int at = IndexOf(data, OpenType, start, end); at >= 0; at = IndexOf(data, OpenType, at + 1, end))
                {
                    int length = VietGlyphs.FontLength(data, at, end);
                    if (length <= 0 || at + length > end) continue;
                    // A font asset keeps its file as a length and the bytes; the length may include padding.
                    if (at >= 4)
                    {
                        int stored = BitConverter.ToInt32(data, at - 4);
                        if (stored >= length && stored - length < 16 && at + stored <= end) length = stored;
                    }
                    if (VietGlyphs.Family(data, at, length) != family) continue;
                    var font = new byte[length];
                    Array.Copy(data, at, font, 0, length);
                    return font;
                }
                return null;
            }

            /// <summary>Unpacks the bundle's first file (its assets, not their .resS data) into
            /// <paramref name="data"/>[start, end).</summary>
            private static bool Unpack(string path, out byte[] data, out int start, out int end)
            {
                data = null; start = end = 0;
                using (var stream = File.OpenRead(path))
                {
                    var reader = new BinaryReader(stream);
                    if (CString(reader) != "UnityFS") return false;
                    int version = BigInt(reader);
                    CString(reader); CString(reader);
                    BigLong(reader);
                    int packedInfo = BigInt(reader), infoSize = BigInt(reader), flags = BigInt(reader);
                    if (version >= 7) Align(stream, 16);
                    long dataAt;
                    byte[] packed;
                    if ((flags & 0x80) != 0)
                    {
                        dataAt = stream.Position;
                        stream.Position = stream.Length - packedInfo;
                        packed = reader.ReadBytes(packedInfo);
                    }
                    else
                    {
                        packed = reader.ReadBytes(packedInfo);
                        dataAt = stream.Position;
                    }
                    byte[] info = Decompress(packed, infoSize, flags & 0x3F);

                    int p = 16;
                    int blocks = BigInt(info, ref p);
                    var sizes = new int[blocks];
                    var packedSizes = new int[blocks];
                    var kinds = new int[blocks];
                    for (int b = 0; b < blocks; b++)
                    {
                        sizes[b] = BigInt(info, ref p);
                        packedSizes[b] = BigInt(info, ref p);
                        kinds[b] = ((info[p] << 8) | info[p + 1]) & 0x3F;
                        p += 2;
                    }
                    int nodes = BigInt(info, ref p);
                    long nodeAt = -1, nodeSize = 0;
                    for (int n = 0; n < nodes; n++)
                    {
                        long offset = BigLong(info, ref p), size = BigLong(info, ref p);
                        p += 4;
                        int nameEnd = Array.IndexOf(info, (byte)0, p);
                        string name = Encoding.UTF8.GetString(info, p, nameEnd - p);
                        p = nameEnd + 1;
                        if (nodeAt < 0 && !name.EndsWith(".resS") && !name.EndsWith(".resource")) { nodeAt = offset; nodeSize = size; }
                    }
                    if (nodeAt < 0 || nodeAt + nodeSize > int.MaxValue || nodeAt + nodeSize > 512L * 1024 * 1024) return false;

                    // Only the blocks up to the end of that file are unpacked.
                    int need = (int)(nodeAt + nodeSize);
                    data = new byte[need];
                    stream.Position = dataAt;
                    int filled = 0;
                    for (int b = 0; b < blocks && filled < need; b++)
                    {
                        byte[] block = reader.ReadBytes(packedSizes[b]);
                        byte[] plain = Decompress(block, sizes[b], kinds[b]);
                        int copy = Math.Min(plain.Length, need - filled);
                        Array.Copy(plain, 0, data, filled, copy);
                        filled += copy;
                    }
                    start = (int)nodeAt;
                    end = filled;
                    return end > start;
                }
            }

            private static byte[] Decompress(byte[] packed, int size, int kind)
            {
                if (kind == 0) return packed;
                if (kind == 2 || kind == 3) return Lz4.Decode(packed, 0, packed.Length, size);
                throw new NotSupportedException("bundle compression " + kind);
            }

            private static string CString(BinaryReader reader)
            {
                var text = new StringBuilder();
                for (int i = 0; i < 64; i++)
                {
                    byte b = reader.ReadByte();
                    if (b == 0) break;
                    text.Append((char)b);
                }
                return text.ToString();
            }

            private static void Align(Stream stream, int to) { stream.Position = (stream.Position + to - 1) / to * to; }
            private static int BigInt(BinaryReader r) { byte[] b = r.ReadBytes(4); return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]; }
            private static long BigLong(BinaryReader r) { return ((long)(uint)BigInt(r) << 32) | (uint)BigInt(r); }
            private static int BigInt(byte[] d, ref int p) { int v = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]; p += 4; return v; }
            private static long BigLong(byte[] d, ref int p) { long hi = (uint)BigInt(d, ref p); return (hi << 32) | (uint)BigInt(d, ref p); }
        }

        /// <summary>LZ4 block decoding: Unity's bundle compression and title.bundle.lz4.</summary>
        internal static class Lz4
        {
            /// <summary>A resource made by Python's lz4.block.compress(store_size=True).</summary>
            internal static byte[] UnpackResource(string name)
            {
                using (Stream stream = typeof(GameTitleFont).Assembly.GetManifestResourceStream(name))
                {
                    if (stream == null) throw new FileNotFoundException(name + " is missing from TiengViet.dll");
                    var packed = new byte[stream.Length];
                    int read = 0;
                    while (read < packed.Length)
                    {
                        int got = stream.Read(packed, read, packed.Length - read);
                        if (got <= 0) break;
                        read += got;
                    }
                    int size = BitConverter.ToInt32(packed, 0);
                    return Decode(packed, 4, packed.Length - 4, size);
                }
            }

            internal static byte[] Decode(byte[] src, int offset, int length, int size)
            {
                var dst = new byte[size];
                int s = offset, end = offset + length, d = 0;
                while (s < end)
                {
                    int token = src[s++];
                    int literals = token >> 4;
                    if (literals == 15) { int b; do { b = src[s++]; literals += b; } while (b == 255); }
                    if (d + literals > size || s + literals > end) throw new InvalidOperationException("bad LZ4 data");
                    Buffer.BlockCopy(src, s, dst, d, literals);
                    s += literals; d += literals;
                    if (s >= end) break;
                    int back = src[s] | (src[s + 1] << 8);
                    s += 2;
                    int match = token & 15;
                    if (match == 15) { int b; do { b = src[s++]; match += b; } while (b == 255); }
                    match += 4;
                    int from = d - back;
                    if (back == 0 || from < 0 || d + match > size) throw new InvalidOperationException("bad LZ4 data");
                    for (int k = 0; k < match; k++) dst[d++] = dst[from++];
                }
                if (d != size) throw new InvalidOperationException("LZ4 data is " + d + " bytes, expected " + size);
                return dst;
            }
        }
    }
}
