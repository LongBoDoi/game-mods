using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefRoles
{
    /// <summary>
    /// The role-select step of the level intro. Each local player gets a row; LEFT/RIGHT changes
    /// their role, SELECT locks it in, BACK unlocks it. The screen closes when everyone is ready,
    /// when the auto-confirm timer runs out, or on the skip key (F9 by default).
    ///
    /// The screen is not a BepInEx/IMGUI overlay: it is a copy of the game's own in-game pause
    /// menu (same panel, header font, button bars and dimmed background), stripped of the pause
    /// menu's logic and relabelled.
    /// </summary>
    internal static class RoleSelectScreen
    {
        private const float PauseMenuWaitSeconds = 5f;
        private const float ReadyHoldSeconds = 0.4f;

        private static readonly ChefRole[] Cycle = { ChefRole.None, ChefRole.Cook, ChefRole.Server };

        private static RoleSelectView _view;
        private static bool _open;
        private static bool _skipRequested;

        internal static bool IsOpen
        {
            get { return _open; }
        }

        internal static void RequestSkip()
        {
            if (_open) _skipRequested = true;
        }

        /// <summary>Runs the role-select screen, then the game's own intro (Ready... Go!).</summary>
        internal static IEnumerator Wrap(IEnumerator original)
        {
            Plugin.Roles.Clear();
            LevelState.Reset();

            if (Plugin.Enabled.Value)
            {
                // Freeze the kitchen while players choose. The game's intro pauses the same layer
                // with its own key, so the two pauses don't interfere.
                TimeManager time = GameUtils.RequestManager<TimeManager>();
                object pauseKey = new object();
                if (time != null) time.SetPaused(TimeManager.PauseLayer.Main, true, pauseKey);

                IEnumerator screen = Run();
                while (true)
                {
                    bool more;
                    try
                    {
                        more = screen.MoveNext();
                    }
                    catch (Exception error)
                    {
                        Plugin.Log.LogError("Màn chọn vai trò bị lỗi, bắt đầu màn với vai trò mặc định: " + error);
                        Plugin.Roles.Clear();
                        Close();
                        more = false;
                    }
                    if (!more) break;
                    yield return screen.Current;
                }

                // Let the game's intro take its own pause before releasing ours, so the kitchen
                // never runs for a frame in between.
                bool hasMore;
                try
                {
                    hasMore = original.MoveNext();
                }
                finally
                {
                    if (time != null) time.SetPaused(TimeManager.PauseLayer.Main, false, pauseKey);
                }
                if (!hasMore) yield break;
                yield return original.Current;
            }

            while (original.MoveNext())
                yield return original.Current;
        }

        private sealed class Slot
        {
            public PlayerInputLookup.Player Player;
            public ChefRole Role;
            public bool Ready;
            public ILogicalButton Left;
            public ILogicalButton Right;
            public ILogicalButton Select;
            public ILogicalButton Cancel;
        }

        private static IEnumerator Run()
        {
            List<PlayerInputLookup.Player> players = FindLocalPlayers();
            if (players.Count == 0)
            {
                Plugin.Log.LogWarning("Không tìm thấy người chơi nào trên máy này; bỏ qua màn chọn vai trò.");
                yield break;
            }

            // The pause menu lives in the InGameMenu scene, which the kitchen loads additively.
            InGamePauseMenu source = FindPauseMenu();
            float waited = 0f;
            while (source == null && waited < PauseMenuWaitSeconds)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
                source = FindPauseMenu();
            }
            if (source == null)
            {
                Plugin.Log.LogWarning("Không tìm thấy menu tạm dừng của game; bỏ qua màn chọn vai trò.");
                yield break;
            }

            _view = RoleSelectView.Build(source, players.Count);
            if (_view == null) yield break;
            _open = true;
            _skipRequested = false;

            var slots = new List<Slot>();
            foreach (PlayerInputLookup.Player player in players)
            {
                var slot = new Slot
                {
                    Player = player,
                    Role = Plugin.Roles.LastPicked(player),
                    Left = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UILeft, player),
                    Right = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIRight, player),
                    Select = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UISelectNotStart, player),
                    Cancel = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UICancel, player),
                };
                // Ignore presses left over from the loading screen.
                slot.Left.ClaimPressEvent();
                slot.Right.ClaimPressEvent();
                slot.Select.ClaimPressEvent();
                slot.Cancel.ClaimPressEvent();
                slots.Add(slot);
            }

            float limit = Plugin.AutoConfirmSeconds.Value;
            float remaining = limit;
            Plugin.Log.LogInfo(string.Format("Mở màn chọn vai trò cho {0} người chơi.", slots.Count));

            while (true)
            {
                if (_skipRequested)
                {
                    foreach (Slot slot in slots) slot.Role = ChefRole.None;
                    Plugin.Log.LogInfo("Đã bỏ qua màn chọn vai trò.");
                    break;
                }
                if (!_view.IsAlive) break;

                foreach (Slot slot in slots) HandleInput(slot);

                bool allReady = true;
                foreach (Slot slot in slots) allReady &= slot.Ready;

                if (limit > 0f) remaining -= Time.unscaledDeltaTime;
                _view.Render(slots.ConvertAll(s => new RoleSelectView.Row(s.Player, s.Role, s.Ready)),
                    limit > 0f ? Mathf.Max(0, Mathf.CeilToInt(remaining)) : -1);

                if (allReady)
                {
                    float hold = 0f;
                    while (hold < ReadyHoldSeconds && _view.IsAlive)
                    {
                        yield return null;
                        hold += Time.unscaledDeltaTime;
                    }
                    break;
                }
                if (limit > 0f && remaining <= 0f)
                {
                    Plugin.Log.LogInfo("Hết giờ chọn vai trò; dùng lựa chọn hiện tại.");
                    break;
                }
                yield return null;
            }

            foreach (Slot slot in slots)
            {
                Plugin.Roles.Set(slot.Player, slot.Role);
                Plugin.Log.LogInfo(string.Format("Người chơi {0}: {1} ({2})",
                    (int)slot.Player + 1, RoleStats.DisplayName(slot.Role), RoleStats.Summary(slot.Role)));
            }
            Close();
        }

        private static void HandleInput(Slot slot)
        {
            if (slot.Select.JustPressed())
            {
                slot.Select.ClaimPressEvent();
                if (!slot.Ready) PlaySound(GameOneShotAudioTag.UISelect);
                slot.Ready = true;
            }
            if (slot.Cancel.JustPressed())
            {
                slot.Cancel.ClaimPressEvent();
                if (slot.Ready) PlaySound(GameOneShotAudioTag.UIBack);
                slot.Ready = false;
            }
            if (slot.Ready) return;

            int step = 0;
            if (slot.Left.JustPressed()) { slot.Left.ClaimPressEvent(); step = -1; }
            if (slot.Right.JustPressed()) { slot.Right.ClaimPressEvent(); step = 1; }
            if (step == 0) return;

            int index = Array.IndexOf(Cycle, slot.Role);
            index = (index + step + Cycle.Length) % Cycle.Length;
            slot.Role = Cycle[index];
            PlaySound(GameOneShotAudioTag.UIHighlight);
        }

        private static void PlaySound(GameOneShotAudioTag tag)
        {
            try
            {
                GameUtils.TriggerAudio(tag, LayerMask.NameToLayer("UI"));
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không phát được âm thanh UI: " + error.Message);
            }
        }

        private static void Close()
        {
            if (_view != null) _view.Destroy();
            _view = null;
            _open = false;
            _skipRequested = false;
        }

        /// <summary>Players with a chef on this machine (one row even if a player controls two chefs).</summary>
        private static List<PlayerInputLookup.Player> FindLocalPlayers()
        {
            var players = new List<PlayerInputLookup.Player>();
            foreach (PlayerIDProvider provider in Object.FindObjectsOfType<PlayerIDProvider>())
            {
                if (!provider.IsLocallyControlled()) continue;
                if (provider.GetComponent<PlayerControls>() == null) continue;
                PlayerInputLookup.Player id = provider.GetID();
                if (!players.Contains(id)) players.Add(id);
            }
            players.Sort();
            return players;
        }

        private static readonly FieldInfo PauseMenuField = AccessTools.Field(typeof(T17InGameFlow), "m_pauseMenu");

        private static InGamePauseMenu FindPauseMenu()
        {
            T17InGameFlow flow = T17InGameFlow.Instance;
            if (flow != null && PauseMenuField != null)
            {
                InGamePauseMenu menu = PauseMenuField.GetValue(flow) as InGamePauseMenu;
                if (menu != null) return menu;
            }
            // Fallback: any pause menu that lives in a loaded scene (not a prefab asset).
            foreach (InGamePauseMenu menu in Resources.FindObjectsOfTypeAll<InGamePauseMenu>())
            {
                if (menu != null && menu.gameObject.scene.IsValid()) return menu;
            }
            return null;
        }
    }

    /// <summary>
    /// A copy of the game's pause-menu panel with its logic removed: the header becomes the title,
    /// the pause buttons are replaced by one button bar per player, and a last bar holds the hint.
    /// </summary>
    internal sealed class RoleSelectView
    {
        internal struct Row
        {
            public readonly PlayerInputLookup.Player Player;
            public readonly ChefRole Role;
            public readonly bool Ready;

            public Row(PlayerInputLookup.Player player, ChefRole role, bool ready)
            {
                Player = player;
                Role = role;
                Ready = ready;
            }
        }

        private static readonly Color ReadyTint = new Color(0.55f, 1f, 0.55f, 1f);

        private GameObject _panel;
        private GameObject _backdrop;
        private T17Text _header;
        private T17Text _body; // used when the panel has no button bars to copy
        private T17Text _hint;
        private readonly List<T17Text> _rowTexts = new List<T17Text>();
        private readonly List<Graphic> _rowArt = new List<Graphic>();
        private readonly List<Color> _rowArtColors = new List<Color>();

        internal bool IsAlive
        {
            get { return _panel != null; }
        }

        internal static RoleSelectView Build(InGamePauseMenu source, int playerCount)
        {
            var view = new RoleSelectView();
            Transform parent = source.transform.parent;

            // Clone under an inactive holder so none of the copied scripts ever wake up.
            var holder = new GameObject("ChefRoles_Holder");
            holder.SetActive(false);
            GameObject panel = Object.Instantiate(source.gameObject, holder.transform, false);
            panel.name = "ChefRoles_RoleSelect";

            // Read the cloned menu's references (Unity remaps them to the copy's children).
            InGamePauseMenu menu = panel.GetComponent<InGamePauseMenu>();
            T17Text header = menu.m_header;
            GameObject buttons = menu.m_buttons;
            T17Text pausedText = menu.m_PausedText;
            GameObject template = null;
            if (buttons != null)
            {
                foreach (T17Button button in buttons.GetComponentsInChildren<T17Button>(true))
                {
                    if (template == null || (!template.activeSelf && button.gameObject.activeSelf))
                        template = button.gameObject;
                }
            }

            UiCopy.MakeInert(panel);

            panel.transform.SetParent(parent, false);
            Object.Destroy(holder);
            panel.transform.SetAsLastSibling();
            UiCopy.ForceVisible(panel);

            view._panel = panel;
            view._header = header;
            view._backdrop = UiCopy.CopyBackdrop(panel);

            if (template != null)
            {
                // Hide the original pause buttons and add one bar per player plus a hint bar.
                foreach (Transform child in buttons.transform) child.gameObject.SetActive(false);
                if (pausedText != null) pausedText.gameObject.SetActive(false);
                buttons.SetActive(true);
                for (int i = 0; i < playerCount; i++) view.AddRow(template, buttons.transform);
                GameObject hintRow = Object.Instantiate(template, buttons.transform, false);
                hintRow.SetActive(true);
                foreach (Graphic art in hintRow.GetComponents<Graphic>()) art.enabled = false;
                view._hint = UiCopy.FitText(hintRow.GetComponentInChildren<T17Text>(true));
            }
            else if (pausedText != null)
            {
                if (buttons != null) buttons.SetActive(false);
                pausedText.gameObject.SetActive(true);
                view._body = UiCopy.FitText(pausedText);
            }
            else
            {
                Plugin.Log.LogWarning("Menu tạm dừng không có nút hay chữ để dùng lại; bỏ qua màn chọn vai trò.");
                Object.Destroy(panel);
                if (view._backdrop != null) Object.Destroy(view._backdrop);
                return null;
            }

            panel.SetActive(true);
            return view;
        }

        private void AddRow(GameObject template, Transform parent)
        {
            GameObject row = Object.Instantiate(template, parent, false);
            row.name = "ChefRoles_Row" + _rowTexts.Count;
            row.SetActive(true);
            Graphic art = row.GetComponent<Graphic>();
            _rowArt.Add(art);
            _rowArtColors.Add(art != null ? art.color : Color.white);
            _rowTexts.Add(UiCopy.FitText(row.GetComponentInChildren<T17Text>(true)));
        }

        internal void Render(List<Row> rows, int secondsLeft)
        {
            if (_header != null) UiCopy.SetText(_header, "CHOOSE YOUR ROLE");

            string hint = "LEFT / RIGHT: change role    SELECT: ready    BACK: change again";
            if (secondsLeft >= 0) hint += string.Format("    ({0})", secondsLeft);

            if (_body != null)
            {
                var lines = new List<string>();
                foreach (Row row in rows) lines.Add(Describe(row));
                lines.Add("");
                lines.Add(hint);
                UiCopy.SetText(_body, string.Join("\n", lines.ToArray()));
                return;
            }

            for (int i = 0; i < _rowTexts.Count && i < rows.Count; i++)
            {
                UiCopy.SetText(_rowTexts[i], Describe(rows[i]));
                if (_rowArt[i] != null) _rowArt[i].color = rows[i].Ready ? ReadyTint : _rowArtColors[i];
            }
            UiCopy.SetText(_hint, hint);
        }

        private static string Describe(Row row)
        {
            string name = RoleStats.DisplayName(row.Role);
            string choice = row.Ready ? name + "  - READY!" : "<  " + name + "  >";
            return string.Format("PLAYER {0}:  {1}    {2}", (int)row.Player + 1, choice, RoleStats.Summary(row.Role));
        }

        internal void Destroy()
        {
            if (_panel != null) Object.Destroy(_panel);
            if (_backdrop != null) Object.Destroy(_backdrop);
            _panel = null;
            _backdrop = null;
        }
    }

    /// <summary>Helpers for reusing copies of the game's UI objects as static visuals.</summary>
    internal static class UiCopy
    {
        /// <summary>
        /// Removes every game script from a copied UI object, keeping only Unity UI visuals and
        /// layout. Buttons are kept for their art but disabled so they can't be clicked or selected.
        /// </summary>
        internal static void MakeInert(GameObject root)
        {
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                if (behaviour is EventSystem || behaviour is BaseInputModule)
                {
                    Object.DestroyImmediate(behaviour);
                    continue;
                }
                if (behaviour is Graphic || behaviour is Selectable || behaviour is UIBehaviour) continue;
                behaviour.enabled = false;
                Object.DestroyImmediate(behaviour);
            }
            foreach (Selectable selectable in root.GetComponentsInChildren<Selectable>(true))
            {
                selectable.enabled = false;
                Button button = selectable as Button;
                if (button != null) button.onClick = new Button.ButtonClickedEvent();
            }
            foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
                animator.enabled = false;
        }

        /// <summary>Undo hidden start states the menu's show animation would normally fix up.</summary>
        internal static void ForceVisible(GameObject root)
        {
            foreach (CanvasGroup group in root.GetComponentsInChildren<CanvasGroup>(true))
            {
                group.alpha = 1f;
                group.interactable = false;
                group.blocksRaycasts = false;
            }
            Vector3 scale = root.transform.localScale;
            if (Mathf.Abs(scale.x) < 0.01f || Mathf.Abs(scale.y) < 0.01f) root.transform.localScale = Vector3.one;
        }

        /// <summary>A copy of the dimmed background the pause menu uses, placed behind the panel.</summary>
        internal static GameObject CopyBackdrop(GameObject panel)
        {
            T17InGameFlow flow = T17InGameFlow.Instance;
            if (flow == null || flow.m_BackgroundImage == null) return null;
            GameObject source = flow.m_BackgroundImage;
            GameObject copy = Object.Instantiate(source, source.transform.parent, false);
            copy.name = "ChefRoles_Backdrop";
            MakeInert(copy);
            ForceVisible(copy);
            if (copy.transform.parent == panel.transform.parent)
                copy.transform.SetSiblingIndex(panel.transform.GetSiblingIndex());
            copy.SetActive(true);
            return copy;
        }

        /// <summary>Lets long role descriptions shrink to fit instead of being cut off.</summary>
        internal static T17Text FitText(T17Text text)
        {
            if (text == null) return null;
            text.gameObject.SetActive(true);
            text.enabled = true;
            int size = Mathf.Max(text.fontSize, 12);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 8;
            text.resizeTextMaxSize = size;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        internal static void SetText(T17Text text, string value)
        {
            if (text != null && text.text != value) text.SetNonLocalizedText(value);
        }
    }
}
