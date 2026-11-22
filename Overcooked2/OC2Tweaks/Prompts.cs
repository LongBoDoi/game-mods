using System;
using System.Collections.Generic;
using HarmonyLib;
using Team17.Online;
using UnityEngine;
using UnityEngine.UI;

namespace OC2Tweaks
{
    /// <summary>
    /// The confirmations for kicking, banning and unbanning, shown in the game's own pop-up.
    /// Used by the settings screen and by the Kick button on the game's player cards.
    /// </summary>
    internal static class Prompts
    {
        internal static void AskRemove(DisplayParty party, Action<string> report)
        {
            string reason;
            if (!Moderation.HostCanManage(out reason)) { report(reason); return; }
            bool canBan = party.SteamId != null && Plugin.Bans.Ready;
            bool vi = Lang.Vi;
            Dialogs.Ask(MenuText.SafeName(party.Name),
                (vi ? "Đuổi người chơi này khỏi phòng? Họ vẫn có thể vào lại." : "Kick this player from the room? They can still rejoin.") +
                (party.Count > 1 ? (vi ? " Cả " + party.Count + " người chơi chung máy sẽ rời phòng."
                    : " All " + party.Count + " players on that machine will leave.") : "") +
                (canBan ? (vi ? " Chọn Cấm để chặn hẳn tài khoản này." : " Choose Ban to block this account for good.") : ""),
                vi ? "Đuổi" : "Kick", delegate { report(Moderation.Remove(party, false)); },
                canBan ? (vi ? "Cấm..." : "Ban...") : null, canBan ? (Action)delegate { AskBan(party, report); } : null);
        }

        internal static void AskBan(DisplayParty party, Action<string> report)
        {
            Dialogs.Ask(MenuText.SafeName(party.Name),
                Lang.T("Ban and kick this player? Their Steam account is saved and kicked automatically whenever it joins a room you host.",
                    "Cấm và đuổi người chơi này? Tài khoản Steam được lưu lại và sẽ tự động bị đuổi mỗi khi vào phòng bạn làm chủ."),
                Lang.T("Ban", "Cấm"), delegate { report(Moderation.Remove(party, true)); }, null, null);
        }

        internal static void AskUnban(BanRecord record, Action<string> report)
        {
            Dialogs.Ask(MenuText.SafeName(record.Name),
                Lang.T("Lift the ban on this account? They'll be able to join your rooms as normal.",
                    "Bỏ cấm tài khoản này? Họ sẽ có thể vào phòng của bạn như bình thường."),
                Lang.T("Unban", "Bỏ cấm"), delegate
                {
                    Plugin.Bans.Unban(record.SteamId);
                    report(Lang.T("Unbanned ", "Đã bỏ cấm ") + MenuText.SafeName(record.Name) + ".");
                }, null, null);
        }

        /// <summary>Where there is no hint line to write to: a pop-up with just OK.</summary>
        internal static void Tell(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            Plugin.Log.LogInfo("Bộ công cụ: " + message);
            Dialogs.Ask("OC2 Tweaks", message, "OK", delegate { }, null, null, false);
        }
    }

    /// <summary>The game's own pop-up, with the mod's words. Hủy is offered and is where the
    /// cursor starts, so a stray Enter never kicks or bans anyone.</summary>
    internal static class Dialogs
    {
        internal static void Ask(string title, string message, string confirm, Action onConfirm,
            string decline, Action onDecline, bool cancel = true)
        {
            T17DialogBox dialog = T17DialogBoxManager.GetDialog(false);
            if (dialog == null) { Plugin.Log.LogWarning("Không mở được hộp thoại xác nhận."); return; }
            dialog.Initialize(MenuText.Literal(title), MenuText.Literal(message), MenuText.Literal(confirm),
                decline != null ? MenuText.Literal(decline) : null, cancel ? MenuText.Literal(Lang.T("Cancel", "Hủy")) : null,
                T17DialogBox.Symbols.Unassigned);
            DialogFonts.Apply(dialog);
            dialog.OnConfirm = (T17DialogBox.DialogEvent)Delegate.Combine(dialog.OnConfirm, Guard(onConfirm));
            if (onDecline != null)
                dialog.OnDecline = (T17DialogBox.DialogEvent)Delegate.Combine(dialog.OnDecline, Guard(onDecline));
            if (cancel)
            {
                bool focused = false;
                dialog.OnUpdate = delegate(T17DialogBox box)
                {
                    if (focused || box == null || box.m_EventSystemForGamer == null || box.m_CancelButton == null) return;
                    focused = true;
                    box.m_EventSystemForGamer.SetSelectedGameObject(box.m_CancelButton.gameObject);
                };
            }
            dialog.Show();
        }

        private static T17DialogBox.DialogEvent Guard(Action action)
        {
            return delegate
            {
                try { action(); }
                catch (Exception error) { Plugin.Log.LogError("Bộ công cụ: thao tác thất bại: " + error); }
            };
        }
    }

    /// <summary>
    /// The pop-up's title and buttons use the game's headline font, which lacks Vietnamese
    /// letters such as ộ, ủ, ấ and cuts long names short. While the mod's pop-up is up they use
    /// the body font (which has them all) and shrink to fit; the pop-up is shared with the game,
    /// so everything is put back the moment it closes.
    /// </summary>
    internal sealed class DialogFonts : MonoBehaviour
    {
        private struct Saved
        {
            internal Text Text;
            internal Font Font;
            internal FontStyle Style;
            internal bool BestFit;
            internal int Min, Max;
        }

        private readonly List<Saved> saved = new List<Saved>();

        internal static void Apply(T17DialogBox dialog)
        {
            try
            {
                Font body = dialog.m_Message != null ? dialog.m_Message.font : null;
                if (body == null) return;
                foreach (DialogFonts old in dialog.GetComponents<DialogFonts>()) old.Restore();
                var keeper = dialog.gameObject.AddComponent<DialogFonts>();
                keeper.Swap(dialog.m_Title, body);
                foreach (T17Button button in new[] { dialog.m_ConfirmButton, dialog.m_DeclineButton, dialog.m_CancelButton })
                    if (button != null) keeper.Swap(button.GetComponentInChildren<T17Text>(true), body);
            }
            catch (Exception error) { Plugin.Log.LogWarning("Không đổi được phông chữ hộp thoại: " + error.Message); }
        }

        private void Swap(Text text, Font body)
        {
            if (text == null) return;
            saved.Add(new Saved
            {
                Text = text, Font = text.font, Style = text.fontStyle, BestFit = text.resizeTextForBestFit,
                Min = text.resizeTextMinSize, Max = text.resizeTextMaxSize
            });
            text.font = body;
            text.fontStyle = FontStyle.Bold;
            text.resizeTextMaxSize = text.fontSize;
            text.resizeTextMinSize = Mathf.Min(text.fontSize, 22);
            text.resizeTextForBestFit = true;
        }

        private void Restore()
        {
            foreach (Saved entry in saved)
            {
                if (entry.Text == null) continue;
                entry.Text.font = entry.Font;
                entry.Text.fontStyle = entry.Style;
                entry.Text.resizeTextForBestFit = entry.BestFit;
                entry.Text.resizeTextMinSize = entry.Min;
                entry.Text.resizeTextMaxSize = entry.Max;
            }
            saved.Clear();
            Destroy(this);
        }

        // The pop-up hides itself by deactivating, whichever button (or Esc) closed it.
        private void OnDisable() { Restore(); }
    }

    /// <summary>
    /// The game's player cards (pause menu, round results) carry a Kick button that this build
    /// hides on every platform and leaves empty. For the host it is shown and leads to the same
    /// kick/ban prompt as the settings screen, so a player can be removed mid-level too.
    /// </summary>
    internal static class PlayerCards
    {
        private static readonly System.Reflection.FieldInfo Options = AccessTools.Field(typeof(UIPlayerMenuBehaviour), "m_menuOptions");

        internal static void Validate()
        {
            if (Options == null ||
                AccessTools.Field(typeof(UIPlayerMenuBehaviour.MenuOption), "m_platformsActiveOn") == null ||
                AccessTools.Method(typeof(UIPlayerMenuBehaviour), "OnClick") == null ||
                AccessTools.Method(typeof(UIPlayerRootMenu), "ReCreateUIPlayers") == null)
                throw new MissingMemberException("UIPlayerMenuBehaviour/UIPlayerRootMenu members changed");
        }

        /// <summary>Lets every card in this root offer Kick; the game itself still hides it from
        /// clients and from your own players.</summary>
        [HarmonyPatch(typeof(UIPlayerRootMenu), "ReCreateUIPlayers")]
        internal static class AllowKick
        {
            private static void Prefix(UIPlayerRootMenu __instance)
            {
                if (Plugin.PlayerCardKickReady) __instance.m_canKickUsers = true;
            }
        }

        /// <summary>Un-hides the Kick entry on each card as it is created.</summary>
        [HarmonyPatch(typeof(UIPlayerMenuBehaviour), "Awake")]
        internal static class ShowKick
        {
            private static void Prefix(UIPlayerMenuBehaviour __instance)
            {
                if (!Plugin.PlayerCardKickReady) return;
                try
                {
                    var options = (System.Collections.Generic.List<UIPlayerMenuBehaviour.MenuOption>)Options.GetValue(__instance);
                    for (int i = 0; i < options.Count; i++)
                    {
                        UIPlayerMenuBehaviour.MenuOption option = options[i];
                        if (option.m_type != UIPlayerMenuBehaviour.UIPlayerMenuOptions.Kick) continue;
                        option.m_platformsActiveOn = -1;
                        options[i] = option;
                    }
                }
                catch (Exception error) { Plugin.Log.LogWarning("Không bật được nút Kick trên thẻ người chơi: " + error.Message); }
            }
        }

        /// <summary>Kick on a card opens the mod's kick/ban prompt instead of the game's empty
        /// handler. Patched on OnClick: the handler itself is empty and may be inlined.</summary>
        [HarmonyPatch(typeof(UIPlayerMenuBehaviour), "OnClick")]
        internal static class KickPrompt
        {
            private static bool Prefix(UIPlayerMenuBehaviour __instance, UIPlayerMenuBehaviour.UIPlayerMenuOptions _option)
            {
                if (_option != UIPlayerMenuBehaviour.UIPlayerMenuOptions.Kick || !Plugin.PlayerCardKickReady) return true;
                try
                {
                    User user = __instance.UserInfo;
                    DisplayParty target = null;
                    if (user != null)
                        foreach (DisplayParty party in Moderation.DisplayParties())
                            if (!party.IsLocal && (party.Primary.Machine == user.Machine ||
                                (user.SessionId != null && ReferenceEquals(party.Session, user.SessionId))))
                            { target = party; break; }
                    if (target == null) Prompts.Tell(Lang.T("This player has left the room or can't be kicked from your machine.",
                        "Người chơi này đã rời phòng hoặc không thể đuổi từ máy của bạn."));
                    else Prompts.AskRemove(target, Prompts.Tell);
                }
                catch (Exception error) { Plugin.Log.LogError("Bộ công cụ: không mở được lựa chọn đuổi: " + error); }
                return false;
            }
        }
    }
}
