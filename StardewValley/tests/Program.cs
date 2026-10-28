using System;
using System.Collections.Generic;
using MonitorCoop;

/// <summary>Offline checks for the monitor layout: which player ends up on which monitor, how big the window
/// has to be, and that the fractions handed to the game turn back into exactly the right pixels.</summary>
internal static class Program
{
    private static int Checks;

    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new Exception(message);
        Checks++;
    }

    private static void CheckRect(LayoutRect actual, int x, int y, int width, int height, string message)
    {
        Check(actual.Equals(new LayoutRect(x, y, width, height)),
            message + ": expected " + new LayoutRect(x, y, width, height) + ", got " + actual);
    }

    private static LayoutRect Display(int x, int y, int width, int height)
    {
        return new LayoutRect(x, y, width, height);
    }

    private static SplitPlan Build(List<LayoutRect> displays, int players, bool playerOneOnPrimary = true,
        int[] requested = null)
    {
        return Layout.Build(displays, Layout.Order(displays, requested, playerOneOnPrimary), players);
    }

    /// <summary>Repeats what Game1.SetWindowSize does with the fractions, so the test fails if the rounding
    /// ever stops landing on the exact rectangle.</summary>
    private static LayoutRect ThroughTheGame(LayoutRect rect, int windowWidth, int windowHeight)
    {
        float x, y, width, height;
        Layout.ToSlot(rect, windowWidth, windowHeight, out x, out y, out width, out height);
        return new LayoutRect(
            (int)(windowWidth * x),
            (int)(windowHeight * y),
            (int)Math.Ceiling((float)windowWidth * width),
            (int)Math.Ceiling((float)windowHeight * height));
    }

    private static void TestDistribute()
    {
        Check(Layout.Distribute(2, 2).Length == 2, "two players on two displays uses both");
        Check(Layout.Distribute(2, 2)[0] == 1 && Layout.Distribute(2, 2)[1] == 1, "one player each");

        int[] threeOnTwo = Layout.Distribute(3, 2);
        Check(threeOnTwo.Length == 2 && threeOnTwo[0] == 2 && threeOnTwo[1] == 1, "three players on two displays is 2 + 1");

        int[] fourOnTwo = Layout.Distribute(4, 2);
        Check(fourOnTwo.Length == 2 && fourOnTwo[0] == 2 && fourOnTwo[1] == 2, "four players on two displays is 2 + 2");

        int[] twoOnThree = Layout.Distribute(2, 3);
        Check(twoOnThree.Length == 2, "an empty display is left out");

        int[] fourOnFour = Layout.Distribute(4, 4);
        Check(fourOnFour.Length == 4 && fourOnFour[3] == 1, "four players on four displays is one each");

        Check(Layout.Distribute(1, 2).Length == 1, "one player needs one display");
        Check(Layout.Distribute(0, 2).Length == 0, "no players, no displays used");
        Check(Layout.Distribute(2, 0).Length == 0, "no displays, nothing to distribute");
    }

    private static void TestOrder()
    {
        // Primary (display 0) physically on the right of the second monitor.
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(1920, 0, 1920, 1080),
            Display(0, 0, 1920, 1080)
        };

        List<int> withPrimaryFirst = Layout.Order(displays, null, true);
        Check(withPrimaryFirst.Count == 2 && withPrimaryFirst[0] == 0 && withPrimaryFirst[1] == 1,
            "player one gets the primary display");

        List<int> leftToRight = Layout.Order(displays, null, false);
        Check(leftToRight[0] == 1 && leftToRight[1] == 0, "without the primary rule, seating runs left to right");

        List<int> requested = Layout.Order(displays, new[] { 1, 0 }, true);
        Check(requested[0] == 1 && requested[1] == 0, "the config's display order wins");

        List<int> deduped = Layout.Order(displays, new[] { 1, 1, 5, -2 }, true);
        Check(deduped.Count == 1 && deduped[0] == 1, "repeated and out-of-range displays are dropped");

        List<int> allBad = Layout.Order(displays, new[] { 7, 9 }, true);
        Check(allBad.Count == 2 && allBad[0] == 0, "a config naming no real display falls back to the layout");

        // Monitors side by side but at different heights are still one row, counted left to right.
        List<int> staggered = Layout.Order(new List<LayoutRect>
        {
            Display(1920, 0, 1920, 1080),
            Display(0, 360, 2560, 1440)
        }, null, false);
        Check(staggered[0] == 1 && staggered[1] == 0, "a staggered pair is seated left to right, not top to bottom");

        List<int> grid = Layout.Order(new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 0, 1920, 1080),
            Display(0, 1080, 1920, 1080),
            Display(1920, 1080, 1920, 1080)
        }, null, false);
        Check(grid[0] == 0 && grid[1] == 1 && grid[2] == 2 && grid[3] == 3,
            "a 2x2 grid is seated row by row, like the game's own four-player layout");

        List<int> stacked = Layout.Order(new List<LayoutRect>
        {
            Display(0, 1080, 1920, 1080),
            Display(0, 0, 1920, 1080)
        }, null, false);
        Check(stacked[0] == 1 && stacked[1] == 0, "monitors stacked vertically are seated top to bottom");

        Check(Layout.Order(new List<LayoutRect>(), null, true).Count == 0, "no displays, no order");
        Check(Layout.Order(null, null, true).Count == 0, "no display list at all is handled");
    }

    private static void TestSideBySide()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 0, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 2);
        Check(plan != null, "two players on two monitors produces a plan");
        CheckRect(plan.Window, 0, 0, 3840, 1080, "the window covers both monitors");
        CheckRect(plan.Players[0], 0, 0, 1920, 1080, "player one gets the whole left monitor");
        CheckRect(plan.Players[1], 1920, 0, 1920, 1080, "player two gets the whole right monitor");
        Check(plan.DisplayForPlayer[0] == 0 && plan.DisplayForPlayer[1] == 1, "players are recorded against their display");
    }

    private static void TestPrimaryOnTheRight()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(1920, 0, 1920, 1080),
            Display(0, 0, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 2);
        CheckRect(plan.Window, 0, 0, 3840, 1080, "the window still covers both monitors");
        CheckRect(plan.Players[0], 1920, 0, 1920, 1080, "player one is on the primary, which is the right monitor");
        CheckRect(plan.Players[1], 0, 0, 1920, 1080, "player two is on the left monitor");
    }

    private static void TestMonitorLeftOfPrimary()
    {
        // Windows puts a monitor arranged to the left of the primary at a negative x.
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(-1920, 0, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 2);
        CheckRect(plan.Window, -1920, 0, 3840, 1080, "the window starts at the negative coordinate");
        CheckRect(plan.Players[0], 1920, 0, 1920, 1080, "player one's slot is relative to the window, not the desktop");
        CheckRect(plan.Players[1], 0, 0, 1920, 1080, "player two sits at the window's left edge");
    }

    private static void TestMixedSizesAndStagger()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 2560, 1440),
            Display(2560, 360, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 2);
        CheckRect(plan.Window, 0, 0, 4480, 1440, "the window is the bounding box of both monitors");
        CheckRect(plan.Players[0], 0, 0, 2560, 1440, "the bigger monitor keeps its full resolution");
        CheckRect(plan.Players[1], 2560, 360, 1920, 1080, "the smaller monitor keeps its own offset");
    }

    private static void TestVerticalStack()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(0, 1080, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 2);
        CheckRect(plan.Window, 0, 0, 1920, 2160, "a stacked pair makes a tall window");
        CheckRect(plan.Players[1], 0, 1080, 1920, 1080, "player two is on the lower monitor");
    }

    private static void TestThreePlayersOnTwoMonitors()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 0, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 3);
        Check(plan.PlayerCount == 3, "all three players get a slot");
        CheckRect(plan.Players[0], 0, 0, 960, 1080, "the two players sharing a monitor split it like vanilla");
        CheckRect(plan.Players[1], 960, 0, 960, 1080, "the second of them gets the other half");
        CheckRect(plan.Players[2], 1920, 0, 1920, 1080, "the third player gets a whole monitor");
        Check(plan.DisplayForPlayer[0] == 0 && plan.DisplayForPlayer[1] == 0 && plan.DisplayForPlayer[2] == 1,
            "the shared monitor holds the first two players");
    }

    private static void TestFourPlayersOnTwoMonitors()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 0, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 4);
        CheckRect(plan.Players[0], 0, 0, 960, 1080, "two players per monitor, left half");
        CheckRect(plan.Players[1], 960, 0, 960, 1080, "two players per monitor, right half");
        CheckRect(plan.Players[2], 1920, 0, 960, 1080, "the second monitor splits the same way");
        CheckRect(plan.Players[3], 2880, 0, 960, 1080, "the fourth player gets the far half");
    }

    private static void TestFourPlayersOnFourMonitors()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 0, 1920, 1080),
            Display(0, 1080, 1920, 1080),
            Display(1920, 1080, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 4);
        CheckRect(plan.Window, 0, 0, 3840, 2160, "the window covers the whole grid");
        for (int i = 0; i < 4; i++)
        {
            Check(plan.Players[i].Width == 1920 && plan.Players[i].Height == 1080,
                "each player gets a whole monitor (player " + (i + 1) + ")");
            Check(plan.DisplayForPlayer[i] == i, "one player per monitor, in seating order");
        }
    }

    private static void TestTwoPlayersOnThreeMonitors()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 0, 1920, 1080),
            Display(3840, 0, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 2);
        CheckRect(plan.Window, 0, 0, 3840, 1080, "the unused monitor is left out of the window");
        Check(plan.DisplayForPlayer[0] == 0 && plan.DisplayForPlayer[1] == 1, "the first two monitors are used");
    }

    private static void TestNothingToDo()
    {
        List<LayoutRect> one = new List<LayoutRect> { Display(0, 0, 1920, 1080) };
        Check(Build(one, 2) == null, "one monitor means the game keeps its own split-screen");
        Check(Build(one, 1) == null, "one player means nothing to do");

        List<LayoutRect> two = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 0, 1920, 1080)
        };
        Check(Build(two, 1) == null, "a single player never spans monitors");
        Check(Build(two, 2, true, new[] { 1 }) == null, "a config naming one display means nothing to do");
        Check(Layout.Build(null, new List<int> { 0, 1 }, 2) == null, "no displays, no plan");
        Check(Layout.Build(two, null, 2) == null, "no order, no plan");
    }

    private static void TestSlotsStayInsideTheWindow()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 2560, 1440),
            Display(2560, 360, 1920, 1080),
            Display(-1280, 1440, 1280, 1024)
        };

        for (int players = 2; players <= 4; players++)
        {
            SplitPlan plan = Build(displays, players);
            Check(plan != null, "a plan for " + players + " players");
            foreach (LayoutRect slot in plan.Players)
            {
                Check(slot.X >= 0 && slot.Y >= 0, "slots are window-relative, never negative");
                Check(slot.Right <= plan.Window.Width && slot.Bottom <= plan.Window.Height,
                    "slots stay inside the window");
                Check(slot.Width > 0 && slot.Height > 0, "no empty slot");
            }
        }
    }

    private static void TestSharedMonitorHalvesDoNotOverlap()
    {
        // An odd width is the case where flooring the origin and rounding the size up could overlap or
        // leave a gap between the two halves.
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1365, 767),
            Display(1365, 0, 1365, 767)
        };

        SplitPlan plan = Build(displays, 3);
        LayoutRect left = plan.Players[0];
        LayoutRect right = plan.Players[1];
        Check(left.X == 0 && right.X == 682, "the halves of an odd-width monitor meet where the game puts them");
        Check(left.Right >= right.X, "no gap between the two halves");
        Check(left.Right - right.X <= 1, "the halves overlap by at most the pixel the game rounds up");
        Check(right.Right == 1365, "the right half reaches the edge of its monitor");
    }

    private static void TestFractionsSurviveTheGamesRounding()
    {
        LayoutRect[] cases =
        {
            new LayoutRect(0, 0, 1920, 1080),
            new LayoutRect(1920, 0, 1920, 1080),
            new LayoutRect(2560, 360, 1920, 1080),
            new LayoutRect(0, 0, 2560, 1440),
            new LayoutRect(960, 0, 960, 1080),
            new LayoutRect(682, 0, 683, 767),
            new LayoutRect(3840, 1080, 1366, 768),
            new LayoutRect(0, 2160, 3840, 2160),
            new LayoutRect(7679, 4319, 1, 1)
        };
        int[][] windows =
        {
            new[] { 3840, 1080 },
            new[] { 4480, 1440 },
            new[] { 1920, 2160 },
            new[] { 7680, 4320 },
            new[] { 5206, 1848 },
            new[] { 2730, 767 }
        };

        foreach (int[] window in windows)
        {
            foreach (LayoutRect rect in cases)
            {
                if (rect.Right > window[0] || rect.Bottom > window[1])
                    continue;

                LayoutRect roundTripped = ThroughTheGame(rect, window[0], window[1]);
                Check(roundTripped.Equals(rect), "the game's rounding reproduces " + rect + " in a "
                    + window[0] + "x" + window[1] + " window, got " + roundTripped);
            }
        }
    }

    private static void TestEveryPlanSurvivesTheGamesRounding()
    {
        List<LayoutRect>[] setups =
        {
            new List<LayoutRect> { Display(0, 0, 1920, 1080), Display(1920, 0, 1920, 1080) },
            new List<LayoutRect> { Display(0, 0, 2560, 1440), Display(2560, 360, 1920, 1080) },
            new List<LayoutRect> { Display(0, 0, 1920, 1080), Display(-1920, 0, 1920, 1080) },
            new List<LayoutRect> { Display(0, 0, 3840, 2160), Display(3840, 0, 2560, 1440) },
            new List<LayoutRect> { Display(0, 0, 1365, 767), Display(1365, 0, 1365, 767) },
            new List<LayoutRect>
            {
                Display(0, 0, 1920, 1080), Display(1920, 0, 1920, 1080),
                Display(0, 1080, 1920, 1080), Display(1920, 1080, 1920, 1080)
            }
        };

        foreach (List<LayoutRect> displays in setups)
        {
            for (int players = 2; players <= 4; players++)
            {
                SplitPlan plan = Build(displays, players);
                if (plan == null)
                    continue;

                foreach (LayoutRect slot in plan.Players)
                {
                    LayoutRect roundTripped = ThroughTheGame(slot, plan.Window.Width, plan.Window.Height);
                    Check(roundTripped.Equals(slot), players + " players: the game reproduces " + slot
                        + " in a " + plan.Window + " window, got " + roundTripped);
                }
            }
        }
    }

    private static void TestPlanComparison()
    {
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 0, 1920, 1080)
        };
        List<LayoutRect> moved = new List<LayoutRect>
        {
            Display(0, 0, 1920, 1080),
            Display(1920, 100, 1920, 1080)
        };

        SplitPlan plan = Build(displays, 2);
        Check(plan.Matches(Build(displays, 2)), "the same monitors and players give the same plan");
        Check(!plan.Matches(Build(displays, 3)), "a player joining changes the plan");
        Check(!plan.Matches(Build(moved, 2)), "rearranging the monitors changes the plan");
        Check(!plan.Matches(null), "no plan never matches");

        // With the primary on the right, dropping the primary rule swaps which monitor player one gets.
        List<LayoutRect> primaryOnTheRight = new List<LayoutRect>
        {
            Display(1920, 0, 1920, 1080),
            Display(0, 0, 1920, 1080)
        };
        Check(!Build(primaryOnTheRight, 2).Matches(Build(primaryOnTheRight, 2, false)),
            "swapping the seating changes the plan");
        Check(plan.Describe().Contains("P1 on display 0"), "the description names each player's display");
    }

    private static void TestCursorOnItsOwnScreen()
    {
        // The arrangement that showed the bug: a 2560x1440 primary at the desktop origin with a 1920x1080
        // monitor to its left, so player one's screen sits 1920 px into the window.
        List<LayoutRect> displays = new List<LayoutRect>
        {
            Display(0, 0, 2560, 1440),
            Display(-1920, 356, 1920, 1080)
        };
        SplitPlan plan = Build(displays, 2);
        CheckRect(plan.Window, -1920, 0, 4480, 1440, "the window covers both monitors");
        CheckRect(plan.Players[0], 1920, 0, 2560, 1440, "player one's screen starts a monitor into the window");
        CheckRect(plan.Players[1], 0, 356, 1920, 1080, "player two keeps its own offset");

        LayoutRect playerOne = plan.Players[0];
        int x, y;

        // The real mouse arrives in window pixels and has to come out measured from player one's screen.
        Layout.CursorOnOwnScreen(1920, 0, playerOne, true, out x, out y);
        Check(x == 0 && y == 0, "the top left of player one's monitor is the cursor's origin");

        Layout.CursorOnOwnScreen(1920 + 1300, 700, playerOne, true, out x, out y);
        Check(x == 1300 && y == 700, "a position on player one's monitor keeps its place");

        Layout.CursorOnOwnScreen(4479, 1439, playerOne, true, out x, out y);
        Check(x == 2559 && y == 1439, "the far corner of player one's monitor is reachable");

        // Its whole monitor, not the other one's size: 2560x1440, not 1920x1080.
        Layout.CursorOnOwnScreen(1920 + 2400, 1300, playerOne, true, out x, out y);
        Check(x == 2400 && y == 1300, "the cursor is not limited by the other monitor's width or height");

        // And never off it.
        Layout.CursorOnOwnScreen(0, 0, playerOne, true, out x, out y);
        Check(x == 0 && y == 0, "a pointer over player two's monitor is held at player one's edge");

        Layout.CursorOnOwnScreen(10000, 10000, playerOne, true, out x, out y);
        Check(x == 2559 && y == 1439, "a pointer past the window stays on player one's monitor");

        // Player two's cursor is already measured from their own screen, so it is only bounded.
        LayoutRect playerTwo = plan.Players[1];
        Layout.CursorOnOwnScreen(500, 500, playerTwo, false, out x, out y);
        Check(x == 500 && y == 500, "a controller cursor is not moved, only bounded");

        Layout.CursorOnOwnScreen(5000, 5000, playerTwo, false, out x, out y);
        Check(x == 1919 && y == 1079, "a controller cursor stops at its own monitor's edge");

        Layout.CursorOnOwnScreen(-50, -50, playerTwo, false, out x, out y);
        Check(x == 0 && y == 0, "a controller cursor cannot go above or left of its own screen");

        // The edge belongs to this screen, never to the neighbour's first column.
        foreach (LayoutRect screen in plan.Players)
        {
            Layout.CursorOnOwnScreen(int.MaxValue, int.MaxValue, screen, false, out x, out y);
            Check(x < screen.Width && y < screen.Height, "the cursor stays within its own screen's pixels");
        }

        // Player one on the left monitor instead: no shift, but still bounded by their own monitor.
        SplitPlan leftFirst = Build(displays, 2, false);
        LayoutRect onTheLeft = leftFirst.Players[0];
        CheckRect(onTheLeft, 0, 356, 1920, 1080, "without the primary rule player one takes the left monitor");
        Layout.CursorOnOwnScreen(500, 356 + 500, onTheLeft, true, out x, out y);
        Check(x == 500 && y == 500, "a screen at a vertical offset measures from its own top edge");
        // Window x 3000 is on the primary monitor, window y 1200 is still within the left monitor's rows.
        Layout.CursorOnOwnScreen(3000, 1200, onTheLeft, true, out x, out y);
        Check(x == 1919 && y == 1200 - 356, "the pointer cannot cross onto the primary monitor");

        // A degenerate screen must not produce a negative position.
        Layout.CursorOnOwnScreen(5, 5, new LayoutRect(0, 0, 0, 0), true, out x, out y);
        Check(x == 0 && y == 0, "an empty screen clamps to its corner rather than going negative");
    }

    private static void TestVanillaSlots()
    {
        Check(Layout.VanillaSlots(1).Length == 1, "one player takes the whole screen");
        Check(Layout.VanillaSlots(2).Length == 2 && Layout.VanillaSlots(2)[1][0] == 0.5f, "two players split left and right");
        Check(Layout.VanillaSlots(3).Length == 3 && Layout.VanillaSlots(3)[0][3] == 0.5f, "three players: one on top, two below");
        Check(Layout.VanillaSlots(4).Length == 4, "four players get quarters");
        Check(Layout.VanillaSlots(9).Length == 1, "an impossible count falls back to the whole screen");
    }

    public static int Main()
    {
        try
        {
            TestDistribute();
            TestOrder();
            TestSideBySide();
            TestPrimaryOnTheRight();
            TestMonitorLeftOfPrimary();
            TestMixedSizesAndStagger();
            TestVerticalStack();
            TestThreePlayersOnTwoMonitors();
            TestFourPlayersOnTwoMonitors();
            TestFourPlayersOnFourMonitors();
            TestTwoPlayersOnThreeMonitors();
            TestNothingToDo();
            TestSlotsStayInsideTheWindow();
            TestSharedMonitorHalvesDoNotOverlap();
            TestFractionsSurviveTheGamesRounding();
            TestEveryPlanSurvivesTheGamesRounding();
            TestPlanComparison();
            TestCursorOnItsOwnScreen();
            TestVanillaSlots();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL after " + Checks + " checks: " + ex.Message);
            return 1;
        }

        Console.WriteLine("PASS: " + Checks + " checks");
        return 0;
    }
}
