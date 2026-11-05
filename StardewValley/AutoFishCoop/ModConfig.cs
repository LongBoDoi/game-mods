using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace AutoFishCoop
{
    /// <summary>config.json, written the first time the mod runs.</summary>
    public sealed class ModConfig
    {
        /// <summary>The most players Stardew puts on one machine.</summary>
        public const int MaxPlayers = 4;

        /// <summary>The buttons the game walks the character with when they are on the D-pad.</summary>
        /// <remarks>Static, so it is not one of the settings written to config.json.</remarks>
        public static readonly SButton[] DpadButtons =
        {
            SButton.DPadUp,
            SButton.DPadDown,
            SButton.DPadLeft,
            SButton.DPadRight
        };

        /// <summary>False leaves Auto Fishing exactly as it is: one set of settings for everyone, and none of
        /// the buttons below.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Pressed to open the menu. Every player presses it on their own controller and gets their
        /// own menu, so nothing has to be shared or aimed at anybody.</summary>
        public KeybindList MenuButton { get; set; } = new KeybindList(SButton.LeftStick);

        /// <summary>Pressed to turn casting again by itself on and off, without opening the menu.</summary>
        public KeybindList ToggleAutoCastButton { get; set; } = new KeybindList(SButton.DPadUp);

        /// <summary>Pressed to turn auto fishing on and off, without opening the menu. Unbound: the D-pad's
        /// other directions are free for it once <see cref="DisableDpadMovement" /> has taken them off walking
        /// duty.</summary>
        public KeybindList ToggleAutoFishButton { get; set; } = new KeybindList();

        /// <summary>Stop the D-pad walking the character, so its buttons are free to be shortcuts. The left
        /// stick still walks and the D-pad still works in menus.</summary>
        public bool DisableDpadMovement { get; set; } = true;

        /// <summary>Only answer those buttons for a player holding a fishing rod.</summary>
        public bool RequireFishingRod { get; set; } = false;

        /// <summary>What particular players start with, keyed by player number ("1" to "4"). Anything left out
        /// is taken from Auto Fishing's own config.json.</summary>
        public Dictionary<string, FishingOverride> Players { get; set; } = new Dictionary<string, FishingOverride>();

        /// <summary>Complains about settings that make no sense and brings them back into range.</summary>
        public void Validate(IMonitor monitor)
        {
            if (Players == null)
                Players = new Dictionary<string, FishingOverride>();

            // A binding written as null in config.json would otherwise throw on every tick.
            MenuButton = MenuButton ?? new KeybindList();
            ToggleAutoCastButton = ToggleAutoCastButton ?? new KeybindList();
            ToggleAutoFishButton = ToggleAutoFishButton ?? new KeybindList();

            if (!MenuButton.IsBound)
                monitor.Log("config.json: MenuButton is not bound, so there is no way to open the menu.", LogLevel.Warn);

            List<string> drop = new List<string>();
            foreach (KeyValuePair<string, FishingOverride> pair in Players)
            {
                if (PlayerNumbers.ToPlayer(pair.Key, MaxPlayers) < 0)
                {
                    monitor.Log("config.json: Players has an entry for \"" + pair.Key + "\", which is not a "
                        + "player number from 1 to " + MaxPlayers + ". It is being ignored.", LogLevel.Warn);
                    drop.Add(pair.Key);
                }
                else if (pair.Value == null)
                {
                    drop.Add(pair.Key);
                }
                else
                {
                    string complaint = pair.Value.Clamp();
                    if (complaint != null)
                        monitor.Log("config.json: player " + pair.Key + ": " + complaint + ".", LogLevel.Warn);
                }
            }

            foreach (string key in drop)
                Players.Remove(key);
        }

        /// <summary>The per-player settings by player index, counting from 0 for player one, which is how
        /// Stardew numbers the split-screen players.</summary>
        public Dictionary<int, FishingOverride> ByPlayer()
        {
            return PlayerNumbers.ByPlayer(Players, MaxPlayers,
                delegate(FishingOverride only) { return !only.IsEmpty(); });
        }
    }
}
