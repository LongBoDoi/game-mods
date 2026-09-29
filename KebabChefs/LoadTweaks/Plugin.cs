using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace LoadTweaks;

/// <summary>
/// Shortens the wait when opening a heavy save.
///
/// Two halves, and the order matters. The first measures a load and reports where its
/// time went, separating time spent working from time spent waiting for the next frame.
/// The second acts on the answer: while a load is in progress, stop rationing frames.
///
/// The measurement is not an extra; it is how you can tell whether the rest did anything,
/// and it is what any further work should be based on.
/// </summary>
[BepInPlugin(Guid, "Load Tweaks", "1.0.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "com.manhl.kebabchefs.loadtweaks";

    public static ManualLogSource Logger;

    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<string> OverlayKey;

    public static ConfigEntry<bool> QuietLogs;
    public static ConfigEntry<bool> DropInfoLogs;
    public static ConfigEntry<int> ItemBatchLimit;
    public static ConfigEntry<int> LoadTickRate;
    public static ConfigEntry<bool> SkipRealtimeWaits;
    public static ConfigEntry<bool> TraceWaits;

    public static ConfigEntry<float> LoadTimeScale;

    public static ConfigEntry<bool> RenderLessWhileLoading;
    public static ConfigEntry<int> RenderEveryNthFrame;

    public static ConfigEntry<bool> UncapFrameRate;
    public static ConfigEntry<int> LoadFrameRateCap;
    public static ConfigEntry<bool> FastAssetIntegration;

    public static ConfigEntry<bool> LogReport;

    public override void Load()
    {
        Logger = Log;

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Off leaves every setting the game chose exactly as it was.");
        OverlayKey = Config.Bind("General", "OverlayKey", "F8",
            "Key that shows/hides the load report overlay. Uses Unity InputSystem key " +
            "names (F8, F9, Backquote, Insert...).");

        QuietLogs = Config.Bind("Loading", "QuietLogsWhileLoading", true,
            "Stop the game writing a stack trace for every ordinary log line while a save " +
            "is loading.\n" +
            "Rebuilding a big save writes around thirteen thousand log lines - the player " +
            "log comes out at seven megabytes - and each one makes Unity walk the call " +
            "stack, which is by far the most expensive part of logging in this kind of " +
            "build. This is also why a higher frame rate never helped: that work has to " +
            "happen either way, so cheaper frames just spread the same amount of it over " +
            "more of them.\n" +
            "Only ordinary log lines lose their trace. Warnings, errors and exceptions keep " +
            "theirs, so a crash during a load is still just as diagnosable. Put back to " +
            "how it was as soon as the load finishes.");

        DropInfoLogs = Config.Bind("Loading", "DropInfoLogsWhileLoading", true,
            "Go one further and do not produce the game's informational log lines at all " +
            "while loading, rather than merely making them cheaper. No stack walk, no text " +
            "assembled, nothing written to disk. Needs the setting above to be on.\n" +
            "Warnings, errors and exceptions are untouched and still reach the log, so " +
            "anything that actually goes wrong during a load is as visible as it ever was. " +
            "What is lost is chatter like 'attach start: Croissant', thirteen thousand " +
            "lines of it.");

        ItemBatchLimit = Config.Bind("Loading", "ItemBatchLimit", 0,
            "How many objects the rebuild does before pausing to let the batch settle. " +
            "0 leaves the game's own value, which measured as 50.\n" +
            "The load is 93 bursts of up to 50 objects with a quarter-second gap after " +
            "each, and those gaps are 86% of the load. Fewer, bigger batches means fewer " +
            "gaps - if the gap is a fixed cost. If instead the gap grows with the batch, " +
            "there is nothing here and the report will show it: watch whether the gaps stay " +
            "the same length and get fewer, or simply get longer.\n" +
            "Raise it a step at a time (try 150). In co-op a bigger batch means many more " +
            "spawn messages at once, and that will have a limit somewhere.");

        SkipRealtimeWaits = Config.Bind("Loading", "SkipRealtimeWaitsWhileLoading", true,
            "End real-time waits immediately while a save is loading.\n" +
            "The load is 93 pauses of a quarter-second each, 86% of the total. That pause " +
            "has now survived a higher frame rate, a faster game clock, a bigger spawn " +
            "batch, a quicker networking tick and thirteen thousand fewer log lines. A " +
            "delay that ignores all of those is measured against the wall clock, and this " +
            "ends those early.\n" +
            "The coroutine still yields and still resumes on the following frame, so every " +
            "step happens in the same order with a frame in between - components still " +
            "start, objects still register. What changes is only how long that frame is " +
            "waited for. Only applies while loading; the game's own waits during play are " +
            "untouched.");

        TraceWaits = Config.Bind("Diagnostics", "TraceWaits", false,
            "Count each kind of waiting the engine does during a load, so the report can " +
            "say what the pause actually was instead of inferring it.\n" +
            "OFF BY DEFAULT now that it has answered its question. It works by attaching to " +
            "UnityEngine.WaitUntil, which every coroutine in the game polls once a frame " +
            "forever - a cost paid during play, not just during a load, for a measurement " +
            "there is no longer any reason to take. Turn it on only to investigate a new " +
            "pause.");

        LoadTickRate = Config.Bind("Loading", "LoadTickRateWhileLoading", 0,
            "Run the networking clock faster while a save is loading. 0 leaves the game's " +
            "value, which is 30 a second. Try 120.\n" +
            "The gap after each batch is a fixed quarter of a second of real time, and it " +
            "has survived every other clock: frame rate, game time scale, and removing " +
            "thirteen thousand log lines. A quarter of a second is eight networking ticks, " +
            "and that is the one clock left that fits.\n" +
            "Put back the moment the load ends - the tick rate is part of how players agree " +
            "on time, so it has no business staying different once you are playing.");

        LoadTimeScale = Config.Bind("Loading", "LoadTimeScale", 1f,
            "Run the game clock faster while a save is loading. 1 leaves it alone; try 3.\n" +
            "Measured on a heavy save: objects are rebuilt at a fixed ~150 a second no " +
            "matter what the frame rate is. Frames went from 96 to 158 a second and the " +
            "load time did not move at all, so something is handing objects out on a timer " +
            "rather than per frame. A coroutine that waits a set number of seconds measures " +
            "them in game time, and this is the only handle on that from outside.\n" +
            "It is not free. Everything else counting seconds counts faster too, including " +
            "the in-game clock, so a load may advance the day a little further than it " +
            "should. Physics takes bigger steps - the step is stretched by the same factor " +
            "so the work per real second stays put, but bigger steps are less accurate, and " +
            "items being placed on counters can settle differently. Keep it modest, and " +
            "look around before saving over a save you care about.\n" +
            "If this changes nothing, the pacing is not a timed wait; the pause histogram " +
            "in the load report will say what it is instead.");

        RenderLessWhileLoading = Config.Bind("Loading", "RenderLessWhileLoading", false,
            "Draw the scene on only every few frames while a save is loading.\n" +
            "OFF BY DEFAULT, and be careful with it. Drawing one frame in four is what a " +
            "loading screen can afford and normal play cannot: if anything ever makes this " +
            "mod believe a load is running when it is not, the game turns sluggish and stops " +
            "responding properly to input. That has happened once already, from the tablet.\n" +
            "It also did nothing when the load was paced by a wall clock. It may be worth " +
            "something now that the waiting is gone and the load is bound by frames again - " +
            "but check the report, and turn it straight back off if anything feels heavy.");
        RenderEveryNthFrame = Config.Bind("Loading", "RenderEveryNthFrame", 4,
            "How often to actually draw while loading. 4 means one frame in four. 1 turns " +
            "the whole thing off. Higher saves more up to the point where drawing is no " +
            "longer what a frame is spending its time on - the load report will show you " +
            "where that is.");

        UncapFrameRate = Config.Bind("Loading", "UncapFrameRateWhileLoading", false,
            "Turn off vertical sync and the frame rate cap for as long as a save is " +
            "loading, then put them back.\n" +
            "OFF BY DEFAULT, and on this machine it would do nothing anyway: the log from " +
            "the first run showed vertical sync already off and no frame cap set, so there " +
            "were never any frames being held back to release. Only worth turning on if " +
            "you have since capped your frame rate or switched vertical sync on.");
        LoadFrameRateCap = Config.Bind("Loading", "LoadFrameRateCap", 0,
            "Highest frame rate to allow while loading. 0 means no limit. Only used when " +
            "the setting above is on.");
        FastAssetIntegration = Config.Bind("Loading", "FastAssetIntegration", false,
            "Let Unity spend more of each frame finishing off assets it has loaded in the " +
            "background, instead of the small slice it reserves by default.\n" +
            "OFF BY DEFAULT: this was the one setting that was actually in effect when the " +
            "game crashed during a scene change, so until that is ruled out it does not " +
            "get to run unasked.");

        LogReport = Config.Bind("Diagnostics", "LogReportAfterEachLoad", true,
            "Write a breakdown of the load to the BepInEx log every time one finishes. " +
            "This is what tells you whether a load was slow because it was busy or because " +
            "it was waiting.");

        LoadProbe.Install(Log);
        WaitCutter.Install(Log);

        ClassInjector.RegisterTypeInIl2Cpp<LoadRunner>();

        var host = new GameObject("LoadTweaks_Runner");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<LoadRunner>();

        Log.LogInfo($"Load Tweaks loaded. Press {OverlayKey.Value} after a load for the breakdown.");
    }
}
