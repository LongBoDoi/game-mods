using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Team17.Online;
using UnityEngine;

namespace DIYChefFix
{
    /// <summary>
    /// What goes wrong in DIYChef FastInit 2.4.0, and what this does about it:
    ///
    /// 1. The default chef is resolved once, when the global save loads, against the chefs the save
    ///    has unlocked at that moment (DLC ownership can still be pending then). A first prefer.txt
    ///    entry that isn't in that list is skipped, and when no entry fits DIYChef falls back to
    ///    Chef_Snowman without checking whether Snowman is unlocked either, and says only "Invalid
    ///    default chef". Here the default is resolved again whenever the unlocked chefs change, the
    ///    fallback is a chef the save really has, names match without regard to case, and the warning
    ///    names the entry and the reason, once things have settled.
    ///
    /// 2. The chef grid (and the carousel and the lobby D-pad switch) only offers the chefs listed in
    ///    prefer.txt that are unlocked. With a prefer.txt that only names a default chef, that is one
    ///    chef or none, and with none the grid silently never opens. Here, after the prefer.txt
    ///    entries, every other unlocked chef is offered too (config: ShowAllChefs).
    ///
    /// 3. Custom chefs in Resources\ are only loaded if prefer.txt lists them. Here every complete
    ///    custom-chef folder is loaded, after the prefer.txt entries (config: LoadAllCustomChefs).
    /// </summary>
    internal static class Fixes
    {
        private const uint NoChef = 127;
        private const byte NoCustomChef = byte.MaxValue;

        /// <summary>The first chef the player wrote in prefer.txt, before anything was added.</summary>
        private static string firstWanted;

        /// <summary>Every chef the player wrote in prefer.txt (with the Chef_ prefix). Only these
        /// can become the default; chefs this mod adds to the list never do.</summary>
        private static readonly HashSet<string> userListed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static int unlockedSignature;
        private static bool configured;
        private static string pendingWarning;
        private static float warnAt;
        private static readonly HashSet<string> warned = new HashSet<string>();
        private static string lastSummary;
        private static float nextPoll;

        private static string Normalize(string name)
        {
            return name.StartsWith("Chef_", StringComparison.OrdinalIgnoreCase) ? name : "Chef_" + name;
        }

        private static int Signature(ChefAvatarData[] unlocked)
        {
            int hash = unlocked.Length;
            for (int i = 0; i < unlocked.Length; i++)
            {
                hash = hash * 31 + (unlocked[i] == null ? 0 : unlocked[i].GetInstanceID());
            }
            return hash;
        }

        private static int IndexOfHead(ChefAvatarData[] avatars, string head)
        {
            for (int i = 0; i < avatars.Length; i++)
            {
                if (avatars[i] != null && string.Equals(avatars[i].HeadName, head, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static ChefAvatarData FindCustom(List<ChefAvatarData> custom, string head)
        {
            string normalized = Normalize(head);
            foreach (ChefAvatarData chef in custom)
            {
                if (string.Equals(chef.HeadName, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return chef;
                }
            }
            return null;
        }

        // ---- Fix 3: load every custom chef folder -------------------------------------------------

        /// <summary>Prefix on DIYChef's LoadAllDIYChef: adds the custom-chef folders prefer.txt doesn't list.</summary>
        internal static void BeforeLoadCustomChefs(MetaGameProgress metaGameProgress)
        {
            try
            {
                KeyValuePair<string, string>[] wanted = DiyChef.Preferred;
                // DIYChef matches official names case-sensitively and silently drops a miss, so
                // spell them the way the game does first ("chef_black_cat" -> "Chef_Black_cat").
                ChefAvatarData[] avatars = metaGameProgress != null ? metaGameProgress.AvatarDirectory.Avatars : new ChefAvatarData[0];
                for (int i = 0; i < wanted.Length; i++)
                {
                    int index = IndexOfHead(avatars, wanted[i].Key);
                    if (index >= 0 && avatars[index].HeadName != wanted[i].Key)
                    {
                        wanted[i] = new KeyValuePair<string, string>(avatars[index].HeadName, wanted[i].Value);
                    }
                }
                DiyChef.Preferred = wanted;
                firstWanted = wanted.Length > 0 ? wanted[0].Key : null;
                userListed.Clear();
                foreach (KeyValuePair<string, string> pair in wanted) userListed.Add(Normalize(pair.Key));
                if (!Plugin.LoadAllCustomChefs.Value) return;

                string resources = Path.Combine(DiyChef.Folder, "Resources");
                if (!Directory.Exists(resources)) return;

                HashSet<string> listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> pair in wanted) listed.Add(Normalize(pair.Key));

                List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>(wanted);
                string[] folders = Directory.GetDirectories(resources);
                Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
                int added = 0;
                foreach (string folder in folders)
                {
                    string name = Path.GetFileName(folder);
                    if (string.Equals(name, "HATS", StringComparison.OrdinalIgnoreCase)) continue;
                    // Only complete chefs, so this never adds DIYChef's "missing file" warnings.
                    if (!File.Exists(Path.Combine(folder, "INFO")) ||
                        !File.Exists(Path.Combine(folder, "t_Head.png")) ||
                        !File.Exists(Path.Combine(folder, "m_Head.txt"))) continue;
                    if (!listed.Add(Normalize(name))) continue;
                    list.Add(new KeyValuePair<string, string>(name, ""));
                    added++;
                }
                if (added > 0)
                {
                    DiyChef.Preferred = list.ToArray();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Loading the unlisted custom chefs failed: " + e);
            }
        }

        // ---- Fixes 1 and 2: default chef and the chef list -------------------------------------------

        /// <summary>Prefix that replaces DIYChef's ConfigureChefDefaults. Falls back to it on error.</summary>
        internal static bool ConfigureDefaults(MetaGameProgress progress, bool hasPreferences)
        {
            try
            {
                Configure(progress, hasPreferences);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Default chef fix failed; DIYChef's own code runs instead: " + e);
                return true;
            }
        }

        private static void Configure(MetaGameProgress progress, bool hasPreferences)
        {
            ChefAvatarData[] avatars = progress.AvatarDirectory.Avatars;
            ChefAvatarData[] unlocked = progress.GetUnlockedAvatars();
            HashSet<ChefAvatarData> isUnlocked = new HashSet<ChefAvatarData>(unlocked);
            List<ChefAvatarData> custom = DiyChef.CustomChefs;
            KeyValuePair<string, string>[] wanted = DiyChef.Preferred;

            unlockedSignature = Signature(unlocked);
            configured = true;
            LogLocked(avatars, isUnlocked);
            DiyChef.DefaultChefID = NoChef;
            DiyChef.InitialChefID = NoChef;
            DiyChef.InitialCustomID = NoCustomChef;
            if (!hasPreferences && wanted.Length == 0)
            {
                // No prefer.txt entries and no custom chefs: DIYChef stays off, like the original.
                pendingWarning = null;
                Report("DIYChef off: prefer.txt lists no chefs and there are no custom chefs.");
                return;
            }

            // The list in prefer.txt order, with names spelled the way the game spells them.
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in wanted)
            {
                string key = pair.Key;
                ChefAvatarData chef = FindCustom(custom, key);
                if (chef != null)
                {
                    key = chef.HeadName;
                }
                else
                {
                    int index = IndexOfHead(avatars, key);
                    if (index >= 0) key = avatars[index].HeadName;
                }
                if (seen.Add(key)) list.Add(new KeyValuePair<string, string>(key, pair.Value));
            }

            int baseIndex = -1;      // the official chef DIYChef uses as its default (and under custom chefs)
            int initialIndex = -1;   // what a new local player starts as
            byte initialCustom = NoCustomChef;
            string initialName = null;
            foreach (KeyValuePair<string, string> pair in list)
            {
                if (!userListed.Contains(Normalize(pair.Key))) continue;
                ChefAvatarData chef = FindCustom(custom, pair.Key);
                if (chef != null)
                {
                    if (initialName == null)
                    {
                        initialName = chef.HeadName;
                        initialCustom = DiyChef.CustomId(chef);
                    }
                    continue;
                }
                int index = IndexOfHead(avatars, pair.Key);
                if (index >= 0 && isUnlocked.Contains(avatars[index]))
                {
                    if (baseIndex < 0) baseIndex = index;
                    if (initialName == null)
                    {
                        initialName = avatars[index].HeadName;
                        initialIndex = index;
                    }
                }
            }

            if (baseIndex < 0)
            {
                // DIYChef used Chef_Snowman here even when the save hasn't got it. Use Snowman only
                // if it is unlocked, else the first chef the save does have.
                int snowman = IndexOfHead(avatars, "Chef_Snowman");
                if (snowman >= 0 && isUnlocked.Contains(avatars[snowman])) baseIndex = snowman;
                else if (unlocked.Length > 0) baseIndex = Array.IndexOf(avatars, unlocked[0]);
                if (baseIndex < 0) baseIndex = snowman >= 0 ? snowman : 0;
            }
            if (initialName == null)
            {
                initialIndex = baseIndex;
                initialName = avatars[baseIndex].HeadName;
            }

            if (Plugin.ShowAllChefs.Value)
            {
                foreach (ChefAvatarData chef in custom)
                {
                    if (seen.Add(chef.HeadName)) list.Add(new KeyValuePair<string, string>(chef.HeadName, ""));
                }
                foreach (ChefAvatarData chef in unlocked)
                {
                    if (chef != null && seen.Add(chef.HeadName)) list.Add(new KeyValuePair<string, string>(chef.HeadName, ""));
                }
            }
            else if (seen.Add(avatars[baseIndex].HeadName))
            {
                list.Add(new KeyValuePair<string, string>(avatars[baseIndex].HeadName, ""));
            }

            DiyChef.Preferred = list.ToArray();
            DiyChef.DefaultChefID = (uint)baseIndex;
            DiyChef.InitialChefID = (uint)(initialIndex >= 0 ? initialIndex : baseIndex);
            DiyChef.InitialCustomID = initialCustom;

            // Explain a first prefer.txt entry that can't be used.
            pendingWarning = null;
            string problem = null;
            if (firstWanted != null && FindCustom(custom, firstWanted) == null)
            {
                int index = IndexOfHead(avatars, firstWanted);
                if (index < 0 && Directory.Exists(Path.Combine(Path.Combine(DiyChef.Folder, "Resources"), firstWanted)))
                {
                    // A custom chef that failed to load: DIYChef's own warning already says why.
                }
                else if (index < 0)
                {
                    problem = Lang.T(
                        "Default chef \"" + firstWanted + "\" from prefer.txt doesn't exist (check the spelling, or its folder in Resources).",
                        "Đầu bếp mặc định \"" + firstWanted + "\" trong prefer.txt không tồn tại (kiểm tra lại tên, hoặc thư mục của nó trong Resources).");
                }
                else if (!isUnlocked.Contains(avatars[index]))
                {
                    problem = avatars[index].ForDlc != null
                        ? Lang.T(
                            "Default chef \"" + avatars[index].HeadName + "\" belongs to a DLC this copy of the game doesn't have.",
                            "Đầu bếp mặc định \"" + avatars[index].HeadName + "\" thuộc một DLC mà bản game này không có.")
                        : Lang.T(
                            "Default chef \"" + avatars[index].HeadName + "\" isn't unlocked in this save yet (DIYChef Fix setting UnlockBaseGameChefs allows it).",
                            "Đầu bếp mặc định \"" + avatars[index].HeadName + "\" chưa được mở khóa trong bản lưu này (bật UnlockBaseGameChefs của DIYChef Fix để dùng được).");
                }
            }
            if (problem != null)
            {
                pendingWarning = problem + "\n" + Lang.T("Using: ", "Đang dùng: ") + initialName;
                // Give DLC ownership a few seconds to settle before saying anything.
                warnAt = Time.realtimeSinceStartup + 8f;
            }

            Report("Default chef " + initialName + (initialCustom != NoCustomChef ? " (custom #" + initialCustom + ")" : " (#" + DiyChef.InitialChefID + ")") +
                   ", base " + avatars[baseIndex].HeadName + "; " + list.Count + " chefs offered (" + custom.Count + " custom), " +
                   unlocked.Length + "/" + avatars.Length + " official chefs unlocked" +
                   (problem != null ? "; first prefer.txt entry unusable: " + firstWanted : ""));
        }

        private static string lastLocked;

        /// <summary>Logs the chefs the save hasn't got (when that changes), to explain choices.</summary>
        private static void LogLocked(ChefAvatarData[] avatars, HashSet<ChefAvatarData> isUnlocked)
        {
            StringBuilder text = new StringBuilder();
            int count = 0;
            foreach (ChefAvatarData chef in avatars)
            {
                if (chef == null || isUnlocked.Contains(chef)) continue;
                if (count++ > 0) text.Append(", ");
                text.Append(chef.HeadName);
                if (chef.ForDlc != null) text.Append(" [DLC ").Append(chef.ForDlc.m_DLCID).Append(']');
            }
            string summary = count + " chefs not unlocked" + (count > 0 ? ": " + text : "");
            if (summary == lastLocked) return;
            lastLocked = summary;
            Plugin.Log.LogInfo(summary);
        }

        private static void Report(string summary)
        {
            if (summary == lastSummary) return;
            lastSummary = summary;
            Plugin.Log.LogInfo(summary);
        }

        // ---- Optional: base-game chefs the save hasn't unlocked yet ---------------------------------

        /// <summary>
        /// Postfix on MetaGameProgress.GetUnlockedAvatars (config: UnlockBaseGameChefs): also returns
        /// the base-game chefs that are still locked, so they can be picked and used as the default.
        /// DLC chefs are never added: they stay tied to owning the DLC. Nothing is written to the save.
        /// </summary>
        internal static void AddBaseGameChefs(MetaGameProgress __instance, ref ChefAvatarData[] __result)
        {
            if (!Plugin.UnlockBaseGameChefs.Value || __result == null) return;
            try
            {
                ChefAvatarData[] all = __instance.AvatarDirectory.Avatars;
                HashSet<ChefAvatarData> have = new HashSet<ChefAvatarData>(__result);
                List<ChefAvatarData> list = null;
                foreach (ChefAvatarData chef in all)
                {
                    if (chef == null || have.Contains(chef) || chef.ForDlc != null || !chef.ActuallyAllowed || !chef.m_PC) continue;
                    if (list == null) list = new List<ChefAvatarData>(__result);
                    list.Add(chef);
                }
                if (list == null) return;
                // Keep the game's order (directory order), which is the order the carousel shows.
                list.Sort((a, b) => Array.IndexOf(all, a).CompareTo(Array.IndexOf(all, b)));
                __result = list.ToArray();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Adding the locked base-game chefs failed: " + e);
            }
        }

        // ---- Re-check after the save's unlocks settle -----------------------------------------------

        internal static void Poll()
        {
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 1f;
            try
            {
                if (!configured || !DiyChef.Registered) return;
                MetaGameProgress progress = GameUtils.GetMetaGameProgress();
                if (progress == null) return;

                if (Signature(progress.GetUnlockedAvatars()) != unlockedSignature)
                {
                    uint oldChef = DiyChef.InitialChefID;
                    byte oldCustom = DiyChef.InitialCustomID;
                    DiyChef.ConfigureDefaults.Invoke(null, new object[] { progress, DiyChef.Preferred.Length != 0 });
                    FollowNewDefault(oldChef, oldCustom);
                }

                if (pendingWarning != null && Time.realtimeSinceStartup >= warnAt && !T17DialogBoxManager.HasAnyOpenDialogs())
                {
                    string message = pendingWarning;
                    pendingWarning = null;
                    Plugin.Log.LogWarning(message.Replace("\n", " "));
                    if (warned.Add(message)) DiyChef.ShowWarning(message);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Default chef re-check failed: " + e);
                configured = false;
            }
        }

        /// <summary>
        /// Local players still on the default that was picked before the unlocks settled move to
        /// the new default; anyone who chose a chef keeps it. Only in the menus, never mid-level.
        /// </summary>
        private static void FollowNewDefault(uint oldChef, byte oldCustom)
        {
            uint newChef = DiyChef.InitialChefID;
            byte newCustom = DiyChef.InitialCustomID;
            if (newChef == NoChef || (newChef == oldChef && newCustom == oldCustom)) return;
            if (T17FrontendFlow.Instance == null || DiyChef.GridActive) return;

            byte[] customIds = DiyChef.UserCustomIds;
            FastList<User> users = ClientUserSystem.m_Users;
            bool changed = false;
            for (int i = 0; i < users.Count && i < customIds.Length; i++)
            {
                User user = users._items[i];
                if (!user.IsLocal || user.SelectedChefAvatar != oldChef || customIds[i] != oldCustom) continue;
                customIds[i] = newCustom;
                DiyChef.SelectChef(user, newChef, newCustom);
                changed = true;
            }
            if (changed && ClientUserSystem.usersChanged != null) ClientUserSystem.usersChanged();
        }

        // ---- Say why the grid didn't open -----------------------------------------------------------

        internal static void AfterGridShow(BaseMenuBehaviour menu, FrontendChefCustomisation[] views)
        {
            try
            {
                if (menu == null || !DiyChef.GridEnabled) return;
                Component grid = menu.GetComponent(DiyChef.GridMenu);
                if (grid == null || DiyChef.GridIsOpen(grid)) return;

                int local = 0, active = 0;
                FastList<User> users = ClientUserSystem.m_Users;
                for (int i = 0; i < users.Count; i++)
                {
                    if (!users._items[i].IsLocal) continue;
                    local++;
                    if (views != null && i < views.Length && views[i] != null && views[i].IsActive()) active++;
                }
                StringBuilder text = new StringBuilder("Chef grid did not open: ");
                text.Append("entries=").Append(DiyChef.GridEntryCount(grid))
                    .Append(" localPlayers=").Append(local)
                    .Append(" activeViews=").Append(active)
                    .Append(" failedEarlier=").Append(DiyChef.GridFailed(grid));
                Plugin.Log.LogWarning(text.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Chef grid check failed: " + e);
            }
        }
    }
}
