using System;
using System.Collections.Generic;
using System.Globalization;
using Team17.Online;

namespace LobbyTools
{
    internal static class SessionStatus
    {
        private static IOnlineMultiplayerSessionCoordinator Coordinator()
        {
            var platform = GameUtils.RequestManagerInterface<IOnlinePlatformManager>();
            return platform == null ? null : platform.OnlineMultiplayerSessionCoordinator();
        }
        internal static bool IsHost()
        {
            var coordinator = Coordinator();
            return coordinator != null && coordinator.IsHost();
        }
        internal static bool IsInSession()
        {
            var coordinator = Coordinator();
            return coordinator != null && coordinator.Members() != null;
        }
    }
    internal sealed class RemoteParty
    {
        internal User Primary;
        internal IOnlineMultiplayerSessionUserId Session;
        internal string SteamId;
        internal string Name;
        internal int Count;
    }

    internal static class Moderation
    {
        internal static string SteamId(IOnlineMultiplayerSessionUserId session)
        {
            // Use transport identity rather than the display name or client-supplied join payload.
            var steam = session as SteamOnlineMultiplayerSessionUserId;
            if (steam == null || !steam.m_steamId.IsValid()) return null;
            return steam.m_steamId.m_SteamID.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool HostCanManage(out string reason)
        {
            if (!SessionStatus.IsInSession()) { reason = "Hãy tạo phòng trực tuyến để quản lý người chơi."; return false; }
            if (!SessionStatus.IsHost()) { reason = "Chỉ chủ phòng hiện tại mới có thể đuổi hoặc cấm người chơi."; return false; }
            IConnectionModeSwitchStatus status = ConnectionModeSwitcher.GetStatus();
            if (status == null || status.GetProgress() != eConnectionModeSwitchProgress.Complete)
            { reason = "Hãy chờ quá trình chuyển kết nối hoàn tất."; return false; }
            var users = ServerUserSystem.m_Users;
            if (users == null || users.Count == 0) { reason = "Đang chờ danh sách người chơi trong phòng."; return false; }
            reason = "Bạn là chủ phòng. Danh sách cập nhật liên tục, kể cả khi đang chơi màn.";
            return true;
        }

        internal static RemoteParty[] Parties()
        {
            string reason;
            if (!HostCanManage(out reason)) return new RemoteParty[0];
            var result = new List<RemoteParty>();
            var users = ServerUserSystem.m_Users;
            for (int i = 0; i < users.Count; i++)
            {
                User user = users._items[i];
                if (user == null || user.IsLocal || user.Machine == ServerUserSystem.s_LocalMachineId ||
                    user.SessionId == null || user.SessionId.IsLocal || user.SessionId.IsHost) continue;
                bool duplicate = false;
                foreach (RemoteParty other in result)
                    if (other.Primary.Machine == user.Machine) { duplicate = true; break; }
                if (duplicate) continue;
                int count = 0;
                for (int j = 0; j < users.Count; j++)
                    if (users._items[j] != null && users._items[j].Machine == user.Machine) count++;
                result.Add(new RemoteParty
                {
                    Primary = user, Session = user.SessionId, SteamId = SteamId(user.SessionId),
                    Name = user.SessionId.DisplayName ?? user.DisplayName ?? "Người chơi", Count = count
                });
            }
            return result.ToArray();
        }

        internal static string Remove(RemoteParty selected, bool ban)
        {
            string reason;
            if (!HostCanManage(out reason)) return reason;
            RemoteParty current = null;
            foreach (RemoteParty party in Parties())
                if (ReferenceEquals(party.Primary, selected.Primary) &&
                    ReferenceEquals(party.Session, selected.Session) && party.SteamId == selected.SteamId)
                { current = party; break; }
            if (current == null) return "Người chơi đã rời phòng hoặc phiên chơi đã thay đổi. Hãy chọn lại người chơi.";
            if (ban && (current.SteamId == null || !Plugin.Bans.Ready))
                return "Không thể cấm: cần mã tài khoản Steam hợp lệ và danh sách cấm có thể lưu được.";
            // Snapshot before RemoveUser: its callbacks disconnect and can recursively remove this party.
            var targets = new List<User>();
            var users = ServerUserSystem.m_Users;
            for (int i = 0; i < users.Count; i++)
            {
                User user = users._items[i];
                if (user != null && user.Machine == current.Primary.Machine)
                {
                    if (user.IsLocal || user.Machine == ServerUserSystem.s_LocalMachineId)
                        throw new InvalidOperationException("Không được phép đuổi người chơi trên máy của bạn.");
                    targets.Add(user);
                }
            }
            if (ban) Plugin.Bans.Ban(current.SteamId, current.Name);
            // Guests first; removing the primary triggers Server.OnUserRemoved -> Disconnect.
            targets.Remove(current.Primary);
            targets.Add(current.Primary);
            foreach (User target in targets)
                if (ServerUserSystem.m_Users.Contains(target)) ServerUserSystem.RemoveUser(target, true);
            Plugin.Log.LogInfo((ban ? "Đã cấm và đuổi " : "Đã đuổi ") + current.Name +
                " (" + (current.SteamId ?? "không rõ tài khoản") + "), nhóm " + current.Count + " người.");
            return ban ? "Đã lưu tài khoản và đuổi người chơi. Nếu gặp lại, họ sẽ tự động bị đuổi." :
                "Đã đuổi người chơi. Họ vẫn có thể tham gia lại.";
        }

        // Runs from the persistent UI object's Update even while the panel is closed.
        // Use the ordinary kick path once a saved ID appears; matchmaking stays unchanged.
        internal static void EnforceBans()
        {
            if (!Plugin.Bans.Ready) return;
            foreach (RemoteParty party in Parties())
            {
                if (party.SteamId == null || !Plugin.Bans.Contains(party.SteamId)) continue;
                Remove(party, false);
            }
        }
    }
}
