using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoadTweaks;

/// <summary>
/// Makes frames come faster while a save is loading.
///
/// This game builds a restaurant back up through coroutines, and a coroutine that yields
/// gets exactly one turn per frame. Several hundred pieces of furniture and several
/// hundred loose items, each with its own "wait until this exists" loop that polls once a
/// frame, adds up to a load measured in frames rather than in work. Capped at 60 frames a
/// second by a monitor, a thousand frames of loading is sixteen seconds of staring at a
/// spinner on a machine that was never busy.
///
/// So the fix is not to make the loading do less. It is to stop rationing the frames.
///
/// The reason this is worth preferring over anything cleverer: it changes no order and
/// skips no step. Every coroutine still gets one turn per frame and still sees the same
/// sequence of events; there are simply more frames in a second. There is no way for it
/// to leave a save half-built, which is not something that could be said for reaching in
/// and running those coroutines faster than the game intended.
///
/// The boost lasts only while loading and is undone afterwards, with a hard time limit in
/// case the end of a load is somehow never noticed.
/// </summary>
public static class LoadBoost
{
    private static bool _boosted;

    private static int _vSyncWas;
    private static int _targetFpsWas;
    private static ThreadPriority _loadPriorityWas;
    private static int _renderIntervalWas = 1;
    private static bool _renderIntervalChanged;

    private static StackTraceLogType _traceWas;
    private static bool _traceChanged;

    private static LogType _filterWas;
    private static bool _filterChanged;

    private static int _batchLimitWas;
    private static bool _batchLimitChanged;

    private static uint _tickRateWas;
    private static bool _tickRateChanged;

    private static float _timeScaleWas = 1f;
    private static float _fixedDeltaWas;
    private static float _timeScaleApplied;
    private static bool _timeScaleChanged;

    private static double _boostedAt;

    /// <summary>Never hold the boost longer than this, whatever the load looks like.</summary>
    private const double MaxBoostSeconds = 90;

    public static bool Active => _boosted;

    // ------------------------------------------------------------------ apply

    public static void OnLoadStarted()
    {
        if (_boosted) return;
        if (!Plugin.Enabled.Value) return;

        _boostedAt = LoadProbe.Now;
        ApplyQuietLogs();
        ApplyBatchLimit();
        ApplyTickRate();
        ApplyRenderInterval();
        ApplyTimeScale();

        if (!Plugin.UncapFrameRate.Value) return;

        try
        {
            _vSyncWas = QualitySettings.vSyncCount;
            _targetFpsWas = Application.targetFrameRate;
            _loadPriorityWas = Application.backgroundLoadingPriority;

            var cap = Plugin.LoadFrameRateCap.Value;

            // A frame rate target is only honoured when the display is not the thing
            // setting the pace, so vertical sync has to come off for the target to mean
            // anything at all.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = cap > 0 ? cap : -1;

            if (Plugin.FastAssetIntegration.Value)
            {
                // How much of each frame Unity is willing to spend finishing off assets it
                // has loaded in the background. The default deliberately keeps this small
                // so that a game staying playable during a load does not stutter - which is
                // the opposite of what is wanted while staring at a loading screen.
                Application.backgroundLoadingPriority = ThreadPriority.High;
            }

            _boosted = true;
            _boostedAt = LoadProbe.Now;

            Plugin.Logger.LogInfo(
                $"Load boost on: vsync {_vSyncWas} -> 0, frame cap {Describe(_targetFpsWas)} -> " +
                $"{Describe(Application.targetFrameRate)}, asset integration " +
                $"{_loadPriorityWas} -> {Application.backgroundLoadingPriority}.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Load boost could not be applied: {e.Message}");
            _boosted = false;
        }
    }

    /// <summary>
    /// Draws the scene on only every Nth frame while loading, without slowing the frames
    /// themselves down.
    ///
    /// This is the lever the measurements pointed at. The load costs about 4500 objects at
    /// roughly one and a half per frame, so the number of frames is decided by the game and
    /// is not something to meddle with. What is left is what each of those frames costs -
    /// and ten milliseconds a frame while staring at a loading screen is almost all of it
    /// spent drawing a restaurant nobody is looking at yet.
    ///
    /// The update loop is untouched: every frame still runs, every coroutine still gets
    /// exactly one turn, in the same order, with the same values. Only the drawing is
    /// skipped, which is why this can be tried without risking a half-built save.
    /// </summary>
    private static void ApplyRenderInterval()
    {
        if (!Plugin.RenderLessWhileLoading.Value) return;

        try
        {
            var every = Mathf.Clamp(Plugin.RenderEveryNthFrame.Value, 1, 20);
            if (every <= 1) return;

            _renderIntervalWas = OnDemandRendering.renderFrameInterval;
            OnDemandRendering.renderFrameInterval = every;
            _renderIntervalChanged = true;

            Plugin.Logger.LogInfo($"Load boost: drawing every {every} frames while loading " +
                                  $"(was every {_renderIntervalWas}).");
        }
        catch (Exception e)
        {
            _renderIntervalChanged = false;
            Plugin.Logger.LogWarning($"Load boost could not reduce drawing: {e.Message}");
        }
    }

    private static void RestoreRenderInterval()
    {
        if (!_renderIntervalChanged) return;
        _renderIntervalChanged = false;

        try { OnDemandRendering.renderFrameInterval = _renderIntervalWas; }
        catch (Exception e) { Plugin.Logger.LogWarning($"Load boost could not restore drawing: {e.Message}"); }
    }

    /// <summary>
    /// Runs the clock faster while loading.
    ///
    /// Objects arrive at a fixed number per second of wall clock however fast frames are
    /// coming, so something is handing them out on a timer. A coroutine that waits a set
    /// number of seconds measures those seconds in game time, so game time is the only
    /// handle there is on it from outside.
    ///
    /// This is not free, which is why it is off unless asked for:
    ///
    ///  * Anything else counting seconds counts them faster too, the in-game clock
    ///    included, so a load may advance the day slightly further than it should.
    ///  * Physics runs more steps per real second. The step size is deliberately left
    ///    alone: stretching it to keep the cost per second flat would make each step
    ///    coarser, and items being placed on counters are exactly the kind of small object
    ///    that settles differently when a step gets bigger. Since the load is over in a
    ///    third of the time, the total number of steps is about the same either way - the
    ///    work is compressed, not multiplied - and the simulation stays identical. Frames
    ///    are not what this load is short of, so it can afford them.
    ///
    /// Keep it modest, and check that nothing has moved before saving over a save you care
    /// about. If it makes no difference at all, the pacing is not coming from a timed wait
    /// and the pause histogram in the report will say what it is coming from instead.
    /// </summary>
    private static void ApplyTimeScale()
    {
        var scale = Plugin.LoadTimeScale.Value;
        if (scale <= 1.001f) return;

        try
        {
            scale = Mathf.Clamp(scale, 1f, 8f);

            _timeScaleWas = Time.timeScale;
            _fixedDeltaWas = Time.fixedDeltaTime;

            // The game stops its own clock while sitting in a menu, and a load can begin
            // from there. Multiplying a stopped clock leaves it stopped, and putting that
            // zero back afterwards would leave the game frozen - so when the clock is not
            // running, this stays out of the way entirely.
            if (_timeScaleWas <= 0.001f)
            {
                Plugin.Logger.LogInfo("Load boost: the game clock is stopped right now, leaving it alone.");
                return;
            }

            Time.timeScale = _timeScaleWas * scale;
            _timeScaleApplied = Time.timeScale;
            _timeScaleChanged = true;

            Plugin.Logger.LogInfo(
                $"Load boost: game clock at {scale:0.##}x while loading " +
                $"(time scale {_timeScaleWas:0.##} -> {Time.timeScale:0.##}, " +
                $"physics step left at {_fixedDeltaWas * 1000:0.#}ms).");
        }
        catch (Exception e)
        {
            _timeScaleChanged = false;
            Plugin.Logger.LogWarning($"Load boost could not change the clock: {e.Message}");
        }
    }

    private static void RestoreTimeScale()
    {
        if (!_timeScaleChanged) return;
        _timeScaleChanged = false;

        try
        {
            // Only put back a value this mod is still responsible for. If the game has
            // changed the clock in the meantime - paused, opened a menu, finished loading
            // and started the day - then whatever it chose is more current than what was
            // saved here, and overwriting it would be how a mod leaves a game frozen.
            if (Mathf.Abs(Time.timeScale - _timeScaleApplied) < 0.0001f)
                Time.timeScale = _timeScaleWas;
            else
                Plugin.Logger.LogInfo("Load boost: the game changed its own clock during the load, " +
                                      "so it has been left as the game set it.");

            Time.fixedDeltaTime = _fixedDeltaWas;
        }
        catch (Exception e)
        {
            // Leaving the clock fast would be far worse than any load time, so say so loudly.
            Plugin.Logger.LogError($"Load boost could NOT restore the game clock: {e.Message}. " +
                                   "Please restart the game.");
        }
    }

    /// <summary>
    /// Stops the game writing a stack trace for every ordinary log line while loading.
    ///
    /// Rebuilding this save writes roughly thirteen thousand log lines - one per item
    /// attached, several times over - and the player log comes out at seven megabytes.
    /// Each of those lines makes Unity walk the call stack, which under IL2CPP is far and
    /// away the most expensive part of logging.
    ///
    /// This is why more frames never helped. Skipping the drawing made frames cheaper, but
    /// the logging is work that has to happen either way, so the same total amount of it
    /// simply spread across more frames and the load took exactly as long.
    ///
    /// Only plain log lines lose their trace. Warnings, errors and exceptions keep theirs,
    /// so if anything goes wrong during a load it is still just as diagnosable.
    /// </summary>
    private static void ApplyQuietLogs()
    {
        if (!Plugin.QuietLogs.Value) return;

        try
        {
            _traceWas = Application.GetStackTraceLogType(LogType.Log);
            if (_traceWas == StackTraceLogType.None) return;

            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            _traceChanged = true;

            Plugin.Logger.LogInfo($"Load boost: stack traces off for ordinary log lines while " +
                                  $"loading (was {_traceWas}); warnings and errors keep theirs.");

            if (!Plugin.DropInfoLogs.Value) return;

            // Better still than cheapening those lines is not producing them. Raising the
            // filter to Warning drops the informational ones outright - no stack walk, no
            // text assembled, nothing written to disk - while every warning, error and
            // exception still comes through, so a problem during a load is as visible as it
            // ever was.
            // The property was compiled away - this build never uses it - but the field it
            // sat on is still there, and the game's own logging reads that field. Going in
            // through the backing field is the only route left.
            var logger = Debug.unityLogger == null ? null : Debug.unityLogger.TryCast<Logger>();
            if (logger == null)
            {
                Plugin.Logger.LogInfo("Load boost: cannot reach the log filter in this build; " +
                                      "stack traces are still off.");
                return;
            }

            _filterWas = logger._filterLogType_k__BackingField;
            logger._filterLogType_k__BackingField = LogType.Warning;
            _filterChanged = true;

            Plugin.Logger.LogInfo("Load boost: the game's informational log lines are dropped " +
                                  "while loading; warnings and errors still get through.");
        }
        catch (Exception e)
        {
            _traceChanged = false;
            Plugin.Logger.LogWarning($"Load boost could not quieten the log: {e.Message}");
        }
    }

    private static void RestoreQuietLogs()
    {
        if (_filterChanged)
        {
            _filterChanged = false;
            try
            {
                var logger = Debug.unityLogger == null ? null : Debug.unityLogger.TryCast<Logger>();
                if (logger != null) logger._filterLogType_k__BackingField = _filterWas;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"Load boost could not restore log filtering: {e.Message}"); }
        }

        if (!_traceChanged) return;
        _traceChanged = false;

        try { Application.SetStackTraceLogType(LogType.Log, _traceWas); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Load boost could not restore the log: {e.Message}"); }
    }

    /// <summary>
    /// Widens the batch the rebuild works in.
    ///
    /// The rebuild spawns a fixed number of objects, then waits for that batch to be
    /// accounted for before starting the next. Measured: bursts of at most 50, then a
    /// quarter-second gap, ninety-three times over. Fewer batches means fewer gaps.
    ///
    /// Left alone by default. The pause exists for a reason this mod cannot see, and in
    /// co-op a much larger batch means many more spawn messages going out at once, which is
    /// exactly the sort of thing that has a limit somewhere. Raise it a step at a time and
    /// check the report: if the gaps stay the same length but there are fewer of them, it
    /// is working; if the gaps simply get longer, the wait is proportional and this is not
    /// the way through.
    /// </summary>
    private static void ApplyBatchLimit()
    {
        var wanted = Plugin.ItemBatchLimit.Value;
        if (wanted <= 0) return;

        try
        {
            var server = GameServer.Instance;
            if (server == null) return;

            _batchLimitWas = server._itemBatchLimit;
            if (_batchLimitWas == wanted) return;

            server._itemBatchLimit = wanted;
            _batchLimitChanged = true;

            Plugin.Logger.LogInfo($"Load boost: rebuild batch size {_batchLimitWas} -> {wanted}.");
        }
        catch (Exception e)
        {
            _batchLimitChanged = false;
            Plugin.Logger.LogWarning($"Load boost could not change the batch size: {e.Message}");
        }
    }

    /// <summary>
    /// The batch size is read each time round the rebuild loop, and the server object only
    /// appears partway through a load, so it is re-asserted rather than set once.
    /// </summary>
    private static void HoldBatchLimit()
    {
        var wanted = Plugin.ItemBatchLimit.Value;
        if (wanted <= 0) return;

        try
        {
            var server = GameServer.Instance;
            if (server == null) return;

            if (!_batchLimitChanged)
            {
                ApplyBatchLimit();
                return;
            }

            if (server._itemBatchLimit != wanted) server._itemBatchLimit = wanted;
        }
        catch { }
    }

    /// <summary>
    /// Runs the networking clock faster while loading.
    ///
    /// The gap after each batch is a fixed quarter of a second of real time - unmoved by
    /// frame rate, by the game clock, or by removing thirteen thousand log lines. A fixed
    /// wall-clock delay that nothing in the game's own timing affects points at the one
    /// clock left: the networking tick, which runs at 30 a second here. A quarter of a
    /// second is eight of those.
    ///
    /// If the rebuild is waiting for spawns to be acknowledged tick by tick, then ticking
    /// more often shortens the wait proportionally. If the gap does not move, it is not the
    /// tick either, and this should go back off.
    ///
    /// Restored the moment the load ends. It is deliberately not left running: the tick
    /// rate is part of how peers agree on time, and it has no business being different from
    /// everyone else's once you are actually playing.
    /// </summary>
    private static void ApplyTickRate()
    {
        var wanted = Plugin.LoadTickRate.Value;
        if (wanted <= 0) return;

        try
        {
            var ticker = Unity.Netcode.NetworkManager.Singleton?.NetworkTickSystem;
            if (ticker == null) return;

            _tickRateWas = ticker._TickRate_k__BackingField;
            if (_tickRateWas == (uint)wanted) return;

            ticker._TickRate_k__BackingField = (uint)wanted;
            _tickRateChanged = true;

            Plugin.Logger.LogInfo($"Load boost: networking tick rate {_tickRateWas} -> {wanted} while loading.");
        }
        catch (Exception e)
        {
            _tickRateChanged = false;
            Plugin.Logger.LogWarning($"Load boost could not change the tick rate: {e.Message}");
        }
    }

    private static void HoldTickRate()
    {
        var wanted = Plugin.LoadTickRate.Value;
        if (wanted <= 0) return;

        try
        {
            var ticker = Unity.Netcode.NetworkManager.Singleton?.NetworkTickSystem;
            if (ticker == null) return;

            if (!_tickRateChanged) { ApplyTickRate(); return; }
            if (ticker._TickRate_k__BackingField != (uint)wanted)
                ticker._TickRate_k__BackingField = (uint)wanted;
        }
        catch { }
    }

    private static void RestoreTickRate()
    {
        if (!_tickRateChanged) return;
        _tickRateChanged = false;

        try
        {
            var ticker = Unity.Netcode.NetworkManager.Singleton?.NetworkTickSystem;
            if (ticker != null) ticker._TickRate_k__BackingField = _tickRateWas;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Load boost could not restore the tick rate: {e.Message}"); }
    }

    private static void RestoreBatchLimit()
    {
        if (!_batchLimitChanged) return;
        _batchLimitChanged = false;

        try
        {
            var server = GameServer.Instance;
            if (server != null) server._itemBatchLimit = _batchLimitWas;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Load boost could not restore the batch size: {e.Message}"); }
    }

    public static void OnLoadFinished() => Restore("load finished");

    /// <summary>
    /// Safety net, called every frame. The boost is tied to a load being in progress, and
    /// anything that leaves it on outside of one - a load whose end went unnoticed, the mod
    /// being switched off mid-load - is caught here rather than left running all session.
    /// </summary>
    public static void Tick()
    {
        // Reduced drawing is applied on its own, so the safety net has to watch for either
        // change being left behind - not just the frame rate one.
        if (LoadProbe.Loading) { HoldBatchLimit(); HoldTickRate(); }

        if (!_boosted && !_renderIntervalChanged && !_timeScaleChanged
            && !_traceChanged && !_filterChanged && !_batchLimitChanged
            && !_tickRateChanged) return;

        if (!Plugin.Enabled.Value)
        {
            Restore("switched off");
            return;
        }

        if (!LoadProbe.Loading)
        {
            Restore("no longer loading");
            return;
        }

        if (LoadProbe.Now - _boostedAt > MaxBoostSeconds)
        {
            Restore("hit the time limit");
            Plugin.Logger.LogWarning(
                "Load boost was on for an unreasonably long time and has been turned off. " +
                "Please mention this - it means the end of a load was not detected.");
        }
    }

    public static void Restore(string why)
    {
        RestoreQuietLogs();
        RestoreTickRate();
        RestoreBatchLimit();
        RestoreTimeScale();
        RestoreRenderInterval();

        if (!_boosted) return;
        _boosted = false;

        try
        {
            QualitySettings.vSyncCount = _vSyncWas;
            Application.targetFrameRate = _targetFpsWas;
            Application.backgroundLoadingPriority = _loadPriorityWas;

            Plugin.Logger.LogInfo($"Load boost off ({why}): vsync back to {_vSyncWas}, " +
                                  $"frame cap back to {Describe(_targetFpsWas)}.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Load boost could not be undone: {e.Message}");
        }
    }

    // ----------------------------------------------------------------- status

    public static string Describe()
    {
        if (!Plugin.Enabled.Value) return "during load: nothing changed (mod is off)";

        var parts = "";

        if (Plugin.QuietLogs.Value)
            parts += Plugin.DropInfoLogs.Value ? "info logs dropped" : "log stack traces off";

        if (Plugin.ItemBatchLimit.Value > 0)
        {
            if (parts.Length > 0) parts += ", ";
            parts += $"batch size {Plugin.ItemBatchLimit.Value}";
        }

        if (Plugin.LoadTickRate.Value > 0)
        {
            if (parts.Length > 0) parts += ", ";
            parts += $"tick rate {Plugin.LoadTickRate.Value}";
        }

        if (Plugin.LoadTimeScale.Value > 1.001f)
        {
            if (parts.Length > 0) parts += ", ";
            parts += $"game clock at {Plugin.LoadTimeScale.Value:0.##}x";
        }

        if (Plugin.RenderLessWhileLoading.Value && Plugin.RenderEveryNthFrame.Value > 1)
        {
            if (parts.Length > 0) parts += ", ";
            parts += $"drawing every {Plugin.RenderEveryNthFrame.Value} frames";
        }

        if (Plugin.UncapFrameRate.Value)
        {
            if (parts.Length > 0) parts += ", ";
            var cap = Plugin.LoadFrameRateCap.Value;
            parts += "frame rate uncapped" + (cap > 0 ? $" up to {cap} fps" : "");
        }

        if (Plugin.FastAssetIntegration.Value)
        {
            if (parts.Length > 0) parts += ", ";
            parts += "asset integration raised";
        }

        return "during load: " + (parts.Length > 0 ? parts : "nothing changed");
    }

    private static string Describe(int frameRate) => frameRate <= 0 ? "unlimited" : frameRate.ToString();
}
