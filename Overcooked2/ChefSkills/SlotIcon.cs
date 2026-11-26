using UnityEngine;
using UnityEngine.UI;

namespace ChefSkills
{
    /// <summary>
    /// A special pick drawn in its slot's own style, the same on the skill card and along the top
    /// of the picker, so the kinds tell apart at a glance: the active is the biggest, a disc with a
    /// gold ring and a soft gold glow; a passive is a smaller disc with a green ring; the quirk is a
    /// rounded square with a red frame, and a red tag with the points it frees. The fill is the
    /// pick's category colour. An empty slot keeps its shape and border, faded, with a "+".
    /// </summary>
    internal sealed class SlotIcon
    {
        internal static readonly Color ActiveColour = new Color(1f, 0.8f, 0.28f, 1f);
        internal static readonly Color PassiveColour = new Color(0.52f, 0.95f, 0.46f, 1f);
        internal static readonly Color QuirkColour = new Color(1f, 0.46f, 0.4f, 1f);
        private static readonly Color EmptyFill = new Color(0.06f, 0.09f, 0.15f, 0.8f);
        private static readonly Color ShadowColour = new Color(0.08f, 0.14f, 0.22f, 0.65f);

        internal readonly RectTransform Root;
        private readonly SpecialSlot _slot;
        private readonly Image _glow, _fill, _picture, _border;
        private readonly Text _glyph, _refund;
        private readonly GameObject _refundTag;
        private int _shown = -1;

        /// <summary>The colour that marks a slot's kind: gold active, green passive, red quirk.</summary>
        internal static Color ColourOf(SpecialSlot slot)
        {
            return slot == SpecialSlot.Active ? ActiveColour : slot == SpecialSlot.Quirk ? QuirkColour : PassiveColour;
        }

        /// <summary>How big a slot's icon is, as a share of the active's.</summary>
        internal static float SizeOf(SpecialSlot slot)
        {
            return slot == SpecialSlot.Active ? 1f : 0.82f;
        }

        /// <param name="size">The icon's fill, across (its border sits just outside it).</param>
        internal SlotIcon(Transform parent, SpecialSlot slot, float size, Font font)
        {
            _slot = slot;
            bool quirk = slot == SpecialSlot.Quirk;
            Color colour = ColourOf(slot);
            Root = Child("Slot" + (int)slot, parent, Vector2.zero, new Vector2(size, size));

            if (slot == SpecialSlot.Active)
            {
                _glow = AddImage(Child("Glow", Root, Vector2.zero, new Vector2(size * 1.7f, size * 1.7f)), SpecialFx.Glow);
                _glow.color = WithAlpha(colour, 0.45f);
            }
            _fill = AddImage(Child("Fill", Root, Vector2.zero, new Vector2(size, size)), quirk ? SpecialFx.RoundBox : SpecialFx.Disc);
            if (quirk) _fill.type = Image.Type.Sliced;
            _picture = AddImage(Child("Picture", Root, Vector2.zero, new Vector2(size * 0.88f, size * 0.88f)), null);
            _picture.preserveAspect = true;
            _picture.enabled = false;

            RectTransform glyph = Child("Glyph", Root, Vector2.zero, new Vector2(size, size));
            _glyph = glyph.gameObject.AddComponent<Text>();
            _glyph.raycastTarget = false;
            _glyph.font = font;
            _glyph.fontSize = Mathf.Max(10, Mathf.RoundToInt(size * 0.42f));
            _glyph.alignment = TextAnchor.MiddleCenter;
            _glyph.horizontalOverflow = HorizontalWrapMode.Overflow;
            _glyph.verticalOverflow = VerticalWrapMode.Overflow;
            AddShadow(_glyph.gameObject);

            // The border, just outside the fill: a ring round a disc, a frame round the square.
            float grow = slot == SpecialSlot.Active ? 10f : 7f;
            if (quirk)
            {
                RectTransform frame = Child("Border", Root, Vector2.zero, new Vector2(size + 6f, size + 6f));
                _border = AddImage(frame, SpecialFx.RoundFrame);
                _border.type = Image.Type.Sliced;
            }
            else _border = AddImage(Child("Border", Root, Vector2.zero, new Vector2(size + grow, size + grow)), SpecialFx.Ring);

            if (quirk)
            {
                // The points it frees, on its bottom-right corner.
                RectTransform tag = Child("Refund", Root, new Vector2(size / 2f - 2f, -size / 2f + 4f), new Vector2(28f, 18f));
                Image tagBack = AddImage(tag, SpecialFx.RoundBox);
                tagBack.type = Image.Type.Sliced;
                tagBack.color = colour;
                RectTransform tagText = Child("Text", tag, Vector2.zero, new Vector2(28f, 18f));
                _refund = tagText.gameObject.AddComponent<Text>();
                _refund.raycastTarget = false;
                _refund.font = font;
                _refund.fontSize = 14;
                _refund.alignment = TextAnchor.MiddleCenter;
                _refund.horizontalOverflow = HorizontalWrapMode.Overflow;
                _refund.color = new Color(0.14f, 0.05f, 0.05f, 1f);
                _refundTag = tag.gameObject;
                _refundTag.SetActive(false);
            }
            Show(Special.None, true);
        }

        /// <summary>Shows a pick (None: the slot is empty). True when it changed.</summary>
        internal bool Show(Special pick, bool force = false)
        {
            if (!force && _shown == (int)pick) return false;
            _shown = (int)pick;
            SpecialInfo info = SpecialCatalog.Info(pick);
            Color colour = ColourOf(_slot);
            bool empty = info == null;
            _fill.color = empty ? EmptyFill : SpecialFx.ColourOf(pick);
            SpecialFx.ShowIcon(_picture, _glyph, empty ? Special.None : pick);
            _glyph.color = empty ? WithAlpha(colour, 0.75f) : Color.white;
            if (empty) _glyph.text = "+";
            _border.color = WithAlpha(colour, empty ? 0.5f : 1f);
            if (_glow != null) _glow.color = WithAlpha(colour, empty ? 0.12f : 0.45f);
            if (_refundTag != null)
            {
                bool refund = !empty && info.Kind == SpecialKind.Quirk && info.Refund > 0;
                _refundTag.SetActive(refund);
                if (refund) _refund.text = "+" + info.Refund;
            }
            return true;
        }

        private static Color WithAlpha(Color colour, float alpha)
        {
            colour.a = alpha;
            return colour;
        }

        private static RectTransform Child(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        private static Image AddImage(RectTransform rect, Sprite sprite)
        {
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = sprite;
            image.color = Color.white;
            return image;
        }

        private static void AddShadow(GameObject target)
        {
            Shadow shadow = target.AddComponent<Shadow>();
            shadow.effectColor = ShadowColour;
            shadow.effectDistance = new Vector2(1.5f, -2f);
        }
    }
}
