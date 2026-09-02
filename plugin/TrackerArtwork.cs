using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace LauncherV2.Plugins.Catalog;

///
/// Builds a map tracker's pictures on this machine, out of this player's own
/// copy of the game.
///
/// A pack we publish for one of OUR games carries no game artwork — that
/// belongs to its publisher. It carries `sprites.json`, a list saying which
/// picture comes from which file, and the pictures are made here. Same trade
/// as cover art and emulators: we supply the machinery, the player supplies
/// the content, and nothing copyrighted is distributed.
///
/// Today it knows one recipe kind, `sc2_minimap`: StarCraft II ships every
/// mission as a .SC2Map, which is an MPQ archive carrying a Minimap.tga of the
/// real terrain.
///
/// Never throws. A tracker is an extra; a game install must not fail because a
/// picture could not be drawn.
///
public static class TrackerArtwork
{
    /// Is every picture the pack asks for already here?
    public static bool IsBuilt(string packDir)
    {
        try
        {
            var plan = ReadPlan(packDir);
            if (plan == null || plan.Sprites.Count == 0) return false;
            foreach (var s in plan.Sprites)
                if (!File.Exists(Path.Combine(packDir, Local(s.Out))))
                    return false;
            return true;
        }
        catch { return false; }
    }

    ///
    /// Fill in every missing picture. Returns how many were written.
    ///
    /// Archives are opened one at a time and closed again: a campaign is 83
    /// separate map files, and several are tens of megabytes.
    ///
    public static int Build(string packDir, string gameDir, Action<string>? log = null)
    {
        int written = 0, stood_in = 0;
        try
        {
            var plan = ReadPlan(packDir);
            if (plan == null || plan.Sprites.Count == 0) return 0;
            if (!Directory.Exists(gameDir))
            {
                log?.Invoke("[Tracker] the game folder is not where London thinks - "
                          + "no artwork can be built");
                return 0;
            }

            var todo = new List<Recipe>();
            foreach (var s in plan.Sprites)
                if (!File.Exists(Path.Combine(packDir, Local(s.Out))))
                    todo.Add(s);
            if (todo.Count == 0) return 0;

            log?.Invoke($"[Tracker] building {todo.Count} pictures from your copy of the game");

            string root = string.IsNullOrEmpty(plan.MapsRoot)
                ? gameDir : Path.Combine(gameDir, Local(plan.MapsRoot));

            foreach (var recipe in todo)
            {
                string archive = Path.Combine(root, Local(recipe.Archive));
                byte[]? tga = null;
                if (File.Exists(archive))
                {
                    try { tga = new MpqLite(archive).Read(recipe.File); }
                    catch (Exception ex) { log?.Invoke($"[Tracker] {Path.GetFileName(archive)}: {ex.Message}"); }
                }
                byte[]? png = null;
                if (tga != null)
                {
                    try { png = Tga.ToPng(tga, recipe.Crop); }
                    catch { png = null; }
                }
                if (png != null && Write(packDir, recipe.Out, png)) written++;
            }

            // Anything the archives could not give us gets our own stand-in, so
            // the pack renders rather than showing holes where a map should be.
            if (written < todo.Count && !string.IsNullOrEmpty(plan.Fallback))
            {
                string stand = Path.Combine(packDir, Local(plan.Fallback!));
                if (File.Exists(stand))
                {
                    byte[] blob = File.ReadAllBytes(stand);
                    foreach (var recipe in todo)
                        if (!File.Exists(Path.Combine(packDir, Local(recipe.Out)))
                            && Write(packDir, recipe.Out, blob))
                            stood_in++;
                }
            }

            log?.Invoke($"[Tracker] {written} pictures built"
                      + (stood_in > 0 ? $", {stood_in} stood in for" : ""));
        }
        catch (Exception ex)
        {
            log?.Invoke("[Tracker] artwork build failed: " + ex.Message);
        }
        return written;
    }

    // ---------------------------------------------------------------- plan

    private sealed class Recipe
    {
        public string Out = "";
        public string Archive = "";
        public string File = "";
        public bool Crop = true;
    }

    private sealed class Plan
    {
        public string Kind = "";
        public string MapsRoot = "";
        public string? Fallback;
        public List<Recipe> Sprites = new();
    }

    private static string Local(string p) =>
        p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static Plan? ReadPlan(string packDir)
    {
        string path = Path.Combine(packDir, "sprites.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var plan = new Plan();
            if (root.TryGetProperty("kind", out var k)) plan.Kind = k.GetString() ?? "";
            if (root.TryGetProperty("maps_root", out var mr)) plan.MapsRoot = mr.GetString() ?? "";
            if (root.TryGetProperty("fallback", out var fb)) plan.Fallback = fb.GetString();
            // Only the recipe kind this code knows how to follow.
            if (plan.Kind != "sc2_minimap") return null;
            if (!root.TryGetProperty("sprites", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var s in arr.EnumerateArray())
            {
                string o = s.TryGetProperty("out", out var ov) ? ov.GetString() ?? "" : "";
                string a = s.TryGetProperty("archive", out var av) ? av.GetString() ?? "" : "";
                string f = s.TryGetProperty("file", out var fv) ? fv.GetString() ?? "" : "";
                bool c = !s.TryGetProperty("crop", out var cv) || cv.ValueKind != JsonValueKind.False;
                // A path that climbs out of the pack, or out of the game folder,
                // is not a recipe.
                if (o.Length == 0 || a.Length == 0 || f.Length == 0
                    || o.Contains("..") || a.Contains("..")) continue;
                plan.Sprites.Add(new Recipe { Out = o, Archive = a, File = f, Crop = c });
            }
            return plan;
        }
        catch { return null; }
    }

    private static bool Write(string packDir, string rel, byte[] data)
    {
        try
        {
            string dest = Path.Combine(packDir, Local(rel));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, data);
            return true;
        }
        catch { return false; }
    }

    // ───────────────────────── MPQ, only as far as StarCraft II needs ──────
    //
    // Measured across all 83 Archipelago campaign maps: Minimap.tga is always
    // flags 0x80000200 — present, multi-sector, zlib, NOT encrypted, not a
    // single unit. So this reads exactly that, and refuses anything else
    // rather than producing plausible rubbish from a file it misunderstood.
    //
    private sealed class MpqLite
    {
        private readonly byte[] _d;
        private readonly int _base;
        private readonly int _sectorSize;
        private readonly byte[] _hash;
        private readonly byte[] _block;
        private readonly int _hashEntries;

        public MpqLite(string path)
        {
            _d = File.ReadAllBytes(path);
            _base = -1;
            for (int off = 0; off + 4 <= _d.Length && off < 0x100000; off += 0x200)
                if (_d[off] == (byte)'M' && _d[off + 1] == (byte)'P'
                 && _d[off + 2] == (byte)'Q' && _d[off + 3] == 0x1A)
                { _base = off; break; }
            if (_base < 0) throw new InvalidDataException("no MPQ header");

            ushort shift = BitConverter.ToUInt16(_d, _base + 14);
            uint htOfs = BitConverter.ToUInt32(_d, _base + 16);
            uint btOfs = BitConverter.ToUInt32(_d, _base + 20);
            int htLen = (int)BitConverter.ToUInt32(_d, _base + 24);
            int btLen = (int)BitConverter.ToUInt32(_d, _base + 28);
            if (htLen <= 0 || htLen > (1 << 20) || btLen < 0) throw new InvalidDataException("bad tables");
            _sectorSize = 512 << shift;
            _hashEntries = htLen;
            _hash = Decrypt(_d, _base + (int)htOfs, htLen * 16, Hash("(hash table)", 3));
            _block = Decrypt(_d, _base + (int)btOfs, btLen * 16, Hash("(block table)", 3));
        }

        public byte[]? Read(string name)
        {
            const uint EXISTS = 0x80000000, ENCRYPTED = 0x00010000,
                       COMPRESS = 0x00000200, SINGLE = 0x01000000;

            int idx = (int)(Hash(name, 0) & (uint)(_hashEntries - 1));
            uint hA = Hash(name, 1), hB = Hash(name, 2);
            int block = -1;
            for (int probe = 0; probe < _hashEntries; probe++)
            {
                int i = (idx + probe) % _hashEntries;
                uint blk = BitConverter.ToUInt32(_hash, i * 16 + 12);
                if (blk == 0xFFFFFFFF) return null;              // empty ends the chain
                if (blk != 0xFFFFFFFE
                    && BitConverter.ToUInt32(_hash, i * 16) == hA
                    && BitConverter.ToUInt32(_hash, i * 16 + 4) == hB)
                { block = (int)(blk & 0x0FFFFFFF); break; }
            }
            if (block < 0 || (block + 1) * 16 > _block.Length) return null;

            uint fofs = BitConverter.ToUInt32(_block, block * 16);
            int fsize = (int)BitConverter.ToUInt32(_block, block * 16 + 8);
            uint flags = BitConverter.ToUInt32(_block, block * 16 + 12);
            if ((flags & EXISTS) == 0) return null;
            if ((flags & (ENCRYPTED | SINGLE)) != 0) return null;   // not what SC2 uses
            if ((flags & COMPRESS) == 0) return null;
            if (fsize <= 0 || fsize > (64 << 20)) return null;

            int start = _base + (int)fofs;
            int nsec = (fsize + _sectorSize - 1) / _sectorSize;
            if (start + 4 * (nsec + 1) > _d.Length) return null;

            var outBuf = new MemoryStream(fsize);
            for (int s = 0; s < nsec; s++)
            {
                int so = (int)BitConverter.ToUInt32(_d, start + s * 4);
                int eo = (int)BitConverter.ToUInt32(_d, start + (s + 1) * 4);
                if (eo < so || start + eo > _d.Length) return null;
                int len = eo - so;
                int expect = Math.Min(_sectorSize, fsize - (int)outBuf.Length);
                if (len == expect)                                  // stored as-is
                {
                    outBuf.Write(_d, start + so, len);
                    continue;
                }
                if (len < 2 || _d[start + so] != 0x02) return null;  // only zlib
                byte[] plain;
                try
                {
                    using var src = new MemoryStream(_d, start + so + 1, len - 1);
                    using var z = new ZLibStream(src, CompressionMode.Decompress);
                    using var dst = new MemoryStream();
                    z.CopyTo(dst);
                    plain = dst.ToArray();
                }
                catch { return null; }
                outBuf.Write(plain, 0, Math.Min(plain.Length, expect));
            }
            return outBuf.Length == fsize ? outBuf.ToArray() : null;
        }

        private static readonly uint[] Crypt = BuildCrypt();

        private static uint[] BuildCrypt()
        {
            var t = new uint[0x500];
            uint seed = 0x00100001;
            for (int i = 0; i < 0x100; i++)
                for (int j = 0; j < 5; j++)
                {
                    seed = (seed * 125 + 3) % 0x2AAAAB;
                    uint a = (seed & 0xFFFF) << 16;
                    seed = (seed * 125 + 3) % 0x2AAAAB;
                    t[i + j * 0x100] = a | (seed & 0xFFFF);
                }
            return t;
        }

        private static uint Hash(string s, int type)
        {
            uint s1 = 0x7FED7FED, s2 = 0xEEEEEEEE;
            foreach (char raw in s.ToUpperInvariant().Replace('/', '\\'))
            {
                uint c = raw;
                s1 = Crypt[(type << 8) + c] ^ (s1 + s2);
                s2 = c + s1 + s2 + (s2 << 5) + 3;
            }
            return s1;
        }

        private static byte[] Decrypt(byte[] d, int ofs, int len, uint key)
        {
            len = Math.Max(0, Math.Min(len, d.Length - ofs)) & ~3;
            var outBuf = new byte[len];
            uint s2 = 0xEEEEEEEE;
            for (int i = 0; i + 4 <= len; i += 4)
            {
                s2 += Crypt[0x400 + (key & 0xFF)];
                uint v = BitConverter.ToUInt32(d, ofs + i) ^ (key + s2);
                BitConverter.GetBytes(v).CopyTo(outBuf, i);
                key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
                s2 = v + s2 + (s2 << 5) + 3;
            }
            return outBuf;
        }
    }

    // ───────────────────────── Targa -> PNG ────────────────────────────────

    private static class Tga
    {
        public static byte[] ToPng(byte[] tga, bool crop)
        {
            int idlen = tga[0], cmap = tga[1], type = tga[2];
            if (type != 2 || cmap != 0) throw new InvalidDataException("not a true-colour TGA");
            int w = BitConverter.ToUInt16(tga, 12), h = BitConverter.ToUInt16(tga, 14);
            int depth = tga[16], desc = tga[17];
            if (depth != 24 && depth != 32) throw new InvalidDataException("unexpected depth");
            int bpp = depth / 8, start = 18 + idlen;
            if (w <= 0 || h <= 0 || start + w * h * bpp > tga.Length)
                throw new InvalidDataException("truncated");

            bool topDown = (desc & 0x20) != 0;
            var rows = new byte[h][];
            for (int y = 0; y < h; y++)
            {
                int src = topDown ? y : (h - 1 - y);
                int off = start + src * w * bpp;
                var row = new byte[w * 3];
                for (int x = 0; x < w; x++)
                {
                    int o = off + x * bpp;
                    row[x * 3] = tga[o + 2];          // stored BGR
                    row[x * 3 + 1] = tga[o + 1];
                    row[x * 3 + 2] = tga[o];
                }
                rows[y] = row;
            }

            if (crop) Crop(ref w, ref h, ref rows);
            Lift(rows);
            return Png(w, h, rows);
        }

        /// Trim the black padding. A minimap is square-padded, and on a 1024
        /// sheet the terrain can be less than half the picture.
        private static void Crop(ref int w, ref int h, ref byte[][] rows)
        {
            const int T = 10;
            bool Lit(byte[] r) { foreach (byte b in r) if (b > T) return true; return false; }

            int top = 0;
            while (top < h - 1 && !Lit(rows[top])) top++;
            int bottom = h - 1;
            while (bottom > top && !Lit(rows[bottom])) bottom--;

            int left = w, right = -1;
            for (int y = top; y <= bottom; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = x * 3;
                    if (rows[y][o] > T || rows[y][o + 1] > T || rows[y][o + 2] > T)
                    { if (x < left) left = x; if (x > right) right = x; }
                }
            if (right < left) return;

            int nw = right - left + 1, nh = bottom - top + 1;
            var outRows = new byte[nh][];
            for (int y = 0; y < nh; y++)
            {
                var row = new byte[nw * 3];
                Buffer.BlockCopy(rows[top + y], left * 3, row, 0, nw * 3);
                outRows[y] = row;
            }
            w = nw; h = nh; rows = outRows;
        }

        /// Raise a very dark minimap until its terrain is readable. Several
        /// missions play at night and come out near-black; this is one gamma
        /// over the whole picture, so it stays the same map, only legible.
        private static void Lift(byte[][] rows, int target = 64)
        {
            var sample = new List<byte>();
            foreach (var row in rows)
                for (int i = 0; i < row.Length; i += 3) sample.Add(row[i]);
            if (sample.Count == 0) return;
            sample.Sort();
            int p90 = sample[(int)(sample.Count * 0.9)];
            if (p90 >= target || p90 <= 0) return;

            double gamma = Math.Log(target / 255.0) / Math.Log(Math.Max(1, p90) / 255.0);
            var table = new byte[256];
            for (int v = 0; v < 256; v++)
                table[v] = (byte)Math.Min(255, (int)(255 * Math.Pow(v / 255.0, gamma)));
            foreach (var row in rows)
                for (int i = 0; i < row.Length; i++) row[i] = table[row[i]];
        }

        private static byte[] Png(int w, int h, byte[][] rows)
        {
            var raw = new byte[h * (1 + w * 3)];
            int o = 0;
            for (int y = 0; y < h; y++)
            {
                raw[o++] = 0;                          // filter: none
                Buffer.BlockCopy(rows[y], 0, raw, o, w * 3);
                o += w * 3;
            }

            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            BE(ihdr, 0, w); BE(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 2;                  // 8-bit RGB
            Chunk(ms, "IHDR", ihdr);

            byte[] deflated;
            using (var comp = new MemoryStream())
            {
                using (var z = new ZLibStream(comp, CompressionLevel.SmallestSize, true))
                    z.Write(raw, 0, raw.Length);
                deflated = comp.ToArray();
            }
            Chunk(ms, "IDAT", deflated);
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void BE(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }

        private static void Chunk(Stream s, string tag, byte[] payload)
        {
            var len = new byte[4]; BE(len, 0, payload.Length);
            s.Write(len, 0, 4);
            var body = new byte[4 + payload.Length];
            for (int i = 0; i < 4; i++) body[i] = (byte)tag[i];
            Buffer.BlockCopy(payload, 0, body, 4, payload.Length);
            s.Write(body, 0, body.Length);
            var crc = new byte[4]; BE(crc, 0, unchecked((int)Crc32(body)));
            s.Write(crc, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrc();

        private static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
