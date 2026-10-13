using System;
using UnityEngine;

namespace LobbyTools
{
    internal sealed class LobbyUI : MonoBehaviour
    {
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
        private bool bansTab;
        private bool settingsTab;
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
        private RemoteParty[] parties = new RemoteParty[0];

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
                "Overcooked 2 - Quản lý phòng  |  " + Plugin.ToggleKey.Value);
            if (GUI.Button(new Rect(window.width - 44, 10, 30, 26), "X")) SetOpen(false);
            GUI.DragWindow(new Rect(0, 0, window.width - 50, 40));
            if (GUI.Button(new Rect(14, 48, 150, 30), "Người chơi"))
            { bansTab = false; settingsTab = false; pending = null; scroll = Vector2.zero; }
            if (GUI.Button(new Rect(174, 48, 150, 30), "Đã cấm (" + Plugin.Bans.Snapshot().Length + ")"))
            { bansTab = true; settingsTab = false; pending = null; scroll = Vector2.zero; }
            if (GUI.Button(new Rect(334, 48, 150, 30), "Ghép trận"))
            { settingsTab = true; pending = null; }

            if (settingsTab)
            {
                DrawSettings();
                GUI.Label(new Rect(14, window.height - 48, window.width - 28, 42), status);
                return;
            }

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
            GUI.Label(new Rect(14, window.height - 48, window.width - 28, 42),
                string.IsNullOrEmpty(status) ? "Nhấn Esc để đóng. Chọn Đuổi hoặc Cấm, rồi xác nhận người chơi đã chọn." : status);
        }

        private void DrawSettings()
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
