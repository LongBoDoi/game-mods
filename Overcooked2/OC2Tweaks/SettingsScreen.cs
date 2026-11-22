using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace OC2Tweaks
{
    /// <summary>
    /// Puts the mod into the game's own Settings menu: a <b>BỘ CÔNG CỤ</b> entry under Game,
    /// Controls and Credits, opening a screen built from the game's Game Settings screen. Every
    /// row on it is one of the game's own rows (tick box, left/right selector, plain bar), so it
    /// looks, sounds and navigates like the rest of the menu, with mouse, keyboard or pad.
    /// </summary>
    [HarmonyPatch(typeof(FrontendRootMenu), "Awake")]
    internal static class SettingsHook
    {
        private static void Postfix(FrontendRootMenu __instance)
        {
            try { SettingsScreen.Attach(__instance); }
            catch (Exception error) { Plugin.Log.LogError("Không thêm được Bộ công cụ vào màn hình Cài đặt: " + error); }
        }
    }

    internal static class SettingsScreen
    {
        internal const string EntryLabel = "OC2 TWEAKS";
        internal const string Title = "OC2 TWEAKS";

        // Everything below is reached by name; CheckRuntime.ps1 asserts each one exists.
        private static readonly MethodInfo RootOnShow = AccessTools.Method(typeof(FrontendRootMenu), "OnMenuShow");
        private static readonly MethodInfo RootOnHide = AccessTools.Method(typeof(FrontendRootMenu), "OnMenuHide");
        private static readonly FieldInfo VersionText = AccessTools.Field(typeof(FrontendOptionsMenu), "m_VersionString");
        internal static readonly FieldInfo ToggleType = AccessTools.Field(typeof(BaseUIOption<IOption>), "m_OptionType");
        internal static readonly FieldInfo ToggleValue = AccessTools.Field(typeof(BaseUIOption<IOption>), "m_Option");
        internal static readonly FieldInfo SelectorType = AccessTools.Field(typeof(BaseUIOption<INameListOption>), "m_OptionType");
        internal static readonly FieldInfo SelectorValue = AccessTools.Field(typeof(BaseUIOption<INameListOption>), "m_Option");

        /// <summary>A value <see cref="OptionsData.OptionType"/> does not define, so a cloned
        /// row's Awake leaves the game's real option (resolution, windowed...) alone.</summary>
        internal const OptionsData.OptionType NoGameOption = (OptionsData.OptionType)(-1);

        private static TweaksMenu menu;

        internal static void Validate()
        {
            if (RootOnShow == null || RootOnHide == null) throw new MissingMemberException("FrontendRootMenu.OnMenuShow/OnMenuHide");
            if (VersionText == null) throw new MissingMemberException("FrontendOptionsMenu.m_VersionString");
            if (ToggleType == null || ToggleValue == null || SelectorType == null || SelectorValue == null)
                throw new MissingMemberException("BaseUIOption`1.m_OptionType/m_Option");
        }

        /// <summary>Adds the entry to the Settings tab. Runs once per main menu, as it loads.</summary>
        internal static void Attach(FrontendRootMenu root)
        {
            FrontendGameSettingsTab[] tabs = root.GetComponentsInChildren<FrontendGameSettingsTab>(true);
            if (tabs.Length == 0)
            {
                Plugin.Log.LogWarning("Không tìm thấy thẻ Cài đặt trong menu chính; Bộ công cụ sẽ không có mục trong Cài đặt.");
                return;
            }
            Transform tab = tabs[0].transform;
            Transform credits = tab.Find("Credits");
            if (credits == null)
                for (int i = tab.childCount - 1; i >= 0 && credits == null; i--)
                    if (tab.GetChild(i).GetComponent<T17Button>() != null) credits = tab.GetChild(i);
            if (credits == null) throw new MissingMemberException("Settings tab has no button to copy");

            GameObject entry = UnityEngine.Object.Instantiate(credits.gameObject, tab, false);
            entry.name = "OC2Tweaks";
            entry.transform.SetSiblingIndex(credits.GetSiblingIndex() + 1);
            T17Button button = entry.GetComponent<T17Button>();
            Mute(button.onClick);
            button.onClick.AddListener(delegate { Open(root); });
            T17Text label = entry.GetComponentInChildren<T17Text>(true);
            if (label != null)
            {
                label.m_bNeedsLocalization = true;
                label.m_LocalizationTag = MenuText.Literal(EntryLabel);
                label.SetNewPlaceHolder(EntryLabel);
            }
            entry.AddComponent<TabEntry>().Sibling = credits.GetComponent<T17Button>();
            menu = null;
        }

        internal static bool IsOpen { get { return menu != null && menu.gameObject.activeInHierarchy; } }

        private static void Open(FrontendRootMenu root)
        {
            try
            {
                if (menu == null) menu = Build(root);
                menu.Show(root.CurrentGamepadUser, root, root.gameObject, false);
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Không mở được Bộ công cụ: " + error);
            }
        }

        /// <summary>Turns off the calls a copied button or event still carries to the original.</summary>
        internal static void Mute(UnityEventBase unityEvent)
        {
            for (int i = 0; i < unityEvent.GetPersistentEventCount(); i++)
                unityEvent.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        internal static void SetText(T17Text text, string value)
        {
            if (text == null) return;
            text.SetNonLocalizedText(value ?? "");
        }

        /// <summary>
        /// Copies the game's Game Settings screen and swaps its brain: the game's
        /// FrontendOptionsMenu (which would load, save and undo the game's own options) is
        /// removed before it ever wakes, and <see cref="TweaksMenu"/> takes its place. The copy is
        /// made under an inactive holder so nothing in it runs until it is ready.
        /// </summary>
        private static TweaksMenu Build(FrontendRootMenu root)
        {
            FrontendOptionsMenu source = root.SearchAllForMenuOfType<FrontendOptionsMenu>();
            if (source == null) throw new MissingMemberException("Game Settings screen not found");
            Transform parent = source.transform.parent;

            var holder = new GameObject("OC2Tweaks.Build");
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            try
            {
                GameObject screen = UnityEngine.Object.Instantiate(source.gameObject, holder.transform, false);
                screen.name = "OC2TweaksMenu";
                FrontendOptionsMenu copied = screen.GetComponent<FrontendOptionsMenu>();
                T17ScrollView scroll = copied.m_ScrollView;
                T17Text version = VersionText.GetValue(copied) as T17Text;
                string legend = copied.m_legendText, legendClient = copied.m_legendTextClient,
                    legendSession = copied.m_legendTextInSession;
                Animator transition = copied.m_TransitionAnimator;
                UnityEngine.Object.DestroyImmediate(copied);

                var tweaks = screen.AddComponent<TweaksMenu>();
                tweaks.m_legendText = legend;
                tweaks.m_legendTextClient = legendClient;
                tweaks.m_legendTextInSession = legendSession;
                tweaks.m_TransitionAnimator = transition;
                tweaks.m_bShouldBlockParentCancelHandler = true;
                tweaks.MenuName = "OC2Tweaks";

                Transform body = screen.transform.Find("SettingsBody");
                Transform console = body.Find("ContentConsole");
                if (console != null) UnityEngine.Object.DestroyImmediate(console.gameObject);
                Transform links = scroll.transform.Find("Links");
                if (links != null) links.gameObject.SetActive(false);
                scroll.m_ContentIsDynamic = false;
                scroll.m_bScrollToSelectedElement = false;
                RectTransform content = scroll.m_ContentParent;

                // Keep one row of each kind as a template, out of the list, and drop the rest.
                var templates = new GameObject("Templates");
                templates.SetActive(false);
                templates.transform.SetParent(screen.transform, false);
                Transform toggle = content.Find("Windowed"), selector = content.Find("Resolution");
                if (toggle == null || selector == null) throw new MissingMemberException("Game Settings rows changed");
                toggle.SetParent(templates.transform, false);
                selector.SetParent(templates.transform, false);
                for (int i = content.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.DestroyImmediate(content.GetChild(i).gameObject);
                foreach (ToggleOption option in toggle.GetComponents<ToggleOption>()) ToggleType.SetValue(option, NoGameOption);
                foreach (SelectorOption option in selector.GetComponents<SelectorOption>()) SelectorType.SetValue(option, NoGameOption);
                // The list wires its children's navigation the first time it initialises, header
                // rows included. Let that happen now, while it is empty; TweaksMenu links the rows.
                scroll.DoSingleTimeInitialize();

                GameObject info = UnityEngine.Object.Instantiate(selector.gameObject, templates.transform, false);
                info.name = "Info";
                foreach (SelectorOption option in info.GetComponents<SelectorOption>()) UnityEngine.Object.DestroyImmediate(option);
                foreach (string arrow in new[] { "Selector/ArrowLeft", "Selector/ArrowRight" })
                {
                    Transform found = info.transform.Find(arrow);
                    if (found != null) UnityEngine.Object.DestroyImmediate(found.gameObject);
                }

                // Settings here apply as they change, so the screen needs one way out, not two.
                Transform cancel = body.Find("Cancel"), confirm = body.Find("Confirm");
                if (cancel != null) cancel.gameObject.SetActive(false);
                T17Button done = confirm.GetComponent<T17Button>();
                // The game's per-platform navigation fix-ups would point XONG back at Cancel.
                foreach (MonoBehaviour fix in confirm.GetComponents<MonoBehaviour>())
                    if (fix != null && fix.GetType().Name == "SetNavigationForPlatform") UnityEngine.Object.DestroyImmediate(fix);
                Mute(done.onClick);
                done.onClick.AddListener(tweaks.Close);
                SetText(confirm.GetComponentInChildren<T17Text>(true), Lang.T("DONE", "XONG"));

                var back = screen.GetComponent<NavigateOnUICancel>();
                if (back == null) back = screen.AddComponent<NavigateOnUICancel>();
                if (back.m_DoThisOnUICancel == null) back.m_DoThisOnUICancel = new UnityEvent();
                Mute(back.m_DoThisOnUICancel);
                back.m_DoThisOnUICancel.AddListener(tweaks.Close);

                // The title font has no Vietnamese letters with a dot below (Ộ, Ụ), so the title
                // is the mod's name; everything under it uses the body font, which has them all.
                Transform header = body.Find("HeaderBacker/Header");
                T17Text title = header != null ? header.GetComponent<T17Text>() : null;
                SetText(title, Title);
                if (version != null) version.text = "OC2Tweaks " + Plugin.Version;

                // The hint sits where Cancel was: what the selected row does, or what just happened.
                T17Text hint = null;
                if (version != null)
                {
                    GameObject hintObject = UnityEngine.Object.Instantiate(version.gameObject, body, false);
                    hintObject.name = "Hint";
                    var rect = (RectTransform)hintObject.transform;
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.anchoredPosition = new Vector2(-400f, -246f);
                    rect.sizeDelta = new Vector2(780f, 150f);
                    hint = hintObject.GetComponent<T17Text>();
                    hint.fontSize = 28;
                    hint.alignment = TextAnchor.MiddleLeft;
                    hint.horizontalOverflow = HorizontalWrapMode.Wrap;
                    hint.verticalOverflow = VerticalWrapMode.Truncate;
                    hint.supportRichText = false;
                    hint.color = new Color(0.16f, 0.3f, 0.43f, 1f);
                    hint.text = "";
                }

                // Make room above the list for the tab strip: the list keeps its bottom edge and
                // gives up the top of its height.
                var list = (RectTransform)scroll.transform;
                float listTop = list.anchoredPosition.y + list.sizeDelta.y * 0.5f;
                float listBottom = list.anchoredPosition.y - list.sizeDelta.y * 0.5f;
                const float stripHeight = 54f, stripGap = 12f;
                float newTop = listTop - stripHeight - stripGap;
                list.sizeDelta = new Vector2(list.sizeDelta.x, newTop - listBottom);
                list.anchoredPosition = new Vector2(list.anchoredPosition.x, (newTop + listBottom) * 0.5f);
                var stripObject = new GameObject("Tabs", typeof(RectTransform));
                stripObject.layer = body.gameObject.layer;
                var strip = (RectTransform)stripObject.transform;
                strip.SetParent(body, false);
                strip.anchorMin = strip.anchorMax = strip.pivot = new Vector2(0.5f, 0.5f);
                strip.sizeDelta = new Vector2(list.sizeDelta.x, stripHeight);
                strip.anchoredPosition = new Vector2(list.anchoredPosition.x, listTop - stripHeight * 0.5f);

                tweaks.Setup(scroll, content, toggle.gameObject, selector.gameObject, info, templates.transform, hint, done,
                    strip, version != null ? version.font : title != null ? title.font : null);

                screen.SetActive(false);
                screen.transform.SetParent(parent, false);
                screen.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
                // Without these the main menu would not know the screen is open: Esc would
                // close what is underneath it instead.
                tweaks.OnShow = (BaseMenuBehaviour.BaseMenuBehaviourEvent)Delegate.Combine(tweaks.OnShow,
                    Delegate.CreateDelegate(typeof(BaseMenuBehaviour.BaseMenuBehaviourEvent), root, RootOnShow));
                tweaks.OnHide = (BaseMenuBehaviour.BaseMenuBehaviourEvent)Delegate.Combine(tweaks.OnHide,
                    Delegate.CreateDelegate(typeof(BaseMenuBehaviour.BaseMenuBehaviourEvent), root, RootOnHide));
                return tweaks;
            }
            finally { UnityEngine.Object.Destroy(holder); }
        }
    }

    /// <summary>
    /// Keeps the copied Settings entry working alongside the game's and other mods' entries: it
    /// borrows the event system the game gave its neighbour (copies do not get one), chains
    /// up/down navigation through every entry, and grows the panel so nothing overlaps.
    /// Other mods add their entries whenever they like (one only on the first tab switch) and
    /// shift the rows below by hand, so it settles again over a few frames whenever the panel's
    /// children change, and keeps the game's ticket number ("05") at the bottom where it belongs.
    /// </summary>
    internal sealed class TabEntry : MonoBehaviour
    {
        internal T17Button Sibling;
        private int settle;
        private int children = -1;

        private void OnEnable() { settle = 3; Fix(); }

        private void LateUpdate()
        {
            Transform tab = transform.parent;
            if (tab != null && tab.childCount != children)
            {
                children = tab.childCount;
                settle = 3;
            }
            if (settle <= 0) return;
            settle--;
            Fix();
        }

        private void Fix()
        {
            try
            {
                T17Button own = GetComponent<T17Button>();
                if (Sibling != null && own != null && own.GetDomain() == null && Sibling.GetDomain() != null)
                    own.SetEventSystem(Sibling.GetDomain());

                Transform tab = transform.parent;
                // The game's ticket number ("05") is kept last, but sits outside the layout: it
                // is placed by hand against the panel's bottom edge, and other mods' hand-made
                // shifts leave it between rows. It is put back at the bottom below.
                var ticket = tab.Find("TicketNumber") as RectTransform;
                if (ticket != null && ticket.GetSiblingIndex() != tab.childCount - 1) ticket.SetAsLastSibling();
                var buttons = new List<T17Button>();
                float height = 0;
                int shown = 0;
                for (int i = 0; i < tab.childCount; i++)
                {
                    Transform child = tab.GetChild(i);
                    if (!child.gameObject.activeSelf) continue;
                    var element = child.GetComponent<LayoutElement>();
                    if (element != null && element.ignoreLayout) continue;
                    shown++;
                    height += ((RectTransform)child).rect.height;
                    T17Button button = child.GetComponent<T17Button>();
                    if (button != null && button.enabled) buttons.Add(button);
                }
                for (int i = 0; i < buttons.Count; i++)
                {
                    Navigation nav = buttons[i].navigation;
                    nav.mode = Navigation.Mode.Explicit;
                    if (i > 0) nav.selectOnUp = buttons[i - 1];
                    if (i < buttons.Count - 1) nav.selectOnDown = buttons[i + 1];
                    else if (nav.selectOnDown is T17Button && buttons.Contains((T17Button)nav.selectOnDown)) nav.selectOnDown = null;
                    buttons[i].navigation = nav;
                }

                var layout = tab.GetComponent<VerticalLayoutGroup>();
                var rect = (RectTransform)tab;
                if (layout != null)
                {
                    height += layout.padding.top + layout.padding.bottom + layout.spacing * Mathf.Max(0, shown - 1);
                    // The panel's paper is one stretched image, so its torn bottom edge grows with
                    // the panel and eats into the margin under the last row. The game's Story list
                    // is about 6.5% taller than its rows for the same reason; match that.
                    float margin = Mathf.Round(height * 0.065f);
                    height += margin;
                    // Fit the panel to its entries — other mods grow it by a row each, and they and
                    // this together could leave it too short or too long. Keep the top edge under
                    // the tab: the tab's animator owns the position, so move the pivot instead.
                    if (Mathf.Abs(height - rect.rect.height) > 0.5f && Mathf.Approximately(rect.anchorMin.y, rect.anchorMax.y))
                    {
                        float top = (1f - rect.pivot.y) * rect.rect.height;
                        rect.pivot = new Vector2(rect.pivot.x, 1f - top / height);
                        rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);
                    }
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                    // Where the game puts it: its bottom edge on the panel's, raised by half the
                    // extra margin so it stays centred in the space under the last row. It is
                    // anchored to the panel's top, so that is the panel's height down, less its
                    // own lower half.
                    var ticketElement = ticket != null ? ticket.GetComponent<LayoutElement>() : null;
                    if (ticketElement != null && ticketElement.ignoreLayout &&
                        Mathf.Approximately(ticket.anchorMin.y, 1f) && Mathf.Approximately(ticket.anchorMax.y, 1f))
                    {
                        float y = -rect.rect.height + ticket.pivot.y * ticket.rect.height + margin * 0.5f;
                        if (Mathf.Abs(ticket.anchoredPosition.y - y) > 0.5f)
                            ticket.anchoredPosition = new Vector2(ticket.anchoredPosition.x, y);
                    }
                }
            }
            catch (Exception error)
            {
                settle = 0;
                Plugin.Log.LogWarning("Không sắp xếp được mục Bộ công cụ trong Cài đặt: " + error.Message);
            }
        }
    }
}
