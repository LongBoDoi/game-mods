package monitorcoop;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;

/**
 * Works out which monitor each split-screen player sits on, and the window that has to be spanned
 * for them. Pure maths on rectangles: no game and no GLFW types, so the tests can cover all of it.
 */
public final class Layout {
    private Layout() {
    }

    /**
     * Spreads players over monitors as evenly as possible, filling the first monitors first: three
     * players on two monitors gives two and one. Monitors that would get nobody are left out.
     */
    public static int[] distribute(int players, int monitors) {
        if (players < 1 || monitors < 1) {
            return new int[0];
        }

        int used = Math.min(players, monitors);
        int[] perMonitor = new int[used];
        int each = players / used;
        int extra = players % used;
        for (int i = 0; i < used; i++) {
            perMonitor[i] = each + (i < extra ? 1 : 0);
        }
        return perMonitor;
    }

    /**
     * The rectangle for one seat on a monitor, laid out the way the game lays out a whole screen.
     * Taken from IsoCamera.getScreenLeft/Top/Width/Height, so players who do share a monitor get
     * the split the game would have given them: two side by side, four in quadrants, three in
     * quadrants with one corner left over.
     *
     * <p>The far half takes the odd pixel of an odd width or height, which closes the one-pixel
     * seam the integer division in the game leaves behind.</p>
     *
     * @param seat  which seat on this monitor, 0 to 3, indexed the way the game indexes players
     * @param seats how many players are on this monitor
     */
    public static Rect seat(int seat, int seats, Rect monitor) {
        boolean splitX = seats > 1;
        boolean splitY = seats > 2;
        int halfW = monitor.w / 2;
        int halfH = monitor.h / 2;

        boolean right = splitX && (seat == 1 || seat == 3);
        boolean bottom = splitY && (seat == 2 || seat == 3);

        int x = right ? halfW : 0;
        int y = bottom ? halfH : 0;
        int w = !splitX ? monitor.w : (right ? monitor.w - halfW : halfW);
        int h = !splitY ? monitor.h : (bottom ? monitor.h - halfH : halfH);
        return new Rect(monitor.x + x, monitor.y + y, w, h);
    }

    /**
     * Picks the monitors to use, in the order players are seated on them.
     *
     * @param monitors           every monitor's bounds in desktop pixels, indexed the way GLFW
     *                           lists them (0 is the primary monitor)
     * @param requested          monitor indexes from the settings; empty decides from the layout
     * @param playerOneOnPrimary seat player one on the primary monitor, the others left to right
     */
    public static List<Integer> order(List<Rect> monitors, List<Integer> requested, boolean playerOneOnPrimary) {
        List<Integer> order = new ArrayList<>();
        if (monitors == null || monitors.isEmpty()) {
            return order;
        }

        if (requested != null && !requested.isEmpty()) {
            for (Integer index : requested) {
                if (index != null && index >= 0 && index < monitors.size() && !order.contains(index)) {
                    order.add(index);
                }
            }
            if (!order.isEmpty()) {
                return order;
            }
        }

        // Reading order: rows of monitors from top to bottom, and within a row from left to right.
        // Monitors go in the same row when they overlap vertically, so monitors that stand side by
        // side at different heights still count left to right rather than one above the other.
        final List<Rect> bounds = monitors;
        List<Integer> byTop = new ArrayList<>();
        for (int i = 0; i < monitors.size(); i++) {
            byTop.add(i);
        }
        Collections.sort(byTop, new Comparator<Integer>() {
            @Override
            public int compare(Integer a, Integer b) {
                int byY = Integer.compare(bounds.get(a).y, bounds.get(b).y);
                return byY != 0 ? byY : Integer.compare(bounds.get(a).x, bounds.get(b).x);
            }
        });

        List<Integer> row = new ArrayList<>();
        int rowBottom = 0;
        for (Integer index : byTop) {
            if (!row.isEmpty() && monitors.get(index).y >= rowBottom) {
                addRow(order, row, monitors);
                row.clear();
            }
            rowBottom = row.isEmpty() ? monitors.get(index).bottom()
                : Math.min(rowBottom, monitors.get(index).bottom());
            row.add(index);
        }
        addRow(order, row, monitors);

        if (playerOneOnPrimary && order.size() > 1 && order.get(0) != 0) {
            order.remove(Integer.valueOf(0));
            order.add(0, 0);
        }
        return order;
    }

    private static void addRow(List<Integer> order, List<Integer> row, final List<Rect> monitors) {
        Collections.sort(row, new Comparator<Integer>() {
            @Override
            public int compare(Integer a, Integer b) {
                int byX = Integer.compare(monitors.get(a).x, monitors.get(b).x);
                return byX != 0 ? byX : Integer.compare(a, b);
            }
        });
        order.addAll(row);
    }

    /**
     * Works out the window to span and each player's rectangle inside it. Returns null when there
     * is nothing to do: fewer than two players, or fewer than two monitors to spread them over.
     */
    public static Plan build(List<Rect> monitors, List<Integer> order, int players) {
        if (monitors == null || order == null || players < 2 || players > Plan.MAX_PLAYERS || order.size() < 2) {
            return null;
        }

        int[] perMonitor = distribute(players, order.size());
        if (perMonitor.length < 2) {
            return null;
        }

        // The window has to cover every monitor in use. In a staggered layout that bounding box
        // includes desktop that no monitor shows; those parts of the window simply stay invisible,
        // which is how each player still gets the whole of their own screen.
        Rect window = monitors.get(order.get(0));
        for (int i = 1; i < perMonitor.length; i++) {
            Rect monitor = monitors.get(order.get(i));
            int left = Math.min(window.x, monitor.x);
            int top = Math.min(window.y, monitor.y);
            window = new Rect(left, top,
                Math.max(window.right(), monitor.right()) - left,
                Math.max(window.bottom(), monitor.bottom()) - top);
        }

        Rect[] slots = new Rect[Plan.MAX_PLAYERS];
        int[] monitorForSlot = new int[Plan.MAX_PLAYERS];
        for (int i = 0; i < Plan.MAX_PLAYERS; i++) {
            monitorForSlot[i] = -1;
        }

        List<Rect> used = new ArrayList<>();
        int player = 0;
        for (int i = 0; i < perMonitor.length; i++) {
            Rect monitor = monitors.get(order.get(i));
            used.add(monitor);
            for (int seat = 0; seat < perMonitor[i]; seat++) {
                slots[player] = relative(seat(seat, perMonitor[i], monitor), window);
                monitorForSlot[player] = order.get(i);
                player++;
            }
        }

        // With three players the game paints the fourth slot black, so that slot is parked on a
        // single pixel of the window that no monitor shows rather than on somebody's screen.
        Rect nowhere = unseen(window, used);
        for (int i = player; i < Plan.MAX_PLAYERS; i++) {
            slots[i] = nowhere;
        }

        return new Plan(window, slots, monitorForSlot, players);
    }

    /**
     * One pixel of the window that no monitor in use shows, for a player slot with nobody in it.
     * A staggered arrangement leaves such corners over; when none is free, the far corner of the
     * window is the least intrusive pixel there is.
     */
    static Rect unseen(Rect window, List<Rect> used) {
        int[][] corners = {
            { 0, 0 },
            { window.w - 1, 0 },
            { 0, window.h - 1 },
            { window.w - 1, window.h - 1 },
        };
        for (int[] corner : corners) {
            int x = window.x + corner[0];
            int y = window.y + corner[1];
            boolean seen = false;
            for (Rect monitor : used) {
                if (x >= monitor.x && x < monitor.right() && y >= monitor.y && y < monitor.bottom()) {
                    seen = true;
                    break;
                }
            }
            if (!seen) {
                return new Rect(corner[0], corner[1], 1, 1);
            }
        }
        return new Rect(Math.max(0, window.w - 1), Math.max(0, window.h - 1), 1, 1);
    }

    /**
     * The layout for a monitor chosen per player, which is what {@code PlayerMonitors} in the
     * settings gives: the first number is player one's monitor.
     *
     * <p>Players with no monitor of their own - because the setting names fewer monitors than there
     * are players, or names one that is not there - go where there is most room, filling the
     * monitors in {@code autoOrder} first. Players who end up on the same monitor share it, split
     * the way the game splits a whole screen.</p>
     *
     * @param wanted    the monitor for each player, 0-based, -1 where the player was not named
     * @param autoOrder the monitors in seating order, for players who were not named
     */
    public static Plan buildAssigned(List<Rect> monitors, int[] wanted, List<Integer> autoOrder, int players) {
        if (monitors == null || monitors.isEmpty() || wanted == null || players < 2 || players > Plan.MAX_PLAYERS) {
            return null;
        }

        int[] chosen = new int[players];
        int[] onMonitor = new int[monitors.size()];
        for (int i = 0; i < players; i++) {
            int monitor = i < wanted.length ? wanted[i] : -1;
            chosen[i] = monitor >= 0 && monitor < monitors.size() ? monitor : -1;
            if (chosen[i] >= 0) {
                onMonitor[chosen[i]]++;
            }
        }

        List<Integer> fallback = autoOrder == null || autoOrder.isEmpty() ? order(monitors, null, true) : autoOrder;
        for (int i = 0; i < players; i++) {
            if (chosen[i] >= 0) {
                continue;
            }
            int best = -1;
            for (Integer monitor : fallback) {
                if (monitor != null && monitor >= 0 && monitor < monitors.size()
                    && (best < 0 || onMonitor[monitor] < onMonitor[best])) {
                    best = monitor;
                }
            }
            if (best < 0) {
                return null;
            }
            chosen[i] = best;
            onMonitor[best]++;
        }

        // The window covers the monitors that ended up with somebody on them, and nothing else.
        Rect window = null;
        List<Rect> used = new ArrayList<>();
        for (int i = 0; i < players; i++) {
            Rect monitor = monitors.get(chosen[i]);
            if (used.contains(monitor)) {
                continue;
            }
            used.add(monitor);
            if (window == null) {
                window = monitor;
            } else {
                int left = Math.min(window.x, monitor.x);
                int top = Math.min(window.y, monitor.y);
                window = new Rect(left, top,
                    Math.max(window.right(), monitor.right()) - left,
                    Math.max(window.bottom(), monitor.bottom()) - top);
            }
        }

        Rect[] slots = new Rect[Plan.MAX_PLAYERS];
        int[] monitorForSlot = new int[Plan.MAX_PLAYERS];
        int[] seatTaken = new int[monitors.size()];
        for (int i = 0; i < Plan.MAX_PLAYERS; i++) {
            monitorForSlot[i] = -1;
        }
        for (int i = 0; i < players; i++) {
            int monitor = chosen[i];
            slots[i] = relative(seat(seatTaken[monitor]++, onMonitor[monitor], monitors.get(monitor)), window);
            monitorForSlot[i] = monitor;
        }

        Rect nowhere = unseen(window, used);
        for (int i = players; i < Plan.MAX_PLAYERS; i++) {
            slots[i] = nowhere;
        }
        return new Plan(window, slots, monitorForSlot, players);
    }

    private static Rect relative(Rect rect, Rect window) {
        return new Rect(rect.x - window.x, rect.y - window.y, rect.w, rect.h);
    }

    /** Keeps a value inside a run of pixels, one short of its far edge. */
    public static int clamp(int value, int min, int size) {
        int max = min + Math.max(0, size - 1);
        return value < min ? min : (value > max ? max : value);
    }
}
