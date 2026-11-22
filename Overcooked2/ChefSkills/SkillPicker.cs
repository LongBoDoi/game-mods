using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// The special picker: SELECT on a card's ACTIVE / PASSIVE / QUIRK row opens it. Every choice
    /// for that slot is a tile (icon and name) in a grid, grouped by category under headers; a
    /// strip at the bottom tells what the highlighted one does and whether it can be picked (one
    /// per kitchen and taken, or clashing with another pick). Directions move, SELECT picks, BACK
    /// closes; the mouse can point, click and scroll.
    ///
    /// One per local player; several open at once share the screen side by side. Built on the skill
    /// screen's canvas with the game's dialog art, like the rest of the screen.
    /// </summary>
    internal sealed class SkillPicker
    {
        // Layout (1920x1080 reference units).
        private const float MaxWidth = 1240f;
        private const float ScreenWidth = 1840f;
        private const float PanelGap = 20f;
        private const float PanelHeight = 900f;
        private const float PanelY = 10f;
        private const float Pad = 46f;
        private const float HeaderHeight = 70f;
        private const float FooterHeight = 170f;
        private const float TileWidth = 96f;
        private const float TileHeight = 118f;
        private const float TileGap = 10f;
        private const float DiscSize = 66f;
        private const float SectionHeader = 34f;
        private const float SectionGap = 14f;

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

        internal readonly PlayerInputLookup.Player Player;
        internal readonly SpecialSlot Slot;
        private readonly GameArt _art;
        private readonly RectTransform _panel, _viewport, _content;
        private readonly Sprite _panelSprite;
        private readonly List<Tile> _tiles = new List<Tile>();
        private readonly Image _footIcon, _footDisc;
        private readonly Text _footGlyph, _footName, _footTag, _footText, _footStatus;
        private readonly float _openedAt;
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

        /// <summary>A player's colour index (for the panel's name tag), and how many players are on this machine.</summary>
        private readonly string _owner;

        private SkillPicker(Transform canvas, GameArt art, PlayerInputLookup.Player player, string owner, SpecialSlot slot, Special current)
        {
            Player = player;
            Slot = slot;
            _art = art;
            _owner = owner;
            _openedAt = Time.unscaledTime;
            _lastMouse = Input.mousePosition;
            _mouseAnchor = _lastMouse;
            _pad = new SpecialInput.PadNav(player);

            if (s_dim == null)
            {
                RectTransform dim = Child("PickerDim", canvas);
                Stretch(dim);
                Image dimImage = dim.gameObject.AddComponent<Image>();
                dimImage.color = new Color(0.03f, 0.05f, 0.08f, 0.55f);
                s_dim = dim.gameObject;
            }
            s_dim.transform.SetAsLastSibling();

            _panel = Child("Picker" + (int)player, canvas);
            _panel.sizeDelta = new Vector2(MaxWidth, PanelHeight);
            _panelSprite = art != null ? art.Sliced(GameArt.CardSprites[(int)slot % GameArt.CardSprites.Length],
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
            string what = slot == SpecialSlot.Active ? Lang.T("CHOOSE YOUR ACTIVE", "CHỌN KỸ NĂNG CHỦ ĐỘNG")
                : slot == SpecialSlot.Quirk ? Lang.T("CHOOSE A QUIRK", "CHỌN MỘT TẬT") : Lang.T("CHOOSE A PASSIVE", "CHỌN KỸ NĂNG BỊ ĐỘNG");
            Text titleText = AddText(title, Header, 44, TitleColour, TextAnchor.MiddleLeft, what);
            AddShadow(titleText.gameObject);
            if (!string.IsNullOrEmpty(owner))
            {
                // Its own object: a GameObject can hold only one Text.
                RectTransform ownerRect = Child("Owner", title);
                Stretch(ownerRect);
                Text ownerText = AddText(ownerRect, Header, 30, MutedColour, TextAnchor.MiddleRight, owner);
                AddShadow(ownerText.gameObject);
            }

            // The grid, scrolled inside a masked viewport.
            _viewport = Child("Grid", _panel);
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = new Vector2(Pad - 6f, Pad + FooterHeight);
            _viewport.offsetMax = new Vector2(-Pad + 6f, -Pad - HeaderHeight);
            _viewport.gameObject.AddComponent<RectMask2D>();
            _content = Child("Content", _viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.offsetMin = Vector2.zero;
            _content.offsetMax = Vector2.zero;

            List<Special> choices = SpecialCatalog.Choices(slot);
            for (int i = 0; i < choices.Count; i++)
            {
                _tiles.Add(MakeTile(choices[i]));
                if (choices[i] == current) _cursor = i;
            }

            // Footer: the highlighted choice.
            RectTransform foot = Child("Details", _panel);
            foot.anchorMin = new Vector2(0f, 0f);
            foot.anchorMax = new Vector2(1f, 0f);
            foot.pivot = new Vector2(0.5f, 0f);
            foot.offsetMin = new Vector2(Pad, Pad);
            foot.offsetMax = new Vector2(-Pad, Pad + FooterHeight - 14f);
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

            // The keys, on the panel's bottom rim.
            RectTransform legend = Child("Legend", _panel);
            legend.anchorMin = legend.anchorMax = new Vector2(0.5f, 0f);
            legend.anchoredPosition = new Vector2(0f, Pad * 0.5f - 6f);
            legend.sizeDelta = new Vector2(900f, 40f);
            HorizontalLayoutGroup row = legend.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleCenter;
            row.spacing = 8f;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            AddButton(legend, PlayerInputLookup.LogicalButtonID.UISelectNotStart, "SELECT");
            AddLabel(legend, Lang.T("PICK", "CHỌN"));
            AddGap(legend, 26f);
            AddButton(legend, PlayerInputLookup.LogicalButtonID.UICancel, "BACK");
            AddLabel(legend, Lang.T("CLOSE", "ĐÓNG"));

            s_open.Add(this);
            s_open.Sort(delegate(SkillPicker a, SkillPicker b) { return ((int)a.Player).CompareTo((int)b.Player); });
            LayoutAll();
            ScrollToCursor(true);
        }

        /// <summary>Opens a picker for this player's slot, on the skill screen's canvas.</summary>
        internal static SkillPicker Open(Transform canvas, GameArt art, PlayerInputLookup.Player player, string owner,
            SpecialSlot slot, Special current, bool byMouse)
        {
            var picker = new SkillPicker(canvas, art, player, owner, slot, current);
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

        /// <summary>Several pickers share the screen's width side by side, in player order.</summary>
        private static void LayoutAll()
        {
            int n = s_open.Count;
            if (n == 0) return;
            float width = Mathf.Min(MaxWidth, (ScreenWidth - (n - 1) * PanelGap) / n);
            float x = -(n * width + (n - 1) * PanelGap) / 2f + width / 2f;
            foreach (SkillPicker picker in s_open)
            {
                if (picker._panel == null) continue;
                picker._panel.anchorMin = picker._panel.anchorMax = new Vector2(0.5f, 0.5f);
                picker._panel.sizeDelta = new Vector2(width, PanelHeight);
                picker._panel.anchoredPosition = new Vector2(x, PanelY);
                picker._panel.SetAsLastSibling();
                x += width + PanelGap;
            }
        }

        // --- every frame ------------------------------------------------------------------------

        /// <summary>What a frame of input did.</summary>
        internal enum Outcome { None, Moved, Picked, Refused, Closed }

        /// <summary>
        /// Reads this player's buttons (and the mouse). <paramref name="picked"/> is set when the
        /// outcome is Picked. <paramref name="reason"/>: why a pick was refused.
        /// </summary>
        internal Outcome HandleInput(ILogicalButton up, ILogicalButton down, ILogicalButton left, ILogicalButton right,
            ILogicalButton select, ILogicalButton cancel, int[] loadout, HashSet<Special> taken, out Special picked)
        {
            picked = Special.None;
            if (_panel == null) return Outcome.Closed;
            // The player's own pad, read directly as well: the menu buttons can be used up by
            // something else first, and this keeps a controller working whatever happens to them.
            bool[] pad = _pad.Poll();
            // The press that opened it.
            if (Time.unscaledTime - _openedAt < 0.05f) return Outcome.None;

            var game = new[] { up.JustPressed(), down.JustPressed(), left.JustPressed(), right.JustPressed(), select.JustPressed(), cancel.JustPressed() };
            for (int i = 0; i < 6; i++)
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
            if (back) return Outcome.Closed;

            int before = _cursor;
            if (goLeft) MoveFlat(-1);
            if (goRight) MoveFlat(1);
            if (goUp) MoveRow(-1);
            if (goDown) MoveRow(1);

            // The mouse: once it has moved (or it opened the picker), pointing moves the highlight,
            // a click picks, and a right click or a click outside closes. Keys and buttons hand
            // control back until it moves again. The wheel always scrolls.
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
                if (under >= 0 && mouseMoved) _cursor = under;
                if (Input.GetMouseButtonDown(0))
                {
                    if (under >= 0)
                    {
                        _cursor = under;
                        choose = true;
                    }
                    else if (!inPanel && s_open.Count == 1) return Outcome.Closed;
                }
                if (Input.GetMouseButtonDown(1) && inPanel) return Outcome.Closed;
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f && inPanel) _scrollTarget = Mathf.Clamp(_scrollTarget - wheel * 90f, 0f, MaxScroll());

            if (_cursor != before) ScrollToCursor(false);

            if (choose)
            {
                Special candidate = _tiles[_cursor].Special;
                if (!Allowed(candidate, loadout, taken))
                {
                    _deniedAt = Time.unscaledTime;
                    _deniedTile = _cursor;
                    return Outcome.Refused;
                }
                picked = candidate;
                return Outcome.Picked;
            }
            return _cursor != before ? Outcome.Moved : Outcome.None;
        }

        private bool Allowed(Special candidate, int[] loadout, HashSet<Special> taken)
        {
            if (candidate == Special.None) return true;
            if (taken != null && taken.Contains(candidate)) return false;
            return SpecialCatalog.Fits(loadout, Slot, candidate);
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

        private float MaxScroll()
        {
            return Mathf.Max(0f, _contentHeight - _viewport.rect.height);
        }

        private void ScrollToCursor(bool instant)
        {
            if (_tiles.Count == 0 || _viewport == null) return;
            float view = _viewport.rect.height;
            if (view <= 1f) view = PanelHeight - 2f * Pad - HeaderHeight - FooterHeight;
            Tile tile = _tiles[_cursor];
            float top = tile.Position.y - SectionHeader - 8f;
            float bottom = tile.Position.y + TileHeight + 8f;
            if (top < _scrollTarget) _scrollTarget = top;
            if (bottom > _scrollTarget + view) _scrollTarget = bottom - view;
            _scrollTarget = Mathf.Clamp(_scrollTarget, 0f, MaxScroll());
            if (instant) _scroll = _scrollTarget;
        }

        /// <summary>Draws a frame: tiles' states, the highlight, the details strip.</summary>
        internal void Render(int[] loadout, HashSet<Special> taken)
        {
            if (_panel == null) return;
            float now = Time.unscaledTime;
            float width = _viewport.rect.width;
            if (width > 1f && Mathf.Abs(width - _shownWidth) > 0.5f)
            {
                _shownWidth = width;
                LayoutTiles(width);
                ScrollToCursor(true);
            }

            // Opens with a little pop, like the game's dialogs.
            float open = Mathf.Clamp01((now - _openedAt) / 0.22f);
            float scale = Mathf.LerpUnclamped(0.9f, 1f, EaseOutBack(open));
            _panel.localScale = new Vector3(scale, scale, 1f);

            _scroll = Mathf.Lerp(_scroll, _scrollTarget, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 16f));
            _content.anchoredPosition = new Vector2(0f, _scroll);

            Special current = loadout != null && (int)Slot < loadout.Length ? (Special)loadout[(int)Slot] : Special.None;
            float pulse = 0.5f + 0.5f * Mathf.Sin(now * 5f);
            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile tile = _tiles[i];
                bool cursor = i == _cursor;
                bool mine = tile.Special == current;
                bool allowed = mine || Allowed(tile.Special, loadout, taken);
                bool isTaken = taken != null && taken.Contains(tile.Special);

                tile.Group.alpha = allowed ? 1f : 0.42f;
                tile.Back.color = cursor ? TileCursorColour : TileColour;
                tile.Frame.enabled = cursor || mine;
                tile.Frame.color = cursor ? Color.Lerp(CursorFrame, Color.white, 0.35f * pulse) : CurrentColour;

                float shake = 0f;
                float denied = now - _deniedAt;
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
            ShowDetails(_tiles[_cursor].Special, current, loadout, taken, now);
        }

        private void ShowDetails(Special special, Special current, int[] loadout, HashSet<Special> taken, float now)
        {
            SpecialInfo info = SpecialCatalog.Info(special);
            _footDisc.color = special == Special.None ? NoneColour : SpecialFx.ColourOf(special);
            SpecialFx.ShowIcon(_footIcon, _footGlyph, special);
            if (special == Special.None) _footGlyph.text = "-";
            _footName.text = info != null ? (info.Kind == SpecialKind.Quirk ? info.Name + "  +" + info.Refund : info.Name) : Lang.T("NONE", "KHÔNG CÓ");
            _footTag.text = TagFor(special, info);
            _footText.text = info != null ? info.Description : SpecialCatalog.SlotHelp[(int)Slot] + Lang.T(". Pick this for none", ". Chọn ô này để không lấy gì");

            string status;
            Color colour = CurrentColour;
            if (special == current) status = Lang.T("YOUR CURRENT PICK", "LỰA CHỌN HIỆN TẠI CỦA BẠN");
            else if (special != Special.None && taken != null && taken.Contains(special))
            {
                status = Lang.T("ANOTHER PLAYER HAS IT: ONE PER KITCHEN", "NGƯỜI KHÁC ĐÃ CHỌN: MỖI BẾP MỘT NGƯỜI");
                colour = BlockedColour;
            }
            else if (special != Special.None && !SpecialCatalog.Fits(loadout, Slot, special))
            {
                Special with = SpecialCatalog.ClashWith(loadout, Slot, special);
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
            if (info == null) return Slot == SpecialSlot.Active ? Lang.T("NO ACTIVE", "KHÔNG CHỦ ĐỘNG") : Slot == SpecialSlot.Quirk ? Lang.T("NO QUIRK", "KHÔNG TẬT") : Lang.T("NO PASSIVE", "KHÔNG BỊ ĐỘNG");
            int category = SpecialCatalog.CategoryOf(special);
            string tag = category >= 0 ? SpecialCatalog.CategoryNames[category] : "";
            if (info.Kind == SpecialKind.Active)
                tag += info.Cooldown >= 999f ? Lang.T("  -  ONCE PER LEVEL", "  -  MỖI MÀN MỘT LẦN") : (Lang.Vi ? "  -  HỒI CHIÊU " + Mathf.RoundToInt(info.Cooldown) + " S" : "  -  " + Mathf.RoundToInt(info.Cooldown) + " S COOLDOWN");
            if (info.Kind == SpecialKind.Quirk && info.Refund > 0) tag += Lang.Vi ? "  -  THÊM " + info.Refund + " ĐIỂM" : "  -  FREES " + info.Refund + (info.Refund == 1 ? " POINT" : " POINTS");
            if (info.TeamWide) tag += Lang.T("  -  WHOLE TEAM, ONE PER KITCHEN", "  -  CẢ ĐỘI, MỖI BẾP MỘT NGƯỜI");
            return tag;
        }

        // --- building ---------------------------------------------------------------------------

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

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }
    }
}
