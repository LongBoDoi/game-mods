using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BonesTweaks
{
    /// <summary>
    /// The F1 panel. IMGUI, drawn in Vietnamese, in the same dark style as the Kebab Chefs
    /// overlay. Opening it shows the mouse cursor; closing it puts the cursor back and saves.
    /// </summary>
    internal class TweakUI : MonoBehaviour
    {
        private const float PanelWidth = 440f;
        private const float Pad = 12f;
        private const float Row = 24f;
        private const float LabelW = 150f;
        private const float ValueW = 90f;
        private const int WindowId = 0x0B0E5CAF;

        private bool _open;
        private Rect _panel = new Rect(40f, 40f, PanelWidth, 200f);
        private bool _dirty;
        private float _nextSave;
        private bool _prevCursorVisible;

        private Key _toggleKey = Key.F1;
        private string _toggleKeyText;

        // ------------------------------------------------------------------ update

        private void Update()
        {
            if (Plugin.FastAct.Value || Plugin.InfiniteIngredients.Value) Plugin.TweaksUsed = true;

            var kb = Keyboard.current;
            if (kb != null && kb[ResolveToggleKey()].wasPressedThisFrame) SetOpen(!_open);

            // Sliders mark the config dirty every frame they move; write it once they settle.
            if (_dirty && Time.unscaledTime >= _nextSave) Save();
        }

        private Key ResolveToggleKey()
        {
            string text = Plugin.ToggleKey.Value;
            if (text != _toggleKeyText)
            {
                _toggleKeyText = text;
                if (!Enum.TryParse(text, true, out _toggleKey) || _toggleKey == Key.None)
                {
                    Plugin.Log.LogWarning($"ToggleKey '{text}' is not a key name; using F1.");
                    _toggleKey = Key.F1;
                }
            }
            return _toggleKey;
        }

        private void SetOpen(bool open)
        {
            if (open == _open) return;
            _open = open;
            if (open)
            {
                _prevCursorVisible = Cursor.visible;
                Cursor.visible = true;
            }
            else
            {
                Cursor.visible = _prevCursorVisible;
                if (_dirty) Save();
            }
        }

        private void MarkDirty()
        {
            _dirty = true;
            _nextSave = Time.unscaledTime + 1f;
        }

        private void Save()
        {
            _dirty = false;
            try { Plugin.Cfg.Save(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Could not save settings: {e.Message}"); }
        }

        private void OnApplicationQuit()
        {
            if (_dirty) Save();
        }

        // --------------------------------------------------------------------- gui

        private void OnGUI()
        {
            if (!_open) return;

            // Keep the cursor visible even if something re-hides it while the panel is open.
            if (!Cursor.visible) Cursor.visible = true;

            // Swap our skin in around our own drawing only; GUI.skin is global state.
            GUISkin previous = GUI.skin;
            var skin = UiSkin();
            if (skin != null) GUI.skin = skin;
            try
            {
                _panel = GUI.Window(WindowId, _panel, DrawWindow, "", GUIStyle.none);
                // Keep the title bar on screen so the panel can always be dragged back.
                _panel.x = Mathf.Clamp(_panel.x, 0f, Mathf.Max(0f, Screen.width - _panel.width));
                _panel.y = Mathf.Clamp(_panel.y, 0f, Mathf.Max(0f, Screen.height - 30f));
            }
            catch (Exception e)
            {
                LogOnce($"Panel failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                GUI.skin = previous;
            }
        }

        private void DrawWindow(int id)
        {
            var r = new Rect(0f, 0f, _panel.width, _panel.height);
            Backdrop(r);

            float x = Pad, w = PanelWidth - 2f * Pad, y = Pad;

            GUI.Label(new Rect(x, y, w, 22f), $"<b>Bone's Cafe Tweaks</b>   —   {ResolveToggleKey()} để đóng");
            GUI.DragWindow(new Rect(0f, 0f, _panel.width, Pad + 22f));
            y += 30f;

            // --- Fast Act hold ---------------------------------------------------------
            GUI.Label(new Rect(x, y, w, Row), "<b>Giữ nút Act (thái, xay...)</b>");
            y += Row;

            bool fast = GUI.Toggle(new Rect(x, y, w, Row), Plugin.FastAct.Value, "  Lặp nhanh khi giữ nút Act (J / X)");
            if (fast != Plugin.FastAct.Value) { Plugin.FastAct.Value = fast; MarkDirty(); }
            y += Row;

            if (fast)
            {
                float rate = Mathf.Round(1f / Mathf.Max(0.02f, Plugin.RepeatInterval.Value));
                float newRate = Mathf.Round(SliderRow(ref y, x, w, "Tốc độ lặp", rate, 4f, 20f, $"{rate:0} lần/giây"));
                if (!Mathf.Approximately(newRate, rate)) { Plugin.RepeatInterval.Value = 1f / newRate; MarkDirty(); }

                float delay = Plugin.InitialDelay.Value;
                float newDelay = Mathf.Round(SliderRow(ref y, x, w, "Chờ trước khi lặp", delay, 0.1f, 0.5f, $"{delay:0.00} giây") * 20f) / 20f;
                if (!Mathf.Approximately(newDelay, delay)) { Plugin.InitialDelay.Value = newDelay; MarkDirty(); }
            }
            else
            {
                GUI.Label(new Rect(x, y, w, Row), "<i>Đang tắt: giữ nút lặp 4 lần/giây như game gốc.</i>");
                y += Row;
            }
            y += 8f;

            // --- Infinite ingredients --------------------------------------------------
            GUI.Label(new Rect(x, y, w, Row), "<b>Nguyên liệu</b>");
            y += Row;

            bool inf = GUI.Toggle(new Rect(x, y, w, Row), Plugin.InfiniteIngredients.Value, "  Nguyên liệu vô hạn");
            if (inf != Plugin.InfiniteIngredients.Value) { Plugin.InfiniteIngredients.Value = inf; MarkDirty(); }
            y += Row;

            GUI.Label(new Rect(x, y, w, Row), "<i>Lấy từ tủ lạnh (bò, cá, trứng...) không bị trừ số lượng.</i>");
            y += Row;
            GUI.Label(new Rect(x, y, w, Row), "<i>Chỉ áp dụng cho nguyên liệu đã mở khóa.</i>");
            y += Row;

            // --- Leaderboard note ------------------------------------------------------
            if (Plugin.TweaksUsed && Plugin.KeepLeaderboardsClean.Value)
            {
                y += 6f;
                var prev = GUI.contentColor;
                GUI.contentColor = new Color(1f, 0.85f, 0.35f);
                GUI.Label(new Rect(x, y, w, Row * 2f),
                    "Đã bật tinh chỉnh: điểm sẽ không được gửi lên bảng xếp hạng online cho đến khi mở lại game.");
                GUI.contentColor = prev;
                y += Row * 2f;
            }

            _panel.height = y + Pad;
        }

        /// <summary>Label, slider and value on one row; returns the slider's value.</summary>
        private static float SliderRow(ref float y, float x, float w, string label, float value, float min, float max, string shown)
        {
            GUI.Label(new Rect(x + 18f, y, LabelW, Row), label);
            float sx = x + 18f + LabelW;
            float sw = w - 18f - LabelW - ValueW - 8f;
            float v = GUI.HorizontalSlider(new Rect(sx, y + 7f, sw, Row), value, min, max);
            GUI.Label(new Rect(sx + sw + 8f, y, ValueW, Row), shown);
            y += Row;
            return v;
        }

        // -------------------------------------------------------------------- theme

        private static readonly Color PanelFill = new Color(0.05f, 0.05f, 0.07f, 0.95f);
        private static readonly Color PanelEdge = new Color(0.55f, 0.58f, 0.66f, 0.9f);
        private static readonly Color TextBright = new Color(0.94f, 0.95f, 0.97f);

        private static Texture2D _fillTex, _edgeTex;
        private static GUISkin _skin;
        private static bool _skinTried;

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        /// <summary>A near-opaque dark ground with a light edge, so text reads over any kitchen.</summary>
        private static void Backdrop(Rect r)
        {
            if (_fillTex == null) _fillTex = Solid(PanelFill);
            if (_edgeTex == null) _edgeTex = Solid(PanelEdge);
            GUI.DrawTexture(r, _fillTex);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1f), _edgeTex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1f, r.width, 1f), _edgeTex);
            GUI.DrawTexture(new Rect(r.x, r.y, 1f, r.height), _edgeTex);
            GUI.DrawTexture(new Rect(r.xMax - 1f, r.y, 1f, r.height), _edgeTex);
        }

        /// <summary>
        /// A light-on-dark copy of the IMGUI skin with an OS font. IMGUI's built-in font is not
        /// guaranteed to carry Vietnamese diacritics (missing glyphs render as blanks); Segoe UI
        /// ships with Windows and covers them.
        /// </summary>
        private static GUISkin UiSkin()
        {
            if (_skinTried) return _skin;
            _skinTried = true;
            try
            {
                var copy = Instantiate(GUI.skin);
                copy.hideFlags = HideFlags.HideAndDontSave;

                foreach (var name in new[] { "Segoe UI", "Arial", "Tahoma" })
                {
                    var f = Font.CreateDynamicFontFromOSFont(name, 14);
                    if (f != null) { copy.font = f; Plugin.Log.LogInfo($"UI font: {name}"); break; }
                }

                foreach (var s in new[] { copy.label, copy.toggle, copy.button, copy.box, copy.window })
                {
                    s.richText = true;
                    foreach (var st in new[] { s.normal, s.hover, s.active, s.focused, s.onNormal, s.onHover, s.onActive, s.onFocused })
                        st.textColor = TextBright;
                }
                copy.label.wordWrap = true;
                _skin = copy;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"UI theme failed ({e.Message}); using the game's IMGUI skin.");
                _skin = null;
            }
            return _skin;
        }

        private static string _lastLog;

        private static void LogOnce(string msg)
        {
            if (msg == _lastLog) return;
            _lastLog = msg;
            Plugin.Log.LogWarning(msg);
        }
    }
}
