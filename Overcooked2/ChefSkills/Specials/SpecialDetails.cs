using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// The skill details panel (toggled with [Controls] DetailsKey, Tab by default): a narrow,
    /// see-through card docked to the side of the screen, so the kitchen in the middle stays in
    /// view. For each chef played on this machine it lists the active, passives and quirk: icon,
    /// name, slot (cooldown, points, team-wide) and what it does. One local player docks left;
    /// with more, they alternate left and right and stack. It slides and fades in and out, and
    /// hides with the skill bar (skill screen, pause menu).
    /// </summary>
    internal sealed class SpecialDetails
    {
        private const float CardWidth = 400f;
        private const float EdgeMargin = 22f;
        private const float StackGap = 16f;
        private const float Pad = 16f;
        private const float IconSize = 46f;
        private const float TextX = Pad + IconSize + 12f;
        private const float FadeSeconds = 0.18f;
        private const float SlideDistance = 60f;
        // Keep clear of the order bar (top) and the score / timer (bottom).
        private const float CentreY = -30f;

        private static readonly Color PanelColour = new Color(0.05f, 0.07f, 0.12f, 0.8f);
        private static readonly Color HeaderColour = new Color(0.66f, 0.72f, 0.82f, 1f);
        private static readonly Color DescColour = new Color(0.93f, 0.95f, 0.98f, 1f);
        private static readonly Color TagColour = new Color(0.6f, 0.66f, 0.76f, 1f);
        private static readonly Color QuirkColour = new Color(1f, 0.58f, 0.54f, 1f);
        private static readonly Color DividerColour = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color ShadowColour = new Color(0f, 0f, 0f, 0.55f);

        private readonly RectTransform _root;
        private readonly CanvasGroup _group;
        private readonly int _layer;
        private readonly List<RectTransform> _cards = new List<RectTransform>();
        private readonly List<bool> _cardRight = new List<bool>();
        private string _builtFor;
        // 0 = closed, 1 = open; eases toward the wanted state.
        private float _shown;

        internal bool Open { get; private set; }

        internal SpecialDetails(RectTransform canvas)
        {
            _layer = canvas.gameObject.layer;
            _root = Rect("Details", canvas, Vector2.zero, Vector2.one);
            _root.offsetMin = _root.offsetMax = Vector2.zero;
            _group = _root.gameObject.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;
            _group.alpha = 0f;
            _root.gameObject.SetActive(false);
        }

        internal void Toggle()
        {
            Open = !Open;
            try
            {
                GameUtils.TriggerAudio(Open ? GameOneShotAudioTag.UISelect : GameOneShotAudioTag.UIBack, LayerMask.NameToLayer("UI"));
            }
            catch (System.Exception)
            {
            }
        }

        /// <summary>Every frame while the skill bar shows: (re)builds the cards and runs the transition.</summary>
        internal void Update(IList<ChefState> chefs)
        {
            if (_root == null) return;
            bool want = Open && chefs.Count > 0;
            float step = Time.unscaledDeltaTime / FadeSeconds;
            _shown = Mathf.Clamp01(_shown + (want ? step : -step));
            bool active = _shown > 0f;
            if (_root.gameObject.activeSelf != active) _root.gameObject.SetActive(active);
            if (!active) return;

            if (chefs.Count > 0)
            {
                string key = Signature(chefs);
                if (key != _builtFor)
                {
                    Build(chefs);
                    _builtFor = key;
                }
            }

            // Eased: slides in from its edge and fades in; the same way back out.
            float t = _shown * _shown * (3f - 2f * _shown);
            _group.alpha = t;
            float slide = (1f - t) * SlideDistance;
            for (int i = 0; i < _cards.Count; i++)
            {
                RectTransform card = _cards[i];
                if (card == null) continue;
                Vector2 at = card.anchoredPosition;
                at.x = _cardRight[i] ? -EdgeMargin + slide : EdgeMargin - slide;
                card.anchoredPosition = at;
            }
        }

        private static string Signature(IList<ChefState> chefs)
        {
            var text = new StringBuilder();
            foreach (ChefState state in chefs)
            {
                text.Append(state.GetHashCode()).Append(':');
                for (int i = 0; i < 4; i++) text.Append(state.Loadout[i]).Append(',');
                text.Append('|');
            }
            text.Append(SpecialInput.DetailsKeyName()).Append(Lang.Vi ? "|vi" : "|en");
            return text.ToString();
        }

        // --- building ---------------------------------------------------------------------------

        private void Build(IList<ChefState> chefs)
        {
            for (int i = _root.childCount - 1; i >= 0; i--) Object.Destroy(_root.GetChild(i).gameObject);
            _cards.Clear();
            _cardRight.Clear();

            // Player 1 left, player 2 right, 3 left under 1, 4 right under 2.
            var heights = new float[chefs.Count];
            for (int i = 0; i < chefs.Count; i++)
            {
                bool right = i % 2 == 1;
                _cards.Add(BuildCard(chefs[i], right, chefs.Count > 1, out heights[i]));
                _cardRight.Add(right);
            }
            for (int side = 0; side < 2; side++)
            {
                float total = 0f;
                int count = 0;
                for (int i = side; i < chefs.Count; i += 2)
                {
                    total += heights[i];
                    count++;
                }
                total += Mathf.Max(0, count - 1) * StackGap;
                float top = CentreY + total * 0.5f;
                for (int i = side; i < chefs.Count; i += 2)
                {
                    _cards[i].anchoredPosition = new Vector2(_cards[i].anchoredPosition.x, top);
                    top -= heights[i] + StackGap;
                }
            }
        }

        private RectTransform BuildCard(ChefState state, bool right, bool several, out float height)
        {
            var anchor = new Vector2(right ? 1f : 0f, 0.5f);
            RectTransform card = Rect("Card", _root, anchor, anchor);
            card.pivot = new Vector2(right ? 1f : 0f, 1f);
            Image back = card.gameObject.AddComponent<Image>();
            back.raycastTarget = false;
            back.sprite = SpecialFx.RoundBox;
            back.type = Image.Type.Sliced;
            back.color = PanelColour;

            float y = Pad;

            // Header: whose skills, and the key that closes it.
            string title = Lang.T("YOUR SKILLS", "KỸ NĂNG CỦA BẠN");
            if (several && state.Provider != null) title = Lang.T("PLAYER ", "NGƯỜI CHƠI ") + ((int)state.Provider.GetID() + 1);
            Text header = MakeText(card, SpecialFx.Font, 17, TextAnchor.UpperLeft, CardWidth - 2f * Pad);
            header.text = title;
            header.color = HeaderColour;
            Place(header.rectTransform, Pad, y);
            string keyName = SpecialInput.DetailsKeyName();
            if (keyName != null)
            {
                Text close = MakeText(card, SpecialFx.Font, 15, TextAnchor.UpperRight, CardWidth - 2f * Pad);
                close.text = Lang.Vi ? keyName + " ĐỂ ĐÓNG" : keyName + " TO CLOSE";
                close.color = TagColour;
                Place(close.rectTransform, Pad, y + 2f);
            }
            y += 26f;

            bool first = true;
            for (int slot = 0; slot < 4; slot++)
            {
                var special = (Special)state.Loadout[slot];
                if (special == Special.None) continue;
                SpecialInfo info = SpecialCatalog.Info(special);
                if (info == null) continue;

                // A thin line between picks.
                if (!first)
                {
                    RectTransform line = Rect("Divider", card, new Vector2(0f, 1f), new Vector2(0f, 1f));
                    Image divider = line.gameObject.AddComponent<Image>();
                    divider.raycastTarget = false;
                    divider.color = DividerColour;
                    Place(line, Pad, y + 8f);
                    line.sizeDelta = new Vector2(CardWidth - 2f * Pad, 1f);
                    y += 17f;
                }
                else y += 6f;
                first = false;

                float rowTop = y;
                Color colour = SpecialFx.ColourOf(special);

                RectTransform icon = Rect("Icon", card, new Vector2(0f, 1f), new Vector2(0f, 1f));
                Image disc = icon.gameObject.AddComponent<Image>();
                disc.raycastTarget = false;
                disc.sprite = SpecialFx.Disc;
                disc.color = colour;
                Place(icon, Pad, rowTop);
                icon.sizeDelta = new Vector2(IconSize, IconSize);
                Text glyph = MakeText(icon, SpecialFx.Font, 18, TextAnchor.MiddleCenter, IconSize);
                glyph.rectTransform.anchorMin = glyph.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                glyph.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                glyph.rectTransform.anchoredPosition = Vector2.zero;
                AddShadow(glyph);
                RectTransform pictureRect = Rect("Picture", icon, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                pictureRect.sizeDelta = new Vector2(IconSize * 0.9f, IconSize * 0.9f);
                Image picture = pictureRect.gameObject.AddComponent<Image>();
                picture.raycastTarget = false;
                picture.preserveAspect = true;
                SpecialFx.ShowIcon(picture, glyph, special);

                float textWidth = CardWidth - TextX - Pad;
                Text name = MakeText(card, SpecialFx.Font, 21, TextAnchor.UpperLeft, textWidth);
                name.text = info.Name;
                name.color = Color.Lerp(colour, Color.white, 0.6f);
                AddShadow(name);
                Place(name.rectTransform, TextX, rowTop);

                Text tag = MakeText(card, SpecialFx.Font, 14, TextAnchor.UpperLeft, textWidth);
                tag.text = TagFor(slot, info);
                tag.color = slot == 3 ? QuirkColour : TagColour;
                Place(tag.rectTransform, TextX, rowTop + 24f);

                Text desc = MakeText(card, SpecialFx.BodyFont, 18, TextAnchor.UpperLeft, textWidth);
                desc.horizontalOverflow = HorizontalWrapMode.Wrap;
                desc.lineSpacing = 1.05f;
                desc.text = info.Description;
                desc.color = DescColour;
                AddShadow(desc);
                float descHeight = Mathf.Max(20f, desc.preferredHeight);
                desc.rectTransform.sizeDelta = new Vector2(textWidth, descHeight);
                Place(desc.rectTransform, TextX, rowTop + 44f);

                y = Mathf.Max(rowTop + IconSize, rowTop + 44f + descHeight);
            }

            height = y + Pad;
            card.sizeDelta = new Vector2(CardWidth, height);
            return card;
        }

        private static string TagFor(int slot, SpecialInfo info)
        {
            string tag = SpecialCatalog.SlotNames[slot];
            if (slot == 0 && info.Cooldown > 0f && info.Cooldown < 999f) tag += "  -  " + (Lang.Vi ? "HỒI CHIÊU " + Mathf.RoundToInt(info.Cooldown) + " S" : Mathf.RoundToInt(info.Cooldown) + " S COOLDOWN");
            if (slot == 0 && info.Cooldown >= 999f) tag += "  -  " + Lang.T("ONCE PER LEVEL", "MỖI MÀN MỘT LẦN");
            if (slot == 3 && info.Refund > 0) tag += "  -  +" + info.Refund + Lang.T(" POINTS", " ĐIỂM");
            if (info.TeamWide) tag += "  -  " + Lang.T("WHOLE TEAM", "CẢ ĐỘI");
            return tag;
        }

        private static void Place(RectTransform rect, float x, float y)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
        }

        private RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = _layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        private Text MakeText(RectTransform parent, Font font, int size, TextAnchor alignment, float width)
        {
            RectTransform rect = Rect("Text", parent, new Vector2(0f, 1f), new Vector2(0f, 1f));
            rect.sizeDelta = new Vector2(width, size + 8f);
            rect.pivot = new Vector2(0f, 1f);
            Text text = rect.gameObject.AddComponent<Text>();
            text.raycastTarget = false;
            text.supportRichText = false;
            text.font = font;
            text.fontSize = size;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = Color.white;
            return text;
        }

        private static void AddShadow(Text text)
        {
            Shadow shadow = text.gameObject.AddComponent<Shadow>();
            shadow.effectColor = ShadowColour;
            shadow.effectDistance = new Vector2(1f, -1.5f);
        }
    }
}
