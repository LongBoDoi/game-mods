using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace OC2Tweaks
{
    /// <summary>
    /// The mod's screen inside Settings, in three tabs: options, hotkeys (select one and press
    /// the new key) and saved bans (select one to lift it). Tabs change with two keys (Z and C by
    /// default), LB/RB on a pad, or a click; the caps beside the tabs show whichever of the two
    /// you last used, keyboard keys or the game's own pad icons. Every change is saved as it is
    /// made. The list redraws itself while open, so a ban made elsewhere shows up straight away.
    /// </summary>
    internal sealed class TweaksMenu : FrontendMenuBehaviour
    {
        private sealed class Row
        {
            internal string Key;
            internal GameObject Object;
            internal RectTransform Rect;
            internal Selectable Selectable;
            internal T17Text Title, Value;
            internal string Hint;
            internal Action Click;
            internal Action Refresh;
        }

        private enum Tab { Options, Keys, Bans }
        private static readonly string[] TabNames = { "TÙY CHỌN", "PHÍM TẮT", "ĐÃ CẤM" };

        private static readonly Color TextColour = new Color(0.953f, 0.918f, 0.929f, 1f);
        private static readonly Color TabIdle = new Color(0.17f, 0.33f, 0.49f, 1f);
        private static readonly Color TabActive = new Color(0.98f, 0.6f, 0.22f, 1f);
        private static readonly Color KeyCap = new Color(0.93f, 0.95f, 0.97f, 1f);
        private static readonly Color Waiting = new Color(1f, 0.9f, 0.42f, 1f);

        /// <summary>True while a hotkey is being captured, or its key is still held down: every
        /// key belongs to the capture then, not to the mod's other hotkeys.</summary>
        internal static bool HoldsKeys { get; private set; }

        private static Tab tab = Tab.Options;

        private T17ScrollView scroll;
        private RectTransform content, viewport;
        private GameObject toggleTemplate, selectorTemplate, infoTemplate;
        private Transform templates;
        private T17Text hint;
        private T17Button done;
        private Image[] tabFills;
        private Text[] tabLabels;
        private Text previousCap, nextCap;
        private Image previousFill, nextFill, previousIcon, nextIcon;
        // Pad icons on the caps after a pad was last used, keyboard keys after a key.
        private bool padIcons;
        private bool leftBumper, rightBumper;

        private readonly List<Row> rows = new List<Row>();
        private readonly Dictionary<GameObject, Row> byObject = new Dictionary<GameObject, Row>();
        private string layout = "";
        private float nextRefresh;
        private GameObject lastSelected;
        private string status;
        private float statusAt;
        private float scrollTarget = -1;
        private Suppressor engagement;
        private bool dialogOpen;
        private Row lastRow;

        // Hotkey capture
        private static KeyCode[] allKeys;
        private ConfigEntry<KeyboardShortcut> capturing;
        private Row captureRow;
        private int captureFrame;
        private Suppressor captureLock;
        private bool releasing;
        private float keysUpSince = -1;

        internal void Setup(T17ScrollView scroll, RectTransform content, GameObject toggle, GameObject selector,
            GameObject info, Transform templates, T17Text hint, T17Button done, RectTransform strip, Font font)
        {
            this.scroll = scroll;
            this.content = content;
            viewport = scroll.m_ViewPort;
            toggleTemplate = toggle;
            selectorTemplate = selector;
            infoTemplate = info;
            this.templates = templates;
            this.hint = hint;
            this.done = done;
            BuildTabs(strip, font);
        }

        // ---------------------------------------------------------------- tabs

        /// <summary>
        /// One flat bar per tab between two key caps, in the menu's colours: the open tab orange
        /// like a selected row, the others the dark blue of the panel's header. Clicking a tab or
        /// a key cap works too.
        /// </summary>
        private void BuildTabs(RectTransform strip, Font font)
        {
            const float cap = 62f, gap = 8f;
            float width = strip.rect.width > 0 ? strip.rect.width : strip.sizeDelta.x;
            float height = strip.sizeDelta.y;
            float each = (width - 2 * (cap + gap) - gap * (TabNames.Length - 1)) / TabNames.Length;
            tabFills = new Image[TabNames.Length];
            tabLabels = new Text[TabNames.Length];
            for (int i = 0; i < TabNames.Length; i++)
            {
                int index = i;
                float x = -width / 2 + cap + gap + i * (each + gap) + each / 2;
                tabFills[i] = Box(strip, "Tab" + i, x, each, height, TabIdle, font, 28, out tabLabels[i],
                    delegate { Switch((Tab)index); });
            }
            Text label;
            previousFill = Box(strip, "Previous", -width / 2 + cap / 2, cap, height, KeyCap, font, 26, out label, delegate { Step(-1); });
            previousCap = label;
            nextFill = Box(strip, "Next", width / 2 - cap / 2, cap, height, KeyCap, font, 26, out label, delegate { Step(1); });
            nextCap = label;
            previousCap.color = nextCap.color = TabIdle;
            previousIcon = Icon(previousFill.rectTransform);
            nextIcon = Icon(nextFill.rectTransform);
            PaintTabs();
        }

        /// <summary>Room for the game's own bordered pad icon, filling a key cap; hidden until a
        /// pad is in use.</summary>
        private static Image Icon(RectTransform cap)
        {
            var made = new GameObject("PadIcon", typeof(RectTransform));
            made.layer = cap.gameObject.layer;
            var rect = (RectTransform)made.transform;
            rect.SetParent(cap, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var image = made.AddComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            made.SetActive(false);
            return image;
        }

        /// <summary>The icon the game's own prompts use for this pad button (Xbox style on PC).
        /// Fetched on opening, since the icon set may not be loaded when the screen is built.</summary>
        private static void LoadIcon(Image icon, ControlPadInput.Button button)
        {
            if (icon == null || icon.sprite != null) return;
            try
            {
                ControllerIconLookup lookup = GameUtils.RequestManager<ControllerIconLookup>();
                if (lookup != null)
                    icon.sprite = lookup.GetIcon(button, ControllerIconLookup.IconContext.Bordered,
                        ControllerIconLookup.DeviceContext.Pad);
            }
            catch (Exception error) { Plugin.Log.LogDebug("Không lấy được biểu tượng nút tay cầm: " + error.Message); }
        }

        private static Image Box(RectTransform parent, string name, float x, float width, float height, Color colour,
            Font font, int size, out Text label, Action click)
        {
            var box = new GameObject(name, typeof(RectTransform));
            box.layer = parent.gameObject.layer;
            var rect = (RectTransform)box.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, 0f);
            rect.sizeDelta = new Vector2(width, height);
            var fill = box.AddComponent<Image>();
            fill.color = colour;
            var button = box.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(delegate { click(); });

            var text = new GameObject("Label", typeof(RectTransform));
            text.layer = box.layer;
            var textRect = (RectTransform)text.transform;
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(6f, 2f);
            textRect.offsetMax = new Vector2(-6f, -2f);
            label = text.AddComponent<Text>();
            label.font = font;
            label.fontSize = size;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleCenter;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 14;
            label.resizeTextMaxSize = size;
            label.supportRichText = false;
            label.raycastTarget = false;
            label.color = TextColour;
            return fill;
        }

        private void PaintTabs()
        {
            if (tabFills == null) return;
            for (int i = 0; i < tabFills.Length; i++)
            {
                tabFills[i].color = i == (int)tab ? TabActive : TabIdle;
                string count = i == (int)Tab.Bans && bans.Length > 0 ? " (" + bans.Length + ")" : "";
                tabLabels[i].text = TabNames[i] + count;
            }
            PaintCap(previousFill, previousCap, previousIcon, Plugin.PreviousTabKey);
            PaintCap(nextFill, nextCap, nextIcon, Plugin.NextTabKey);
        }

        /// <summary>A white cap with the key's name, or just the game's LB/RB icon on a pad (the
        /// icon has its own border, so the cap's fill goes clear but still takes clicks).</summary>
        private void PaintCap(Image fill, Text label, Image icon, ConfigEntry<KeyboardShortcut> key)
        {
            bool pad = padIcons && icon.sprite != null;
            if (icon.gameObject.activeSelf != pad) icon.gameObject.SetActive(pad);
            label.enabled = !pad;
            fill.color = pad ? Color.clear : KeyCap;
            label.text = MenuText.KeyLabel(key.Value.ToString());
        }

        /// <summary>
        /// Follows the device you are using, the way the game's own button prompts do: a button
        /// or a stick/d-pad push on a pad switches the caps to pad icons, a keyboard key back to
        /// key names. The mouse changes nothing.
        /// </summary>
        private void TrackDevice()
        {
            bool pad = padIcons;
            if (PadPressed()) pad = true;
            else if (Input.anyKeyDown && KeyboardPressed()) pad = false;
            if (pad == padIcons) return;
            padIcons = pad;
            PaintTabs();
        }

        private static bool PadPressed()
        {
            try
            {
                InControl.InputDevice device = InControl.InputManager.ActiveDevice;
                if (device != null && device != InControl.InputDevice.Null &&
                    (device.AnyButton.WasPressed || device.Direction.WasPressed))
                    return true;
            }
            catch (Exception) { }
            for (KeyCode key = KeyCode.JoystickButton0; key <= KeyCode.JoystickButton19; key++)
                if (Input.GetKeyDown(key)) return true;
            return false;
        }

        private static bool KeyboardPressed()
        {
            if (allKeys == null) allKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
            foreach (KeyCode key in allKeys)
                if (key < KeyCode.Mouse0 && key != KeyCode.None && Input.GetKeyDown(key)) return true;
            return false;
        }

        /// <summary>LB/RB held, through the game's own pad layer (InControl, which knows every
        /// pad the game does) or Unity's buttons 4/5. A step happens when this turns on, so a pad
        /// both of them see still moves one tab per press.</summary>
        private static bool BumperHeld(bool right)
        {
            if (Input.GetKey(right ? KeyCode.JoystickButton5 : KeyCode.JoystickButton4)) return true;
            try
            {
                InControl.InputDevice device = InControl.InputManager.ActiveDevice;
                if (device == null || device == InControl.InputDevice.Null) return false;
                return (right ? device.RightBumper : device.LeftBumper).IsPressed;
            }
            catch (Exception) { return false; }
        }

        private void Step(int by)
        {
            Switch((Tab)(((int)tab + by + TabNames.Length) % TabNames.Length));
        }

        private void Switch(Tab to)
        {
            if (capturing != null || releasing) return;
            if (to != tab) GameUtils.TriggerAudio(GameOneShotAudioTag.UIHighlight, gameObject.layer);
            tab = to;
            status = null;
            scrollTarget = -1;
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, 0f);
            Rebuild(null, -1);
            PaintTabs();
            lastSelected = null;
        }

        private static bool Pressed(ConfigEntry<KeyboardShortcut> key)
        {
            try { return key.Value.IsDown(); }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- showing and hiding

        public override bool Show(GamepadUser gamer, BaseMenuBehaviour parent, GameObject invoker, bool hideInvoker = true)
        {
            if (gameObject.activeInHierarchy) return false;
            if (T17FrontendFlow.Instance != null) T17FrontendFlow.Instance.BlockFocusKitchen = true;
            var manager = GameUtils.RequestManager<GamepadEngagementManager>();
            if (manager != null) engagement = manager.Suppressor.AddSuppressor(this);
            InviteMonitor.InviteAccepted = (GenericVoid)Delegate.Combine(InviteMonitor.InviteAccepted, new GenericVoid(Close));

            status = null;
            layout = "";
            // Start from whatever opened the screen; TrackDevice follows any change after.
            padIcons = gamer != null && gamer.ControlType != GamepadUser.ControlTypeEnum.Keyboard;
            leftBumper = BumperHeld(false);
            rightBumper = BumperHeld(true);
            LoadIcon(previousIcon, ControlPadInput.Button.LB);
            LoadIcon(nextIcon, ControlPadInput.Button.RB);
            Rebuild(null, -1);
            PaintTabs();
            // The game's own options screen shows its list first, then itself.
            bool shown = scroll.Show(gamer, parent, invoker, hideInvoker) && base.Show(gamer, parent, invoker, hideInvoker);
            BindEventSystems(gameObject);
            Link();
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, 0f);
            Select(FirstSelectable());
            return shown;
        }

        public override bool Hide(bool restoreInvokerState = true, bool isTabSwitch = false)
        {
            StopCapture(null);
            ReleaseKeys();
            InviteMonitor.InviteAccepted = (GenericVoid)Delegate.Remove(InviteMonitor.InviteAccepted, new GenericVoid(Close));
            if (engagement != null) { engagement.Release(); engagement = null; }
            if (T17FrontendFlow.Instance != null) T17FrontendFlow.Instance.BlockFocusKitchen = false;
            bool hidden = scroll.Hide(restoreInvokerState, isTabSwitch);
            return base.Hide(restoreInvokerState, isTabSwitch) && hidden;
        }

        /// <summary>Esc and XONG. While a key is being captured, Esc belongs to the capture.</summary>
        public override void Close()
        {
            if (capturing != null || releasing) return;
            Hide();
        }

        private void OnDisable() { ReleaseKeys(); }

        protected override void Update()
        {
            base.Update();
            float now = Time.realtimeSinceStartup;
            try
            {
                TrackDevice();
                bool left = BumperHeld(false), right = BumperHeld(true);
                bool leftDown = left && !leftBumper, rightDown = right && !rightBumper;
                leftBumper = left;
                rightBumper = right;

                if (capturing != null) Capture();
                else if (releasing) WaitForRelease(now);
                else if (!T17DialogBoxManager.HasAnyOpenDialogs())
                {
                    if (Pressed(Plugin.PreviousTabKey) || leftDown) Step(-1);
                    else if (Pressed(Plugin.NextTabKey) || rightDown) Step(1);
                }

                if (now >= nextRefresh)
                {
                    nextRefresh = now + 0.5f;
                    Refresh(now);
                }
                TrackSelection();
                // A pop-up replaces the hint bar's legend with its own; put ours back after.
                bool dialogs = T17DialogBoxManager.HasAnyOpenDialogs();
                if (dialogOpen && !dialogs && T17FrontendFlow.Instance != null && T17FrontendFlow.Instance.m_Rootmenu != null)
                    T17FrontendFlow.Instance.m_Rootmenu.SetLegendText(GetLegendText());
                dialogOpen = dialogs;
            }
            catch (Exception error)
            {
                nextRefresh = now + 5f;
                Plugin.Log.LogError("Bộ công cụ: không cập nhật được màn hình: " + error);
            }
        }

        private void LateUpdate()
        {
            if (scrollTarget < 0) return;
            float y = Mathf.Lerp(content.anchoredPosition.y, scrollTarget, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
            if (Mathf.Abs(y - scrollTarget) < 0.5f) { y = scrollTarget; scrollTarget = -1; }
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
        }

        // ---------------------------------------------------------------- building the list

        private BanRecord[] bans = new BanRecord[0];

        private void Refresh(float now)
        {
            bans = Plugin.Bans.Snapshot();
            PaintTabs();

            if (Layout() != layout)
            {
                // Rebuilding moves the selection, which would pull it out of an open pop-up or
                // away from a key being captured.
                if (T17DialogBoxManager.HasAnyOpenDialogs() || capturing != null || releasing) return;
                // Right after a pop-up the selection is still on its way back; the last row you
                // were on is the better guide.
                Row selected = Selected() ?? lastRow;
                Rebuild(selected != null ? selected.Key : null,
                    selected != null ? Chain().IndexOf(selected.Selectable) : -1);
                return;
            }
            foreach (Row row in rows)
                if (row.Refresh != null) row.Refresh();
        }

        /// <summary>What decides which rows the open tab has. When it changes the rows are
        /// rebuilt; otherwise only their values are updated, so the selection never jumps.</summary>
        private string Layout()
        {
            var text = new StringBuilder(tab.ToString()).Append('|');
            if (tab == Tab.Bans)
            {
                text.Append(Plugin.Bans.Ready);
                foreach (BanRecord ban in bans) text.Append(ban.SteamId).Append('|');
            }
            return text.ToString();
        }

        private void Rebuild(string keepKey, int keepIndex)
        {
            bans = Plugin.Bans.Snapshot();
            layout = Layout();
            foreach (Row old in rows)
            {
                old.Object.SetActive(false);
                Destroy(old.Object);
            }
            rows.Clear();
            byObject.Clear();
            lastRow = null;

            switch (tab)
            {
                case Tab.Options: OptionRows(); break;
                case Tab.Keys: KeyRows(); break;
                default: BanRows(); break;
            }

            BindEventSystems(content.gameObject);
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            Link();

            // Stay on the same row, or near where it was when it went away (a kicked player,
            // a lifted ban), and keep whatever the hint bar was reporting.
            Selectable keep = null;
            if (keepKey != null)
                foreach (Row row in rows) if (row.Key == keepKey) { keep = row.Selectable; break; }
            List<Selectable> chain = Chain();
            if (keep == null && keepIndex >= 0 && chain.Count > 0) keep = chain[Mathf.Min(keepIndex, chain.Count - 1)];
            if (gameObject.activeInHierarchy) Select(keep ?? FirstSelectable());
        }

        private void OptionRows()
        {
            Toggle("match.host", "Luôn làm chủ phòng khi tìm trận", Plugin.ForceHostReady,
                () => Plugin.ForceHost.Value, on => Plugin.ForceHost.Value = on,
                Plugin.ForceHostReady
                    ? "Khi tìm trận Arcade/Versus công khai, bạn tự tạo phòng và chờ người khác vào thay vì vào phòng của người khác. Áp dụng từ lần tìm trận tiếp theo."
                    : "Không bật được chức năng này trên bản game hiện tại. Xem BepInEx/LogOutput.log.");
            Toggle("overlay.on", "Hiện độ trễ ở góc màn hình", true,
                () => Plugin.PingOverlay.Value, on => Plugin.PingOverlay.Value = on,
                "Một danh sách nhỏ độ trễ của mọi người ở góc màn hình, kể cả khi đang chơi. Không nhận chuột hay phím, tự ẩn khi không ở trong phòng trực tuyến.");
            Choice("overlay.corner", "Vị trí bảng độ trễ", () => MenuText.CornerNames,
                () => Array.IndexOf(MenuText.Corners, Plugin.PingOverlayCorner.Value),
                i => Plugin.PingOverlayCorner.Value = MenuText.Corners[i],
                "Góc màn hình hiện danh sách độ trễ. Xanh lá dưới 80 ms, vàng dưới 150 ms, đỏ khi cao hơn, xám khi đang chờ số mới.");
            Toggle("restart.on", "Chơi lại màn bằng một phím", QuickRestart.Ready,
                () => Plugin.QuickRestartEnabled.Value && QuickRestart.Ready, on => Plugin.QuickRestartEnabled.Value = on,
                QuickRestart.Ready
                    ? "Nhấn phím chơi lại (thẻ Phím tắt) khi đang chơi để chơi lại màn ngay, như Tạm dừng → Chơi lại → Đồng ý nhưng không qua menu. Chỉ chủ phòng cần bật; cả phòng chơi lại cùng nhau."
                    : "Không bật được: bản game này không còn InGamePauseMenu.OnRestartConfirmed.");
        }

        private void KeyRows()
        {
            KeyRow("key.restart", "Chơi lại màn", Plugin.QuickRestartKey,
                "Chỉ có tác dụng khi đang chơi một màn, và bỏ qua lần nhấn thứ hai trong vòng " +
                QuickRestart.Cooldown.ToString("0") + " giây.");
            KeyRow("key.overlay", "Bật/tắt bảng độ trễ", Plugin.PingOverlayKey,
                "Bật hoặc tắt danh sách độ trễ ở góc màn hình ở bất cứ đâu.");
            KeyRow("key.previous", "Thẻ trước", Plugin.PreviousTabKey,
                "Chuyển sang thẻ bên trái trên màn hình này. Tay cầm: LB.");
            KeyRow("key.next", "Thẻ sau", Plugin.NextTabKey,
                "Chuyển sang thẻ bên phải trên màn hình này. Tay cầm: RB.");
        }

        private void BanRows()
        {
            if (!Plugin.Bans.Ready)
                Info("bans.broken", "Không dùng được danh sách cấm", "", null,
                    "Tệp danh sách cấm bị lỗi hoặc không đọc được. Xem BepInEx/LogOutput.log, sửa tệp rồi khởi động lại game. Bạn vẫn đuổi được người chơi.");
            else if (bans.Length == 0)
                Info("bans.none", "Chưa cấm ai", "", null,
                    "Tài khoản bị cấm sẽ tự động bị đuổi mỗi khi vào phòng bạn làm chủ.");
            foreach (BanRecord ban in bans)
            {
                BanRecord record = ban;
                string date = ban.BannedUtc.ToLocalTime().ToString("dd/MM/yyyy");
                Info("b:" + ban.SteamId, MenuText.SafeName(ban.Name), date, delegate { Prompts.AskUnban(record, Report); },
                    "Bị cấm ngày " + date + ". Chọn để bỏ cấm.").Title.supportRichText = false;
            }
        }

        private List<Selectable> Chain()
        {
            var chain = new List<Selectable>();
            foreach (Row row in rows)
                if (row.Selectable != null) chain.Add(row.Selectable);
            return chain;
        }

        private Row Add(GameObject template, string key)
        {
            // Built under the inactive templates holder so the row's components are wired up
            // before they wake (a toggle reads its option in Awake).
            GameObject made = Instantiate(template, templates, false);
            made.name = key;
            var row = new Row
            {
                Key = key, Object = made, Rect = (RectTransform)made.transform,
                Selectable = made.GetComponent<Selectable>()
            };
            Transform title = made.transform.Find("Title");
            row.Title = title != null ? title.GetComponent<T17Text>() : null;
            Transform value = made.transform.Find("Selector/Value");
            row.Value = value != null ? value.GetComponent<T17Text>() : null;
            if (row.Title != null)
            {
                row.Title.resizeTextForBestFit = true;
                row.Title.resizeTextMinSize = 20;
                row.Title.resizeTextMaxSize = row.Title.fontSize;
            }
            if (row.Value != null)
            {
                row.Value.resizeTextForBestFit = true;
                row.Value.resizeTextMinSize = 18;
                row.Value.resizeTextMaxSize = row.Value.fontSize;
                row.Value.supportRichText = false;
            }
            rows.Add(row);
            byObject[made] = row;
            if (row.Selectable != null) byObject[row.Selectable.gameObject] = row;
            return row;
        }

        private void Place(Row row)
        {
            row.Object.transform.SetParent(content, false);
            row.Object.SetActive(true);
        }

        private Row Info(string key, string title, string value, Action click, string about)
        {
            Row row = Add(infoTemplate, key);
            row.Hint = about;
            row.Click = click;
            SettingsScreen.SetText(row.Title, title);
            SettingsScreen.SetText(row.Value, value);
            var button = row.Selectable as Button;
            if (button != null) button.onClick.AddListener(delegate { Clicked(row); });
            Place(row);
            return row;
        }

        private void Toggle(string key, string title, bool available, Func<bool> read, Action<bool> write, string about)
        {
            Row row = Add(toggleTemplate, key);
            row.Hint = about;
            SettingsScreen.SetText(row.Title, title);
            var bridge = new ToggleBridge(read, delegate(bool on)
            {
                Save(delegate { write(on); });
                nextRefresh = 0;
            });
            ToggleOption option = row.Object.GetComponent<ToggleOption>();
            SettingsScreen.ToggleValue.SetValue(option, bridge);
            if (row.Selectable != null) row.Selectable.interactable = available;
            // A setting changed elsewhere (the overlay key, the config file) shows up here too.
            var toggle = row.Selectable as Toggle;
            row.Refresh = delegate
            {
                if (toggle != null && toggle.isOn != read()) option.SyncUIWithOption();
            };
            Place(row);
        }

        private void Choice(string key, string title, Func<string[]> names, Func<int> read, Action<int> write, string about)
        {
            Row row = Add(selectorTemplate, key);
            row.Hint = about;
            SettingsScreen.SetText(row.Title, title);
            SelectorOption option = row.Object.GetComponent<SelectorOption>();
            var bridge = new ChoiceBridge(names, read, delegate(int index) { Save(delegate { write(index); }); });
            bridge.Changed = delegate { option.SyncUIWithOption(); };
            SettingsScreen.SelectorValue.SetValue(option, bridge);
            row.Refresh = delegate
            {
                string[] all = names();
                int index = bridge.GetOption();
                if (row.Value != null && index < all.Length && row.Value.text != all[index]) option.SyncUIWithOption();
            };
            Place(row);
            option.SyncUIWithOption();
        }

        // ---------------------------------------------------------------- hotkeys

        private void KeyRow(string key, string title, ConfigEntry<KeyboardShortcut> setting, string about)
        {
            Row row = null;
            row = Info(key, title, MenuText.KeyLabel(setting.Value.ToString()), delegate { StartCapture(row, setting); },
                about + " Chọn rồi nhấn phím mới; giữ Ctrl, Shift hoặc Alt để dùng tổ hợp.");
            row.Refresh = delegate
            {
                if (captureRow == row || row.Value == null) return;
                string label = MenuText.KeyLabel(setting.Value.ToString());
                if (row.Value.text != label) row.Value.text = label;
            };
        }

        private ConfigEntry<KeyboardShortcut>[] Hotkeys()
        {
            return new[] { Plugin.QuickRestartKey, Plugin.PingOverlayKey, Plugin.PreviousTabKey, Plugin.NextTabKey };
        }

        /// <summary>
        /// Waits for the next key. The game's menu is paused meanwhile (its event system is
        /// suppressed, as the game does for its own screen-area adjuster), so the key cannot
        /// also move the selection, press a button or close the screen.
        /// </summary>
        private void StartCapture(Row row, ConfigEntry<KeyboardShortcut> setting)
        {
            if (capturing != null || releasing) return;
            capturing = setting;
            captureRow = row;
            captureFrame = Time.frameCount;
            HoldsKeys = true;
            if (m_CachedEventSystem != null && captureLock == null) captureLock = m_CachedEventSystem.Disable(this);
            if (row.Value != null)
            {
                row.Value.text = "Nhấn phím...";
                row.Value.color = Waiting;
            }
            status = "Nhấn phím mới cho \"" + row.Title.text + "\". Giữ Ctrl, Shift hoặc Alt để tạo tổ hợp. Esc để hủy.";
            statusAt = float.MaxValue;
            ShowHint(status);
        }

        private void Capture()
        {
            if (Time.frameCount <= captureFrame) return; // the press that started it
            if (allKeys == null) allKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
            foreach (KeyCode key in allKeys)
            {
                if (!Input.GetKeyDown(key)) continue;
                if (key == KeyCode.Escape || key >= KeyCode.JoystickButton0)
                {
                    StopCapture("Đã hủy đổi phím.");
                    return;
                }
                if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6) continue;
                if (IsModifier(key)) continue;
                var held = new List<KeyCode>();
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) held.Add(KeyCode.LeftControl);
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) held.Add(KeyCode.LeftShift);
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) held.Add(KeyCode.LeftAlt);
                var shortcut = new KeyboardShortcut(key, held.ToArray());
                ConfigEntry<KeyboardShortcut> setting = capturing;
                string title = captureRow.Title.text;
                string chosen = shortcut.ToString();
                var others = new List<string>();
                foreach (ConfigEntry<KeyboardShortcut> other in Hotkeys())
                    if (other != setting) others.Add(other.Value.ToString());
                string message = null;
                Save(delegate
                {
                    setting.Value = shortcut;
                    message = title + ": " + MenuText.KeyLabel(chosen) + "." + MenuText.KeyNote(chosen, others.ToArray());
                });
                StopCapture(message);
                PaintTabs();
                return;
            }
        }

        private static bool IsModifier(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl: case KeyCode.RightControl: case KeyCode.LeftShift: case KeyCode.RightShift:
                case KeyCode.LeftAlt: case KeyCode.RightAlt: case KeyCode.AltGr: case KeyCode.LeftCommand:
                case KeyCode.RightCommand: case KeyCode.LeftWindows: case KeyCode.RightWindows:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Ends a capture. The menu stays paused until every key is let go, so the
        /// key just bound (or Esc) does not reach the menu as it is released.</summary>
        private void StopCapture(string message)
        {
            if (capturing == null) return;
            ConfigEntry<KeyboardShortcut> setting = capturing;
            Row row = captureRow;
            capturing = null;
            captureRow = null;
            releasing = true;
            keysUpSince = -1;
            if (row != null && row.Value != null)
            {
                row.Value.text = MenuText.KeyLabel(setting.Value.ToString());
                row.Value.color = TextColour;
            }
            if (message != null) Report(message);
        }

        private void WaitForRelease(float now)
        {
            if (Input.anyKey) { keysUpSince = -1; return; }
            if (keysUpSince < 0) { keysUpSince = now; return; }
            if (now - keysUpSince >= 0.15f) ReleaseKeys();
        }

        private void ReleaseKeys()
        {
            releasing = false;
            HoldsKeys = capturing != null;
            if (captureLock != null && m_CachedEventSystem != null) m_CachedEventSystem.ReleaseSuppressor(captureLock);
            else if (captureLock != null) captureLock.Release();
            captureLock = null;
        }

        private void Save(Action change)
        {
            try { change(); }
            catch (Exception error)
            {
                status = "Không lưu được thay đổi: " + error.Message;
                statusAt = Time.realtimeSinceStartup;
                ShowHint(status);
                Plugin.Log.LogError(error);
            }
        }

        // ---------------------------------------------------------------- navigation

        /// <summary>Up and down move between rows; down from the last row reaches XONG, and up
        /// from XONG returns to the row you left.</summary>
        private void Link()
        {
            List<Selectable> chain = Chain();
            for (int i = 0; i < chain.Count; i++)
            {
                var nav = new Navigation { mode = Navigation.Mode.Explicit };
                nav.selectOnUp = i > 0 ? chain[i - 1] : null;
                nav.selectOnDown = i < chain.Count - 1 ? chain[i + 1] : (Selectable)done;
                chain[i].navigation = nav;
            }
            var back = new Navigation { mode = Navigation.Mode.Explicit };
            back.selectOnUp = chain.Count > 0 ? chain[chain.Count - 1] : null;
            done.navigation = back;
        }

        private Selectable FirstSelectable()
        {
            foreach (Row row in rows)
                if (row.Selectable != null) return row.Selectable;
            return done;
        }

        private void BindEventSystems(GameObject root)
        {
            if (m_CachedEventSystem == null) return;
            foreach (IT17EventHelper helper in root.GetComponentsInChildren<IT17EventHelper>(true))
                if (!(helper is T17Text)) helper.SetEventSystem(m_CachedEventSystem);
        }

        private void Select(Selectable target)
        {
            if (target == null || m_CachedEventSystem == null) return;
            m_CachedEventSystem.SetSelectedGameObject(null);
            m_CachedEventSystem.SetSelectedGameObject(target.gameObject);
        }

        private Row Selected()
        {
            if (m_CachedEventSystem == null) return null;
            GameObject current = m_CachedEventSystem.currentSelectedGameObject;
            Row row;
            return current != null && byObject.TryGetValue(current, out row) ? row : null;
        }

        // ---------------------------------------------------------------- selection and hints

        private void TrackSelection()
        {
            if (m_CachedEventSystem == null) return;
            GameObject current = m_CachedEventSystem.currentSelectedGameObject;
            if (current == lastSelected) return;
            lastSelected = current;
            Row row;
            if (current == null || !byObject.TryGetValue(current, out row) || !rows.Contains(row))
            {
                if (current == done.gameObject && status == null) ShowHint("Mọi thay đổi đã được lưu ngay khi bạn chọn.");
                return;
            }
            // A result just reported outlives the selection settling back after its pop-up
            // and after the list rebuilds; a move of your own replaces it with the row's hint.
            if (status == null || Time.realtimeSinceStartup - statusAt > 0.75f)
            {
                status = null;
                ShowHint(row.Hint);
            }
            lastRow = row;
            var nav = done.navigation;
            nav.selectOnUp = row.Selectable;
            done.navigation = nav;
            Reveal(row);
        }

        private void ShowHint(string text)
        {
            if (hint != null) hint.text = text ?? "";
        }

        /// <summary>Scrolls just enough to show the selected row.</summary>
        private void Reveal(Row row)
        {
            float from = Offset(row.Rect), to = from + row.Rect.rect.height;
            float view = viewport.rect.height;
            float max = Mathf.Max(0f, content.rect.height - view);
            float y = scrollTarget >= 0 ? scrollTarget : content.anchoredPosition.y;
            if (from < y) y = from;
            else if (to > y + view) y = to - view;
            scrollTarget = Mathf.Clamp(y, 0f, max);
        }

        /// <summary>Distance from the top of the list to the top of a row.</summary>
        private static float Offset(RectTransform rect)
        {
            return -(rect.anchoredPosition.y + (1f - rect.pivot.y) * rect.rect.height);
        }

        /// <summary>Ping colours for the corner overlay.</summary>
        internal static Color Colour(PingBand band)
        {
            switch (band)
            {
                case PingBand.Good: return new Color(0.62f, 1f, 0.62f, 1f);
                case PingBand.Fair: return new Color(1f, 0.9f, 0.42f, 1f);
                case PingBand.Poor: return new Color(1f, 0.58f, 0.52f, 1f);
                case PingBand.Stale: return new Color(0.82f, 0.85f, 0.9f, 1f);
                default: return TextColour;
            }
        }

        // ---------------------------------------------------------------- actions

        private void Clicked(Row row)
        {
            if (capturing != null || releasing) return;
            if (row.Click != null) row.Click();
            else ShowHint(row.Hint);
        }

        private void Report(string message)
        {
            status = message;
            statusAt = Time.realtimeSinceStartup;
            ShowHint(message);
            nextRefresh = 0;
            if (!string.IsNullOrEmpty(message)) Plugin.Log.LogInfo("Bộ công cụ: " + message);
        }
    }
}
