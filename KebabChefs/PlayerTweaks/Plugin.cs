using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace PlayerTweaks;

[BepInPlugin(Guid, "Player Tweaks", "1.0.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "com.manhl.kebabchefs.playertweaks";

    public static ManualLogSource Logger;

    // Master switch. When off, the game's own values are left completely alone.
    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<string> ToggleKey;

    public static ConfigEntry<float> WalkSpeed;
    public static ConfigEntry<float> RunSpeed;
    public static ConfigEntry<float> CrouchSpeed;
    public static ConfigEntry<float> JumpSpeed;
    public static ConfigEntry<float> GravityMultiplier;
    public static ConfigEntry<float> Sensitivity;
    public static ConfigEntry<float> InteractDistance;

    public static ConfigEntry<bool> FreeCursorWhenOpen;
    public static ConfigEntry<bool> BlockInteractionWhenOpen;

    public static ConfigEntry<bool> CarryEnabled;
    public static ConfigEntry<int> CarryCount;

    // Custom teleport waypoints, serialised as name~x~y~z|name~x~y~z
    public static ConfigEntry<string> Waypoints;

    // Per-location teleport hotkeys, serialised as name~Key|name~Key
    public static ConfigEntry<string> HotkeyBindings;
    public static ConfigEntry<bool> HotkeysEnabled;

    // Extras
    public static ConfigEntry<bool> MultiPickEnabled;
    public static ConfigEntry<float> MultiPickRadius;
    public static ConfigEntry<bool> MultiPickOutline;
    public static ConfigEntry<bool> StoreHoldRepeat;
    public static ConfigEntry<bool> FishCutSkip;
    public static ConfigEntry<bool> SlicerAnyItem;
    public static ConfigEntry<bool> SlicerLoadArmful;
    public static ConfigEntry<bool> SpiceTweaksEnabled;
    public static ConfigEntry<bool> SpiceInstant;
    public static ConfigEntry<int> SpiceAmount;
    public static ConfigEntry<bool> BoxPutBack;
    public static ConfigEntry<bool> BoxKeepEmpty;
    public static ConfigEntry<bool> BoxHoldRepeat;
    public static ConfigEntry<string> BoxDestroyKey;
    public static ConfigEntry<string> BoxTakeOutKey;
    public static ConfigEntry<string> PlateAlignKey;
    public static ConfigEntry<bool> PlaceAllEnabled;
    public static ConfigEntry<string> PlaceAllModifier;
    public static ConfigEntry<bool> PlateAutoAlign;
    public static ConfigEntry<float> PlateGridSize;
    public static ConfigEntry<bool> PlateEdgeSnap;
    public static ConfigEntry<string> PlateSnapToggleKey;
    public static ConfigEntry<string> PlateStockExcluded;
    public static ConfigEntry<string> PlateStockToggleKey;
    public static ConfigEntry<bool> PlateStockMenuEnabled;
    public static ConfigEntry<bool> CounterTopUpEnabled;
    public static ConfigEntry<int> CounterStackLimit;

    public static ConfigEntry<bool> CatVisitsEnabled;
    public static ConfigEntry<int> CatVisitMode;
    public static ConfigEntry<int> CatVisitInterval;
    public static ConfigEntry<float> CatVisitHour;
    public static ConfigEntry<string> CatSummonKey;

    // Today's-menu overlay
    public static ConfigEntry<bool> DishHudEnabled;
    public static ConfigEntry<float> DishHudX;
    public static ConfigEntry<float> DishHudY;
    public static ConfigEntry<string> DishHudHidden;
    public static ConfigEntry<int> MenuSortMode;
    public static ConfigEntry<string> MenuPresetData;

    public override void Load()
    {
        Logger = Log;

        Enabled = Config.Bind("General", "Enabled", true,
            "Apply the tweaks below. Turn off to hand every value back to the game.");
        ToggleKey = Config.Bind("General", "ToggleKey", "F1",
            "Key that opens/closes the overlay. Uses Unity InputSystem key names (F1, F2, Backquote, Insert...).");
        FreeCursorWhenOpen = Config.Bind("General", "FreeCursorWhenOpen", true,
            "Unlock the mouse and stop the camera turning while the overlay is open.");
        BlockInteractionWhenOpen = Config.Bind("General", "BlockInteractionWhenOpen", true,
            "Stop clicks in the overlay from also reaching the game, so pressing a button " +
            "cannot pick something up or set an interaction going behind the panel.");

        // Defaults here are placeholders; the real game values are captured the first
        // time a player is found, and the UI seeds these entries from them.
        WalkSpeed = Config.Bind("Movement", "WalkSpeed", -1f, "Walk speed. -1 = leave at the game's value.");
        RunSpeed = Config.Bind("Movement", "RunSpeed", -1f, "Sprint speed. -1 = leave at the game's value.");
        CrouchSpeed = Config.Bind("Movement", "CrouchSpeed", -1f, "Crouch speed. -1 = leave at the game's value.");
        JumpSpeed = Config.Bind("Movement", "JumpSpeed", -1f, "Jump velocity. -1 = leave at the game's value.");
        GravityMultiplier = Config.Bind("Movement", "GravityMultiplier", -1f, "Gravity multiplier. -1 = leave at the game's value.");
        Sensitivity = Config.Bind("Camera", "SensitivityMultiplier", -1f, "Mouse sensitivity multiplier. -1 = leave at the game's value.");
        InteractDistance = Config.Bind("Interaction", "ItemInteractDistance", -1f, "How far you can reach items. -1 = leave at the game's value.");

        CarryEnabled = Config.Bind("Movement", "CarryLimitEnabled", false,
            "Override how many items can be carried in hand at once.");
        CarryCount = Config.Bind("Movement", "CarryLimit", 4,
            "Items carried at once when the override is on. Clamped to 1-20.");

        Waypoints = Config.Bind("Teleport", "Waypoints", "",
            "Saved custom waypoints, as name~x~y~z separated by |. Managed from the Teleport tab.");

        MultiPickEnabled = Config.Bind("Extras", "MultiPickEnabled", false,
            "Hold Shift and scroll while looking at an item to mark more of the same kind, then pick " +
            "them all up with one click. Limited by how much you can carry.");
        MultiPickRadius = Config.Bind("Extras", "MultiPickRadius", 1.5f,
            "How far from the item you are pointing at to look for more of the same kind, " +
            "in metres. Keep it small so it only takes the pile in front of you.");
        MultiPickOutline = Config.Bind("Extras", "MultiPickOutline", true,
            "Mark items with the game's own selection border, the same one it draws around whatever " +
            "you are looking at, instead of tinting them green. Turn this off if marked items stop " +
            "showing any highlight at all.");
        StoreHoldRepeat = Config.Bind("Extras", "StoreHoldRepeat", false,
            "Hold the mouse button at a store shelf to keep adding/removing items instead of clicking once each.");

        FishCutSkip = Config.Bind("Extras", "FishCutSkipMinigame", false,
            "Skip the knife minigame when cutting a fish. The fish is cut the instant the " +
            "minigame would have opened, scored as a run that touched no spikes.");

        SlicerAnyItem = Config.Bind("Extras", "SlicerAcceptsAnySliceable", false,
            "Let the Slicify machine take anything the game can cut, instead of only the " +
            "fruits and vegetables its rack was built for. Items that have no slicing of " +
            "their own are still refused - there would be nothing for the machine to do.");
        SlicerLoadArmful = Config.Bind("Extras", "SlicerLoadArmful", false,
            "Hold Shift and click a Slicify machine to hand it everything you are carrying. " +
            "The items go down at the machine and onto its rack one at a time as it frees " +
            "up. This path does not go through the machine's own list of accepted items, so " +
            "it takes fish and meat regardless of the option above.");

        SpiceTweaksEnabled = Config.Bind("Extras", "SpiceTweaksEnabled", false,
            "Enable the seasoning tweaks below.");
        SpiceInstant = Config.Bind("Extras", "SpiceInstant", true,
            "Skip the hold-to-season delay. Only speeds up spice events, not other held-item actions.");
        SpiceAmount = Config.Bind("Extras", "SpiceAmountPerUse", 5,
            "Grams added per shake. The game ships with 1.");

        BoxPutBack = Config.Bind("Extras", "BoxPutBack", false,
            "Right-click a shopping box to put the held item back into it, and hold the interact " +
            "key at one to keep taking items out.");
        // Split out of BoxPutBack, and defaulted from it rather than from a constant: this
        // behaviour used to ride along with that switch, so seeding it with whatever that
        // switch is set to means nobody's restaurant changes just because the option moved.
        // Once written to the file the two are independent, which is the point of the split.
        BoxKeepEmpty = Config.Bind("Extras", "BoxKeepEmpty", BoxPutBack.Value,
            "Keep an emptied shopping box from destroying itself, so there is something left " +
            "to put items back into. Independent of BoxPutBack - leave it off if you would " +
            "rather the game tidied empty boxes away.");
        // Deliberately separate from StoreHoldRepeat. Sharing that entry meant switching off
        // the shop-shelf repeat silently switched off the box repeats too, in a different
        // section of the panel, with nothing to say so.
        BoxHoldRepeat = Config.Bind("Extras", "BoxHoldRepeat", true,
            "Hold the button/key at a shopping box to keep putting items in or taking them out, " +
            "instead of one per press.");
        PlaceAllEnabled = Config.Bind("Extras", "PlaceAllHeldItems", false,
            "Hold the modifier key below and click a plate, baking tray or dessert tray to " +
            "put down everything you are carrying in one action, arranged the way the align " +
            "key does. Works wherever the game offers a plating; putting items down on a " +
            "counter is a different action and still goes one at a time.");
        PlaceAllModifier = Config.Bind("Extras", "PlaceAllModifier", "Shift",
            "Which key to hold while clicking: Shift, Ctrl, Alt, or None to place the whole " +
            "armful on every ordinary click.");
        PlateAutoAlign = Config.Bind("Extras", "PlateGridSnap", false,
            "Make the placement preview move in grid steps instead of smoothly, for every " +
            "placing mechanic - plates, counters, tables, shelves. You still aim it " +
            "wherever you like. Build mode is untouched; it has its own grid.");
        PlateGridSize = Config.Bind("Extras", "PlateGridSize", 0.03f,
            "Grid step in metres. Smaller is finer; larger snaps more coarsely.");
        PlateSnapToggleKey = Config.Bind("Extras", "PlateSnapToggleKey", "G",
            "Press this in game to switch between grid mode and auto-snap mode. " +
            "Uses Unity InputSystem key names.");
        PlateEdgeSnap = Config.Bind("Extras", "PlateAutoSnapMode", false,
            "Off (default) = grid mode: the preview rounds to grid steps. " +
            "On = auto-snap mode: the preview always sits flush against the nearest " +
            "already-placed item.");
        PlateAlignKey = Config.Bind("Extras", "PlateAlignKey", "V",
            "Press this while looking at a plate or tray to arrange the items on it. " +
            "Uses Unity InputSystem key names.");
        // Stored as rounded positions. The restaurant-object id this used to use names the
        // shop catalogue entry, so every cabinet of one model shared it and marking one
        // marked them all; a position is unique to the cabinet standing there.
        PlateStockExcluded = Config.Bind("Extras", "PlateStockExcluded", "",
            "Plate cabinets waiters should not draw clean plates from, by position " +
            "(atX_Y_Z, in tenths of a metre), separated by |. Managed from the Extras tab. " +
            "An excluded cabinet is still used if no other cabinet has the plate, so " +
            "service cannot stall. Moving a cabinet in build mode detaches its mark.");
        PlateStockMenuEnabled = Config.Bind("Extras", "PlateStockMenuEnabled", true,
            "Show the small panel over a plate cabinet when you look at one. Turning this off " +
            "keeps the exclusions working and only hides the panel - the toggle key still " +
            "applies to whichever cabinet you are looking at.");
        PlateStockToggleKey = Config.Bind("Extras", "PlateStockToggleKey", "K",
            "Press this while looking at a plate cabinet to allow or forbid waiters taking " +
            "clean plates from it. Uses Unity InputSystem key names.");
        // A counter holds one plate and counts down from its capacity; a serve fills it to
        // that number and a second serve onto an occupied counter does nothing at all
        // (measured: 4 portions in, 4 portions out, plate still in hand). Depth is the only
        // real lever, so stacking works by deepening the counter to what is left plus one
        // plate and refilling it. This is the safety rail on how far that can go.
        CounterStackLimit = Config.Bind("Extras", "CounterStackLimit", 0,
            "The most portions a counter may be stacked to. 0 = no limit. Each serve adds " +
            "the counter's own number (4, 5 or 6) on top of what is left. The card above " +
            "the counter draws a fixed row of dots, so if it stops looking right when a " +
            "counter is stacked high, set a limit here.");

        CounterTopUpEnabled = Config.Bind("Extras", "CounterTopUp", false,
            "Serve a counter that still has portions on it, and have them add up. Each plate " +
            "is worth the counter's own number (4, 5 or 6) on top of whatever is left, so " +
            "three portions plus a plate makes seven. Nothing on the counter is thrown away " +
            "and the counter's real capacity is never edited. Removing the dish at a counter " +
            "that has been stacked takes off one plate's worth the same way and hands you a " +
            "plate of it back, leaving the dish and the rest of the food on the counter, so " +
            "three plates on take three removals to get back off. Once it is down to a single " +
            "plate's worth, removing clears it as the game always does. A counter still serves " +
            "one dish at a time: topping up adds to what is on it, and the rest of today's " +
            "menu is refused until the counter is empty again. Each plate keeps its " +
            "own food and serves its own portions, newest first, the way a pile of plates " +
            "works - so a bad plate cannot be laundered by a good one served after it, a good " +
            "one is not dragged down by what came before, and removing twice gives you back " +
            "the second plate then the first. This is the whole feature - " +
            "with it off, counters behave exactly as the game ships them. In co-op both " +
            "players need the mod and this option on, or the one without it decides how " +
            "much food the counter really has.");

        // The cat's whole schedule is six fields on one component, and the daily roll leaves
        // its answer behind in one of them. So this does not simulate a cat; it lets the game
        // roll and then writes over the answer.
        CatVisitsEnabled = Config.Bind("Cat", "CatVisitsEnabled", false,
            "Decide when the thief cat visits instead of leaving it to chance. With this off " +
            "the game rolls for it as it always has, and nothing here is touched. The numbers " +
            "the game shipped with are read the first time the restaurant loads and put back " +
            "the moment this is switched off. In co-op only the host can put a cat in the " +
            "restaurant, so the host's schedule is the one that happens - a client's panel " +
            "sends it as a request and shows the host's answer, and needs the host to have " +
            "this on.");
        CatVisitMode = Config.Bind("Cat", "CatVisitMode", 0,
            "How the visit is decided: 0 = every day, 1 = never, 2 = every so many days " +
            "counted from the cat's last visit, 3 = only when you ask. Whatever this says, " +
            "you can still overrule today from the panel.");
        CatVisitInterval = Config.Bind("Cat", "CatVisitDays", 2,
            "Days between visits when the mode is 'every so many days'. 1 is every day. " +
            "Counted from the day the cat last actually came, which is the game's own record, " +
            "so it survives a reload.");
        CatVisitHour = Config.Bind("Cat", "CatVisitHour", -1f,
            "What time the cat turns up, as an hour of the day (13.5 = half past one). " +
            "-1 leaves the game's own arrival time alone. Summoning the cat by hand pulls " +
            "this forward to the current hour for that visit only, and puts it back after.");
        CatSummonKey = Config.Bind("Cat", "CatSummonKey", "",
            "Optional key that brings the cat right now, the same as the panel's button. " +
            "Empty means no key. Uses Unity InputSystem key names (F7, C, Numpad0...).");

        BoxTakeOutKey = Config.Bind("Extras", "BoxTakeOutKey", "F",
            "The key the game uses to take an item out of a box. Holding it keeps taking, " +
            "when the hold-repeat option is on. Change this if you have rebound interact.");
        BoxDestroyKey = Config.Bind("Extras", "BoxDestroyKey", "X",
            "Hold this key while hovering an item in a box to destroy that whole stack. " +
            "Uses Unity InputSystem key names (X, Delete, Backspace...).");

        DishHudEnabled = Config.Bind("Menu", "DishHudEnabled", false,
            "Show a panel in the corner of the screen listing today's dishes with how many " +
            "portions are plated up and ready to serve - the dots on the card above a " +
            "service counter. Green stocked, yellow running low, red nothing left.");
        // -1 rather than a real coordinate, because the default is the top right corner
        // and where that is depends on the resolution, which is not known this early.
        DishHudX = Config.Bind("Menu", "DishHudX", -1f,
            "Screen position of the panel, set by dragging it with the tweak overlay open. " +
            "-1 puts it back in the top right corner.");
        DishHudY = Config.Bind("Menu", "DishHudY", -1f,
            "Screen position of the panel. -1 puts it back in the top right corner.");
        // Stored as the dishes to leave out rather than the ones to show, so a dish added to
        // the menu later turns up in the panel by default instead of silently missing.
        DishHudHidden = Config.Bind("Menu", "DishHudHidden", "",
            "Dishes to leave out of the panel, by name, separated by |. Managed from the " +
            "Menu tab; a dish not listed here is shown.");

        MenuSortMode = Config.Bind("Menu", "DishPickerSort", 0,
            "How the dish picker orders what it offers: 0 = by name, 1 = most expensive " +
            "first, 2 = cheapest first.");

        // Recipe keys rather than display names: the name a dish shows depends on the
        // language, and a preset saved in one language would not load in another.
        MenuPresetData = Config.Bind("Menu", "MenuPresets", "",
            "Saved menus, as name~Recipe1,Recipe2 separated by |. Managed from the Menu " +
            "tab; a dish the current save has not unlocked is simply left out when the " +
            "preset is used.");

        HotkeysEnabled = Config.Bind("Teleport", "HotkeysEnabled", true,
            "Allow the per-location hotkeys below to fire while the overlay is closed.");
        HotkeyBindings = Config.Bind("Teleport", "HotkeyBindings", "",
            "Per-location teleport hotkeys, as name~Key separated by |. Bind them from the Teleport tab.");

        CursorGuard.Install(Log);
        DishStatus.Install(Log);
        SpiceTweaks.Install(Log);
        FishCutting.Install(Log);
        SlicerTweaks.Install(Log);
        BoxTweaks.Install(Log);
        PlateTweaks.Install(Log);
        PlaceAll.Install(Log);
        PlateStocks.Install(Log);
        PlateStocksNet.Install(Log);
        MultiPick.Install(Log);
        CounterTopUp.Install(Log);
        CatVisits.Install(Log);
        CatVisitsNet.Install(Log);

        ClassInjector.RegisterTypeInIl2Cpp<TweakUI>();

        var host = new GameObject("PlayerTweaks_UI");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<TweakUI>();

        Log.LogInfo($"Player Tweaks loaded. Press {ToggleKey.Value} in game to open the overlay.");
    }
}
