using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Changes how many items a character can carry at once.
///
/// The cap is not a number anywhere - it is the length of
/// CharacterItemInteract.itemHolderQueue, an array of Transforms that carried items get
/// parented to. Raising the limit means creating extra holder transforms and handing back
/// a longer array, not editing a counter.
///
/// Every CharacterItemInteract in the scene is extended, not just our own. Picking an item
/// up goes through OnItemAddedToQueueServerRpc(itemIndex, queueIndex), so the machine
/// hosting the session validates the slot against ITS copy of that character. Extending
/// only the local player leaves the host's copy at four slots, the server rejects slot
/// five, and the extra capacity does nothing - which is exactly what happens when a
/// modded guest joins an unmodded host.
/// </summary>
public static class CarryTweaks
{
    /// <summary>
    /// Hard ceiling on holders. Not a taste limit - each one is a real GameObject
    /// parented to a character, so an unbounded value would spawn scene objects endlessly.
    /// </summary>
    public const int MaxSupported = 50;

    /// <summary>Per-character state, so each one can be restored to its own original.</summary>
    private sealed class Entry
    {
        public readonly List<Transform> Original = new();
        public readonly List<GameObject> Added = new();
        public Vector3 Step = Vector3.up * 0.12f;
    }

    private static readonly Dictionary<int, Entry> Tracked = new();

    private static float _scanTimer;
    private static int _defaultCount = -1;

    public static string Status = "chưa hoạt động";

    /// <summary>The game's own carry limit, or -1 before a character has been seen.</summary>
    public static int DefaultCount => _defaultCount;

    public static void Tick(CharacterItemInteract _)
    {
        // Rescan periodically: characters spawn as players join, and a newly joined guest
        // needs their holder array extended on this machine too.
        _scanTimer -= Time.unscaledDeltaTime;
        if (_scanTimer > 0f) return;
        _scanTimer = 2f;

        CharacterItemInteract[] all;
        try
        {
            var found = UnityEngine.Object.FindObjectsByType<CharacterItemInteract>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (found == null || found.Length == 0) return;

            all = new CharacterItemInteract[found.Length];
            for (int i = 0; i < found.Length; i++) all[i] = found[i];
        }
        catch (Exception e)
        {
            LogOnce($"Carry: character scan failed: {e.Message}");
            return;
        }

        int applied = 0;
        foreach (var interact in all)
        {
            if (interact == null) continue;
            if (ApplyTo(interact)) applied++;
        }

        int want = Wanted();
        Status = _defaultCount < 0
            ? "đang chờ nhân vật"
            : $"{want} mỗi nhân vật (mặc định {_defaultCount}), {applied} nhân vật";
    }

    private static int Wanted()
    {
        if (!Plugin.CarryEnabled.Value) return _defaultCount;
        return Mathf.Clamp(Plugin.CarryCount.Value, 1, MaxSupported);
    }

    private static bool ApplyTo(CharacterItemInteract interact)
    {
        try
        {
            var queue = interact.itemHolderQueue;
            if (queue == null) return false;

            int id = interact.GetInstanceID();
            if (!Tracked.TryGetValue(id, out var entry))
            {
                entry = new Entry();
                for (int i = 0; i < queue.Length; i++) entry.Original.Add(queue[i]);
                if (entry.Original.Count == 0) return false;

                // Continue the game's own spacing so a taller stack looks native.
                if (entry.Original.Count >= 2 &&
                    entry.Original[^1] != null && entry.Original[^2] != null)
                {
                    var delta = entry.Original[^1].localPosition - entry.Original[^2].localPosition;
                    if (delta.sqrMagnitude > 0.0001f) entry.Step = delta;
                }

                Tracked[id] = entry;

                if (_defaultCount < 0)
                {
                    _defaultCount = entry.Original.Count;
                    Plugin.Logger.LogInfo(
                        $"Carry: game default is {_defaultCount} item holders, step={entry.Step}.");
                }
            }

            int want = Mathf.Max(1, Wanted());
            if (queue.Length == want) return true;

            var array = new Il2CppReferenceArray<Transform>(want);
            for (int i = 0; i < want; i++)
            {
                if (i < entry.Original.Count) { array[i] = entry.Original[i]; continue; }

                var extra = GetOrCreateExtra(entry, i);
                if (extra == null) return false;
                array[i] = extra;
            }

            interact.itemHolderQueue = array;
            TrimExtras(entry, want);

            Plugin.Logger.LogInfo(
                $"Carry: '{interact.name}' item holders set to {want} (default {entry.Original.Count}).");
            return true;
        }
        catch (Exception e)
        {
            LogOnce($"Carry: apply failed: {e.Message}");
            return false;
        }
    }

    private static Transform GetOrCreateExtra(Entry entry, int i)
    {
        int extraIndex = i - entry.Original.Count;

        while (entry.Added.Count <= extraIndex)
        {
            var anchor = entry.Original[^1];
            if (anchor == null) return null;

            var go = new GameObject($"PlayerTweaks_ItemHolder_{entry.Added.Count}");
            go.transform.SetParent(anchor.parent, false);
            go.transform.localRotation = anchor.localRotation;
            go.transform.localScale = anchor.localScale;
            go.transform.localPosition = anchor.localPosition + entry.Step * (entry.Added.Count + 1);
            entry.Added.Add(go);
        }

        return entry.Added[extraIndex].transform;
    }

    private static void TrimExtras(Entry entry, int want)
    {
        int needed = Mathf.Max(0, want - entry.Original.Count);
        for (int i = entry.Added.Count - 1; i >= needed; i--)
        {
            var go = entry.Added[i];
            entry.Added.RemoveAt(i);
            if (go != null) UnityEngine.Object.Destroy(go);
        }
    }

    /// <summary>Puts every tracked character back to its own original holder array.</summary>
    public static void Restore(CharacterItemInteract _)
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsByType<CharacterItemInteract>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (all != null)
            {
                foreach (var interact in all)
                {
                    if (interact == null) continue;
                    if (!Tracked.TryGetValue(interact.GetInstanceID(), out var entry)) continue;

                    var array = new Il2CppReferenceArray<Transform>(entry.Original.Count);
                    for (int i = 0; i < entry.Original.Count; i++) array[i] = entry.Original[i];
                    interact.itemHolderQueue = array;
                }
            }

            foreach (var entry in Tracked.Values) TrimExtras(entry, entry.Original.Count);

            _scanTimer = 2f;   // do not immediately re-apply on the next tick
            Status = "đã khôi phục mặc định của game";
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Carry: restore failed: {e.Message}");
        }
    }

    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogWarning(message);
    }
}
