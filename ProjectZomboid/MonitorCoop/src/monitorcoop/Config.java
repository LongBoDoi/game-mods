package monitorcoop;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * MonitorCoop.ini, next to the game's own settings in the Zomboid folder, written the first time
 * the mod runs. It is re-read while the game is running whenever the file changes on disk, so a
 * setting can be tried out without restarting.
 */
public final class Config {
    /** Whether to spread split-screen players over the monitors at all. */
    public boolean enabled = true;

    /** Turns the monitor layout off and on while the game is running. A key name, or None. */
    public String toggleKey = "F7";

    /**
     * Which monitor each player gets, in player order: the first number is player one's monitor.
     * The numbers are the ones the mod lists in the log, 1 being the primary monitor. Empty works
     * it out from the desktop arrangement.
     *
     * <p>Two players given the same monitor share it, split the way the game splits a whole
     * screen. A player the setting does not name goes where there is most room.</p>
     */
    public List<Integer> playerMonitors = new ArrayList<>();

    /**
     * With {@link #playerMonitors} empty, seat player one on the primary monitor and the others
     * from left to right. Off seats everyone purely left to right, top row first.
     */
    public boolean playerOneOnPrimary = true;

    /** Put the window back the way it was once the extra players leave. */
    public boolean restoreWindowOnExit = true;

    /**
     * Show each player as much of the world as they would see playing on that monitor alone.
     *
     * <p>The game holds the view it had at the size the window was when the session started: make
     * the window bigger and the extra pixels go into sharpness rather than showing more world.
     * Spanning the monitors would then leave every player looking at about as much of the world as
     * the half window showed them. Telling the game that the spanned window is the size it started
     * at undoes that; off keeps the game's own behaviour, which is lighter on the graphics card.</p>
     */
    public boolean fullMonitorView = true;

    /**
     * Hold the mouse pointer inside player one's own monitor while the game has focus, so it
     * cannot wander onto another player's screen. The keyboard and mouse belong to player one
     * either way: that is the game's own rule, not something this mod changes.
     */
    public boolean confineMouseToPlayerOne = true;

    /**
     * Act as if this many players were in split-screen, to try a seating arrangement on your own:
     * the window spans and player one lands on their monitor while the other screens stay empty.
     * 0 uses the real number of players, which is what you want for playing.
     */
    public int pretendPlayers;

    /**
     * Draw a frame around each player's area in their own colour, over everything else. For
     * working out where a layout went wrong: the frame is exactly the rectangle the mod handed the
     * game, so a screenshot says whether that rectangle and the monitor agree.
     */
    public boolean showPlayerAreas;

    /**
     * Rectangles to use instead of the ones worked out from the monitors, as
     * {@code x,y,w,h; x,y,w,h} in desktop pixels, one per player. The escape hatch for a desktop
     * the mod reads wrongly; empty means work them out.
     */
    public List<Rect> playerRects = new ArrayList<>();

    private File file;
    private long stamp;

    /** Reads the settings, writing the file with the defaults in it when there is none yet. */
    public static Config load(File file) {
        Config config = new Config();
        config.file = file;
        try {
            if (!file.exists()) {
                File parent = file.getParentFile();
                if (parent != null) {
                    parent.mkdirs();
                }
                Files.write(file.toPath(), template().getBytes(StandardCharsets.UTF_8));
                Log.info("Wrote " + file);
            } else {
                config.read(file);
            }
            config.stamp = file.lastModified();
        } catch (Exception e) {
            Log.error("Could not read " + file + "; using the defaults", e);
        }
        return config;
    }

    /** True when the file changed on disk since it was last read, and re-reads it if so. */
    public boolean reloadIfChanged() {
        if (this.file == null) {
            return false;
        }
        long now = this.file.lastModified();
        if (now == this.stamp) {
            return false;
        }
        this.stamp = now;
        try {
            Config fresh = new Config();
            fresh.read(this.file);
            this.enabled = fresh.enabled;
            this.toggleKey = fresh.toggleKey;
            this.playerMonitors = fresh.playerMonitors;
            this.playerOneOnPrimary = fresh.playerOneOnPrimary;
            this.restoreWindowOnExit = fresh.restoreWindowOnExit;
            this.fullMonitorView = fresh.fullMonitorView;
            this.confineMouseToPlayerOne = fresh.confineMouseToPlayerOne;
            this.pretendPlayers = fresh.pretendPlayers;
            this.showPlayerAreas = fresh.showPlayerAreas;
            this.playerRects = fresh.playerRects;
            Log.info("Re-read " + this.file.getName() + ": " + this.describe());
            return true;
        } catch (Exception e) {
            Log.error("Could not re-read " + this.file, e);
            return false;
        }
    }

    /**
     * The monitor for each player as the layout wants it: 0-based, and -1 for a player the
     * settings do not name (or name with a monitor number that is not a monitor).
     */
    public int[] playerMonitorIndexes() {
        int[] indexes = new int[this.playerMonitors.size()];
        for (int i = 0; i < indexes.length; i++) {
            Integer number = this.playerMonitors.get(i);
            indexes[i] = number != null && number >= 1 ? number - 1 : -1;
        }
        return indexes;
    }

    public String describe() {
        StringBuilder text = new StringBuilder();
        text.append("Enabled=").append(this.enabled)
            .append(" ToggleKey=").append(this.toggleKey)
            .append(" PlayerMonitors=").append(this.playerMonitors.isEmpty() ? "auto" : this.playerMonitors)
            .append(" PlayerOneOnPrimary=").append(this.playerOneOnPrimary)
            .append(" RestoreWindowOnExit=").append(this.restoreWindowOnExit)
            .append(" FullMonitorView=").append(this.fullMonitorView)
            .append(" ConfineMouseToPlayerOne=").append(this.confineMouseToPlayerOne);
        if (this.pretendPlayers > 0) {
            text.append(" PretendPlayers=").append(this.pretendPlayers);
        }
        if (this.showPlayerAreas) {
            text.append(" ShowPlayerAreas=true");
        }
        if (!this.playerRects.isEmpty()) {
            text.append(" PlayerRects=").append(this.playerRects);
        }
        return text.toString();
    }

    private void read(File from) throws IOException {
        Map<String, String> values = new HashMap<>();
        boolean first = true;
        for (String line : Files.readAllLines(from.toPath(), StandardCharsets.UTF_8)) {
            // Notepad writes a byte order mark at the top of a UTF-8 file, which would otherwise
            // make the first setting unreadable.
            if (first) {
                first = false;
                if (line.startsWith("﻿")) {
                    line = line.substring(1);
                }
            }
            String text = line.trim();
            if (text.isEmpty() || text.startsWith("#") || text.startsWith(";")) {
                continue;
            }
            int split = text.indexOf('=');
            if (split <= 0) {
                continue;
            }
            values.put(text.substring(0, split).trim().toLowerCase(), text.substring(split + 1).trim());
        }

        this.enabled = bool(values, "enabled", this.enabled);
        this.toggleKey = string(values, "togglekey", this.toggleKey);
        // Monitors= was what this setting was called before it became one monitor per player.
        this.playerMonitors = numbers(values.containsKey("playermonitors")
            ? values.get("playermonitors") : values.get("monitors"));
        this.playerOneOnPrimary = bool(values, "playeroneonprimary", this.playerOneOnPrimary);
        this.restoreWindowOnExit = bool(values, "restorewindowonexit", this.restoreWindowOnExit);
        this.fullMonitorView = bool(values, "fullmonitorview", this.fullMonitorView);
        this.confineMouseToPlayerOne = bool(values, "confinemousetoplayerone", this.confineMouseToPlayerOne);
        this.pretendPlayers = number(values, "pretendplayers", this.pretendPlayers);
        this.showPlayerAreas = bool(values, "showplayerareas", this.showPlayerAreas);
        this.playerRects = rects(values.get("playerrects"));
    }

    private static boolean bool(Map<String, String> values, String key, boolean fallback) {
        String value = values.get(key);
        if (value == null || value.isEmpty()) {
            return fallback;
        }
        return value.equalsIgnoreCase("true") || value.equals("1") || value.equalsIgnoreCase("yes");
    }

    private static int number(Map<String, String> values, String key, int fallback) {
        String value = values.get(key);
        if (value == null || value.isEmpty()) {
            return fallback;
        }
        try {
            return Integer.parseInt(value.trim());
        } catch (NumberFormatException e) {
            Log.warn(key + ": '" + value + "' is not a number; using " + fallback + ".");
            return fallback;
        }
    }

    private static String string(Map<String, String> values, String key, String fallback) {
        String value = values.get(key);
        return value == null || value.isEmpty() ? fallback : value;
    }

    private static List<Integer> numbers(String value) {
        List<Integer> numbers = new ArrayList<>();
        if (value == null) {
            return numbers;
        }
        for (String part : value.split("[,;\\s]+")) {
            if (part.isEmpty()) {
                continue;
            }
            try {
                numbers.add(Integer.valueOf(Integer.parseInt(part.trim())));
            } catch (NumberFormatException e) {
                Log.warn("PlayerMonitors: '" + part + "' is not a monitor number; ignoring it.");
                numbers.add(Integer.valueOf(-1));
            }
        }
        return numbers;
    }

    private static List<Rect> rects(String value) {
        List<Rect> rects = new ArrayList<>();
        if (value == null || value.trim().isEmpty()) {
            return rects;
        }
        for (String part : value.split(";")) {
            if (part.trim().isEmpty()) {
                continue;
            }
            String[] bits = part.trim().split("[,\\s]+");
            if (bits.length != 4) {
                Log.warn("PlayerRects: '" + part.trim() + "' is not x,y,w,h; ignoring the setting.");
                return new ArrayList<>();
            }
            try {
                rects.add(new Rect(Integer.parseInt(bits[0]), Integer.parseInt(bits[1]),
                    Integer.parseInt(bits[2]), Integer.parseInt(bits[3])));
            } catch (NumberFormatException e) {
                Log.warn("PlayerRects: '" + part.trim() + "' is not four numbers; ignoring the setting.");
                return new ArrayList<>();
            }
        }
        if (rects.size() < 2) {
            Log.warn("PlayerRects needs a rectangle for at least two players; ignoring the setting.");
            return new ArrayList<>();
        }
        return rects;
    }

    private static String template() {
        StringBuilder text = new StringBuilder();
        text.append("# MonitorCoop settings. The mod re-reads this file while the game is running,\n");
        text.append("# so a change takes effect within a second or two of saving it.\n");
        text.append("\n");
        text.append("# false leaves local split-screen exactly as the game ships it.\n");
        text.append("Enabled=true\n");
        text.append("\n");
        text.append("# Turns the monitor layout off and on in game. A key name the game knows\n");
        text.append("# (F1..F12, A..Z, 0..9, HOME, END...), or None for no key at all.\n");
        text.append("ToggleKey=F7\n");
        text.append("\n");
        text.append("# Which monitor each player gets, in player order: the first number is player\n");
        text.append("# one's. The numbers are the ones the mod lists in console.txt when the game\n");
        text.append("# starts (1 is your primary monitor), for example:\n");
        text.append("#   PlayerMonitors=2,1     player one on monitor 2, player two on monitor 1\n");
        text.append("#   PlayerMonitors=1,1,2   players one and two share monitor 1, player three\n");
        text.append("#                          gets monitor 2 to themselves\n");
        text.append("# Empty works it out from the way the monitors are arranged on the desktop.\n");
        text.append("# Saving a change here moves the players within a second or two, in game.\n");
        text.append("PlayerMonitors=\n");
        text.append("\n");
        text.append("# Only used when PlayerMonitors is empty: player one gets the primary monitor\n");
        text.append("# and the others follow left to right. false seats everyone purely left to\n");
        text.append("# right, top row first.\n");
        text.append("PlayerOneOnPrimary=true\n");
        text.append("\n");
        text.append("# Put the window back (size, position, fullscreen) when the extra players leave.\n");
        text.append("RestoreWindowOnExit=true\n");
        text.append("\n");
        text.append("# Show each player as much of the world as they would see playing on that monitor\n");
        text.append("# alone. The game otherwise keeps the view it had at the window size the session\n");
        text.append("# started at, which would leave everyone looking at about as much world as the\n");
        text.append("# half window showed. false keeps that, and is lighter on the graphics card.\n");
        text.append("FullMonitorView=true\n");
        text.append("\n");
        text.append("# Hold the mouse pointer inside player one's monitor while the game has focus, so\n");
        text.append("# it cannot wander onto another player's screen. Alt-tab always frees it.\n");
        text.append("ConfineMouseToPlayerOne=true\n");
        text.append("\n");
        text.append("# Act as if this many players were in split-screen, so you can try a seating\n");
        text.append("# arrangement on your own: the window spans and player one lands on their own\n");
        text.append("# monitor, with the other screens left empty. Put it back to 0 to play.\n");
        text.append("PretendPlayers=0\n");
        text.append("\n");
        text.append("# Draw a frame around each player's area in their own colour (player one red,\n");
        text.append("# two green, three blue, four yellow), with that many little marks inside the\n");
        text.append("# top left corner. For working out where a layout went wrong: the frame is\n");
        text.append("# exactly the rectangle the mod gave the game, so a screenshot says whether the\n");
        text.append("# mod and your monitors agree.\n");
        text.append("ShowPlayerAreas=false\n");
        text.append("\n");
        text.append("# The escape hatch: exact rectangles in desktop pixels, one per player, instead of\n");
        text.append("# the ones worked out from the monitors. For example, for two players:\n");
        text.append("#   PlayerRects=0,0,2560,1440; -1920,356,1920,1080\n");
        text.append("PlayerRects=\n");
        return text.toString();
    }
}
