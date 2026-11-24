using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TiengViet
{
    /// <summary>
    /// Adds the Vietnamese letters to the game's title font (Naughty Squirrel), from the font's
    /// own parts, so titles keep the game's look in Vietnamese.
    ///
    /// Naughty Squirrel has the accents Vietnamese shares with French and Spanish: á à ã â ă ê ô
    /// and their capitals, each drawn as the plain letter plus one accent. The letters it lacks
    /// (ả ạ ơ ư ấ ặ ộ ự... 94 in all) are made the same way: the plain letter, a hat or breve
    /// from its own â/ă, and a tone mark. Grave, acute and tilde are the font's own, cut from à,
    /// á and ã; the three marks it has no glyph for are drawn from its other glyphs: the hook
    /// above from the top of its question mark, the dot below from its dot accent, and the horn
    /// of ơ and ư from its apostrophe.
    ///
    /// The font is commercial, so it is not shipped with the mod: this runs on the player's PC,
    /// on the copy inside their own game (see <see cref="GameTitleFont"/>). It needs nothing
    /// but the font file and works on any OpenType font with CFF outlines.
    /// </summary>
    internal static class VietGlyphs
    {
        /// <summary>Bumped whenever the letters change, so a cached build is made again.</summary>
        internal const int Revision = 2;

        /// <summary>Each row: a vowel without a tone, then with grave, hook, tilde, acute, dot below.</summary>
        private static readonly string[] Lower =
        {
            "aàảãáạ", "ăằẳẵắặ", "âầẩẫấậ",
            "eèẻẽéẹ", "êềểễếệ", "iìỉĩíị",
            "oòỏõóọ", "ôồổỗốộ", "ơờởỡớợ",
            "uùủũúụ", "ưừửữứự", "yỳỷỹýỵ",
        };
        private static readonly string[] Upper =
        {
            "AÀẢÃÁẠ", "ĂẰẲẴẮẶ", "ÂẦẨẪẤẬ",
            "EÈẺẼÉẸ", "ÊỀỂỄẾỆ", "IÌỈĨÍỊ",
            "OÒỎÕÓỌ", "ÔỒỔỖỐỘ", "ƠỜỞỠỚỢ",
            "UÙỦŨÚỤ", "ƯỪỬỮỨỰ", "YỲỶỸÝỴ",
        };
        private const int None = 0, Grave = 1, Hook = 2, Tilde = 3, Acute = 4, DotBelow = 5;

        // How the new marks are sized and placed, in parts of the letter's or the mark's size.
        /// <summary>The hook above is as tall as this share of the font's acute.</summary>
        private const double HookHeight = 1.15;
        /// <summary>A tone on top of a hat or a breve is drawn this much smaller.</summary>
        private const double StackScale = 0.8;
        /// <summary>The dot below is this share of the font's dot accent.</summary>
        private const double DotScale = 0.8;
        /// <summary>The horn is as tall as this share of its letter.</summary>
        private const double HornHeight = 0.5;
        /// <summary>How much of the horn's width lies inside the letter's box.</summary>
        private const double HornInside = 0.45;
        /// <summary>The horn's lowest point, as a share of the letter's height.</summary>
        private const double HornBottom = 0.55;

        internal static byte[] Build(byte[] font, out int added)
        {
            var sfnt = new Sfnt(font);
            var cff = new Cff(sfnt.Table("CFF "));
            Dictionary<int, int> cmap = ReadCmap(sfnt.Table("cmap"));
            int[] advances, bearings;
            ReadMetrics(sfnt, cff.Count, out advances, out bearings);

            var builder = new Composer(cff, cmap);
            var made = new List<KeyValuePair<int, Glyph>>();
            for (int upper = 0; upper < 2; upper++)
            {
                string[] rows = upper == 1 ? Upper : Lower;
                foreach (string row in rows)
                    for (int tone = None; tone <= DotBelow; tone++)
                    {
                        int code = row[tone];
                        if (cmap.ContainsKey(code)) continue;
                        Glyph glyph = builder.Make(row, tone, upper == 1);
                        if (glyph == null) continue;
                        builder.Remember(code, glyph);
                        made.Add(new KeyValuePair<int, Glyph>(code, glyph));
                    }
            }
            added = made.Count;
            if (added == 0) return font;

            // New glyphs go at the end, so every glyph the font's tables point at keeps its number.
            int first = cff.Count;
            var charStrings = new List<byte[]>();
            var names = new List<string>();
            var widths = new List<int>(advances);
            var lsbs = new List<int>(bearings);
            double top = double.MinValue;
            for (int i = 0; i < made.Count; i++)
            {
                Glyph glyph = made[i].Value;
                charStrings.Add(glyph.Encode(cff.NominalWidth));
                names.Add("uni" + made[i].Key.ToString("X4"));
                Box box = Box.Of(glyph.Contours);
                widths.Add((int)Math.Round(glyph.Width));
                lsbs.Add(box.Empty ? 0 : (int)Math.Floor(box.X0));
                if (!box.Empty) top = Math.Max(top, box.Y1);
                cmap[made[i].Key] = first + i;
            }

            sfnt.Set("CFF ", cff.Write(charStrings, names));
            sfnt.Set("cmap", WriteCmap(cmap));
            sfnt.Set("hmtx", WriteMetrics(widths, lsbs));
            byte[] hhea = sfnt.Table("hhea");
            int widest = 0;
            foreach (int w in widths) widest = Math.Max(widest, w);
            PutU16(hhea, 10, Math.Max(U16(hhea, 10), widest));
            PutU16(hhea, 34, widths.Count);
            byte[] maxp = sfnt.Table("maxp");
            PutU16(maxp, 4, widths.Count);
            byte[] head = sfnt.Table("head");
            if (top > S16(head, 42)) PutU16(head, 42, (int)Math.Ceiling(top) & 0xFFFF);
            if (sfnt.Has("post"))
            {
                // Glyph names are in the CFF table; the post table keeps only its metrics.
                byte[] post = new byte[32];
                Array.Copy(sfnt.Table("post"), post, 32);
                PutU32(post, 0, 0x00030000);
                sfnt.Set("post", post);
            }
            return sfnt.Write();
        }

        // ------------------------------------------------------------------------------------
        // Outlines
        // ------------------------------------------------------------------------------------

        /// <summary>One closed path: a start point and line (2 numbers) or curve (6) segments.</summary>
        private sealed class Contour
        {
            internal double X, Y;
            internal readonly List<double[]> Segments = new List<double[]>();

            internal Contour Moved(double scale, double dx, double dy)
            {
                var copy = new Contour { X = X * scale + dx, Y = Y * scale + dy };
                foreach (double[] s in Segments)
                {
                    var t = new double[s.Length];
                    for (int i = 0; i < s.Length; i += 2) { t[i] = s[i] * scale + dx; t[i + 1] = s[i + 1] * scale + dy; }
                    copy.Segments.Add(t);
                }
                return copy;
            }

            internal bool Same(Contour other)
            {
                if (other.Segments.Count != Segments.Count || !Near(X, other.X) || !Near(Y, other.Y)) return false;
                for (int i = 0; i < Segments.Count; i++)
                {
                    double[] a = Segments[i], b = other.Segments[i];
                    if (a.Length != b.Length) return false;
                    for (int j = 0; j < a.Length; j++) if (!Near(a[j], b[j])) return false;
                }
                return true;
            }

            private static bool Near(double a, double b) { return Math.Abs(a - b) < 0.5; }
        }

        private sealed class Glyph
        {
            internal readonly List<Contour> Contours = new List<Contour>();
            internal double Width;

            internal byte[] Encode(double nominalWidth)
            {
                var o = new List<byte>();
                double x = 0, y = 0;
                bool first = true;
                foreach (Contour c in Contours)
                {
                    if (first) Number(o, Width - nominalWidth);
                    first = false;
                    double tx = Q(c.X), ty = Q(c.Y);
                    Number(o, tx - x); Number(o, ty - y); o.Add(21);   // rmoveto
                    x = tx; y = ty;
                    foreach (double[] s in c.Segments)
                    {
                        for (int i = 0; i < s.Length; i += 2)
                        {
                            double px = Q(s[i]), py = Q(s[i + 1]);
                            Number(o, px - x); Number(o, py - y);
                            x = px; y = py;
                        }
                        o.Add((byte)(s.Length == 2 ? 5 : 8));            // rlineto, rrcurveto
                    }
                }
                if (first) Number(o, Width - nominalWidth);
                o.Add(14);                                               // endchar
                return o.ToArray();
            }

            /// <summary>Rounds to 1/64 unit, so the steps between points add up exactly.</summary>
            private static double Q(double v) { return Math.Round(v * 64) / 64; }

            private static void Number(List<byte> o, double v)
            {
                double rounded = Math.Round(v);
                if (Math.Abs(v - rounded) > 1e-9)
                {
                    int fixed16 = (int)Math.Round(v * 65536);
                    o.Add(255);
                    o.Add((byte)(fixed16 >> 24)); o.Add((byte)(fixed16 >> 16)); o.Add((byte)(fixed16 >> 8)); o.Add((byte)fixed16);
                    return;
                }
                int n = (int)rounded;
                if (n >= -107 && n <= 107) o.Add((byte)(n + 139));
                else if (n >= 108 && n <= 1131) { n -= 108; o.Add((byte)((n >> 8) + 247)); o.Add((byte)n); }
                else if (n >= -1131 && n <= -108) { n = -n - 108; o.Add((byte)((n >> 8) + 251)); o.Add((byte)n); }
                else { o.Add(28); o.Add((byte)(n >> 8)); o.Add((byte)n); }
            }
        }

        private struct Box
        {
            internal double X0, Y0, X1, Y1;
            internal bool Empty { get { return X0 > X1; } }
            internal double CenterX { get { return (X0 + X1) / 2; } }
            internal double Width { get { return X1 - X0; } }
            internal double Height { get { return Y1 - Y0; } }

            internal static Box Of(IEnumerable<Contour> contours)
            {
                var b = new Box { X0 = double.MaxValue, Y0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue };
                foreach (Contour c in contours)
                {
                    double x = c.X, y = c.Y;
                    b.Add(x, y);
                    foreach (double[] s in c.Segments)
                    {
                        if (s.Length == 6)
                        {
                            b.Curve(x, s[0], s[2], s[4], true);
                            b.Curve(y, s[1], s[3], s[5], false);
                        }
                        x = s[s.Length - 2]; y = s[s.Length - 1];
                        b.Add(x, y);
                    }
                }
                return b;
            }

            private void Add(double x, double y)
            {
                X0 = Math.Min(X0, x); X1 = Math.Max(X1, x);
                Y0 = Math.Min(Y0, y); Y1 = Math.Max(Y1, y);
            }

            /// <summary>Widens the box to the extremes of one coordinate of a cubic curve.</summary>
            private void Curve(double p0, double p1, double p2, double p3, bool horizontal)
            {
                double a = -p0 + 3 * p1 - 3 * p2 + p3, b = 2 * (p0 - 2 * p1 + p2), c = p1 - p0;
                var roots = new List<double>();
                if (Math.Abs(a) < 1e-9) { if (Math.Abs(b) > 1e-9) roots.Add(-c / b); }
                else
                {
                    double d = b * b - 4 * a * c;
                    if (d >= 0) { d = Math.Sqrt(d); roots.Add((-b + d) / (2 * a)); roots.Add((-b - d) / (2 * a)); }
                }
                foreach (double t in roots)
                {
                    if (t <= 0 || t >= 1) continue;
                    double u = 1 - t;
                    double v = u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
                    if (horizontal) { X0 = Math.Min(X0, v); X1 = Math.Max(X1, v); }
                    else { Y0 = Math.Min(Y0, v); Y1 = Math.Max(Y1, v); }
                }
            }
        }

        /// <summary>Moves (and scales) shapes so their box is centred on x with its bottom at y.</summary>
        private static List<Contour> Place(List<Contour> shape, double scale, double centerX, double bottom)
        {
            Box b = Box.Of(shape);
            double dx = centerX - b.CenterX * scale, dy = bottom - b.Y0 * scale;
            var placed = new List<Contour>();
            foreach (Contour c in shape) placed.Add(c.Moved(scale, dx, dy));
            return placed;
        }

        // ------------------------------------------------------------------------------------
        // Making the letters
        // ------------------------------------------------------------------------------------

        private sealed class Composer
        {
            private readonly Cff cff;
            private readonly Dictionary<int, int> cmap;
            private readonly Dictionary<int, Glyph> cache = new Dictionary<int, Glyph>();
            private readonly Dictionary<int, Glyph> made = new Dictionary<int, Glyph>();

            internal Composer(Cff cff, Dictionary<int, int> cmap) { this.cff = cff; this.cmap = cmap; }

            internal void Remember(int code, Glyph glyph) { made[code] = glyph; }

            private Glyph Get(int code)
            {
                Glyph glyph;
                if (made.TryGetValue(code, out glyph)) return glyph;
                int id;
                if (!cmap.TryGetValue(code, out id)) return null;
                if (!cache.TryGetValue(id, out glyph)) cache[id] = glyph = cff.Outline(id);
                return glyph;
            }

            /// <summary>The contours of an accented letter that its plain letter does not have.</summary>
            private List<Contour> Mark(char accented, char plain)
            {
                Glyph a = Get(accented), p = Get(plain);
                if (a == null || p == null) return null;
                var extra = new List<Contour>();
                foreach (Contour c in a.Contours)
                {
                    bool shared = false;
                    foreach (Contour d in p.Contours) if (c.Same(d)) { shared = true; break; }
                    if (!shared) extra.Add(c);
                }
                return extra.Count > 0 && extra.Count < a.Contours.Count ? extra : null;
            }

            /// <summary>A tone mark above, as the font draws it on a/A, and where it sits there:
            /// its centre's distance from the letter's centre and its gap above the letter.</summary>
            private List<Contour> Tone(int tone, bool upper, out double dx, out double gap)
            {
                char plain = upper ? 'A' : 'a';
                Box letter = Box.Of(Get(plain).Contours);
                if (tone == Hook)
                {
                    double gx, ag, gg;
                    List<Contour> acute = Tone(Acute, upper, out dx, out ag);
                    Tone(Grave, upper, out gx, out gg);
                    dx = (dx + gx) / 2;
                    gap = Math.Min(ag, gg);
                    return HookShape(Box.Of(acute).Height * HookHeight);
                }
                char accented = (upper ? Upper : Lower)[0][tone];
                List<Contour> mark = Mark(accented, plain);
                if (mark == null) throw new NotSupportedException("no " + accented + " to take the mark from");
                Box box = Box.Of(mark);
                dx = box.CenterX - letter.CenterX;
                gap = box.Y0 - letter.Y1;
                return mark;
            }

            /// <summary>The top of the question mark (without its dot), at the given height.</summary>
            private List<Contour> HookShape(double height)
            {
                Glyph question = Get('?');
                if (question == null) throw new NotSupportedException("no question mark");
                Contour hook = null;
                double highest = double.MinValue;
                foreach (Contour c in question.Contours)
                {
                    Box b = Box.Of(new[] { c });
                    if (b.Y1 > highest) { highest = b.Y1; hook = c; }
                }
                var shape = new List<Contour> { hook };
                return Scaled(shape, height / Box.Of(shape).Height);
            }

            private static List<Contour> Scaled(List<Contour> shape, double scale)
            {
                var result = new List<Contour>();
                foreach (Contour c in shape) result.Add(c.Moved(scale, 0, 0));
                return result;
            }

            /// <summary>The horn of ơ and ư: the font's apostrophe, its round end above the
            /// letter's right shoulder and its tail running down into the letter.</summary>
            private List<Contour> Horn(Box letter)
            {
                Glyph apostrophe = Get('’') ?? Get('\'');
                if (apostrophe == null) throw new NotSupportedException("no apostrophe");
                List<Contour> shape = Scaled(apostrophe.Contours, letter.Height * HornHeight / Box.Of(apostrophe.Contours).Height);
                Box b = Box.Of(shape);
                double left = letter.X1 - b.Width * HornInside;
                double bottom = letter.Y0 + letter.Height * HornBottom;
                var placed = new List<Contour>();
                foreach (Contour c in shape) placed.Add(c.Moved(1, left - b.X0, bottom - b.Y0));
                return placed;
            }

            internal Glyph Make(string row, int tone, bool upper)
            {
                char vowel = row[0];
                char plain = Plain(vowel, upper);
                Glyph plainGlyph = Get(plain);
                if (plainGlyph == null) return null;
                Box letter = Box.Of(plainGlyph.Contours);
                bool horn = vowel == 'ơ' || vowel == 'ư' || vowel == 'Ơ' || vowel == 'Ư';
                bool hat = vowel == 'â' || vowel == 'ê' || vowel == 'ô' || vowel == 'Â' || vowel == 'Ê' || vowel == 'Ô';
                bool breve = vowel == 'ă' || vowel == 'Ă';

                var glyph = new Glyph();
                if (tone == None)
                {
                    if (!horn) return null;
                    List<Contour> hornShape = Horn(letter);
                    glyph.Contours.AddRange(plainGlyph.Contours);
                    glyph.Contours.AddRange(hornShape);
                    double side = Math.Max(0, plainGlyph.Width - letter.X1);
                    glyph.Width = Math.Max(plainGlyph.Width, Box.Of(hornShape).X1 + side * 0.5);
                    return glyph;
                }

                // The letter with its hat, breve or horn, and no tone.
                Glyph body = Get(vowel);
                if (body == null) return null;
                glyph.Width = body.Width;
                glyph.Contours.AddRange(body.Contours);

                if (tone == DotBelow)
                {
                    Glyph dot = Get('˙');
                    if (dot == null) return null;
                    double gap = letter.Height * 0.07;
                    List<Contour> shape = Scaled(dot.Contours, DotScale);
                    Box d = Box.Of(shape);
                    glyph.Contours.AddRange(Place(shape, 1, letter.CenterX, letter.Y0 - gap - d.Height));
                    return glyph;
                }

                double dx, toneGap;
                List<Contour> mark = Tone(tone, upper, out dx, out toneGap);
                if (hat || breve)
                {
                    List<Contour> under = Mark(vowel, plain);
                    if (under == null) return null;
                    Box u = Box.Of(under);
                    Box m = Box.Of(mark);
                    if (breve || tone == Tilde)
                        glyph.Contours.AddRange(Place(mark, StackScale, u.CenterX, u.Y1 + Math.Max(toneGap, 0) * 0.6));
                    else
                    {
                        // Vietnamese puts the acute, grave and hook beside the hat, on its right.
                        double width = m.Width * StackScale;
                        double centerX = u.X1 - width * 0.15 + width / 2;
                        glyph.Contours.AddRange(Place(mark, StackScale, centerX, u.Y0 + u.Height * 0.45));
                    }
                }
                else
                {
                    // The letter the font puts accents on: i has its own (dotless) form for them.
                    Glyph target = plain == 'i' && Get('ı') != null ? Get('ı') : plainGlyph;
                    Box t = Box.Of(target.Contours);
                    if (plain == 'i') { glyph.Contours.Clear(); glyph.Contours.AddRange(target.Contours); }
                    glyph.Contours.AddRange(Place(mark, 1, t.CenterX + dx, t.Y1 + toneGap));
                }
                return glyph;
            }

            private static char Plain(char vowel, bool upper)
            {
                string[] rows = upper ? Upper : Lower;
                string plains = upper ? "AAAEEIOOOUUY" : "aaaeeiooouuy";
                for (int i = 0; i < rows.Length; i++) if (rows[i][0] == vowel) return plains[i];
                return vowel;
            }
        }

        // ------------------------------------------------------------------------------------
        // The CFF table
        // ------------------------------------------------------------------------------------

        private sealed class DictEntry
        {
            internal int Op;
            internal byte[] Raw;
            internal double[] Args;
        }

        private sealed class Cff
        {
            private readonly byte[] data;
            private readonly int headerSize;
            private readonly List<byte[]> names, topDicts, strings, globalSubrs, charStrings, localSubrs;
            private readonly List<DictEntry> top;
            private readonly int[] charset;
            private readonly int privateOffset, privateSize, privateLength;
            internal readonly double DefaultWidth, NominalWidth;

            internal int Count { get { return charStrings.Count; } }

            internal Cff(byte[] d)
            {
                data = d;
                if (d[0] != 1) throw new NotSupportedException("CFF version " + d[0]);
                headerSize = d[2];
                int pos = headerSize;
                names = ReadIndex(d, ref pos);
                topDicts = ReadIndex(d, ref pos);
                strings = ReadIndex(d, ref pos);
                globalSubrs = ReadIndex(d, ref pos);
                if (topDicts.Count != 1) throw new NotSupportedException(topDicts.Count + " fonts in one CFF");
                top = ReadDict(topDicts[0], 0, topDicts[0].Length);
                if (Find(top, 1230) != null) throw new NotSupportedException("CID-keyed CFF");
                DictEntry e = Find(top, 16);
                if (e != null && e.Args[0] > 1) throw new NotSupportedException("custom CFF encoding");
                e = Find(top, 1206);
                if (e != null && e.Args[0] != 2) throw new NotSupportedException("charstring type " + e.Args[0]);

                int at = (int)Find(top, 17).Args[0];
                charStrings = ReadIndex(d, ref at);
                charset = ReadCharset(d, (int)Find(top, 15).Args[0], charStrings.Count);

                DictEntry priv = Find(top, 18);
                privateSize = (int)priv.Args[0];
                privateOffset = (int)priv.Args[1];
                List<DictEntry> p = ReadDict(d, privateOffset, privateSize);
                DictEntry w = Find(p, 20);
                DefaultWidth = w != null ? w.Args[0] : 0;
                w = Find(p, 21);
                NominalWidth = w != null ? w.Args[0] : 0;
                privateLength = privateSize;
                DictEntry subrs = Find(p, 19);
                if (subrs != null)
                {
                    int s = privateOffset + (int)subrs.Args[0];
                    localSubrs = ReadIndex(d, ref s);
                    privateLength = Math.Max(privateLength, s - privateOffset);
                }
                else localSubrs = new List<byte[]>();
            }

            internal Glyph Outline(int id) { return new Type2(this).Run(charStrings[id]); }

            /// <summary>The table again with the new glyphs added at the end.</summary>
            internal byte[] Write(List<byte[]> added, List<string> addedNames)
            {
                var allStrings = new List<byte[]>(strings);
                var allCharset = new List<int>(charset);
                foreach (string name in addedNames)
                {
                    allCharset.Add(391 + allStrings.Count);
                    allStrings.Add(Encoding.ASCII.GetBytes(name));
                }
                var allCharStrings = new List<byte[]>(charStrings);
                allCharStrings.AddRange(added);

                byte[] nameIndex = WriteIndex(names), stringIndex = WriteIndex(allStrings), gsubrIndex = WriteIndex(globalSubrs);
                var charsetBytes = new List<byte> { 0 };
                for (int i = 1; i < allCharset.Count; i++) { charsetBytes.Add((byte)(allCharset[i] >> 8)); charsetBytes.Add((byte)allCharset[i]); }
                byte[] charStringIndex = WriteIndex(allCharStrings);

                // The Top DICT's offsets are written as 5-byte numbers, so its size is known first.
                byte[] topDict = WriteTop(0, 0, 0);
                int topIndexSize = WriteIndex(new List<byte[]> { topDict }).Length;
                int charsetAt = headerSize + nameIndex.Length + topIndexSize + stringIndex.Length + gsubrIndex.Length;
                int charStringsAt = charsetAt + charsetBytes.Count;
                int privateAt = charStringsAt + charStringIndex.Length;
                topDict = WriteTop(charsetAt, charStringsAt, privateAt);

                var o = new List<byte>();
                for (int i = 0; i < headerSize; i++) o.Add(data[i]);
                o.AddRange(nameIndex);
                o.AddRange(WriteIndex(new List<byte[]> { topDict }));
                o.AddRange(stringIndex);
                o.AddRange(gsubrIndex);
                o.AddRange(charsetBytes);
                o.AddRange(charStringIndex);
                // The Private DICT and its subroutines, which sit after it, keep their bytes.
                for (int i = 0; i < privateLength; i++) o.Add(data[privateOffset + i]);
                return o.ToArray();
            }

            private byte[] WriteTop(int charsetAt, int charStringsAt, int privateAt)
            {
                var o = new List<byte>();
                foreach (DictEntry e in top)
                {
                    if (e.Op == 15) Int5(o, charsetAt);
                    else if (e.Op == 17) Int5(o, charStringsAt);
                    else if (e.Op == 18) { Int5(o, privateSize); Int5(o, privateAt); }
                    else o.AddRange(e.Raw);
                    if (e.Op >= 1200) { o.Add(12); o.Add((byte)(e.Op - 1200)); }
                    else o.Add((byte)e.Op);
                }
                return o.ToArray();
            }

            private static void Int5(List<byte> o, int v)
            {
                o.Add(29); o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
            }

            private static int[] ReadCharset(byte[] d, int at, int count)
            {
                var sids = new int[count];
                if (at <= 2) throw new NotSupportedException("predefined CFF charset");
                int format = d[at++], g = 1;
                while (g < count)
                {
                    if (format == 0) { sids[g++] = U16(d, at); at += 2; continue; }
                    int first = U16(d, at), left = format == 1 ? d[at + 2] : U16(d, at + 2);
                    at += format == 1 ? 3 : 4;
                    for (int i = 0; i <= left && g < count; i++) sids[g++] = first + i;
                }
                return sids;
            }

            /// <summary>Runs a Type 2 charstring into plain contours, without its hints.</summary>
            private sealed class Type2
            {
                private readonly Cff cff;
                private readonly List<double> stack = new List<double>();
                private readonly Glyph glyph = new Glyph();
                private readonly double[] storage = new double[32];
                private Contour current;
                private double x, y;
                private int stems, depth;
                private bool widthRead, done;

                internal Type2(Cff owner) { cff = owner; }

                internal Glyph Run(byte[] code)
                {
                    glyph.Width = cff.DefaultWidth;
                    Exec(code);
                    return glyph;
                }

                private static int Bias(int count) { return count < 1240 ? 107 : count < 33900 ? 1131 : 32768; }

                private void Width(bool extra)
                {
                    if (widthRead) return;
                    widthRead = true;
                    if (extra && stack.Count > 0) { glyph.Width = cff.NominalWidth + stack[0]; stack.RemoveAt(0); }
                }

                private void Move(double dx, double dy)
                {
                    x += dx; y += dy;
                    current = new Contour { X = x, Y = y };
                    glyph.Contours.Add(current);
                }

                private void Line(double dx, double dy)
                {
                    x += dx; y += dy;
                    current.Segments.Add(new[] { x, y });
                }

                private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
                {
                    double x1 = x + dx1, y1 = y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
                    x = x2 + dx3; y = y2 + dy3;
                    current.Segments.Add(new[] { x1, y1, x2, y2, x, y });
                }

                private double Pop()
                {
                    double v = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    return v;
                }

                private void Exec(byte[] code)
                {
                    if (++depth > 12) throw new NotSupportedException("subroutines nest too deep");
                    int i = 0;
                    while (i < code.Length && !done)
                    {
                        int b = code[i++];
                        if (b >= 32 || b == 28)
                        {
                            if (b == 28) { stack.Add((short)((code[i] << 8) | code[i + 1])); i += 2; }
                            else if (b <= 246) stack.Add(b - 139);
                            else if (b <= 250) stack.Add((b - 247) * 256 + code[i++] + 108);
                            else if (b <= 254) stack.Add(-(b - 251) * 256 - code[i++] - 108);
                            else { stack.Add(((code[i] << 24) | (code[i + 1] << 16) | (code[i + 2] << 8) | code[i + 3]) / 65536.0); i += 4; }
                            continue;
                        }
                        var s = stack;
                        int n = s.Count;
                        switch (b)
                        {
                            case 1: case 3: case 18: case 23:   // stems
                                Width(n % 2 == 1);
                                stems += s.Count / 2;
                                s.Clear();
                                break;
                            case 19: case 20:                   // hintmask, cntrmask
                                Width(n % 2 == 1);
                                stems += s.Count / 2;
                                s.Clear();
                                i += (stems + 7) / 8;
                                break;
                            case 21:
                                Width(n > 2);
                                Move(s[s.Count - 2], s[s.Count - 1]);
                                s.Clear();
                                break;
                            case 22:
                                Width(n > 1);
                                Move(s[s.Count - 1], 0);
                                s.Clear();
                                break;
                            case 4:
                                Width(n > 1);
                                Move(0, s[s.Count - 1]);
                                s.Clear();
                                break;
                            case 5:
                                for (int k = 0; k + 1 < n; k += 2) Line(s[k], s[k + 1]);
                                s.Clear();
                                break;
                            case 6: case 7:
                                for (int k = 0; k < n; k++)
                                {
                                    bool horizontal = (k % 2 == 0) == (b == 6);
                                    if (horizontal) Line(s[k], 0); else Line(0, s[k]);
                                }
                                s.Clear();
                                break;
                            case 8:
                                for (int k = 0; k + 5 < n; k += 6) Curve(s[k], s[k + 1], s[k + 2], s[k + 3], s[k + 4], s[k + 5]);
                                s.Clear();
                                break;
                            case 24:
                                {
                                    int k = 0;
                                    for (; k + 6 <= n - 2; k += 6) Curve(s[k], s[k + 1], s[k + 2], s[k + 3], s[k + 4], s[k + 5]);
                                    Line(s[k], s[k + 1]);
                                    s.Clear();
                                    break;
                                }
                            case 25:
                                {
                                    int k = 0;
                                    for (; k + 2 <= n - 6; k += 2) Line(s[k], s[k + 1]);
                                    Curve(s[k], s[k + 1], s[k + 2], s[k + 3], s[k + 4], s[k + 5]);
                                    s.Clear();
                                    break;
                                }
                            case 26:
                                {
                                    int k = 0;
                                    double dx1 = 0;
                                    if (n % 2 == 1) dx1 = s[k++];
                                    for (; k + 3 < n; k += 4) { Curve(dx1, s[k], s[k + 1], s[k + 2], 0, s[k + 3]); dx1 = 0; }
                                    s.Clear();
                                    break;
                                }
                            case 27:
                                {
                                    int k = 0;
                                    double dy1 = 0;
                                    if (n % 2 == 1) dy1 = s[k++];
                                    for (; k + 3 < n; k += 4) { Curve(s[k], dy1, s[k + 1], s[k + 2], s[k + 3], 0); dy1 = 0; }
                                    s.Clear();
                                    break;
                                }
                            case 30: case 31:
                                {
                                    bool horizontal = b == 31;
                                    for (int k = 0; k + 3 < n; )
                                    {
                                        bool last = n - k == 5;
                                        if (horizontal) Curve(s[k], 0, s[k + 1], s[k + 2], last ? s[k + 4] : 0, s[k + 3]);
                                        else Curve(0, s[k], s[k + 1], s[k + 2], s[k + 3], last ? s[k + 4] : 0);
                                        k += last ? 5 : 4;
                                        horizontal = !horizontal;
                                    }
                                    s.Clear();
                                    break;
                                }
                            case 10:
                                {
                                    int index = (int)Pop() + Bias(cff.localSubrs.Count);
                                    Exec(cff.localSubrs[index]);
                                    break;
                                }
                            case 29:
                                {
                                    int index = (int)Pop() + Bias(cff.globalSubrs.Count);
                                    Exec(cff.globalSubrs[index]);
                                    break;
                                }
                            case 11:
                                depth--;
                                return;
                            case 14:
                                Width(n == 1 || n == 5);
                                if (s.Count >= 4) throw new NotSupportedException("seac accented glyph");
                                s.Clear();
                                done = true;
                                break;
                            case 12:
                                Escape(code[i++]);
                                break;
                            default:
                                throw new NotSupportedException("charstring operator " + b);
                        }
                    }
                    depth--;
                }

                private void Escape(int op)
                {
                    var s = stack;
                    switch (op)
                    {
                        case 0: s.Clear(); break;                                     // dotsection
                        case 35:
                            Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                            Curve(s[6], s[7], s[8], s[9], s[10], s[11]);
                            s.Clear();
                            break;
                        case 34:
                            Curve(s[0], 0, s[1], s[2], s[3], 0);
                            Curve(s[4], 0, s[5], -s[2], s[6], 0);
                            s.Clear();
                            break;
                        case 36:
                            Curve(s[0], s[1], s[2], s[3], s[4], 0);
                            Curve(s[5], 0, s[6], s[7], s[8], -(s[1] + s[3] + s[7]));
                            s.Clear();
                            break;
                        case 37:
                            {
                                double dx = s[0] + s[2] + s[4] + s[6] + s[8], dy = s[1] + s[3] + s[5] + s[7] + s[9];
                                Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                                if (Math.Abs(dx) > Math.Abs(dy)) Curve(s[6], s[7], s[8], s[9], s[10], -dy);
                                else Curve(s[6], s[7], s[8], s[9], -dx, s[10]);
                                s.Clear();
                                break;
                            }
                        case 3: { double b = Pop(), a = Pop(); s.Add(a != 0 && b != 0 ? 1 : 0); break; }
                        case 4: { double b = Pop(), a = Pop(); s.Add(a != 0 || b != 0 ? 1 : 0); break; }
                        case 5: s.Add(Pop() == 0 ? 1 : 0); break;
                        case 9: s.Add(Math.Abs(Pop())); break;
                        case 10: { double b = Pop(), a = Pop(); s.Add(a + b); break; }
                        case 11: { double b = Pop(), a = Pop(); s.Add(a - b); break; }
                        case 12: { double b = Pop(), a = Pop(); s.Add(b == 0 ? 0 : a / b); break; }
                        case 14: s.Add(-Pop()); break;
                        case 15: { double b = Pop(), a = Pop(); s.Add(a == b ? 1 : 0); break; }
                        case 18: Pop(); break;
                        case 20: { int at = (int)Pop(); double v = Pop(); if (at >= 0 && at < storage.Length) storage[at] = v; break; }
                        case 21: { int at = (int)Pop(); s.Add(at >= 0 && at < storage.Length ? storage[at] : 0); break; }
                        case 22: { double v2 = Pop(), v1 = Pop(), s2 = Pop(), s1 = Pop(); s.Add(v1 <= v2 ? s1 : s2); break; }
                        case 23: s.Add(0.5); break;
                        case 24: { double b = Pop(), a = Pop(); s.Add(a * b); break; }
                        case 26: s.Add(Math.Sqrt(Math.Max(0, Pop()))); break;
                        case 27: { double v = Pop(); s.Add(v); s.Add(v); break; }
                        case 28: { double b = Pop(), a = Pop(); s.Add(b); s.Add(a); break; }
                        case 29:
                            {
                                int k = (int)Pop();
                                if (k < 0) k = 0;
                                s.Add(s[Math.Max(0, s.Count - 1 - k)]);
                                break;
                            }
                        case 30:
                            {
                                int j = (int)Pop(), count = (int)Pop();
                                if (count <= 0 || count > s.Count) break;
                                var part = s.GetRange(s.Count - count, count);
                                for (int k = 0; k < count; k++) s[s.Count - count + ((k + j) % count + count) % count] = part[k];
                                break;
                            }
                        default:
                            throw new NotSupportedException("charstring operator 12 " + op);
                    }
                }
            }
        }

        private static DictEntry Find(List<DictEntry> dict, int op)
        {
            foreach (DictEntry e in dict) if (e.Op == op) return e;
            return null;
        }

        private static List<DictEntry> ReadDict(byte[] d, int start, int length)
        {
            var entries = new List<DictEntry>();
            var args = new List<double>();
            int i = start, end = start + length, argStart = start;
            while (i < end)
            {
                int b = d[i];
                if (b <= 21)
                {
                    int op = b;
                    var raw = new byte[i - argStart];
                    Array.Copy(d, argStart, raw, 0, raw.Length);
                    i++;
                    if (b == 12) op = 1200 + d[i++];
                    entries.Add(new DictEntry { Op = op, Raw = raw, Args = args.ToArray() });
                    args.Clear();
                    argStart = i;
                    continue;
                }
                if (b == 28) { args.Add((short)((d[i + 1] << 8) | d[i + 2])); i += 3; }
                else if (b == 29) { args.Add((d[i + 1] << 24) | (d[i + 2] << 16) | (d[i + 3] << 8) | d[i + 4]); i += 5; }
                else if (b == 30)
                {
                    var text = new StringBuilder();
                    i++;
                    bool end_ = false;
                    while (!end_)
                    {
                        int pair = d[i++];
                        for (int half = 0; half < 2 && !end_; half++)
                        {
                            int nibble = half == 0 ? pair >> 4 : pair & 15;
                            if (nibble <= 9) text.Append((char)('0' + nibble));
                            else if (nibble == 10) text.Append('.');
                            else if (nibble == 11) text.Append('E');
                            else if (nibble == 12) text.Append("E-");
                            else if (nibble == 14) text.Append('-');
                            else if (nibble == 15) end_ = true;
                        }
                    }
                    double value;
                    double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                    args.Add(value);
                }
                else if (b >= 32 && b <= 246) { args.Add(b - 139); i++; }
                else if (b >= 247 && b <= 250) { args.Add((b - 247) * 256 + d[i + 1] + 108); i += 2; }
                else if (b >= 251 && b <= 254) { args.Add(-(b - 251) * 256 - d[i + 1] - 108); i += 2; }
                else throw new NotSupportedException("DICT byte " + b);
            }
            return entries;
        }

        private static List<byte[]> ReadIndex(byte[] d, ref int pos)
        {
            int count = U16(d, pos);
            pos += 2;
            var items = new List<byte[]>(count);
            if (count == 0) return items;
            int size = d[pos++];
            var offsets = new int[count + 1];
            for (int i = 0; i <= count; i++)
            {
                int v = 0;
                for (int k = 0; k < size; k++) v = (v << 8) | d[pos + i * size + k];
                offsets[i] = v;
            }
            int baseAt = pos + (count + 1) * size - 1;
            for (int i = 0; i < count; i++)
            {
                var item = new byte[offsets[i + 1] - offsets[i]];
                Array.Copy(d, baseAt + offsets[i], item, 0, item.Length);
                items.Add(item);
            }
            pos = baseAt + offsets[count];
            return items;
        }

        private static byte[] WriteIndex(List<byte[]> items)
        {
            var o = new List<byte>();
            o.Add((byte)(items.Count >> 8)); o.Add((byte)items.Count);
            if (items.Count == 0) return o.ToArray();
            int total = 1;
            foreach (byte[] item in items) total += item.Length;
            int size = total < 0x100 ? 1 : total < 0x10000 ? 2 : total < 0x1000000 ? 3 : 4;
            o.Add((byte)size);
            int offset = 1;
            for (int i = 0; i <= items.Count; i++)
            {
                for (int k = size - 1; k >= 0; k--) o.Add((byte)(offset >> (8 * k)));
                if (i < items.Count) offset += items[i].Length;
            }
            foreach (byte[] item in items) o.AddRange(item);
            return o.ToArray();
        }

        // ------------------------------------------------------------------------------------
        // Other tables
        // ------------------------------------------------------------------------------------

        private static Dictionary<int, int> ReadCmap(byte[] t)
        {
            var map = new Dictionary<int, int>();
            int count = U16(t, 2), best = -1, bestRank = -1;
            for (int i = 0; i < count; i++)
            {
                int platform = U16(t, 4 + i * 8), encoding = U16(t, 6 + i * 8);
                int offset = (int)U32(t, 8 + i * 8), format = U16(t, offset);
                int rank = format == 12 && (platform == 3 && encoding == 10 || platform == 0) ? 3
                    : format == 4 && (platform == 3 && encoding == 1 || platform == 0) ? 2 : -1;
                if (rank > bestRank) { bestRank = rank; best = offset; }
            }
            if (best < 0) throw new NotSupportedException("no Unicode cmap");
            if (U16(t, best) == 12)
            {
                long groups = U32(t, best + 12);
                for (int g = 0; g < groups; g++)
                {
                    int at = best + 16 + g * 12;
                    long start = U32(t, at), end = U32(t, at + 4), glyph = U32(t, at + 8);
                    for (long c = start; c <= end && c < 0x110000; c++) map[(int)c] = (int)(glyph + c - start);
                }
                return map;
            }
            int segments = U16(t, best + 6) / 2;
            int ends = best + 14, starts = ends + segments * 2 + 2, deltas = starts + segments * 2, ranges = deltas + segments * 2;
            for (int s = 0; s < segments; s++)
            {
                int end = U16(t, ends + s * 2), start = U16(t, starts + s * 2);
                int delta = S16(t, deltas + s * 2), range = U16(t, ranges + s * 2);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int g;
                    if (range == 0) g = (c + delta) & 0xFFFF;
                    else
                    {
                        int at = ranges + s * 2 + range + (c - start) * 2;
                        g = U16(t, at);
                        if (g != 0) g = (g + delta) & 0xFFFF;
                    }
                    if (g != 0) map[c] = g;
                }
            }
            return map;
        }

        /// <summary>A format 4 cmap, for both the Unicode and the Windows Unicode platform.</summary>
        private static byte[] WriteCmap(Dictionary<int, int> map)
        {
            var codes = new List<int>();
            foreach (int c in map.Keys) if (c < 0xFFFF) codes.Add(c);
            codes.Sort();
            var starts = new List<int>();
            var ends = new List<int>();
            var deltas = new List<int>();
            for (int i = 0; i < codes.Count; )
            {
                int j = i;
                while (j + 1 < codes.Count && codes[j + 1] == codes[j] + 1 && map[codes[j + 1]] == map[codes[j]] + 1) j++;
                starts.Add(codes[i]); ends.Add(codes[j]); deltas.Add((map[codes[i]] - codes[i]) & 0xFFFF);
                i = j + 1;
            }
            starts.Add(0xFFFF); ends.Add(0xFFFF); deltas.Add(1);
            int segments = starts.Count;
            int length = 16 + segments * 8;
            var sub = new byte[length];
            PutU16(sub, 0, 4); PutU16(sub, 2, length); PutU16(sub, 4, 0);
            PutU16(sub, 6, segments * 2);
            int power = 1, log = 0;
            while (power * 2 <= segments) { power *= 2; log++; }
            PutU16(sub, 8, power * 2); PutU16(sub, 10, log); PutU16(sub, 12, segments * 2 - power * 2);
            for (int s = 0; s < segments; s++)
            {
                PutU16(sub, 14 + s * 2, ends[s]);
                PutU16(sub, 16 + segments * 2 + s * 2, starts[s]);
                PutU16(sub, 16 + segments * 4 + s * 2, deltas[s]);
                PutU16(sub, 16 + segments * 6 + s * 2, 0);
            }
            var t = new byte[20 + length];
            PutU16(t, 0, 0); PutU16(t, 2, 2);
            PutU16(t, 4, 0); PutU16(t, 6, 3); PutU32(t, 8, 20);
            PutU16(t, 12, 3); PutU16(t, 14, 1); PutU32(t, 16, 20);
            Array.Copy(sub, 0, t, 20, length);
            return t;
        }

        private static void ReadMetrics(Sfnt sfnt, int glyphs, out int[] advances, out int[] bearings)
        {
            byte[] hmtx = sfnt.Table("hmtx");
            int metrics = U16(sfnt.Table("hhea"), 34);
            advances = new int[glyphs];
            bearings = new int[glyphs];
            for (int g = 0; g < glyphs; g++)
            {
                if (g < metrics) { advances[g] = U16(hmtx, g * 4); bearings[g] = S16(hmtx, g * 4 + 2); }
                else
                {
                    advances[g] = advances[metrics - 1];
                    int at = metrics * 4 + (g - metrics) * 2;
                    bearings[g] = at + 2 <= hmtx.Length ? S16(hmtx, at) : 0;
                }
            }
        }

        private static byte[] WriteMetrics(List<int> advances, List<int> bearings)
        {
            var t = new byte[advances.Count * 4];
            for (int g = 0; g < advances.Count; g++)
            {
                PutU16(t, g * 4, advances[g]);
                PutU16(t, g * 4 + 2, bearings[g] & 0xFFFF);
            }
            return t;
        }

        // ------------------------------------------------------------------------------------
        // The font file
        // ------------------------------------------------------------------------------------

        private sealed class Sfnt
        {
            private readonly uint version;
            private readonly Dictionary<string, byte[]> tables = new Dictionary<string, byte[]>();

            internal Sfnt(byte[] d)
            {
                version = U32(d, 0);
                if (version != 0x4F54544F) throw new NotSupportedException("not an OpenType CFF font");
                int count = U16(d, 4);
                for (int i = 0; i < count; i++)
                {
                    int at = 12 + i * 16;
                    string tag = Encoding.ASCII.GetString(d, at, 4);
                    int offset = (int)U32(d, at + 8), length = (int)U32(d, at + 12);
                    var table = new byte[length];
                    Array.Copy(d, offset, table, 0, length);
                    tables[tag] = table;
                }
            }

            internal bool Has(string tag) { return tables.ContainsKey(tag); }

            internal byte[] Table(string tag)
            {
                byte[] t;
                if (!tables.TryGetValue(tag, out t)) throw new NotSupportedException("no " + tag.Trim() + " table");
                return t;
            }

            internal void Set(string tag, byte[] table) { tables[tag] = table; }

            internal byte[] Write()
            {
                var tags = new List<string>(tables.Keys);
                tags.Sort(string.CompareOrdinal);
                int count = tags.Count, power = 1, log = 0;
                while (power * 2 <= count) { power *= 2; log++; }
                int size = 12 + count * 16;
                foreach (string tag in tags) size += (tables[tag].Length + 3) & ~3;
                var o = new byte[size];
                PutU32(o, 0, version);
                PutU16(o, 4, count); PutU16(o, 6, power * 16); PutU16(o, 8, log); PutU16(o, 10, count * 16 - power * 16);
                byte[] head = tables["head"];
                PutU32(head, 8, 0);
                int at = 12 + count * 16, headAt = -1;
                for (int i = 0; i < count; i++)
                {
                    byte[] t = tables[tags[i]];
                    int record = 12 + i * 16;
                    Encoding.ASCII.GetBytes(tags[i], 0, 4, o, record);
                    PutU32(o, record + 4, Sum(t, 0, t.Length));
                    PutU32(o, record + 8, (uint)at);
                    PutU32(o, record + 12, (uint)t.Length);
                    Array.Copy(t, 0, o, at, t.Length);
                    if (tags[i] == "head") headAt = at;
                    at += (t.Length + 3) & ~3;
                }
                if (headAt >= 0) PutU32(o, headAt + 8, unchecked(0xB1B0AFBA - Sum(o, 0, o.Length)));
                return o;
            }

            private static uint Sum(byte[] d, int start, int length)
            {
                uint sum = 0;
                for (int i = 0; i < length; i += 4)
                {
                    uint word = 0;
                    for (int k = 0; k < 4; k++) word = (word << 8) | (uint)(i + k < length ? d[start + i + k] : 0);
                    unchecked { sum += word; }
                }
                return sum;
            }
        }

        /// <summary>The size of an OpenType font starting at <paramref name="at"/>, or 0 when
        /// there is no plausible one there.</summary>
        internal static int FontLength(byte[] d, int at, int limit)
        {
            if (at + 12 > limit) return 0;
            int count = U16(d, at + 4);
            if (count < 4 || count > 64 || at + 12 + count * 16 > limit) return 0;
            long end = 12 + count * 16;
            for (int i = 0; i < count; i++)
            {
                int record = at + 12 + i * 16;
                for (int k = 0; k < 4; k++) if (d[record + k] < 32 || d[record + k] > 126) return 0;
                long table = (long)U32(d, record + 8) + U32(d, record + 12);
                if (table > 64 * 1024 * 1024) return 0;
                end = Math.Max(end, table);
            }
            return (int)end;
        }

        /// <summary>The font's family name (name ID 1), or null.</summary>
        internal static string Family(byte[] d, int at, int length)
        {
            int count = U16(d, at + 4);
            for (int i = 0; i < count; i++)
            {
                int record = at + 12 + i * 16;
                if (Encoding.ASCII.GetString(d, record, 4) != "name") continue;
                int t = at + (int)U32(d, record + 8);
                int names = U16(d, t + 2), strings = t + U16(d, t + 4);
                for (int n = 0; n < names; n++)
                {
                    int r = t + 6 + n * 12;
                    int platform = U16(d, r), id = U16(d, r + 6), len = U16(d, r + 8), off = U16(d, r + 10);
                    if (id != 1 || strings + off + len > at + length) continue;
                    if (platform == 3 || platform == 0) return Encoding.BigEndianUnicode.GetString(d, strings + off, len);
                    if (platform == 1) return Encoding.ASCII.GetString(d, strings + off, len);
                }
            }
            return null;
        }

        private static int U16(byte[] d, int at) { return (d[at] << 8) | d[at + 1]; }
        private static int S16(byte[] d, int at) { return (short)((d[at] << 8) | d[at + 1]); }
        private static uint U32(byte[] d, int at) { return (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]); }
        private static void PutU16(byte[] d, int at, int v) { d[at] = (byte)(v >> 8); d[at + 1] = (byte)v; }
        private static void PutU32(byte[] d, int at, uint v) { d[at] = (byte)(v >> 24); d[at + 1] = (byte)(v >> 16); d[at + 2] = (byte)(v >> 8); d[at + 3] = (byte)v; }
    }
}
