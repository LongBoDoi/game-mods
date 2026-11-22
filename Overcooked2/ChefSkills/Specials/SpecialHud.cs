using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// The skill bar: for each chef played on this machine, a round button at the bottom of the
    /// screen for its active special (its icon, a cooldown sweep with the seconds left, a ring while
    /// its effect lasts, the key that uses it) and small round icons for its passives and quirk
    /// (with any recharge sweep or count a module sets, see ChefState.SetTimer/SetCount). Several
    /// local players get one bar each, side by side, marked with their chef colour.
    ///
    /// A refused press shakes the button and says why over it; a used one pops; and when the
    /// active is ready again the button flashes and a ring goes out under the chef.
    /// </summary>
    internal sealed class SpecialHud
    {
        private const float FlashSeconds = 1.6f;
        private const float ActiveSize = 112f;
        private const float SmallSize = 58f;
        private const float SmallGap = 10f;
        private const float BarGap = 56f;
        private const float BottomMargin = 26f;

        private static readonly Color ShadowColour = new Color(0.03f, 0.05f, 0.09f, 0.6f);
        private static readonly Color OutlineColour = new Color(0.04f, 0.06f, 0.1f, 0.9f);
        private static readonly Color SweepColour = new Color(0.02f, 0.03f, 0.06f, 0.62f);
        private static readonly Color DenyColour = new Color(1f, 0.38f, 0.34f, 1f);
        private static readonly Color KeyBack = new Color(0.04f, 0.06f, 0.1f, 0.78f);

        // When each chef was last refused (and why) or used its active.
        private static readonly Dictionary<ChefState, float> s_deniedAt = new Dictionary<ChefState, float>();
        private static readonly Dictionary<ChefState, string> s_deniedWhy = new Dictionary<ChefState, string>();
        private static readonly Dictionary<ChefState, float> s_usedAt = new Dictionary<ChefState, float>();

        private sealed class Icon
        {
            public RectTransform Root;
            public Image Back, Sweep, Ring, Glow, Picture;
            public Text Glyph, Seconds, Count;
            public Special Special = (Special)(-1);
        }

        private sealed class Bar
        {
            public RectTransform Root;
            public Icon Active;
            public readonly Icon[] Small = new Icon[3];
            public Text Key, Reason, Player, DetailsHint;
            public readonly List<Chip> Chips = new List<Chip>();
            public Image KeyBack, PlayerBack;
            public bool WasCooling;
            public float ReadyAt = -10f;
        }

        private readonly GameObject _root;
        private readonly Canvas _canvas;
        private readonly Dictionary<ChefState, Bar> _bars = new Dictionary<ChefState, Bar>();
        private readonly SpecialDetails _details;

        private static readonly System.Reflection.FieldInfo InRoundResults =
            HarmonyLib.AccessTools.Field(typeof(T17InGameFlow), "m_bInRoundResults");
        private static int s_menuFrame = -1;
        private static bool s_menuOpen;

        /// <summary>
        /// Is a game menu over the level (pause menu, its options, a dialog, the round results)?
        /// Everything this mod draws on screen hides then.
        /// </summary>
        internal static bool GameMenuOpen()
        {
            if (s_menuFrame == Time.frameCount) return s_menuOpen;
            s_menuFrame = Time.frameCount;
            bool open = false;
            try
            {
                if (T17DialogBoxManager.HasAnyOpenDialogs()) open = true;
                T17InGameFlow flow = T17InGameFlow.Instance;
                if (!open && flow != null)
                {
                    if (flow.m_Rootmenu != null && flow.m_Rootmenu.GetCurrentOpenMenu() != null) open = true;
                    else if (flow.IsPauseMenuOpen()) open = true;
                    else if (InRoundResults != null && (bool)InRoundResults.GetValue(flow)) open = true;
                }
            }
            catch (Exception)
            {
            }
            s_menuOpen = open;
            return open;
        }

        internal SpecialHud()
        {
            _root = new GameObject("ChefSkills_SkillBar", typeof(RectTransform));
            Object.DontDestroyOnLoad(_root);
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) _root.layer = uiLayer;
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 4000;
            _canvas = canvas;
            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _details = new SpecialDetails((RectTransform)_root.transform);
            s_deniedAt.Clear();
            s_deniedWhy.Clear();
            s_usedAt.Clear();
        }

        internal static void Deny(ChefState state)
        {
            Deny(state, null);
        }

        /// <summary>A press of this chef's active was refused (and why, if known): shake, say so, "no" sound.</summary>
        internal static void Deny(ChefState state, string reason)
        {
            if (state == null) return;
            s_deniedAt[state] = Time.unscaledTime;
            s_deniedWhy[state] = reason;
            try
            {
                GameUtils.TriggerAudio(GameOneShotAudioTag.UIBack, LayerMask.NameToLayer("UI"));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// A refusal as this player reads it. Reasons travel between machines in both languages
        /// (Lang.Both), so this player's wording is picked here; a plain English reason from the
        /// framework is still put in the language in use, and any other reason is shown as sent.
        /// </summary>
        internal static string ReasonText(string reason)
        {
            reason = Lang.Pick(reason);
            if (reason == null || !Lang.Vi) return reason;
            switch (reason)
            {
                case "NOT READY": return "CHƯA HỒI CHIÊU XONG";
                case "NOT SYNCED YET": return "CHƯA ĐỒNG BỘ XONG";
                case "NOT AVAILABLE": return "KHÔNG DÙNG ĐƯỢC";
                case "ERROR (SEE LOG)": return "LỖI (XEM LOG)";
                case "NOTHING TO USE IT ON": return "KHÔNG CÓ GÌ ĐỂ DÙNG";
                default: return reason;
            }
        }

        internal static void Used(ChefState state)
        {
            if (state != null) s_usedAt[state] = Time.unscaledTime;
        }

        // --- every frame ------------------------------------------------------------------------

        internal void Update(IList<ChefState> chefs)
        {
            if (_root == null) return;
            bool menu = GameMenuOpen();
            bool hidden = SkillSelectScreen.IsOpen || menu;
            // Hidden as a whole: a game menu or the skill screen is over the level.
            if (_canvas != null && _canvas.enabled == hidden) _canvas.enabled = !hidden;
            if (!hidden && SpecialInput.DetailsPressed()) _details.Toggle();
            var shown = new List<ChefState>();
            foreach (ChefState state in chefs)
            {
                if (!state.Local || state.Chef == null) continue;
                if (state.Loadout[0] + state.Loadout[1] + state.Loadout[2] + state.Loadout[3] == 0) continue;
                shown.Add(state);
            }
            shown.Sort(delegate(ChefState a, ChefState b)
            {
                return ((int)PlayerOf(a)).CompareTo((int)PlayerOf(b));
            });

            float width = 0f;
            for (int i = 0; i < shown.Count; i++) width += BarWidth(shown[i]) + (i > 0 ? BarGap : 0f);
            float x = -width * 0.5f;
            var seen = new HashSet<ChefState>();
            foreach (ChefState state in shown)
            {
                seen.Add(state);
                Bar bar = BarFor(state);
                if (bar.Root.gameObject.activeSelf == hidden) bar.Root.gameObject.SetActive(!hidden);
                float w = BarWidth(state);
                bar.Root.anchoredPosition = new Vector2(x, BottomMargin);
                x += w + BarGap;
                if (!hidden) Draw(state, bar, shown.Count > 1);
            }
            var gone = new List<ChefState>();
            foreach (KeyValuePair<ChefState, Bar> pair in _bars)
                if (!seen.Contains(pair.Key)) gone.Add(pair.Key);
            foreach (ChefState state in gone)
            {
                if (_bars[state].Root != null) Object.Destroy(_bars[state].Root.gameObject);
                _bars.Remove(state);
            }
            if (!hidden) _details.Update(shown);
        }

        private static PlayerInputLookup.Player PlayerOf(ChefState state)
        {
            return state.Provider != null ? state.Provider.GetID() : PlayerInputLookup.Player.One;
        }

        private static float BarWidth(ChefState state)
        {
            int small = 0;
            for (int i = 1; i < 4; i++)
                if (state.Loadout[i] != 0) small++;
            float width = state.Active != Special.None ? ActiveSize : 0f;
            if (small > 0) width += (width > 0f ? 16f : 0f) + small * SmallSize + (small - 1) * SmallGap;
            return Mathf.Max(width, ActiveSize);
        }

        private void Draw(ChefState state, Bar bar, bool several)
        {
            float now = SpecialRuntime.Now;
            float real = Time.unscaledTime;
            Special active = state.Active;

            // --- the active button --------------------------------------------------------------
            bool hasActive = active != Special.None;
            bar.Active.Root.gameObject.SetActive(hasActive);
            bar.Key.transform.parent.gameObject.SetActive(hasActive);
            float smallX = 0f;
            if (hasActive)
            {
                smallX = ActiveSize + 16f;
                Color colour = SpecialFx.ColourOf(active);
                SetSpecial(bar.Active, active);
                bool cooling = now < state.CooldownUntil;
                bool running = now < state.ActiveUntil;
                bool pending = now < state.PendingUntil;
                float left = state.CooldownUntil - now;

                if (bar.WasCooling && !cooling)
                {
                    // Ready again: flash the button and send a ring out under the chef.
                    bar.ReadyAt = real;
                    SpecialFx.Burst(state.Position, colour, 1.1f, 0.45f);
                }
                bar.WasCooling = cooling;

                bar.Active.Back.color = cooling && !running ? Color.Lerp(colour, Color.black, 0.55f) : colour;
                bar.Active.Sweep.enabled = cooling && state.CooldownTotal > 0f && state.CooldownTotal < 999f;
                if (bar.Active.Sweep.enabled) bar.Active.Sweep.fillAmount = Mathf.Clamp01(left / state.CooldownTotal);

                // The effect running: a bright ring that empties as it runs out.
                float duration = state.ActiveUntil - now;
                float total = Mathf.Max(0.01f, state.ActiveUntil - (state.CooldownUntil - state.CooldownTotal));
                bar.Active.Ring.fillAmount = running ? Mathf.Clamp01(duration / total) : 1f;
                Color ring = running ? Color.white : colour;
                float readyFlash = Mathf.Clamp01(1f - (real - bar.ReadyAt) / 0.6f);
                if (!cooling && !running) ring.a = 0.55f + 0.35f * Mathf.Sin(real * 4f) * 0.5f + 0.45f * readyFlash;
                else if (!running) ring.a = 0.25f;
                bar.Active.Ring.color = ring;
                Color glow = colour;
                glow.a = !cooling ? 0.35f + 0.5f * readyFlash : 0f;
                if (running) glow.a = 0.5f;
                bar.Active.Glow.color = glow;

                string seconds = null;
                if (running) seconds = Mathf.CeilToInt(duration).ToString();
                else if (cooling && state.CooldownTotal < 999f) seconds = left >= 10f ? Mathf.CeilToInt(left).ToString() : left.ToString("0.0");
                else if (cooling) seconds = Lang.T("USED", "HẾT");
                bar.Active.Seconds.text = seconds ?? "";
                bar.Active.Seconds.color = running ? Color.white : new Color(1f, 1f, 1f, 0.95f);
                SetGlyphColour(bar.Active, seconds != null ? new Color(1f, 1f, 1f, 0.28f) : Color.white);
                bar.Active.Count.text = CountText(state, active);

                bar.Key.text = SpecialInput.NameFor(PlayerOf(state));

                // Shake when refused; pop when used.
                float scale = 1f, shake = 0f;
                float denied;
                bool refused = s_deniedAt.TryGetValue(state, out denied) && real - denied < FlashSeconds;
                if (refused && real - denied < 0.35f)
                {
                    float t = (real - denied) / 0.35f;
                    shake = Mathf.Sin(t * Mathf.PI * 6f) * 9f * (1f - t);
                    bar.Active.Back.color = Color.Lerp(bar.Active.Back.color, DenyColour, 1f - t);
                }
                float used;
                if (s_usedAt.TryGetValue(state, out used))
                {
                    float age = real - used;
                    if (age >= 0f && age < 0.3f) scale = 1f + 0.22f * Mathf.Sin(Mathf.PI * age / 0.3f);
                }
                scale *= 1f + 0.12f * readyFlash * Mathf.Sin(Mathf.PI * Mathf.Clamp01((real - bar.ReadyAt) / 0.6f));
                bar.Active.Root.localScale = new Vector3(scale, scale, 1f);
                bar.Active.Root.anchoredPosition = new Vector2(ActiveSize * 0.5f + shake, ActiveSize * 0.5f + 30f);

                // Over the button, centred: why a press was refused, or what a press does now.
                string why;
                bool showWhy = refused && s_deniedWhy.TryGetValue(state, out why) && !string.IsNullOrEmpty(why);
                bool recallHint = !showWhy && running && active == Special.Recall;
                bar.Reason.gameObject.SetActive(showWhy || recallHint);
                if (showWhy)
                {
                    bar.Reason.text = ReasonText(s_deniedWhy[state]);
                    Color c = DenyColour;
                    c.a = Mathf.Clamp01((FlashSeconds - (real - denied)) / 0.4f);
                    bar.Reason.color = c;
                }
                else if (recallHint)
                {
                    bar.Reason.text = Lang.T("PRESS AGAIN TO RETURN", "BẤM LẦN NỮA ĐỂ QUAY VỀ");
                    bar.Reason.color = Color.Lerp(colour, Color.white, 0.6f);
                }
                if (pending) SetGlyphColour(bar.Active, new Color(1f, 1f, 1f, 0.5f + 0.5f * Mathf.Sin(real * 12f)));
            }
            else bar.Reason.gameObject.SetActive(false);

            // Which key opens the details panel, next to the small icons.
            string detailsKey = SpecialInput.DetailsKeyName();
            bar.DetailsHint.gameObject.SetActive(detailsKey != null);
            if (detailsKey != null)
            {
                bar.DetailsHint.text = detailsKey + ": " + (_details.Open ? Lang.T("HIDE INFO", "ẨN THÔNG TIN") : Lang.T("SKILL INFO", "XEM KỸ NĂNG"));
                bar.DetailsHint.rectTransform.anchoredPosition = new Vector2(smallX, 14f);
            }

            // --- passives and quirk ------------------------------------------------------------
            int slot = 0;
            for (int i = 1; i < 4; i++)
            {
                Icon icon = bar.Small[i - 1];
                Special special = (Special)state.Loadout[i];
                if (special == Special.None)
                {
                    icon.Root.gameObject.SetActive(false);
                    continue;
                }
                icon.Root.gameObject.SetActive(true);
                SetSpecial(icon, special);
                icon.Root.anchoredPosition = new Vector2(smallX + slot * (SmallSize + SmallGap) + SmallSize * 0.5f, SmallSize * 0.5f + 30f);
                slot++;
                bool quirk = i == 3;
                Color colour = SpecialFx.ColourOf(special);
                Vector2 timer;
                bool timing = state.Timers.TryGetValue(special, out timer) && timer.x > now;
                icon.Back.color = timing ? Color.Lerp(colour, Color.black, 0.5f) : colour;
                icon.Sweep.enabled = timing;
                if (timing) icon.Sweep.fillAmount = Mathf.Clamp01((timer.x - now) / timer.y);
                icon.Seconds.text = timing && timer.x - now >= 1f ? Mathf.CeilToInt(timer.x - now).ToString() : "";
                SetGlyphColour(icon, timing ? new Color(1f, 1f, 1f, 0.35f) : Color.white);
                icon.Ring.color = quirk ? new Color(1f, 0.45f, 0.42f, 0.9f) : new Color(1f, 1f, 1f, 0.55f);
                icon.Count.text = CountText(state, special);
            }

            // --- texts --------------------------------------------------------------------------
            DrawChips(state, bar, BarWidth(state));
            bar.Player.transform.parent.gameObject.SetActive(several);
            if (several)
            {
                PlayerInputLookup.Player player = PlayerOf(state);
                bar.Player.text = "P" + ((int)player + 1);
                int index = 0;
                try
                {
                    index = GameArt.ColourIndex(GameArt.ColourOf(player));
                }
                catch (Exception)
                {
                }
                bar.PlayerBack.color = GameArt.ArrowTints[Mathf.Clamp(index, 0, GameArt.ArrowTints.Length - 1)];
            }
        }

        // --- status chips -----------------------------------------------------------------------

        private const float ChipHeight = 36f;
        private const float ChipGap = 10f;
        private const float ChipRowGap = 8f;
        private const float ChipMaxRow = 760f;
        // Over the bar, above the refusal text over the active button.
        private const float ChipBaseY = ActiveSize + 30f + 50f;

        private static readonly Color ChipDark = new Color(0.05f, 0.07f, 0.12f, 1f);
        private static readonly Color ChipNeutral = new Color(0.6f, 0.68f, 0.8f, 1f);

        /// <summary>One status line a module set (STREAK +20%, POCKET: TOMATO...), as a coloured chip.</summary>
        internal sealed class Chip
        {
            public RectTransform Root;
            public Image Halo, Back, Frame, Dot, Picture;
            public Text Label, Glyph;
            public string Key, Text;
            public Special Special = (Special)(-1);
            public float ChangedAt;
        }

        /// <summary>
        /// The chef's status lines as chips in a row centred over its bar (more rows upwards when
        /// they don't fit). A chip pops and flashes when it appears or its text changes.
        /// </summary>
        private void DrawChips(ChefState state, Bar bar, float barWidth)
        {
            var lines = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> line in state.Statuses) lines[line.Key] = line.Value;
            for (int i = bar.Chips.Count - 1; i >= 0; i--)
            {
                if (bar.Chips[i].Root != null && lines.ContainsKey(bar.Chips[i].Key)) continue;
                if (bar.Chips[i].Root != null) Object.Destroy(bar.Chips[i].Root.gameObject);
                bar.Chips.RemoveAt(i);
            }
            float real = Time.unscaledTime;
            foreach (KeyValuePair<string, string> line in state.Statuses)
            {
                Chip chip = bar.Chips.Find(delegate(Chip c) { return c.Key == line.Key; });
                if (chip == null)
                {
                    chip = MakeChip(bar.Root, line.Key);
                    bar.Chips.Add(chip);
                }
                string text = Lang.Pick(line.Value).Replace("\n", "  ").ToUpperInvariant();
                Special special = state.StatusSpecial(line.Key);
                if (chip.Text != text || chip.Special != special)
                {
                    chip.Text = text;
                    chip.Label.text = text;
                    if (chip.Special != special)
                    {
                        chip.Special = special;
                        SpecialFx.ShowIcon(chip.Picture, chip.Glyph, special);
                    }
                    chip.ChangedAt = real;
                }
            }
            if (bar.Chips.Count == 0) return;
            // Same order as the lines (module key order), so chips don't swap places.
            bar.Chips.Sort(delegate(Chip a, Chip b) { return string.CompareOrdinal(a.Key, b.Key); });

            // Rows of chips, bottom row first.
            var widths = new float[bar.Chips.Count];
            for (int i = 0; i < bar.Chips.Count; i++)
            {
                Chip chip = bar.Chips[i];
                bool dot = chip.Special != Special.None;
                chip.Dot.gameObject.SetActive(dot);
                float left = dot ? ChipHeight - 2f : 14f;
                chip.Label.rectTransform.anchoredPosition = new Vector2(left, 0f);
                widths[i] = left + chip.Label.preferredWidth + 16f;
            }
            int start = 0;
            float y = ChipBaseY;
            while (start < bar.Chips.Count)
            {
                int end = start;
                float row = widths[start];
                while (end + 1 < bar.Chips.Count && row + ChipGap + widths[end + 1] <= ChipMaxRow)
                {
                    end++;
                    row += ChipGap + widths[end];
                }
                float x = barWidth * 0.5f - row * 0.5f;
                for (int i = start; i <= end; i++)
                {
                    DrawChip(bar.Chips[i], new Vector2(x, y), widths[i], real);
                    x += widths[i] + ChipGap;
                }
                y += ChipHeight + ChipRowGap;
                start = end + 1;
            }
        }

        private static void DrawChip(Chip chip, Vector2 at, float width, float real)
        {
            Color colour = chip.Special != Special.None ? SpecialFx.ColourOf(chip.Special) : ChipNeutral;
            float age = real - chip.ChangedAt;
            // Pops in (and on every change), with a bright flash that settles into a soft glow.
            float pop = age < 0.3f ? 1f + 0.3f * (1f - age / 0.3f) * (1f - age / 0.3f) : 1f;
            float flash = Mathf.Clamp01(1f - age / 0.9f);
            float breathe = 0.5f + 0.5f * Mathf.Sin(real * 3f);

            chip.Root.anchoredPosition = at + new Vector2(width * 0.5f, ChipHeight * 0.5f);
            chip.Root.sizeDelta = new Vector2(width, ChipHeight);
            chip.Root.localScale = new Vector3(pop, pop, 1f);

            Color back = Color.Lerp(ChipDark, colour, 0.32f + 0.3f * flash);
            back.a = 0.94f;
            chip.Back.color = back;
            chip.Frame.color = Color.Lerp(colour, Color.white, 0.2f + 0.6f * flash);
            Color halo = colour;
            halo.a = 0.25f + 0.15f * breathe + 0.5f * flash;
            chip.Halo.color = halo;
            chip.Dot.color = colour;
        }

        private Chip MakeChip(RectTransform parent, string key)
        {
            var chip = new Chip { Key = key };
            chip.Root = Rect("Chip " + key, parent, new Vector2(0f, 0f), Vector2.zero, new Vector2(100f, ChipHeight));
            chip.Halo = Sliced(chip.Root, "Halo", SpecialFx.RoundHalo, 26f);
            chip.Back = Sliced(chip.Root, "Back", SpecialFx.RoundBox, 0f);
            chip.Frame = Sliced(chip.Root, "Frame", SpecialFx.RoundFrame, 0f);

            // The special's icon: a disc with its letters, at the left end.
            float dot = ChipHeight - 10f;
            RectTransform dotRect = Rect("Dot", chip.Root, new Vector2(0f, 0.5f), new Vector2(5f + dot * 0.5f, 0f), new Vector2(dot, dot));
            chip.Dot = dotRect.gameObject.AddComponent<Image>();
            chip.Dot.raycastTarget = false;
            chip.Dot.sprite = SpecialFx.Disc;
            chip.Picture = Img(dotRect, "Picture", null, dot * 0.92f);
            chip.Picture.preserveAspect = true;
            chip.Picture.enabled = false;
            chip.Glyph = MakeText(dotRect, "Glyph", 12, TextAnchor.MiddleCenter);
            chip.Glyph.rectTransform.anchoredPosition = Vector2.zero;
            chip.Glyph.rectTransform.sizeDelta = new Vector2(dot, dot);

            chip.Label = MakeText(chip.Root, "Label", 20, TextAnchor.MiddleLeft);
            chip.Label.rectTransform.anchorMin = chip.Label.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            chip.Label.rectTransform.pivot = new Vector2(0f, 0.5f);
            chip.Label.rectTransform.sizeDelta = new Vector2(600f, ChipHeight);
            return chip;
        }

        /// <summary>A 9-sliced image filling its parent, grown by <paramref name="grow"/> on every side.</summary>
        private Image Sliced(RectTransform parent, string name, Sprite sprite, float grow)
        {
            RectTransform rect = Rect(name, parent, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(-grow, -grow);
            rect.offsetMax = new Vector2(grow, grow);
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            return image;
        }

        private static void SetGlyphColour(Icon icon, Color colour)
        {
            icon.Glyph.color = colour;
            // A picture under the seconds stays a little more visible than letters would.
            if (icon.Picture != null) icon.Picture.color = new Color(colour.r, colour.g, colour.b, Mathf.Max(colour.a, 0.42f));
        }

        private static string CountText(ChefState state, Special special)
        {
            int count;
            return state.Counts.TryGetValue(special, out count) && count != 0 ? count.ToString() : "";
        }

        private static void SetSpecial(Icon icon, Special special)
        {
            if (icon.Special == special) return;
            icon.Special = special;
            SpecialFx.ShowIcon(icon.Picture, icon.Glyph, special);
        }

        // --- building ---------------------------------------------------------------------------

        private Bar BarFor(ChefState state)
        {
            Bar bar;
            if (_bars.TryGetValue(state, out bar) && bar.Root != null) return bar;
            bar = new Bar();
            bar.Root = Rect("Bar", _root.transform, new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(10f, 10f));
            bar.Root.pivot = new Vector2(0f, 0f);

            bar.Active = MakeIcon(bar.Root, "Active", ActiveSize, 46, 40);

            RectTransform keyBack = Rect("KeyBack", bar.Root, new Vector2(0f, 0f), new Vector2(ActiveSize * 0.5f, 14f), new Vector2(ActiveSize, 26f));
            bar.KeyBack = keyBack.gameObject.AddComponent<Image>();
            bar.KeyBack.raycastTarget = false;
            bar.KeyBack.color = KeyBack;
            bar.Key = MakeText(keyBack, "Key", 18, TextAnchor.MiddleCenter);
            bar.Key.rectTransform.anchoredPosition = Vector2.zero;
            bar.Key.color = new Color(0.95f, 0.97f, 1f, 1f);

            for (int i = 0; i < 3; i++) bar.Small[i] = MakeIcon(bar.Root, "Small" + i, SmallSize, 24, 22);


            // Centred over the active button (its top is at ActiveSize + 30).
            bar.Reason = MakeText(bar.Root, "Reason", 24, TextAnchor.LowerCenter);
            bar.Reason.rectTransform.pivot = new Vector2(0.5f, 0f);
            bar.Reason.rectTransform.anchoredPosition = new Vector2(ActiveSize * 0.5f, ActiveSize + 42f);
            bar.Reason.gameObject.SetActive(false);

            bar.DetailsHint = MakeText(bar.Root, "DetailsHint", 16, TextAnchor.MiddleLeft);
            bar.DetailsHint.rectTransform.anchorMin = bar.DetailsHint.rectTransform.anchorMax = Vector2.zero;
            bar.DetailsHint.rectTransform.pivot = new Vector2(0f, 0.5f);
            bar.DetailsHint.color = new Color(0.86f, 0.9f, 0.96f, 0.8f);

            RectTransform chip = Rect("PlayerChip", bar.Root, new Vector2(0f, 0f), new Vector2(-24f, ActiveSize * 0.5f + 30f), new Vector2(40f, 28f));
            bar.PlayerBack = chip.gameObject.AddComponent<Image>();
            bar.PlayerBack.raycastTarget = false;
            bar.PlayerBack.sprite = SpecialFx.Disc;
            bar.Player = MakeText(chip, "Player", 18, TextAnchor.MiddleCenter);
            bar.Player.rectTransform.anchoredPosition = Vector2.zero;
            chip.gameObject.SetActive(false);

            _bars[state] = bar;
            return bar;
        }

        private Icon MakeIcon(RectTransform parent, string name, float size, int glyphSize, int secondsSize)
        {
            var icon = new Icon();
            icon.Root = Rect(name, parent, new Vector2(0f, 0f), Vector2.zero, new Vector2(size, size));
            Image shadow = Img(icon.Root, "Shadow", SpecialFx.Disc, size + 10f);
            shadow.color = ShadowColour;
            shadow.rectTransform.anchoredPosition = new Vector2(0f, -3f);
            icon.Glow = Img(icon.Root, "Glow", SpecialFx.Glow, size * 1.7f);
            icon.Glow.color = Color.clear;
            icon.Back = Img(icon.Root, "Back", SpecialFx.Disc, size);
            Image shine = Img(icon.Root, "Shine", SpecialFx.Glow, size * 0.9f);
            shine.color = new Color(1f, 1f, 1f, 0.22f);
            shine.rectTransform.anchoredPosition = new Vector2(-size * 0.12f, size * 0.14f);
            icon.Picture = Img(icon.Root, "Picture", null, size * 0.86f);
            icon.Picture.preserveAspect = true;
            icon.Picture.enabled = false;
            icon.Glyph = MakeText(icon.Root, "Glyph", glyphSize, TextAnchor.MiddleCenter);
            icon.Glyph.rectTransform.anchoredPosition = Vector2.zero;
            icon.Sweep = Img(icon.Root, "Sweep", SpecialFx.Disc, size);
            icon.Sweep.type = Image.Type.Filled;
            icon.Sweep.fillMethod = Image.FillMethod.Radial360;
            icon.Sweep.fillOrigin = (int)Image.Origin360.Top;
            icon.Sweep.fillClockwise = false;
            icon.Sweep.color = SweepColour;
            icon.Sweep.enabled = false;
            icon.Ring = Img(icon.Root, "Ring", SpecialFx.Ring, size + 6f);
            icon.Ring.type = Image.Type.Filled;
            icon.Ring.fillMethod = Image.FillMethod.Radial360;
            icon.Ring.fillOrigin = (int)Image.Origin360.Top;
            icon.Ring.fillClockwise = false;
            icon.Seconds = MakeText(icon.Root, "Seconds", secondsSize, TextAnchor.MiddleCenter);
            icon.Seconds.rectTransform.anchoredPosition = Vector2.zero;
            icon.Count = MakeText(icon.Root, "Count", Mathf.RoundToInt(secondsSize * 0.75f), TextAnchor.MiddleCenter);
            icon.Count.rectTransform.anchoredPosition = new Vector2(size * 0.36f, size * 0.36f);
            icon.Count.color = new Color(1f, 0.95f, 0.6f, 1f);
            return icon;
        }

        private RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = _root.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        private Image Img(RectTransform parent, string name, Sprite sprite, float size)
        {
            RectTransform rect = Rect(name, parent, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = sprite;
            return image;
        }

        private Text MakeText(RectTransform parent, string name, int size, TextAnchor alignment)
        {
            Vector2 anchor = parent == null ? Vector2.zero : new Vector2(0.5f, 0.5f);
            if (alignment == TextAnchor.LowerLeft || alignment == TextAnchor.LowerCenter) anchor = new Vector2(0f, 0f);
            RectTransform rect = Rect(name, parent, anchor, Vector2.zero, new Vector2(400f, 40f));
            Text text = rect.gameObject.AddComponent<Text>();
            text.raycastTarget = false;
            text.supportRichText = false;
            text.font = SpecialFx.Font;
            text.fontSize = size;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = Color.white;
            Outline outline = rect.gameObject.AddComponent<Outline>();
            outline.effectColor = OutlineColour;
            outline.effectDistance = new Vector2(2f, -2f);
            return text;
        }

        internal void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            _bars.Clear();
        }
    }
}
