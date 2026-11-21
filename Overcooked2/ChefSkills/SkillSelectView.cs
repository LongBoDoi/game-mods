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
        private const float CardHeight = 680f;
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
        private const float HatY = 278f;
        private const float NameY = 214f;
        private const float PointsY = 174f;
        private const float RowTop = 128f;
        private const float RowStep = 37f;
        private const float DescriptionY = -222f;
        private const float HintY = -282f;
        // Rows fill the card art's dark interior (304 wide, measured in game) evenly, 8 in from
        // each side: name -144..-34, arrow, pips -12..84, arrow, effect 104..144.
        private const float RowWidth = 300f;
        private const float NameX = -89f;
        private const float NameWidth = 110f;
        private const float PipsX = 36f;
        private const float PipsWidth = 96f;
        private const float PipGap = 3f;
        private const float PipHeight = 18f;
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
        private RectTransform _skipFill;
        private readonly Sprite[] _cardSprites = new Sprite[2];
        private readonly List<Card> _cards = new List<Card>();

        internal bool IsAlive
        {
            get { return _root != null; }
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
                "CHOOSE YOUR SKILLS", false);
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
            signature.Append(SkillStats.MaxLevel);
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

            // Only this machine's players get arrows: they're the ones who can change a level here.
            if (info.IsLocal)
            {
                card.Left = Arrow(card.Root, "L", arrowIndex, ArrowLeftX);
                card.Right = Arrow(card.Root, "R", arrowIndex, ArrowRightX);
            }

            RectTransform description = Child("About", card.Root, new Vector2(0f, DescriptionY), new Vector2(292f, 56f));
            card.Description = AddText(description, _art.Body, 22, NeutralColour, TextAnchor.MiddleCenter, "", true);
            AddShadow(card.Description.gameObject);

            // Bottom: READY! badge once locked in; otherwise the lock-in button (this machine's
            // players) or what that player is doing (everyone else).
            RectTransform badge = Child("Ready", card.Root, new Vector2(0f, HintY), new Vector2(220f, 64f));
            AddImage(badge, _art.Get(GameArt.BadgeSprite), ReadyBadgeColour).color = ReadyBadgeColour;
            RectTransform badgeText = Child("Text", badge, new Vector2(-4f, 4f), new Vector2(186f, 48f));
            AddShadow(AddText(badgeText, _art.Header, 34, Color.white, TextAnchor.MiddleCenter, "READY!", true).gameObject);
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
            float y = RowTop - RowStep * index;
            row.Root = Child("Skill" + index, parent, new Vector2(0f, y), new Vector2(RowWidth, RowStep - 3f));

            Color highlight = tint;
            highlight.a = 0.35f;
            row.Highlight = AddImage(Stretch(Child("Highlight", row.Root)), null, highlight);
            row.Highlight.enabled = false;

            RectTransform name = Child("Name", row.Root, new Vector2(NameX, 0f), new Vector2(NameWidth, RowStep - 5f));
            AddShadow(AddText(name, _art.Header, 23, TitleColour, TextAnchor.MiddleLeft, SkillStats.Names[index], true).gameObject);

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
            row.Percent = AddText(percent, _art.Body, 21, NeutralColour, TextAnchor.MiddleRight, "", true);
            AddShadow(row.Percent.gameObject);
            return row;
        }

        private Image Arrow(RectTransform parent, string side, int colourIndex, float x)
        {
            Sprite sprite = _art.Get("UI_ChefSelectArrows" + side + "_" + GameArt.ArrowColours[colourIndex]);
            if (sprite == null)
                sprite = _art.Get(side == "L" ? "t_ui_arrow_left_standard_01" : "t_ui_arrow_right_standard_01");
            RectTransform rect = Child("Arrow" + side, parent, new Vector2(x, RowTop), new Vector2(18f, 32f));
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
            AddLabel(row.transform, _art.Header, 28, TitleColour, "SKILL");
            Spacer(row.transform, 28f);
            AddDirection(row.transform, PlayerInputLookup.LogicalButtonID.UILeft, player, "LEFT", 0f);
            AddDirection(row.transform, PlayerInputLookup.LogicalButtonID.UIRight, player, "RIGHT", 180f);
            AddLabel(row.transform, _art.Header, 28, TitleColour, "LOWER / RAISE");
            Spacer(row.transform, 28f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UISelectNotStart, player), 42f, "SELECT", _art);
            AddLabel(row.transform, _art.Header, 28, TitleColour, "LOCK IN");
            Spacer(row.transform, 28f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UICancel, player), 42f, "BACK", _art);
            AddLabel(row.transform, _art.Header, 28, TitleColour, "UNDO");
            Spacer(row.transform, 28f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UISkip, player), 42f, "SKIP", _art);
            Text skip = AddLabel(row.transform, _art.Header, 28, TitleColour, skipLabel);

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
            dim.a = DimAlpha * Mathf.Clamp01(open * 2f);
            _dim.color = dim;

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
                card.RowsGroup.alpha = known ? 1f : 0.35f;

                if (!card.Initialised || card.ShownSelected != selected)
                {
                    card.ShownSelected = selected;
                    card.Description.text = selected >= 0 ? SkillStats.Descriptions[selected] : "";
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

                card.HintText.text = HintFor(card.IsLocal, state);
                card.HintGroup.alpha = choosing && free != 0 ? 0.4f : 1f;
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
                _tag.text = "LET'S COOK!";
                _tag.color = ReadyBadgeColour;
            }
            else if (waitingForHost)
            {
                ShowTag(true);
                _tag.text = "WAITING FOR HOST";
                _tag.color = TitleColour;
            }
            else if (counted > 1)
            {
                ShowTag(true);
                _tag.text = "READY " + ready + "/" + counted;
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
            bool show = selected >= 0;
            card.Left.enabled = show;
            card.Right.enabled = show;
            if (!show) return;
            int max = SkillStats.MaxLevel;
            int level = info.Levels[selected];
            float y = RowTop - RowStep * selected;
            // They nudge outwards like the game's chef-select arrows, and fade where there's no
            // further to go: the bottom level, or the top one / no point to spend.
            float nudge = 3f * Mathf.Abs(Mathf.Sin(now * 4f));
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
            if (free == 0) return "BALANCED";
            if (free > 0) return free == 1 ? "1 POINT TO SPEND" : free + " POINTS TO SPEND";
            return free == -1 ? "1 POINT OVER" : -free + " POINTS OVER";
        }

        private static string HintFor(bool local, SkillSync.CardState state)
        {
            switch (state)
            {
                case SkillSync.CardState.Choosing: return local ? "LOCK IN" : "CHOOSING...";
                case SkillSync.CardState.Loading: return "LOADING...";
                case SkillSync.CardState.NoMod: return "NO MOD";
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
