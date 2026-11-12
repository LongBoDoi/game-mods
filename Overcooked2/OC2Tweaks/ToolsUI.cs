using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace OC2Tweaks
{
    internal sealed class ToolsUI : MonoBehaviour
    {
        private enum Tab { Players, Bans, Matchmaking, Hotkeys }

        /// <summary>Which binding the panel is waiting for a keypress to replace.</summary>
        private enum Capture { None, Toggle, Restart }

        internal static bool IsOpen { get; private set; }
        private static float inputReleasedAt;
        private static bool closingWithMouse;
        internal static bool BlockMenuInput
        {
            get
            {
                if (closingWithMouse && !Input.GetMouseButton(0)) closingWithMouse = false;
                return IsOpen || Time.realtimeSinceStartup < inputReleasedAt || closingWithMouse ||
                    Input.GetKeyDown(Plugin.ToggleKey.Value);
            }
        }
        private Rect window = new Rect(40, 40, 600, 620);
        private Vector2 scroll;
        private Tab tab = Tab.Players;
        private Capture capturing;
        private bool oldVisible;
        private CursorLockMode oldLock;
        private GUISkin skin;
        private Font vietnameseFont;
        private Texture2D fill;
        private RemoteParty pending;
        private bool pendingBan;
        private string status = "";
        private float nextRefresh;
        private float nextBanCheck;
        private float nextBanErrorLog;
        // Resources.FindObjectsOfTypeAll walks every loaded object of the type, and OnGUI runs
        // several times a frame, so the Phim tat tab reads this instead of asking directly.
        private float nextLevelCheck;
        private bool inLevel;
        private RemoteParty[] parties = new RemoteParty[0];

        private static readonly KeyCode[] ModifierKeys =
        {
            KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr, KeyCode.LeftCommand,
            KeyCode.RightCommand, KeyCode.LeftWindows, KeyCode.RightWindows
        };

        private void Update()
        {
            // Independent of panel visibility and timeScale, so matches cannot bypass saved IDs.
            if (Time.realtimeSinceStartup >= nextBanCheck)
            {
                nextBanCheck = Time.realtimeSinceStartup + 0.25f;
                try { Moderation.EnforceBans(); }
                catch (Exception error)
                {
                    status = "Không thể tự động đuổi người chơi đã cấm: " + error.Message;
                    if (Time.realtimeSinceStartup >= nextBanErrorLog)
                    {
                        nextBanErrorLog = Time.realtimeSinceStartup + 5f;
                        Plugin.Log.LogError(status);
                    }
                }
                try { ConnectionMonitor.Tick(Time.realtimeSinceStartup); }
                catch (Exception error)
                {
                    status = "Không thể đo độ trễ kết nối: " + error.Message;
                    if (Time.realtimeSinceStartup >= nextBanErrorLog)
                    {
                        nextBanErrorLog = Time.realtimeSinceStartup + 5f;
                        Plugin.Log.LogError(status);
                    }
                }
            }

            // While waiting for a new binding, every key belongs to the rebind and to nothing else.
            if (capturing != Capture.None) return;

            if (Input.GetKeyDown(Plugin.ToggleKey.Value)) SetOpen(!IsOpen);
            else if (IsOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                if (pending != null) pending = null;
                else SetOpen(false);
            }
            if (IsOpen && Time.realtimeSinceStartup >= nextRefresh)
            {
                nextRefresh = Time.realtimeSinceStartup + 0.25f;
                try { parties = Moderation.Parties(); }
                catch (Exception error) { status = "Không thể tải danh sách người chơi: " + error.Message; parties = new RemoteParty[0]; }
            }
            if (IsOpen && tab == Tab.Hotkeys && Time.realtimeSinceStartup >= nextLevelCheck)
            {
                nextLevelCheck = Time.realtimeSinceStartup + 0.25f;
                try { inLevel = QuickRestart.IsLevelRunning(); }
                catch (Exception error) { inLevel = false; status = "Không kiểm tra được trạng thái màn: " + error.Message; }
            }

            // Suppressed with the panel open: the panel is a menu, and a restart key that also
            // works while you are clicking around in it restarts levels by accident.
            if (!IsOpen) QuickRestart.Tick(Time.realtimeSinceStartup);
        }

        private void LateUpdate()
        {
            if (IsOpen) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }

        private void SetOpen(bool open)
        {
            if (open == IsOpen) return;
            IsOpen = open;
            pending = null;
            capturing = Capture.None;
            if (open)
            {
                oldVisible = Cursor.visible;
                oldLock = Cursor.lockState;
                nextRefresh = 0;
            }
            else
            {
                inputReleasedAt = Time.realtimeSinceStartup + 0.2f;
                closingWithMouse = Input.GetMouseButton(0);
                Cursor.visible = oldVisible;
                Cursor.lockState = oldLock;
            }
        }

        private void OnDisable() { SetOpen(false); }
        private void OnDestroy()
        {
            SetOpen(false);
            if (skin != null) Destroy(skin);
            if (vietnameseFont != null) Destroy(vietnameseFont);
            if (fill != null) Destroy(fill);
        }

        private void MakeSkin()
        {
            if (skin != null) return;
            skin = Instantiate(GUI.skin);
            skin.hideFlags = HideFlags.HideAndDontSave;
            vietnameseFont = Font.CreateDynamicFontFromOSFont("Segoe UI", 14);
            vietnameseFont.hideFlags = HideFlags.HideAndDontSave;
            skin.font = vietnameseFont;
            foreach (GUIStyle style in new[] { skin.label, skin.box, skin.button, skin.toggle,
                skin.window, skin.textField })
            {
                style.fontSize = 14;
                style.font = vietnameseFont;
                style.richText = false; // Player names are untrusted text.
                foreach (GUIStyleState state in new[] { style.normal, style.hover, style.active,
                    style.focused, style.onNormal, style.onHover, style.onActive, style.onFocused })
                    state.textColor = new Color(0.94f, 0.95f, 0.97f);
            }
            skin.label.wordWrap = true;
            fill = new Texture2D(1, 1);
            fill.hideFlags = HideFlags.HideAndDontSave;
            fill.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.07f, 0.98f));
            fill.Apply();
        }

        private void OnGUI()
        {
            if (!IsOpen) return;

            // Taken before the window draws: a key event consumed by a control inside the window
            // would never reach the rebind.
            if (capturing != Capture.None && Event.current != null &&
                Event.current.type == EventType.KeyDown) CaptureKey();

            GUISkin previousSkin = GUI.skin;
            Color previousBackground = GUI.backgroundColor;
            Color previousColor = GUI.color;
            int previousDepth = GUI.depth;
            bool previousEnabled = GUI.enabled;
            try
            {
                MakeSkin();
                GUI.skin = skin;
                GUI.depth = -200;
                window.width = Mathf.Min(600, Screen.width);
                window.height = Mathf.Min(620, Screen.height);
                window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
                window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
                window = GUI.Window(0x0C20006, window, DrawWindow, "", GUIStyle.none);
            }
            finally
            {
                GUI.skin = previousSkin;
                GUI.backgroundColor = previousBackground;
                GUI.color = previousColor;
                GUI.depth = previousDepth;
                GUI.enabled = previousEnabled;
            }
        }

        private void DrawWindow(int id)
        {
            GUI.DrawTexture(new Rect(0, 0, window.width, window.height), fill);
            GUI.Label(new Rect(14, 12, window.width - 70, 25),
                "Overcooked 2 - Bộ công cụ  |  " + Plugin.ToggleKey.Value);
            if (GUI.Button(new Rect(window.width - 44, 10, 30, 26), "X")) SetOpen(false);
            GUI.DragWindow(new Rect(0, 0, window.width - 50, 40));

            string[] labels =
            {
                "Người chơi", "Đã cấm (" + Plugin.Bans.Snapshot().Length + ")",
                "Ghép trận", "Phím tắt"
            };
            float tabWidth = (window.width - 28 - 30) / 4f;
            for (int i = 0; i < labels.Length; i++)
            {
                if (!GUI.Button(new Rect(14 + i * (tabWidth + 10), 48, tabWidth, 30), labels[i])) continue;
                tab = (Tab)i;
                pending = null;
                capturing = Capture.None;
                scroll = Vector2.zero;
                nextLevelCheck = 0; // Show the real state as soon as Phim tat opens.
            }

            if (tab == Tab.Matchmaking || tab == Tab.Hotkeys)
            {
                if (tab == Tab.Matchmaking) DrawMatchmaking();
                else DrawHotkeys();
                DrawStatus(status);
                return;
            }

            bool bansTab = tab == Tab.Bans;
            string reason;
            bool canManage = Moderation.HostCanManage(out reason);
            GUI.Label(new Rect(14, 88, window.width - 28, 42),
                bansTab ? "Tài khoản đã cấm sẽ tự động bị đuổi nếu gặp lại khi bạn làm chủ phòng." : reason);
            GUI.Label(new Rect(14, 132, window.width - 28, 30), "Luôn làm chủ phòng: " +
                (Plugin.ForceHost.Value ? "BẬT" : "TẮT"));
            if (!Plugin.Bans.Ready)
                GUI.Label(new Rect(14, 164, window.width - 28, 38),
                    "Không thể dùng danh sách cấm. Xem BepInEx/LogOutput.log. Bạn vẫn có thể đuổi người chơi.");

            float top = !Plugin.Bans.Ready ? 204 : 168;
            float bottom = pending != null ? 145 : 65;
            Rect viewport = new Rect(14, top, window.width - 28, Mathf.Max(40, window.height - top - bottom));
            BanRecord[] records = bansTab ? Plugin.Bans.Snapshot() : new BanRecord[0];
            int count = bansTab ? records.Length : parties.Length;
            float width = viewport.width - 20;
            scroll = GUI.BeginScrollView(viewport, scroll, new Rect(0, 0, width, Mathf.Max(viewport.height, count * 82)));
            try
            {
                if (count == 0) GUI.Label(new Rect(0, 4, width, 42),
                    bansTab ? "Chưa có người chơi nào bị cấm." : "Chưa có người chơi từ máy khác tham gia phòng của bạn.");
                for (int i = 0; i < count; i++)
                {
                    float y = i * 82;
                    if (bansTab) DrawBan(records[i], y, width);
                    else DrawParty(parties[i], y, width, canManage);
                }
            }
            finally { GUI.EndScrollView(); }

            if (pending != null)
            {
                float y = window.height - 145;
                GUI.Label(new Rect(14, y, window.width - 28, 58),
                    (pendingBan ? "Cấm và đuổi " : "Đuổi ") + SafeName(pending.Name) + "?" +
                    (pending.Count > 1 ? " Cả " + pending.Count + " người chơi chung kết nối sẽ rời phòng." : "") +
                    (pendingBan ? " Tài khoản này sẽ được lưu và tự động bị đuổi nếu gặp lại." : ""));
                GUI.enabled = canManage;
                if (GUI.Button(new Rect(14, y + 62, 160, 28), pendingBan ? "Xác nhận cấm" : "Xác nhận đuổi"))
                {
                    RemoteParty target = pending;
                    bool ban = pendingBan;
                    pending = null;
                    Run(delegate { return Moderation.Remove(target, ban); });
                    nextRefresh = 0;
                }
                GUI.enabled = true;
                if (GUI.Button(new Rect(184, y + 62, 100, 28), "Hủy")) pending = null;
            }
            DrawStatus(string.IsNullOrEmpty(status)
                ? "Nhấn Esc để đóng. Chọn Đuổi hoặc Cấm, rồi xác nhận người chơi đã chọn." : status);
        }

        private void DrawStatus(string text)
        {
            GUI.Label(new Rect(14, window.height - 48, window.width - 28, 42), text);
        }

        private void DrawMatchmaking()
        {
            GUI.enabled = Plugin.ForceHostReady;
            bool forceHost = GUI.Toggle(new Rect(14, 86, window.width - 28, 26),
                Plugin.ForceHost.Value, "  Luôn làm chủ phòng khi ghép trận");
            GUI.enabled = true;
            if (forceHost != Plugin.ForceHost.Value)
                Run(delegate
                {
                    Plugin.ForceHost.Value = forceHost;
                    return forceHost ? "Đã bật luôn làm chủ phòng từ lần ghép trận tiếp theo." :
                        "Đã tắt luôn làm chủ phòng. Lần tiếp theo sẽ ghép trận bình thường.";
                });
            GUI.Label(new Rect(14, 116, window.width - 28, 64), Plugin.ForceHostReady ?
                "Áp dụng từ lần ghép trận Arcade/Versus tiếp theo. Bạn tạo phòng và chờ người chơi khác tham gia." :
                "Không thể dùng chức năng này. Xem BepInEx/LogOutput.log.");
            GUI.Label(new Rect(14, 196, window.width - 28, 64),
                "Độ trễ chỉ dùng để hiển thị trong thẻ Người chơi. Mod không tự động đuổi người chơi vì độ trễ cao.");
        }

        private void DrawHotkeys()
        {
            float right = window.width - 150;
            float y = 90;

            GUI.Label(new Rect(14, y, right - 24, 26), "Mở bộ công cụ: " + Plugin.ToggleKey.Value);
            if (GUI.Button(new Rect(right, y - 3, 136, 28),
                capturing == Capture.Toggle ? "Đang chờ..." : "Đổi phím"))
                StartCapture(Capture.Toggle);
            y += 42;

            GUI.enabled = QuickRestart.Ready;
            bool enabled = GUI.Toggle(new Rect(14, y, right - 24, 26),
                Plugin.QuickRestartEnabled.Value && QuickRestart.Ready,
                "  Chơi lại màn ngay bằng một phím");
            GUI.enabled = true;
            if (QuickRestart.Ready && enabled != Plugin.QuickRestartEnabled.Value)
                Run(delegate
                {
                    Plugin.QuickRestartEnabled.Value = enabled;
                    return enabled
                        ? "Đã bật chơi lại nhanh. Nhấn " + Plugin.QuickRestartKey.Value + " khi đang chơi một màn."
                        : "Đã tắt chơi lại nhanh. Phím đó không còn tác dụng.";
                });
            y += 32;

            GUI.Label(new Rect(32, y, right - 42, 26), "Phím chơi lại: " + Plugin.QuickRestartKey.Value);
            GUI.enabled = QuickRestart.Ready;
            if (GUI.Button(new Rect(right, y - 3, 136, 28),
                capturing == Capture.Restart ? "Đang chờ..." : "Đổi phím"))
                StartCapture(Capture.Restart);
            GUI.enabled = true;
            y += 44;

            if (capturing != Capture.None)
            {
                GUI.Label(new Rect(14, y, window.width - 28, 48),
                    "Hãy nhấn phím mới. Giữ thêm Ctrl, Shift hoặc Alt nếu muốn dùng tổ hợp. Nhấn Esc để hủy.");
                return;
            }

            if (!QuickRestart.Ready)
            {
                GUI.Label(new Rect(14, y, window.width - 28, 64),
                    "Không bật được chức năng chơi lại nhanh: bản game này không còn " +
                    "InGamePauseMenu.OnRestartConfirmed. Các chức năng khác vẫn hoạt động bình thường.");
                return;
            }

            GUI.Label(new Rect(14, y, window.width - 28, 112),
                "Nhấn phím trên khi đang chơi một màn và màn đó chơi lại ngay, đúng như Tạm dừng -> " +
                "Chơi lại -> Đồng ý, nhưng không phải bấm qua menu và câu hỏi xác nhận.\n\n" +
                "Ở menu chính, phòng chờ, bản đồ chiến dịch và trong lúc đang tải thì phím này không " +
                "làm gì: chơi lại giữa lúc đổi scene là nguyên nhân làm cả phòng treo ở màn hình đen.");
            y += 120;

            float cooling = QuickRestart.Cooling(Time.realtimeSinceStartup);
            GUI.Label(new Rect(14, y, window.width - 28, 64),
                "Hai lần nhấn cách nhau dưới " + QuickRestart.Cooldown.ToString("0") +
                " giây cũng bị bỏ qua, vì chơi lại khi lần trước chưa xong có thể làm mất phòng." +
                (cooling > 0f ? " Còn chờ " + cooling.ToString("0.0") + " giây." : "") +
                "\nKhi bảng này đang mở thì phím tạm ngưng - hãy đóng bảng rồi nhấn. Chỉ chủ phòng cần mod này: " +
                "chơi lại là quyết định của chủ phòng và cả phòng chơi lại cùng nhau.");
            y += 72;

            GUI.enabled = Plugin.QuickRestartEnabled.Value && cooling <= 0f;
            if (GUI.Button(new Rect(14, y, 200, 30), "Chơi lại màn ngay"))
                Run(delegate { return QuickRestart.Restart(Time.realtimeSinceStartup); });
            GUI.enabled = true;
            GUI.Label(new Rect(224, y + 2, window.width - 238, 26),
                inLevel ? "Đang ở trong một màn." : "Không ở trong màn nào.");
        }

        private void StartCapture(Capture which)
        {
            capturing = which;
            status = which == Capture.Toggle
                ? "Đang chờ phím mới cho bộ công cụ..."
                : "Đang chờ phím mới cho chơi lại nhanh...";
        }

        /// <summary>
        /// Turns the key event being processed into a new binding. Modifiers on their own are not
        /// a binding, so they are swallowed and the panel keeps waiting for a real key.
        /// </summary>
        private void CaptureKey()
        {
            KeyCode key = Event.current.keyCode;
            bool control = Event.current.control, shift = Event.current.shift, alt = Event.current.alt;
            Event.current.Use(); // Keep the keypress out of the game menu behind the panel.

            if (key == KeyCode.None || Array.IndexOf(ModifierKeys, key) >= 0) return;

            Capture which = capturing;
            capturing = Capture.None;
            if (key == KeyCode.Escape) { status = "Đã hủy đổi phím."; return; }

            if (which == Capture.Toggle)
            {
                Run(delegate
                {
                    Plugin.ToggleKey.Value = key;
                    // Esc already closes the panel, and Update reads this key every frame.
                    return "Đã đổi phím mở bộ công cụ thành " + key + "." + Clash(key);
                });
                return;
            }

            var modifiers = new List<KeyCode>();
            if (control) modifiers.Add(KeyCode.LeftControl);
            if (shift) modifiers.Add(KeyCode.LeftShift);
            if (alt) modifiers.Add(KeyCode.LeftAlt);
            KeyboardShortcut shortcut = new KeyboardShortcut(key, modifiers.ToArray());
            Run(delegate
            {
                Plugin.QuickRestartKey.Value = shortcut;
                return "Đã đổi phím chơi lại thành " + shortcut + "." +
                    (modifiers.Count == 0 ? Clash(key) : "");
            });
        }

        /// <summary>Warns when the two bindings would both answer the same key.</summary>
        private static string Clash(KeyCode key)
        {
            bool sameKey = Plugin.ToggleKey.Value == key &&
                Plugin.QuickRestartKey.Value.MainKey == key &&
                !HasModifiers(Plugin.QuickRestartKey.Value);
            return sameKey
                ? " Lưu ý: phím này đang dùng cho cả bộ công cụ và chơi lại nhanh - hãy đổi một trong hai."
                : "";
        }

        /// <summary>BepInEx exposes Modifiers as an IEnumerable, so count it by walking it.</summary>
        private static bool HasModifiers(KeyboardShortcut shortcut)
        {
            foreach (KeyCode held in shortcut.Modifiers) return held != KeyCode.None;
            return false;
        }

        private void DrawParty(RemoteParty party, float y, float width, bool canManage)
        {
            GUI.Label(new Rect(0, y, width - 160, 30), SafeName(party.Name) +
                (party.Count > 1 ? " (nhóm " + party.Count + " người)" : ""));
            GUI.Label(new Rect(0, y + 31, width - 160, 36), ConnectionMonitor.Display(party.Session, Time.realtimeSinceStartup));
            GUI.enabled = canManage;
            if (GUI.Button(new Rect(width - 154, y + 4, 70, 30), "Đuổi"))
            { pending = party; pendingBan = false; }
            GUI.enabled = canManage && party.SteamId != null && Plugin.Bans.Ready;
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.85f, 0.4f, 0.35f);
            if (GUI.Button(new Rect(width - 76, y + 4, 70, 30), "Cấm"))
            { pending = party; pendingBan = true; }
            GUI.backgroundColor = old;
            GUI.enabled = true;
        }

        private void DrawBan(BanRecord record, float y, float width)
        {
            GUI.Label(new Rect(0, y, width - 100, 30), SafeName(record.Name));
            GUI.Label(new Rect(0, y + 31, width - 100, 40),
                "Ngày cấm: " + record.BannedUtc.ToLocalTime().ToString("dd/MM/yyyy"));
            GUI.enabled = Plugin.Bans.Ready;
            if (GUI.Button(new Rect(width - 90, y + 4, 84, 30), "Bỏ cấm"))
                Run(delegate { Plugin.Bans.Unban(record.SteamId); return "Đã bỏ cấm " + SafeName(record.Name) + "."; });
            GUI.enabled = true;
        }

        private void Run(Func<string> action)
        {
            try { status = action(); }
            catch (Exception error)
            {
                status = "Thao tác thất bại: " + error.Message;
                Plugin.Log.LogError(error);
            }
        }

        private static string SafeName(string name)
        {
            name = (name ?? "Người chơi").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            return name.Length > 70 ? name.Substring(0, 70) + "..." : name;
        }
    }
}
