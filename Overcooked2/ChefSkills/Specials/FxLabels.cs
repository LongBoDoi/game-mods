using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ChefSkills
{
    /// <summary>
    /// Every word the mod shows over something in the kitchen: the labels of rings (x2, ECHO,
    /// CHOPPING +36%, SWAP?...), the floating words (SAVED!, NOT READY...) and the aiming hint.
    ///
    /// The words over one thing make one stack: over a station and what stands on it (a board
    /// and its item, a cooker and its pot, a sink), or over a chef and what it holds, whichever
    /// skill or special they come from. Labels stand one above the other at the bottom, the
    /// oldest lowest, each in its own colour; floating words pop up above them and fly up as they
    /// fade, never over one another.
    ///
    /// The stack sits above the game's own icons over the same things (ingredient icons, progress
    /// bars, cooking dials, emote bubbles) and the over-head skill bar. On screen the words are
    /// drawn over the kitchen's icons, the game's HUD (orders, timer, score) and the skill bar,
    /// and under what is there to be read or chosen: the skill info cards, the skill screen, an
    /// open emote wheel, and (hidden then) the pause menu, dialogs, round results, tutorial
    /// popups, cutscenes and screen wipes.
    /// </summary>
    internal static class FxLabels
    {
        /// <summary>Word sizes, in metres of letter height at the thing they are over.</summary>
        internal const float FloatSize = 0.37f;
        internal const float LabelSize = 0.28f;
        // The skills' station labels (CHOPPING +36%): longer, so smaller, to keep a stack of them low.
        internal const float SkillLabelSize = 0.19f;

        private const float FloatSeconds = 1.5f;
        private const float FloatFade = 0.45f;
        // How far a floating word flies up over its life, in metres.
        private const float FloatRise = 0.9f;
        private const float HeldFade = 0.25f;
        private const int MaxFloating = 5;
        // Room left between the game's icons (or the thing's top) and the first word, in metres.
        private const float Gap = 0.12f;
        private const int FontSize = 48;
        // Over the skill bar (4000), under the skill screen (5000); the info cards go just above.
        private const int BaseOrder = 4500;

        internal sealed class Line
        {
            public string Key;
            public string Text;
            public Color Colour;
            public float Start, End, Metres;
            public bool Held;
            internal RectTransform Rect;
            internal Text Label;
            internal float Y;
            internal bool Placed;
            // 1 = shown, 0 = given way to something drawn over the words (an emote wheel).
            internal float Shown = 1f;
        }

        private sealed class Stack
        {
            public Transform Root;
            public bool HasRoot, IsChef;
            public Vector3 Point;
            public Vector3 Anchor;
            public readonly List<Line> Lines = new List<Line>();
            public Renderer[] Renderers;
            public float RenderersAt = float.MinValue;
            // The middle of the station, in its own space (its pivot can be off to one side).
            public Vector3 LocalCentre;
        }

        private sealed class Cover
        {
            public GameObject Root;
            public Graphic[] Graphics;
        }

        private static readonly List<Stack> s_stacks = new List<Stack>();
        private static readonly List<Line> s_held = new List<Line>();
        private static readonly List<Line> s_floating = new List<Line>();
        private static readonly Vector3[] s_corners = new Vector3[4];
        private static GameObject s_canvasObject;
        private static Canvas s_canvas;
        private static RectTransform s_layer;
        private static int s_order = BaseOrder;
        private static Canvas s_iconCanvas;
        private static Canvas s_hudCanvas;
        private static HoverIconUIController[] s_icons = new HoverIconUIController[0];
        private static readonly Dictionary<HoverIconUIController, Graphic[]> s_graphics = new Dictionary<HoverIconUIController, Graphic[]>();
        private static float s_iconsAt = float.MinValue;
        private static float s_wheelsAt = float.MinValue;
        // Things drawn over the words, and where they are this frame (in the layer's units).
        private static readonly List<Cover> s_covers = new List<Cover>();
        private static readonly List<Rect> s_coverRects = new List<Rect>();

        private static readonly FieldInfo WheelSelector =
            HarmonyLib.AccessTools.Field(typeof(ClientEmoteWheel), "m_emoteSelector");
        private static readonly FieldInfo SelectorObject =
            HarmonyLib.AccessTools.Field(typeof(EmoteSelector), "m_gameObject");

        /// <summary>The words' place among the screen layers (the info cards go just over it).</summary>
        internal static int SortingOrder
        {
            get { return s_order; }
        }

        // --- adding words ---------------------------------------------------------------------------

        /// <summary>A word that pops up over <paramref name="target"/> (or at <paramref name="at"/>), flies up and fades.</summary>
        internal static void Float(GameObject target, Vector3 at, string text, Color colour)
        {
            if (string.IsNullOrEmpty(text)) return;
            Stack stack = StackFor(target, at);
            if (stack == null) return;
            string shown = Lang.Pick(text);
            float now = Time.unscaledTime;
            // The same word again while it is still fresh: it pops again rather than showing twice.
            int floating = 0, oldest = -1;
            for (int i = 0; i < stack.Lines.Count; i++)
            {
                Line old = stack.Lines[i];
                if (old.Held) continue;
                if (old.Text == shown && now - old.Start < 0.5f)
                {
                    old.Start = now;
                    old.End = now + FloatSeconds;
                    old.Colour = colour;
                    return;
                }
                floating++;
                if (oldest < 0 || old.Start < stack.Lines[oldest].Start) oldest = i;
            }
            if (floating >= MaxFloating && oldest >= 0)
            {
                Destroy(stack.Lines[oldest]);
                stack.Lines.RemoveAt(oldest);
            }
            stack.Lines.Add(new Line
            {
                Text = shown, Colour = colour, Start = now, End = now + FloatSeconds, Metres = FloatSize
            });
        }

        /// <summary>
        /// A label that stays over <paramref name="target"/> (or at <paramref name="at"/>) for
        /// <paramref name="seconds"/>, stacked with the other labels there; holding the same key
        /// again keeps it (and updates its word).
        /// </summary>
        internal static Line Hold(GameObject target, Vector3 at, string key, string text, Color colour, float seconds, float metres)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Stack stack = StackFor(target, at);
            if (stack == null) return null;
            float now = Time.unscaledTime;
            string shown = Lang.Pick(text);
            for (int i = 0; i < stack.Lines.Count; i++)
            {
                Line old = stack.Lines[i];
                if (!old.Held || old.Key != key) continue;
                old.End = Mathf.Max(old.End, now + seconds);
                old.Colour = colour;
                old.Text = shown;
                return old;
            }
            var line = new Line
            {
                Key = key, Text = shown, Colour = colour, Start = now, End = now + seconds, Metres = metres, Held = true
            };
            stack.Lines.Add(line);
            return line;
        }

        /// <summary>
        /// The stack for a thing: a chef (with what it holds), or the station something stands on
        /// (the highest one, so a pot on a cooker shares the cooker's). Without a thing, the stack
        /// nearest the spot, or a new one there.
        /// </summary>
        private static Stack StackFor(GameObject target, Vector3 at)
        {
            if (!SpecialRuntime.InLevel) return null;
            if (target != null)
            {
                bool chef;
                Transform root = RootOf(target.transform, out chef);
                for (int i = 0; i < s_stacks.Count; i++)
                    if (s_stacks[i].HasRoot && s_stacks[i].Root == root) return s_stacks[i];
                var stack = new Stack { Root = root, HasRoot = true, IsChef = chef };
                stack.Anchor = AnchorOf(stack);
                s_stacks.Add(stack);
                return stack;
            }
            Stack best = null;
            float bestDistance = 0.8f;
            for (int i = 0; i < s_stacks.Count; i++)
            {
                Vector3 anchor = s_stacks[i].Anchor;
                if (Mathf.Abs(anchor.y - at.y) > 2.5f) continue;
                float distance = new Vector2(anchor.x - at.x, anchor.z - at.z).magnitude;
                if (distance < bestDistance)
                {
                    best = s_stacks[i];
                    bestDistance = distance;
                }
            }
            if (best != null) return best;
            best = new Stack { Point = at, Anchor = at };
            s_stacks.Add(best);
            return best;
        }

        private static Transform RootOf(Transform target, out bool chef)
        {
            Transform station = null;
            for (Transform t = target; t != null; t = t.parent)
            {
                if (t.GetComponent<PlayerControls>() != null)
                {
                    chef = true;
                    return t;
                }
                if (t.GetComponent<AttachStation>() != null || t.GetComponent<WashingStation>() != null) station = t;
            }
            chef = false;
            return station != null ? station : target;
        }

        /// <summary>Where a stack's words start in the world: over the thing's top.</summary>
        private static Vector3 AnchorOf(Stack stack)
        {
            if (!stack.HasRoot) return stack.Point;
            if (stack.Root == null) return stack.Anchor;
            Vector3 at = stack.Root.position;
            if (stack.IsChef) return at + Vector3.up * 2.1f;
            float now = Time.unscaledTime;
            if (stack.Renderers == null || now - stack.RenderersAt > 0.5f)
            {
                stack.Renderers = stack.Root.GetComponentsInChildren<Renderer>();
                stack.RenderersAt = now;
                stack.LocalCentre = stack.Root.InverseTransformPoint(CentreOf(stack.Root, stack.Renderers));
            }
            // The words stand over the middle of the station.
            Vector3 centre = stack.Root.TransformPoint(stack.LocalCentre);
            at.x = centre.x;
            at.z = centre.z;
            float top = at.y + 0.8f;
            bool any = false;
            for (int i = 0; i < stack.Renderers.Length; i++)
            {
                Renderer r = stack.Renderers[i];
                if (r == null || !r.enabled || r.GetType().Name == "ParticleSystemRenderer") continue;
                top = any ? Mathf.Max(top, r.bounds.max.y) : r.bounds.max.y;
                any = true;
            }
            return new Vector3(at.x, top + 0.15f, at.z);
        }

        /// <summary>
        /// The middle of a station seen from above: its own solid body (the counter, the sink),
        /// else the middle of what is drawn of it, else its pivot.
        /// </summary>
        private static Vector3 CentreOf(Transform root, Renderer[] renderers)
        {
            bool any = false;
            Bounds bounds = new Bounds();
            Collider[] colliders = root.GetComponents<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (any) bounds.Encapsulate(c.bounds);
                else bounds = c.bounds;
                any = true;
            }
            if (!any && renderers != null)
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer r = renderers[i];
                    if (r == null || !r.enabled || r.GetType().Name == "ParticleSystemRenderer") continue;
                    if (any) bounds.Encapsulate(r.bounds);
                    else bounds = r.bounds;
                    any = true;
                }
            return any ? bounds.center : root.position;
        }

        // --- drawing --------------------------------------------------------------------------------

        /// <summary>Every frame (from SpecialFx.Update).</summary>
        internal static void Update(bool hide)
        {
            float now = Time.unscaledTime;
            for (int s = s_stacks.Count - 1; s >= 0; s--)
            {
                Stack stack = s_stacks[s];
                for (int i = stack.Lines.Count - 1; i >= 0; i--)
                    if (now >= stack.Lines[i].End)
                    {
                        Destroy(stack.Lines[i]);
                        stack.Lines.RemoveAt(i);
                    }
                if (stack.Lines.Count == 0) s_stacks.RemoveAt(s);
            }
            if (s_stacks.Count == 0 || !Layer()) return;
            Camera camera = SpecialFx.GameCamera();
            bool show = !hide && camera != null;
            if (s_layer.gameObject.activeSelf != show) s_layer.gameObject.SetActive(show);
            if (!show) return;
            Refresh(now);
            if (s_canvas.sortingOrder != s_order) s_canvas.sortingOrder = s_order;
            PlaceCovers();
            float scale = Mathf.Max(0.01f, s_canvas.scaleFactor);
            float step = Mathf.Clamp01(Time.unscaledDeltaTime * 14f);
            float give = Time.unscaledDeltaTime * 8f;
            for (int s = 0; s < s_stacks.Count; s++)
                Draw(s_stacks[s], now, camera, scale, step, give);
        }

        private static void Draw(Stack stack, float now, Camera camera, float scale, float step, float give)
        {
            stack.Anchor = AnchorOf(stack);
            Vector3 screen = camera.WorldToScreenPoint(stack.Anchor);
            bool visible = screen.z > 0f;
            for (int i = 0; i < stack.Lines.Count; i++)
                if (stack.Lines[i].Rect != null && stack.Lines[i].Rect.gameObject.activeSelf != visible)
                    stack.Lines[i].Rect.gameObject.SetActive(visible);
            if (!visible) return;
            // Pixels for a metre over there (the words keep their size in the kitchen as the camera moves).
            float perMetre = Mathf.Max(1f, ((Vector2)camera.WorldToScreenPoint(stack.Anchor + camera.transform.up) - (Vector2)screen).magnitude);
            float bottom = screen.y;
            if (stack.HasRoot && stack.Root != null) bottom = Mathf.Max(bottom, IconsTop(stack.Root) + Gap * perMetre);
            float bar;
            if (stack.IsChef && stack.Root != null && SpecialHud.OverheadTop(stack.Root.gameObject, out bar))
                bottom = Mathf.Max(bottom, bar + Gap * perMetre);
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(s_layer, new Vector2(screen.x, bottom), null, out local))
                return;

            float y = DrawHeld(stack, now, local, perMetre, scale, step, give);
            DrawFloating(stack, now, local.x, y, perMetre, scale, step, give);
        }

        /// <summary>The labels, one above the other (the oldest lowest); returns the top.</summary>
        private static float DrawHeld(Stack stack, float now, Vector2 local, float perMetre, float scale, float step, float give)
        {
            s_held.Clear();
            for (int i = 0; i < stack.Lines.Count; i++)
                if (stack.Lines[i].Held) s_held.Add(stack.Lines[i]);
            s_held.Sort(StartOrder.Instance);

            float y = local.y;
            for (int i = 0; i < s_held.Count; i++)
            {
                Line line = s_held[i];
                float height = Mathf.Clamp(line.Metres * perMetre, 16f, 72f) / scale;
                if (line.Rect == null) line.Label = MakeText(out line.Rect);
                if (line.Label.text != line.Text) line.Label.text = line.Text;
                // A new label rises into its place; the ones over a label that went slide down.
                if (!line.Placed)
                {
                    line.Y = y - height * 0.4f;
                    line.Placed = true;
                }
                line.Y = Mathf.Lerp(line.Y, y, step);
                float age = now - line.Start;
                float pop = age < 0.15f ? Mathf.Lerp(1.25f, 1f, age / 0.15f) : 1f;
                float alpha = Mathf.Clamp01((line.End - now) / HeldFade) * Mathf.Clamp01(age / 0.12f);
                Place(line, local.x, height, pop, alpha, give);
                y += height * 1.08f;
            }
            return y;
        }

        /// <summary>
        /// The floating words over the labels: each flies up over its life, the newest lowest, and
        /// one that would run into the word above pushes it up.
        /// </summary>
        private static void DrawFloating(Stack stack, float now, float x, float bottom, float perMetre, float scale, float step, float give)
        {
            s_floating.Clear();
            for (int i = 0; i < stack.Lines.Count; i++)
                if (!stack.Lines[i].Held) s_floating.Add(stack.Lines[i]);
            if (s_floating.Count == 0) return;
            s_floating.Sort(StartOrder.Instance);
            float above = bottom;
            for (int i = s_floating.Count - 1; i >= 0; i--)
            {
                Line line = s_floating[i];
                float life = Mathf.Max(0.01f, line.End - line.Start);
                float t = Mathf.Clamp01((now - line.Start) / life);
                float height = Mathf.Clamp(line.Metres * perMetre, 16f, 72f) / scale;
                float rise = FloatRise * perMetre / scale * (1f - (1f - t) * (1f - t));
                float target = Mathf.Max(bottom + rise, above);
                if (line.Rect == null) line.Label = MakeText(out line.Rect);
                if (line.Label.text != line.Text) line.Label.text = line.Text;
                if (!line.Placed)
                {
                    line.Y = target;
                    line.Placed = true;
                }
                // Follows its place closely (it is always moving up), easing when pushed.
                line.Y = Mathf.Max(target, Mathf.Lerp(line.Y, target, step));
                if (line.Y > target + height * 3f) line.Y = target;
                above = line.Y + height * 1.05f;

                float age = now - line.Start;
                float pop = age < 0.14f ? Mathf.Lerp(1.4f, 1f, age / 0.14f) : 1f;
                float alpha = Mathf.Min(Mathf.Clamp01(age / 0.08f), Mathf.Clamp01((line.End - now) / FloatFade));
                Place(line, x, height, pop, alpha, give);
            }
        }

        /// <summary>Puts a word at its place and size, fading it out where something covers the words.</summary>
        private static void Place(Line line, float x, float height, float pop, float alpha, float give)
        {
            line.Rect.anchoredPosition = new Vector2(x, line.Y);
            float size = height / FontSize * pop;
            line.Rect.localScale = new Vector3(size, size, 1f);
            if (s_coverRects.Count > 0)
            {
                float width = line.Label.preferredWidth * size;
                var area = new Rect(x - width * 0.5f, line.Y, width, height * pop * 1.1f);
                bool covered = false;
                for (int c = 0; c < s_coverRects.Count && !covered; c++)
                    covered = s_coverRects[c].Overlaps(area);
                line.Shown = Mathf.MoveTowards(line.Shown, covered ? 0f : 1f, give);
            }
            else line.Shown = Mathf.MoveTowards(line.Shown, 1f, give);
            Color colour = line.Colour;
            colour.a *= alpha * line.Shown;
            line.Label.color = colour;
        }

        private sealed class StartOrder : IComparer<Line>
        {
            internal static readonly StartOrder Instance = new StartOrder();

            public int Compare(Line a, Line b)
            {
                return a.Start.CompareTo(b.Start);
            }
        }

        /// <summary>The top (screen pixels) of the game's icons over this thing or anything on it; -1 if none.</summary>
        private static float IconsTop(Transform root)
        {
            if (s_iconCanvas == null) return -1f;
            Camera iconCamera = CameraOf(s_iconCanvas);
            float top = -1f;
            for (int i = 0; i < s_icons.Length; i++)
            {
                HoverIconUIController icon = s_icons[i];
                if (icon == null || !icon.isActiveAndEnabled) continue;
                Transform follows = icon.GetFollowTransform();
                if (follows == null || (follows != root && !follows.IsChildOf(root))) continue;
                Graphic[] graphics;
                if (!s_graphics.TryGetValue(icon, out graphics)) continue;
                for (int g = 0; g < graphics.Length; g++)
                {
                    Graphic graphic = graphics[g];
                    if (graphic == null || !graphic.enabled || !graphic.gameObject.activeInHierarchy || graphic.color.a < 0.02f) continue;
                    graphic.rectTransform.GetWorldCorners(s_corners);
                    for (int c = 0; c < 4; c++)
                    {
                        float y = iconCamera == null ? s_corners[c].y : iconCamera.WorldToScreenPoint(s_corners[c]).y;
                        if (y > top) top = y;
                    }
                }
            }
            return top;
        }

        private static Camera CameraOf(Canvas canvas)
        {
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            if (root == null || root.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            return root.worldCamera;
        }

        /// <summary>
        /// Every quarter second: the game's icons over the kitchen, its open-able emote wheels, and
        /// the words' place among the screen layers (over the game's HUD and icons when those are
        /// screen layers too, so the orders, timer and score never cover a word).
        /// </summary>
        private static void Refresh(float now)
        {
            if (now - s_iconsAt < 0.25f) return;
            s_iconsAt = now;
            s_graphics.Clear();
            try
            {
                if (s_iconCanvas == null)
                {
                    GameObject named = GameUtils.GetNamedCanvas("HoverIconCanvas");
                    s_iconCanvas = named != null ? named.GetComponent<Canvas>() : null;
                }
                if (s_hudCanvas == null)
                {
                    GameObject named = GameUtils.GetNamedCanvas("ScalingHUDCanvas");
                    s_hudCanvas = named != null ? named.GetComponent<Canvas>() : null;
                }
            }
            catch (System.Exception)
            {
            }
            s_icons = s_iconCanvas != null
                ? s_iconCanvas.GetComponentsInChildren<HoverIconUIController>(false)
                : new HoverIconUIController[0];
            for (int i = 0; i < s_icons.Length; i++)
                s_graphics[s_icons[i]] = s_icons[i].GetComponentsInChildren<Graphic>(false);

            int order = BaseOrder;
            order = Mathf.Max(order, OverlayOrder(s_iconCanvas) + 1);
            order = Mathf.Max(order, OverlayOrder(s_hudCanvas) + 1);
            s_order = Mathf.Min(order, 32000);

            // The game's emote wheels: one opens around a chef, and goes over the words there.
            // (Each chef's wheel is made once, so looking for new ones now and then is enough.)
            if (WheelSelector != null && SelectorObject != null && now - s_wheelsAt >= 2f)
            {
                s_wheelsAt = now;
                s_covers.Clear();
                ClientEmoteWheel[] wheels = Object.FindObjectsOfType<ClientEmoteWheel>();
                for (int i = 0; i < wheels.Length; i++)
                {
                    try
                    {
                        object selector = WheelSelector.GetValue(wheels[i]);
                        GameObject root = selector != null ? SelectorObject.GetValue(selector) as GameObject : null;
                        if (root != null) s_covers.Add(new Cover { Root = root });
                    }
                    catch (System.Exception)
                    {
                    }
                }
            }
        }

        private static int OverlayOrder(Canvas canvas)
        {
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            return root != null && root.renderMode == RenderMode.ScreenSpaceOverlay ? root.sortingOrder : int.MinValue + 1;
        }

        /// <summary>Where the open covers are this frame, in the words' layer.</summary>
        private static void PlaceCovers()
        {
            s_coverRects.Clear();
            for (int i = 0; i < s_covers.Count; i++)
            {
                Cover cover = s_covers[i];
                if (cover.Root == null || !cover.Root.activeInHierarchy) continue;
                if (cover.Graphics == null) cover.Graphics = cover.Root.GetComponentsInChildren<Graphic>(true);
                bool any = false;
                float minX = 0f, minY = 0f, maxX = 0f, maxY = 0f;
                for (int g = 0; g < cover.Graphics.Length; g++)
                {
                    Graphic graphic = cover.Graphics[g];
                    if (graphic == null || !graphic.enabled || !graphic.gameObject.activeInHierarchy || graphic.color.a < 0.02f) continue;
                    Camera camera = CameraOf(graphic.canvas);
                    graphic.rectTransform.GetWorldCorners(s_corners);
                    for (int c = 0; c < 4; c++)
                    {
                        Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, s_corners[c]);
                        Vector2 local;
                        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(s_layer, screen, null, out local)) continue;
                        if (!any)
                        {
                            minX = maxX = local.x;
                            minY = maxY = local.y;
                            any = true;
                        }
                        else
                        {
                            minX = Mathf.Min(minX, local.x);
                            maxX = Mathf.Max(maxX, local.x);
                            minY = Mathf.Min(minY, local.y);
                            maxY = Mathf.Max(maxY, local.y);
                        }
                    }
                }
                if (any) s_coverRects.Add(Rect.MinMaxRect(minX - 8f, minY - 8f, maxX + 8f, maxY + 8f));
            }
        }

        /// <summary>Our own screen layer for the words (see the class summary for what is over and under it).</summary>
        private static bool Layer()
        {
            if (s_layer != null && s_canvas != null) return true;
            if (s_canvasObject == null)
            {
                s_canvasObject = new GameObject("ChefSkills_Words", typeof(RectTransform));
                Object.DontDestroyOnLoad(s_canvasObject);
                int uiLayer = LayerMask.NameToLayer("UI");
                if (uiLayer >= 0) s_canvasObject.layer = uiLayer;
                Canvas canvas = s_canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = s_order;
                CanvasScaler scaler = s_canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
            }
            s_canvas = s_canvasObject.GetComponent<Canvas>();
            var go = new GameObject("Words", typeof(RectTransform));
            go.layer = s_canvasObject.layer;
            s_layer = (RectTransform)go.transform;
            s_layer.SetParent(s_canvasObject.transform, false);
            s_layer.anchorMin = Vector2.zero;
            s_layer.anchorMax = Vector2.one;
            s_layer.offsetMin = s_layer.offsetMax = Vector2.zero;
            return s_canvas != null;
        }

        private static Text MakeText(out RectTransform rect)
        {
            var go = new GameObject("Word", typeof(RectTransform));
            go.layer = s_layer.gameObject.layer;
            rect = (RectTransform)go.transform;
            rect.SetParent(s_layer, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(2400f, FontSize * 1.2f);
            Text text = go.AddComponent<Text>();
            text.raycastTarget = false;
            text.supportRichText = false;
            text.font = SpecialFx.Font;
            text.fontSize = FontSize;
            text.alignment = TextAnchor.LowerCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = Color.white;
            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.04f, 0.06f, 0.1f, 0.9f);
            outline.effectDistance = new Vector2(3f, -3f);
            return text;
        }

        private static void Destroy(Line line)
        {
            if (line.Rect != null) Object.Destroy(line.Rect.gameObject);
            line.Rect = null;
        }

        /// <summary>Removes every word (the level ended).</summary>
        internal static void Clear()
        {
            for (int s = 0; s < s_stacks.Count; s++)
                for (int i = 0; i < s_stacks[s].Lines.Count; i++) Destroy(s_stacks[s].Lines[i]);
            s_stacks.Clear();
            s_graphics.Clear();
            s_icons = new HoverIconUIController[0];
            s_iconCanvas = null;
            s_hudCanvas = null;
            s_covers.Clear();
            s_coverRects.Clear();
            s_wheelsAt = float.MinValue;
            if (s_layer != null) Object.Destroy(s_layer.gameObject);
            s_layer = null;
            s_canvas = null;
        }
    }
}
