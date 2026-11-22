using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace EmoteSync
{
    /// <summary>
    /// Emote Sync: makes the emote settings of Arcade MOD (HostTools, com.ch3ngyz.plugin.HostUtilities)
    /// belong to each player instead of to whoever is watching.
    ///
    /// On its own, HostTools rewrites every chef's emote bubble with the settings of the machine it
    /// runs on, so the host sees the host's texts on everybody and each client sees their own. With
    /// Emote Sync, each machine shares its emote wheel settings (Do_Not_Replace / Use_Custom_Text /
    /// Use_Voice_Pack_File_Name per emote, font size, font colour) and its custom texts through its
    /// Steam lobby member data, and every bubble is drawn with the settings of the player who sent
    /// the emote. Each emote a player starts is also shared, with exactly what its bubble shows
    /// (including the voice-pack file name the sender's machine picked), so every screen shows the
    /// same bubble over the same lobby card or chef. Bubbles are recognised by the game's
    /// localisation key rather than their text, so every emote works in every game language.
    /// </summary>
    [BepInPlugin(Id, "Emote Sync", Version)]
    // Loads after TiengViet when it is installed, so the language it sets applies from the start.
    [BepInDependency("com.manhl.overcooked2.tiengviet", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(HostTools.Guid)]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.emotesync";
        public const string Version = "1.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                Lang.T("Each player uses their own emote text and text style (Arcade MOD / HostTools), synced for the whole room.",
                    "Mỗi người chơi dùng chữ và kiểu chữ biểu cảm của riêng mình (Arcade MOD / HostTools), đồng bộ cho cả phòng."));

            if (!HostTools.Init())
            {
                Log.LogError("Không đọc được phần biểu cảm của Arcade MOD (HostTools) bản này; Emote Sync không chạy.");
                return;
            }

            _harmony = new Harmony(Id);
            try
            {
                // HostTools' own bubble rewrite is skipped while Emote Sync is on; EmoteView does the
                // same job with the sender's settings instead of the local ones.
                _harmony.Patch(HostTools.TextPostfix,
                    prefix: new HarmonyMethod(typeof(Plugin), "SkipHostToolsText"));
                _harmony.Patch(AccessTools.Method(typeof(ClientEmoteWheel), "UpdateEmoteWheel"),
                    postfix: new HarmonyMethod(typeof(Plugin), "AfterUpdateEmoteWheel"));
                _harmony.Patch(AccessTools.Method(typeof(ClientEmoteWheel), "StartEmote", new[] { typeof(int) }),
                    prefix: new HarmonyMethod(typeof(Plugin), "BeforeStartEmote"));
                _harmony.Patch(AccessTools.Method(typeof(ClientEmoteWheel), "RequestEmoteStart", new[] { typeof(int) }),
                    postfix: new HarmonyMethod(typeof(Plugin), "AfterRequestEmoteStart"));
                _harmony.Patch(AccessTools.Method(typeof(ClientEmoteWheel), "ProcessServerMessage"),
                    prefix: new HarmonyMethod(typeof(Plugin), "BeforeProcessServerMessage"));
                var afterFindEntry = new HarmonyMethod(typeof(Plugin), "AfterFindEntry");
                afterFindEntry.priority = Priority.Last;
                _harmony.Patch(AccessTools.Method(typeof(AudioManager), "FindEntry", new[] { typeof(GameOneShotAudioTag) }),
                    postfix: afterFindEntry);
                Log.LogInfo("Đã tải Emote Sync " + Version + ": chữ biểu cảm theo từng người chơi.");
            }
            catch (Exception error)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
                Log.LogError("Không thể bật Emote Sync: " + error);
            }
        }

        private void Update()
        {
            if (_harmony == null) return;
            try
            {
                EmoteNet.Tick(Enabled.Value);
            }
            catch (Exception error)
            {
                EmoteView.LogOnce("tick", error);
            }
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }

        private static bool SkipHostToolsText()
        {
            return !Enabled.Value;
        }

        private static void AfterUpdateEmoteWheel(ClientEmoteWheel __instance)
        {
            if (!Enabled.Value) return;
            try
            {
                EmoteView.Update(__instance);
            }
            catch (Exception error)
            {
                EmoteView.LogOnce("view", error);
            }
        }

        private static bool BeforeStartEmote(ClientEmoteWheel __instance, int _emoteIdx)
        {
            if (!Enabled.Value) return true;
            try
            {
                return EmoteView.BeforeStart(__instance, _emoteIdx);
            }
            catch (Exception error)
            {
                EmoteView.LogOnce("start", error);
                return true;
            }
        }

        private static void AfterRequestEmoteStart(ClientEmoteWheel __instance, int _emoteIdx)
        {
            if (!Enabled.Value) return;
            try
            {
                EmoteView.LocalEmote(__instance, _emoteIdx);
            }
            catch (Exception error)
            {
                EmoteView.LogOnce("send", error);
            }
        }

        private static bool BeforeProcessServerMessage(ClientEmoteWheel __instance)
        {
            if (!Enabled.Value) return true;
            try
            {
                return EmoteView.AcceptGameMessage(__instance);
            }
            catch (Exception error)
            {
                EmoteView.LogOnce("message", error);
                return true;
            }
        }

        private static void AfterFindEntry(GameOneShotAudioTag _tag)
        {
            try
            {
                EmoteView.SoundPicked(_tag);
            }
            catch (Exception error)
            {
                EmoteView.LogOnce("sound", error);
            }
        }
    }
}
