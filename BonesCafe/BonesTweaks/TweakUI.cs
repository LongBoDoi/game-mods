using System;
using System.Globalization;
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
        private const float FieldW = 64f;
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
            if (Plugin.AnyTweakOn) Plugin.TweaksUsed = true;

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
            _editId = null;
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
            TrackEditFocus();

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
                // Shown as repeats per second; stored as the interval between them.
                float rate = 1f / Mathf.Max(0.02f, Plugin.RepeatInterval.Value);
                NumberRow(ref y, x, w, "rate", "Tốc độ lặp", rate.ToString("0.#"), $"lần/giây ({MinRate}–{MaxRate})", text =>
                {
                    if (!TryParseNumber(text, out float r)) return false;
                    Plugin.RepeatInterval.Value = 1f / Mathf.Clamp(r, MinRate, MaxRate);
                    return true;
                });

                NumberRow(ref y, x, w, "delay", "Chờ trước khi lặp", Plugin.InitialDelay.Value.ToString("0.##"), "giây (0,02–1)", text =>
                {
                    if (!TryParseNumber(text, out float d)) return false;
                    Plugin.InitialDelay.Value = Mathf.Clamp(d, 0.02f, 1f);
                    return true;
                });
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
            y += Row + 8f;

            // --- Infinite souls --------------------------------------------------------
            GUI.Label(new Rect(x, y, w, Row), "<b>Linh hồn</b>");
            y += Row;

            bool souls = GUI.Toggle(new Rect(x, y, w, Row), Plugin.InfiniteSouls.Value, "  Linh hồn vô hạn");
            if (souls != Plugin.InfiniteSouls.Value)
            {
                Plugin.InfiniteSouls.Value = souls;
                MarkDirty();
                // The HUD counter only redraws when the game changes it; show the new number now.
                try { FindObjectOfType<SoulsManager>()?.UpdateDisplayedSummonsAvailable(); } catch { }
            }
            y += Row;

            GUI.Label(new Rect(x, y, w, Row), "<i>Triệu hồi bộ xương phụ bếp không giới hạn (cần đã mở khóa triệu hồi).</i>");
            y += Row + 8f;

            // --- Cafe size -------------------------------------------------------------
            y = DrawCafeSize(x, y, w);

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

        private float DrawCafeSize(float x, float y, float w)
        {
            GUI.Label(new Rect(x, y, w, Row), "<b>Kích thước quán</b>");
            y += Row;

            bool on = GUI.Toggle(new Rect(x, y, w, Row), Plugin.CafeSizeEnabled.Value, "  Tự chọn kích thước quán");
            if (on != Plugin.CafeSizeEnabled.Value) { Plugin.CafeSizeEnabled.Value = on; MarkDirty(); }
            y += Row;

            if (on)
            {
                // One button per preset, 0 (smallest) to 5 (largest), then "custom"; the chosen one stays lit.
                const int presets = 6;
                float bx = x + 18f, customW = 86f;
                float bw = (w - 18f - customW - presets * 4f) / presets;
                bool custom = Plugin.CafeSizeCustom.Value;
                var prevBg = GUI.backgroundColor;
                var lit = new Color(0.45f, 0.8f, 1f);
                for (int i = 0; i < presets; i++)
                {
                    bool chosen = !custom && Plugin.CafeSizeLevel.Value == i;
                    GUI.backgroundColor = chosen ? lit : prevBg;
                    if (GUI.Button(new Rect(bx + i * (bw + 4f), y, bw, Row), chosen ? $"<b>{i}</b>" : i.ToString()) && !chosen)
                    {
                        Plugin.CafeSizeLevel.Value = i;
                        Plugin.CafeSizeCustom.Value = false;
                        MarkDirty();
                    }
                }
                GUI.backgroundColor = custom ? lit : prevBg;
                if (GUI.Button(new Rect(bx + presets * (bw + 4f), y, customW, Row), custom ? "<b>Tùy chỉnh</b>" : "Tùy chỉnh") && !custom)
                {
                    Plugin.CafeSizeCustom.Value = true;
                    MarkDirty();
                }
                GUI.backgroundColor = prevBg;
                y += Row + 4f;

                if (custom)
                {
                    NumberRow(ref y, x, w, "width", "Chiều rộng", CafeSize.ChosenWidth.ToString(),
                        $"ô ({CustomLayout.MinWidth}–{CustomLayout.MaxWidth})", text =>
                        {
                            if (!TryParseNumber(text, out float v)) return false;
                            Plugin.CafeWidth.Value = Mathf.Clamp(Mathf.RoundToInt(v), CustomLayout.MinWidth, CustomLayout.MaxWidth);
                            return true;
                        });
                    NumberRow(ref y, x, w, "depth", "Chiều sâu", CafeSize.ChosenDepth.ToString(),
                        $"ô ({CustomLayout.MinDepth}–{CustomLayout.MaxDepth})", text =>
                        {
                            if (!TryParseNumber(text, out float v)) return false;
                            Plugin.CafeDepth.Value = Mathf.Clamp(Mathf.RoundToInt(v), CustomLayout.MinDepth, CustomLayout.MaxDepth);
                            return true;
                        });
                    GUI.Label(new Rect(x + 18f, y, w - 18f, Row * 2f),
                        "<i>Cửa và tường trước giữ nguyên; quán mở rộng sang trái, phải và ra sau. " +
                        "Lớn hơn 25x15 thì camera sẽ thu nhỏ.</i>");
                    y += Row * 2f;
                }
                else
                {
                    int size = CafeSize.Chosen;
                    GUI.Label(new Rect(x + 18f, y, w - 18f, Row), $"Cỡ {size}: khoảng {CafeSize.Dimensions[size]} ô" + (size == 5 ? " (lớn nhất)" : ""));
                    y += Row;
                }
            }

            if (CafeSize.PendingReload)
            {
                bool canReload = CanReloadCafe();
                var prevEnabled = GUI.enabled;
                GUI.enabled = canReload;
                if (GUI.Button(new Rect(x + 18f, y, 220f, Row), "Áp dụng ngay (tải lại quán)")) ReloadCafe();
                GUI.enabled = prevEnabled;
                y += Row;
                GUI.Label(new Rect(x + 18f, y, w - 18f, Row * 2f), canReload
                    ? "<i>Hoặc sẽ tự áp dụng khi quán tải lại (ngày mới).</i>"
                    : "<i>Chỉ tải lại được trước khi mở cửa. Sẽ tự áp dụng vào ngày mới.</i>");
                y += Row * 2f;
            }
            GUI.Label(new Rect(x, y, w, Row * 2f), "<i>Thu nhỏ quán: đồ nằm ngoài tường được trả về kho. Không áp dụng cho thử thách.</i>");
            y += Row * 2f;
            return y;
        }

        /// <summary>Only before the day starts (the layout phase), and never in a challenge or the tutorial.</summary>
        private static bool CanReloadCafe()
        {
            var gc = GameController.Instance;
            var ms = MainGameState.Instance;
            return gc != null && gc.state == GameState.KitchenLayout && ms != null && !ms.isChallengeMode && !ms.isTutorialMode;
        }

        /// <summary>What the game's own cafe-size picker does: save, then load the cafe scene again.</summary>
        private void ReloadCafe()
        {
            try
            {
                Save();
                SetOpen(false);
                GameData.Instance.SaveGame();
                SceneFader.LoadScene("MainGame0");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cafe size: reload failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------ number input

        private const int MinRate = 1, MaxRate = 50;   // RepeatInterval's 0.02..1 s, as repeats per second

        // The field being typed in, and what has been typed. While a field has focus it shows the
        // typed text, not the stored value, so a half-typed "3" on the way to "35" is not rewritten
        // to the clamped minimum under the cursor. Once focus leaves (click elsewhere, Enter, or
        // closing the panel) it shows the stored - clamped - value again.
        private string _editId, _editText;

        /// <summary>Label, text box and unit on one row. A valid number applies as it is typed.</summary>
        private void NumberRow(ref float y, float x, float w, string id, string label, string current,
                               string unit, Func<string, bool> apply)
        {
            GUI.Label(new Rect(x + 18f, y, LabelW, Row), label);
            var box = new Rect(x + 18f + LabelW, y + 2f, FieldW, Row - 4f);

            GUI.SetNextControlName(id);
            string shown = _editId == id ? _editText : current;
            string typed = GUI.TextField(box, shown, 6);
            if (typed != shown)
            {
                _editId = id;
                _editText = typed;
                if (apply(typed)) MarkDirty();
            }

            GUI.Label(new Rect(box.xMax + 8f, y, w - 18f - LabelW - FieldW - 8f, Row), unit);
            y += Row;
        }

        /// <summary>Ends typing when the focused field changes or Enter is pressed.</summary>
        private void TrackEditFocus()
        {
            var e = Event.current;
            if (_editId != null && e.type == EventType.KeyDown &&
                (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                GUIUtility.keyboardControl = 0;
                _editId = null;
                e.Use();
                return;
            }
            if (_editId != null && e.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != _editId)
                _editId = null;
        }

        /// <summary>Accepts both "0.25" and the Vietnamese "0,25".</summary>
        private static bool TryParseNumber(string text, out float value) =>
            float.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !float.IsNaN(value) && !float.IsInfinity(value);

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

                // Text boxes: light text too, but no rich text - it would hide what is typed.
                foreach (var st in new[] { copy.textField.normal, copy.textField.hover, copy.textField.active, copy.textField.focused,
                                           copy.textField.onNormal, copy.textField.onHover, copy.textField.onActive, copy.textField.onFocused })
                    st.textColor = TextBright;
                copy.textField.alignment = TextAnchor.MiddleCenter;
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
