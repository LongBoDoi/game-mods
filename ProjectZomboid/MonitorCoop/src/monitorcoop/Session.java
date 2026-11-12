package monitorcoop;

import java.io.File;
import java.util.ArrayList;
import java.util.List;
import org.lwjglx.input.Keyboard;
import org.lwjglx.input.Mouse;
import org.lwjglx.opengl.Display;
import zombie.ZomboidFileSystem;
import zombie.characters.IsoPlayer;
import zombie.core.Core;
import zombie.core.opengl.RenderThread;

/**
 * Decides when the monitor layout should be on, asks for the window it needs, and only hands the
 * layout to the game once the window really is that size - a rectangle that reaches past the
 * window would put a player's view somewhere nobody can see.
 *
 * <p>Runs once a frame from {@code GameWindow.frameStep}, which is cheap: it compares the player
 * count against the layout in use and looks at the monitors again only every half second.</p>
 */
public final class Session {
    /** How often to look at the settings file again. */
    private static final long RECHECK_MILLIS = 500L;

    /** How often to ask GLFW about the monitors again, which costs a hop to the render thread. */
    private static final long MONITOR_RECHECK_MILLIS = 2000L;

    /**
     * How often the window may be nudged back into place before the mod assumes something else
     * owns it and stops fighting over it.
     */
    private static final int MAX_CORRECTIONS = 10;

    private static Config config;
    private static boolean started;
    private static boolean offByKey;
    private static boolean toggleWasDown;
    private static boolean warnedAboutKey;

    private static List<Rect> monitors = new ArrayList<>();
    private static long lastRecheck;
    private static long lastMonitorCheck;

    /** The layout the window was asked for, which becomes live once the window matches it. */
    private static Plan asked;
    private static int corrections;
    private static boolean gaveUpCorrecting;

    private Session() {
    }

    /** Runs once a frame, from the game loop. Never throws: the game has to carry on regardless. */
    public static void tick() {
        try {
            step();
        } catch (Throwable t) {
            Log.error("Stopping the monitor layout after an error", t);
            try {
                clear();
            } catch (Throwable ignored) {
            }
        }
    }

    private static void step() {
        if (Patch.broken() != null) {
            // A patch the layout cannot do without did not go in; the game is on its own.
            if (asked != null) {
                clear();
            }
            return;
        }
        if (!Window.ready()) {
            return;
        }
        if (!started) {
            start();
        }

        long now = System.currentTimeMillis();
        if (now - lastRecheck >= RECHECK_MILLIS) {
            lastRecheck = now;
            config.reloadIfChanged();
        }
        if (now - lastMonitorCheck >= MONITOR_RECHECK_MILLIS) {
            lastMonitorCheck = now;
            // Asking GLFW has to happen on the thread that owns the window, so it is worth doing
            // rarely: monitors are plugged in and re-arranged, not moved around every frame.
            List<Rect> fresh = Window.monitors();
            if (!fresh.isEmpty() && !fresh.equals(monitors)) {
                boolean first = monitors.isEmpty();
                monitors = fresh;
                Log.info((first ? "Monitors: " : "The monitors changed: ") + describe(monitors));
                if (monitors.size() < 2) {
                    Log.info("One monitor, so split-screen stays in one window as the game intends.");
                }
            }
        }

        if (!Window.onScreen()) {
            // Minimised - which is what Windows does to a fullscreen game that loses focus - so
            // there is nothing to measure or move. The layout in use stays as it is, and the mod
            // gets a fresh go at the window once it is back.
            corrections = 0;
            gaveUpCorrecting = false;
            return;
        }

        checkToggleKey();
        Window.finishRestore();

        int players = IsoPlayer.numPlayers;
        if (config.pretendPlayers > 0) {
            players = Math.max(players, Math.min(config.pretendPlayers, Plan.MAX_PLAYERS));
        }

        Plan wanted = config.enabled && !offByKey ? wanted(players) : null;
        if (wanted == null) {
            if (asked != null) {
                Log.info(offByKey ? "Monitor layout off; back to the game's own split-screen."
                    : "Back to the game's own window.");
                clear();
            }
            return;
        }

        if (asked == null || !asked.matches(wanted)) {
            apply(wanted);
            return;
        }

        reassert();
    }

    private static void start() {
        started = true;
        // The patches happen while the game is starting, before the game has taken over the log,
        // so say what went in now that a line will end up in console.txt.
        Log.info("MonitorCoop " + Agent.VERSION + " patched " + Patch.summary() + ".");
        config = Config.load(settingsFile());
        Log.info("Settings: " + config.describe());
        // The monitors are read by the check below, which is due straight away.
    }

    private static File settingsFile() {
        String folder;
        try {
            folder = ZomboidFileSystem.instance.getCacheDir();
        } catch (Throwable t) {
            folder = System.getProperty("user.home") + File.separator + "Zomboid";
        }
        return new File(folder, "MonitorCoop.ini");
    }

    /** The layout for this many players, or null when there is nothing for the mod to do. */
    private static Plan wanted(int players) {
        if (players < 2 || players > Plan.MAX_PLAYERS) {
            return null;
        }

        if (!config.playerRects.isEmpty()) {
            return fromSettings(players);
        }
        if (monitors.isEmpty()) {
            return null;
        }

        List<Integer> order = Layout.order(monitors, null, config.playerOneOnPrimary);
        if (!config.playerMonitors.isEmpty()) {
            // A monitor chosen per player; one monitor for everybody is a choice too, so this one
            // does not need two of them.
            return Layout.buildAssigned(monitors, config.playerMonitorIndexes(), order, players);
        }
        if (monitors.size() < 2) {
            return null;
        }
        return Layout.build(monitors, order, players);
    }

    /** The layout from PlayerRects in the settings, which overrides everything worked out here. */
    private static Plan fromSettings(int players) {
        List<Rect> given = config.playerRects;
        if (given.size() < players) {
            return null;
        }

        Rect window = given.get(0);
        for (int i = 1; i < players; i++) {
            Rect rect = given.get(i);
            int left = Math.min(window.x, rect.x);
            int top = Math.min(window.y, rect.y);
            window = new Rect(left, top,
                Math.max(window.right(), rect.right()) - left,
                Math.max(window.bottom(), rect.bottom()) - top);
        }

        Rect nowhere = Layout.unseen(window, given.subList(0, players));
        Rect[] slots = new Rect[Plan.MAX_PLAYERS];
        int[] monitorForSlot = new int[Plan.MAX_PLAYERS];
        for (int i = 0; i < Plan.MAX_PLAYERS; i++) {
            if (i < players) {
                Rect rect = given.get(i);
                slots[i] = new Rect(rect.x - window.x, rect.y - window.y, rect.w, rect.h);
            } else {
                slots[i] = nowhere;
            }
            monitorForSlot[i] = -1;
        }
        return new Plan(window, slots, monitorForSlot, players);
    }

    private static void apply(Plan plan) {
        // The layout only goes live once the window is the size it was made for.
        Hooks.plan = null;
        asked = plan;
        corrections = 0;
        gaveUpCorrecting = false;
        Log.info("Spanning the monitors for " + plan.count + " players: " + plan.describe());
        Window.span(plan.window, config.fullMonitorView);
    }

    private static void clear() {
        Hooks.plan = null;
        asked = null;
        corrections = 0;
        gaveUpCorrecting = false;
        Window.restore(config == null || config.restoreWindowOnExit);
    }

    /**
     * Hands the layout to the game once the window matches it, and nudges the window back if
     * something moved it. The game applies a window size of its own whenever it changes the window
     * mode, so this is also what survives the player going through the display options.
     */
    private static void reassert() {
        Rect window = asked.window;
        Rect now = Window.current();
        boolean rightSize = now.w == window.w && now.h == window.h;
        boolean rightPlace = now.x == window.x && now.y == window.y;
        // The game copies the window size into its own at the top of each frame; the layout has to
        // wait for that, because every rectangle it hands out is measured against it.
        boolean gameKnows = Core.width == window.w && Core.height == window.h;

        if (rightSize && rightPlace && gameKnows) {
            corrections = 0;
            if (Hooks.plan != asked) {
                Hooks.plan = asked;
                Log.info("A monitor each: " + asked.describe());
            }
            return;
        }

        Hooks.plan = null;
        if (rightSize && rightPlace) {
            // Only the game's own screen size is behind, which the next frame fixes by itself.
            return;
        }
        if (gaveUpCorrecting) {
            return;
        }

        if (++corrections > MAX_CORRECTIONS) {
            gaveUpCorrecting = true;
            Log.warn("Something keeps moving the game window; leaving it alone."
                + " Press " + config.toggleKey + " twice to try again.");
            return;
        }
        Window.span(window, config.fullMonitorView);
    }

    private static void checkToggleKey() {
        int key = toggleKey();
        if (key <= 0) {
            return;
        }
        boolean down = Keyboard.isKeyDown(key) && Window.focused();
        if (down && !toggleWasDown) {
            offByKey = !offByKey;
            Log.info(offByKey ? "Monitor layout turned off." : "Monitor layout turned on.");
        }
        toggleWasDown = down;
    }

    private static int toggleKey() {
        String name = config.toggleKey == null ? "" : config.toggleKey.trim();
        if (name.isEmpty() || name.equalsIgnoreCase("none")) {
            return 0;
        }
        int key = Keyboard.getKeyIndex(name.toUpperCase());
        if (key <= 0) {
            if (!warnedAboutKey) {
                warnedAboutKey = true;
                Log.warn("ToggleKey '" + name + "' is not a key the game knows; there is no toggle key.");
            }
            return 0;
        }
        return key;
    }

    /**
     * Keeps the mouse pointer on player one's own monitor. Only player one has a mouse - that is
     * the game's rule, not the mod's - so this is about the pointer not wandering onto a screen
     * where it could click another player's windows. Losing focus (alt-tab) frees it.
     */
    static void confineMouse() {
        Plan live = Hooks.plan;
        if (live == null || config == null || !config.confineMouseToPlayerOne || !Window.focused()) {
            return;
        }
        // Moving the pointer is a window call, so leave it to the thread that owns the window; the
        // game draws the UI from there every frame, which is often enough to hold a pointer back.
        if (Thread.currentThread() != RenderThread.renderThread) {
            return;
        }
        Rect rect = live.slot(0);
        if (rect == null) {
            return;
        }

        int x = Mouse.getX();
        int y = Display.getHeight() - Mouse.getY();     // the wrapper measures from the bottom
        int clampedX = Layout.clamp(x, rect.x, rect.w);
        int clampedY = Layout.clamp(y, rect.y, rect.h);
        if (clampedX != x || clampedY != y) {
            Mouse.setCursorPosition(clampedX, clampedY);
        }
    }

    /** Whether to outline each player's area, for working out where a layout went wrong. */
    static boolean showPlayerAreas() {
        return config != null && config.showPlayerAreas;
    }

    private static String describe(List<Rect> list) {
        if (list.isEmpty()) {
            return "none yet";
        }
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < list.size(); i++) {
            if (i > 0) {
                text.append("; ");
            }
            text.append(i + 1).append(": ").append(list.get(i));
            if (i == 0) {
                text.append(" (primary)");
            }
        }
        return text.toString();
    }
}
