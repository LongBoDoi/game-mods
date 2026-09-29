using System;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LoadTweaks;

/// <summary>
/// Ticks the probe once a frame and draws the breakdown of the last load.
///
/// The per-frame work is deliberately trivial - a counter and two comparisons - because
/// during a load this runs on the very frames the mod is trying to make cheaper.
/// </summary>
public class LoadRunner : MonoBehaviour
{
    public LoadRunner(IntPtr ptr) : base(ptr) { }

    private bool _open;
    private string _notice = "";
    private float _noticeUntil;
    private string _badKeyName;

    private void Update()
    {
        try
        {
            LoadProbe.Tick(Time.unscaledDeltaTime);
            LoadBoost.Tick();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"LoadRunner: {e.Message}");
        }

        if (ToggleWasPressed()) _open = !_open;
    }

    [HideFromIl2Cpp]
    private bool ToggleWasPressed()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            var name = Plugin.OverlayKey.Value;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None)
            {
                // Only complain once per bad value, not every frame.
                if (_badKeyName != name)
                {
                    _badKeyName = name;
                    Plugin.Logger.LogWarning($"'{name}' is not a valid InputSystem key name.");
                }
                return false;
            }

            _badKeyName = name;
            return kb[key].wasPressedThisFrame;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- overlay

    private void OnGUI()
    {
        if (!_open) return;

        try { Draw(); }
        catch (Exception e)
        {
            _open = false;
            Plugin.Logger.LogWarning($"Overlay closed after an error: {e.Message}");
        }
    }

    [HideFromIl2Cpp]
    private void Draw()
    {
        var phases = LoadProbe.Snapshot();

        const float width = 560f;
        var height = 168f + phases.Count * 17f;

        var panel = new Rect(20f, 20f, width, height);
        GUI.Box(panel, GUIContent.none);

        var x = panel.x + 12f;
        var y = panel.y + 10f;
        const float row = 19f;
        var w = width - 24f;

        GUI.Label(new Rect(x, y, w, row), LoadProbe.Loading
            ? "Load Tweaks - loading now..."
            : "Load Tweaks - last load");
        y += row + 4f;

        GUI.Label(new Rect(x, y, w, row), LoadProbe.LastLoadSummary);
        y += row;
        GUI.Label(new Rect(x, y, w, row), LoadBoost.Describe());
        y += row + 8f;

        GUI.Label(new Rect(x, y, w, row), "phase".PadRight(44) + "steps    lasted     busy");
        y += row;

        foreach (var phase in phases)
        {
            var lasted = phase.Last - phase.First;
            var busyMs = phase.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            var line = phase.Name.PadRight(44)
                     + phase.Frames.ToString().PadLeft(5)
                     + (lasted.ToString("0.00") + "s").PadLeft(10)
                     + (busyMs.ToString("0") + "ms").PadLeft(9);

            GUI.Label(new Rect(x, y, w, row), line);
            y += 17f;
        }

        y += 8f;

        const float buttonW = 150f;
        const float buttonH = 24f;

        if (GUI.Button(new Rect(x, y, buttonW, buttonH), "Write report to log"))
        {
            LoadProbe.WriteReport();
            Notice("written to the BepInEx log");
        }

        y += buttonH + 4f;

        var message = Time.unscaledTime < _noticeUntil
            ? _notice
            : "A phase that lasted much longer than it was busy was waiting for frames.";

        GUI.Label(new Rect(x, y, w, row), message);
    }

    [HideFromIl2Cpp]
    private void Notice(string text)
    {
        _notice = text;
        _noticeUntil = Time.unscaledTime + 4f;
    }
}
