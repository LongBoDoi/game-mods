using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;

namespace AutoFishCoop
{
    /// <summary>Puts the right player's settings in front of Auto Fishing before every piece of work it does.</summary>
    /// <remarks>Auto Fishing does its work from events Stardew raises once per split-screen player, but reads
    /// one settings object for all of them. These patches sit in front of those methods and lend it the
    /// settings of the player whose turn it is.</remarks>
    internal static class PerPlayerPatch
    {
        /// <summary>Whether the patches are in place.</summary>
        public static bool Working { get; private set; }

        /// <summary>From config.json: false hands Auto Fishing its own settings back and leaves it alone.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>The settings as they stand for each player, or null until Auto Fishing has run once.</summary>
        public static PerPlayerSettings Settings { get; private set; }

        /// <summary>Auto Fishing's own settings object always holds the mouse handle of the screen that is
        /// running, instead of the one it cached when it started.</summary>
        private static readonly ScreenMouseState Mouse = new ScreenMouseState();

        private static Dictionary<int, FishingOverride> Overrides = new Dictionary<int, FishingOverride>();

        private static IMonitor Monitor;

        /// <summary>Set if a patch ever throws: the patches then stand aside for the rest of the session rather
        /// than throwing once per tick inside someone else's mod.</summary>
        private static bool Broken;

        /// <summary>Patches Auto Fishing. <see cref="AutoFishingMod.Find" /> has to have succeeded first.</summary>
        /// <returns>Whether the patches went on.</returns>
        public static bool Apply(Harmony harmony, IMonitor monitor)
        {
            Monitor = monitor;

            try
            {
                harmony.Patch(AutoFishingMod.OnUpdateTicked,
                    prefix: new HarmonyMethod(typeof(PerPlayerPatch), nameof(BeforeUpdateTicked)));
                harmony.Patch(AutoFishingMod.OnButtonPressed,
                    prefix: new HarmonyMethod(typeof(PerPlayerPatch), nameof(BeforeButtonPressed)));
                harmony.Patch(AutoFishingMod.CloseCaughtFishDialogue,
                    prefix: new HarmonyMethod(typeof(PerPlayerPatch), nameof(BeforeCloseCaughtFish)));
                Working = true;
            }
            catch (Exception ex)
            {
                monitor.Log("Auto Fishing could not be patched, so its settings still apply to every player at "
                    + "once: " + ex.Message, LogLevel.Error);
                Working = false;
            }

            return Working;
        }

        /// <summary>Takes the overrides from a re-read config.json and starts everyone again from them.</summary>
        public static void Reload(Dictionary<int, FishingOverride> overrides)
        {
            Overrides = overrides ?? new Dictionary<int, FishingOverride>();
            if (Settings != null)
                Settings.Reload(Overrides);
        }

        /// <summary>Auto Fishing's work for one player: casting, hooking, landing the fish.</summary>
        private static void BeforeUpdateTicked(object __instance)
        {
            Switch(AutoFishingMod.AutoFishConfig, __instance);
        }

        /// <summary>Auto Fishing's own keys, which Stardew raises for the player who pressed them.</summary>
        private static void BeforeButtonPressed(object __instance)
        {
            Switch(AutoFishingMod.ModEntryConfig, __instance);
        }

        /// <summary>Auto Fishing dismisses the "you caught a fish" pose by writing a click into the mouse state
        /// it looked up when it started, which belongs to whichever screen was current then. This hands it a
        /// handle that is always the running screen's, so one player's catch stops reaching into another
        /// player's mouse.</summary>
        private static void BeforeCloseCaughtFish(object __instance)
        {
            if (Broken || !Enabled || __instance == null)
                return;

            try
            {
                if (!(AutoFishingMod.CachedMouseState.GetValue(__instance) is ScreenMouseState))
                    AutoFishingMod.CachedMouseState.SetValue(__instance, Mouse);
            }
            catch (Exception ex)
            {
                GiveUp(ex);
            }
        }

        /// <summary>Hands Auto Fishing the settings of the player whose screen is running.</summary>
        /// <param name="configField">The field holding the settings object, on <paramref name="instance" />.</param>
        private static void Switch(FieldInfo configField, object instance)
        {
            if (Broken || instance == null)
                return;

            try
            {
                object config = configField.GetValue(instance);
                if (config == null)
                    return;

                if (Settings == null)
                {
                    // The first run is the one moment Auto Fishing's settings are still exactly what its
                    // config.json said, which is what every player starts from.
                    if (!Enabled)
                        return;
                    Settings = new PerPlayerSettings(new SharedConfig(config, AutoFishingMod.Settings), Overrides);
                }

                // Before a save is loaded a screen's farmer is a placeholder, not the player, and there is
                // nothing for Auto Fishing to do for them anyway.
                if (!Enabled)
                    Settings.Release();
                else if (Context.IsWorldReady)
                    Settings.Enter(Game1.player.UniqueMultiplayerID, Game1.game1.instanceIndex);
            }
            catch (Exception ex)
            {
                GiveUp(ex);
            }
        }

        /// <summary>Stands aside for the rest of the session. A patch that throws would throw once a tick from
        /// inside another mod's code, so the first failure is the only one worth reporting.</summary>
        private static void GiveUp(Exception ex)
        {
            Broken = true;
            if (Monitor != null)
            {
                Monitor.Log("Something went wrong handing Auto Fishing a player's settings, so this mod is "
                    + "standing aside for the rest of this session. Auto Fishing's settings now apply to every "
                    + "player at once again.", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Debug);
            }
        }
    }

    /// <summary>A handle to the mouse state of whichever split-screen player's code is running.</summary>
    /// <remarks>Stardew gives every split-screen player their own input state and swaps <see cref="Game1.input" />
    /// to it while that player's code runs, so looking it up each time is what makes the handle follow the
    /// player. Auto Fishing holds one of these from startup instead, which is one screen's for the whole
    /// session.</remarks>
    internal sealed class ScreenMouseState : IReflectedField<MouseState>
    {
        private static readonly FieldInfo Field = AccessTools.DeclaredField(typeof(InputState), "_currentMouseState");

        public FieldInfo FieldInfo
        {
            get { return Field; }
        }

        public MouseState GetValue()
        {
            InputState input = Game1.input;
            return input != null ? (MouseState)Field.GetValue(input) : default(MouseState);
        }

        public void SetValue(MouseState value)
        {
            InputState input = Game1.input;
            if (input != null)
                Field.SetValue(input, value);
        }
    }
}
