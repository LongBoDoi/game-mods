using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.Tools;

namespace AutoFishCoop
{
    /// <summary>The parts of Tibbles' Auto Fishing 2.0 this mod reaches into.</summary>
    /// <remarks>They are all looked up by name once at startup. If a new version of Auto Fishing has moved
    /// something, the lookup fails and this mod switches itself off with a line in the log, rather than
    /// half-patching a mod it no longer understands.</remarks>
    internal static class AutoFishingMod
    {
        /// <summary>Auto Fishing's mod ID, which is also what this mod's manifest depends on.</summary>
        public const string UniqueId = "Tibbles.AutoFishing2";

        private const string AssemblyName = "AutoFishing2";

        /// <summary>AutoFish.OnUpdateTicked: Auto Fishing's work for one player, once per tick per screen.</summary>
        public static MethodInfo OnUpdateTicked { get; private set; }

        /// <summary>ModEntry.OnButtonPressed: Auto Fishing's own keys.</summary>
        public static MethodInfo OnButtonPressed { get; private set; }

        /// <summary>FishingManager.CloseCaughtFishDialogue: dismisses the "you caught a fish" pose.</summary>
        public static MethodInfo CloseCaughtFishDialogue { get; private set; }

        /// <summary>AutoFish.config: the settings object every player shares.</summary>
        public static FieldInfo AutoFishConfig { get; private set; }

        /// <summary>ModEntry._config: the same settings object.</summary>
        public static FieldInfo ModEntryConfig { get; private set; }

        /// <summary>FishingManager._currentMouseState: a handle to the mouse state of whichever screen was
        /// current when Auto Fishing started.</summary>
        public static FieldInfo CachedMouseState { get; private set; }

        /// <summary>The settings on Auto Fishing's config object, by the name they have in its config.json.</summary>
        public static IDictionary<string, Setting> Settings { get; private set; }

        /// <summary>Looks everything up.</summary>
        /// <returns>Whether all of it was found; what was missing is logged.</returns>
        public static bool Find(IMonitor monitor)
        {
            Assembly assembly = null;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    assembly = loaded;
                    break;
                }
            }

            if (assembly == null)
            {
                monitor.Log("Auto Fishing 2.0 (" + UniqueId + ") is not loaded, so there is nothing to make per-player.", LogLevel.Warn);
                return false;
            }

            Type autoFish = assembly.GetType("AutoFishing2.AutoFish");
            Type fishingManager = assembly.GetType("AutoFishing2.FishingManager");
            Type modEntry = assembly.GetType("AutoFishing2.ModEntry");
            Type config = assembly.GetType("AutoFishing2.ModConfig");

            List<string> missing = new List<string>();
            if (autoFish == null)
                missing.Add("the AutoFish class");
            if (fishingManager == null)
                missing.Add("the FishingManager class");
            if (modEntry == null)
                missing.Add("the ModEntry class");
            if (config == null)
                missing.Add("the ModConfig class");

            if (missing.Count == 0)
            {
                OnUpdateTicked = AccessTools.DeclaredMethod(autoFish, "OnUpdateTicked", new Type[] { typeof(object), typeof(UpdateTickedEventArgs) });
                OnButtonPressed = AccessTools.DeclaredMethod(modEntry, "OnButtonPressed", new Type[] { typeof(object), typeof(ButtonPressedEventArgs) });
                CloseCaughtFishDialogue = AccessTools.DeclaredMethod(fishingManager, "CloseCaughtFishDialogue", new Type[] { typeof(FishingRod) });
                AutoFishConfig = FieldOfType(autoFish, "config", config);
                ModEntryConfig = FieldOfType(modEntry, "_config", config);
                CachedMouseState = FieldOfType(fishingManager, "_currentMouseState", typeof(IReflectedField<MouseState>));

                if (OnUpdateTicked == null)
                    missing.Add("AutoFish.OnUpdateTicked");
                if (OnButtonPressed == null)
                    missing.Add("ModEntry.OnButtonPressed");
                if (CloseCaughtFishDialogue == null)
                    missing.Add("FishingManager.CloseCaughtFishDialogue");
                if (AutoFishConfig == null)
                    missing.Add("AutoFish.config");
                if (ModEntryConfig == null)
                    missing.Add("ModEntry._config");
                if (CachedMouseState == null)
                    missing.Add("FishingManager._currentMouseState");

                Settings = FindSettings(config, missing);
            }

            if (missing.Count > 0)
            {
                monitor.Log("This mod is off: Auto Fishing 2.0 is not the version it knows how to work with. It could not find "
                    + string.Join(", ", missing.ToArray()) + ".", LogLevel.Error);
                monitor.Log("Auto Fishing's own settings still apply to every player at once.", LogLevel.Error);
                return false;
            }

            return true;
        }

        /// <summary>The settings this mod swaps, which is every setting Auto Fishing reads while it runs.</summary>
        private static IDictionary<string, Setting> FindSettings(Type config, List<string> missing)
        {
            Dictionary<string, Setting> found = new Dictionary<string, Setting>();
            AddSetting(config, found, missing, "AutoCast", typeof(bool));
            AddSetting(config, found, missing, "AutoFish", typeof(bool));
            AddSetting(config, found, missing, "AutoHit", typeof(bool));
            AddSetting(config, found, missing, "CaughtDoubleFish", typeof(bool));
            AddSetting(config, found, missing, "CastingPower", typeof(float));
            AddSetting(config, found, missing, "StaminaThreshold", typeof(float));
            return found;
        }

        private static void AddSetting(Type config, IDictionary<string, Setting> found, List<string> missing,
            string name, Type type)
        {
            Setting setting = Setting.Find(config, name, type);
            if (setting == null)
                missing.Add("ModConfig." + name);
            else
                found[name] = setting;
        }

        /// <summary>A field of exactly the type expected, or null: a field that has changed type is as good as
        /// missing.</summary>
        private static FieldInfo FieldOfType(Type type, string name, Type fieldType)
        {
            FieldInfo field = AccessTools.DeclaredField(type, name);
            return field != null && field.FieldType == fieldType ? field : null;
        }

        /// <summary>One of Auto Fishing's settings. It declares some as fields and some as properties, so this
        /// takes either.</summary>
        internal sealed class Setting
        {
            private readonly FieldInfo Field;
            private readonly PropertyInfo Property;

            private Setting(FieldInfo field, PropertyInfo property)
            {
                Field = field;
                Property = property;
            }

            /// <summary>Finds a readable and writable setting of the given type, or returns null.</summary>
            public static Setting Find(Type type, string name, Type valueType)
            {
                FieldInfo field = AccessTools.DeclaredField(type, name);
                if (field != null)
                    return field.FieldType == valueType && !field.IsInitOnly ? new Setting(field, null) : null;

                PropertyInfo property = AccessTools.DeclaredProperty(type, name);
                if (property != null && property.PropertyType == valueType && property.CanRead && property.CanWrite)
                    return new Setting(null, property);

                return null;
            }

            public object Get(object target)
            {
                return Field != null ? Field.GetValue(target) : Property.GetValue(target, null);
            }

            public void Set(object target, object value)
            {
                if (Field != null)
                    Field.SetValue(target, value);
                else
                    Property.SetValue(target, value, null);
            }
        }
    }

    /// <summary>Auto Fishing's own settings object, read and written through the members found at startup.</summary>
    internal sealed class SharedConfig : ISharedSettings
    {
        private readonly object Config;
        private readonly IDictionary<string, AutoFishingMod.Setting> Settings;

        public SharedConfig(object config, IDictionary<string, AutoFishingMod.Setting> settings)
        {
            if (config == null)
                throw new ArgumentNullException("config");
            if (settings == null)
                throw new ArgumentNullException("settings");

            Config = config;
            Settings = settings;
        }

        public FishingSettings Read()
        {
            return new FishingSettings
            {
                AutoCast = (bool)Settings["AutoCast"].Get(Config),
                AutoFish = (bool)Settings["AutoFish"].Get(Config),
                AutoHit = (bool)Settings["AutoHit"].Get(Config),
                CaughtDoubleFish = (bool)Settings["CaughtDoubleFish"].Get(Config),
                CastingPower = (float)Settings["CastingPower"].Get(Config),
                StaminaThreshold = (float)Settings["StaminaThreshold"].Get(Config)
            };
        }

        public void Write(FishingSettings settings)
        {
            if (settings == null)
                return;

            Settings["AutoCast"].Set(Config, settings.AutoCast);
            Settings["AutoFish"].Set(Config, settings.AutoFish);
            Settings["AutoHit"].Set(Config, settings.AutoHit);
            Settings["CaughtDoubleFish"].Set(Config, settings.CaughtDoubleFish);
            Settings["CastingPower"].Set(Config, settings.CastingPower);
            Settings["StaminaThreshold"].Set(Config, settings.StaminaThreshold);
        }
    }
}
