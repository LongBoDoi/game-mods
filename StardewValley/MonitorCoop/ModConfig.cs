using System;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace MonitorCoop
{
    /// <summary>config.json, written next to the mod the first time it runs.</summary>
    internal sealed class ModConfig
    {
        /// <summary>Whether to spread split-screen players over the monitors at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Turns the monitor layout on and off while the game is running.</summary>
        public KeybindList ToggleKey { get; set; } = new KeybindList(SButton.F7);

        /// <summary>Which displays to use, in seating order, e.g. [0, 2]. Empty decides from the desktop
        /// layout. These are the game's display numbers, which the "monitorcoop" console command lists.</summary>
        public int[] Displays { get; set; } = new int[0];

        /// <summary>With <see cref="Displays"/> empty, seat player one on the primary display and the
        /// others from left to right. Turn this off to seat everyone purely left to right.</summary>
        public bool PlayerOneOnPrimary { get; set; } = true;

        /// <summary>How much the world is zoomed out for each player. The game halves it in split-screen
        /// because each player normally has a quarter or half of the window; with a monitor each, 1 shows
        /// the same amount of the farm as playing alone. 0.5 is the vanilla split-screen view.</summary>
        public float ZoomModifier { get; set; } = 1f;

        /// <summary>Put the window back the way it was (size, position, fullscreen mode) once the extra
        /// players leave.</summary>
        public bool RestoreWindowOnExit { get; set; } = true;

        /// <summary>Hold the mouse pointer inside player one's monitor while the game has focus, so it
        /// cannot wander onto another player's screen. Either way each player's cursor is measured from
        /// their own monitor and can reach all of it; turning this off only lets the pointer itself leave,
        /// with the cursor waiting at the edge. Alt-tab always frees the pointer.</summary>
        public bool ConfinePointerToOwnScreen { get; set; } = true;

        /// <summary>Clamps values a hand-edited config could get wrong.</summary>
        public void Validate(IMonitor monitor)
        {
            if (ToggleKey == null)
                ToggleKey = new KeybindList();

            if (Displays == null)
                Displays = new int[0];

            float zoom = ZoomModifier;
            if (float.IsNaN(zoom) || float.IsInfinity(zoom))
                zoom = 1f;
            zoom = Math.Max(0.25f, Math.Min(2f, zoom));
            if (zoom != ZoomModifier)
            {
                monitor.Log("ZoomModifier must be between 0.25 and 2; using " + zoom + ".", LogLevel.Warn);
                ZoomModifier = zoom;
            }
        }
    }
}
