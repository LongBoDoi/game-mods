package monitorcoop;

import java.lang.instrument.Instrumentation;

/**
 * Where the mod starts. The game is Java, so the mod is a Java agent: the launcher is told to load
 * this jar with {@code -javaagent}, which runs {@link #premain} before the game's own main method
 * and so before any of the classes it patches have been loaded.
 */
public final class Agent {
    /** Bumped with the mod, and printed into the log so a bug report says which build it is. */
    public static final String VERSION = "1.0.0";

    private Agent() {
    }

    public static void premain(String arguments, Instrumentation instrumentation) {
        start(instrumentation);
    }

    /** For completeness: attaching to a game that is already running works the same way. */
    public static void agentmain(String arguments, Instrumentation instrumentation) {
        start(instrumentation);
    }

    private static void start(Instrumentation instrumentation) {
        try {
            Log.info("MonitorCoop " + VERSION + " - a monitor each for local split-screen.");
            instrumentation.addTransformer(new Patch(), false);
        } catch (Throwable t) {
            Log.error("MonitorCoop could not start; the game carries on without it", t);
        }
    }
}
