using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Tools;

namespace AutoFishCoop
{
    /// <summary>Gives every local co-op player their own copy of Auto Fishing's settings, and buttons on their
    /// own controller to change them.</summary>
    public sealed class ModEntry : Mod
    {
        /// <summary>The settings a button of its own can turn on and off.</summary>
        private enum FishingSetting
        {
            AutoCast,
            AutoFish
        }

        /// <summary>How long a toggle says what it did, in milliseconds.</summary>
        private const float MessageTime = 2000f;

        private ModConfig Config;

        /// <summary>The message a toggle last put on each player's own screen, so the next one replaces it
        /// instead of stacking up underneath it.</summary>
        private readonly PerScreen<HUDMessage> LastMessage = new PerScreen<HUDMessage>();

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();
            Config.Validate(Monitor);

            if (!AutoFishingMod.Find(Monitor))
                return;

            PerPlayerPatch.Enabled = Config.Enabled;
            PerPlayerPatch.Reload(Config.ByPlayer());
            if (!PerPlayerPatch.Apply(new Harmony(ModManifest.UniqueID), Monitor))
                return;

            Monitor.Log(Config.Enabled
                ? "Auto Fishing is patched: every local player now keeps their own settings."
                : "Turned off in config.json: Auto Fishing's settings apply to every player at once.",
                LogLevel.Trace);

            if (Config.Enabled && Config.DisableDpadMovement)
                Monitor.Log("The D-pad no longer walks: the left stick does that, and the D-pad is free for "
                    + "shortcuts. Set DisableDpadMovement to false in config.json to give it back.", LogLevel.Trace);

            // Suppressing has to happen before the game reads the pad, which is what UpdateTicking is for.
            helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
            helper.Events.Input.ButtonsChanged += OnButtonsChanged;

            helper.ConsoleCommands.Add("autofishcoop",
                "Shows what each local player's Auto Fishing settings are.\n\n"
                + "Usage: autofishcoop\n"
                + "Usage: autofishcoop reload   (re-reads config.json)\n"
                + "Usage: autofishcoop menu     (opens player one's menu, without pressing anything)",
                OnCommand);
        }

        /// <summary>Takes the D-pad off walking duty for this player, before the game reads it.</summary>
        private void OnUpdateTicking(object sender, UpdateTickingEventArgs e)
        {
            if (!Config.Enabled || !Config.DisableDpadMovement || !InTheWorld())
                return;

            // SMAPI keeps a suppressed button hidden from the game until it is really let go of, and hidden
            // from mods with it - which is why asking whether it is down does not keep finding it held, and
            // why one call per press is all it takes.
            foreach (SButton button in ModConfig.DpadButtons)
            {
                if (Helper.Input.IsDown(button))
                    Helper.Input.Suppress(button);
            }
        }

        /// <summary>Answers this player's own buttons: the menu, and the toggles that skip it.</summary>
        private void OnButtonsChanged(object sender, ButtonsChangedEventArgs e)
        {
            if (!Config.Enabled || PerPlayerPatch.Settings == null || !CanUseButtons())
                return;

            if (Config.MenuButton.JustPressed())
            {
                Helper.Input.SuppressActiveKeybinds(Config.MenuButton);
                Game1.activeClickableMenu = new FishingMenu(PerPlayerPatch.Settings, Game1.player.UniqueMultiplayerID,
                    Game1.game1.instanceIndex, Config.MenuButton);
                Game1.playSound("shwip");
                return;
            }

            if (Config.ToggleAutoCastButton.JustPressed())
            {
                Helper.Input.SuppressActiveKeybinds(Config.ToggleAutoCastButton);
                Toggle(FishingSetting.AutoCast);
            }
            else if (Config.ToggleAutoFishButton.JustPressed())
            {
                Helper.Input.SuppressActiveKeybinds(Config.ToggleAutoFishButton);
                Toggle(FishingSetting.AutoFish);
            }
        }

        /// <summary>Turns one of this player's settings on or off, and says so on their own screen.</summary>
        private void Toggle(FishingSetting setting)
        {
            bool on = false;
            PerPlayerPatch.Settings.Change(Game1.player.UniqueMultiplayerID, Game1.game1.instanceIndex, delegate(FishingSettings settings)
            {
                if (setting == FishingSetting.AutoCast)
                    on = settings.AutoCast = !settings.AutoCast;
                else
                    on = settings.AutoFish = !settings.AutoFish;
            });

            Game1.playSound("drumkit6");
            Announce((setting == FishingSetting.AutoCast ? "Cast again by itself" : "Auto fishing")
                + ": " + (on ? "on" : "off"));
        }

        /// <summary>Puts a short message on this player's own screen, replacing the last one.</summary>
        private void Announce(string text)
        {
            if (Game1.hudMessages == null)
                return;

            // Game1.hudMessages is one of the statics Stardew keeps per split-screen player, so this is the
            // screen of whoever pressed the button.
            if (LastMessage.Value != null)
                Game1.hudMessages.Remove(LastMessage.Value);

            HUDMessage message = new HUDMessage(text, MessageTime);
            message.noIcon = true;
            Game1.addHUDMessage(message);
            LastMessage.Value = message;
        }

        /// <summary>Whether this player is walking around their own screen, rather than in a menu, a cut scene
        /// or an arcade game - where the D-pad is the game's own to use.</summary>
        private static bool InTheWorld()
        {
            return Context.IsPlayerFree && Game1.currentMinigame == null && !Game1.IsChatting;
        }

        /// <summary>Whether this player's buttons should be answered at all.</summary>
        private bool CanUseButtons()
        {
            if (!InTheWorld())
                return false;

            return !Config.RequireFishingRod
                || (Game1.player != null && Game1.player.CurrentTool is FishingRod);
        }

        private void OnCommand(string name, string[] args)
        {
            if (args.Length > 0 && args[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                Config = Helper.ReadConfig<ModConfig>();
                Config.Validate(Monitor);
                PerPlayerPatch.Enabled = Config.Enabled;
                PerPlayerPatch.Reload(Config.ByPlayer());
                Monitor.Log("config.json re-read; every player starts again from Auto Fishing's own settings.",
                    LogLevel.Info);
                return;
            }

            // A way to see the menu without a controller, for looking it over.
            if (args.Length > 0 && args[0].Equals("menu", StringComparison.OrdinalIgnoreCase))
            {
                if (PerPlayerPatch.Settings == null || !Context.IsWorldReady)
                    Monitor.Log("Load a save first: there is nobody to show a menu to yet.", LogLevel.Info);
                else if (Game1.activeClickableMenu != null)
                    Monitor.Log("Close the menu that is already open on player one's screen first.", LogLevel.Info);
                else
                {
                    Game1.activeClickableMenu = new FishingMenu(PerPlayerPatch.Settings, Game1.player.UniqueMultiplayerID,
                        Game1.game1.instanceIndex, null);
                    Monitor.Log("Opened player " + (Game1.game1.instanceIndex + 1) + "'s menu.", LogLevel.Info);
                }
                return;
            }

            StringBuilder report = new StringBuilder();

            if (!Config.Enabled)
                report.AppendLine("Turned off in config.json: Auto Fishing's settings apply to every player at once.");

            PerPlayerSettings settings = PerPlayerPatch.Settings;
            if (settings == null)
            {
                report.Append("Auto Fishing has not run yet, so nobody has their own settings so far.");
                Monitor.Log(report.ToString(), LogLevel.Info);
                return;
            }

            report.AppendLine("Auto Fishing's own settings: " + settings.Configured);

            // A farmer is one of the statics kept per screen, so each one has to be asked for from their own.
            Dictionary<int, FishingOverride> overrides = Config.ByPlayer();
            GameRunner.instance.ExecuteForInstances(delegate(Game1 game)
            {
                if (!Context.IsWorldReady)
                    return;

                int number = game.instanceIndex;
                report.AppendLine("Player " + (number + 1) + " (" + Game1.player.Name + "): "
                    + settings.For(Game1.player.UniqueMultiplayerID, number));

                FishingOverride only;
                if (overrides.TryGetValue(number, out only))
                    report.AppendLine("    started from config.json with " + only);
            });

            report.AppendLine("Each of them keeps their own copy, and changes their own.");

            if (Config.Enabled)
            {
                report.AppendLine(Config.MenuButton.IsBound
                    ? "On a player's own controller: " + Config.MenuButton + " opens their menu"
                        + (Config.RequireFishingRod ? ", with a fishing rod in hand." : ".")
                    : "MenuButton is not bound in config.json, so there is no way to open the menu.");

                if (Config.ToggleAutoCastButton.IsBound)
                    report.AppendLine("    " + Config.ToggleAutoCastButton + ": cast again by itself, on and off.");
                if (Config.ToggleAutoFishButton.IsBound)
                    report.AppendLine("    " + Config.ToggleAutoFishButton + ": auto fishing, on and off.");

                report.Append(Config.DisableDpadMovement
                    ? "The D-pad does not walk; the left stick does."
                    : "The D-pad still walks, so a shortcut on it also takes a step.");
            }

            Monitor.Log(report.ToString(), LogLevel.Info);
        }
    }
}
