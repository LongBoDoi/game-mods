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
            /// <summary>Local cards: where this player's skill bar shows in a level (the DISPLAY row).</summary>
            public string BarMode;
            /// <summary>Local cards: the BAR SIZE and BAR POSITION rows.</summary>
            public string BarSize, BarSpot;
            /// <summary>Local cards: the BAR SIZE row's value in percent (its arrows fade at either end).</summary>
            public int BarSizePercent;
            /// <summary>Local cards: moving the skill bar's preview to place it (the BAR POSITION row).</summary>
            public bool Placing;
            /// <summary>Local cards: the player is on the keyboard (what the key-setting help says).</summary>
            public bool Keyboard;
            /// <summary>A short message (the new key, or why it wasn't taken), shown in place of the description for a moment.</summary>
            public string Notice;
            public float NoticeAt;
            /// <summary>Local cards: the SETTINGS panel is open over the card, with <see cref="Setting"/> highlighted.</summary>
            public bool SettingsOpen;
            public int Setting;
        }

        /// <summary>One of the setting rows in a card's SETTINGS panel: its name and value.</summary>
        private sealed class SettingRow
        {
            public RectTransform Root;
            public Image Highlight;
            public Text Name, Value;
            // The value sits in a box of its own (framed while the row is highlighted).
            public Image Pill, PillFrame;
            public float PunchAt = -10f;
        }

        /// <summary>A button on a card (EDIT SKILLS, SETTINGS): a row SELECT (or a click) uses.</summary>
        private sealed class CardButton
        {
            public RectTransform Root;
            public Image Back, Frame;
            public Text Label;
        }

        /// <summary>
        /// Rows on a card: the nine skills, then (with specials on) the EDIT SKILLS button under the
        /// special icons and the SETTINGS button.
        /// </summary>
        internal static int RowCount
        {
            get { return SkillStats.Count + (SpecialCatalog.Enabled ? 2 : 0); }
        }

        /// <summary>The EDIT SKILLS button, which opens the special picker (-1 with specials off).</summary>
        internal static int EditRow
        {
            get { return SpecialCatalog.Enabled ? SkillStats.Count : -1; }
        }

        /// <summary>The SETTINGS button, which opens the keys and skill bar panel (-1 with specials off).</summary>
        internal static int SettingsRow
        {
            get { return SpecialCatalog.Enabled ? SkillStats.Count + 1 : -1; }
        }

        /// <summary>The rows of the SETTINGS panel, in order.</summary>
        internal const int KeySetting = 0, DetailsSetting = 1, BarSetting = 2, SizeSetting = 3, SpotSetting = 4;
        internal const int SettingCount = 5;

        private static string[] SettingNames
        {
            get
            {
                return new[]
                {
                    Lang.T("SKILL KEY", "PHÍM KỸ NĂNG"), Lang.T("INFO KEY", "PHÍM XEM"), Lang.T("DISPLAY", "HIỂN THỊ"),
                    Lang.T("BAR SIZE", "CỠ THANH"), Lang.T("BAR POSITION", "VỊ TRÍ THANH"),
                };
            }
        }

        /// <summary>A skill row's centre, in the card's row space.</summary>
        private static float RowY(int index)
        {
            return RowTop - RowStep * index;
        }

        /// <summary>The row of special icons, under the nine skills.</summary>
        private static float IconsY
        {
            get { return RowY(SkillStats.Count - 1) - RowStep / 2f - IconsGap - IconSize / 2f; }
        }

        /// <summary>A row's centre: a skill, or one of the buttons under the special icons.</summary>
        private static float RowCentre(int row)
        {
            if (row >= 0 && row == EditRow) return IconsY - IconSize / 2f - ButtonGap - ButtonHeight / 2f;
            if (row >= 0 && row == SettingsRow) return RowCentre(EditRow) - ButtonHeight - SettingsGap;
            return RowY(row);
        }

        /// <summary>How far down the card's rows go (the scroll window's content).</summary>
        private static float RowsBottom
        {
            get
            {
                return SpecialCatalog.Enabled ? RowCentre(SettingsRow) - ButtonHeight / 2f
                    : RowY(SkillStats.Count - 1) - RowStep / 2f;
            }
        }

        private sealed class Row
        {
            public RectTransform Root;
            public Image Highlight;
            public Text Percent;
            public Image[] Minus;
            public Image[] Plus;
            public int Shown = int.MinValue;
            public int ShownFloor = int.MinValue;
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
            // The special picks as icons, with a highlight behind them, and the buttons under them
            // (local cards only).
            public SlotIcon[] Icons;
            public float[] IconPunchAt;
            public Image IconsHighlight;
            public CardButton EditButton, SettingsButton;
            // The SETTINGS panel over the card (local cards): SKILL KEY, INFO KEY, DISPLAY, BAR
            // SIZE, BAR POSITION, and its own arrows for the rows that step.
            public RectTransform SettingsPanel;
            public SettingRow[] Settings;
            public Image SettingLeft, SettingRight;
            public bool SettingsShown;
            public float SettingsOpenedAt = -10f;
            // The hint's CLOSE part, shown in place of LOCK IN while the panel is open.
            public GameObject ClosePart;
            // The window the rows scroll in, the rows themselves, and how far they are scrolled up.
            public RectTransform Viewport, Content;
            public float Scroll, ScrollTarget;
            public Image MoreUp, MoreDown;
            // The hint at the bottom: what SELECT does on this row, and LOCK IN.
            public GameObject PickPart;
            public Text PickText;
            public CanvasGroup LockGroup;
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
        // The window the rows scroll in (card units): from just under the points line down to
        // above the description.
        private const float ViewTop = RowTop + RowStep / 2f + 2f;
        private const float ViewBottom = -212f;
        // Keep this much of the next row in view while scrolling.
        private const float ScrollMargin = RowStep;
        // Under the nine skills: the special picks as a row of icons, then the EDIT SKILLS and
        // SETTINGS buttons.
        // The active's icon is IconSize across; the others are smaller (see SlotIcon), IconGap apart.
        private const float IconsGap = 10f;
        private const float IconSize = 50f;
        private const float IconGap = 18f;
        // CHOOSE SKILLS sits a little further under the icons than SETTINGS under it.
        private const float ButtonGap = 18f;
        private const float SettingsGap = 6f;
        private const float ButtonWidth = 230f;
        private const float ButtonHeight = 30f;
        // The SETTINGS panel covers the card from under its name down to the description.
        private const float PanelTop = 232f;
        private const float PanelBottom = -212f;
        private const float PanelTitleY = 204f;
        private const float SettingTop = 158f;
        private const float SettingStep = 40f;
        // A setting row: its name on the left in small muted letters; its value on the right, in a
        // box of its own (between its arrows, inside the box, for the rows that step).
        private const float SettingNameX = -78f;
        private const float SettingNameWidth = 136f;
        private const float SettingValueX = 76f;
        private const float SettingValueWidth = 140f;
        private const float SettingPillHeight = 30f;
        private const float SteppedValueWidth = 92f;
        private const float SettingArrowLeftX = 18f;
        private const float SettingArrowRightX = 134f;
        // The description has its own room under the rows' window (-214..-294), up to three lines,
        // above the hint (its icons start at -301).
        private const float DescriptionY = -254f;
        private const float DescriptionHeight = 78f;
        private const float HintY = -322f;
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
        // Hiding (to look at the kitchen) and closing (the level starting) are quicker than opening.
        private const float HideSeconds = 0.2f;
        private const float CloseSeconds = 0.25f;
        // How small the dialog is when it pops in, and when it has shrunk away.
        private const float HiddenScale = 0.86f;
        private const float ClosedScale = 0.9f;
        // The "show skills" bar drops in from above the screen's top edge.
        private const float PeekY = -60f;
        private const float PeekHiddenY = 50f;
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
        private static readonly Color ButtonColour = new Color(0.1f, 0.15f, 0.24f, 0.85f);
        private static readonly Color PanelBackColour = new Color(0.07f, 0.1f, 0.16f, 0.97f);
        private static readonly Color PillColour = new Color(0.17f, 0.25f, 0.38f, 1f);
        private static readonly Color PillSelectedColour = new Color(0.26f, 0.38f, 0.56f, 1f);
        private static readonly Color SettingNameColour = new Color(0.66f, 0.72f, 0.82f, 1f);

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
        private CanvasGroup _dialogGroup;
        // The dialog's show/hide transition: it runs from _shownFrom (when it last changed
        // direction, at _changedAt) towards 1 when shown or 0 when hidden.
        private float _shownFrom;
        private float _changedAt;
        // Closing: the screen fades out from how it looked at _closedAt, then destroys itself.
        private bool _closing;
        private float _closedAt;
        private float _closeDialogAlpha;
        private float _closeScale;
        private float _closeDim;
        private float _closePeekAlpha;
        private string _signature;
        private GameObject _skipTrack;
        private GameObject _peek;
        private CanvasGroup _peekGroup;
        private bool _hidden;
        private RectTransform _skipFill;
        private readonly Sprite[] _cardSprites = new Sprite[2];
        private readonly List<Card> _cards = new List<Card>();

        /// <summary>Built and not yet closed (a closing screen is only fading out).</summary>
        internal bool IsAlive
        {
            get { return _root != null && !_closing; }
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

        /// <summary>
        /// The control of one of this machine's cards under the mouse: a special icon or EDIT SKILLS
        /// (<paramref name="row"/> = <see cref="EditRow"/>, <paramref name="slot"/> = the icon's slot,
        /// or the ACTIVE slot for the button), or SETTINGS (<see cref="SettingsRow"/>).
        /// </summary>
        internal bool ControlAt(Vector3 mouse, out string cardKey, out int row, out int slot)
        {
            cardKey = null;
            row = -1;
            slot = 0;
            if (!IsAlive || _hidden) return false;
            foreach (Card card in _cards)
            {
                if (!card.IsLocal || card.Icons == null || card.SettingsShown) continue;
                // A row scrolled out of the window can't be clicked.
                if (card.Viewport == null || !RectTransformUtility.RectangleContainsScreenPoint(card.Viewport, mouse, null)) continue;
                cardKey = card.Key;
                for (int i = 0; i < card.Icons.Length; i++)
                {
                    if (!Contains(card.Icons[i].Root, mouse)) continue;
                    row = EditRow;
                    slot = i;
                    return true;
                }
                if (card.EditButton != null && Contains(card.EditButton.Root, mouse))
                {
                    row = EditRow;
                    return true;
                }
                if (card.SettingsButton != null && Contains(card.SettingsButton.Root, mouse))
                {
                    row = SettingsRow;
                    return true;
                }
            }
            cardKey = null;
            return false;
        }

        /// <summary>The row of a card's open SETTINGS panel under the mouse (-1: on the panel, but not on a row).</summary>
        internal bool SettingAt(Vector3 mouse, string cardKey, out int setting)
        {
            setting = -1;
            if (!IsAlive || _hidden) return false;
            foreach (Card card in _cards)
            {
                if (card.Key != cardKey || !card.SettingsShown || !Contains(card.SettingsPanel, mouse)) continue;
                for (int i = 0; i < card.Settings.Length; i++)
                    if (Contains(card.Settings[i].Root, mouse)) setting = i;
                return true;
            }
            return false;
        }

        private static bool Contains(RectTransform rect, Vector3 mouse)
        {
            return rect != null && rect.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rect, mouse, null);
        }

        /// <summary>A player's skill bar, drawn where it will sit in the kitchen while they place it.</summary>
        internal struct BarPreview
        {
            public string CardKey;
            public string Label;
            /// <summary>The bar's bottom centre, as fractions of the screen from its bottom-left.</summary>
            public Vector2 Spot;
            /// <summary>Its size in the skill bar's own canvas units (already scaled by the player's size).</summary>
            public float Width, Height;
        }

        private readonly Dictionary<string, RectTransform> _previews = new Dictionary<string, RectTransform>();

        /// <summary>Draws these bar previews over the screen (and removes any others).</summary>
        internal void ShowPreviews(List<BarPreview> previews)
        {
            if (!IsAlive) return;
            var keep = new HashSet<string>();
            RectTransform canvas = (RectTransform)_root.transform;
            Canvas own = _root.GetComponent<Canvas>();
            // The skill bar's canvas scales 1920x1080 to the screen matching width and height halfway.
            float barFactor = Mathf.Sqrt(Screen.width / 1920f * (Screen.height / 1080f));
            float factor = own != null && own.scaleFactor > 0f ? barFactor / own.scaleFactor : 1f;
            float now = Time.unscaledTime;
            foreach (BarPreview preview in previews)
            {
                keep.Add(preview.CardKey);
                RectTransform rect;
                if (!_previews.TryGetValue(preview.CardKey, out rect) || rect == null)
                {
                    rect = BuildPreview(preview.CardKey);
                    _previews[preview.CardKey] = rect;
                }
                float w = preview.Width * factor, h = preview.Height * factor;
                rect.sizeDelta = new Vector2(w, h);
                rect.anchoredPosition = SpecialHud.SpotToCorner(preview.Spot, w, h, canvas.rect);
                rect.SetAsLastSibling();
                Color tint = TintOf(preview.CardKey);
                float pulse = 0.5f + 0.5f * Mathf.Sin(now * 5f);
                rect.Find("Back").GetComponent<Image>().color = WithAlpha(tint, 0.35f + 0.1f * pulse);
                rect.Find("Frame").GetComponent<Image>().color = Color.Lerp(tint, Color.white, 0.3f + 0.4f * pulse);
                Text label = rect.Find("Label").GetComponent<Text>();
                if (label.text != preview.Label) label.text = preview.Label;
            }
            var gone = new List<string>();
            foreach (KeyValuePair<string, RectTransform> pair in _previews)
                if (!keep.Contains(pair.Key)) gone.Add(pair.Key);
            foreach (string key in gone)
            {
                if (_previews[key] != null) Object.Destroy(_previews[key].gameObject);
                _previews.Remove(key);
            }
        }

        private Color TintOf(string cardKey)
        {
            foreach (Card card in _cards)
                if (card.Key == cardKey) return card.Tint;
            return TitleColour;
        }

        private RectTransform BuildPreview(string key)
        {
            RectTransform rect = Child("BarPreview" + key, _root.transform);
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            Image back = AddImage(Stretch(Child("Back", rect)), SpecialFx.RoundBox, TitleColour);
            back.type = Image.Type.Sliced;
            RectTransform frameRect = Stretch(Child("Frame", rect));
            frameRect.offsetMin = new Vector2(-3f, -3f);
            frameRect.offsetMax = new Vector2(3f, 3f);
            Image frame = AddImage(frameRect, SpecialFx.RoundFrame, TitleColour);
            frame.type = Image.Type.Sliced;
            RectTransform label = Stretch(Child("Label", rect));
            label.offsetMin = new Vector2(6f, 4f);
            label.offsetMax = new Vector2(-6f, -4f);
            AddShadow(AddText(label, _art.Header, 28, TitleColour, TextAnchor.MiddleCenter, "", true).gameObject);
            return rect;
        }

        /// <summary>Where a card's centre is across the screen, in the canvas's reference units (0 = middle).</summary>
        internal bool CardX(string cardKey, out float x)
        {
            x = 0f;
            if (!IsAlive) return false;
            foreach (Card card in _cards)
            {
                if (card.Key != cardKey || card.Root == null) continue;
                x = _dialog.anchoredPosition.x + card.Root.anchoredPosition.x;
                return true;
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
            view._shownFrom = 0f;
            view._changedAt = Time.unscaledTime;
            view.ApplyShown(0f, Time.unscaledTime);
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
            _dialogGroup = _dialog.gameObject.AddComponent<CanvasGroup>();

            // Header plate straddling the top-left edge, as on the game's own dialogs.
            _plate = Child("Header", _dialog, Vector2.zero, new Vector2(840f, 168f));
            AddImage(_plate, _art.Get(GameArt.HeaderSprite), new Color(0.27f, 0.72f, 0.86f, 1f));
            // Centred on the banner (its slanted ends are the same width either side).
            _title = Child("Title", _plate, new Vector2(0f, 11f), new Vector2(690f, 113f));
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

        /// <summary>
        /// The cards sit centred on the panel, between its top and bottom rims (the title plate's
        /// art ends above the cards' tops either way).
        /// </summary>
        private static float CardY
        {
            get
            {
                float visibleBottom = -DialogHeight / 2f + BottomInset * DialogHeight;
                return (VisibleTop + visibleBottom) / 2f;
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
            float left = -group / 2f;
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

            // Every skill as a row: name, level pips (red below 0, green above) and the effect. The
            // rows scroll inside a window (keeping the highlighted one in view), so the line under
            // them always has room.
            card.Viewport = Child("Rows", card.Root, new Vector2(0f, (ViewTop + ViewBottom) / 2f), new Vector2(CardWidth, ViewTop - ViewBottom));
            card.Viewport.gameObject.AddComponent<RectMask2D>();
            RectTransform rows = Child("Skills", card.Viewport, new Vector2(0f, -(ViewTop + ViewBottom) / 2f), new Vector2(CardWidth, CardHeight));
            card.Content = rows;
            card.RowsGroup = rows.gameObject.AddComponent<CanvasGroup>();
            card.RowsGroup.interactable = false;
            card.RowsGroup.blocksRaycasts = false;
            card.Rows = new Row[SkillStats.Count];
            for (int i = 0; i < card.Rows.Length; i++) card.Rows[i] = BuildRow(rows, i, card.Tint);
            // The special picks as one row of icons. Only this machine's players get the buttons
            // under them: other players' cards show what they picked, not their keys and settings.
            card.Icons = new SlotIcon[0];
            card.IconPunchAt = new float[0];
            if (SpecialCatalog.Enabled)
            {
                BuildIcons(card, rows);
                if (info.IsLocal)
                {
                    card.EditButton = BuildButton(rows, "Edit", RowCentre(EditRow), Lang.T("CHOOSE SKILLS", "CHỌN KỸ NĂNG"));
                    card.SettingsButton = BuildButton(rows, "Settings", RowCentre(SettingsRow), Lang.T("SETTINGS", "CÀI ĐẶT"));
                }
            }

            // Only this machine's players get arrows: they're the ones who can change a level here.
            if (info.IsLocal)
            {
                card.Left = Arrow(rows, "L", arrowIndex, ArrowLeftX);
                card.Right = Arrow(rows, "R", arrowIndex, ArrowRightX);
            }

            // Little arrows at the window's edges when there are more rows above or below.
            card.MoreUp = ScrollMark(card.Root, ViewTop - 8f, -90f);
            card.MoreDown = ScrollMark(card.Root, ViewBottom + 8f, 90f);

            // Over the rows, under the description (which explains the highlighted setting).
            card.Settings = new SettingRow[0];
            if (info.IsLocal && SpecialCatalog.Enabled) BuildSettingsPanel(card, arrowIndex);

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

            // Local cards: the player's own buttons. On a row where SELECT does something, its
            // button and what it does ("[A] CHOOSE"), then always the lock-in button ("[X] LOCK IN").
            RectTransform hint = Child("Hint", card.Root, new Vector2(0f, HintY), new Vector2(340f, 52f));
            card.HintGroup = hint.gameObject.AddComponent<CanvasGroup>();
            HorizontalLayoutGroup row = MakeRow(hint, 18f);
            if (info.IsLocal)
            {
                RectTransform pick = Child("Pick", row.transform);
                MakeRow(pick, 8f);
                pick.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                AddIcon(pick, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UISelectNotStart, info.Player), 38f,
                    SpecialInput.IsKeyboard(info.Player) ? "SPACE" : "A", _art);
                card.PickText = AddLabel(pick, _art.Header, 26, NeutralColour, "");
                card.PickPart = pick.gameObject;
                card.PickPart.SetActive(false);
            }
            RectTransform lockPart = Child("LockIn", row.transform);
            MakeRow(lockPart, 8f);
            card.LockGroup = lockPart.gameObject.AddComponent<CanvasGroup>();
            if (info.IsLocal) AddLockInIcon(lockPart, info.Player);
            card.HintText = AddLabel(lockPart, _art.Header, 30, NeutralColour, "");
            if (info.IsLocal)
            {
                // While the SETTINGS panel is open, BACK closes it (locking in waits until then).
                RectTransform closePart = Child("Close", row.transform);
                MakeRow(closePart, 8f);
                AddIcon(closePart, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UICancel, info.Player), 38f,
                    SpecialInput.IsKeyboard(info.Player) ? "ESC" : "B", _art);
                AddLabel(closePart, _art.Header, 26, NeutralColour, Lang.T("CLOSE", "ĐÓNG"));
                card.ClosePart = closePart.gameObject;
                card.ClosePart.SetActive(false);
            }
            card.Hint = hint.gameObject;
            return card;
        }

        /// <summary>The special picks, one icon per slot, side by side under the skills.</summary>
        private void BuildIcons(Card card, RectTransform parent)
        {
            float y = IconsY;
            int count = SpecialCatalog.SlotCount;
            Color highlight = card.Tint;
            highlight.a = 0.2f;
            RectTransform back = Child("IconsHighlight", parent, new Vector2(0f, y), new Vector2(RowWidth, IconSize + 8f));
            card.IconsHighlight = AddImage(back, SpecialFx.RoundBox, highlight);
            card.IconsHighlight.type = Image.Type.Sliced;
            card.IconsHighlight.color = highlight;
            card.IconsHighlight.enabled = false;

            // Side by side, each slot in its own style and size (the active biggest), centred.
            card.Icons = new SlotIcon[count];
            card.IconPunchAt = new float[count];
            float total = (count - 1) * IconGap;
            for (int i = 0; i < count; i++) total += IconSize * SlotIcon.SizeOf((SpecialSlot)i);
            float x = -total / 2f;
            for (int i = 0; i < count; i++)
            {
                float size = IconSize * SlotIcon.SizeOf((SpecialSlot)i);
                var icon = new SlotIcon(parent, (SpecialSlot)i, size, _art.Header);
                icon.Root.anchoredPosition = new Vector2(x + size / 2f, y);
                x += size + IconGap;
                card.Icons[i] = icon;
                card.IconPunchAt[i] = -10f;
            }
        }

        private CardButton BuildButton(RectTransform parent, string name, float y, string label)
        {
            var button = new CardButton();
            button.Root = Child(name, parent, new Vector2(0f, y), new Vector2(ButtonWidth, ButtonHeight));
            button.Back = AddImage(Stretch(Child("Back", button.Root)), SpecialFx.RoundBox, ButtonColour);
            button.Back.type = Image.Type.Sliced;
            RectTransform frame = Stretch(Child("Frame", button.Root));
            frame.offsetMin = new Vector2(-3f, -3f);
            frame.offsetMax = new Vector2(3f, 3f);
            button.Frame = AddImage(frame, SpecialFx.RoundFrame, TitleColour);
            button.Frame.type = Image.Type.Sliced;
            button.Frame.enabled = false;
            RectTransform text = Child("Label", button.Root, new Vector2(0f, 1f), new Vector2(ButtonWidth - 16f, ButtonHeight - 4f));
            button.Label = AddText(text, _art.Header, 22, TitleColour, TextAnchor.MiddleCenter, label, true);
            AddShadow(button.Label.gameObject);
            return button;
        }

        /// <summary>The SETTINGS panel: the keys and the skill bar, over the card while it is open.</summary>
        private void BuildSettingsPanel(Card card, int arrowIndex)
        {
            RectTransform panel = Child("SettingsPanel", card.Root, new Vector2(0f, (PanelTop + PanelBottom) / 2f),
                new Vector2(RowWidth + 24f, PanelTop - PanelBottom));
            Image back = AddImage(panel, SpecialFx.RoundBox, PanelBackColour);
            back.type = Image.Type.Sliced;
            back.color = PanelBackColour;
            RectTransform frame = Stretch(Child("Frame", panel));
            frame.offsetMin = new Vector2(-3f, -3f);
            frame.offsetMax = new Vector2(3f, 3f);
            Image frameImage = AddImage(frame, SpecialFx.RoundFrame, card.Tint);
            frameImage.type = Image.Type.Sliced;
            frameImage.color = WithAlpha(card.Tint, 0.85f);

            // Rows are placed in the card's own units, from the card's centre.
            float centre = (PanelTop + PanelBottom) / 2f;
            RectTransform title = Child("Title", panel, new Vector2(0f, PanelTitleY - centre), new Vector2(RowWidth, 40f));
            AddShadow(AddText(title, _art.Header, 30, PointsColour, TextAnchor.MiddleCenter, Lang.T("SETTINGS", "CÀI ĐẶT"), true).gameObject);
            AddImage(Child("Line", panel, new Vector2(0f, PanelTitleY - centre - 24f), new Vector2(RowWidth - 24f, 2f)), null,
                WithAlpha(PointsColour, 0.4f));

            string[] names = SettingNames;
            card.Settings = new SettingRow[SettingCount];
            for (int i = 0; i < SettingCount; i++)
            {
                var row = new SettingRow();
                bool stepped = i == BarSetting || i == SizeSetting;
                row.Root = Child("Setting" + i, panel, new Vector2(0f, SettingTop - SettingStep * i - centre), new Vector2(RowWidth, SettingStep - 6f));
                Color highlight = card.Tint;
                highlight.a = 0.22f;
                row.Highlight = AddImage(Stretch(Child("Highlight", row.Root)), SpecialFx.RoundBox, highlight);
                row.Highlight.type = Image.Type.Sliced;
                row.Highlight.color = highlight;
                row.Highlight.enabled = false;
                RectTransform name = Child("Name", row.Root, new Vector2(SettingNameX, 0f), new Vector2(SettingNameWidth, SettingStep - 12f));
                row.Name = AddText(name, _art.Header, 18, SettingNameColour, TextAnchor.MiddleLeft, names[i], true);

                RectTransform pill = Child("Pill", row.Root, new Vector2(SettingValueX, 0f), new Vector2(SettingValueWidth, SettingPillHeight));
                row.Pill = AddImage(pill, SpecialFx.RoundBox, PillColour);
                row.Pill.type = Image.Type.Sliced;
                row.Pill.color = PillColour;
                RectTransform pillFrame = Stretch(Child("Frame", pill));
                pillFrame.offsetMin = new Vector2(-2f, -2f);
                pillFrame.offsetMax = new Vector2(2f, 2f);
                row.PillFrame = AddImage(pillFrame, SpecialFx.RoundFrame, PointsColour);
                row.PillFrame.type = Image.Type.Sliced;
                row.PillFrame.enabled = false;
                RectTransform value = Child("Value", pill, Vector2.zero,
                    new Vector2(stepped ? SteppedValueWidth : SettingValueWidth - 12f, SettingPillHeight - 4f));
                row.Value = AddText(value, _art.Header, 21, Color.white, TextAnchor.MiddleCenter, "", true);
                AddShadow(row.Value.gameObject);
                card.Settings[i] = row;
            }
            card.SettingLeft = Arrow(panel, "L", arrowIndex, SettingArrowLeftX);
            card.SettingRight = Arrow(panel, "R", arrowIndex, SettingArrowRightX);
            card.SettingsPanel = panel;
            panel.gameObject.SetActive(false);
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

        /// <summary>The special picks by name, in slot order ("BLINK, PACK MULE, HEAVY BOOTS +2"); null with none.</summary>
        private static string PicksSummary(int[] specials)
        {
            if (specials == null) return null;
            var names = new List<string>();
            foreach (int pick in specials)
            {
                SpecialInfo info = SpecialCatalog.Info((Special)pick);
                if (info == null) continue;
                names.Add(info.Kind == SpecialKind.Quirk ? info.Name + " +" + info.Refund : info.Name);
            }
            return names.Count > 0 ? string.Join(", ", names.ToArray()) : null;
        }

        /// <summary>The line under the rows: what the highlighted skill, button or setting does.</summary>
        private static string DescriptionFor(int selected, CardInfo info)
        {
            if (info.SettingsOpen) return SettingDescription(info);
            if (selected < 0) return "";
            if (selected < SkillStats.Count) return SkillStats.Descriptions[selected];
            if (selected == EditRow)
                return PicksSummary(info.Specials) ?? Lang.T("No special skills yet. SELECT to choose your active, passives and quirk",
                    "Chưa có kỹ năng đặc biệt. SELECT để chọn kỹ năng chủ động, bị động và tật");
            if (selected == SettingsRow)
                return Lang.Vi
                    ? "Phím kỹ năng " + info.KeyName + ", phím xem " + info.DetailsKeyName + ", thanh " + info.BarMode + ". SELECT để đổi"
                    : "Skill key " + info.KeyName + ", info key " + info.DetailsKeyName + ", bar " + info.BarMode + ". SELECT to change";
            return "";
        }

        /// <summary>What the highlighted row of the SETTINGS panel does (or how to set it, while setting it).</summary>
        private static string SettingDescription(CardInfo info)
        {
            switch (info.Setting)
            {
                case KeySetting:
                    return info.Capturing
                        ? (info.Keyboard
                            ? Lang.T("Press any key now (Esc: cancel, Backspace: back to emote)", "Bấm phím bất kỳ ngay (Esc: hủy, Backspace: về phím biểu cảm)")
                            : Lang.T("Press any button now: shoulders, triggers, d-pad... (Start: cancel, Y: back to emote)", "Bấm nút bất kỳ ngay: vai, cò, d-pad... (Start: hủy, Y: về nút biểu cảm)"))
                        : Lang.T("The key or button that uses your active special. SELECT to change it", "Phím hoặc nút dùng kỹ năng chủ động của bạn. SELECT để đổi");
                case DetailsSetting:
                    string detailsKey = info.DetailsKeyName;
                    return info.Capturing
                        ? (info.Keyboard
                            ? Lang.T("Press any key now (Esc: cancel, Backspace: no key)", "Bấm phím bất kỳ ngay (Esc: hủy, Backspace: bỏ phím)")
                            : Lang.T("Press any button now (Start: cancel, the same button again: none)", "Bấm nút bất kỳ ngay (Start: hủy, bấm lại nút cũ: bỏ nút)"))
                        : (Lang.Vi ? "Trong bếp, " + (detailsKey == null || detailsKey == "KHÔNG CÓ" || detailsKey == "NONE" ? "một phím" : detailsKey) + " hiện hoặc ẩn chi tiết kỹ năng của bạn. SELECT để đổi" : "In the kitchen, " + (detailsKey == null || detailsKey == "NONE" ? "a key" : detailsKey) + " shows or hides your skills' details. SELECT to change it");
                case BarSetting:
                    return Lang.T("Your skill bar in the kitchen: on screen, over your chef or hidden. LEFT / RIGHT to change",
                        "Thanh kỹ năng của bạn trong bếp: trên màn hình, trên đầu đầu bếp hoặc ẩn. TRÁI / PHẢI để đổi");
                case SizeSetting:
                    return Lang.T("How big your skill bar is, on screen and over your head. LEFT / RIGHT to change",
                        "Cỡ thanh kỹ năng của bạn, trên màn hình và trên đầu. TRÁI / PHẢI để đổi");
                default:
                    return info.Placing
                        ? Lang.T("Move the bar with the directions. SELECT: done, BACK: cancel, HIDE: back to automatic",
                            "Di chuyển thanh bằng phím hướng. SELECT: xong, BACK: hủy, ẨN: về tự động")
                        : Lang.T("Where your bar sits on screen: automatic, or anywhere you put it. SELECT to move it",
                            "Vị trí thanh trên màn hình: tự động, hoặc nơi bạn đặt. SELECT để di chuyển");
            }
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

            // The directions aren't listed: moving with them is a given.
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UISelectNotStart, player), 42f, "SELECT", _art);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("PICK", "CHỌN"));
            Spacer(row.transform, 18f);
            AddLockInIcon(row.transform, player);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("LOCK IN", "CHỐT"));
            Spacer(row.transform, 18f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UICancel, player), 42f, "BACK", _art);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("UNDO", "BỎ CHỐT"));
            Spacer(row.transform, 18f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UIChangeMode, player), 42f, "TAB", _art);
            AddLabel(row.transform, _art.Header, 28, TitleColour, Lang.T("HIDE", "ẨN"));
            Spacer(row.transform, 18f);
            AddIcon(row.transform, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UISkip, player), 42f, "SKIP", _art);
            Text skip = AddLabel(row.transform, _art.Header, 28, TitleColour, skipLabel);

            // Shown instead of everything else while the screen is hidden to look at the kitchen.
            RectTransform peekBar = Child("Peek", _root.transform, Vector2.zero, new Vector2(620f, 64f));
            peekBar.anchorMin = peekBar.anchorMax = new Vector2(0.5f, 1f);
            peekBar.anchoredPosition = new Vector2(0f, PeekHiddenY);
            _peekGroup = peekBar.gameObject.AddComponent<CanvasGroup>();
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

        /// <summary>
        /// Hides the dialog and the dimming (leaving a small "show skills" bar), or shows them
        /// again. Both are animated by <see cref="Render"/>, starting from wherever the last
        /// change had got to, so a quick hide-and-show doesn't jump.
        /// </summary>
        internal void SetHidden(bool hidden)
        {
            if (!IsAlive || _closing || _hidden == hidden) return;
            float now = Time.unscaledTime;
            _shownFrom = ShownAmount(now);
            _changedAt = now;
            _hidden = hidden;
        }

        /// <summary>How far shown the dialog is: 0 hidden, 1 fully shown.</summary>
        private float ShownAmount(float now)
        {
            float seconds = _hidden ? HideSeconds : OpenSeconds;
            float progress = Mathf.Clamp01((now - _changedAt) / seconds);
            return Mathf.Lerp(_shownFrom, _hidden ? 0f : 1f, progress);
        }

        /// <summary>
        /// Poses the dialog, the dimming and the "show skills" bar for <paramref name="shown"/>:
        /// opening pops the dialog in (grow with a little overshoot, fade in); hiding shrinks and
        /// fades it while the bar drops in from the top.
        /// </summary>
        private void ApplyShown(float shown, float now)
        {
            bool showing = !_hidden;
            float fade = showing ? Mathf.Clamp01(shown * 2f) : shown;
            float scale = Mathf.LerpUnclamped(HiddenScale, 1f, showing ? EaseOutBack(shown) : shown);
            _dialog.localScale = new Vector3(scale, scale, 1f);
            if (_dialogGroup != null) _dialogGroup.alpha = fade;
            bool dialogOn = showing || shown > 0f;
            if (_dialog.gameObject.activeSelf != dialogOn) _dialog.gameObject.SetActive(dialogOn);
            Color dim = DimColour;
            dim.a = DimAlpha * fade;
            _dim.color = dim;

            if (_peek == null) return;
            // The bar comes in as the dialog goes, and leaves quickly when it comes back.
            float peek = showing ? Mathf.Clamp01(1f - shown * 3f) : 1f - shown;
            bool peekOn = peek > 0f;
            if (_peek.activeSelf != peekOn) _peek.SetActive(peekOn);
            if (!peekOn) return;
            RectTransform bar = (RectTransform)_peek.transform;
            bar.anchoredPosition = new Vector2(0f, Mathf.LerpUnclamped(PeekHiddenY, PeekY, EaseOutBack(peek)));
            bar.localScale = Vector3.one * (1f + 0.04f * Mathf.Sin(now * 3f));
            if (_peekGroup != null) _peekGroup.alpha = Mathf.Clamp01(peek * 1.5f);
        }

        /// <summary>
        /// Fades the screen out (shrinking the dialog a little), then destroys it. The screen is
        /// finished as far as the caller is concerned: it is no longer alive and draws nothing new.
        /// </summary>
        internal void Close()
        {
            if (_root == null) return;
            if (_closing) return;
            if (!_root.activeInHierarchy)
            {
                Destroy();
                return;
            }
            ShowPreviews(new List<BarPreview>());
            _closing = true;
            _closedAt = Time.unscaledTime;
            _closeDialogAlpha = _dialog != null && _dialog.gameObject.activeSelf && _dialogGroup != null ? _dialogGroup.alpha : 0f;
            _closeScale = _dialog != null ? _dialog.localScale.x : 1f;
            _closeDim = _dim != null ? _dim.color.a : 0f;
            _closePeekAlpha = _peek != null && _peek.activeSelf && _peekGroup != null ? _peekGroup.alpha : 0f;
            _skipTrack = null;
            _root.AddComponent<CloseFade>().View = this;
        }

        /// <summary>One frame of the close fade; true once it has finished.</summary>
        private bool StepClose()
        {
            float progress = Mathf.Clamp01((Time.unscaledTime - _closedAt) / CloseSeconds);
            float left = 1f - progress * progress;
            if (_dialog != null)
            {
                float scale = Mathf.Lerp(ClosedScale * _closeScale, _closeScale, left);
                _dialog.localScale = new Vector3(scale, scale, 1f);
            }
            if (_dialogGroup != null) _dialogGroup.alpha = _closeDialogAlpha * left;
            if (_dim != null)
            {
                Color dim = DimColour;
                dim.a = _closeDim * left;
                _dim.color = dim;
            }
            if (_peekGroup != null) _peekGroup.alpha = _closePeekAlpha * left;
            return progress >= 1f;
        }

        /// <summary>Runs the close fade on the screen's own object (the screen's loop has ended by then).</summary>
        private sealed class CloseFade : MonoBehaviour
        {
            internal SkillSelectView View;

            private void Update()
            {
                bool done;
                try
                {
                    done = View == null || View.StepClose();
                }
                catch (Exception error)
                {
                    Plugin.Log.LogDebug("Màn chọn kỹ năng không mờ dần được: " + error.Message);
                    done = true;
                }
                if (!done) return;
                if (View != null) View.Destroy();
                else Object.Destroy(gameObject);
                View = null;
            }
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

            ApplyShown(ShownAmount(now), now);

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
                    // Only the levels this skill can really go down to get a pip (a quirk that
                    // takes a skill away stops it at 0, and so may a MinLevel set in the config).
                    int floor = SkillStats.MinLevel((Skill)s, known ? info.Specials : null);
                    ShowLevel(card.Rows[s], (Skill)s, level, floor, card.Initialised, now);
                    card.Rows[s].Highlight.enabled = s == selected;
                    card.Rows[s].Root.localScale = Punch(now - card.Rows[s].PunchAt, 0.06f);
                }
                for (int s = 0; s < card.Icons.Length; s++)
                {
                    int pick = known && info.Specials != null && s < info.Specials.Length ? info.Specials[s] : 0;
                    if (card.Icons[s].Show((Special)pick) && card.Initialised) card.IconPunchAt[s] = now;
                    card.Icons[s].Root.localScale = Punch(now - card.IconPunchAt[s], 0.15f);
                }
                if (card.IconsHighlight != null) card.IconsHighlight.enabled = selected >= 0 && selected == EditRow;
                ShowButton(card.EditButton, selected >= 0 && selected == EditRow, pulse);
                ShowButton(card.SettingsButton, selected >= 0 && selected == SettingsRow, pulse);
                card.RowsGroup.alpha = known ? 1f : 0.35f;

                // What the highlighted row does (it changes as the pick under it changes), or for
                // a moment why the lock-in was refused. Other players' cards, and locked-in ones,
                // name their specials (the icons alone don't).
                float deniedAge = now - info.DeniedAt;
                string about = !string.IsNullOrEmpty(info.DeniedReason) && deniedAge >= 0f && deniedAge < 2f
                    ? info.DeniedReason
                    : choosing ? DescriptionFor(selected, info) : known ? PicksSummary(info.Specials) ?? "" : "";
                float noticeAge = now - info.NoticeAt;
                if (!string.IsNullOrEmpty(info.Notice) && noticeAge >= 0f && noticeAge < 2.5f && !info.Capturing) about = info.Notice;
                ShowSettings(card, info, choosing, pulse, now);
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
                // Lowering skills past the cap frees nothing more: say so once it's reached.
                bool capped = known && info.Levels != null && LoweredBy(info.Levels) >= SkillStats.MaxPointsFreed;
                string pointsText = PointsText(free, capped);
                if (!card.Initialised || card.ShownFree != free || card.Points.text != pointsText)
                {
                    card.ShownFree = free;
                    card.Points.text = pointsText;
                }
                float denied = now - info.DeniedAt;
                bool flash = denied >= 0f && denied < DeniedSeconds;
                card.Points.color = flash || free < 0 ? NerfColour : free == 0 ? BuffColour : PointsColour;
                card.Points.rectTransform.localScale = Punch(denied * PunchSeconds / DeniedSeconds, 0.18f);

                // The lock-in part always says LOCK IN (or what another player is doing); SELECT's
                // own button is shown with what it does, only on rows where it does something.
                card.HintText.text = HintFor(card.IsLocal, state);
                string pickHint = choosing ? PickHint(selected, info) : null;
                if (card.PickPart != null)
                {
                    if (card.PickPart.activeSelf != (pickHint != null)) card.PickPart.SetActive(pickHint != null);
                    if (pickHint != null && card.PickText.text != pickHint) card.PickText.text = pickHint;
                }
                // While the SETTINGS panel is open, BACK closes it; LOCK IN waits until then.
                bool settingsOpen = choosing && info.SettingsOpen;
                if (card.ClosePart != null && card.ClosePart.activeSelf != settingsOpen) card.ClosePart.SetActive(settingsOpen);
                if (card.ClosePart != null && card.LockGroup.gameObject.activeSelf == settingsOpen) card.LockGroup.gameObject.SetActive(!settingsOpen);
                // Locking in only works at exactly 0 points, and not while setting a key or placing the bar.
                card.LockGroup.alpha = choosing && (free != 0 || info.Capturing || info.Placing) ? 0.4f : 1f;
                card.HintGroup.alpha = 1f;
                if (!card.IsLocal && (state == SkillSync.CardState.Choosing || state == SkillSync.CardState.Loading))
                {
                    // Other players' "choosing..." / "loading..." breathe so it reads as live.
                    Color hint = NeutralColour;
                    hint.a = pulse;
                    card.HintText.color = hint;
                }
                else card.HintText.color = NeutralColour;

                if (card.Left != null) ShowArrows(card, info, selected, free, now);
                ScrollRows(card, selected, now);
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
            // Only the skill rows step with Left/Right; the buttons use SELECT.
            bool show = selected >= 0 && selected < SkillStats.Count && !info.SettingsOpen;
            card.Left.enabled = show;
            card.Right.enabled = show;
            if (!show) return;
            float y = RowY(selected);
            int max = SkillStats.MaxLevel;
            int level = info.Levels[selected];
            bool canLower = level > SkillStats.MinLevel((Skill)selected, info.Specials);
            bool canRaise = level < max;
            PlaceArrows(card.Left, card.Right, card.Tint, ArrowLeftX, ArrowRightX, y, canLower, canRaise, now);
        }

        /// <summary>
        /// A pair of arrows either side of a value, nudging outwards like the game's chef-select
        /// arrows, and faded where there's no further to go.
        /// </summary>
        private static void PlaceArrows(Image left, Image right, Color tint, float leftX, float rightX, float y,
            bool canLower, bool canRaise, float now)
        {
            float nudge = 3f * Mathf.Abs(Mathf.Sin(now * 4f));
            left.rectTransform.anchoredPosition = new Vector2(leftX - nudge, y);
            right.rectTransform.anchoredPosition = new Vector2(rightX + nudge, y);
            left.color = new Color(1f, 1f, 1f, canLower ? 1f : 0.3f);
            right.color = new Color(1f, 1f, 1f, canRaise ? 1f : 0.3f);
            if (left.sprite == null) left.color = WithAlpha(tint, left.color.a);
            if (right.sprite == null) right.color = WithAlpha(tint, right.color.a);
        }

        /// <summary>A card button: brighter, framed and pulsing while it is the highlighted row.</summary>
        private static void ShowButton(CardButton button, bool selected, float pulse)
        {
            if (button == null) return;
            button.Back.color = selected ? new Color(0.22f, 0.32f, 0.48f, 0.95f) : ButtonColour;
            button.Frame.enabled = selected;
            if (selected) button.Frame.color = Color.Lerp(PointsColour, Color.white, 0.35f * pulse);
            button.Label.color = selected ? Color.white : NeutralColour;
            float grow = selected ? 1.04f : 1f;
            button.Root.localScale = new Vector3(grow, grow, 1f);
        }

        /// <summary>
        /// The SETTINGS panel over a local card: shown while it is open (popping in), with every
        /// setting's value, the highlighted one framed and the one being set pulsing.
        /// </summary>
        private static void ShowSettings(Card card, CardInfo info, bool choosing, float pulse, float now)
        {
            if (card.SettingsPanel == null) return;
            bool open = choosing && info.SettingsOpen;
            if (open != card.SettingsShown)
            {
                card.SettingsShown = open;
                card.SettingsPanel.gameObject.SetActive(open);
                if (open) card.SettingsOpenedAt = now;
            }
            if (!open) return;
            float age = Mathf.Clamp01((now - card.SettingsOpenedAt) / 0.18f);
            float scale = Mathf.LerpUnclamped(0.92f, 1f, EaseOutBack(age));
            card.SettingsPanel.localScale = new Vector3(scale, scale, 1f);

            string[] values =
            {
                info.Capturing && !info.CapturingDetails ? Lang.T("PRESS A KEY...", "BẤM PHÍM...") : info.KeyName,
                info.Capturing && info.CapturingDetails ? Lang.T("PRESS A KEY...", "BẤM PHÍM...") : info.DetailsKeyName,
                info.BarMode, info.BarSize,
                info.Placing ? Lang.T("MOVING...", "ĐANG DI CHUYỂN...") : info.BarSpot,
            };
            bool[] waiting = { info.Capturing && !info.CapturingDetails, info.Capturing && info.CapturingDetails, false, false, info.Placing };
            for (int s = 0; s < card.Settings.Length && s < values.Length; s++)
            {
                SettingRow setting = card.Settings[s];
                bool here = s == info.Setting;
                setting.Highlight.enabled = here;
                setting.Name.color = here ? TitleColour : SettingNameColour;
                setting.Pill.color = here ? PillSelectedColour : PillColour;
                setting.PillFrame.enabled = here;
                if (here) setting.PillFrame.color = Color.Lerp(PointsColour, Color.white, 0.35f * pulse);
                string value = values[s] ?? "";
                if (setting.Value.text != value)
                {
                    if (setting.Value.text != "" && !waiting[s]) setting.PunchAt = now;
                    setting.Value.text = value;
                }
                setting.Value.color = waiting[s] ? WithAlpha(PointsColour, pulse) : value == "-" ? NeutralColour : TitleColour;
                setting.Root.localScale = Punch(now - setting.PunchAt, 0.06f);
            }

            // DISPLAY and BAR SIZE step with Left/Right: their arrows sit either side of the value.
            bool stepped = !info.Placing && (info.Setting == BarSetting || info.Setting == SizeSetting);
            card.SettingLeft.enabled = stepped;
            card.SettingRight.enabled = stepped;
            if (!stepped) return;
            bool canLower = true, canRaise = true; // the display modes wrap round
            if (info.Setting == SizeSetting)
            {
                canLower = info.BarSizePercent > SkillBarSettings.MinSize;
                canRaise = info.BarSizePercent < SkillBarSettings.MaxSize;
            }
            float y = card.Settings[info.Setting].Root.anchoredPosition.y;
            PlaceArrows(card.SettingLeft, card.SettingRight, card.Tint, SettingArrowLeftX, SettingArrowRightX, y, canLower, canRaise, now);
        }

        /// <summary>
        /// A skill's level as pips: one per level it can go to, red to the left of the 0 mark and
        /// green to the right. Pips below the skill's floor aren't drawn at all.
        /// </summary>
        private static void ShowLevel(Row row, Skill skill, int level, int floor, bool animate, float now)
        {
            int max = SkillStats.MaxLevel;
            level = Mathf.Clamp(level, -max, max);
            if (row.Shown == level && row.ShownFloor == floor) return;
            if (animate && row.Shown != level) row.PunchAt = now;
            row.Shown = level;
            row.ShownFloor = floor;
            for (int k = 0; k < max; k++)
            {
                row.Minus[k].enabled = k < -floor;
                row.Minus[k].color = -level > k ? NerfColour : EmptyPipColour;
                row.Plus[k].color = level > k ? BuffColour : EmptyPipColour;
            }
            int percent = SkillStats.PercentChange(skill, level);
            row.Percent.text = level == 0 || percent == 0 ? "" : (percent > 0 ? "+" : "-") + Mathf.Abs(percent) + "%";
            row.Percent.color = percent > 0 ? BuffColour : NerfColour;
        }

        private static string PointsText(int free, bool capped)
        {
            if (free == UnknownPoints) return "";
            string text;
            if (free == 0) text = Lang.T("BALANCED", "ĐÃ CÂN BẰNG");
            else if (free > 0) text = Lang.Vi ? "CÒN " + free + " ĐIỂM ĐỂ CỘNG" : free == 1 ? "1 POINT TO SPEND" : free + " POINTS TO SPEND";
            else text = Lang.Vi ? "LỐ " + -free + " ĐIỂM" : free == -1 ? "1 POINT OVER" : -free + " POINTS OVER";
            if (!capped) return text;
            int max = SkillStats.MaxPointsFreed;
            return text + (Lang.Vi ? "  ·  HẠ TỐI ĐA " + max + " ĐIỂM" : "  ·  MAX " + max + " FREED");
        }

        /// <summary>How many levels below 0 the build has in all (before the cap on freed points).</summary>
        private static int LoweredBy(int[] levels)
        {
            int lowered = 0;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] < 0) lowered -= levels[i];
            return lowered;
        }

        /// <summary>What SELECT does on the highlighted row, or null where it does nothing (skill rows).</summary>
        private static string PickHint(int selected, CardInfo info)
        {
            if (info.SettingsOpen)
            {
                switch (info.Setting)
                {
                    case KeySetting:
                    case DetailsSetting:
                        return info.Capturing ? Lang.T("WAITING...", "ĐANG CHỜ...") : Lang.T("SET KEY", "ĐỔI PHÍM");
                    case BarSetting:
                    case SizeSetting:
                        return Lang.T("CHANGE", "ĐỔI");
                    default:
                        return info.Placing ? Lang.T("DONE", "XONG") : Lang.T("MOVE", "DI CHUYỂN");
                }
            }
            if (selected < SkillStats.Count) return null;
            if (selected == SettingsRow) return Lang.T("OPEN", "MỞ");
            return Lang.T("CHOOSE", "CHỌN");
        }

        /// <summary>
        /// Scrolls the card's rows so the highlighted one (and a row either side) stays in the
        /// window, easing there; the little arrows show when more rows are above or below.
        /// </summary>
        private static void ScrollRows(Card card, int selected, float now)
        {
            if (card.Content == null) return;
            float max = Mathf.Max(0f, ViewBottom - RowsBottom);
            if (selected >= 0)
            {
                float y = RowCentre(selected);
                float low = ViewBottom + RowStep / 2f + ScrollMargin - y;
                float high = ViewTop - RowStep / 2f - ScrollMargin - y;
                card.ScrollTarget = Mathf.Clamp(card.ScrollTarget, low, Mathf.Max(low, high));
            }
            else card.ScrollTarget = 0f;
            card.ScrollTarget = Mathf.Clamp(card.ScrollTarget, 0f, max);
            card.Scroll = card.Initialised
                ? Mathf.Lerp(card.Scroll, card.ScrollTarget, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f))
                : card.ScrollTarget;
            card.Content.anchoredPosition = new Vector2(0f, -(ViewTop + ViewBottom) / 2f + card.Scroll);
            if (card.MoreUp != null) card.MoreUp.enabled = card.Scroll > 2f;
            if (card.MoreDown != null) card.MoreDown.enabled = card.Scroll < max - 2f;
            float bob = 2f * Mathf.Sin(now * 4f);
            if (card.MoreUp != null) card.MoreUp.rectTransform.anchoredPosition = new Vector2(ScrollMarkX, ViewTop - 8f + bob);
            if (card.MoreDown != null) card.MoreDown.rectTransform.anchoredPosition = new Vector2(ScrollMarkX, ViewBottom + 8f - bob);
        }

        // The scroll arrows sit at the right edge of the card's dark interior.
        private const float ScrollMarkX = 138f;

        /// <summary>A small arrow (the game's menu arrow, turned <paramref name="angle"/>) at the rows' window edge.</summary>
        private Image ScrollMark(RectTransform parent, float y, float angle)
        {
            Sprite arrow = _art.Get("t_ui_arrow_left_standard_01");
            RectTransform rect = Child("More", parent, new Vector2(ScrollMarkX, y), new Vector2(16f, 16f));
            Image image = AddImage(rect, arrow, TitleColour);
            image.color = arrow != null ? new Color(1f, 1f, 1f, 0.85f) : WithAlpha(TitleColour, 0.6f);
            image.preserveAspect = true;
            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
            image.enabled = false;
            return image;
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

        /// <summary>
        /// The LOCK IN button's icon: "[ENTER]" on the keyboard, otherwise the player's own lock-in
        /// button (X on a whole controller; a free button on half of a shared one).
        /// </summary>
        private void AddLockInIcon(Transform row, PlayerInputLookup.Player player)
        {
            if (SpecialInput.IsKeyboard(player))
            {
                AddIcon(row, null, 42f, "ENTER", _art);
                return;
            }
            ControlPadInput.Button button = SpecialInput.LockInPadButton(player);
            if (button != ControlPadInput.Button.Invalid)
                AddIcon(row, GameArt.PadIcon(button, player), 42f, SpecialInput.ButtonName(button), _art);
            else
                AddIcon(row, GameArt.ButtonIcon(PlayerInputLookup.LogicalButtonID.UIResultsToggleProfile, player), 42f, "X", _art);
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

        private static void Spacer(Transform row, float width)
        {
            LayoutElement layout = Child("Gap", row).gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = width;
            layout.preferredHeight = 1f;
        }
    }
}
