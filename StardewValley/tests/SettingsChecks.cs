using System;
using System.Collections.Generic;
using AutoFishCoop;

/// <summary>Offline checks for the per-player Auto Fishing settings: that a key, a stamina cut-off or a change
/// of casting power stays with the player it belongs to. No game and no Auto Fishing involved - the settings
/// object they share is stood in for.</summary>
internal static class SettingsChecks
{
    private static int Checks;

    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new Exception(message);
        Checks++;
    }

    /// <summary>Stands in for Auto Fishing's own settings object: one set of settings, copied in and out the
    /// way the real one is read and written field by field.</summary>
    private sealed class FakeAutoFishing : ISharedSettings
    {
        public FishingSettings Current = Configured();

        public int Reads;

        public int Writes;

        public FishingSettings Read()
        {
            Reads++;
            return Current.Copy();
        }

        public void Write(FishingSettings settings)
        {
            Writes++;
            Current = settings.Copy();
        }
    }

    /// <summary>What Auto Fishing's own config.json ships with.</summary>
    private static FishingSettings Configured()
    {
        return new FishingSettings
        {
            AutoCast = false,
            AutoFish = true,
            AutoHit = true,
            CaughtDoubleFish = false,
            CastingPower = 1f,
            StaminaThreshold = 25f
        };
    }

    private static PerPlayerSettings Store(FakeAutoFishing shared, Dictionary<int, FishingOverride> overrides = null)
    {
        return new PerPlayerSettings(shared, overrides);
    }

    private static void TestEveryoneStartsFromAutoFishingsOwnSettings()
    {
        FakeAutoFishing shared = new FakeAutoFishing();
        PerPlayerSettings store = Store(shared);

        Check(store.Configured.Matches(Configured()), "the configured settings are what Auto Fishing was holding");
        Check(store.CurrentPlayer == null, "nobody's settings are loaded to begin with");

        for (int player = 0; player < 4; player++)
            Check(store.For(player, player).Matches(Configured()), "player " + (player + 1) + " starts from the configured settings");

        store.Enter(0, 0);
        Check(store.CurrentPlayer == 0, "player one's settings are the ones loaded");
        Check(shared.Current.Matches(Configured()), "which are still the configured ones");
    }

    private static void TestAToggleOnlyChangesOnePlayer()
    {
        FakeAutoFishing shared = new FakeAutoFishing();
        PerPlayerSettings store = Store(shared);

        store.Change(0, 0, delegate(FishingSettings settings) { settings.AutoFish = false; });

        Check(!store.For(0, 0).AutoFish, "player one turned auto fishing off");
        Check(store.For(1, 1).AutoFish, "player two is still fishing automatically");
        Check(!shared.Current.AutoFish, "and Auto Fishing can see it straight away, since player one is loaded");

        store.Enter(1, 1);
        Check(shared.Current.AutoFish, "player two's turn: auto fishing is on again");
        Check(!store.For(0, 0).AutoFish, "player one's choice is kept while someone else plays");

        store.Enter(0, 0);
        Check(!shared.Current.AutoFish, "and comes back when it is their turn again");
    }

    private static void TestAutoFishingsOwnChangesAreKeptPerPlayer()
    {
        FakeAutoFishing shared = new FakeAutoFishing();
        Dictionary<int, FishingOverride> overrides = new Dictionary<int, FishingOverride>();
        overrides[0] = new FishingOverride { AutoCast = true };
        overrides[1] = new FishingOverride { AutoCast = true };
        PerPlayerSettings store = Store(shared, overrides);

        // Auto Fishing switches auto cast off itself when the player runs out of stamina.
        store.Enter(0, 0);
        shared.Current.AutoCast = false;

        store.Enter(1, 1);
        Check(shared.Current.AutoCast, "player two keeps casting after player one ran out of stamina");
        Check(!store.For(0, 0).AutoCast, "player one's cut-off was kept for them");

        // The same goes for the casting power keys, which Auto Fishing moves in its own settings.
        shared.Current.CastingPower = 0.5f;
        store.Enter(0, 0);
        Check(store.For(1, 1).CastingPower == 0.5f, "player two's casting power stayed where they put it");
        Check(store.For(0, 0).CastingPower == 1f, "player one's did not move");
    }

    private static void TestOverridesApplyToTheNamedPlayerOnly()
    {
        FakeAutoFishing shared = new FakeAutoFishing();
        Dictionary<int, FishingOverride> overrides = new Dictionary<int, FishingOverride>();
        overrides[1] = new FishingOverride { AutoFish = false, CastingPower = 0.25f };
        PerPlayerSettings store = Store(shared, overrides);

        Check(store.For(0, 0).Matches(Configured()), "player one is unaffected by player two's settings");
        Check(!store.For(1, 1).AutoFish, "player two starts with auto fishing off");
        Check(store.For(1, 1).CastingPower == 0.25f, "and with a quarter of the casting power");
        Check(store.For(1, 1).AutoHit, "settings the override leaves out keep Auto Fishing's value");

        // The override is a starting point, not a rule: what the player does afterwards wins.
        store.Change(1, 1, delegate(FishingSettings settings) { settings.AutoFish = true; });
        store.Enter(0, 0);
        store.Enter(1, 1);
        Check(shared.Current.AutoFish, "player two turned it back on and it stayed on");
    }

    private static void TestSettingsBelongToTheFarmer()
    {
        // Multiplayer IDs are any 64-bit number, negative ones included. This is a real one.
        const long Farmhand = -1557777352833061823L;
        const long Other = 8L;

        FakeAutoFishing shared = new FakeAutoFishing();
        Dictionary<int, FishingOverride> overrides = new Dictionary<int, FishingOverride>();
        overrides[1] = new FishingOverride { CastingPower = 0.5f };
        PerPlayerSettings store = Store(shared, overrides);

        store.Change(Farmhand, 1, delegate(FishingSettings settings) { settings.AutoCast = true; });
        store.Enter(0, 0);
        Check(!shared.Current.AutoCast, "player one does not get player two's auto cast");

        store.Enter(Farmhand, 1);
        Check(shared.Current.AutoCast, "a farmer with a negative ID gets theirs back on their turn");
        Check(store.CurrentPlayer == Farmhand, "and is the one Auto Fishing is working for");

        // A rejoin, or someone ahead of them leaving, gives them another number but not another farmer.
        store.Enter(0, 0);
        store.Enter(Farmhand, 2);
        Check(shared.Current.AutoCast, "a farmer who comes back as another player number keeps their settings");
        Check(shared.Current.CastingPower == 0.5f, "including what their first number's override gave them");

        // Somebody else in that place is somebody else.
        store.Enter(Other, 1);
        Check(!shared.Current.AutoCast, "a different farmer in their place does not inherit their settings");
        Check(shared.Current.CastingPower == 0.5f, "and starts from the override for that number");

        // No value of an ID can mean nobody: this one used to.
        store.Change(-1, 3, delegate(FishingSettings settings) { settings.AutoHit = false; });
        store.Enter(0, 0);
        store.Enter(-1, 3);
        Check(!shared.Current.AutoHit, "a farmer whose ID happens to be -1 keeps their settings too");
    }

    private static void TestTheSamePlayerTwiceCostsNothing()
    {
        FakeAutoFishing shared = new FakeAutoFishing();
        PerPlayerSettings store = Store(shared);

        store.Enter(0, 0);
        int writes = shared.Writes;
        store.Enter(0, 0);
        store.Enter(0, 0);
        Check(shared.Writes == writes, "the settings are only swapped when the player changes");

        store.Enter(1, 1);
        Check(shared.Writes == writes + 1, "and are swapped when it does");
    }

    private static void TestSwitchingOffHandsTheSettingsBack()
    {
        FakeAutoFishing shared = new FakeAutoFishing();
        PerPlayerSettings store = Store(shared);

        store.Change(0, 0, delegate(FishingSettings settings) { settings.AutoFish = false; });
        store.Release();
        Check(shared.Current.Matches(Configured()), "Auto Fishing has its own settings back");

        int writes = shared.Writes;
        store.Release();
        store.Release();
        Check(shared.Writes == writes, "which it is not handed again every tick");

        store.Enter(0, 0);
        Check(!shared.Current.AutoFish, "switching back on remembers what player one had chosen");
    }

    private static void TestReloadStartsEveryoneAgain()
    {
        FakeAutoFishing shared = new FakeAutoFishing();
        PerPlayerSettings store = Store(shared);

        store.Change(0, 0, delegate(FishingSettings settings) { settings.AutoFish = false; });

        Dictionary<int, FishingOverride> overrides = new Dictionary<int, FishingOverride>();
        overrides[0] = new FishingOverride { CastingPower = 0.5f };
        store.Reload(overrides);

        Check(store.For(0, 0).AutoFish, "the toggle player one had made is gone");
        Check(store.For(0, 0).CastingPower == 0.5f, "and the new override is in its place");
        Check(store.Configured.CastingPower == 1f, "Auto Fishing's own settings are still what they were");
    }

    private static void TestOverrideValues()
    {
        FishingOverride nothing = new FishingOverride();
        Check(nothing.IsEmpty(), "an override with nothing in it is empty");
        Check(nothing.Clamp() == null, "and has nothing to complain about");

        FishingSettings settings = Configured();
        nothing.ApplyTo(settings);
        Check(settings.Matches(Configured()), "and changes nothing");

        FishingOverride one = new FishingOverride { AutoHit = false };
        Check(!one.IsEmpty(), "an override with one setting is not empty");
        one.ApplyTo(settings);
        Check(!settings.AutoHit && settings.AutoFish, "and only writes the setting it names");

        FishingOverride silly = new FishingOverride { CastingPower = 2f, StaminaThreshold = -5f };
        string complaint = silly.Clamp();
        Check(complaint != null && complaint.Contains("CastingPower"), "a casting power above 1 is complained about");
        Check(silly.CastingPower.Value == 1f, "and brought back to 1");
        Check(silly.StaminaThreshold.Value == 0f, "a negative stamina cut-off becomes 0");

        FishingOverride low = new FishingOverride { CastingPower = -1f };
        Check(low.Clamp() != null && low.CastingPower.Value == 0f, "a casting power below 0 becomes 0");
        Check(new FishingOverride { CastingPower = 0.5f }.Clamp() == null, "a sensible one is left alone");
    }

    private static void TestCastingPowerCycles()
    {
        Check(FishingSettings.NextCastingPower(1f) == 0.75f, "full power steps down to three quarters");
        Check(FishingSettings.NextCastingPower(0.75f) == 0.5f, "then to a half");
        Check(FishingSettings.NextCastingPower(0.5f) == 0.25f, "then to a quarter");
        Check(FishingSettings.NextCastingPower(0.25f) == 1f, "and back round to full");
        Check(FishingSettings.NextCastingPower(0.6f) == 0.5f, "an odd power steps down to the next one below it");
        Check(FishingSettings.NextCastingPower(0.1f) == 1f, "and one below the lowest goes back to full");
        Check(FishingSettings.NextCastingPower(0f) == 1f, "as does no power at all");
    }

    private static void TestPlayerNumbersInConfig()
    {
        Check(PlayerNumbers.ToPlayer("1", 4) == 0, "player one is index 0");
        Check(PlayerNumbers.ToPlayer("4", 4) == 3, "player four is index 3");
        Check(PlayerNumbers.ToPlayer("0", 4) == -1, "there is no player zero");
        Check(PlayerNumbers.ToPlayer("5", 4) == -1, "there is no fifth player");
        Check(PlayerNumbers.ToPlayer("two", 4) == -1, "a word is not a player number");
        Check(PlayerNumbers.ToPlayer("", 4) == -1, "neither is nothing");
        Check(PlayerNumbers.ToPlayer(null, 4) == -1, "nor a missing key");
    }

    private static void TestConfigSectionsAreReKeyedByPlayer()
    {
        Dictionary<string, string> written = new Dictionary<string, string>();
        written["1"] = "player one";
        written["3"] = "player three";
        written["9"] = "nobody";
        written["x"] = "nobody";
        written["2"] = "";
        written["4"] = null;

        Dictionary<int, string> byPlayer = PlayerNumbers.ByPlayer(written, 4,
            delegate(string value) { return value.Length > 0; });

        Check(byPlayer.Count == 2, "only the entries for players who exist and say something are kept");
        Check(byPlayer[0] == "player one", "player one is index 0");
        Check(byPlayer[2] == "player three", "player three is index 2");
        Check(!byPlayer.ContainsKey(1), "an entry with nothing in it is left out");
        Check(!byPlayer.ContainsKey(3), "and so is a missing one");

        Check(PlayerNumbers.ByPlayer(written, 4, null).Count == 3,
            "without a test of its own, everything keyed by a player number is kept");
        Check(PlayerNumbers.ByPlayer<string>(null, 4, null).Count == 0, "a section that is not there is empty");
    }

    private static void TestNonsenseIsRefused()
    {
        bool threw = false;
        try
        {
            new PerPlayerSettings(null, null);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }
        Check(threw, "there is nothing to do without Auto Fishing's settings");
    }

    private static void TestSettingsRead()
    {
        FishingSettings settings = Configured();
        string text = settings.ToString();
        Check(text.Contains("auto fish on") && text.Contains("auto cast off"), "the settings read as English");
        Check(text.Contains("100% power"), "with the casting power as a percentage");
        Check(new FishingOverride().ToString() == "nothing", "an empty override says so");
        Check(new FishingOverride { AutoFish = false }.ToString().Contains("auto fish off"), "and a full one lists what it sets");
        Check(settings.Copy().Matches(settings), "a copy matches what it came from");
        Check(!settings.Matches(null), "and nothing matches nothing");
    }

    public static int Run()
    {
        try
        {
            TestEveryoneStartsFromAutoFishingsOwnSettings();
            TestAToggleOnlyChangesOnePlayer();
            TestAutoFishingsOwnChangesAreKeptPerPlayer();
            TestOverridesApplyToTheNamedPlayerOnly();
            TestSettingsBelongToTheFarmer();
            TestTheSamePlayerTwiceCostsNothing();
            TestSwitchingOffHandsTheSettingsBack();
            TestReloadStartsEveryoneAgain();
            TestOverrideValues();
            TestCastingPowerCycles();
            TestPlayerNumbersInConfig();
            TestConfigSectionsAreReKeyedByPlayer();
            TestNonsenseIsRefused();
            TestSettingsRead();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL after " + Checks + " settings checks: " + ex.Message);
            return 1;
        }

        Console.WriteLine("PASS: " + Checks + " settings checks");
        return 0;
    }
}
