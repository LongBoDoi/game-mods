using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace LoadTweaks;

/// <summary>
/// Measures where the time in a save load actually goes.
///
/// This is the part of the mod that has to exist first. A load that takes half a minute
/// can be slow for two completely different reasons, and they want opposite fixes:
///
///  * It is doing a lot of work. Then the wall-clock time and the CPU time are close, and
///    the only thing that helps is doing less work.
///  * It is spending frames waiting. This game loads a restaurant through coroutines that
///    hand out one step per frame, and through per-object "wait until this exists" loops
///    that poll once a frame. Hundreds of objects then cost hundreds of frames no matter
///    how fast the machine is. Wall-clock time is enormous, CPU time is tiny, and the fix
///    is to make frames come faster - not to optimise anything.
///
/// So every phase is recorded twice: how long it lasted, and how much of that it was
/// actually busy for. The gap between those two numbers is the answer.
///
/// Nothing in here changes behaviour. It reads a clock.
/// </summary>
public static class LoadProbe
{
    public sealed class Phase
    {
        public string Name;
        public long Frames;
        public long Ticks;
        public double First;
        public double Last;
        public bool Seen;

        /// <summary>
        /// Whether seeing this phase is enough to conclude that a load has begun.
        ///
        /// Most of these only ever run during a load, but not all: an item waiting for its
        /// id runs every time an item is spawned, which during a shift is constantly. If
        /// that were allowed to declare a load, cooking a meal would switch vertical sync
        /// off mid-game. Those phases can only extend a load that something else started.
        /// </summary>
        public bool CanStartLoad;
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static readonly Dictionary<MethodBase, Phase> ByMethod = new();
    private static readonly List<Phase> Phases = new();

    /// <summary>
    /// One second of the load: how many frames fitted into it, and how many objects were
    /// built during it.
    ///
    /// This is the measurement that decides what to do next. The load costs
    /// frames x cost-per-frame, and the number of frames is fixed by the game rebuilding
    /// roughly one object at a time. So the only question worth asking is what makes a
    /// frame expensive - and specifically whether it is a fixed cost, or one that grows as
    /// the restaurant fills up with the objects just rebuilt. Flat means something
    /// constant, like drawing the scene. Rising means the scene itself, and the fix would
    /// be somewhere else entirely.
    /// </summary>
    public sealed class Slice
    {
        public int Frames;
        public double Seconds;
        public long Objects;
    }

    private static readonly List<Slice> Timeline = new();
    private const int MaxSlices = 180;

    private static Slice _slice;
    private static double _sliceStart;
    private static long _objectsAtSliceStart;

    private static long TotalObjects => RestaurantObjectsCreated + ItemsCreated;

    // ------------------------------------------------------------- pacing
    //
    // Objects arrive at a fixed number per second of wall clock, no matter what the frame
    // rate is doing. That means something is handing them out on a timer, and the shape of
    // the pauses between them says what: a quarter of a second means a deliberate wait
    // written into the rebuild, a thirtieth means it is keeping step with the networking
    // tick, and a scatter of tiny ones means no pause at all and the cost is in the work.

    private static double _lastObjectAt;
    private static long _currentBatch;

    public static long Batches;
    public static long LargestBatch;
    public static double TotalGapSeconds;

    /// <summary>A pause longer than this is treated as the end of a burst.</summary>
    private const double BatchGap = 0.004;

    private static readonly double[] GapEdges = { 0.001, 0.003, 0.006, 0.012, 0.020, 0.040, 0.090, 0.200 };
    private static readonly string[] GapNames =
    {
        "under 1ms", "1 - 3ms", "3 - 6ms", "6 - 12ms", "12 - 20ms",
        "20 - 40ms", "40 - 90ms", "90 - 200ms", "over 200ms"
    };
    private static readonly int[] GapCounts = new int[9];
    private static readonly double[] GapTotals = new double[9];
    private static readonly long[] GapFrames = new long[9];

    private static int _lastObjectFrame;

    /// <summary>
    /// Every kind of thing built so far this load.
    ///
    /// A pause that always lands immediately before the first appearance of a kind is a
    /// prefab being fetched off disk, and the answer would be to fetch them in advance. A
    /// pause that lands anywhere is a wait, and the answer is a different one entirely.
    /// </summary>
    private static readonly HashSet<string> SeenKinds = new();

    public static long LongPauseCount;
    public static long LongPauseNewKind;
    public static long LongPauseFrames;

    private static readonly List<string> LongPauses = new();
    private const int MaxLongPauseLines = 45;

    /// <summary>A pause this long is one of the ones that actually make up the load.</summary>
    private const double LongPause = 0.2;

    private static void RecordObjectTiming(string kind)
    {
        var now = Clock.Elapsed.TotalSeconds;
        var frame = UnityEngine.Time.frameCount;
        var newKind = SeenKinds.Add(kind ?? "?");

        if (_lastObjectAt > 0)
        {
            var gap = now - _lastObjectAt;
            var frames = frame - _lastObjectFrame;

            var bucket = GapEdges.Length;
            for (var i = 0; i < GapEdges.Length; i++)
            {
                if (gap >= GapEdges[i]) continue;
                bucket = i;
                break;
            }

            GapCounts[bucket]++;
            GapTotals[bucket] += gap;
            GapFrames[bucket] += frames;

            if (gap > BatchGap)
            {
                Batches++;
                if (_currentBatch > LargestBatch) LargestBatch = _currentBatch;
                _currentBatch = 0;
                TotalGapSeconds += gap;
            }

            // The long pauses are almost the whole load, so each one is written down on its
            // own. An average would hide the only thing worth knowing: whether a pause was
            // one frame that took a third of a second, or a hundred quiet frames spent
            // waiting for something.
            if (gap >= LongPause)
            {
                LongPauseCount++;
                LongPauseFrames += frames;
                if (newKind) LongPauseNewKind++;

                if (LongPauses.Count < MaxLongPauseLines)
                    LongPauses.Add((gap * 1000).ToString("0").PadLeft(6) + "ms over "
                        + frames.ToString().PadLeft(4) + " frame(s)  ->  " + kind
                        + (newKind ? "   (first of its kind)" : ""));
            }
        }

        _lastObjectAt = now;
        _lastObjectFrame = frame;
        _currentBatch++;
        LastActivity = now;
    }

    public static long RestaurantObjectsCreated;
    public static long ItemsCreated;

    public static bool Loading { get; private set; }
    public static double LoadStart { get; private set; }
    public static double LastActivity { get; private set; }
    public static int FramesDuringLoad { get; private set; }

    public static double LastLoadSeconds { get; private set; }
    public static float LastLoadFps { get; private set; }
    public static string LastLoadSummary = "no load measured yet";

    public static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>
    /// Seconds of quiet after which a load is considered finished.
    ///
    /// Generous, because the remaining hooks are sparse: the save is read while still on
    /// the menu, and then nothing this mod watches happens again until the new scene is up
    /// and the restaurant starts being rebuilt. A short window would call the load finished
    /// during the scene change and then start a second one.
    /// </summary>
    private const double SettleSeconds = 6;

    // ---------------------------------------------------------------- install

    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        var harmony = new Harmony(Plugin.Guid + ".probe");

        var prefix = new HarmonyMethod(AccessTools.Method(typeof(LoadProbe), nameof(Enter)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(LoadProbe), nameof(Leave)));

        // Deliberately a short list of ordinary methods.
        //
        // An earlier version also hooked the step function of the load coroutines, which
        // gave a per-phase frame count and would have been genuinely useful. It also
        // crashed the game on the scene swap. Those hidden compiler-generated classes are
        // driven from Unity's own native coroutine runner, and standing in the middle of
        // that is not something to do speculatively for a measurement. The same goes for
        // LevelLoadManager, which is what runs at the moment of the crash.
        //
        // What is left touches nothing that runs during a scene change, and nothing that
        // is called per object per frame.
        //
        // Only the first two may declare that a load has begun, because only those two mean
        // a save is actually being read. That list used to include restoring the decor,
        // listing the save files and initialising the restaurant - all of which also happen
        // while simply playing, particularly from the tablet. Each was announcing a load,
        // which switched on loading-screen behaviour: one frame drawn in four, real-time
        // waits cut short, the log silenced. For six seconds at a time, on a tablet somebody
        // was trying to use. The rest are still measured; they just cannot start a load,
        // only extend one a genuine save read began.
        TimeMethods(log, harmony, prefix, postfix, typeof(KebabLogic), "LoadGameData", true);
        TimeMethods(log, harmony, prefix, postfix, typeof(KebabLogic), "ProcessGameData", true);
        TimeMethods(log, harmony, prefix, postfix, typeof(RestaurantManager), "Initialize", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(RestaurantManager), "RestoreRestaurantDesign", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(RestaurantManager), "LoadIngredientStacks", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(GameServer), "InitGameItems", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(GameServer), "OnRestaurantLoaded", false);

        // Widening the window. The measured span covered about thirty seconds, but the
        // wait as experienced is roughly twice that, so some of it is happening outside
        // what was being watched - before the save is read, or after the last object is
        // built. These are all ordinary methods, none of them on the scene-change path
        // that crashed.
        TimeMethods(log, harmony, prefix, postfix, typeof(KebabLogic), "LoadValidDataFiles", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(KebabLogic), "GetSaveFiles", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(GameServer), "Initialize", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(GameServer), "InitDependencies", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(GameServer), "GetAndRunMapInitializer", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(IngameNetworkManager), "OnEnteredToGame", false);
        TimeMethods(log, harmony, prefix, postfix, typeof(RestaurantManager), "InitDefaultRO", false);

        Count(log, harmony, typeof(RestaurantManager), "CreateRestaurantObjectInternal", nameof(CountRestaurantObject));
        Count(log, harmony, typeof(ItemManager), "CreateItemOnServer", nameof(CountItem));

        log.LogInfo($"LoadProbe: watching {Phases.Count} phases of the load.");
    }

    private static void TimeMethods(BepInEx.Logging.ManualLogSource log, Harmony harmony,
                                    HarmonyMethod prefix, HarmonyMethod postfix,
                                    Type owner, string methodName, bool canStartLoad)
    {
        var any = false;

        foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.Name != methodName || method.DeclaringType != owner) continue;

            if (Register(log, harmony, method, $"{owner.Name}.{methodName}", prefix, postfix, canStartLoad))
                any = true;
        }

        if (!any) log.LogWarning($"LoadProbe: could not resolve {owner.Name}.{methodName}.");
    }

    private static bool Register(BepInEx.Logging.ManualLogSource log, Harmony harmony,
                                 MethodBase method, string name,
                                 HarmonyMethod prefix, HarmonyMethod postfix, bool canStartLoad)
    {
        try
        {
            if (ByMethod.ContainsKey(method)) return true;

            var phase = new Phase { Name = name, CanStartLoad = canStartLoad };

            // Registered before patching: the postfix looks its phase up by method and must
            // never be the one to create it.
            ByMethod[method] = phase;
            Phases.Add(phase);

            harmony.Patch(method, prefix: prefix, postfix: postfix);
            return true;
        }
        catch (Exception e)
        {
            ByMethod.Remove(method);
            log.LogWarning($"LoadProbe: patching {name} failed: {e.Message}");
            return false;
        }
    }

    private static void Count(BepInEx.Logging.ManualLogSource log, Harmony harmony,
                              Type owner, string methodName, string handler)
    {
        var found = false;

        foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.Name != methodName || method.DeclaringType != owner) continue;

            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(LoadProbe), handler)));
                found = true;
            }
            catch (Exception e)
            {
                log.LogWarning($"LoadProbe: counting {owner.Name}.{methodName} failed: {e.Message}");
            }
        }

        if (!found) log.LogWarning($"LoadProbe: could not resolve {owner.Name}.{methodName} to count.");
    }

    // ------------------------------------------------------------------ hooks

    private static void Enter(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Leave(MethodBase __originalMethod, long __state)
    {
        var elapsed = Stopwatch.GetTimestamp() - __state;

        if (!ByMethod.TryGetValue(__originalMethod, out var phase)) return;

        var now = Clock.Elapsed.TotalSeconds;

        MarkActivity(now, phase.CanStartLoad);

        // A phase that cannot declare a load is of no interest outside one, and recording
        // it during an ordinary shift would fill the report with noise.
        if (!Loading) return;

        if (!phase.Seen)
        {
            phase.Seen = true;
            phase.First = now;
        }

        phase.Last = now;
        phase.Frames++;
        phase.Ticks += elapsed;
    }

    // Items are created constantly during an ordinary shift, so these only count while a
    // load is actually in progress - otherwise the totals would be meaningless. They also
    // keep the load alive: with the coroutine hooks gone, a steady stream of objects being
    // built is the main sign that the rebuild is still going.
    private static void CountRestaurantObject(int id)
    {
        if (!Loading) return;
        RestaurantObjectsCreated++;
        RecordObjectTiming("furniture #" + id);
    }

    private static void CountItem(string itemName)
    {
        if (!Loading) return;
        ItemsCreated++;
        RecordObjectTiming(itemName);
    }

    // ------------------------------------------------------------- load state

    private static void MarkActivity(double now, bool canStartLoad)
    {
        if (!Loading)
        {
            if (!canStartLoad) return;
            Begin(now);
        }

        LastActivity = now;
    }

    private static void Begin(double now)
    {
        Loading = true;
        LoadStart = now;
        LastActivity = now;
        FramesDuringLoad = 0;

        RestaurantObjectsCreated = 0;
        ItemsCreated = 0;

        Timeline.Clear();
        _slice = null;
        WaitCutter.Reset();

        Batches = 0;
        LargestBatch = 0;
        TotalGapSeconds = 0;
        _lastObjectAt = 0;
        _currentBatch = 0;
        Array.Clear(GapCounts, 0, GapCounts.Length);
        Array.Clear(GapTotals, 0, GapTotals.Length);
        Array.Clear(GapFrames, 0, GapFrames.Length);
        SeenKinds.Clear();
        LongPauses.Clear();
        LongPauseCount = 0;
        LongPauseNewKind = 0;
        LongPauseFrames = 0;
        _lastObjectFrame = 0;

        foreach (var phase in Phases)
        {
            phase.Seen = false;
            phase.Frames = 0;
            phase.Ticks = 0;
            phase.First = 0;
            phase.Last = 0;
        }

        LoadBoost.OnLoadStarted();
        Plugin.Logger.LogInfo("Load started.");
    }

    /// <summary>
    /// Called every frame. A load has no single end - the last thing to finish differs
    /// between saves - so it is treated as over once nothing load-shaped has happened for
    /// a moment.
    /// </summary>
    public static void Tick(float unscaledDelta)
    {
        if (!Loading) return;

        FramesDuringLoad++;

        var now = Clock.Elapsed.TotalSeconds;

        if (_slice == null)
        {
            _slice = new Slice();
            _sliceStart = now;
            _objectsAtSliceStart = TotalObjects;
        }

        _slice.Frames++;
        _slice.Seconds += unscaledDelta;

        if (now - _sliceStart >= 1.0)
        {
            _slice.Objects = TotalObjects - _objectsAtSliceStart;
            if (Timeline.Count < MaxSlices) Timeline.Add(_slice);
            _slice = null;
        }

        if (now - LastActivity < SettleSeconds) return;

        End(now);
    }

    private static void End(double now)
    {
        Loading = false;

        // A load is only declared over once nothing has happened for several seconds, and
        // those quiet seconds were being counted into the timeline as though they were part
        // of the load. They are not - trim them, or the last rows read like a stall.
        while (Timeline.Count > 0 && Timeline[Timeline.Count - 1].Objects == 0)
            Timeline.RemoveAt(Timeline.Count - 1);

        var span = LastActivity - LoadStart;
        LastLoadSeconds = span;
        LastLoadFps = span > 0.01 ? (float)(FramesDuringLoad / span) : 0f;

        LastLoadSummary =
            $"{span:0.0}s over {FramesDuringLoad} frames ({LastLoadFps:0} fps), " +
            $"{RestaurantObjectsCreated} objects, {ItemsCreated} items";

        LoadBoost.OnLoadFinished();

        if (Plugin.LogReport.Value) WriteReport();
    }

    // ---------------------------------------------------------------- report

    public static void WriteReport()
    {
        var log = Plugin.Logger;
        var span = LastActivity - LoadStart;

        log.LogInfo("---- Load report ----");
        log.LogInfo($"total {span:0.00}s, {FramesDuringLoad} frames, {LastLoadFps:0.0} fps average");
        log.LogInfo($"created {RestaurantObjectsCreated} restaurant objects and {ItemsCreated} items");
        log.LogInfo(LoadBoost.Describe());
        log.LogInfo(WaitCutter.Describe());
        log.LogInfo("");
        log.LogInfo("  phase                                            steps     lasted      busy");

        var ranked = new List<Phase>(Phases);
        ranked.Sort((a, b) => (b.Last - b.First).CompareTo(a.Last - a.First));

        foreach (var phase in ranked)
        {
            if (!phase.Seen) continue;

            var lasted = phase.Last - phase.First;
            var busyMs = phase.Ticks * 1000.0 / Stopwatch.Frequency;

            log.LogInfo("  "
                + phase.Name.PadRight(46)
                + phase.Frames.ToString().PadLeft(7)
                + (lasted.ToString("0.00") + "s").PadLeft(11)
                + (busyMs.ToString("0") + "ms").PadLeft(10));
        }

        WriteTimeline(log);
        WritePacing(log);
        WriteContext(log);

        log.LogInfo("---- end of load report ----");
    }

    /// <summary>
    /// Prints how the cost of a frame moved over the course of the load, second by second,
    /// against how many objects were being built in that same second.
    /// </summary>
    private static void WriteTimeline(BepInEx.Logging.ManualLogSource log)
    {
        if (Timeline.Count == 0) return;

        log.LogInfo("");
        log.LogInfo("  second   frames   frame ms   objects built");

        for (var i = 0; i < Timeline.Count; i++)
        {
            var slice = Timeline[i];
            var frameMs = slice.Frames > 0 ? slice.Seconds * 1000.0 / slice.Frames : 0;

            log.LogInfo("  "
                + (i + 1).ToString().PadLeft(6)
                + slice.Frames.ToString().PadLeft(9)
                + frameMs.ToString("0.0").PadLeft(11)
                + slice.Objects.ToString().PadLeft(16));
        }

        var first = Timeline[0];
        var last = Timeline[Timeline.Count - 1];

        var firstMs = first.Frames > 0 ? first.Seconds * 1000.0 / first.Frames : 0;
        var lastMs = last.Frames > 0 ? last.Seconds * 1000.0 / last.Frames : 0;

        log.LogInfo("");
        log.LogInfo($"frame cost went from {firstMs:0.0}ms to {lastMs:0.0}ms as the restaurant filled up.");
        log.LogInfo("Roughly flat means a fixed cost per frame - drawing the scene, most likely -");
        log.LogInfo("and rendering less often while loading is the safe fix. Clearly rising means");
        log.LogInfo("the objects being rebuilt are themselves what makes each later frame slower.");
    }

    /// <summary>
    /// Prints the shape of the pauses between objects being built.
    ///
    /// The frame rate has already been ruled out, so objects are arriving on some kind of
    /// timer. Which timer decides what can be done about it, and the gap between one object
    /// and the next is the direct evidence.
    /// </summary>
    private static void WritePacing(BepInEx.Logging.ManualLogSource log)
    {
        var objects = TotalObjects;
        if (objects < 2) return;

        var span = LastActivity - LoadStart;

        log.LogInfo("");
        log.LogInfo("  pause before next object       count     total    share   frames each");

        for (var i = 0; i < GapCounts.Length; i++)
        {
            if (GapCounts[i] == 0) continue;

            var share = span > 0 ? GapTotals[i] / span * 100.0 : 0;
            var perGap = (double)GapFrames[i] / GapCounts[i];

            log.LogInfo("  "
                + GapNames[i].PadRight(28)
                + GapCounts[i].ToString().PadLeft(8)
                + (GapTotals[i].ToString("0.00") + "s").PadLeft(10)
                + (share.ToString("0") + "%").PadLeft(9)
                + perGap.ToString("0.0").PadLeft(14));
        }

        var perSecond = span > 0 ? objects / span : 0;
        var average = Batches > 0 ? (double)objects / Batches : objects;

        log.LogInfo("");
        log.LogInfo($"{objects} objects at {perSecond:0} a second, in {Batches} bursts of " +
                    $"{average:0.0} on average (largest {LargestBatch}).");
        log.LogInfo($"{TotalGapSeconds:0.0}s of the {span:0.0}s load was the pause between bursts.");
        if (LongPauseCount > 0)
        {
            var framesEach = (double)LongPauseFrames / LongPauseCount;

            log.LogInfo("");
            log.LogInfo($"{LongPauseCount} pauses of 200ms or more, averaging {framesEach:0.0} frames each, " +
                        $"{LongPauseNewKind} of them right before something built for the first time.");
            log.LogInfo("");

            foreach (var line in LongPauses) log.LogInfo("  " + line);

            if (LongPauseCount > LongPauses.Count)
                log.LogInfo($"  ... and {LongPauseCount - LongPauses.Count} more");
        }

        log.LogInfo("");
        log.LogInfo("Read the frames column first. About one frame per pause means the pause IS a");
        log.LogInfo("frame - real work, most likely fetching something off disk, and the fix is to");
        log.LogInfo("fetch it earlier. Many frames per pause means nothing was happening and the");
        log.LogInfo("rebuild was waiting on a timer, which is a different fix entirely.");
    }

    /// <summary>
    /// The settings that could plausibly be setting the pace, printed so they can be read
    /// against the pauses above rather than guessed at.
    /// </summary>
    private static void WriteContext(BepInEx.Logging.ManualLogSource log)
    {
        log.LogInfo("");

        try
        {
            var manager = Unity.Netcode.NetworkManager.Singleton;
            var tick = manager != null && manager.NetworkConfig != null
                ? manager.NetworkConfig.TickRate.ToString() + "/s"
                : "unknown";

            log.LogInfo($"networking tick rate {tick}   (a tick every " +
                        (manager != null && manager.NetworkConfig != null && manager.NetworkConfig.TickRate > 0
                            ? (1000.0 / manager.NetworkConfig.TickRate).ToString("0") + "ms)"
                            : "?)"));
        }
        catch (Exception e)
        {
            log.LogInfo($"networking tick rate unavailable ({e.Message})");
        }

        try
        {
            log.LogInfo($"time scale {UnityEngine.Time.timeScale:0.##}, " +
                        $"physics step {UnityEngine.Time.fixedDeltaTime * 1000:0.#}ms, " +
                        $"vsync {UnityEngine.QualitySettings.vSyncCount}, " +
                        $"frame cap {(UnityEngine.Application.targetFrameRate <= 0 ? "none" : UnityEngine.Application.targetFrameRate.ToString())}");
        }
        catch { }
    }

    public static List<Phase> Snapshot()
    {
        var ranked = new List<Phase>();
        foreach (var phase in Phases) if (phase.Seen) ranked.Add(phase);
        ranked.Sort((a, b) => (b.Last - b.First).CompareTo(a.Last - a.First));
        return ranked;
    }
}
