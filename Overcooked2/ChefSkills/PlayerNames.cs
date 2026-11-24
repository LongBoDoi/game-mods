using System;
using System.Collections.Generic;
using Steamworks;
using Team17.Online;

namespace ChefSkills
{
    /// <summary>
    /// The names shown on this machine's skill cards: each player's name as the game knows it (the
    /// session user whose chef it is). The game names extra local players only after their device
    /// ("Controller", "Keyboard"), so those are shown as the first player's name with their number
    /// ("Alex 2"), the way other machines' extra players are named.
    /// </summary>
    internal static class PlayerNames
    {
        /// <summary>The name for a local player, from their chefs' entity IDs and their place among this machine's players.</summary>
        internal static string Local(List<uint> chefs, int rank)
        {
            string name = UserName(chefs);
            if (string.IsNullOrEmpty(name) || IsDeviceName(name))
            {
                string first = FirstName();
                name = string.IsNullOrEmpty(first)
                    ? Lang.T("PLAYER ", "NGƯỜI CHƠI ") + (rank + 1)
                    : rank == 0 ? first : first + " " + (rank + 1);
            }
            return name.Trim();
        }

        /// <summary>The display name of the session user who plays one of these chefs.</summary>
        private static string UserName(List<uint> chefs)
        {
            if (chefs == null || chefs.Count == 0) return null;
            try
            {
                FastList<User> users = ClientUserSystem.m_Users;
                if (users == null) return null;
                for (int i = 0; i < users.Count; i++)
                {
                    User user = users._items[i];
                    if (user == null || !user.IsLocal) continue;
                    if ((user.EntityID != 0 && chefs.Contains(user.EntityID)) || (user.Entity2ID != 0 && chefs.Contains(user.Entity2ID)))
                        return user.DisplayName;
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không đọc được tên người chơi: " + error.Message);
            }
            return null;
        }

        /// <summary>This machine's own name: its first local user's, or the Steam name.</summary>
        private static string FirstName()
        {
            try
            {
                FastList<User> users = ClientUserSystem.m_Users;
                if (users != null)
                    for (int i = 0; i < users.Count; i++)
                    {
                        User user = users._items[i];
                        if (user != null && user.IsLocal && !string.IsNullOrEmpty(user.DisplayName) && !IsDeviceName(user.DisplayName))
                            return user.DisplayName;
                    }
                if (SteamPlayerManager.Initialized) return SteamFriends.GetPersonaName();
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không đọc được tên Steam: " + error.Message);
            }
            return null;
        }

        /// <summary>The game's placeholder names for a local player without an account ("Controller", "Keyboard").</summary>
        private static bool IsDeviceName(string name)
        {
            if (name == "Unknown") return true;
            try
            {
                return name == Localization.Get("StartScreen.Engagement.ControllerDevice") ||
                    name == Localization.Get("StartScreen.Engagement.KeyboardDevice");
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
