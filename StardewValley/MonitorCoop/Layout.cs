using System;
using System.Collections.Generic;
using System.Text;

namespace MonitorCoop
{
    /// <summary>A rectangle in desktop pixels. Deliberately free of XNA types so the layout maths can be
    /// tested without the game.</summary>
    internal struct LayoutRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public LayoutRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int Right { get { return X + Width; } }
        public int Bottom { get { return Y + Height; } }

        public bool Equals(LayoutRect other)
        {
            return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
        }

        public override string ToString()
        {
            return Width + "x" + Height + " at " + X + "," + Y;
        }
    }

    /// <summary>Which window to open, and which part of it each player gets.</summary>
    internal sealed class SplitPlan
    {
        /// <summary>The window to span, in desktop coordinates: the bounding box of the displays in use.</summary>
        public LayoutRect Window;

        /// <summary>Each player's slice of that window in window-relative pixels, indexed by the game's
        /// instance index (0 is player one).</summary>
        public LayoutRect[] Players;

        /// <summary>The display each player sits on, for the console command and the log.</summary>
        public int[] DisplayForPlayer;

        public int PlayerCount { get { return Players.Length; } }

        public bool Matches(SplitPlan other)
        {
            if (other == null || other.PlayerCount != PlayerCount || !other.Window.Equals(Window))
                return false;

            for (int i = 0; i < Players.Length; i++)
            {
                if (!other.Players[i].Equals(Players[i]) || other.DisplayForPlayer[i] != DisplayForPlayer[i])
                    return false;
            }
            return true;
        }

        public string Describe()
        {
            StringBuilder text = new StringBuilder();
            text.Append("window ").Append(Window.ToString());
            for (int i = 0; i < Players.Length; i++)
            {
                text.Append("; P").Append(i + 1).Append(" on display ").Append(DisplayForPlayer[i])
                    .Append(" -> ").Append(Players[i].ToString());
            }
            return text.ToString();
        }
    }

    internal static class Layout
    {
        /// <summary>Spreads players over displays as evenly as possible, filling the first displays first:
        /// three players on two displays gives two and one. Displays that would get nobody are left out.</summary>
        public static int[] Distribute(int players, int displays)
        {
            if (players < 1 || displays < 1)
                return new int[0];

            int used = Math.Min(players, displays);
            int[] perDisplay = new int[used];
            int each = players / used;
            int extra = players % used;
            for (int i = 0; i < used; i++)
                perDisplay[i] = each + (i < extra ? 1 : 0);
            return perDisplay;
        }

        /// <summary>The game's own split of one screen between one and four players, as (x, y, width, height)
        /// fractions. Taken from Game1.SetWindowSize, so players who do share a display get exactly the
        /// vanilla split-screen layout on it.</summary>
        public static float[][] VanillaSlots(int count)
        {
            switch (count)
            {
                case 2:
                    return new[]
                    {
                        new[] { 0f, 0f, 0.5f, 1f },
                        new[] { 0.5f, 0f, 0.5f, 1f }
                    };

                case 3:
                    return new[]
                    {
                        new[] { 0f, 0f, 1f, 0.5f },
                        new[] { 0f, 0.5f, 0.5f, 0.5f },
                        new[] { 0.5f, 0.5f, 0.5f, 0.5f }
                    };

                case 4:
                    return new[]
                    {
                        new[] { 0f, 0f, 0.5f, 0.5f },
                        new[] { 0.5f, 0f, 0.5f, 0.5f },
                        new[] { 0f, 0.5f, 0.5f, 0.5f },
                        new[] { 0.5f, 0.5f, 0.5f, 0.5f }
                    };

                default:
                    return new[] { new[] { 0f, 0f, 1f, 1f } };
            }
        }

        /// <summary>Picks the displays to use, in the order players are seated on them.</summary>
        /// <param name="displays">Every display's bounds, indexed as the game indexes them (0 is the primary).</param>
        /// <param name="requested">Display indexes from the config; empty means decide from the layout.</param>
        /// <param name="playerOneOnPrimary">Seat player one on display 0, and the others left to right.</param>
        public static List<int> Order(IList<LayoutRect> displays, IList<int> requested, bool playerOneOnPrimary)
        {
            List<int> order = new List<int>();
            if (displays == null || displays.Count == 0)
                return order;

            if (requested != null && requested.Count > 0)
            {
                foreach (int index in requested)
                {
                    if (index >= 0 && index < displays.Count && !order.Contains(index))
                        order.Add(index);
                }
                if (order.Count > 0)
                    return order;
            }

            // Reading order: rows of monitors from top to bottom, and within a row from left to right.
            // Monitors are put in the same row when they overlap vertically, so monitors that stand side by
            // side at different heights still count left to right rather than one above the other.
            List<int> byTop = new List<int>();
            for (int i = 0; i < displays.Count; i++)
                byTop.Add(i);
            byTop.Sort(delegate(int a, int b)
            {
                int byY = displays[a].Y.CompareTo(displays[b].Y);
                return byY != 0 ? byY : displays[a].X.CompareTo(displays[b].X);
            });

            List<int> row = new List<int>();
            int rowBottom = 0;
            foreach (int index in byTop)
            {
                if (row.Count > 0 && displays[index].Y >= rowBottom)
                {
                    AddRow(order, row, displays);
                    row.Clear();
                }

                rowBottom = row.Count > 0 ? Math.Min(rowBottom, displays[index].Bottom) : displays[index].Bottom;
                row.Add(index);
            }
            AddRow(order, row, displays);

            if (playerOneOnPrimary && order.Count > 1 && order[0] != 0)
            {
                order.Remove(0);
                order.Insert(0, 0);
            }
            return order;
        }

        private static void AddRow(List<int> order, List<int> row, IList<LayoutRect> displays)
        {
            row.Sort(delegate(int a, int b)
            {
                int byX = displays[a].X.CompareTo(displays[b].X);
                return byX != 0 ? byX : a.CompareTo(b);
            });
            order.AddRange(row);
        }

        /// <summary>Works out the window to span and each player's rectangle inside it. Returns null when
        /// there is nothing to do, i.e. fewer than two players or fewer than two usable displays.</summary>
        public static SplitPlan Build(IList<LayoutRect> displays, IList<int> order, int players)
        {
            if (displays == null || order == null || players < 2 || order.Count < 2)
                return null;

            int[] perDisplay = Distribute(players, order.Count);
            if (perDisplay.Length < 2)
                return null;

            // The window has to cover every display in use. In a staggered layout that bounding box
            // includes desktop that no monitor shows; those parts of the window simply stay invisible,
            // which is how each player still gets their whole screen.
            LayoutRect window = displays[order[0]];
            for (int i = 1; i < perDisplay.Length; i++)
            {
                LayoutRect display = displays[order[i]];
                int left = Math.Min(window.X, display.X);
                int top = Math.Min(window.Y, display.Y);
                window = new LayoutRect(left, top,
                    Math.Max(window.Right, display.Right) - left,
                    Math.Max(window.Bottom, display.Bottom) - top);
            }

            LayoutRect[] slots = new LayoutRect[players];
            int[] displayForPlayer = new int[players];
            int player = 0;
            for (int i = 0; i < perDisplay.Length; i++)
            {
                LayoutRect display = displays[order[i]];
                float[][] shares = VanillaSlots(perDisplay[i]);
                for (int seat = 0; seat < perDisplay[i]; seat++)
                {
                    float[] share = shares[seat];
                    // The game's own rounding: floor the origin, round the size up, so two players sharing
                    // a display meet without a seam.
                    int x = display.X - window.X + (int)(display.Width * share[0]);
                    int y = display.Y - window.Y + (int)(display.Height * share[1]);
                    int width = (int)Math.Ceiling(display.Width * share[2]);
                    int height = (int)Math.Ceiling(display.Height * share[3]);
                    slots[player] = new LayoutRect(x, y, width, height);
                    displayForPlayer[player] = order[i];
                    player++;
                }
            }

            return new SplitPlan { Window = window, Players = slots, DisplayForPlayer = displayForPlayer };
        }

        /// <summary>Puts a cursor position on the player's own screen: measured from that screen's corner, and
        /// never past its edges.</summary>
        /// <param name="rawX">The position the game reported, before this.</param>
        /// <param name="rawY">The position the game reported, before this.</param>
        /// <param name="screen">The player's screen inside the window, in window-relative pixels.</param>
        /// <param name="fromWindow">True when the position is measured from the window's corner (the real
        /// mouse, which the game reports in window pixels) and has to be moved onto this screen; false when
        /// it is already measured from the player's own screen (the controller cursor the game simulates for
        /// the other players).</param>
        public static void CursorOnOwnScreen(int rawX, int rawY, LayoutRect screen, bool fromWindow,
            out int x, out int y)
        {
            x = fromWindow ? rawX - screen.X : rawX;
            y = fromWindow ? rawY - screen.Y : rawY;

            // The far edge is one pixel short of the next screen along, so a cursor held against it stays on
            // this screen instead of landing on the neighbour's first column.
            x = Math.Max(0, Math.Min(x, Math.Max(0, screen.Width - 1)));
            y = Math.Max(0, Math.Min(y, Math.Max(0, screen.Height - 1)));
        }

        /// <summary>Turns a pixel rectangle into the (x, y, width, height) fractions the game expects.</summary>
        /// <remarks>Game1.SetWindowSize turns these back into pixels with <c>(int)(w * x)</c> for the origin
        /// and <c>ceil(w * width)</c> for the size, so the fractions are nudged a quarter of a pixel inwards.
        /// Both roundings then land exactly on the rectangle asked for instead of a pixel either side of it.</remarks>
        public static void ToSlot(LayoutRect rect, int windowWidth, int windowHeight,
            out float x, out float y, out float width, out float height)
        {
            x = windowWidth > 0 ? (rect.X + 0.25f) / windowWidth : 0f;
            y = windowHeight > 0 ? (rect.Y + 0.25f) / windowHeight : 0f;
            width = windowWidth > 0 ? (rect.Width - 0.25f) / windowWidth : 1f;
            height = windowHeight > 0 ? (rect.Height - 0.25f) / windowHeight : 1f;
        }
    }
}
