package monitorcoop;

/**
 * Everything the mod says, tagged so it stands out in the game log. The game writes stdout to
 * {@code <user>\Zomboid\console.txt}, so these lines end up in the log players already send when
 * something is wrong.
 */
public final class Log {
    private static final String TAG = "[MonitorCoop] ";

    private Log() {
    }

    public static void info(String message) {
        System.out.println(TAG + message);
    }

    public static void warn(String message) {
        System.out.println(TAG + "WARN: " + message);
    }

    public static void error(String message) {
        System.out.println(TAG + "ERROR: " + message);
        System.err.println(TAG + "ERROR: " + message);
    }

    public static void error(String message, Throwable thrown) {
        error(message + ": " + thrown);
        thrown.printStackTrace(System.out);
    }
}
