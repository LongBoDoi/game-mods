using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DIYChefFix
{
    /// <summary>
    /// Reflection handles into the third-party DIYChef FastInit plugin (dev.gua.overcooked.diychef,
    /// OC2DIYChef-FastInit\DIYChefFastInit.dll). Nothing here is referenced at compile time, so a
    /// DIYChef build with different internals just makes Init fail and this mod stay out of the way.
    /// </summary>
    internal static class DiyChef
    {
        internal const string Guid = "dev.gua.overcooked.diychef";

        internal static Type Customisation;
        internal static Type GridMenu;

        internal static MethodInfo LoadAllCustomChefs;   // IEnumerator LoadAllDIYChef(MetaGameProgress, bool)
        internal static MethodInfo ConfigureDefaults;    // void ConfigureChefDefaults(MetaGameProgress, bool)
        internal static MethodInfo GridShow;             // void ChefGridMenu.Show(BaseMenuBehaviour, FrontendChefCustomisation[], Action)

        private static FieldInfo preferred;              // KeyValuePair<string, string>[] preferredChefs
        private static FieldInfo defaultChefId;          // uint defaultChefID (127 = DIYChef off)
        private static FieldInfo initialChefId;          // uint initialChefID
        private static FieldInfo initialCustomId;        // byte initialDIYChefID (255 = none)
        private static FieldInfo customChefs;            // List<DIYChefAvatarData> diyChefs
        private static FieldInfo userCustomIds;          // byte[4] userDIYChefID
        private static FieldInfo registered;             // bool registered
        private static FieldInfo localClient;            // Client localClient
        private static FieldInfo customIdField;          // byte DIYChefAvatarData.id
        private static FieldInfo gridActive;             // ChefGridMenu ChefGridMenu.Active
        private static FieldInfo gridEntries;            // List<ChefGridEntry> ChefGridMenu.entries
        private static FieldInfo gridFailed;             // bool ChefGridMenu.failed
        private static PropertyInfo gridEnabled;         // static bool ChefGridMenu.Enabled
        private static PropertyInfo gridIsOpen;          // bool ChefGridMenu.IsOpen
        private static MethodInfo showWarning;           // void ShowWarningDialog(string)
        private static MethodInfo sendCustomChef;        // void SendClientDIYChefMessage(byte, User)
        private static MethodInfo sendAvatar;            // ClientMessenger.ChefAvatar(uint, User) (game)

        /// <summary>DIYChef's plugin folder, where prefer.txt and Resources\ live.</summary>
        internal static string Folder;

        internal static bool Init(out string problem)
        {
            problem = null;
            Customisation = AccessTools.TypeByName("OC2DIYChef.DIYChefCustomisation");
            GridMenu = AccessTools.TypeByName("OC2DIYChef.ChefGridMenu");
            Type customChef = AccessTools.TypeByName("OC2DIYChef.DIYChefAvatarData");
            if (Customisation == null || GridMenu == null || customChef == null)
            {
                problem = "DIYChef types not found";
                return false;
            }
            Folder = Path.GetDirectoryName(Customisation.Assembly.Location);

            LoadAllCustomChefs = AccessTools.Method(Customisation, "LoadAllDIYChef", new[] { typeof(MetaGameProgress), typeof(bool) });
            ConfigureDefaults = AccessTools.Method(Customisation, "ConfigureChefDefaults", new[] { typeof(MetaGameProgress), typeof(bool) });
            GridShow = AccessTools.Method(GridMenu, "Show", new[] { typeof(BaseMenuBehaviour), typeof(FrontendChefCustomisation[]), typeof(Action) });
            showWarning = AccessTools.Method(Customisation, "ShowWarningDialog", new[] { typeof(string) });
            sendCustomChef = AccessTools.Method(Customisation, "SendClientDIYChefMessage");
            sendAvatar = AccessTools.Method("ClientMessenger:ChefAvatar");

            preferred = AccessTools.Field(Customisation, "preferredChefs");
            defaultChefId = AccessTools.Field(Customisation, "defaultChefID");
            initialChefId = AccessTools.Field(Customisation, "initialChefID");
            initialCustomId = AccessTools.Field(Customisation, "initialDIYChefID");
            customChefs = AccessTools.Field(Customisation, "diyChefs");
            userCustomIds = AccessTools.Field(Customisation, "userDIYChefID");
            registered = AccessTools.Field(Customisation, "registered");
            localClient = AccessTools.Field(Customisation, "localClient");
            customIdField = AccessTools.Field(customChef, "id");
            gridActive = AccessTools.Field(GridMenu, "Active");
            gridEntries = AccessTools.Field(GridMenu, "entries");
            gridFailed = AccessTools.Field(GridMenu, "failed");
            gridEnabled = AccessTools.Property(GridMenu, "Enabled");
            gridIsOpen = AccessTools.Property(GridMenu, "IsOpen");

            object[] required =
            {
                LoadAllCustomChefs, ConfigureDefaults, GridShow, showWarning, sendCustomChef, sendAvatar,
                preferred, defaultChefId, initialChefId, initialCustomId, customChefs, userCustomIds,
                registered, localClient, customIdField, gridActive, gridEntries, gridFailed, gridEnabled, gridIsOpen
            };
            string[] names =
            {
                "LoadAllDIYChef", "ConfigureChefDefaults", "ChefGridMenu.Show", "ShowWarningDialog",
                "SendClientDIYChefMessage", "ClientMessenger.ChefAvatar", "preferredChefs", "defaultChefID",
                "initialChefID", "initialDIYChefID", "diyChefs", "userDIYChefID", "registered", "localClient",
                "DIYChefAvatarData.id", "ChefGridMenu.Active", "ChefGridMenu.entries", "ChefGridMenu.failed",
                "ChefGridMenu.Enabled", "ChefGridMenu.IsOpen"
            };
            List<string> missing = new List<string>();
            for (int i = 0; i < required.Length; i++)
            {
                if (required[i] == null) missing.Add(names[i]);
            }
            if (missing.Count > 0)
            {
                problem = "missing " + string.Join(", ", missing.ToArray());
                return false;
            }
            return true;
        }

        internal static KeyValuePair<string, string>[] Preferred
        {
            get { return (KeyValuePair<string, string>[])preferred.GetValue(null) ?? new KeyValuePair<string, string>[0]; }
            set { preferred.SetValue(null, value); }
        }

        internal static uint DefaultChefID
        {
            get { return (uint)defaultChefId.GetValue(null); }
            set { defaultChefId.SetValue(null, value); }
        }

        internal static uint InitialChefID
        {
            get { return (uint)initialChefId.GetValue(null); }
            set { initialChefId.SetValue(null, value); }
        }

        internal static byte InitialCustomID
        {
            get { return (byte)initialCustomId.GetValue(null); }
            set { initialCustomId.SetValue(null, value); }
        }

        internal static bool Registered { get { return (bool)registered.GetValue(null); } }

        internal static byte[] UserCustomIds { get { return (byte[])userCustomIds.GetValue(null); } }

        internal static bool HasLocalClient { get { return localClient.GetValue(null) != null; } }

        /// <summary>The registered custom (DIY) chefs; each one is a ChefAvatarData.</summary>
        internal static List<ChefAvatarData> CustomChefs
        {
            get
            {
                List<ChefAvatarData> list = new List<ChefAvatarData>();
                IEnumerable chefs = customChefs.GetValue(null) as IEnumerable;
                if (chefs == null) return list;
                foreach (object chef in chefs)
                {
                    ChefAvatarData data = chef as ChefAvatarData;
                    if (data != null) list.Add(data);
                }
                return list;
            }
        }

        internal static byte CustomId(ChefAvatarData chef) { return (byte)customIdField.GetValue(chef); }

        internal static bool GridEnabled { get { return (bool)gridEnabled.GetValue(null, null); } }

        internal static bool GridActive { get { return (UnityEngine.Object)gridActive.GetValue(null) != null; } }

        internal static bool GridIsOpen(Component grid) { return (bool)gridIsOpen.GetValue(grid, null); }

        internal static bool GridFailed(Component grid) { return (bool)gridFailed.GetValue(grid); }

        internal static int GridEntryCount(Component grid)
        {
            ICollection entries = gridEntries.GetValue(grid) as ICollection;
            return entries == null ? -1 : entries.Count;
        }

        internal static void ShowWarning(string message) { showWarning.Invoke(null, new object[] { message }); }

        internal static void SelectChef(Team17.Online.User user, uint official, byte custom)
        {
            sendAvatar.Invoke(null, new object[] { official, user });
            if (HasLocalClient)
            {
                sendCustomChef.Invoke(null, new object[] { custom, user });
            }
        }
    }
}
