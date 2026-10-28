using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;

namespace MonitorCoop
{
    /// <summary>Keeps each player's cursor on that player's own monitor.</summary>
    /// <remarks>The game reports the real mouse in window pixels, and in vanilla split-screen player one's
    /// screen always starts at the window's corner, so the two are the same thing. Spanning the monitors
    /// breaks that: a player one sitting on the second monitor in the window has their cursor shifted by the
    /// first monitor's width, and the cursor's range is that other monitor's size rather than their own.
    /// This measures the cursor from the player's own screen instead and holds it inside it, so every player
    /// has their whole monitor and nobody's cursor wanders onto someone else's.</remarks>
    internal static class CursorPatch
    {
        private static IMonitor Log;
        private static bool LoggedError;

        /// <summary>Whether to hold the mouse pointer itself inside player one's monitor, rather than only
        /// the cursor the game draws.</summary>
        public static bool ConfinePointer = true;

        /// <summary>Patches the game. Returns false if the input code no longer looks the way this expects,
        /// which leaves the cursor as the game handles it.</summary>
        public static bool Apply(Harmony harmony, IMonitor monitor)
        {
            Log = monitor;

            // The game only ever reads the mouse through GetMouseState and only ever moves it through
            // SetMousePosition, so these two cover every cursor in the game.
            MethodInfo read = AccessTools.Method(typeof(InputState), nameof(InputState.GetMouseState));
            MethodInfo write = AccessTools.Method(typeof(InputState), nameof(InputState.SetMousePosition),
                new[] { typeof(int), typeof(int) });
            if (read == null || write == null)
            {
                monitor.Log("InputState.GetMouseState/SetMousePosition not found; cursors will be left to the"
                    + " game, so player one's cursor may be offset while the monitors are spanned.", LogLevel.Error);
                return false;
            }

            try
            {
                harmony.Patch(read, postfix: new HarmonyMethod(typeof(CursorPatch), nameof(KeepCursorOnOwnScreen)));
                harmony.Patch(write, prefix: new HarmonyMethod(typeof(CursorPatch), nameof(MoveCursorBackToWindow)));
            }
            catch (Exception ex)
            {
                monitor.Log("Could not patch the mouse input: " + ex.Message, LogLevel.Error);
                return false;
            }

            return true;
        }

        /// <summary>Reports the cursor on the player's own screen: measured from its corner, never past its
        /// edges. For player one, who has the real mouse, the pointer is pushed back too so that it cannot
        /// cross onto another player's monitor.</summary>
        public static void KeepCursorOnOwnScreen(ref MouseState __result)
        {
            try
            {
                // Only while the mod is laying the players out over the monitors; otherwise every screen is
                // the window and the game's own handling is right.
                if (SplitScreenPatch.Plan == null)
                    return;

                Game1 instance = Game1.game1;
                if (instance == null)
                    return;

                Rectangle screen = instance.localMultiplayerWindow;
                if (screen.Width <= 0 || screen.Height <= 0)
                    return;

                // The real mouse is in window pixels; the cursors the game simulates for the other players
                // are already measured from their own screen.
                bool hasRealMouse = instance.IsMainInstance;

                int x, y;
                Layout.CursorOnOwnScreen(__result.X, __result.Y,
                    new LayoutRect(screen.X, screen.Y, screen.Width, screen.Height), hasRealMouse, out x, out y);

                if (hasRealMouse && ConfinePointer && (x + screen.X != __result.X || y + screen.Y != __result.Y)
                    && GameRunner.instance != null && GameRunner.instance.IsActive)
                {
                    // Held against the edge the pointer stays there; let go of the window (alt-tab) and it is
                    // free again, which is the way out to the other monitors.
                    Mouse.SetPosition(x + screen.X, y + screen.Y);
                }

                if (x != __result.X || y != __result.Y)
                {
                    __result = new MouseState(x, y, __result.ScrollWheelValue, __result.LeftButton,
                        __result.MiddleButton, __result.RightButton, __result.XButton1, __result.XButton2);
                }
            }
            catch (Exception ex)
            {
                Report("Could not place a player's cursor: ", ex);
            }
        }

        /// <summary>Turns a position on player one's screen back into the window position the game needs to
        /// move the pointer to, which is the other half of measuring the cursor from their own screen.</summary>
        public static void MoveCursorBackToWindow(ref int x, ref int y)
        {
            try
            {
                if (SplitScreenPatch.Plan == null)
                    return;

                Game1 instance = Game1.game1;
                if (instance == null || !instance.IsMainInstance)
                    return;

                Rectangle screen = instance.localMultiplayerWindow;
                if (screen.Width <= 0 || screen.Height <= 0)
                    return;

                int localX, localY;
                Layout.CursorOnOwnScreen(x, y, new LayoutRect(screen.X, screen.Y, screen.Width, screen.Height),
                    false, out localX, out localY);
                x = localX + screen.X;
                y = localY + screen.Y;
            }
            catch (Exception ex)
            {
                Report("Could not move a player's cursor: ", ex);
            }
        }

        private static void Report(string what, Exception ex)
        {
            if (LoggedError)
                return;

            LoggedError = true;
            Log.Log(what + ex, LogLevel.Error);
        }
    }
}
