using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// The special picker: CHOOSE SKILLS on a card (or a click on one of the card's special icons)
    /// opens it. Along the top are all the special slots (ACTIVE, PASSIVE, PASSIVE, QUIRK) with
    /// what is in each, drawn in each slot's own style (see <see cref="SlotIcon"/>); under them,
    /// every choice for the slot in focus is a tile (icon and name) in a grid, grouped by category
    /// under headers; a strip at the bottom tells what the highlighted one does and whether it can
    /// be picked (one per kitchen and taken, or clashing with another pick), and under it the keys.
    ///
    /// On the slots, LEFT / RIGHT move between them, DOWN or SELECT goes into the grid, BACK
    /// closes. In the grid, the directions move (UP from the top row goes back to the slots),
    /// SELECT picks for that slot and goes back to the slots (SELECT on the slot's current pick
    /// empties the slot instead), BACK goes back without picking. The mouse can point, click a slot
    /// or a tile, and scroll; a right click closes.
    ///
    /// One per local player. With one player on this machine it opens as a dialog over the dimmed
    /// screen; with several, each player's picker stays in their own lane of the screen, over their
    /// own card, so the others can keep choosing while it is open. Built on the skill screen's
    /// canvas with the game's dialog art, like the rest of the screen.
    /// </summary>
    internal sealed class SkillPicker
    {
        // Layout (1920x1080 reference units).
        private const float MaxWidth = 1240f;
        private const float ScreenWidth = 1840f;
        private const float PanelGap = 20f;
        private const float PanelHeight = 940f;
        private const float PanelY = 10f;
        private const float Pad = 46f;
        private const float HeaderHeight = 70f;
        private const float FooterHeight = 170f;
        // The key legend, inside the panel under the details strip.
        private const float LegendHeight = 44f;
        private const float TileWidth = 96f;
        private const float TileHeight = 118f;
        private const float TileGap = 10f;
        private const float DiscSize = 66f;
        private const float SectionHeader = 34f;
        private const float SectionGap = 14f;
        // The slots along the top: one box each, side by side.
        private const float StripHeight = 128f;
        private const float StripGap = 12f;
        private const float StripBoxGap = 10f;
        // The active's icon across; the others are smaller (see SlotIcon).
        private const float StripIcon = 56f;

        private static readonly Color PanelTint = new Color(0.3f, 0.4f, 0.52f, 1f);
        private static readonly Color TileColour = new Color(0.08f, 0.11f, 0.18f, 0.82f);
        private static readonly Color TileCursorColour = new Color(0.2f, 0.28f, 0.42f, 0.95f);
        private static readonly Color CursorFrame = new Color(1f, 0.86f, 0.3f, 1f);
        private static readonly Color CurrentColour = new Color(0.55f, 1f, 0.5f, 1f);
        private static readonly Color BlockedColour = new Color(1f, 0.5f, 0.44f, 1f);
        private static readonly Color TitleColour = new Color(0.95f, 0.92f, 0.93f, 1f);
        private static readonly Color BodyColour = new Color(0.86f, 0.9f, 0.96f, 1f);
        private static readonly Color MutedColour = new Color(0.62f, 0.68f, 0.78f, 1f);
        private static readonly Color ShadowColour = new Color(0.08f, 0.14f, 0.22f, 0.65f);
        private static readonly Color NoneColour = new Color(0.45f, 0.5f, 0.6f, 1f);

        private static readonly List<SkillPicker> s_open = new List<SkillPicker>();
        private static GameObject s_dim;
        private static bool s_padNoticeLogged;

        private sealed class Tile
        {
            public Special Special;
            public RectTransform Root;
            public Image Back, Frame, Disc, Picture;
            public Text Glyph, Name, Badge;
            public Image BadgeBack;
            public CanvasGroup Group;
            public Vector2 Position; // top-left in content space (y down, positive)
            public int Row;
        }

        /// <summary>One slot along the top: its name, the pick in the slot's style, and the pick's name.</summary>
        private sealed class SlotBox
        {
            public RectTransform Root;
            public Image Back, Frame;
            public SlotIcon Icon;
            public Text Label, Name;
            public int Shown = -1;
            public float PunchAt = -10f;
        }

        internal readonly PlayerInputLookup.Player Player;
        /// <summary>The player plays alone with two chefs: a quirk then applies to both.</summary>
        internal bool Solo;
        private readonly GameArt _art;
        private readonly RectTransform _panel, _viewport, _content;
        private readonly Sprite _panelSprite;
        private readonly List<Tile> _tiles = new List<Tile>();
        private readonly SlotBox[] _boxes;
        private readonly RectTransform _stripLegend, _gridLegend;
        private readonly Image _footIcon, _footDisc;
        private readonly Text _footGlyph, _footName, _footTag, _footText, _footStatus;
        private readonly Text _titleText, _ownerText;
        private bool _narrow;
        private readonly float _openedAt;
        // The slot in focus, and whether the grid (rather than the slots along the top) has the focus.
        private SpecialSlot _slot;
        private bool _inGrid;
        private int _cursor;
        private float _scroll, _scrollTarget, _contentHeight;
        private float _shownWidth = -1f;
        private Vector3 _lastMouse;
        // The mouse only points and clicks once it has really moved (or opened the picker): a
        // resting or jittering pointer, or a controller set up to also click, never takes over.
        private bool _mouseMode;
        private Vector3 _mouseAnchor;
        private readonly SpecialInput.PadNav _pad;
        private float _deniedAt = -10f;
        private int _deniedTile = -1;
        // The part of the screen this picker keeps to, when several players share the screen.
        private bool _hasLane;
        private float _laneLeft, _laneRight;

        private SkillPicker(Transform canvas, GameArt art, PlayerInputLookup.Player player, string owner, SpecialSlot slot,
            int[] loadout, bool shared)
        {
            Player = player;
            _slot = slot;
            _art = art;
            _openedAt = Time.unscaledTime;
            _lastMouse = Input.mousePosition;
            _mouseAnchor = _lastMouse;
            _pad = new SpecialInput.PadNav(player);

            // One player on this machine: the picker is a dialog over a dimmed screen. With several,
            // the others are still choosing, so nothing is dimmed.
            if (s_dim == null && !shared)
            {
                RectTransform dim = Child("PickerDim", canvas);
                Stretch(dim);
                Image dimImage = dim.gameObject.AddComponent<Image>();
                dimImage.color = new Color(0.03f, 0.05f, 0.08f, 0.55f);
                s_dim = dim.gameObject;
            }
            if (s_dim != null) s_dim.transform.SetAsLastSibling();

            _panel = Child("Picker" + (int)player, canvas);
            _panel.sizeDelta = new Vector2(MaxWidth, PanelHeight);
            _panelSprite = art != null ? art.Sliced(GameArt.CardSprites[(int)player % GameArt.CardSprites.Length],
                new Vector4(120f, 95f, 120f, 95f), 135f) : null;
            Image back = _panel.gameObject.AddComponent<Image>();
            back.raycastTarget = false;
            back.sprite = _panelSprite;
            back.type = _panelSprite != null ? Image.Type.Sliced : Image.Type.Simple;
            back.color = _panelSprite != null ? Color.white : PanelTint;

            // Header: what's being chosen (and for whom when several players share the screen).
            RectTransform title = Child("Title", _panel);
            title.anchorMin = new Vector2(0f, 1f);
            title.anchorMax = new Vector2(1f, 1f);
            title.pivot = new Vector2(0.5f, 1f);
            title.offsetMin = new Vector2(Pad, -Pad - HeaderHeight + 10f);
            title.offsetMax = new Vector2(-Pad, -Pad + 6f);
            _titleText = AddText(title, Header, 44, TitleColour, TextAnchor.MiddleLeft, TitleFor(slot));
            AddShadow(_titleText.gameObject);
            if (!string.IsNullOrEmpty(owner))
            {
                // Its own object: a GameObject can hold only one Text.
                RectTransform ownerRect = Child("Owner", title);
                Stretch(ownerRect);
                _ownerText = AddText(ownerRect, Header, 30, MutedColour, TextAnchor.MiddleRight, owner);
                AddShadow(_ownerText.gameObject);
            }

            // Every slot, side by side under the header. The boxes keep half a gap in from their
            // share of the row, so the row is widened by that much either side.
            RectTransform strip = Child("Slots", _panel);
            strip.anchorMin = new Vector2(0f, 1f);
            strip.anchorMax = new Vector2(1f, 1f);
            strip.pivot = new Vector2(0.5f, 1f);
            strip.offsetMin = new Vector2(Pad - StripBoxGap / 2f, -Pad - HeaderHeight - StripHeight);
            strip.offsetMax = new Vector2(-Pad + StripBoxGap / 2f, -Pad - HeaderHeight);
            _boxes = new SlotBox[SpecialCatalog.SlotCount];
            for (int i = 0; i < _boxes.Length; i++) _boxes[i] = MakeSlotBox(strip, i, _boxes.Length);

            // The grid, scrolled inside a masked viewport.
            _viewport = Child("Grid", _panel);
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = new Vector2(Pad - 6f, Pad + LegendHeight + FooterHeight);
            _viewport.offsetMax = new Vector2(-Pad + 6f, -Pad - HeaderHeight - StripHeight - StripGap);
            _viewport.gameObject.AddComponent<RectMask2D>();
            _content = Child("Content", _viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.offsetMin = Vector2.zero;
            _content.offsetMax = Vector2.zero;
            BuildTiles(CurrentIn(loadout, slot));

            // Footer: the highlighted choice.
            RectTransform foot = Child("Details", _panel);
            foot.anchorMin = new Vector2(0f, 0f);
            foot.anchorMax = new Vector2(1f, 0f);
            foot.pivot = new Vector2(0.5f, 0f);
            foot.offsetMin = new Vector2(Pad, Pad + LegendHeight);
            foot.offsetMax = new Vector2(-Pad, Pad + LegendHeight + FooterHeight - 14f);
            Image footBack = foot.gameObject.AddComponent<Image>();
            footBack.raycastTarget = false;
            footBack.sprite = SpecialFx.RoundBox;
            footBack.type = Image.Type.Sliced;
            footBack.color = new Color(0.05f, 0.08f, 0.13f, 0.75f);

            RectTransform disc = Child("Disc", foot);
            disc.anchorMin = disc.anchorMax = new Vector2(0f, 0.5f);
            disc.sizeDelta = new Vector2(112f, 112f);
            disc.anchoredPosition = new Vector2(84f, 0f);
            _footDisc = disc.gameObject.AddComponent<Image>();
            _footDisc.raycastTarget = false;
            _footDisc.sprite = SpecialFx.Disc;
            RectTransform footPicture = Child("Picture", disc);
            footPicture.sizeDelta = new Vector2(100f, 100f);
            _footIcon = footPicture.gameObject.AddComponent<Image>();
            _footIcon.raycastTarget = false;
            _footIcon.preserveAspect = true;
            RectTransform footGlyph = Child("Glyph", disc);
            footGlyph.sizeDelta = new Vector2(112f, 112f);
            _footGlyph = AddText(footGlyph, Header, 40, Color.white, TextAnchor.MiddleCenter, "");
            AddShadow(_footGlyph.gameObject);

            _footName = FootText(foot, "Name", 34, TitleColour, 16f, 40f, Header);
            _footTag = FootText(foot, "Tag", 20, MutedColour, 52f, 26f, Header);
            _footText = FootText(foot, "About", 22, BodyColour, 80f, 54f, Body);
            _footText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _footStatus = FootText(foot, "Status", 20, CurrentColour, 128f, 26f, Header);

            // The keys, inside the panel under the details: one set for the slots, one for the grid.
            _stripLegend = MakeLegend(true);
            _gridLegend = MakeLegend(false);

            s_open.Add(this);
            s_open.Sort(delegate(SkillPicker a, SkillPicker b) { return ((int)a.Player).CompareTo((int)b.Player); });
            LayoutAll();
            ScrollToCursor(true);
        }

        /// <summary>
        /// Opens a picker for this player's specials on the skill screen's canvas, with
        /// <paramref name="slot"/> in focus.
        /// </summary>
        internal static SkillPicker Open(Transform canvas, GameArt art, PlayerInputLookup.Player player, string owner,
            SpecialSlot slot, int[] loadout, bool byMouse, bool shared)
        {
            var picker = new SkillPicker(canvas, art, player, owner, slot, loadout, shared);
            picker._mouseMode = byMouse;
            return picker;
        }

        internal bool IsOpen
        {
            get { return _panel != null; }
        }

        internal void Close()
        {
            s_open.Remove(this);
            if (_panel != null) Object.Destroy(_panel.gameObject);
            if (_panelSprite != null) Object.Destroy(_panelSprite);
            if (s_open.Count == 0 && s_dim != null)
            {
                Object.Destroy(s_dim);
                s_dim = null;
            }
            else LayoutAll();
        }

        /// <summary>The skill screen closed: every picker goes with it.</summary>
        internal static void CloseAll()
        {
            foreach (SkillPicker picker in new List<SkillPicker>(s_open)) picker.Close();
            s_open.Clear();
            if (s_dim != null) Object.Destroy(s_dim);
            s_dim = null;
        }

        /// <summary>
        /// Keeps this picker inside its player's lane of the screen (from <paramref name="left"/> to
        /// <paramref name="right"/>, in reference units from the middle), over their own card.
        /// </summary>
        internal void SetLane(float left, float right)
        {
            if (_hasLane && Mathf.Abs(_laneLeft - left) < 0.5f && Mathf.Abs(_laneRight - right) < 0.5f) return;
            _hasLane = true;
            _laneLeft = left;
            _laneRight = right;
            LayoutAll();
        }

        /// <summary>Is this screen point on any open picker?</summary>
        internal static bool AnyAt(Vector3 point)
        {
            foreach (SkillPicker picker in s_open)
                if (picker._panel != null && RectTransformUtility.RectangleContainsScreenPoint(picker._panel, point, null)) return true;
            return false;
        }

        /// <summary>
        /// A picker with a lane sits in it, as wide as the lane allows. The rest (one player on this
        /// machine) share the screen's width side by side, in player order.
        /// </summary>
        private static void LayoutAll()
        {
            var shared = new List<SkillPicker>();
            foreach (SkillPicker picker in s_open)
            {
                if (picker._panel == null) continue;
                picker._panel.anchorMin = picker._panel.anchorMax = new Vector2(0.5f, 0.5f);
                picker._panel.SetAsLastSibling();
                if (!picker._hasLane)
                {
                    shared.Add(picker);
                    continue;
                }
                float laneWidth = Mathf.Max(200f, picker._laneRight - picker._laneLeft - PanelGap);
                picker._panel.sizeDelta = new Vector2(Mathf.Min(MaxWidth, laneWidth), PanelHeight);
                picker._panel.anchoredPosition = new Vector2((picker._laneLeft + picker._laneRight) / 2f, PanelY);
            }
            int n = shared.Count;
            if (n == 0) return;
            float width = Mathf.Min(MaxWidth, (ScreenWidth - (n - 1) * PanelGap) / n);
            float x = -(n * width + (n - 1) * PanelGap) / 2f + width / 2f;
            foreach (SkillPicker picker in shared)
            {
                picker._panel.sizeDelta = new Vector2(width, PanelHeight);
                picker._panel.anchoredPosition = new Vector2(x, PanelY);
                x += width + PanelGap;
            }
        }

        // --- every frame ------------------------------------------------------------------------

        /// <summary>
        /// What a frame of input did. Picked: a slot's pick changed (or was removed); the picker
        /// stays open. Returned: went back from the grid to the slots.
        /// </summary>
        internal enum Outcome { None, Moved, Picked, Refused, Returned, Closed }

        /// <summary>
        /// Reads this player's buttons (and the mouse). When the outcome is Picked,
        /// <paramref name="pickedSlot"/> and <paramref name="picked"/> say which slot now holds
        /// what (<see cref="Special.None"/> when it was emptied).
        /// </summary>
        internal Outcome HandleInput(ILogicalButton up, ILogicalButton down, ILogicalButton left, ILogicalButton right,
            ILogicalButton select, ILogicalButton cancel, int[] loadout, HashSet<Special> taken,
            out SpecialSlot pickedSlot, out Special picked)
        {
            picked = Special.None;
            pickedSlot = _slot;
            if (_panel == null) return Outcome.Closed;
            // The player's own pad, read directly as well: the menu buttons can be used up by
            // something else first, and this keeps a controller working whatever happens to them.
            bool[] pad = _pad.Poll();
            // The press that opened it.
            if (Time.unscaledTime - _openedAt < 0.05f) return Outcome.None;

            var game = new[]
            {
                up.JustPressed(), down.JustPressed(), left.JustPressed(), right.JustPressed(), select.JustPressed(),
                cancel.JustPressed(),
            };
            for (int i = 0; i < game.Length && i < pad.Length; i++)
            {
                if (pad[i] && !game[i] && !s_padNoticeLogged)
                {
                    s_padNoticeLogged = true;
                    Plugin.Log.LogInfo("Bảng chọn kỹ năng: nút menu của tay cầm không tới được (nơi khác đã dùng); đọc thẳng từ tay cầm.");
                }
                game[i] |= pad[i];
            }
            bool goUp = game[SpecialInput.PadNav.Up], goDown = game[SpecialInput.PadNav.Down];
            bool goLeft = game[SpecialInput.PadNav.Left], goRight = game[SpecialInput.PadNav.Right];
            bool choose = game[SpecialInput.PadNav.Select];
            bool back = game[SpecialInput.PadNav.Cancel];

            if (back)
            {
                if (!_inGrid) return Outcome.Closed;
                _inGrid = false;
                return Outcome.Returned;
            }

            int before = _cursor;
            SpecialSlot slotBefore = _slot;
            bool gridBefore = _inGrid;
            bool pick = false;
            if (!_inGrid)
            {
                // On the slots: LEFT / RIGHT pick the slot, DOWN or SELECT go into its choices.
                if (goLeft) SetSlot(StepSlot(_slot, -1), loadout);
                if (goRight) SetSlot(StepSlot(_slot, 1), loadout);
                if (goDown || choose) EnterGrid(loadout);
            }
            else
            {
                if (goLeft) MoveFlat(-1);
                if (goRight) MoveFlat(1);
                if (goUp)
                {
                    // From the top row, back up to the slots.
                    if (_tiles[_cursor].Row == 0) _inGrid = false;
                    else MoveRow(-1);
                }
                if (goDown) MoveRow(1);
                pick = choose;
            }

            // The mouse: once it has moved (or it opened the picker), pointing at a tile moves the
            // highlight, a click on a slot puts it in focus, a click on a tile picks it, and a right
            // click or a click outside closes. Keys and buttons hand control back until it moves
            // again. The wheel always scrolls.
            Vector3 mouse = Input.mousePosition;
            bool keyed = goUp || goDown || goLeft || goRight || choose;
            if (keyed)
            {
                _mouseMode = false;
                _mouseAnchor = mouse;
            }
            else if (!_mouseMode && (mouse - _mouseAnchor).sqrMagnitude > 144f) _mouseMode = true;
            bool mouseMoved = (mouse - _lastMouse).sqrMagnitude > 4f;
            _lastMouse = mouse;
            bool inPanel = RectTransformUtility.RectangleContainsScreenPoint(_panel, mouse, null);
            if (_mouseMode && !keyed)
            {
                int under = TileUnder(mouse);
                int box = BoxUnder(mouse);
                if (under >= 0 && mouseMoved)
                {
                    _cursor = under;
                    _inGrid = true;
                }
                if (Input.GetMouseButtonDown(0))
                {
                    if (box >= 0)
                    {
                        SetSlot((SpecialSlot)box, loadout);
                        _inGrid = false;
                    }
                    else if (under >= 0)
                    {
                        _cursor = under;
                        _inGrid = true;
                        pick = true;
                    }
                    else if (!inPanel && s_open.Count == 1) return Outcome.Closed;
                }
                if (Input.GetMouseButtonDown(1) && inPanel) return Outcome.Closed;
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f && inPanel) _scrollTarget = Mathf.Clamp(_scrollTarget - wheel * 90f, 0f, MaxScroll());

            if (_cursor != before) ScrollToCursor(false);

            if (pick)
            {
                Special candidate = _tiles[_cursor].Special;
                // SELECT on the slot's current pick takes it out again: the slot is emptied.
                if (candidate == CurrentIn(loadout, _slot))
                {
                    _inGrid = false;
                    pickedSlot = _slot;
                    return Outcome.Picked;
                }
                if (!Allowed(candidate, loadout, taken))
                {
                    _deniedAt = Time.unscaledTime;
                    _deniedTile = _cursor;
                    return Outcome.Refused;
                }
                // Picked: back to the slots, to see it there and go on to the next one.
                _inGrid = false;
                pickedSlot = _slot;
                picked = candidate;
                return Outcome.Picked;
            }
            return _cursor != before || _slot != slotBefore || _inGrid != gridBefore ? Outcome.Moved : Outcome.None;
        }

        private static SpecialSlot StepSlot(SpecialSlot slot, int step)
        {
            int count = SpecialCatalog.SlotCount;
            return (SpecialSlot)(((int)slot + step + count) % count);
        }

        private static Special CurrentIn(int[] loadout, SpecialSlot slot)
        {
            return loadout != null && (int)slot < loadout.Length ? (Special)loadout[(int)slot] : Special.None;
        }

        private static string TitleFor(SpecialSlot slot)
        {
            return slot == SpecialSlot.Active ? Lang.T("CHOOSE YOUR ACTIVE", "CHỌN KỸ NĂNG CHỦ ĐỘNG")
                : slot == SpecialSlot.Quirk ? Lang.T("CHOOSE A QUIRK", "CHỌN MỘT TẬT") : Lang.T("CHOOSE A PASSIVE", "CHỌN KỸ NĂNG BỊ ĐỘNG");
        }

        /// <summary>Puts another slot in focus: the grid then shows that slot's choices.</summary>
        private void SetSlot(SpecialSlot slot, int[] loadout)
        {
            if (slot == _slot) return;
            _slot = slot;
            _titleText.text = TitleFor(slot);
            _boxes[(int)slot].PunchAt = Time.unscaledTime;
            BuildTiles(CurrentIn(loadout, slot));
        }

        /// <summary>Into the grid, on the slot's current pick.</summary>
        private void EnterGrid(int[] loadout)
        {
            // A slot with nothing to choose (every choice turned off) has no grid to go into.
            if (_tiles.Count == 0) return;
            _inGrid = true;
            Special current = CurrentIn(loadout, _slot);
            for (int i = 0; i < _tiles.Count; i++)
                if (_tiles[i].Special == current) _cursor = i;
            ScrollToCursor(false);
        }

        private bool Allowed(Special candidate, int[] loadout, HashSet<Special> taken)
        {
            if (candidate == Special.None) return true;
            if (taken != null && taken.Contains(candidate)) return false;
            return SpecialCatalog.Fits(loadout, _slot, candidate);
        }

        private void MoveFlat(int step)
        {
            _cursor = (_cursor + step + _tiles.Count) % _tiles.Count;
        }

        /// <summary>Up/down: the tile in the row above/below nearest the current one's column.</summary>
        private void MoveRow(int step)
        {
            Tile here = _tiles[_cursor];
            int lastRow = _tiles[_tiles.Count - 1].Row;
            int target = here.Row + step;
            if (target < 0) target = lastRow;
            if (target > lastRow) target = 0;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (_tiles[i].Row != target) continue;
                float distance = Mathf.Abs(_tiles[i].Position.x - here.Position.x);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            if (best >= 0) _cursor = best;
        }

        private int TileUnder(Vector3 mouse)
        {
            if (!RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null)) return -1;
            for (int i = 0; i < _tiles.Count; i++)
                if (_tiles[i].Root != null && RectTransformUtility.RectangleContainsScreenPoint(_tiles[i].Root, mouse, null))
                    return i;
            return -1;
        }

        private int BoxUnder(Vector3 mouse)
        {
            for (int i = 0; i < _boxes.Length; i++)
                if (_boxes[i].Root != null && RectTransformUtility.RectangleContainsScreenPoint(_boxes[i].Root, mouse, null))
                    return i;
            return -1;
        }

        private float MaxScroll()
        {
            return Mathf.Max(0f, _contentHeight - _viewport.rect.height);
        }

        private void ScrollToCursor(bool instant)
        {
            if (_tiles.Count == 0 || _viewport == null) return;
            float view = _viewport.rect.height;
            if (view <= 1f) view = PanelHeight - 2f * Pad - HeaderHeight - StripHeight - StripGap - FooterHeight - LegendHeight;
            Tile tile = _tiles[_cursor];
            float top = tile.Position.y - SectionHeader - 8f;
            float bottom = tile.Position.y + TileHeight + 8f;
            if (top < _scrollTarget) _scrollTarget = top;
            if (bottom > _scrollTarget + view) _scrollTarget = bottom - view;
            _scrollTarget = Mathf.Clamp(_scrollTarget, 0f, MaxScroll());
            if (instant) _scroll = _scrollTarget;
        }

        // Below this panel width (a lane on a screen shared by three or four players) the header and
        // the details strip are packed tighter.
        private const float NarrowWidth = 640f;

        private void Fit(bool narrow)
        {
            FitLegend(_inGrid ? _gridLegend : _stripLegend);
            if (narrow == _narrow) return;
            _narrow = narrow;
            _titleText.fontSize = narrow ? 30 : 44;
            // The picker sits over its owner's card then, so the owner's name can go.
            if (_ownerText != null) _ownerText.gameObject.SetActive(!narrow);
            _footDisc.gameObject.SetActive(!narrow);
            foreach (Text text in new[] { _footName, _footTag, _footText, _footStatus })
            {
                RectTransform rect = text.rectTransform;
                rect.offsetMin = new Vector2(narrow ? 20f : 160f, rect.offsetMin.y);
            }
        }

        /// <summary>
        /// Shrinks a key legend to fit the panel whatever its width. Only an active legend can be
        /// measured (an inactive one's layout reads as empty), so this runs as one is shown.
        /// </summary>
        private void FitLegend(RectTransform legend)
        {
            if (!legend.gameObject.activeInHierarchy) return;
            float room = _panel.sizeDelta.x - 2f * Pad;
            legend.localScale = Vector3.one;
            LayoutRebuilder.ForceRebuildLayoutImmediate(legend);
            float wanted = LayoutUtility.GetPreferredWidth(legend);
            float scale = wanted > room && wanted > 1f ? room / wanted : 1f;
            legend.localScale = new Vector3(scale, scale, 1f);
        }

        /// <summary>Draws a frame: the slots, tiles' states, the highlight, the details strip.</summary>
        internal void Render(int[] loadout, HashSet<Special> taken)
        {
            if (_panel == null) return;
            float now = Time.unscaledTime;
            float width = _viewport.rect.width;
            if (width > 1f && Mathf.Abs(width - _shownWidth) > 0.5f)
            {
                _shownWidth = width;
                Fit(_panel.sizeDelta.x < NarrowWidth);
                LayoutTiles(width);
                ScrollToCursor(true);
            }

            // Opens with a little pop, like the game's dialogs.
            float open = Mathf.Clamp01((now - _openedAt) / 0.22f);
            float scale = Mathf.LerpUnclamped(0.9f, 1f, EaseOutBack(open));
            _panel.localScale = new Vector3(scale, scale, 1f);

            _scroll = Mathf.Lerp(_scroll, _scrollTarget, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 16f));
            _content.anchoredPosition = new Vector2(0f, _scroll);

            if (_stripLegend.gameObject.activeSelf == _inGrid || _gridLegend.gameObject.activeSelf != _inGrid)
            {
                _stripLegend.gameObject.SetActive(!_inGrid);
                _gridLegend.gameObject.SetActive(_inGrid);
                FitLegend(_inGrid ? _gridLegend : _stripLegend);
            }

            float pulse = 0.5f + 0.5f * Mathf.Sin(now * 5f);
            float denied = now - _deniedAt;
            for (int i = 0; i < _boxes.Length; i++)
            {
                SlotBox box = _boxes[i];
                int pickHere = loadout != null && i < loadout.Length ? loadout[i] : 0;
                if (box.Shown != pickHere)
                {
                    if (box.Shown != -1) box.PunchAt = now;
                    box.Shown = pickHere;
                    ShowBox(box, (Special)pickHere);
                }
                bool focus = i == (int)_slot;
                box.Back.color = focus ? TileCursorColour : TileColour;
                box.Frame.enabled = focus;
                // Bright and pulsing while the slots have the focus; steady while its grid has it.
                box.Frame.color = focus && !_inGrid ? Color.Lerp(CursorFrame, Color.white, 0.35f * pulse) : WithAlpha(CursorFrame, 0.6f);
                float grow = focus && !_inGrid ? 1.04f : 1f;
                box.Root.localScale = Punch(now - box.PunchAt, 0.08f) * grow;
            }

            Special current = CurrentIn(loadout, _slot);
            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile tile = _tiles[i];
                bool cursor = _inGrid && i == _cursor;
                bool mine = tile.Special == current;
                bool allowed = mine || Allowed(tile.Special, loadout, taken);
                bool isTaken = taken != null && taken.Contains(tile.Special);

                tile.Group.alpha = allowed ? 1f : 0.42f;
                tile.Back.color = cursor ? TileCursorColour : TileColour;
                tile.Frame.enabled = cursor || mine;
                tile.Frame.color = cursor ? Color.Lerp(CursorFrame, Color.white, 0.35f * pulse) : CurrentColour;

                float shake = 0f;
                if (i == _deniedTile && denied < 0.35f) shake = Mathf.Sin(denied / 0.35f * Mathf.PI * 6f) * 6f * (1f - denied / 0.35f);
                float grow = cursor ? 1.08f : 1f;
                tile.Root.localScale = new Vector3(grow, grow, 1f);
                tile.Root.anchoredPosition = new Vector2(tile.Position.x + TileWidth / 2f + shake, -tile.Position.y - TileHeight / 2f);

                string badge = mine ? Lang.T("CURRENT", "ĐANG CHỌN") : isTaken ? Lang.T("TAKEN", "ĐÃ BỊ LẤY") : !allowed ? Lang.T("CLASH", "KỴ NHAU") : null;
                tile.BadgeBack.gameObject.SetActive(badge != null);
                if (badge != null)
                {
                    tile.Badge.text = badge;
                    tile.BadgeBack.color = mine ? CurrentColour : BlockedColour;
                }
            }
            // On the slots, the details are the slot's own pick; in the grid, the highlighted tile.
            Special shown = _inGrid && _cursor < _tiles.Count ? _tiles[_cursor].Special : current;
            ShowDetails(shown, current, loadout, taken, now);
        }

        private void ShowBox(SlotBox box, Special pick)
        {
            SpecialInfo info = SpecialCatalog.Info(pick);
            box.Icon.Show(pick);
            box.Name.text = info == null ? Lang.T("EMPTY", "TRỐNG")
                : info.Kind == SpecialKind.Quirk ? info.Name + " +" + info.Refund : info.Name;
            box.Name.color = info == null ? MutedColour : TitleColour;
        }

        private void ShowDetails(Special special, Special current, int[] loadout, HashSet<Special> taken, float now)
        {
            SpecialInfo info = SpecialCatalog.Info(special);
            _footDisc.color = special == Special.None ? NoneColour : SpecialFx.ColourOf(special);
            SpecialFx.ShowIcon(_footIcon, _footGlyph, special);
            if (special == Special.None) _footGlyph.text = "-";
            _footName.text = info != null ? (info.Kind == SpecialKind.Quirk ? info.Name + "  +" + info.Refund : info.Name) : Lang.T("EMPTY", "TRỐNG");
            _footTag.text = TagFor(special, info);
            _footText.text = info != null ? info.Description : SpecialCatalog.SlotHelp[(int)_slot];

            string status;
            Color colour = CurrentColour;
            if (!_inGrid)
            {
                status = current == Special.None
                    ? Lang.T("EMPTY: SELECT TO CHOOSE ONE", "ĐANG TRỐNG: SELECT ĐỂ CHỌN")
                    : Lang.T("YOUR CURRENT PICK", "LỰA CHỌN HIỆN TẠI CỦA BẠN");
                if (current == Special.None) colour = MutedColour;
            }
            else if (special == current) status = Lang.T("YOUR CURRENT PICK: SELECT AGAIN TO REMOVE IT", "ĐANG CHỌN: SELECT LẦN NỮA ĐỂ BỎ");
            else if (special != Special.None && taken != null && taken.Contains(special))
            {
                status = Lang.T("ANOTHER PLAYER HAS IT: ONE PER KITCHEN", "NGƯỜI KHÁC ĐÃ CHỌN: MỖI BẾP MỘT NGƯỜI");
                colour = BlockedColour;
            }
            else if (special != Special.None && !SpecialCatalog.Fits(loadout, _slot, special))
            {
                Special with = SpecialCatalog.ClashWith(loadout, _slot, special);
                status = with != Special.None ? (Lang.Vi ? "KỴ VỚI " + SpecialCatalog.NameOf(with) + " (LỰA CHỌN KHÁC CỦA BẠN)" : "CAN'T GO WITH " + SpecialCatalog.NameOf(with) + " (YOUR OTHER PICK)") : Lang.T("NOT AVAILABLE HERE", "KHÔNG DÙNG ĐƯỢC Ở ĐÂY");
                colour = BlockedColour;
            }
            else
            {
                status = Lang.T("SELECT TO PICK", "SELECT ĐỂ CHỌN");
                colour = MutedColour;
            }
            float denied = now - _deniedAt;
            if (denied < 0.4f) colour = Color.Lerp(BlockedColour, Color.white, 0.5f * Mathf.Sin(denied / 0.4f * Mathf.PI));
            _footStatus.text = status;
            _footStatus.color = colour;
        }

        private string TagFor(Special special, SpecialInfo info)
        {
            if (info == null) return _slot == SpecialSlot.Active ? Lang.T("NO ACTIVE", "KHÔNG CHỦ ĐỘNG") : _slot == SpecialSlot.Quirk ? Lang.T("NO QUIRK", "KHÔNG TẬT") : Lang.T("NO PASSIVE", "KHÔNG BỊ ĐỘNG");
            int category = SpecialCatalog.CategoryOf(special);
            string tag = category >= 0 ? SpecialCatalog.CategoryNames[category] : "";
            if (info.Kind == SpecialKind.Active)
                tag += info.Cooldown >= 999f ? Lang.T("  -  ONCE PER LEVEL", "  -  MỖI MÀN MỘT LẦN") : (Lang.Vi ? "  -  HỒI CHIÊU " + Mathf.RoundToInt(info.Cooldown) + " S" : "  -  " + Mathf.RoundToInt(info.Cooldown) + " S COOLDOWN");
            if (info.Kind == SpecialKind.Quirk && info.Refund > 0) tag += Lang.Vi ? "  -  THÊM " + info.Refund + " ĐIỂM" : "  -  FREES " + info.Refund + (info.Refund == 1 ? " POINT" : " POINTS");
            if (info.TeamWide) tag += Lang.T("  -  WHOLE TEAM, ONE PER KITCHEN", "  -  CẢ ĐỘI, MỖI BẾP MỘT NGƯỜI");
            if (info.Kind == SpecialKind.Quirk && Solo) tag += Lang.T("  -  SOLO: BOTH CHEFS", "  -  CHƠI MỘT MÌNH: CẢ HAI ĐẦU BẾP");
            return tag;
        }

        // --- building ---------------------------------------------------------------------------

        /// <summary>The focused slot's choices as tiles (the highlight on <paramref name="current"/>).</summary>
        private void BuildTiles(Special current)
        {
            foreach (Tile old in _tiles)
                if (old.Root != null) Object.Destroy(old.Root.gameObject);
            _tiles.Clear();
            _cursor = 0;
            // No "none" tile: SELECT on the current pick empties the slot instead.
            foreach (Special choice in SpecialCatalog.Choices(_slot))
            {
                if (choice == Special.None) continue;
                if (choice == current) _cursor = _tiles.Count;
                _tiles.Add(MakeTile(choice));
            }
            _deniedTile = -1;
            _scroll = _scrollTarget = 0f;
            float width = _viewport.rect.width;
            if (width > 1f)
            {
                _shownWidth = width;
                LayoutTiles(width);
                ScrollToCursor(true);
            }
            else _shownWidth = -1f;
        }

        private SlotBox MakeSlotBox(RectTransform strip, int index, int count)
        {
            var box = new SlotBox();
            box.Root = Child("Slot" + index, strip);
            box.Root.anchorMin = new Vector2((float)index / count, 0f);
            box.Root.anchorMax = new Vector2((float)(index + 1) / count, 1f);
            box.Root.offsetMin = new Vector2(StripBoxGap / 2f, 0f);
            box.Root.offsetMax = new Vector2(-StripBoxGap / 2f, 0f);
            box.Back = Sliced(box.Root, "Back", SpecialFx.RoundBox, 0f);
            box.Frame = Sliced(box.Root, "Frame", SpecialFx.RoundFrame, 3f);
            box.Frame.enabled = false;

            RectTransform label = Child("Slot", box.Root);
            label.anchorMin = new Vector2(0f, 1f);
            label.anchorMax = new Vector2(1f, 1f);
            label.pivot = new Vector2(0.5f, 1f);
            label.offsetMin = new Vector2(4f, -26f);
            label.offsetMax = new Vector2(-4f, -5f);
            // The slot's name in its own colour (gold active, green passive, red quirk).
            box.Label = AddText(label, Header, 17, Color.Lerp(SlotIcon.ColourOf((SpecialSlot)index), Color.white, 0.25f),
                TextAnchor.MiddleCenter, SpecialCatalog.SlotNames[index]);
            Shrink(box.Label, 17);
            AddShadow(box.Label.gameObject);

            // The pick, drawn the same way as on the card. Centred in the room between the name
            // and the pick's name, whatever its size.
            RectTransform holder = Child("Icon", box.Root);
            holder.anchorMin = holder.anchorMax = new Vector2(0.5f, 1f);
            holder.anchoredPosition = new Vector2(0f, -30f - StripIcon / 2f);
            holder.sizeDelta = new Vector2(StripIcon, StripIcon);
            box.Icon = new SlotIcon(holder, (SpecialSlot)index, StripIcon * SlotIcon.SizeOf((SpecialSlot)index), Header);

            RectTransform name = Child("Name", box.Root);
            name.anchorMin = new Vector2(0f, 0f);
            name.anchorMax = new Vector2(1f, 0f);
            name.pivot = new Vector2(0.5f, 0f);
            name.offsetMin = new Vector2(5f, 4f);
            name.offsetMax = new Vector2(-5f, 34f);
            box.Name = AddText(name, Header, 16, TitleColour, TextAnchor.MiddleCenter, "");
            Shrink(box.Name, 16);
            box.Name.lineSpacing = 0.85f;
            AddShadow(box.Name.gameObject);
            return box;
        }

        /// <summary>Wraps a text and lets it shrink to fit its box.</summary>
        private static void Shrink(Text text, int size)
        {
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.resizeTextForBestFit = true;
            text.resizeTextMaxSize = size;
            text.resizeTextMinSize = 10;
        }

        /// <summary>The keys on the panel's bottom rim, for the slots or for the grid.</summary>
        private RectTransform MakeLegend(bool slots)
        {
            RectTransform legend = Child(slots ? "SlotKeys" : "PickKeys", _panel);
            legend.anchorMin = legend.anchorMax = new Vector2(0.5f, 0f);
            legend.anchoredPosition = new Vector2(0f, Pad + LegendHeight / 2f - 4f);
            legend.sizeDelta = new Vector2(900f, 40f);
            HorizontalLayoutGroup row = legend.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleCenter;
            row.spacing = 8f;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            // The directions aren't listed: moving with them is a given.
            AddButton(legend, PlayerInputLookup.LogicalButtonID.UISelectNotStart, "SELECT");
            AddLabel(legend, slots ? Lang.T("CHANGE", "ĐỔI") : Lang.T("PICK / REMOVE", "CHỌN / BỎ"));
            AddGap(legend, 26f);
            AddButton(legend, PlayerInputLookup.LogicalButtonID.UICancel, "BACK");
            AddLabel(legend, slots ? Lang.T("CLOSE", "ĐÓNG") : Lang.T("BACK", "QUAY LẠI"));
            legend.gameObject.SetActive(slots);
            return legend;
        }

        /// <summary>Places the tiles in rows, each category starting a new row under its header.</summary>
        private void LayoutTiles(float width)
        {
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                Transform child = _content.GetChild(i);
                if (child.name == "Section") Object.Destroy(child.gameObject);
            }
            int columns = Mathf.Max(1, Mathf.FloorToInt((width - 8f + TileGap) / (TileWidth + TileGap)));
            float rowWidth = columns * TileWidth + (columns - 1) * TileGap;
            float left = (width - rowWidth) / 2f;
            float y = 4f;
            int row = -1, column = columns;
            int section = -2;
            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile tile = _tiles[i];
                int category = tile.Special == Special.None ? -1 : SpecialCatalog.CategoryOf(tile.Special);
                if (category != section)
                {
                    // A new category: its header, then a fresh row.
                    if (section != -2) y += TileHeight + SectionGap;
                    section = category;
                    MakeSection(category, left, y, rowWidth);
                    y += SectionHeader;
                    row++;
                    column = 0;
                }
                else if (column >= columns)
                {
                    y += TileHeight + TileGap;
                    row++;
                    column = 0;
                }
                tile.Position = new Vector2(left + column * (TileWidth + TileGap) - width / 2f, y);
                tile.Row = row;
                column++;
            }
            _contentHeight = y + TileHeight + 8f;
            _content.sizeDelta = new Vector2(0f, _contentHeight);
        }

        private void MakeSection(int category, float left, float y, float width)
        {
            RectTransform header = Child("Section", _content);
            header.anchorMin = header.anchorMax = new Vector2(0f, 1f);
            header.pivot = new Vector2(0f, 1f);
            header.anchoredPosition = new Vector2(left, -y);
            header.sizeDelta = new Vector2(width, SectionHeader - 4f);
            Color colour = category >= 0 ? SpecialFx.ColourOf(FirstOf(category)) : NoneColour;
            RectTransform bar = Child("Bar", header);
            bar.anchorMin = bar.anchorMax = new Vector2(0f, 0.5f);
            bar.pivot = new Vector2(0f, 0.5f);
            bar.sizeDelta = new Vector2(8f, 22f);
            Image barImage = bar.gameObject.AddComponent<Image>();
            barImage.raycastTarget = false;
            barImage.sprite = SpecialFx.RoundBox;
            barImage.type = Image.Type.Sliced;
            barImage.color = colour;
            RectTransform textRect = Child("Text", header);
            Stretch(textRect);
            textRect.offsetMin = new Vector2(18f, 0f);
            Text text = AddText(textRect, Header, 24, Color.Lerp(colour, Color.white, 0.45f), TextAnchor.MiddleLeft,
                category >= 0 ? SpecialCatalog.CategoryNames[category] : Lang.T("NO SPECIAL", "KHÔNG CÓ KỸ NĂNG"));
            AddShadow(text.gameObject);
            RectTransform line = Child("Line", header);
            line.anchorMin = new Vector2(0f, 0f);
            line.anchorMax = new Vector2(1f, 0f);
            line.pivot = new Vector2(0.5f, 0f);
            line.sizeDelta = new Vector2(0f, 2f);
            Image lineImage = line.gameObject.AddComponent<Image>();
            lineImage.raycastTarget = false;
            lineImage.color = new Color(colour.r, colour.g, colour.b, 0.35f);
        }

        private static Special FirstOf(int category)
        {
            for (int n = 1; n <= 112; n++)
                if (SpecialCatalog.CategoryOf((Special)n) == category) return (Special)n;
            return Special.None;
        }

        private Tile MakeTile(Special special)
        {
            var tile = new Tile { Special = special };
            tile.Root = Child("Tile", _content);
            tile.Root.anchorMin = tile.Root.anchorMax = new Vector2(0.5f, 1f);
            tile.Root.sizeDelta = new Vector2(TileWidth, TileHeight);
            tile.Group = tile.Root.gameObject.AddComponent<CanvasGroup>();
            tile.Group.interactable = false;
            tile.Group.blocksRaycasts = false;

            tile.Back = Sliced(tile.Root, "Back", SpecialFx.RoundBox, 0f);
            tile.Frame = Sliced(tile.Root, "Frame", SpecialFx.RoundFrame, 3f);
            tile.Frame.enabled = false;

            RectTransform disc = Child("Disc", tile.Root);
            disc.anchorMin = disc.anchorMax = new Vector2(0.5f, 1f);
            disc.anchoredPosition = new Vector2(0f, -8f - DiscSize / 2f);
            disc.sizeDelta = new Vector2(DiscSize, DiscSize);
            tile.Disc = disc.gameObject.AddComponent<Image>();
            tile.Disc.raycastTarget = false;
            tile.Disc.sprite = SpecialFx.Disc;
            tile.Disc.color = special == Special.None ? NoneColour : SpecialFx.ColourOf(special);
            RectTransform picture = Child("Picture", disc);
            picture.sizeDelta = new Vector2(DiscSize * 0.9f, DiscSize * 0.9f);
            tile.Picture = picture.gameObject.AddComponent<Image>();
            tile.Picture.raycastTarget = false;
            tile.Picture.preserveAspect = true;
            RectTransform glyph = Child("Glyph", disc);
            glyph.sizeDelta = new Vector2(DiscSize, DiscSize);
            tile.Glyph = AddText(glyph, Header, 26, Color.white, TextAnchor.MiddleCenter, "");
            AddShadow(tile.Glyph.gameObject);
            SpecialFx.ShowIcon(tile.Picture, tile.Glyph, special);
            if (special == Special.None) tile.Glyph.text = "-";

            RectTransform name = Child("Name", tile.Root);
            name.anchorMin = name.anchorMax = new Vector2(0.5f, 0f);
            name.pivot = new Vector2(0.5f, 0f);
            name.anchoredPosition = new Vector2(0f, 5f);
            name.sizeDelta = new Vector2(TileWidth - 6f, 36f);
            SpecialInfo info = SpecialCatalog.Info(special);
            tile.Name = AddText(name, Header, 15, TitleColour, TextAnchor.MiddleCenter, info != null ? info.Name : Lang.T("NONE", "KHÔNG CÓ"));
            tile.Name.horizontalOverflow = HorizontalWrapMode.Wrap;
            tile.Name.resizeTextForBestFit = true;
            tile.Name.resizeTextMaxSize = 15;
            tile.Name.resizeTextMinSize = 10;
            tile.Name.lineSpacing = 0.85f;
            AddShadow(tile.Name.gameObject);

            // CURRENT / TAKEN / CLASH, on the tile's top edge.
            RectTransform badge = Child("Badge", tile.Root);
            badge.anchorMin = badge.anchorMax = new Vector2(0.5f, 1f);
            badge.anchoredPosition = new Vector2(0f, -2f);
            badge.sizeDelta = new Vector2(TileWidth - 14f, 20f);
            tile.BadgeBack = badge.gameObject.AddComponent<Image>();
            tile.BadgeBack.raycastTarget = false;
            tile.BadgeBack.sprite = SpecialFx.RoundBox;
            tile.BadgeBack.type = Image.Type.Sliced;
            RectTransform badgeText = Child("Text", badge);
            Stretch(badgeText);
            tile.Badge = AddText(badgeText, Header, 13, new Color(0.08f, 0.08f, 0.1f, 1f), TextAnchor.MiddleCenter, "");
            badge.gameObject.SetActive(false);
            return tile;
        }

        // --- small helpers ------------------------------------------------------------------------

        private Font Header
        {
            get { return _art != null ? _art.Header : SpecialFx.Font; }
        }

        private Font Body
        {
            get { return _art != null ? _art.Body : SpecialFx.BodyFont; }
        }

        private Text FootText(RectTransform foot, string name, int size, Color colour, float top, float height, Font font)
        {
            RectTransform rect = Child(name, foot);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(160f, -top - height);
            rect.offsetMax = new Vector2(-20f, -top);
            Text text = AddText(rect, font, size, colour, TextAnchor.UpperLeft, "");
            AddShadow(text.gameObject);
            return text;
        }

        private void AddButton(RectTransform row, PlayerInputLookup.LogicalButtonID id, string fallback)
        {
            Sprite icon = GameArt.ButtonIcon(id, Player);
            if (icon == null)
            {
                AddLabel(row, "[" + fallback + "]");
                return;
            }
            RectTransform rect = Child("Icon", row);
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = icon;
            image.preserveAspect = true;
            LayoutElement layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = 36f;
            layout.preferredHeight = 36f;
        }

        private void AddLabel(RectTransform row, string value)
        {
            RectTransform rect = Child("Label", row);
            Text text = AddText(rect, Header, 24, TitleColour, TextAnchor.MiddleCenter, value);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            AddShadow(text.gameObject);
        }

        private static void AddGap(RectTransform row, float width)
        {
            LayoutElement layout = Child("Gap", row).gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = width;
            layout.preferredHeight = 1f;
        }

        private static Image Sliced(RectTransform parent, string name, Sprite sprite, float grow)
        {
            RectTransform rect = Child(name, parent);
            Stretch(rect);
            rect.offsetMin = new Vector2(-grow, -grow);
            rect.offsetMax = new Vector2(grow, grow);
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            return image;
        }

        private static RectTransform Child(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Text AddText(RectTransform rect, Font font, int size, Color colour, TextAnchor alignment, string value)
        {
            Text text = rect.gameObject.AddComponent<Text>();
            text.raycastTarget = false;
            text.supportRichText = false;
            text.font = font;
            text.fontSize = size;
            text.color = colour;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = value;
            return text;
        }

        private static void AddShadow(GameObject target)
        {
            Shadow shadow = target.AddComponent<Shadow>();
            shadow.effectColor = ShadowColour;
            shadow.effectDistance = new Vector2(2f, -2f);
        }

        private static Color WithAlpha(Color colour, float alpha)
        {
            colour.a = alpha;
            return colour;
        }

        /// <summary>A quick grow-and-settle, <paramref name="amount"/> bigger at the peak.</summary>
        private static Vector3 Punch(float age, float amount)
        {
            const float seconds = 0.18f;
            if (age < 0f || age >= seconds) return Vector3.one;
            float s = 1f + amount * Mathf.Sin(Mathf.PI * age / seconds);
            return new Vector3(s, s, 1f);
        }

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }
    }
}
