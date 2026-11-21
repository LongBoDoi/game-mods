using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// A small label over each chef played on this machine: its active special and whether it is
    /// ready (or how many seconds are left), plus any status line a module sets (stamina,
    /// charges...). It flashes red when a press is refused and pops when the active is used.
    /// </summary>
    internal sealed class SpecialHud
    {
        private const float HeadHeight = 2.3f;
        private const float FlashSeconds = 0.35f;

        private static readonly Color ReadyColour = new Color(0.62f, 1f, 0.5f, 1f);
        private static readonly Color WaitColour = new Color(0.85f, 0.88f, 0.95f, 1f);
        private static readonly Color DenyColour = new Color(1f, 0.45f, 0.4f, 1f);
        private static readonly Color ShadowColour = new Color(0.05f, 0.08f, 0.12f, 0.85f);

        // Chef entity/instance -> when it was last refused or used (for the flash and pop).
        private static readonly Dictionary<ChefState, float> s_deniedAt = new Dictionary<ChefState, float>();
        private static readonly Dictionary<ChefState, float> s_usedAt = new Dictionary<ChefState, float>();

        private readonly GameObject _root;
        private readonly Font _font;
        private readonly Dictionary<ChefState, Text> _labels = new Dictionary<ChefState, Text>();

        internal SpecialHud()
        {
            _root = new GameObject("ChefSkills_SpecialHud", typeof(RectTransform));
            Object.DontDestroyOnLoad(_root);
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) _root.layer = uiLayer;
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 4000;
            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            try
            {
                GameArt art = GameArt.Load();
                _font = art.Header;
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không tải được phông chữ cho nhãn kỹ năng: " + error.Message);
            }
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            s_deniedAt.Clear();
            s_usedAt.Clear();
        }

        internal static void Deny(ChefState state)
        {
            s_deniedAt[state] = Time.unscaledTime;
            try
            {
                GameUtils.TriggerAudio(GameOneShotAudioTag.UIBack, LayerMask.NameToLayer("UI"));
            }
            catch (Exception)
            {
            }
        }

        internal static void Used(ChefState state)
        {
            s_usedAt[state] = Time.unscaledTime;
        }

        internal void Update(IList<ChefState> chefs)
        {
            if (_root == null) return;
            Camera camera = FindCamera();
            bool hidden = SkillSelectScreen.IsOpen || camera == null;
            var seen = new HashSet<ChefState>();
            foreach (ChefState state in chefs)
            {
                if (!state.Local || state.Chef == null) continue;
                Special active = state.Active;
                if (active == Special.None && string.IsNullOrEmpty(state.Status)) continue;
                seen.Add(state);
                Text label = LabelFor(state);
                Vector3 screen = hidden ? Vector3.zero : camera.WorldToScreenPoint(state.Position + Vector3.up * HeadHeight);
                bool visible = !hidden && screen.z > 0f && state.Chef.activeInHierarchy;
                if (label.gameObject.activeSelf != visible) label.gameObject.SetActive(visible);
                if (!visible) continue;

                RectTransform canvas = (RectTransform)_root.transform;
                float scale = canvas.rect.width > 0f && Screen.width > 0 ? canvas.rect.width / Screen.width : 1f;
                label.rectTransform.anchoredPosition = new Vector2(screen.x * scale, screen.y * scale);

                float now = SpecialRuntime.Now;
                string text = "";
                Color colour = WaitColour;
                if (active != Special.None)
                {
                    string name = SpecialCatalog.NameOf(active);
                    if (now < state.ActiveUntil)
                    {
                        text = name + " " + Mathf.CeilToInt(state.ActiveUntil - now) + "s";
                        colour = ReadyColour;
                    }
                    else if (now < state.CooldownUntil && state.CooldownTotal < 999f)
                        text = name + " " + Mathf.CeilToInt(state.CooldownUntil - now);
                    else if (now < state.CooldownUntil)
                        text = name + " USED";
                    else
                    {
                        text = name + " READY";
                        colour = ReadyColour;
                    }
                }
                if (!string.IsNullOrEmpty(state.Status)) text = text.Length > 0 ? text + "\n" + state.Status : state.Status;

                float denied;
                if (s_deniedAt.TryGetValue(state, out denied) && Time.unscaledTime - denied < FlashSeconds) colour = DenyColour;
                float used, pop = 1f;
                if (s_usedAt.TryGetValue(state, out used))
                {
                    float age = Time.unscaledTime - used;
                    if (age >= 0f && age < 0.25f) pop = 1f + 0.3f * Mathf.Sin(Mathf.PI * age / 0.25f);
                }
                label.text = text;
                label.color = colour;
                label.rectTransform.localScale = new Vector3(pop, pop, 1f);
            }
            var gone = new List<ChefState>();
            foreach (KeyValuePair<ChefState, Text> pair in _labels)
                if (!seen.Contains(pair.Key)) gone.Add(pair.Key);
            foreach (ChefState state in gone)
            {
                if (_labels[state] != null) Object.Destroy(_labels[state].gameObject);
                _labels.Remove(state);
            }
        }

        private Text LabelFor(ChefState state)
        {
            Text label;
            if (_labels.TryGetValue(state, out label) && label != null) return label;
            var go = new GameObject("Label", typeof(RectTransform));
            go.layer = _root.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root.transform, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(320f, 70f);
            label = go.AddComponent<Text>();
            label.raycastTarget = false;
            label.supportRichText = false;
            label.font = _font;
            label.fontSize = 26;
            label.alignment = TextAnchor.LowerCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = ShadowColour;
            outline.effectDistance = new Vector2(2f, -2f);
            _labels[state] = label;
            return label;
        }

        private static Camera FindCamera()
        {
            Camera camera = Camera.main;
            if (camera != null && camera.isActiveAndEnabled) return camera;
            Camera best = null;
            foreach (Camera candidate in Camera.allCameras)
            {
                if (!candidate.isActiveAndEnabled || candidate.targetTexture != null) continue;
                if (candidate.orthographic) continue;
                if (best == null || candidate.depth < best.depth) best = candidate;
            }
            return best;
        }

        internal void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            _labels.Clear();
        }
    }
}
