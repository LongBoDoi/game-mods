using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// The skill-select screen: the game's dimmed dialog overlay and large dialog panel with its
    /// blue header plate, one taped card per player in the game (chef hat in the player's colour,
    /// every skill as a row of level pips, red to the left and green to the right, with what the
    /// level changes, the points still to spend and a READY! badge), a ready-count tag and the
    /// game's control legend bar with real button icons. Local players' cards highlight the skill
    /// being changed, with the player's coloured chef-select arrows; other machines' cards show
    /// those players' builds as they change them. Built on its own overlay canvas, so none of the
    /// game's menu logic is copied or disturbed.
    /// </summary>
    internal sealed class SkillSelectView
    {
        /// <summary>What one card shows.</summary>
        internal struct CardInfo
        {
            public string Key;
            public string Title;
            public bool IsLocal;
            public PlayerInputLookup.Player Player;
            public ChefColourData Colour;
            public int[] Levels;
            /// <summary>The highlighted skill (local cards), or -1.</summary>
            public int Selected;
            public int FreePoints;
            /// <summary>When this player last tried something the rules don't allow.</summary>
            public float DeniedAt;
            public SkillSync.CardState State;
            /// <summary>Special-skill loadout (see <see cref="SpecialSlot"/>); null = none.</summary>
            public int[] Specials;
            /// <summary>Why the last lock-in was refused, shown in place of the description for a moment.</summary>
            public string DeniedReason;
            /// <summary>Local cards: the key or button that uses the active special.</summary>
            public string KeyName;
            /// <summary>Local cards: the key or button that opens the skill details panel in the kitchen.</summary>
            public string DetailsKeyName;
            /// <summary>Local cards: waiting for the player to press their new skill key.</summary>
            public bool Capturing;
            /// <summary>The key being set is the details key.</summary>
            public bool CapturingDetails;
            /// <summary>A short message (the new key, or why it wasn't taken), shown in place of the description for a moment.</summary>
            public string Notice;
            public float NoticeAt;
        }

        /// <summary>One special-skill pick on a card: the slot's name and what is picked.</summary>
        private sealed class SpecialRow
        {
            public RectTransform Root;
            public Image Highlight;
            public Text Value;
            public Image Disc, Picture;
            public Text Glyph;
            public int Shown = -1;
            public float PunchAt = -10f;
        }

        /// <summary>Rows on a card: the nine skills, then the special picks when specials are on.</summary>
        internal static int RowCount
        {
            get { return SkillStats.Count + (SpecialCatalog.Enabled ? SpecialCatalog.SlotCount + 2 : 0); }
        }

        /// <summary>The last row, under the specials: the key that uses the active (-1 with specials off).</summary>
        internal static int KeyRow
        {
            get { return SpecialCatalog.Enabled ? SkillStats.Count + SpecialCatalog.SlotCount : -1; }
        }

        /// <summary>Under it: the key that opens the skill details panel (-1 with specials off).</summary>
        internal static int DetailsRow
        {
            get { return SpecialCatalog.Enabled ? SkillStats.Count + SpecialCatalog.SlotCount + 1 : -1; }
        }

        private static bool IsKeyRow(int index)
        {
            return index >= 0 && (index == KeyRow || index == DetailsRow);
        }

        private static float RowY(int index)
        {
            return RowTop - RowStep * index - (index >= SkillStats.Count ? SpecialGap : 0f)
                - (index >= SkillStats.Count + SpecialCatalog.SlotCount ? KeyGap : 0f);
        }

        private sealed class Row
        {
            public RectTransform Root;
            public Image Highlight;
            public Text Percent;
            public Image[] Minus;
            public Image[] Plus;
            public int Shown = int.MinValue;
            public float PunchAt = -10f;
        }

        private sealed class Card
        {
            public string Key;
            public bool IsLocal;
            public RectTransform Root;
            public Image Panel;
            public Color PanelColour;
            public Color PanelReadyColour;
            public Color PanelIdleColour;
            public Color Tint;
            public CanvasGroup RowsGroup;
            public Row[] Rows;
            public SpecialRow[] Specials;
            public SpecialRow KeyRowView, DetailsRowView;
            public Image Left;
            public Image Right;
            public Text Points;
            public Text Description;
            public GameObject Badge;
            public GameObject Hint;
            public CanvasGroup HintGroup;
            public Text HintText;
            public bool Initialised;
            public int ShownSelected = -2;
            public int ShownFree = -1;
            public SkillSync.CardState ShownState;
            public float ReadyPunchAt = -10f;
        }

        // Layout, in 1920x1080 reference units.
        private const float CardWidth = 380f;
        private const float CardHeight = 760f;
        private const float CardGap = 40f;
        // The game draws its large dialog art inset from the image's box: about 7.9% of the height
        // is empty above the panel's top rim and 8.6% below its bottom rim (measured in game), and
        // nothing at the sides. Everything vertical is placed from those visible edges. The box is
        // a little taller than the screen so the visible panel runs from just under the top of
        // the screen down to the control bar.
        private const float DialogY = -7.5f;
        private const float DialogHeight = 1081f;
        private const float TopInset = 0.079f;
        private const float BottomInset = 0.086f;
        // The control bar, above the box's (empty) bottom edge so it sits at the foot of the screen.
        private const float LegendY = 44f;
        private const float PlateHeight = 168f;
        private const float TagWidth = 330f;
        private const float TagGap = 30f;
        private const float TitleSize = 70f;
        // Panel showing either side of the row of cards (or of the header), and the widest the
        // dialog can get.
        private const float SideMargin = 150f;
        private const float HeaderMargin = 60f;
        private const float MinDialogWidth = 1200f;
        private const float MaxDialogWidth = 1900f;
        // The title's text box is 150 narrower than the plate (its slanted ends), plus some air.
        private const float PlateExtra = 170f;

        // Inside a card, relative to its centre.
        private const float HatY = 318f;
        private const float NameY = 254f;
        private const float PointsY = 216f;
        private const float RowTop = 178f;
        private const float RowStep = 28f;
        // Space between the nine skills and the special-skill rows under them.
        private const float SpecialGap = 10f;
        // A special row: its pick fills the pips and effect columns, between the arrows.
        private const float SpecialValueX = 62f;
        private const float SpecialValueWidth = 140f;
        // Space between the special picks and the skill-key row.
        private const float KeyGap = 4f;
        private const float DescriptionY = -260f;
        private const float DescriptionHeight = 46f;
        private const float HintY = -318f;
        // Rows fill the card art's dark interior (304 wide, measured in game) evenly, 8 in from
        // each side: name -144..-34, arrow, pips -12..84, arrow, effect 104..144.
        private const float RowWidth = 300f;
        private const float NameX = -89f;
        private const float NameWidth = 110f;
        private const float PipsX = 36f;
        private const float PipsWidth = 96f;
        private const float PipGap = 3f;
        private const float PipHeight = 16f;
        private const float ArrowLeftX = -23f;
        private const float ArrowRightX = 95f;
        private const float PercentX = 124f;
        private const float PercentWidth = 40f;

        // What the points line shows for a card whose build isn't known yet (loading, no mod).
        private const int UnknownPoints = int.MinValue;

        private const float OpenSeconds = 0.3f;
        private const float PunchSeconds = 0.18f;
        private const float DeniedSeconds = 0.35f;
        private const float DimAlpha = 0.82f;

        private static readonly Color DimColour = new Color(0.06f, 0.09f, 0.1f, DimAlpha);
        private static readonly Color TitleColour = new Color(0.95f, 0.92f, 0.93f, 1f);
        private static readonly Color BuffColour = new Color(0.62f, 1f, 0.5f, 1f);
        private static readonly Color NerfColour = new Color(1f, 0.58f, 0.48f, 1f);
        private static readonly Color NeutralColour = new Color(0.8f, 0.86f, 0.93f, 1f);
        private static readonly Color PointsColour = new Color(1f, 0.86f, 0.38f, 1f);
        private static readonly Color ReadyBadgeColour = new Color(0.55f, 1f, 0.5f, 1f);
        private static readonly Color ShadowColour = new Color(0.08f, 0.14f, 0.22f, 0.65f);
        private static readonly Color EmptyPipColour = new Color(0.05f, 0.08f, 0.14f, 0.45f);

        private GameArt _art;
        private GameObject _root;
        private Image _dim;
        private RectTransform _dialog;
        private RectTransform _plate;
        private RectTransform _title;
        private Text _titleText;
        private float _titleWidth;
        private Text _tag;
        private RectTransform _tagBox;
        private bool _tagShown = true;
        private float _plateWidth;
        private float _openedAt;
        private string _signature;
        private GameObject _skipTrack;
        private GameObject _peek;
        private bool _hidden;
        private RectTransform _skipFill;
        private readonly Sprite[] _cardSprites = new Sprite[2];
        private readonly List<Card> _cards = new List<Card>();

        internal bool IsAlive
        {
            get { return _root != null; }
        }

        /// <summary>The screen's canvas: the special picker is built on it, over the cards.</summary>
        internal Transform Overlay
        {
            get { return _root != null ? _root.transform : null; }
        }

        internal GameArt Art
        {
            get { return _art; }
        }

        /// <summary>The ACTIVE / PASSIVE / QUIRK row of one of this machine's cards under the mouse (its card's key and row number).</summary>
        internal bool SpecialRowAt(Vector3 mouse, out string cardKey, out int row)
        {
            cardKey = null;
            row = -1;
            if (!IsAlive || _hidden) return false;
            foreach (Card card in _cards)
            {
                if (!card.IsLocal || card.Specials == null) continue;
                for (int i = 0; i < card.Specials.Length; i++)
                {
                    if (card.Specials[i].Root == null || !RectTransformUtility.RectangleContainsScreenPoint(card.Specials[i].Root, mouse, null))
                        continue;
                    cardKey = card.Key;
                    row = SkillStats.Count + i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Builds the frame; the legend shows <paramref name="legendPlayer"/>'s buttons.</summary>
        internal static SkillSelectView Build(GameArt art, PlayerInputLookup.Player legendPlayer, string skipLabel)
        {
            var view = new SkillSelectView();
            view._art = art;
            view.BuildCanvas();
            view.BuildFrame();
            view.BuildLegend(legendPlayer, skipLabel);
            view._openedAt = Time.unscaledTime;
            return view;
        }

        private void BuildCanvas()
        {
            _root = new GameObject("ChefSkills_SkillSelect", typeof(RectTransform));
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) _root.layer = uiLayer;
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            // Fit the 1920x1080 layout inside the screen whatever its shape.
            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
            scaler.matchWidthOrHeight = aspect >= 16f / 9f ? 1f : 0f;

            RectTransform dim = Stretch(Child("Dim", _root.transform));
            _dim = AddImage(dim, null, DimColour);
        }

        private void BuildFrame()
        {
            _dialog = Child("Dialog", _root.transform, new Vector2(0f, DialogY), new Vector2(MinDialogWidth, DialogHeight));
            AddImage(_dialog, _art.Get(GameArt.DialogSprite), new Color(0.62f, 0.8f, 0.92f, 1f));

            // Header plate straddling the top-left edge, as on the game's own dialogs.
            _plate = Child("Header", _dialog, Vector2.zero, new Vector2(840f, 168f));
            AddImage(_plate, _art.Get(GameArt.HeaderSprite), new Color(0.27f, 0.72f, 0.86f, 1f));
            _title = Child("Title", _plate, new Vector2(40f, 11f), new Vector2(690f, 113f));
            // Always one line: the dialog is made wide enough for the title (see Layout), and the
            // text is only made smaller if even the widest dialog can't fit it.
            _titleText = AddText(_title, _art.Header, (int)TitleSize, TitleColour, TextAnchor.MiddleCenter,
                Lang.T("CHOOSE YOUR SKILLS", "CHỌN KỸ NĂNG"), false);
            _titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _titleText.verticalOverflow = VerticalWrapMode.Overflow;
            _titleWidth = _titleText.preferredWidth;
            if (_titleWidth <= 0f) _titleWidth = 800f;

            // Ready-count tag on the top-right edge.
            _tagBox = Child("Status", _dialog, Vector2.zero, new Vector2(TagWidth, 86f));
            AddImage(_tagBox, _art.Get(GameArt.BoxSprite), new Color(0.27f, 0.72f, 0.86f, 1f));
            RectTransform tagText = Child("Text", _tagBox, new Vector2(-4f, 6f), new Vector2(TagWidth - 44f, 60f));
            _tag = AddText(tagText, _art.Header, 36, TitleColour, TextAnchor.MiddleCenter, "", true);
            AddShadow(_tag.gameObject);

            // The taped panels from the controls screen, sliced so their corners keep their shape.
            for (int i = 0; i < _cardSprites.Length; i++)
                _cardSprites[i] = _art.Sliced(GameArt.CardSprites[i], new Vector4(120f, 95f, 120f, 95f), 135f);

            Layout(1);
        }

        /// <summary>Sizes the dialog for <paramref name="count"/> cards and places them in a row.</summary>
        private void Layout(int count)
        {
            count = Mathf.Max(1, count);
            float cardsWidth = count * CardWidth + (count - 1) * CardGap;
            // Wide enough for the cards with panel either side, and for the title plate on one
            // line next to the ready tag.
            float plateNeeded = _titleWidth + PlateExtra;
            float headerNeeded = plateNeeded + TagGap + TagWidth;
            float width = Mathf.Max(MinDialogWidth, cardsWidth + 2f * SideMargin, headerNeeded + 2f * HeaderMargin);
            width = Mathf.Min(width, MaxDialogWidth);
            _dialog.sizeDelta = new Vector2(width, DialogHeight);

            _plateWidth = Mathf.Min(plateNeeded, width - 2f * HeaderMargin - TagGap - TagWidth);
            _plate.sizeDelta = new Vector2(_plateWidth, PlateHeight);
            _title.sizeDelta = new Vector2(_plateWidth - 150f, 113f);
            float room = _plateWidth - 150f;
            _titleText.fontSize = room >= _titleWidth ? (int)TitleSize : Mathf.Max(30, (int)(TitleSize * room / _titleWidth));
            PlaceHeader(_tagShown);

            float x = -cardsWidth / 2f + CardWidth / 2f;
            foreach (Card card in _cards)
            {
                card.Root.anchoredPosition = new Vector2(x, CardY);
                x += CardWidth + CardGap;
            }
        }

        /// <summary>Where the panel's top rim is drawn, relative to the dialog's centre.</summary>
        private static float VisibleTop
        {
            get { return DialogHeight / 2f - TopInset * DialogHeight; }
        }

        /// <summary>The cards sit centred between the bottom of the title plate and the panel's bottom rim.</summary>
        private static float CardY
        {
            get
            {
                float plateBottom = VisibleTop - PlateHeight / 2f;
                float visibleBottom = -DialogHeight / 2f + BottomInset * DialogHeight;
                return (plateBottom + visibleBottom) / 2f;
            }
        }

        /// <summary>
        /// The title plate sits centred on the panel's top rim, and with the ready tag beside it the
        /// two are centred together; on its own the plate is centred.
        /// </summary>
        private void PlaceHeader(bool tagShown)
        {
            _tagShown = tagShown;
            float y = VisibleTop;
            float group = _plateWidth + (tagShown ? TagGap + TagWidth : 0f);
            // On its own, the plate centres its title text, which sits 40 right of the plate's middle.
            float left = -group / 2f - (tagShown ? 0f : 40f);
            _plate.anchoredPosition = new Vector2(left + _plateWidth / 2f, y);
            // Level with the title text, which sits a little above the plate's middle.
            _tagBox.anchoredPosition = new Vector2(left + _plateWidth + TagGap + TagWidth / 2f, y + 11f);
        }

        private void ShowTag(bool show)
        {
            if (_tagBox.gameObject.activeSelf != show) _tagBox.gameObject.SetActive(show);
            if (_tagShown != show) PlaceHeader(show);
        }

        /// <summary>Rebuilds the cards when players arrive or leave; keeps them otherwise.</summary>
        private void SetCards(List<CardInfo> infos)
        {
            var signature = new System.Text.StringBuilder();
            // A card is rebuilt when its player's chef colour turns up (other machines send it with
            // their first update), so the hat appears.
            foreach (CardInfo info in infos)
                signature.Append(info.Key).Append(info.IsLocal ? "+" : "-")
                    .Append(info.Colour != null ? info.Colour.GetInstanceID() : 0).Append('/');
            signature.Append(SkillStats.MaxLevel).Append(SpecialCatalog.Enabled ? "S" : "");
            string value = signature.ToString();
            if (value == _signature) return;
            _signature = value;

            foreach (Card card in _cards)
                if (card.Root != null) Object.Destroy(card.Root.gameObject);
            _cards.Clear();
            for (int i = 0; i < infos.Count; i++) _cards.Add(BuildCard(infos[i], i));
            Layout(_cards.Count);
        }

        private Card BuildCard(CardInfo info, int index)
        {
            var card = new Card { Key = info.Key, IsLocal = info.IsLocal };
            card.Root = Child("Card" + (index + 1), _dialog, new Vector2(0f, CardY), new Vector2(CardWidth, CardHeight));

            Sprite panel = _cardSprites[index % _cardSprites.Length];
            card.Panel = AddImage(card.Root, panel, new Color(0.3f, 0.4f, 0.52f, 1f));
            if (panel != null) card.Panel.type = Image.Type.Sliced;
            card.PanelColour = card.Panel.color;
            card.PanelReadyColour = panel != null ? new Color(0.72f, 1f, 0.72f, 1f) : new Color(0.25f, 0.5f, 0.3f, 1f);
            card.PanelIdleColour = panel != null ? new Color(0.7f, 0.72f, 0.78f, 1f) : new Color(0.3f, 0.33f, 0.38f, 1f);

            int arrowIndex = Mathf.Clamp((int)info.Player, 0, GameArt.ArrowColours.Length - 1);
            if (info.Colour != null) arrowIndex = NearestArrow(info.Colour.UIColour);
            card.Tint = GameArt.ArrowTints[arrowIndex];

            // Chef hat in this player's colour.
            if (info.Colour != null && info.Colour.Background != null)
            {
                RectTransform hat = Child("Hat", card.Root, new Vector2(0f, HatY), new Vector2(96f, 86f));
                AddImage(hat, info.Colour.Background, Color.white).preserveAspect = true;
            }

            RectTransform label = Child("Label", card.Root, new Vector2(0f, NameY), new Vector2(320f, 44f));
            AddShadow(AddText(label, _art.Header, 34, TitleColour, TextAnchor.MiddleCenter, info.Title, true).gameObject);

            RectTransform points = Child("Points", card.Root, new Vector2(0f, PointsY), new Vector2(300f, 34f));
            card.Points = AddText(points, _art.Header, 26, PointsColour, TextAnchor.MiddleCenter, "", true);
            AddShadow(card.Points.gameObject);

            // Every skill as a row: name, level pips (red below 0, green above) and the effect.
            RectTransform rows = Child("Skills", card.Root, Vector2.zero, new Vector2(CardWidth, CardHeight));
            card.RowsGroup = rows.gameObject.AddComponent<CanvasGroup>();
            card.RowsGroup.interactable = false;
            card.RowsGroup.blocksRaycasts = false;
            card.Rows = new Row[SkillStats.Count];
            for (int i = 0; i < card.Rows.Length; i++) card.Rows[i] = BuildRow(rows, i, card.Tint);
            card.Specials = new SpecialRow[SpecialCatalog.Enabled ? SpecialCatalog.SlotCount : 0];
            for (int i = 0; i < card.Specials.Length; i++) card.Specials[i] = BuildSpecialRow(rows, i, card.Tint);
            if (SpecialCatalog.Enabled)
            {
                card.KeyRowView = BuildSpecialRow(rows, SpecialCatalog.SlotCount, card.Tint);
                card.KeyRowView.Value.color = TitleColour;
                card.DetailsRowView = BuildSpecialRow(rows, SpecialCatalog.SlotCount + 1, card.Tint);
                card.DetailsRowView.Value.color = TitleColour;
            }

            // Only this machine's players get arrows: they're the ones who can change a level here.
            if (info.IsLocal)
            {
                card.Left = Arrow(card.Root, "L", arrowIndex, ArrowLeftX);
                card.Right = Arrow(card.Root, "R", arrowIndex, ArrowRightX);
            }

            RectTransform description = Child("About", card.Root, new Vector2(0f, DescriptionY), new Vector2(292f, DescriptionHeight));
            card.Description = AddText(description, _art.Body, 22, NeutralColour, TextAnchor.MiddleCenter, "", true);
            AddShadow(card.Description.gameObject);

            // Bottom: READY! badge once locked in; otherwise the lock-in button (this machine's
            // players) or what that player is doing (everyone else).
            RectTransform badge = Child("Ready", card.Root, new Vector2(0f, HintY), new Vector2(220f, 64f));
            AddImage(badge, _art.Get(GameArt.BadgeSprite), ReadyBadgeColour).color = ReadyBadgeColour;
            RectTransform badgeText = Child("Text", badge, new Vector2(-4f, 4f), new Vector2(186f, 48f));
            AddShadow(AddText(badgeText, _art.Header, 34, Color.white, TextAnchor.MiddleCenter, Lang.T("READY!", "SẴN SÀNG!"), true).gameObject);
            card.Badge = badge.gameObject;

            RectTransform hint = Child("Hint", card.Root, new Vector2(0f, HintY), new Vector2(320f, 52f));
            card.HintGroup = hint.gameObject.AddComponent<CanvasGroup>();
            HorizontalLayoutGroup row = MakeRow(hint, 10f);
            if (info.IsLocal)
                AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UISelectNotStart, info.Player),
                    42f, "SELECT", _art);
            card.HintText = AddLabel(row.transform, _art.Header, 30, NeutralColour, "");
            card.Hint = hint.gameObject;
            return card;
        }

        private Row BuildRow(RectTransform parent, int index, Color tint)
        {
            var row = new Row();
            float y = RowY(index);
            row.Root = Child("Skill" + index, parent, new Vector2(0f, y), new Vector2(RowWidth, RowStep - 3f));

            Color highlight = tint;
            highlight.a = 0.35f;
            row.Highlight = AddImage(Stretch(Child("Highlight", row.Root)), null, highlight);
            row.Highlight.enabled = false;

            RectTransform name = Child("Name", row.Root, new Vector2(NameX, 0f), new Vector2(NameWidth, RowStep - 5f));
            AddShadow(AddText(name, _art.Header, 21, TitleColour, TextAnchor.MiddleLeft, SkillStats.Names[index], true).gameObject);

            int max = SkillStats.MaxLevel;
            float pip = (PipsWidth - 2f * PipGap - (2 * max - 2) * PipGap) / (2 * max);
            row.Minus = new Image[max];
            row.Plus = new Image[max];
            for (int k = 0; k < max; k++)
            {
                float offset = PipGap + pip / 2f + k * (pip + PipGap);
                row.Minus[k] = AddImage(Child("Minus" + k, row.Root, new Vector2(PipsX - offset, 0f),
                    new Vector2(pip, PipHeight)), null, EmptyPipColour);
                row.Plus[k] = AddImage(Child("Plus" + k, row.Root, new Vector2(PipsX + offset, 0f),
                    new Vector2(pip, PipHeight)), null, EmptyPipColour);
            }
            // A small mark at 0, between the red and green pips.
            AddImage(Child("Zero", row.Root, new Vector2(PipsX, 0f), new Vector2(2f, PipHeight + 8f)), null,
                new Color(1f, 1f, 1f, 0.6f));

            RectTransform percent = Child("Effect", row.Root, new Vector2(PercentX, 0f), new Vector2(PercentWidth, RowStep - 5f));
            row.Percent = AddText(percent, _art.Body, 20, NeutralColour, TextAnchor.MiddleRight, "", true);
            AddShadow(row.Percent.gameObject);
            return row;
        }

        private SpecialRow BuildSpecialRow(RectTransform parent, int slot, Color tint)
        {
            var row = new SpecialRow();
            row.Root = Child("Special" + slot, parent, new Vector2(0f, RowY(SkillStats.Count + slot)),
                new Vector2(RowWidth, RowStep - 3f));

            Color highlight = tint;
            highlight.a = 0.35f;
            row.Highlight = AddImage(Stretch(Child("Highlight", row.Root)), null, highlight);
            row.Highlight.enabled = false;

            RectTransform name = Child("Name", row.Root, new Vector2(NameX, 0f), new Vector2(NameWidth, RowStep - 5f));
            bool key = slot >= SpecialCatalog.SlotCount;
            AddShadow(AddText(name, _art.Header, 21, key ? NeutralColour : PointsColour, TextAnchor.MiddleLeft,
                key ? (slot == SpecialCatalog.SlotCount ? Lang.T("SKILL KEY", "PHÍM KỸ NĂNG") : Lang.T("DETAILS KEY", "PHÍM CHI TIẾT")) : SpecialCatalog.SlotNames[slot], true).gameObject);

            RectTransform value = Child("Pick", row.Root, new Vector2(SpecialValueX, 0f),
                new Vector2(SpecialValueWidth, RowStep - 5f));
            row.Value = AddText(value, _art.Header, 21, TitleColour, TextAnchor.MiddleCenter, "", true);
            AddShadow(row.Value.gameObject);
            if (!key)
            {
                // The pick's icon, between the slot's name and the pick.
                RectTransform disc = Child("Disc", row.Root, new Vector2(-21f, 0f), new Vector2(RowStep - 2f, RowStep - 2f));
                row.Disc = AddImage(disc, SpecialFx.Disc, Color.white);
                row.Disc.color = Color.white;
                RectTransform picture = Child("Picture", disc, Vector2.zero, new Vector2(RowStep, RowStep));
                row.Picture = AddImage(picture, null, Color.white);
                row.Picture.preserveAspect = true;
                RectTransform glyph = Child("Glyph", disc, Vector2.zero, new Vector2(RowStep, RowStep));
                row.Glyph = AddText(glyph, _art.Header, 12, Color.white, TextAnchor.MiddleCenter, "", false);
                disc.gameObject.SetActive(false);
            }
            return row;
        }

        private static void ShowSpecial(SpecialRow row, int slot, int pick, bool animate, float now)
        {
            if (row.Shown == pick) return;
            if (animate) row.PunchAt = now;
            row.Shown = pick;
            SpecialInfo info = SpecialCatalog.Info((Special)pick);
            if (row.Disc != null)
            {
                row.Disc.transform.gameObject.SetActive(info != null);
                if (info != null)
                {
                    row.Disc.color = SpecialFx.ColourOf((Special)pick);
                    SpecialFx.ShowIcon(row.Picture, row.Glyph, (Special)pick);
                }
            }
            if (info == null)
            {
                row.Value.text = "-";
                row.Value.color = new Color(0.6f, 0.66f, 0.75f, 1f);
                return;
            }
            row.Value.text = info.Kind == SpecialKind.Quirk ? info.Name + " +" + info.Refund : info.Name;
            row.Value.color = info.Kind == SpecialKind.Quirk ? NerfColour
                : info.Kind == SpecialKind.Active ? PointsColour : BuffColour;
        }

        /// <summary>The line under the rows: what the highlighted skill or pick does.</summary>
        private static string DescriptionFor(int selected, int[] specials, bool capturing, string detailsKey)
        {
            if (selected < 0) return "";
            if (selected < SkillStats.Count) return SkillStats.Descriptions[selected];
            if (selected == KeyRow)
                return capturing
                    ? Lang.T("Press the new key or button now (Esc / B: cancel, Backspace: back to emote)", "Bấm phím hoặc nút mới ngay (Esc / B: hủy, Backspace: về nút biểu cảm)")
                    : Lang.T("The key or button that uses your active special. SELECT to change it", "Phím hoặc nút dùng kỹ năng chủ động của bạn. SELECT để đổi");
            if (selected == DetailsRow)
                return capturing
                    ? Lang.T("Press the new key or button now (Esc / B: cancel, Backspace: no key)", "Bấm phím hoặc nút mới ngay (Esc / B: hủy, Backspace: bỏ phím)")
                    : (Lang.Vi ? "Trong bếp, " + (detailsKey == null || detailsKey == "KHÔNG CÓ" || detailsKey == "NONE" ? "một phím" : detailsKey) + " hiện hoặc ẩn chi tiết kỹ năng của bạn. SELECT để đổi" : "In the kitchen, " + (detailsKey == null || detailsKey == "NONE" ? "a key" : detailsKey) + " shows or hides your skills' details. SELECT to change it");
            int slot = selected - SkillStats.Count;
            Special pick = specials != null && slot < specials.Length ? (Special)specials[slot] : Special.None;
            SpecialInfo info = SpecialCatalog.Info(pick);
            if (info == null) return SpecialCatalog.SlotHelp[slot];
            string text = info.Description;
            if (info.Kind == SpecialKind.Active)
                text += info.Cooldown >= 999f ? Lang.T(" (once per level)", " (mỗi màn một lần)") : " (" + Mathf.RoundToInt(info.Cooldown) + " s)";
            if (info.TeamWide) text += Lang.T(". One per kitchen", ". Mỗi bếp một người");
            return text;
        }

        private Image Arrow(RectTransform parent, string side, int colourIndex, float x)
        {
            Sprite sprite = _art.Get("UI_ChefSelectArrows" + side + "_" + GameArt.ArrowColours[colourIndex]);
            if (sprite == null)
                sprite = _art.Get(side == "L" ? "t_ui_arrow_left_standard_01" : "t_ui_arrow_right_standard_01");
            RectTransform rect = Child("Arrow" + side, parent, new Vector2(x, RowTop), new Vector2(16f, 26f));
            Image image = AddImage(rect, sprite, GameArt.ArrowTints[colourIndex]);
            image.preserveAspect = true;
            return image;
        }

        private void BuildLegend(PlayerInputLookup.Player player, string skipLabel)
        {
            RectTransform bar = Child("Legend", _dialog, Vector2.zero, new Vector2(1600f, 64f));
            // Anchored to the dialog's bottom edge, so it stays put when the dialog resizes.
            bar.anchorMin = bar.anchorMax = new Vector2(0.5f, 0f);
            bar.anchoredPosition = new Vector2(0f, LegendY);
            AddImage(bar, _art.Get(GameArt.LegendSprite), new Color(0.2f, 0.42f, 0.55f, 1f));
            RectTransform items = Child("Items", bar, new Vector2(0f, 2f), new Vector2(1560f, 56f));
            HorizontalLayoutGroup row = MakeRow(items, 8f);

            AddDirection(row.transform, PlayerInputLookup.LogicalButtonID.UIUp, player, "UP", -90f);
            AddDirection(row.transform, PlayerInputLookup.LogicalButtonID.UIDown, player, "DOWN", 90f);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("SKILL", "KỸ NĂNG"));
            Spacer(row.transform, 28f);
            AddDirection(row.transform, PlayerInputLookup.LogicalButtonID.UILeft, player, "LEFT", 0f);
            AddDirection(row.transform, PlayerInputLookup.LogicalButtonID.UIRight, player, "RIGHT", 180f);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("CHANGE", "ĐỔI"));
            Spacer(row.transform, 28f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UISelectNotStart, player), 42f, "SELECT", _art);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("LOCK IN / PICK", "CHỐT / CHỌN"));
            Spacer(row.transform, 28f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UICancel, player), 42f, "BACK", _art);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("UNDO", "BỎ CHỐT"));
            Spacer(row.transform, 28f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UIChangeMode, player), 42f, "TAB", _art);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("HIDE", "ẨN"));
            Spacer(row.transform, 28f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UISkip, player), 42f, "SKIP", _art);
            Text skip = AddLabel(row.transform, _art.Header, 28, TitleColour, skipLabel);

            // Shown instead of everything else while the screen is hidden to look at the kitchen.
            RectTransform peekBar = Child("Peek", _root.transform, Vector2.zero, new Vector2(620f, 64f));
            peekBar.anchorMin = peekBar.anchorMax = new Vector2(0.5f, 1f);
            peekBar.anchoredPosition = new Vector2(0f, -60f);
            AddImage(peekBar, _art.Get(GameArt.LegendSprite), new Color(0.2f, 0.42f, 0.55f, 1f));
            RectTransform peekItems = Child("Items", peekBar, new Vector2(0f, 2f), new Vector2(600f, 56f));
            HorizontalLayoutGroup peekRow = MakeRow(peekItems, 8f);
            AddIcon(peekRow.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UIChangeMode, player), 42f, "TAB", _art);
            AddLabel(peekRow.transform, _art.Header, 28, TitleColour, Lang.T("SHOW SKILLS", "HIỆN KỸ NĂNG"));
            _peek = peekBar.gameObject;
            _peek.SetActive(false);

            // Fills under the skip label while the button is held, like the game's cutscene skip.
            // A child of the label, so the row's layout leaves it alone.
            RectTransform track = Child("SkipTrack", skip.transform);
            track.anchorMin = new Vector2(0f, 0f);
            track.anchorMax = new Vector2(1f, 0f);
            track.pivot = new Vector2(0f, 0.5f);
            track.sizeDelta = new Vector2(0f, 6f);
            track.anchoredPosition = new Vector2(0f, -2f);
            AddImage(track, null, new Color(0f, 0f, 0f, 0.35f));
            _skipFill = Child("Fill", track);
            _skipFill.anchorMin = Vector2.zero;
            _skipFill.anchorMax = Vector2.one;
            _skipFill.pivot = new Vector2(0f, 0.5f);
            _skipFill.offsetMin = Vector2.zero;
            _skipFill.offsetMax = Vector2.zero;
            AddImage(_skipFill, null, ReadyBadgeColour);
            _skipTrack = track.gameObject;
            ShowSkipProgress(0f);
        }

        /// <summary>The screen is hidden so the players can look at the kitchen.</summary>
        internal bool Hidden
        {
            get { return _hidden; }
        }

        /// <summary>Hides the dialog and the dimming (leaving a small "show skills" bar), or shows them again.</summary>
        internal void SetHidden(bool hidden)
        {
            if (!IsAlive || _hidden == hidden) return;
            _hidden = hidden;
            _dialog.gameObject.SetActive(!hidden);
            if (_peek != null) _peek.SetActive(hidden);
            if (!hidden) _openedAt = Time.unscaledTime - OpenSeconds * 0.5f;
        }

        /// <summary>Shows how far the Skip hold has got (0 hides the bar).</summary>
        internal void ShowSkipProgress(float progress)
        {
            if (!IsAlive || _skipTrack == null) return;
            progress = Mathf.Clamp01(progress);
            if (_skipTrack.activeSelf != progress > 0f) _skipTrack.SetActive(progress > 0f);
            _skipFill.localScale = new Vector3(progress, 1f, 1f);
        }

        /// <summary>
        /// Draws one frame. <paramref name="waitingForHost"/>: online, and the host's screen isn't
        /// open yet. <paramref name="everyoneReady"/>: every player who can lock in has.
        /// </summary>
        internal void Render(List<CardInfo> infos, bool everyoneReady, bool waitingForHost)
        {
            if (!IsAlive) return;
            SetCards(infos);
            float now = Time.unscaledTime;

            float open = Mathf.Clamp01((now - _openedAt) / OpenSeconds);
            float scale = Mathf.LerpUnclamped(0.86f, 1f, EaseOutBack(open));
            _dialog.localScale = new Vector3(scale, scale, 1f);
            Color dim = DimColour;
            dim.a = _hidden ? 0f : DimAlpha * Mathf.Clamp01(open * 2f);
            _dim.color = dim;
            if (_peek != null)
            {
                RectTransform peek = (RectTransform)_peek.transform;
                peek.localScale = Vector3.one * (1f + 0.04f * Mathf.Sin(now * 3f));
            }

            int counted = 0, ready = 0;
            float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(now * 3f));
            for (int i = 0; i < _cards.Count && i < infos.Count; i++)
            {
                Card card = _cards[i];
                CardInfo info = infos[i];
                SkillSync.CardState state = info.State;
                if (state != SkillSync.CardState.NoMod) counted++;
                if (state == SkillSync.CardState.Ready) ready++;
                bool isReady = state == SkillSync.CardState.Ready;
                bool known = state == SkillSync.CardState.Choosing || isReady;
                bool choosing = card.IsLocal && state == SkillSync.CardState.Choosing;

                // Rows: levels, and which one this player is changing.
                int selected = choosing ? info.Selected : -1;
                for (int s = 0; s < card.Rows.Length; s++)
                {
                    int level = known && info.Levels != null && s < info.Levels.Length ? info.Levels[s] : 0;
                    ShowLevel(card.Rows[s], (Skill)s, level, card.Initialised, now);
                    card.Rows[s].Highlight.enabled = s == selected;
                    card.Rows[s].Root.localScale = Punch(now - card.Rows[s].PunchAt, 0.06f);
                }
                for (int s = 0; s < card.Specials.Length; s++)
                {
                    int pick = known && info.Specials != null && s < info.Specials.Length ? info.Specials[s] : 0;
                    ShowSpecial(card.Specials[s], s, pick, card.Initialised, now);
                    card.Specials[s].Highlight.enabled = SkillStats.Count + s == selected;
                    card.Specials[s].Root.localScale = Punch(now - card.Specials[s].PunchAt, 0.06f);
                }
                card.RowsGroup.alpha = known ? 1f : 0.35f;

                // What the highlighted row does (it changes as the pick under it changes), or for
                // a moment why the lock-in was refused.
                float deniedAge = now - info.DeniedAt;
                string about = !string.IsNullOrEmpty(info.DeniedReason) && deniedAge >= 0f && deniedAge < 2f
                    ? info.DeniedReason : DescriptionFor(selected, info.Specials, info.Capturing, info.DetailsKeyName);
                float noticeAge = now - info.NoticeAt;
                if (!string.IsNullOrEmpty(info.Notice) && noticeAge >= 0f && noticeAge < 2.5f && !info.Capturing) about = info.Notice;
                if (card.KeyRowView != null)
                {
                    card.KeyRowView.Highlight.enabled = selected == KeyRow;
                    string key = card.IsLocal ? (info.Capturing && !info.CapturingDetails ? Lang.T("PRESS A KEY...", "BẤM PHÍM...") : info.KeyName ?? "") : "";
                    if (card.KeyRowView.Value.text != key) card.KeyRowView.Value.text = key;
                    card.KeyRowView.Value.color = info.Capturing && !info.CapturingDetails ? WithAlpha(PointsColour, pulse) : TitleColour;
                }
                if (card.DetailsRowView != null)
                {
                    card.DetailsRowView.Highlight.enabled = selected == DetailsRow;
                    bool waiting = info.Capturing && info.CapturingDetails;
                    string key = card.IsLocal ? (waiting ? Lang.T("PRESS A KEY...", "BẤM PHÍM...") : info.DetailsKeyName ?? "") : "";
                    if (card.DetailsRowView.Value.text != key) card.DetailsRowView.Value.text = key;
                    card.DetailsRowView.Value.color = waiting ? WithAlpha(PointsColour, pulse) : TitleColour;
                }
                if (!card.Initialised || card.ShownSelected != selected || card.Description.text != about)
                {
                    card.ShownSelected = selected;
                    card.Description.text = about;
                }

                if (!card.Initialised || card.ShownState != state)
                {
                    if (card.Initialised && isReady != (card.ShownState == SkillSync.CardState.Ready))
                        card.ReadyPunchAt = now;
                    card.ShownState = state;
                    card.Badge.SetActive(isReady);
                    card.Hint.SetActive(!isReady);
                    card.Panel.color = isReady ? card.PanelReadyColour
                        : state == SkillSync.CardState.Choosing ? card.PanelColour : card.PanelIdleColour;
                }

                // Points still to spend (or overspent); a build can only be locked in at exactly 0.
                int free = known ? info.FreePoints : UnknownPoints;
                if (!card.Initialised || card.ShownFree != free)
                {
                    card.ShownFree = free;
                    card.Points.text = PointsText(free);
                }
                float denied = now - info.DeniedAt;
                bool flash = denied >= 0f && denied < DeniedSeconds;
                card.Points.color = flash || free < 0 ? NerfColour : free == 0 ? BuffColour : PointsColour;
                card.Points.rectTransform.localScale = Punch(denied * PunchSeconds / DeniedSeconds, 0.18f);

                card.HintText.text = choosing && IsKeyRow(selected) ? (info.Capturing ? Lang.T("WAITING...", "ĐANG CHỜ...") : Lang.T("SET KEY", "ĐỔI PHÍM"))
                    : choosing && selected >= SkillStats.Count ? Lang.T("CHOOSE", "CHỌN") : HintFor(card.IsLocal, state);
                card.HintGroup.alpha = choosing && free != 0 && selected < SkillStats.Count ? 0.4f : 1f;
                if (!card.IsLocal && (state == SkillSync.CardState.Choosing || state == SkillSync.CardState.Loading))
                {
                    // Other players' "choosing..." / "loading..." breathe so it reads as live.
                    Color hint = NeutralColour;
                    hint.a = pulse;
                    card.HintText.color = hint;
                }
                else card.HintText.color = NeutralColour;

                if (card.Left != null) ShowArrows(card, info, selected, free, now);
                card.Initialised = true;
                card.Root.localScale = Punch(now - card.ReadyPunchAt, 0.06f);
            }

            _tagBox.localScale = Vector3.one;
            if (everyoneReady)
            {
                ShowTag(true);
                _tag.text = Lang.T("LET'S COOK!", "VÀO BẾP THÔI!");
                _tag.color = ReadyBadgeColour;
            }
            else if (waitingForHost)
            {
                ShowTag(true);
                _tag.text = Lang.T("WAITING FOR HOST", "CHỜ CHỦ PHÒNG");
                _tag.color = TitleColour;
            }
            else if (counted > 1)
            {
                ShowTag(true);
                _tag.text = Lang.T("READY ", "SẴN SÀNG ") + ready + "/" + counted;
                _tag.color = ready > 0 ? ReadyBadgeColour : TitleColour;
            }
            else
            {
                ShowTag(false);
            }
        }

        /// <summary>The coloured arrows sit either side of the highlighted skill's pips.</summary>
        private static void ShowArrows(Card card, CardInfo info, int selected, int free, float now)
        {
            // Only skill rows step with Left/Right; special rows open the picker.
            bool show = selected >= 0 && selected < SkillStats.Count;
            card.Left.enabled = show;
            card.Right.enabled = show;
            if (!show) return;
            float y = RowY(selected);
            // They nudge outwards like the game's chef-select arrows.
            float nudge = 3f * Mathf.Abs(Mathf.Sin(now * 4f));
            int max = SkillStats.MaxLevel;
            int level = info.Levels[selected];
            // They fade where there's no further to go: the bottom level, or the top one.
            card.Left.rectTransform.anchoredPosition = new Vector2(ArrowLeftX - nudge, y);
            card.Right.rectTransform.anchoredPosition = new Vector2(ArrowRightX + nudge, y);
            card.Left.color = new Color(1f, 1f, 1f, level > -max ? 1f : 0.3f);
            card.Right.color = new Color(1f, 1f, 1f, level < max ? 1f : 0.3f);
            if (card.Left.sprite == null) card.Left.color = WithAlpha(card.Tint, card.Left.color.a);
            if (card.Right.sprite == null) card.Right.color = WithAlpha(card.Tint, card.Right.color.a);
        }

        private static void ShowLevel(Row row, Skill skill, int level, bool animate, float now)
        {
            int max = SkillStats.MaxLevel;
            level = Mathf.Clamp(level, -max, max);
            if (row.Shown == level) return;
            if (animate) row.PunchAt = now;
            row.Shown = level;
            for (int k = 0; k < max; k++)
            {
                row.Minus[k].color = -level > k ? NerfColour : EmptyPipColour;
                row.Plus[k].color = level > k ? BuffColour : EmptyPipColour;
            }
            int percent = SkillStats.PercentChange(skill, level);
            row.Percent.text = level == 0 || percent == 0 ? "" : (percent > 0 ? "+" : "-") + Mathf.Abs(percent) + "%";
            row.Percent.color = percent > 0 ? BuffColour : NerfColour;
        }

        private static string PointsText(int free)
        {
            if (free == UnknownPoints) return "";
            if (free == 0) return Lang.T("BALANCED", "ĐÃ CÂN BẰNG");
            if (free > 0) return Lang.Vi ? "CÒN " + free + " ĐIỂM ĐỂ CỘNG" : free == 1 ? "1 POINT TO SPEND" : free + " POINTS TO SPEND";
            return Lang.Vi ? "LỐ " + -free + " ĐIỂM" : free == -1 ? "1 POINT OVER" : -free + " POINTS OVER";
        }

        private static string HintFor(bool local, SkillSync.CardState state)
        {
            switch (state)
            {
                case SkillSync.CardState.Choosing: return local ? Lang.T("LOCK IN", "CHỐT") : Lang.T("CHOOSING...", "ĐANG CHỌN...");
                case SkillSync.CardState.Loading: return Lang.T("LOADING...", "ĐANG TẢI...");
                case SkillSync.CardState.NoMod: return Lang.T("NO MOD", "CHƯA CÀI MOD");
                default: return "";
            }
        }

        internal void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
            for (int i = 0; i < _cardSprites.Length; i++)
            {
                if (_cardSprites[i] != null) Object.Destroy(_cardSprites[i]);
                _cardSprites[i] = null;
            }
            _cards.Clear();
        }

        // --- small helpers -------------------------------------------------------------------

        private static Color WithAlpha(Color colour, float alpha)
        {
            colour.a = alpha;
            return colour;
        }

        private static int NearestArrow(Color colour)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < GameArt.ArrowTints.Length; i++)
            {
                Color tint = GameArt.ArrowTints[i];
                float dr = tint.r - colour.r, dg = tint.g - colour.g, db = tint.b - colour.b;
                float distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        /// <summary>A quick grow-and-settle, <paramref name="amount"/> bigger at the peak.</summary>
        private static Vector3 Punch(float age, float amount)
        {
            if (age < 0f || age >= PunchSeconds) return Vector3.one;
            float s = 1f + amount * Mathf.Sin(Mathf.PI * age / PunchSeconds);
            return new Vector3(s, s, 1f);
        }

        private static RectTransform Child(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static RectTransform Child(string name, Transform parent, Vector2 position, Vector2 size)
        {
            RectTransform rect = Child(name, parent);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        private static RectTransform Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        /// <summary>An image of <paramref name="sprite"/>, or a flat <paramref name="fallback"/> panel without it.</summary>
        private static Image AddImage(RectTransform rect, Sprite sprite, Color fallback)
        {
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = sprite;
            image.color = sprite != null ? Color.white : fallback;
            return image;
        }

        private static Text AddText(RectTransform rect, Font font, int size, Color colour, TextAnchor alignment,
            string value, bool shrinkToFit)
        {
            Text text = rect.gameObject.AddComponent<Text>();
            text.raycastTarget = false;
            text.supportRichText = false;
            text.font = font;
            text.fontSize = size;
            text.color = colour;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            if (shrinkToFit)
            {
                text.resizeTextForBestFit = true;
                text.resizeTextMaxSize = size;
                text.resizeTextMinSize = Mathf.Max(10, size / 2);
            }
            text.text = value;
            return text;
        }

        private static void AddShadow(GameObject target)
        {
            Shadow shadow = target.AddComponent<Shadow>();
            shadow.effectColor = ShadowColour;
            shadow.effectDistance = new Vector2(2f, -3f);
        }

        private static HorizontalLayoutGroup MakeRow(RectTransform rect, float spacing)
        {
            HorizontalLayoutGroup row = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleCenter;
            row.spacing = spacing;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            return row;
        }

        private static Text AddLabel(Transform row, Font font, int size, Color colour, string value)
        {
            RectTransform rect = Child("Label", row);
            Text text = rect.gameObject.AddComponent<Text>();
            text.raycastTarget = false;
            text.supportRichText = false;
            text.font = font;
            text.fontSize = size;
            text.color = colour;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = value;
            return text;
        }

        /// <summary>A button icon, or the button's name in brackets when the game has no icon for it.</summary>
        private static void AddIcon(Transform row, Sprite icon, float size, string fallbackName, GameArt art)
        {
            if (icon == null)
            {
                AddLabel(row, art.Header, 28, TitleColour, "[" + fallbackName + "]");
                return;
            }
            RectTransform rect = Child("Icon", row);
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = icon;
            image.preserveAspect = true;
            LayoutElement layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = size;
            layout.preferredHeight = size;
        }

        /// <summary>
        /// A direction button's icon. The game has no icon for some devices' directions (it would
        /// show as "[UP]"), so those get the game's own menu arrow, turned to point the right way:
        /// <paramref name="angle"/> is the turn from pointing left, anticlockwise.
        /// </summary>
        private void AddDirection(Transform row, PlayerInputLookup.LogicalButtonID id, PlayerInputLookup.Player player,
            string name, float angle)
        {
            Sprite icon = GameArt.ButtonIcon(id, player);
            Sprite arrow = _art.Get("t_ui_arrow_left_standard_01");
            if (icon != null || arrow == null)
            {
                AddIcon(row, icon, 42f, name, _art);
                return;
            }
            // The layout sizes a holder; the arrow turns inside it.
            RectTransform holder = Child("Icon", row);
            LayoutElement layout = holder.gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = 34f;
            layout.preferredHeight = 34f;
            RectTransform turned = Child("Arrow", holder, Vector2.zero, new Vector2(34f, 34f));
            Image image = AddImage(turned, arrow, TitleColour);
            image.preserveAspect = true;
            turned.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        private static void Spacer(Transform row, float width)
        {
            LayoutElement layout = Child("Gap", row).gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = width;
            layout.preferredHeight = 1f;
        }
    }
}
