using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AutoFishCoop
{
    /// <summary>Reading the player numbers config.json is keyed by.</summary>
    public static class PlayerNumbers
    {
        /// <summary>Turns a player number as written in config.json ("1" for player one) into the index
        /// Stardew knows that player by, counting from 0.</summary>
        /// <returns>The player index, or -1 if the key is not a player number.</returns>
        public static int ToPlayer(string key, int maxPlayers)
        {
            int number;
            if (key == null || !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return -1;

            return number >= 1 && number <= maxPlayers ? number - 1 : -1;
        }

        /// <summary>Re-keys a part of config.json that is written by player number so it is keyed by player
        /// index instead, leaving out anything that is not a player number or has nothing in it.</summary>
        /// <param name="byNumber">The config.json section, keyed by player number.</param>
        /// <param name="maxPlayers">The highest player number there can be.</param>
        /// <param name="keep">Whether an entry says anything worth keeping.</param>
        public static Dictionary<int, T> ByPlayer<T>(IDictionary<string, T> byNumber, int maxPlayers,
            Func<T, bool> keep) where T : class
        {
            Dictionary<int, T> byIndex = new Dictionary<int, T>();
            if (byNumber == null)
                return byIndex;

            foreach (KeyValuePair<string, T> pair in byNumber)
            {
                int player = ToPlayer(pair.Key, maxPlayers);
                if (player >= 0 && pair.Value != null && (keep == null || keep(pair.Value)))
                    byIndex[player] = pair.Value;
            }

            return byIndex;
        }
    }

    /// <summary>The Auto Fishing settings that belong to one player.</summary>
    /// <remarks>Auto Fishing keeps one of these for the whole game, which is why its keys and its stamina
    /// cut-off apply to everyone at once. This mod keeps one per player and lends Auto Fishing whichever one
    /// belongs to the player it is working for.</remarks>
    public sealed class FishingSettings
    {
        /// <summary>Cast the rod again as soon as the player is free to.</summary>
        public bool AutoCast;

        /// <summary>Land the fish without playing the fishing minigame.</summary>
        public bool AutoFish;

        /// <summary>Hook the fish the moment it nibbles.</summary>
        public bool AutoHit;

        /// <summary>Allow the double catch that treasure bait can give.</summary>
        public bool CaughtDoubleFish;

        /// <summary>How far the rod is cast, from 0 to 1.</summary>
        public float CastingPower;

        /// <summary>Auto casting stops when the player's stamina falls to this. 0 never stops.</summary>
        public float StaminaThreshold;

        public FishingSettings Copy()
        {
            return new FishingSettings
            {
                AutoCast = AutoCast,
                AutoFish = AutoFish,
                AutoHit = AutoHit,
                CaughtDoubleFish = CaughtDoubleFish,
                CastingPower = CastingPower,
                StaminaThreshold = StaminaThreshold
            };
        }

        public bool Matches(FishingSettings other)
        {
            return other != null
                && other.AutoCast == AutoCast
                && other.AutoFish == AutoFish
                && other.AutoHit == AutoHit
                && other.CaughtDoubleFish == CaughtDoubleFish
                && other.CastingPower == CastingPower
                && other.StaminaThreshold == StaminaThreshold;
        }

        /// <summary>The casting power to move to when a player asks for the next one: a quarter less each
        /// time, back to full after the lowest.</summary>
        public static float NextCastingPower(float power)
        {
            float[] steps = { 1f, 0.75f, 0.5f, 0.25f };
            foreach (float step in steps)
            {
                // A hair of slack, since the power is a float the player may have set to anything.
                if (step < power - 0.01f)
                    return step;
            }

            return 1f;
        }

        public override string ToString()
        {
            return "auto fish " + OnOff(AutoFish)
                + ", auto hit " + OnOff(AutoHit)
                + ", auto cast " + OnOff(AutoCast)
                + " at " + (int)Math.Round(CastingPower * 100f) + "% power"
                + ", stamina cut-off " + StaminaThreshold
                + (CaughtDoubleFish ? ", double catch on" : "");
        }

        private static string OnOff(bool value)
        {
            return value ? "on" : "off";
        }
    }

    /// <summary>What one player starts with instead of what Auto Fishing's own config.json says. Anything left
    /// out keeps Auto Fishing's value.</summary>
    public sealed class FishingOverride
    {
        public bool? AutoCast { get; set; }

        public bool? AutoFish { get; set; }

        public bool? AutoHit { get; set; }

        public bool? CaughtDoubleFish { get; set; }

        public float? CastingPower { get; set; }

        public float? StaminaThreshold { get; set; }

        /// <summary>Whether nothing at all is named.</summary>
        /// <remarks>A method rather than a property so that it does not end up written into config.json as a
        /// setting of its own.</remarks>
        public bool IsEmpty()
        {
            return !AutoCast.HasValue && !AutoFish.HasValue && !AutoHit.HasValue
                && !CaughtDoubleFish.HasValue && !CastingPower.HasValue && !StaminaThreshold.HasValue;
        }

        /// <summary>Writes the values that were given over the ones the player would otherwise start with.</summary>
        public void ApplyTo(FishingSettings settings)
        {
            if (settings == null)
                return;

            if (AutoCast.HasValue)
                settings.AutoCast = AutoCast.Value;
            if (AutoFish.HasValue)
                settings.AutoFish = AutoFish.Value;
            if (AutoHit.HasValue)
                settings.AutoHit = AutoHit.Value;
            if (CaughtDoubleFish.HasValue)
                settings.CaughtDoubleFish = CaughtDoubleFish.Value;
            if (CastingPower.HasValue)
                settings.CastingPower = CastingPower.Value;
            if (StaminaThreshold.HasValue)
                settings.StaminaThreshold = StaminaThreshold.Value;
        }

        /// <summary>Brings the values into the range Auto Fishing expects.</summary>
        /// <returns>What had to be changed, for the log, or null if nothing did.</returns>
        public string Clamp()
        {
            StringBuilder complaints = new StringBuilder();

            if (CastingPower.HasValue && (CastingPower.Value < 0f || CastingPower.Value > 1f))
            {
                complaints.Append("CastingPower has to be between 0 and 1; ");
                CastingPower = Math.Max(0f, Math.Min(1f, CastingPower.Value));
            }

            if (StaminaThreshold.HasValue && StaminaThreshold.Value < 0f)
            {
                complaints.Append("StaminaThreshold cannot be negative; ");
                StaminaThreshold = 0f;
            }

            return complaints.Length > 0
                ? complaints.ToString(0, complaints.Length - 2)
                : null;
        }

        public override string ToString()
        {
            StringBuilder text = new StringBuilder();

            if (AutoFish.HasValue)
                text.Append("auto fish ").Append(AutoFish.Value ? "on" : "off").Append(", ");
            if (AutoHit.HasValue)
                text.Append("auto hit ").Append(AutoHit.Value ? "on" : "off").Append(", ");
            if (AutoCast.HasValue)
                text.Append("auto cast ").Append(AutoCast.Value ? "on" : "off").Append(", ");
            if (CastingPower.HasValue)
                text.Append("power ").Append(CastingPower.Value).Append(", ");
            if (StaminaThreshold.HasValue)
                text.Append("stamina cut-off ").Append(StaminaThreshold.Value).Append(", ");
            if (CaughtDoubleFish.HasValue)
                text.Append("double catch ").Append(CaughtDoubleFish.Value ? "on" : "off").Append(", ");

            return text.Length > 0 ? text.ToString(0, text.Length - 2) : "nothing";
        }
    }

    /// <summary>Auto Fishing's own settings object: the one every player would otherwise share.</summary>
    public interface ISharedSettings
    {
        /// <summary>What Auto Fishing is working from at this moment.</summary>
        FishingSettings Read();

        /// <summary>Hands Auto Fishing a different set of settings to work from.</summary>
        void Write(FishingSettings settings);
    }

    /// <summary>A set of Auto Fishing settings per player, lent to Auto Fishing one player at a time.</summary>
    /// <remarks>Stardew raises its events once per split-screen player, so Auto Fishing runs through this up to
    /// four times a tick with a different player each time. Before each of those runs the settings of the
    /// player whose turn it is go into Auto Fishing's own settings object, and whatever the player before them
    /// left in there is kept for when their turn comes round again. Auto Fishing changes those settings itself
    /// - the stamina cut-off switches auto cast off, the power keys move the casting power - so what is kept is
    /// what is in the object at the moment of the swap, not what was put there.
    /// <para>A player here is their farmer's multiplayer ID, which stays the same however often they leave and
    /// rejoin. It is not the split-screen screen ID, which is new every time a screen opens, and not the player
    /// number, which moves when someone ahead of them leaves. That number only decides which of the overrides
    /// a player starts from.</para></remarks>
    public sealed class PerPlayerSettings
    {
        private readonly ISharedSettings Shared;

        /// <summary>What Auto Fishing's config.json said, which is what a player who has changed nothing
        /// plays with.</summary>
        private readonly FishingSettings Defaults;

        private readonly Dictionary<long, FishingSettings> ByPlayer = new Dictionary<long, FishingSettings>();

        private Dictionary<int, FishingOverride> Overrides;

        /// <summary>The player whose settings Auto Fishing is holding, or null. Multiplayer IDs can be
        /// anything, negative numbers included, so no value of one can stand for nobody.</summary>
        private long? Loaded;

        /// <summary>Whether Auto Fishing has its own settings back (the mod is switched off).</summary>
        private bool Released;

        /// <param name="shared">Auto Fishing's settings object, still untouched: its values become every
        /// player's starting point.</param>
        /// <param name="overrides">Settings for particular players, by player number counting from 0.</param>
        public PerPlayerSettings(ISharedSettings shared, Dictionary<int, FishingOverride> overrides)
        {
            if (shared == null)
                throw new ArgumentNullException("shared");

            Shared = shared;
            Overrides = overrides ?? new Dictionary<int, FishingOverride>();
            Defaults = shared.Read();
            if (Defaults == null)
                throw new ArgumentException("Auto Fishing's settings could not be read.", "shared");
        }

        /// <summary>The settings Auto Fishing was configured with.</summary>
        public FishingSettings Configured
        {
            get { return Defaults.Copy(); }
        }

        /// <summary>The player whose settings Auto Fishing is working from, or null if none are loaded.</summary>
        public long? CurrentPlayer
        {
            get { return Loaded; }
        }

        /// <summary>Hands Auto Fishing this player's settings, keeping whatever the player before them left
        /// behind.</summary>
        /// <param name="player">The player's multiplayer ID.</param>
        /// <param name="number">Their player number counting from 0, for the overrides.</param>
        public void Enter(long player, int number)
        {
            Released = false;
            if (Loaded == player)
                return;

            Keep();
            Shared.Write(Of(player, number));
            Loaded = player;
        }

        /// <summary>Changes one player's settings and lets Auto Fishing see the change straight away.</summary>
        /// <returns>The player's settings after the change.</returns>
        public FishingSettings Change(long player, int number, Action<FishingSettings> change)
        {
            if (change == null)
                throw new ArgumentNullException("change");

            Enter(player, number);
            FishingSettings settings = Shared.Read();
            change(settings);
            Shared.Write(settings);
            return settings.Copy();
        }

        /// <summary>What a player is playing with right now.</summary>
        public FishingSettings For(long player, int number)
        {
            return Loaded == player
                ? Shared.Read()
                : Of(player, number).Copy();
        }

        /// <summary>Hands Auto Fishing its own configured settings back and stops lending it any, for when the
        /// mod is switched off. Safe to call every tick: it only acts the first time.</summary>
        public void Release()
        {
            if (Released)
                return;

            Keep();
            Shared.Write(Defaults.Copy());
            Released = true;
        }

        /// <summary>Starts everyone again from the configured settings and the overrides given, for when
        /// config.json is re-read.</summary>
        public void Reload(Dictionary<int, FishingOverride> overrides)
        {
            Overrides = overrides ?? new Dictionary<int, FishingOverride>();
            ByPlayer.Clear();
            Loaded = null;
            Released = false;
        }

        /// <summary>Keeps what the loaded player's settings have become, since Auto Fishing changes them as it
        /// goes.</summary>
        private void Keep()
        {
            if (Loaded.HasValue)
            {
                FishingSettings settings = Shared.Read();
                if (settings != null)
                    ByPlayer[Loaded.Value] = settings;
            }

            Loaded = null;
        }

        /// <summary>The stored settings of a player, started from the configured ones the first time.</summary>
        private FishingSettings Of(long player, int number)
        {
            FishingSettings settings;
            if (!ByPlayer.TryGetValue(player, out settings))
            {
                settings = Defaults.Copy();

                FishingOverride only;
                if (Overrides.TryGetValue(number, out only) && only != null)
                    only.ApplyTo(settings);

                ByPlayer[player] = settings;
            }

            return settings;
        }
    }
}
