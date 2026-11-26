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
    /// (with any recharge sweep or count a module sets, see ChefState.SetTimer/SetCount).
    ///
    /// Each local player picks where theirs goes (<see cref="SkillBarSettings"/>): on screen
    /// (several players' bars spread over the bottom in lanes, each tagged P1..P4 and underlined in
    /// that player's colour), over their chef's head (small, following the chef), or hidden.
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
        // A chef's other active (not the one the SKILL KEY uses now), beside the big button.
        private const float SecondSize = 76f;
        private const float SecondGap = 12f;
        private const float BarGap = 56f;
        private const float BottomMargin = 26f;

        private static readonly Color ShadowColour = new Color(0.03f, 0.05f, 0.09f, 0.6f);
        private static readonly Color OutlineColour = new Color(0.04f, 0.06f, 0.1f, 0.9f);
        private static readonly Color SweepColour = new Color(0.02f, 0.03f, 0.06f, 0.62f);
        private static readonly Color DenyColour = new Color(1f, 0.38f, 0.34f, 1f);
        private static readonly Color KeyBack = new Color(0.04f, 0.06f, 0.1f, 0.78f);

        // When each chef was last refused (and why) or used its active.
        private static readonly Dictionary<ChefState, float> s_deniedAt = new Dictionary<ChefState, float>();
        private static readonly Dictionary<ChefState, float> s_usedAt = new Dictionary<ChefState, float>();
        private static readonly Dictionary<ChefState, Special> s_usedSpecial = new Dictionary<ChefState, Special>();
        private static readonly Dictionary<ChefState, float> s_switchedAt = new Dictionary<ChefState, float>();

        /// <summary>The loadout slots shown as small icons, in card order: the passives, then the quirk.</summary>
        private static readonly int[] SmallSlots = { 1, 2, 4, 3 };

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
            // The active the SKILL KEY uses (big) and the chef's other one (small, dimmed).
            public Icon Active, Second;
            public CanvasGroup SecondGroup;
            public Text SwitchKey;
            public Image SwitchBack;
            // The passives, then the quirk (see SmallSlots).
            public readonly Icon[] Small = new Icon[SmallSlots.Length];
            public Text Key, Reason, Player, DetailsHint;
            public readonly List<Chip> Chips = new List<Chip>();
            public Image KeyBack, PlayerBack, Strip;
            // Per active slot: was it cooling last frame, and when it became ready.
            public readonly bool[] WasCooling = new bool[2];
            public readonly float[] ReadyAt = { -10f, -10f };
            // Over a chef: how wide what is shown is (the bar is centred on it).
            public float CompactWidth = 112f;
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

        private static int s_kitchenFrame = -1;
        private static bool s_kitchenHidden;
        private static Canvas s_hoverIcons;
        private static ScreenTransitionManager s_transitions;

        /// <summary>
        /// Has the game put something over the kitchen that hides its own icons: a tutorial popup,
        /// a cutscene, a level transition (all switch the kitchen's icon layer off), or the screen
        /// wipe between scenes? The mod's words and skill bar stay under those, so they hide too.
        /// </summary>
        internal static bool KitchenUiHidden()
        {
            if (s_kitchenFrame == Time.frameCount) return s_kitchenHidden;
            s_kitchenFrame = Time.frameCount;
            bool hidden = false;
            try
            {
                if (s_hoverIcons == null)
                {
                    GameObject named = GameUtils.GetNamedCanvas("HoverIconCanvas");
                    s_hoverIcons = named != null ? named.GetComponent<Canvas>() : null;
                }
                if (s_hoverIcons != null && !s_hoverIcons.isActiveAndEnabled) hidden = true;
                if (!hidden)
                {
                    if (s_transitions == null) s_transitions = GameUtils.RequestManager<ScreenTransitionManager>();
                    if (s_transitions != null && !s_transitions.IsIdle) hidden = true;
                }
            }
            catch (Exception)
            {
            }
            s_kitchenHidden = hidden;
            return hidden;
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
            s_usedAt.Clear();
            s_usedSpecial.Clear();
            s_switchedAt.Clear();
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
            // Why: a floating word over the chef, like every other word (the bar only shakes).
            if (!string.IsNullOrEmpty(reason)) SpecialFx.Text(state.Chef, ReasonText(reason), DenyColour);
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

        /// <summary>This chef's <paramref name="special"/> went off: its button pops.</summary>
        internal static void Used(ChefState state, Special special)
        {
            if (state == null) return;
            s_usedAt[state] = Time.unscaledTime;
            s_usedSpecial[state] = special;
        }

        /// <summary>The player switched which active the SKILL KEY uses: the big button pops, with a click.</summary>
        internal static void Switched(ChefState state)
        {
            if (state == null) return;
            s_switchedAt[state] = Time.unscaledTime;
            try
            {
                GameUtils.TriggerAudio(GameOneShotAudioTag.UIHighlight, LayerMask.NameToLayer("UI"));
            }
            catch (Exception)
            {
            }
        }

        // --- every frame ------------------------------------------------------------------------

        // On screen, several players' bars are spread over the middle of the bottom edge (the score
        // and the timer keep the corners), each in its own lane.
        private const float LanesHalfWidth = 660f;
        private const float PackedGap = 90f;
        // Over a chef: how high above its feet the bar sits, and how big it is drawn.
        private const float HeadHeight = 2.2f;
        private const float OverheadScale = 0.42f;

        internal void Update(IList<ChefState> chefs)
        {
            if (_root == null) return;
            bool menu = GameMenuOpen();
            bool hidden = SkillSelectScreen.IsOpen || menu || KitchenUiHidden();
            // Hidden as a whole: a game menu, the skill screen, a tutorial popup, a cutscene or a
            // screen wipe is over the level.
            if (_canvas != null && _canvas.enabled == hidden) _canvas.enabled = !hidden;

            // The players on this machine in player order: each one's rank picks their own DISPLAY
            // setting, and their own info key opens their own details card.
            var players = new List<PlayerInputLookup.Player>();
            foreach (ChefState state in chefs)
                if (state.Local && state.Chef != null && !players.Contains(PlayerOf(state))) players.Add(PlayerOf(state));
            players.Sort();
            foreach (PlayerInputLookup.Player player in players)
                if (SpecialInput.DetailsPressed(player) && !hidden) _details.Toggle(player);

            var shown = new List<ChefState>();
            foreach (ChefState state in chefs)
            {
                if (!state.Local || state.Chef == null) continue;
                if (!Array.Exists(state.Loadout, pick => pick != 0)) continue;
                shown.Add(state);
            }
            shown.Sort(delegate(ChefState a, ChefState b)
            {
                return ((int)PlayerOf(a)).CompareTo((int)PlayerOf(b));
            });

            // Bars on screen at their player's own spot, and the rest sharing the bottom in lanes.
            var auto = new List<ChefState>();
            var modes = new Dictionary<ChefState, SkillBarMode>();
            var scales = new Dictionary<ChefState, float>();
            var spots = new Dictionary<ChefState, Vector2>();
            foreach (ChefState state in shown)
            {
                int rank = players.IndexOf(PlayerOf(state));
                SkillBarMode mode = SkillBarSettings.Get(rank);
                modes[state] = mode;
                scales[state] = SkillBarSettings.Scale(rank);
                Vector2 spot;
                if (mode != SkillBarMode.OnScreen) continue;
                if (SkillBarSettings.Position(rank, out spot)) spots[state] = spot;
                else auto.Add(state);
            }
            float lane;
            Dictionary<ChefState, float> lefts = LayOut(auto, scales, out lane);
            bool several = players.Count > 1;
            Camera camera = SpecialFx.GameCamera();
            Rect canvas = ((RectTransform)_root.transform).rect;

            var seen = new HashSet<ChefState>();
            foreach (ChefState state in shown)
            {
                seen.Add(state);
                Bar bar = BarFor(state);
                TrackReady(state, bar);
                SkillBarMode mode = modes[state];
                float scale = scales[state];
                bool visible = !hidden && mode != SkillBarMode.Hidden;
                if (visible && mode == SkillBarMode.AboveHead) visible = PlaceOverhead(state, bar, camera, scale);
                else if (visible)
                {
                    bar.Root.localScale = new Vector3(scale, scale, 1f);
                    Vector2 spot;
                    if (spots.TryGetValue(state, out spot))
                    {
                        bar.Root.anchorMin = bar.Root.anchorMax = Vector2.zero;
                        bar.Root.anchoredPosition = SpotToCorner(spot, BarWidth(state) * scale, BarHeight * scale, canvas);
                    }
                    else
                    {
                        bar.Root.anchorMin = bar.Root.anchorMax = new Vector2(0.5f, 0f);
                        bar.Root.anchoredPosition = new Vector2(lefts[state], BottomMargin);
                    }
                }
                if (bar.Root.gameObject.activeSelf != visible) bar.Root.gameObject.SetActive(visible);
                // Chips wrap within the bar's lane when bars share the bottom (in the bar's own units).
                float chipRow = auto.Count > 1 && auto.Contains(state) ? (lane - 20f) / scale : ChipMaxRow;
                if (visible) Draw(state, bar, several, mode, chipRow);
            }
            var gone = new List<ChefState>();
            foreach (KeyValuePair<ChefState, Bar> pair in _bars)
                if (!seen.Contains(pair.Key)) gone.Add(pair.Key);
            foreach (ChefState state in gone)
            {
                if (_bars[state].Root != null) Object.Destroy(_bars[state].Root.gameObject);
                _bars.Remove(state);
            }
            if (!hidden) _details.Update(shown, players);
        }

        /// <summary>
        /// Where each on-screen bar's left edge goes. One bar sits in the middle; several get a lane
        /// each across the middle of the screen, in player order, so they stand well apart (packed
        /// with a wide gap when they don't fit their lanes). <paramref name="lane"/>: each lane's width.
        /// </summary>
        private static Dictionary<ChefState, float> LayOut(List<ChefState> bars, Dictionary<ChefState, float> scales, out float lane)
        {
            var lefts = new Dictionary<ChefState, float>();
            int n = bars.Count;
            lane = n > 0 ? 2f * LanesHalfWidth / n : 0f;
            if (n == 0) return lefts;
            if (n == 1)
            {
                lefts[bars[0]] = -BarWidth(bars[0]) * scales[bars[0]] * 0.5f;
                return lefts;
            }
            bool fits = true;
            float total = 0f;
            foreach (ChefState state in bars)
            {
                float w = BarWidth(state) * scales[state];
                total += w;
                if (w + PackedGap > lane) fits = false;
            }
            if (fits)
            {
                for (int i = 0; i < n; i++)
                    lefts[bars[i]] = -LanesHalfWidth + lane * (i + 0.5f) - BarWidth(bars[i]) * scales[bars[i]] * 0.5f;
                return lefts;
            }
            total += (n - 1) * PackedGap;
            float x = -total * 0.5f;
            foreach (ChefState state in bars)
            {
                lefts[state] = x;
                x += BarWidth(state) * scales[state] + PackedGap;
            }
            lane = total / n;
            return lefts;
        }

        /// <summary>
        /// About how tall a bar is from its bottom (the key label) to the top of the active button,
        /// in its own units: used to keep a bar placed by its player on the screen.
        /// </summary>
        internal const float BarHeight = ActiveSize + 34f;

        /// <summary>
        /// The bottom-left corner for a bar whose bottom centre is at <paramref name="spot"/>
        /// (screen fractions), kept wholly on the canvas.
        /// </summary>
        internal static Vector2 SpotToCorner(Vector2 spot, float width, float height, Rect canvas)
        {
            float x = spot.x * canvas.width - width * 0.5f;
            float y = spot.y * canvas.height;
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, canvas.width - width));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, canvas.height - height));
            return new Vector2(x, y);
        }

        /// <summary>The width of this chef's bar as drawn on screen (its own units), for previews.</summary>
        internal static float FullWidth(int[] loadout)
        {
            int small = 0;
            foreach (int i in SmallSlots)
                if (loadout != null && i < loadout.Length && loadout[i] != 0) small++;
            return ActivesWidth(SpecialCatalog.ActiveIn(loadout, 0) != Special.None || SpecialCatalog.ActiveIn(loadout, 1) != Special.None,
                SpecialCatalog.TwoActives(loadout), small);
        }

        /// <summary>The bar's width: the active button(s), then the small passive and quirk icons.</summary>
        private static float ActivesWidth(bool any, bool two, int small)
        {
            float width = any ? ActiveSize : 0f;
            if (two) width += SecondGap + SecondSize;
            if (small > 0) width += (width > 0f ? 16f : 0f) + small * SmallSize + (small - 1) * SmallGap;
            return Mathf.Max(width, ActiveSize);
        }

        /// <summary>
        /// Puts the bar over its chef's head, drawn small. False when the chef isn't in front of the
        /// camera (nothing to show it over).
        /// </summary>
        private bool PlaceOverhead(ChefState state, Bar bar, Camera camera, float size)
        {
            if (camera == null) return false;
            Vector3 screen = camera.WorldToScreenPoint(state.Position + Vector3.up * HeadHeight);
            if (screen.z <= 0f) return false;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)_root.transform, screen, null, out local))
                return false;
            float scale = OverheadScale * size;
            bar.Root.anchorMin = bar.Root.anchorMax = new Vector2(0.5f, 0.5f);
            bar.Root.localScale = new Vector3(scale, scale, 1f);
            // Centred over the head (Draw packs what it shows from the left, see Compact); the key
            // label under the button isn't drawn here, so drop by its height.
            bar.Root.anchoredPosition = local + new Vector2(-bar.CompactWidth * scale * 0.5f, -30f * scale);
            // Its top on the screen, so the words over this chef go above it (FxLabels).
            float factor = _canvas != null ? _canvas.scaleFactor : 1f;
            s_overheadTop[state.Chef.GetInstanceID()] = new Vector2(Time.frameCount, screen.y + (ActiveSize - 30f) * scale * factor);
            return true;
        }

        // Chef instance ID -> (frame drawn, top of its over-head bar in screen pixels).
        private static readonly Dictionary<int, Vector2> s_overheadTop = new Dictionary<int, Vector2>();

        /// <summary>The top (screen pixels) of the bar drawn over this chef's head this frame or the last.</summary>
        internal static bool OverheadTop(GameObject chef, out float top)
        {
            Vector2 entry;
            top = 0f;
            if (chef == null || !s_overheadTop.TryGetValue(chef.GetInstanceID(), out entry) || Time.frameCount - (int)entry.x > 2)
                return false;
            top = entry.y;
            return true;
        }

        /// <summary>When an active is ready again: a ring goes out under the chef (and its button flashes, if shown).</summary>
        private static void TrackReady(ChefState state, Bar bar)
        {
            for (int slot = 0; slot < bar.WasCooling.Length; slot++)
            {
                Special active = state.ActiveAt(slot);
                if (active == Special.None)
                {
                    bar.WasCooling[slot] = false;
                    continue;
                }
                bool cooling = SpecialRuntime.Now < state.Timing[slot].CooldownUntil;
                if (bar.WasCooling[slot] && !cooling)
                {
                    bar.ReadyAt[slot] = Time.unscaledTime;
                    SpecialFx.Burst(state.Position, SpecialFx.ColourOf(active), 1.1f, 0.45f);
                }
                bar.WasCooling[slot] = cooling;
            }
        }

        private static PlayerInputLookup.Player PlayerOf(ChefState state)
        {
            return state.Provider != null ? state.Provider.GetID() : PlayerInputLookup.Player.One;
        }

        private static float BarWidth(ChefState state)
        {
            return FullWidth(state.Loadout);
        }

        private void Draw(ChefState state, Bar bar, bool several, SkillBarMode mode, float chipRow)
        {
            float now = SpecialRuntime.Now;
            float real = Time.unscaledTime;

            // --- the active buttons: the one the SKILL KEY uses big, the other small beside it ---
            int mainSlot = state.SelectedSlot;
            Special active = state.ActiveAt(mainSlot);
            Special other = state.ActiveAt(1 - mainSlot);
            bool hasActive = active != Special.None;
            bool two = hasActive && other != Special.None;
            bar.Active.Root.gameObject.SetActive(hasActive);
            bar.Second.Root.gameObject.SetActive(two);
            bool onScreen = mode == SkillBarMode.OnScreen;
            // Over a chef the bar is small: just the icons, the timers and what a press did.
            bar.Key.transform.parent.gameObject.SetActive(hasActive && onScreen);
            string switchKey = two && onScreen ? SpecialInput.SwitchNameFor(PlayerOf(state)) : null;
            bar.SwitchBack.gameObject.SetActive(switchKey != null);
            if (switchKey != null) bar.SwitchKey.text = switchKey;
            float smallX = 0f;
            if (hasActive)
            {
                smallX = ActiveSize + 16f + (two ? SecondGap + SecondSize : 0f);
                DrawActive(state, bar, bar.Active, mainSlot, true, now, real);
                if (two) DrawActive(state, bar, bar.Second, 1 - mainSlot, false, now, real);
                bar.Key.text = SpecialInput.NameFor(PlayerOf(state));

                // Over the button, centred: what a press does now. (Why a press was refused floats
                // up over the chef instead, see Deny.)
                bool recallHint = now < state.Timing[mainSlot].ActiveUntil && active == Special.Recall;
                bar.Reason.gameObject.SetActive(recallHint);
                if (recallHint)
                {
                    bar.Reason.text = Lang.T("PRESS AGAIN TO RETURN", "BẤM LẦN NỮA ĐỂ QUAY VỀ");
                    bar.Reason.color = Color.Lerp(SpecialFx.ColourOf(active), Color.white, 0.6f);
                }
            }
            else bar.Reason.gameObject.SetActive(false);

            // Which key opens the details panel, next to the small icons.
            string detailsKey = onScreen ? SpecialInput.DetailsNameFor(PlayerOf(state)) : null;
            bar.DetailsHint.gameObject.SetActive(detailsKey != null);
            if (detailsKey != null)
            {
                bar.DetailsHint.text = detailsKey + ": " + (_details.IsOpen(PlayerOf(state)) ? Lang.T("HIDE INFO", "ẨN THÔNG TIN") : Lang.T("SKILL INFO", "XEM KỸ NĂNG"));
                bar.DetailsHint.rectTransform.anchoredPosition = new Vector2(smallX, 14f);
            }

            // --- passives and quirk ------------------------------------------------------------
            int slot = 0;
            for (int row = 0; row < SmallSlots.Length; row++)
            {
                int i = SmallSlots[row];
                Icon icon = bar.Small[row];
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
                bool quirk = i == (int)SpecialSlot.Quirk;
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
            if (onScreen) DrawChips(state, bar, BarWidth(state), chipRow);
            else
            {
                ClearChips(bar);
                Compact(state, bar, now, real);
            }
            // Several players: a P1..P4 tag and a line in their colour under the bar say whose it is.
            bool tagged = several && onScreen;
            bar.Player.transform.parent.gameObject.SetActive(tagged);
            bar.Strip.gameObject.SetActive(tagged);
            if (tagged)
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
                Color tint = GameArt.ArrowTints[Mathf.Clamp(index, 0, GameArt.ArrowTints.Length - 1)];
                bar.PlayerBack.color = tint;
                bar.Strip.color = tint;
                bar.Strip.rectTransform.sizeDelta = new Vector2(BarWidth(state) + 24f, 5f);
                bar.Strip.rectTransform.anchoredPosition = new Vector2(BarWidth(state) * 0.5f, 3f);
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
        private static void ClearChips(Bar bar)
        {
            foreach (Chip chip in bar.Chips)
                if (chip.Root != null) Object.Destroy(chip.Root.gameObject);
            bar.Chips.Clear();
        }

        /// <summary>
        /// Over a chef, only what matters right now is drawn, packed together, so the stations
        /// around the chef stay in view: the active while it is ready, running or waiting for a
        /// second press (or just refused), and a passive or quirk only while it shows a timer or a
        /// count. While the active recharges nothing of it shows; a ring under the chef says when
        /// it is ready again. With nothing to show the bar disappears.
        /// </summary>
        private static void Compact(ChefState state, Bar bar, float now, float real)
        {
            float x = 0f, width = 0f;
            int mainSlot = state.SelectedSlot;
            bool hasActive = state.ActiveAt(mainSlot) != Special.None;
            ActiveTimer timing = state.Timing[mainSlot];
            float denied;
            bool refused = s_deniedAt.TryGetValue(state, out denied) && real - denied < FlashSeconds;
            bool showActive = hasActive && (now >= timing.CooldownUntil || now < timing.ActiveUntil || now < state.PendingUntil || refused);
            bar.Active.Root.gameObject.SetActive(showActive);
            bar.SwitchBack.gameObject.SetActive(false);
            if (!showActive) bar.Reason.gameObject.SetActive(false);
            if (showActive)
            {
                width = ActiveSize;
                x = ActiveSize + 16f;
            }
            // The other active only while its effect runs (so a running Time Out still shows).
            int otherSlot = 1 - mainSlot;
            bool showOther = state.ActiveAt(otherSlot) != Special.None && now < state.Timing[otherSlot].ActiveUntil;
            bar.Second.Root.gameObject.SetActive(showOther);
            if (showOther)
            {
                bar.Second.Root.anchoredPosition = new Vector2(x + SecondSize * 0.5f, ActiveSize * 0.5f + 30f);
                width = x + SecondSize;
                x += SecondSize + 16f;
            }
            for (int row = 0; row < SmallSlots.Length; row++)
            {
                Icon icon = bar.Small[row];
                var special = (Special)state.Loadout[SmallSlots[row]];
                if (special == Special.None) continue;
                Vector2 timer;
                bool timed = state.Timers.TryGetValue(special, out timer) && timer.x > now;
                bool show = timed || CountText(state, special) != "";
                icon.Root.gameObject.SetActive(show);
                if (!show) continue;
                // Small icons sit level with the middle of the active button.
                icon.Root.anchoredPosition = new Vector2(x + SmallSize * 0.5f, ActiveSize * 0.5f + 30f);
                width = x + SmallSize;
                x += SmallSize + SmallGap;
            }
            bar.CompactWidth = width;
            if (width <= 0f) bar.Root.gameObject.SetActive(false);
        }

        /// <summary>
        /// One active's button: its cooldown sweep and seconds, a ring while its effect runs, a
        /// glow when ready. The <paramref name="main"/> one (the SKILL KEY uses it) also shakes
        /// when refused and pops when switched to; the other one sits dimmed beside it.
        /// </summary>
        private static void DrawActive(ChefState state, Bar bar, Icon icon, int slot, bool main, float now, float real)
        {
            Special active = state.ActiveAt(slot);
            ActiveTimer timing = state.Timing[slot];
            Color colour = SpecialFx.ColourOf(active);
            SetSpecial(icon, active);
            bool cooling = now < timing.CooldownUntil;
            bool running = now < timing.ActiveUntil;
            bool pending = main && now < state.PendingUntil;
            float left = timing.CooldownUntil - now;

            icon.Back.color = cooling && !running ? Color.Lerp(colour, Color.black, 0.55f) : colour;
            icon.Sweep.enabled = cooling && timing.CooldownTotal > 0f && timing.CooldownTotal < 999f;
            if (icon.Sweep.enabled) icon.Sweep.fillAmount = Mathf.Clamp01(left / timing.CooldownTotal);

            // The effect running: a bright ring that empties as it runs out.
            float duration = timing.ActiveUntil - now;
            float total = Mathf.Max(0.01f, timing.ActiveUntil - (timing.CooldownUntil - timing.CooldownTotal));
            icon.Ring.fillAmount = running ? Mathf.Clamp01(duration / total) : 1f;
            Color ring = running ? Color.white : colour;
            float readyFlash = Mathf.Clamp01(1f - (real - bar.ReadyAt[slot]) / 0.6f);
            if (!cooling && !running) ring.a = 0.55f + 0.35f * Mathf.Sin(real * 4f) * 0.5f + 0.45f * readyFlash;
            else if (!running) ring.a = 0.25f;
            icon.Ring.color = ring;
            Color glow = colour;
            glow.a = !cooling ? 0.35f + 0.5f * readyFlash : 0f;
            if (running) glow.a = 0.5f;
            if (!main) glow.a *= 0.5f;
            icon.Glow.color = glow;

            string seconds = null;
            if (running) seconds = Mathf.CeilToInt(duration).ToString();
            else if (cooling && timing.CooldownTotal < 999f) seconds = left >= 10f ? Mathf.CeilToInt(left).ToString() : left.ToString("0.0");
            else if (cooling) seconds = Lang.T("USED", "HẾT");
            icon.Seconds.text = seconds ?? "";
            icon.Seconds.color = running ? Color.white : new Color(1f, 1f, 1f, 0.95f);
            SetGlyphColour(icon, seconds != null ? new Color(1f, 1f, 1f, 0.28f) : Color.white);
            icon.Count.text = CountText(state, active);

            // Shake when refused; pop when used or switched to.
            float scale = 1f, shake = 0f;
            float denied = -100f;
            bool refused = main && s_deniedAt.TryGetValue(state, out denied) && real - denied < FlashSeconds;
            if (refused && real - denied < 0.35f)
            {
                float t = (real - denied) / 0.35f;
                shake = Mathf.Sin(t * Mathf.PI * 6f) * 9f * (1f - t);
                icon.Back.color = Color.Lerp(icon.Back.color, DenyColour, 1f - t);
            }
            float used;
            Special usedSpecial;
            if (s_usedAt.TryGetValue(state, out used) && s_usedSpecial.TryGetValue(state, out usedSpecial) && usedSpecial == active)
            {
                float age = real - used;
                if (age >= 0f && age < 0.3f) scale = 1f + 0.22f * Mathf.Sin(Mathf.PI * age / 0.3f);
            }
            float switched;
            if (main && s_switchedAt.TryGetValue(state, out switched))
            {
                float age = real - switched;
                if (age >= 0f && age < 0.25f) scale *= 1f + 0.15f * Mathf.Sin(Mathf.PI * age / 0.25f);
            }
            scale *= 1f + 0.12f * readyFlash * Mathf.Sin(Mathf.PI * Mathf.Clamp01((real - bar.ReadyAt[slot]) / 0.6f));
            icon.Root.localScale = new Vector3(scale, scale, 1f);
            float x = main ? ActiveSize * 0.5f : ActiveSize + SecondGap + SecondSize * 0.5f;
            icon.Root.anchoredPosition = new Vector2(x + shake, ActiveSize * 0.5f + 30f);
            if (pending) SetGlyphColour(icon, new Color(1f, 1f, 1f, 0.5f + 0.5f * Mathf.Sin(real * 12f)));
        }

        private void DrawChips(ChefState state, Bar bar, float barWidth, float maxRow)
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
                while (end + 1 < bar.Chips.Count && row + ChipGap + widths[end + 1] <= maxRow)
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
            bar.Second = MakeIcon(bar.Root, "Second", SecondSize, 32, 28);
            bar.SecondGroup = bar.Second.Root.gameObject.AddComponent<CanvasGroup>();
            bar.SecondGroup.alpha = 0.72f;
            bar.SecondGroup.interactable = false;
            bar.SecondGroup.blocksRaycasts = false;
            bar.Second.Root.gameObject.SetActive(false);
            // Under the other active: the key that switches to it.
            RectTransform switchBack = Rect("SwitchBack", bar.Root, new Vector2(0f, 0f),
                new Vector2(ActiveSize + SecondGap + SecondSize * 0.5f, 14f + (ActiveSize - SecondSize) * 0.5f), new Vector2(SecondSize + 8f, 24f));
            bar.SwitchBack = switchBack.gameObject.AddComponent<Image>();
            bar.SwitchBack.raycastTarget = false;
            bar.SwitchBack.color = KeyBack;
            bar.SwitchKey = MakeText(switchBack, "SwitchKey", 15, TextAnchor.MiddleCenter);
            bar.SwitchKey.rectTransform.anchoredPosition = Vector2.zero;
            bar.SwitchKey.color = new Color(0.95f, 0.97f, 1f, 0.9f);
            switchBack.gameObject.SetActive(false);

            RectTransform keyBack = Rect("KeyBack", bar.Root, new Vector2(0f, 0f), new Vector2(ActiveSize * 0.5f, 14f), new Vector2(ActiveSize, 26f));
            bar.KeyBack = keyBack.gameObject.AddComponent<Image>();
            bar.KeyBack.raycastTarget = false;
            bar.KeyBack.color = KeyBack;
            bar.Key = MakeText(keyBack, "Key", 18, TextAnchor.MiddleCenter);
            bar.Key.rectTransform.anchoredPosition = Vector2.zero;
            bar.Key.color = new Color(0.95f, 0.97f, 1f, 1f);

            for (int i = 0; i < bar.Small.Length; i++) bar.Small[i] = MakeIcon(bar.Root, "Small" + i, SmallSize, 24, 22);


            // Centred over the active button (its top is at ActiveSize + 30).
            bar.Reason = MakeText(bar.Root, "Reason", 24, TextAnchor.LowerCenter);
            bar.Reason.rectTransform.pivot = new Vector2(0.5f, 0f);
            bar.Reason.rectTransform.anchoredPosition = new Vector2(ActiveSize * 0.5f, ActiveSize + 42f);
            bar.Reason.gameObject.SetActive(false);

            bar.DetailsHint = MakeText(bar.Root, "DetailsHint", 16, TextAnchor.MiddleLeft);
            bar.DetailsHint.rectTransform.anchorMin = bar.DetailsHint.rectTransform.anchorMax = Vector2.zero;
            bar.DetailsHint.rectTransform.pivot = new Vector2(0f, 0.5f);
            bar.DetailsHint.color = new Color(0.86f, 0.9f, 0.96f, 0.8f);

            // A line in the player's colour under the bar (several players on this machine).
            RectTransform strip = Rect("Strip", bar.Root, new Vector2(0f, 0f), Vector2.zero, new Vector2(10f, 5f));
            bar.Strip = strip.gameObject.AddComponent<Image>();
            bar.Strip.raycastTarget = false;
            bar.Strip.sprite = SpecialFx.RoundBox;
            bar.Strip.type = Image.Type.Sliced;
            strip.gameObject.SetActive(false);

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
