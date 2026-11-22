using System;
using System.Collections.Generic;
using UnityEngine;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// What players see when a special does something: a burst ring on the floor where it went off,
    /// a coloured ring under whatever it is working on (a pot cooking faster, a buffed chef, an
    /// item being pulled), a beam between two places, a word floating up ("x2", "SAVED!").
    ///
    /// Every effect is drawn on this machine only. Effects started from code that runs on every
    /// machine (movement, buffs, OnActivated) can use the plain methods; code that only runs on the
    /// host (cooking, orders, fire, items) uses the ...All methods, which send the effect through
    /// <see cref="SpecialNet"/> to every machine, the host included. Those are throttled, so they
    /// can be called every frame while something holds (a mark is re-sent before it runs out).
    ///
    /// Colours: each group of specials has its own (<see cref="ColourOf"/>), the same as its icon in
    /// the skill bar, so a ring's colour says what kind of special made it.
    /// </summary>
    internal static class SpecialFx
    {
        private enum Kind { Burst = 1, Mark = 2, Text = 3, Beam = 4 }

        private const int MaxEffects = 96;
        private const float RingLift = 0.04f;
        // World canvases: this many canvas units per metre.
        private const float UnitsPerMetre = 250f;

        private sealed class Effect
        {
            public Kind Kind;
            public GameObject Root;
            public RectTransform Rect;
            public Image Image;
            public Image Inner;
            public Text Label;
            public Transform Follow;
            public Renderer[] Renderers;
            public Vector3 Position, To;
            public Color Colour;
            public float Start, End, Radius;
            public string Key;
        }

        private static readonly List<Effect> s_effects = new List<Effect>();
        private static readonly Dictionary<string, Effect> s_marks = new Dictionary<string, Effect>();
        private static readonly Dictionary<string, float> s_sent = new Dictionary<string, float>();
        private static Sprite s_disc, s_ring, s_glow;
        private static Font s_font;
        private static Material s_onTop;
        private static Camera s_camera;
        private static int s_cameraFrame = -1;
        private static bool s_registered;

        // --- colours and glyphs -----------------------------------------------------------------

        private static readonly Color Movement = new Color(0.3f, 0.82f, 1f);
        private static readonly Color Carrying = new Color(1f, 0.62f, 0.2f);
        private static readonly Color Stations = new Color(0.45f, 0.92f, 0.38f);
        private static readonly Color Cooking = new Color(1f, 0.36f, 0.2f);
        private static readonly Color Orders = new Color(1f, 0.84f, 0.22f);
        private static readonly Color Team = new Color(1f, 0.46f, 0.76f);
        private static readonly Color Buffs = new Color(0.74f, 0.52f, 1f);
        private static readonly Color Hazards = new Color(0.28f, 1f, 0.8f);
        private static readonly Color Versus = new Color(1f, 0.24f, 0.3f);
        private static readonly Color Quirks = new Color(0.72f, 0.74f, 0.8f);

        /// <summary>The colour of a special's group (rings, bursts, its icon).</summary>
        internal static Color ColourOf(Special special)
        {
            int n = (int)special;
            if (n <= 20) return Movement;
            if (n <= 36) return Carrying;
            if (n <= 47) return Stations;
            if (n <= 58) return Cooking;
            if (n <= 71) return Orders;
            if (n <= 78) return Team;
            if (n <= 89) return Buffs;
            if (n <= 94) return Hazards;
            if (n <= 100) return Versus;
            return Quirks;
        }

        // --- icons -------------------------------------------------------------------------------

        private const int IconCell = 128;
        private const int IconColumns = 16;
        private static Texture2D s_iconSheet;
        private static bool s_iconSheetTried;
        private static readonly Dictionary<int, Sprite> s_icons = new Dictionary<int, Sprite>();

        /// <summary>
        /// The special's picture from the icon sheet built into the mod (Art/SpecialIcons.png,
        /// cell n = Special value n), or null when there is none.
        /// </summary>
        internal static Sprite IconOf(Special special)
        {
            int n = (int)special;
            if (n <= 0) return null;
            Sprite sprite;
            if (s_icons.TryGetValue(n, out sprite) && sprite != null) return sprite;
            Texture2D sheet = IconSheet();
            if (sheet == null) return null;
            int rows = sheet.height / IconCell;
            int col = n % IconColumns, row = n / IconColumns;
            if (row >= rows) return null;
            var rect = new Rect(col * IconCell, sheet.height - (row + 1) * IconCell, IconCell, IconCell);
            // An empty cell: no picture for this one.
            if (sheet.GetPixel(col * IconCell + IconCell / 2, sheet.height - row * IconCell - IconCell / 2).a <= 0f &&
                sheet.GetPixel(col * IconCell + IconCell / 3, sheet.height - row * IconCell - IconCell / 3).a <= 0f &&
                sheet.GetPixel(col * IconCell + IconCell * 2 / 3, sheet.height - row * IconCell - IconCell * 2 / 3).a <= 0f)
                return null;
            sprite = Sprite.Create(sheet, rect, new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s_icons[n] = sprite;
            return sprite;
        }

        private static Texture2D IconSheet()
        {
            if (s_iconSheet != null || s_iconSheetTried) return s_iconSheet;
            s_iconSheetTried = true;
            try
            {
                using (System.IO.Stream stream = typeof(SpecialFx).Assembly.GetManifestResourceStream("ChefSkills.SpecialIcons.png"))
                {
                    if (stream == null) return null;
                    var bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    var texture = new Texture2D(2, 2, TextureFormat.ARGB32, true);
                    if (!ImageConversion.LoadImage(texture, bytes, false)) return null;
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.filterMode = FilterMode.Trilinear;
                    texture.anisoLevel = 2;
                    texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    s_iconSheet = texture;
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Không tải được biểu tượng kỹ năng: " + error.Message);
            }
            return s_iconSheet;
        }

        /// <summary>
        /// Shows a special on an icon: its picture in <paramref name="picture"/>, or (no picture)
        /// its two letters in <paramref name="glyph"/>.
        /// </summary>
        internal static void ShowIcon(Image picture, Text glyph, Special special)
        {
            Sprite sprite = special != Special.None ? IconOf(special) : null;
            if (picture != null)
            {
                picture.sprite = sprite;
                picture.enabled = sprite != null;
            }
            if (glyph != null) glyph.text = sprite == null ? Glyph(special) : "";
        }

        /// <summary>Two letters for a special's icon: the first letters of its first two words, or of its one word.</summary>
        internal static string Glyph(Special special)
        {
            string name = SpecialCatalog.EnglishNameOf(special);
            if (string.IsNullOrEmpty(name) || special == Special.None) return "";
            var words = new List<string>();
            foreach (string part in name.Split(' ', '-', '\''))
                if (part.Length > 0 && char.IsLetter(part[0])) words.Add(part);
            if (words.Count >= 2) return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
            if (words.Count == 1) return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
            return name.Substring(0, Math.Min(2, name.Length)).ToUpperInvariant();
        }

        /// <summary>
        /// A special's name followed by more words, in both languages (<see cref="Lang.Both"/>), for
        /// text sent to other machines: each screen shows it in its own player's language.
        /// </summary>
        internal static string NameBoth(Special special, string en, string vi)
        {
            SpecialInfo info = SpecialCatalog.Info(special);
            string nameEn = info != null ? info.NameEn : "NONE";
            string nameVi = info != null && !string.IsNullOrEmpty(info.NameVi) ? info.NameVi : nameEn;
            return Lang.Both(nameEn + en, nameVi + vi);
        }

        // --- shared art -------------------------------------------------------------------------

        /// <summary>A soft white disc.</summary>
        internal static Sprite Disc
        {
            get
            {
                if (s_disc == null) s_disc = MakeSprite(128, delegate(float r) { return Mathf.Clamp01((1f - r) * 24f); });
                return s_disc;
            }
        }

        /// <summary>A white ring (outer edge at the sprite's edge, 14% thick).</summary>
        internal static Sprite Ring
        {
            get
            {
                if (s_ring == null)
                    s_ring = MakeSprite(128, delegate(float r) { return Mathf.Clamp01((1f - r) * 24f) * Mathf.Clamp01((r - 0.84f) * 24f); });
                return s_ring;
            }
        }

        /// <summary>A white glow, brightest in the middle.</summary>
        internal static Sprite Glow
        {
            get
            {
                if (s_glow == null) s_glow = MakeSprite(64, delegate(float r) { float a = Mathf.Clamp01(1f - r); return a * a; });
                return s_glow;
            }
        }

        /// <summary>The game's header font (falls back to Arial).</summary>
        internal static Font Font
        {
            get
            {
                if (s_font != null) return s_font;
                foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
                    if (font != null && font.name == "Header_Font")
                    {
                        s_font = font;
                        break;
                    }
                if (s_font == null) s_font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                return s_font;
            }
        }

        private static Font s_bodyFont;

        /// <summary>The game's body font (plain, easy to read in sentences; falls back to the header font).</summary>
        internal static Font BodyFont
        {
            get
            {
                if (s_bodyFont != null) return s_bodyFont;
                foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
                    if (font != null && font.name == "Body_Font")
                    {
                        s_bodyFont = font;
                        break;
                    }
                return s_bodyFont ?? Font;
            }
        }

        private static Sprite s_pill, s_frame, s_halo;

        /// <summary>A white rounded box that stretches (9-sliced, corner radius 14): tags, labels.</summary>
        internal static Sprite RoundBox
        {
            get
            {
                if (s_pill == null) s_pill = MakeSliced(64, 16, 0f, 14f, delegate(float d) { return Mathf.Clamp01(0.5f - d); });
                return s_pill;
            }
        }

        /// <summary>A white rounded outline, 5 thick, that stretches (9-sliced).</summary>
        internal static Sprite RoundFrame
        {
            get
            {
                if (s_frame == null)
                    s_frame = MakeSliced(64, 20, 0f, 14f, delegate(float d) { return Mathf.Clamp01(0.5f - d) * Mathf.Clamp01(d + 5.5f); });
                return s_frame;
            }
        }

        /// <summary>A soft white glow round a box that stretches (9-sliced): the glow fades over its 24 px border.</summary>
        internal static Sprite RoundHalo
        {
            get
            {
                if (s_halo == null)
                    s_halo = MakeSliced(64, 31, 24f, 6f, delegate(float d)
                    {
                        if (d <= 0f) return 1f;
                        float a = Mathf.Clamp01(1f - d / 24f);
                        return a * a;
                    });
                return s_halo;
            }
        }

        /// <summary>
        /// A 9-sliced sprite from the signed distance (pixels, negative inside) to a rounded box
        /// inset <paramref name="inset"/> from the texture's edge.
        /// </summary>
        private static Sprite MakeSliced(int size, int border, float inset, float radius, Func<float, float> alpha)
        {
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            float box = half - inset - radius;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - half) - box, qy = Mathf.Abs(y + 0.5f - half) - box;
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                    float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha(d)) * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }

        private static Sprite MakeSprite(int size, Func<float, float> alpha)
        {
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha(r)) * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }

        /// <summary>UI material drawn over the kitchen (labels never hide behind a counter).</summary>
        private static Material OnTop
        {
            get
            {
                if (s_onTop == null)
                {
                    s_onTop = new Material(Canvas.GetDefaultCanvasMaterial());
                    s_onTop.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
                    s_onTop.hideFlags = HideFlags.DontUnloadUnusedAsset;
                }
                return s_onTop;
            }
        }

        /// <summary>The camera the kitchen is seen through (cached per frame).</summary>
        internal static Camera GameCamera()
        {
            if (s_cameraFrame == Time.frameCount && s_camera != null) return s_camera;
            s_cameraFrame = Time.frameCount;
            s_camera = null;
            Camera main = Camera.main;
            if (main != null && main.isActiveAndEnabled)
            {
                s_camera = main;
                return main;
            }
            foreach (Camera candidate in Camera.allCameras)
            {
                if (!candidate.isActiveAndEnabled || candidate.targetTexture != null || candidate.orthographic) continue;
                if (s_camera == null || candidate.depth < s_camera.depth) s_camera = candidate;
            }
            return s_camera;
        }

        // --- this machine -----------------------------------------------------------------------

        /// <summary>A ring that spreads out on the floor and fades (something went off here).</summary>
        internal static void Burst(Vector3 position, Color colour, float radius, float seconds)
        {
            Effect effect = Make(Kind.Burst, "Burst", null, false);
            if (effect == null) return;
            effect.Image.sprite = Ring;
            effect.Inner.sprite = Glow;
            effect.Position = position;
            effect.Colour = colour;
            effect.Radius = Mathf.Max(0.3f, radius);
            effect.End = effect.Start + Mathf.Max(0.15f, seconds);
            Place(effect);
        }

        internal static void Burst(Vector3 position, Special special)
        {
            Burst(position, ColourOf(special), 1.4f, 0.5f);
        }

        /// <summary>
        /// A coloured ring under <paramref name="target"/> (following it) for <paramref name="seconds"/>,
        /// with an optional word over it. Marking the same target with the same label again just
        /// keeps it longer, so it can be called every frame while something holds.
        /// </summary>
        internal static void Mark(GameObject target, Color colour, float seconds, string label, float radius)
        {
            if (target == null) return;
            string key = target.GetInstanceID() + "/" + (label ?? "") + "/" + ColorUtility.ToHtmlStringRGB(colour);
            Effect effect;
            float now = Time.unscaledTime;
            if (s_marks.TryGetValue(key, out effect) && effect.Root != null)
            {
                effect.End = Mathf.Max(effect.End, now + seconds);
                return;
            }
            effect = Make(Kind.Mark, "Mark", label, false);
            if (effect == null) return;
            effect.Image.sprite = Ring;
            effect.Inner.sprite = Glow;
            effect.Follow = target.transform;
            effect.Renderers = target.GetComponentsInChildren<Renderer>();
            effect.Colour = colour;
            effect.Radius = radius > 0f ? radius : DefaultRadius(target);
            effect.End = now + seconds;
            effect.Key = key;
            s_marks[key] = effect;
            Place(effect);
        }

        internal static void Mark(GameObject target, Special special, float seconds, string label)
        {
            Mark(target, ColourOf(special), seconds, label, 0f);
        }

        /// <summary>Stops every mark on this target now.</summary>
        internal static void Unmark(GameObject target)
        {
            if (target == null) return;
            Transform t = target.transform;
            for (int i = 0; i < s_effects.Count; i++)
                if (s_effects[i].Kind == Kind.Mark && s_effects[i].Follow == t) s_effects[i].End = 0f;
        }

        /// <summary>A word that floats up from over <paramref name="target"/> and fades.</summary>
        internal static void Text(GameObject target, string text, Color colour)
        {
            if (target == null || string.IsNullOrEmpty(text)) return;
            Text(Top(target), text, colour);
        }

        /// <summary>A word that floats up from <paramref name="position"/> and fades.</summary>
        internal static void Text(Vector3 position, string text, Color colour)
        {
            if (string.IsNullOrEmpty(text)) return;
            Effect effect = Make(Kind.Text, "Text", text, true);
            if (effect == null) return;
            effect.Position = position;
            effect.Colour = colour;
            effect.End = effect.Start + 1.3f;
            Place(effect);
        }

        /// <summary>A band along the floor from one place to another (a teleport, a pull, a throw), fading.</summary>
        internal static void Beam(Vector3 from, Vector3 to, Color colour, float seconds)
        {
            Effect effect = Make(Kind.Beam, "Beam", null, false);
            if (effect == null) return;
            effect.Image.sprite = null;
            effect.Inner.enabled = false;
            effect.Position = from;
            effect.To = to;
            effect.Colour = colour;
            effect.End = effect.Start + Mathf.Max(0.15f, seconds);
            Place(effect);
        }

        // --- every machine ----------------------------------------------------------------------

        /// <summary><see cref="Burst(Vector3, Special)"/> on every machine.</summary>
        internal static void BurstAll(Vector3 position, Special special)
        {
            Send(Kind.Burst, 0, position, new Vector3(1.4f, 0f, 0f), 0.5f, special, null);
        }

        /// <summary>
        /// <see cref="Mark(GameObject, Special, float, string)"/> on every machine. Throttled: while
        /// the same mark is still showing it isn't sent again until it's over half gone, so call it
        /// as often as you like while the effect holds.
        /// </summary>
        internal static void MarkAll(GameObject target, Special special, float seconds, string label)
        {
            if (target == null) return;
            uint id = SkillManager.EntityId(target);
            if (id == 0)
            {
                Mark(target, special, seconds, label);
                return;
            }
            string key = "M" + id + "/" + (int)special + "/" + label;
            if (Throttled(key, seconds * 0.5f)) return;
            Send(Kind.Mark, id, target.transform.position, Vector3.zero, seconds, special, label);
        }

        /// <summary><see cref="Text(GameObject, string, Color)"/> on every machine (the same word at most every 0.4 s).</summary>
        internal static void TextAll(GameObject target, string text, Special special)
        {
            if (target == null || string.IsNullOrEmpty(text)) return;
            uint id = SkillManager.EntityId(target);
            if (Throttled("T" + (id != 0 ? id.ToString() : target.GetInstanceID().ToString()) + "/" + text, 0.4f)) return;
            if (id == 0)
            {
                Text(target, text, ColourOf(special));
                return;
            }
            Send(Kind.Text, id, Top(target), Vector3.zero, 0f, special, text);
        }

        /// <summary><see cref="Text(Vector3, string, Color)"/> on every machine.</summary>
        internal static void TextAllAt(Vector3 position, string text, Special special)
        {
            if (string.IsNullOrEmpty(text)) return;
            Send(Kind.Text, 0, position, Vector3.zero, 0f, special, text);
        }

        /// <summary><see cref="Beam"/> on every machine.</summary>
        internal static void BeamAll(Vector3 from, Vector3 to, Special special, float seconds)
        {
            Send(Kind.Beam, 0, from, to, seconds, special, null);
        }

        private static bool Throttled(string key, float seconds)
        {
            float now = Time.unscaledTime;
            float last;
            if (s_sent.TryGetValue(key, out last) && now - last < seconds) return true;
            s_sent[key] = now;
            if (s_sent.Count > 512) s_sent.Clear();
            return false;
        }

        private static void Send(Kind kind, uint target, Vector3 a, Vector3 b, float seconds, Special special, string text)
        {
            Register();
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = SpecialNet.Topic.Fx, Chef = target, Vector1 = a, Vector2 = b, G = seconds,
                I = (int)kind | ((int)special << 8), Text = text
            });
        }

        /// <summary>Hooks the effect messages up (from SpecialRuntime.RegisterModules).</summary>
        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            SpecialNet.Register(SpecialNet.Topic.Fx, OnMessage);
        }

        private static void OnMessage(SpecialMessage message)
        {
            if (!SpecialRuntime.InLevel) return;
            var kind = (Kind)(message.I & 0xFF);
            var special = (Special)(message.I >> 8);
            Color colour = ColourOf(special);
            GameObject target = null;
            if (message.Chef != 0)
            {
                EntitySerialisationEntry entry = EntitySerialisationRegistry.GetEntry(message.Chef);
                target = entry != null ? entry.m_GameObject : null;
            }
            switch (kind)
            {
                case Kind.Burst:
                    Burst(message.Vector1, colour, message.D > 0f ? message.D : 1.4f, message.G > 0f ? message.G : 0.5f);
                    break;
                case Kind.Mark:
                    if (target != null) Mark(target, colour, message.G, message.Text, 0f);
                    break;
                case Kind.Text:
                    if (target != null) Text(target, message.Text, colour);
                    else Text(message.Vector1, message.Text, colour);
                    break;
                case Kind.Beam:
                    Beam(message.Vector1, message.Vector2, colour, message.G);
                    break;
            }
        }

        // --- drawing ----------------------------------------------------------------------------

        private static float DefaultRadius(GameObject target)
        {
            if (target.GetComponent<PlayerControls>() != null) return 0.7f;
            Bounds bounds;
            if (!BoundsOf(target.GetComponentsInChildren<Renderer>(), out bounds)) return 0.5f;
            return Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.15f, 0.35f, 1.1f);
        }

        private static bool BoundsOf(Renderer[] renderers, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            if (renderers == null) return false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || !r.enabled || r.GetType().Name == "ParticleSystemRenderer") continue;
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        /// <summary>Just over the top of an object (chefs: over the head).</summary>
        private static Vector3 Top(GameObject target)
        {
            if (target.GetComponent<PlayerControls>() != null) return target.transform.position + Vector3.up * 2.1f;
            Bounds bounds;
            if (BoundsOf(target.GetComponentsInChildren<Renderer>(), out bounds))
                return new Vector3(bounds.center.x, bounds.max.y + 0.35f, bounds.center.z);
            return target.transform.position + Vector3.up * 0.8f;
        }

        private static Effect Make(Kind kind, string name, string label, bool onTop)
        {
            if (!SpecialRuntime.InLevel) return null;
            while (s_effects.Count >= MaxEffects) Remove(0);
            var root = new GameObject("ChefSkillsFx_" + name, typeof(RectTransform));
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = onTop ? 120 : 60;
            var rect = (RectTransform)root.transform;
            rect.sizeDelta = new Vector2(100f, 100f);
            rect.localScale = Vector3.one / UnitsPerMetre;

            var effect = new Effect { Kind = kind, Root = root, Rect = rect, Start = Time.unscaledTime };
            if (kind != Kind.Text)
            {
                effect.Inner = AddImage(root.transform, "Inner", false);
                effect.Image = AddImage(root.transform, "Ring", false);
            }
            if (!string.IsNullOrEmpty(label))
            {
                // Labels stand up and face the camera; they are a child canvas for Mark (which lies flat).
                Transform parent = root.transform;
                if (kind == Kind.Mark)
                {
                    var holder = new GameObject("LabelCanvas", typeof(RectTransform));
                    holder.transform.SetParent(null, false);
                    Canvas labelCanvas = holder.AddComponent<Canvas>();
                    labelCanvas.renderMode = RenderMode.WorldSpace;
                    labelCanvas.sortingOrder = 120;
                    ((RectTransform)holder.transform).sizeDelta = new Vector2(100f, 100f);
                    holder.transform.localScale = Vector3.one / UnitsPerMetre;
                    holder.transform.SetParent(root.transform.parent, true);
                    parent = holder.transform;
                }
                effect.Label = AddText(parent, label, kind == Kind.Text ? 92 : 70);
            }
            s_effects.Add(effect);
            return effect;
        }

        private static Image AddImage(Transform parent, string name, bool onTop)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(100f, 100f);
            Image image = go.AddComponent<Image>();
            image.raycastTarget = false;
            if (onTop) image.material = OnTop;
            return image;
        }

        private static Text AddText(Transform parent, string text, int size)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(900f, 160f);
            Text label = go.AddComponent<Text>();
            label.raycastTarget = false;
            label.supportRichText = false;
            label.font = Font;
            label.fontSize = size;
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.text = Lang.Pick(text);
            label.material = OnTop;
            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.04f, 0.06f, 0.1f, 0.9f);
            outline.effectDistance = new Vector2(5f, -5f);
            return label;
        }

        /// <summary>Every frame (from SpecialRuntime.Update): moves, fades and ends the effects.</summary>
        internal static void Update()
        {
            if (s_effects.Count == 0) return;
            float now = Time.unscaledTime;
            Camera camera = GameCamera();
            bool hide = SpecialHud.GameMenuOpen();
            for (int i = s_effects.Count - 1; i >= 0; i--)
            {
                Effect effect = s_effects[i];
                if (effect.Root == null || now >= effect.End || (effect.Kind == Kind.Mark && effect.Follow == null))
                {
                    Remove(i);
                    continue;
                }
                // Nothing of ours over the pause menu or a dialog.
                if (effect.Root.activeSelf == hide) effect.Root.SetActive(!hide);
                if (hide) continue;
                Place(effect, now, camera);
            }
        }

        private static void Place(Effect effect)
        {
            Place(effect, Time.unscaledTime, GameCamera());
        }

        private static void Place(Effect effect, float now, Camera camera)
        {
            float life = Mathf.Max(0.01f, effect.End - effect.Start);
            float t = Mathf.Clamp01((now - effect.Start) / life);
            Transform root = effect.Root.transform;
            Color colour = effect.Colour;
            switch (effect.Kind)
            {
                case Kind.Burst:
                {
                    root.position = effect.Position + Vector3.up * RingLift;
                    root.rotation = Quaternion.Euler(90f, 0f, 0f);
                    float size = effect.Radius * 2f * Mathf.Lerp(0.25f, 1f, 1f - (1f - t) * (1f - t)) * UnitsPerMetre;
                    effect.Image.rectTransform.sizeDelta = new Vector2(size, size);
                    effect.Inner.rectTransform.sizeDelta = new Vector2(size, size);
                    colour.a = 1f - t;
                    effect.Image.color = colour;
                    Color glow = effect.Colour;
                    glow.a = 0.45f * (1f - t);
                    effect.Inner.color = glow;
                    break;
                }
                case Kind.Mark:
                {
                    Vector3 at = effect.Follow.position;
                    Bounds bounds;
                    float floor = effect.Follow.GetComponent<PlayerControls>() != null || !BoundsOf(effect.Renderers, out bounds)
                        ? at.y
                        : bounds.min.y;
                    root.position = new Vector3(at.x, floor + RingLift, at.z);
                    root.rotation = Quaternion.Euler(90f, (now * 60f) % 360f, 0f);
                    // Pops in, then breathes; fades over its last quarter second.
                    float age = now - effect.Start;
                    float pop = age < 0.2f ? Mathf.Lerp(1.5f, 1f, age / 0.2f) : 1f + 0.06f * Mathf.Sin(age * 6f);
                    float size = effect.Radius * 2f * pop * UnitsPerMetre;
                    effect.Image.rectTransform.sizeDelta = new Vector2(size, size);
                    effect.Inner.rectTransform.sizeDelta = new Vector2(size, size);
                    float fade = Mathf.Clamp01((effect.End - now) / 0.25f);
                    colour.a = 0.95f * fade;
                    effect.Image.color = colour;
                    Color glow = effect.Colour;
                    glow.a = 0.3f * fade;
                    effect.Inner.color = glow;
                    if (effect.Label != null)
                    {
                        Transform label = effect.Label.transform.parent;
                        label.position = new Vector3(at.x, Top(effect.Follow.gameObject).y + 0.1f, at.z);
                        if (camera != null) label.rotation = camera.transform.rotation;
                        Color text = effect.Colour;
                        text.a = fade;
                        effect.Label.color = text;
                    }
                    break;
                }
                case Kind.Text:
                {
                    root.position = effect.Position + Vector3.up * (0.9f * (1f - (1f - t) * (1f - t)));
                    if (camera != null) root.rotation = camera.transform.rotation;
                    float pop = t < 0.12f ? Mathf.Lerp(1.4f, 1f, t / 0.12f) : 1f;
                    root.localScale = Vector3.one * (pop / UnitsPerMetre);
                    colour.a = t < 0.7f ? 1f : 1f - (t - 0.7f) / 0.3f;
                    effect.Label.color = colour;
                    break;
                }
                case Kind.Beam:
                {
                    Vector3 from = effect.Position, to = effect.To;
                    Vector3 flat = to - from;
                    flat.y = 0f;
                    float length = Mathf.Max(0.1f, flat.magnitude);
                    root.position = (from + to) * 0.5f + Vector3.up * (RingLift + 0.02f);
                    root.rotation = Quaternion.LookRotation(Vector3.down, flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward);
                    effect.Image.rectTransform.sizeDelta = new Vector2(0.28f * (1f - 0.5f * t) * UnitsPerMetre, length * UnitsPerMetre);
                    colour.a = 0.85f * (1f - t);
                    effect.Image.color = colour;
                    break;
                }
            }
        }

        private static void Remove(int index)
        {
            Effect effect = s_effects[index];
            s_effects.RemoveAt(index);
            if (effect.Key != null)
            {
                Effect current;
                if (s_marks.TryGetValue(effect.Key, out current) && current == effect) s_marks.Remove(effect.Key);
            }
            if (effect.Label != null && effect.Kind == Kind.Mark && effect.Label.transform.parent != null)
                Object.Destroy(effect.Label.transform.parent.gameObject);
            if (effect.Root != null) Object.Destroy(effect.Root);
        }

        /// <summary>Removes every effect (the level ended).</summary>
        internal static void Clear()
        {
            for (int i = s_effects.Count - 1; i >= 0; i--) Remove(i);
            s_effects.Clear();
            s_marks.Clear();
            s_sent.Clear();
        }
    }
}
