using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace MonitorCoop
{
    /// <summary>Gives every local co-op player their own monitor, laid out the way the monitors are.</summary>
    public sealed class ModEntry : Mod
    {
        /// <summary>How often the monitor layout and player count are checked, in ticks (60 per second).</summary>
        private const int CheckEveryTicks = 30;

        private ModConfig Config;
        private WindowSpanner Spanner;

        /// <summary>Whether the toggle key has switched the mod off for this session.</summary>
        private bool ToggledOff;

        /// <summary>False when the game's layout code could not be patched, which disables the mod.</summary>
        private bool Working;

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();
            Config.Validate(Monitor);
            Spanner = new WindowSpanner(Monitor, Config);

            Harmony harmony = new Harmony(ModManifest.UniqueID);
            Working = SplitScreenPatch.Apply(harmony, Monitor);
            SplitScreenPatch.ZoomModifier = Config.ZoomModifier;

            // The layout is what matters; if only the cursor patch fails the mod still works, with the
            // game's own cursor handling and the offset that comes with it.
            if (Working)
            {
                CursorPatch.Apply(harmony, Monitor);
                CursorPatch.ConfinePointer = Config.ConfinePointerToOwnScreen;
            }

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            helper.Events.Input.ButtonsChanged += OnButtonsChanged;

            helper.ConsoleCommands.Add("monitorcoop",
                "Shows the monitors, the split-screen players and where they are.\n\n"
                + "Usage: monitorcoop\n"
                + "Usage: monitorcoop reload   (re-reads config.json)",
                OnCommand);
        }

        /// <summary>Keeps the window in step with the number of players and the monitors attached.</summary>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // Split-screen raises these events once per player; the window belongs to the first one.
            if (Context.ScreenId != 0 || !Working)
                return;

            if (e.IsMultipleOf(CheckEveryTicks))
                Sync();

            Spanner.Reassert();
        }

        /// <summary>Extra players are dropped when the game goes back to the title, so the window goes back
        /// with them.</summary>
        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            if (Context.ScreenId == 0 && Working)
                Sync();
        }

        private void OnButtonsChanged(object sender, ButtonsChangedEventArgs e)
        {
            if (Context.ScreenId != 0 || !Working || !Config.ToggleKey.JustPressed())
                return;

            ToggledOff = !ToggledOff;
            Sync();

            string message = ToggledOff ? "Monitor co-op off" : "Monitor co-op on";
            Monitor.Log(message + ".", LogLevel.Info);
            if (Context.IsWorldReady)
                Game1.addHUDMessage(HUDMessage.ForCornerTextbox(message));
        }

        /// <summary>Works out what the window should look like now and changes it if that differs.</summary>
        private void Sync()
        {
            SplitPlan wanted = BuildPlan();

            if (wanted == null)
            {
                if (Spanner.IsSpanning)
                    Spanner.Restore();
                return;
            }

            if (!wanted.Matches(Spanner.Plan))
                Spanner.Apply(wanted);
        }

        /// <summary>The layout the current monitors and players call for, or null to leave the window to the
        /// game (the mod is off, there is one player, or there is one monitor).</summary>
        private SplitPlan BuildPlan()
        {
            if (!Config.Enabled || ToggledOff)
                return null;

            GameRunner runner = GameRunner.instance;
            if (runner == null || runner.gameInstances == null)
                return null;

            int players = runner.gameInstances.Count;
            if (players < 2)
                return null;

            List<LayoutRect> displays = WindowSpanner.GetDisplays();
            return Layout.Build(displays, Layout.Order(displays, Config.Displays, Config.PlayerOneOnPrimary), players);
        }

        private void OnCommand(string name, string[] args)
        {
            if (args.Length > 0 && args[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                // Hand the window back before swapping the spanner out, or the new one would not know how
                // to undo what the old one did.
                Spanner.Restore();

                Config = Helper.ReadConfig<ModConfig>();
                Config.Validate(Monitor);
                Spanner = new WindowSpanner(Monitor, Config);
                SplitScreenPatch.ZoomModifier = Config.ZoomModifier;
                CursorPatch.ConfinePointer = Config.ConfinePointerToOwnScreen;
                ToggledOff = false;
                Sync();
                Monitor.Log("config.json re-read.", LogLevel.Info);
                return;
            }

            StringBuilder report = new StringBuilder();
            report.AppendLine("Monitors the game can see (0 is the primary):");
            List<LayoutRect> displays = WindowSpanner.GetDisplays();
            if (displays.Count == 0)
                report.AppendLine("    none reported (is the game still starting up?)");
            for (int i = 0; i < displays.Count; i++)
                report.AppendLine("    " + i + ": " + displays[i]);

            int players = GameRunner.instance != null && GameRunner.instance.gameInstances != null
                ? GameRunner.instance.gameInstances.Count
                : 0;
            report.AppendLine("Players in split-screen: " + players);

            if (!Working)
                report.AppendLine("The mod is disabled: the game's layout code could not be patched.");
            else if (!Config.Enabled)
                report.AppendLine("Turned off in config.json.");
            else if (ToggledOff)
                report.AppendLine("Turned off with " + Config.ToggleKey + ".");

            report.Append(Spanner.IsSpanning
                ? "Now spanning: " + Spanner.Plan.Describe()
                : "Not spanning; the game's own window and split-screen layout are in use.");

            Monitor.Log(report.ToString(), LogLevel.Info);
        }
    }
}
