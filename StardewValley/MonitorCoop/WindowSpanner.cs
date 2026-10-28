using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace MonitorCoop
{
    /// <summary>Opens one borderless window across the monitors in a plan, and puts it back afterwards.</summary>
    /// <remarks>The game draws every split-screen player into one window, so a player can only have a whole
    /// monitor if the window covers them all. The game's own window mode is left in charge of everything
    /// else: the mod switches it to windowed first, then restores it.</remarks>
    internal sealed class WindowSpanner
    {
        /// <summary>How often the window may be nudged back into place before the mod assumes something
        /// else owns it and stops fighting over it.</summary>
        private const int MaxCorrections = 10;

        private readonly IMonitor Monitor;
        private readonly ModConfig Config;

        private bool HasSavedWindow;
        private bool SavedBorderless;
        private Point SavedPosition;
        private int SavedBackBufferWidth;
        private int SavedBackBufferHeight;
        private int SavedWindowMode;

        private int Corrections;
        private bool GaveUpCorrecting;

        public WindowSpanner(IMonitor monitor, ModConfig config)
        {
            Monitor = monitor;
            Config = config;
        }

        /// <summary>The plan currently applied to the window, or null.</summary>
        public SplitPlan Plan { get; private set; }

        public bool IsSpanning { get { return Plan != null; } }

        /// <summary>Every display the game can see, indexed the way the game indexes them (0 is the primary).</summary>
        public static List<LayoutRect> GetDisplays()
        {
            List<LayoutRect> displays = new List<LayoutRect>();
            GameWindow window = GameRunner.instance != null ? GameRunner.instance.Window : null;
            if (window == null)
                return displays;

            for (int index = 0; index < 16; index++)
            {
                // Out-of-range indexes come back as an empty rectangle, which is how the count is found.
                Rectangle bounds = window.GetDisplayBounds(index);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    break;

                displays.Add(new LayoutRect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            }
            return displays;
        }

        /// <summary>Spans the window over the plan's displays and tells the game to re-lay out its players.</summary>
        public void Apply(SplitPlan plan)
        {
            GameWindow window = GameRunner.instance != null ? GameRunner.instance.Window : null;
            if (window == null || plan == null)
                return;

            if (!HasSavedWindow)
                SaveWindow(window);

            // A fullscreen window belongs to one display, so the game has to be windowed first. Its own
            // option handles the transition (and remembers it for next launch); the mode is restored later.
            if (Game1.options != null && !Game1.options.isCurrentlyWindowed())
                Game1.options.setWindowedOption(1);

            window.IsBorderless = true;
            Game1.graphics.PreferredBackBufferWidth = plan.Window.Width;
            Game1.graphics.PreferredBackBufferHeight = plan.Window.Height;
            Game1.graphics.ApplyChanges();

            // ApplyChanges re-centres the window on its own display, so the position comes last.
            window.Position = new Point(plan.Window.X, plan.Window.Y);

            Plan = plan;
            Corrections = 0;
            GaveUpCorrecting = false;

            // The patch only applies a plan once the window really is this size, so hand it over now and
            // ask the game to recompute every player's share of the window.
            SplitScreenPatch.Plan = plan;
            SplitScreenPatch.ZoomModifier = Config.ZoomModifier;
            GameRunner.instance.OnWindowSizeChange(null, null);

            Monitor.Log("Spanning the monitors: " + plan.Describe(), LogLevel.Info);
        }

        /// <summary>Hands the window back to the game, restoring what it looked like before.</summary>
        public void Restore()
        {
            if (!IsSpanning)
                return;

            SplitScreenPatch.Plan = null;
            Plan = null;

            GameWindow window = GameRunner.instance != null ? GameRunner.instance.Window : null;
            if (window != null && HasSavedWindow && Config.RestoreWindowOnExit)
            {
                window.IsBorderless = SavedBorderless;
                Game1.graphics.PreferredBackBufferWidth = SavedBackBufferWidth;
                Game1.graphics.PreferredBackBufferHeight = SavedBackBufferHeight;
                Game1.graphics.ApplyChanges();
                window.Position = SavedPosition;

                if (SavedWindowMode != 1 && Game1.options != null)
                    Game1.options.setWindowedOption(SavedWindowMode);
            }

            HasSavedWindow = false;
            if (GameRunner.instance != null)
                GameRunner.instance.OnWindowSizeChange(null, null);

            Monitor.Log("Back to the game's own window.", LogLevel.Info);
        }

        /// <summary>Puts the window back where the plan wants it if something moved or resized it, which the
        /// game does itself every time it applies graphics changes.</summary>
        public void Reassert()
        {
            SplitPlan plan = Plan;
            if (plan == null || GaveUpCorrecting)
                return;

            GameWindow window = GameRunner.instance != null ? GameRunner.instance.Window : null;
            if (window == null)
                return;

            Rectangle bounds = window.ClientBounds;
            bool wrongSize = bounds.Width != plan.Window.Width || bounds.Height != plan.Window.Height;
            bool wrongPlace = bounds.X != plan.Window.X || bounds.Y != plan.Window.Y;
            if (!wrongSize && !wrongPlace && window.IsBorderless)
            {
                // The window is where it should be, so the budget below is about something moving it back
                // repeatedly, not about a long session with the odd correction in it.
                Corrections = 0;
                return;
            }

            if (++Corrections > MaxCorrections)
            {
                GaveUpCorrecting = true;
                Monitor.Log("Something keeps moving the game window; leaving it alone. Press the toggle key"
                    + " to try again.", LogLevel.Warn);
                return;
            }

            if (!window.IsBorderless)
                window.IsBorderless = true;

            if (wrongSize)
            {
                Game1.graphics.PreferredBackBufferWidth = plan.Window.Width;
                Game1.graphics.PreferredBackBufferHeight = plan.Window.Height;
                Game1.graphics.ApplyChanges();
            }

            window.Position = new Point(plan.Window.X, plan.Window.Y);
        }

        private void SaveWindow(GameWindow window)
        {
            SavedBorderless = window.IsBorderless;
            SavedPosition = window.Position;
            SavedBackBufferWidth = Game1.graphics.PreferredBackBufferWidth;
            SavedBackBufferHeight = Game1.graphics.PreferredBackBufferHeight;

            SavedWindowMode = 1;
            if (Game1.options != null)
            {
                if (Game1.options.isCurrentlyFullscreen())
                    SavedWindowMode = 2;
                else if (Game1.options.isCurrentlyWindowedBorderless())
                    SavedWindowMode = 0;
            }

            HasSavedWindow = true;
        }
    }
}
