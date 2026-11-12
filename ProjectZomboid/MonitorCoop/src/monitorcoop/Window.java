package monitorcoop;

import java.lang.reflect.Field;
import java.util.ArrayList;
import java.util.List;
import org.lwjgl.PointerBuffer;
import org.lwjgl.glfw.GLFW;
import org.lwjgl.glfw.GLFWVidMode;
import org.lwjglx.opengl.Display;
import org.lwjglx.opengl.DisplayMode;
import zombie.core.Core;
import zombie.core.opengl.RenderThread;

/**
 * The game window: what the monitors look like, and how to open one borderless window across them
 * and put it back again.
 *
 * <p>The game draws every split-screen player into one window, so a player can only have a whole
 * monitor if the window covers them all. The window itself is GLFW's, reached the way the game
 * reaches it, and resized on the thread that owns the OpenGL context because that is where the
 * game does its own window changes. Everything downstream follows by itself: GLFW reports the new
 * size, and the game copies that into its own screen size at the top of every frame.</p>
 */
public final class Window {
    private static boolean saved;
    private static boolean savedFullscreen;
    private static long savedMonitor;
    private static boolean savedBorderless;
    private static int savedX;
    private static int savedY;
    private static int savedWidth;
    private static int savedHeight;
    private static float savedInitialWidth;
    private static float savedInitialHeight;

    private static boolean warnedAboutDisplayMode;

    /** How long, and how many tries, to spend making sure the window really did go back. */
    private static final long PUT_BACK_MILLIS = 2000L;
    private static final int PUT_BACK_ATTEMPTS = 5;

    private static Rect putBack;
    private static boolean putBackFullscreen;
    private static long putBackMonitor;
    private static boolean putBackBorderless;
    private static int putBackAttempts;
    private static long putBackUntil;

    private Window() {
    }

    /** Whether the window exists yet; nothing here is worth trying before it does. */
    public static boolean ready() {
        return Display.isCreated() && Display.getWindow() != 0L;
    }

    /** Where the window is now, in desktop pixels. */
    public static Rect current() {
        return new Rect(Display.getX(), Display.getY(), Display.getWidth(), Display.getHeight());
    }

    public static boolean focused() {
        return Display.isActive();
    }

    /**
     * Whether the window is on screen at all. A minimised window - which is what Windows does to a
     * fullscreen game that loses focus - reports a position far off the desktop, and moving or
     * measuring it then is meaningless: the mod leaves it alone until it comes back.
     */
    public static boolean onScreen() {
        return Display.isVisible() && Display.getX() > -30000 && Display.getY() > -30000
            && Display.getWidth() > 0 && Display.getHeight() > 0;
    }

    /**
     * Every monitor, in desktop pixels, indexed the way GLFW lists them: the primary monitor is
     * always first. Returns an empty list when GLFW has nothing to say yet.
     */
    public static List<Rect> monitors() {
        final List<Rect> monitors = new ArrayList<>();
        onRenderThread(new Runnable() {
            @Override
            public void run() {
                PointerBuffer handles = GLFW.glfwGetMonitors();
                if (handles == null) {
                    return;
                }
                int[] x = new int[1];
                int[] y = new int[1];
                for (int i = 0; i < handles.limit(); i++) {
                    long handle = handles.get(i);
                    GLFWVidMode mode = GLFW.glfwGetVideoMode(handle);
                    if (mode == null) {
                        continue;
                    }
                    GLFW.glfwGetMonitorPos(handle, x, y);
                    monitors.add(new Rect(x[0], y[0], mode.width(), mode.height()));
                }
            }
        });
        return monitors;
    }

    /**
     * Opens the window over the rectangle given, without a border and whatever window mode the
     * game was in before. Remembers that mode the first time, for {@link #restore()}.
     */
    public static void span(final Rect target, final boolean fullMonitorView) {
        if (!ready()) {
            return;
        }
        // Spanning again settles any argument about where the window was going back to.
        putBack = null;
        if (!saved) {
            save();
        }

        onRenderThread(new Runnable() {
            @Override
            public void run() {
                long window = Display.getWindow();
                // Borderless first: a window with a border would have to fit its frame in as well,
                // and a fullscreen window belongs to a single monitor, which the move below undoes.
                Display.setBorderlessWindow(true);
                GLFW.glfwSetWindowMonitor(window, 0L, target.x, target.y, target.w, target.h, GLFW.GLFW_DONT_CARE);
                GLFW.glfwSetWindowPos(window, target.x, target.y);
                tellDisplay(target.w, target.h);
            }
        });

        if (fullMonitorView) {
            // The game scales the view by the window size it started at over the window size now,
            // so a bigger window shows the same world more sharply instead of more world. Saying
            // the spanned window is the size it started at is what gives each player the view they
            // would have playing on that monitor alone.
            Core.initialWidth = target.w;
            Core.initialHeight = target.h;
        }
    }

    /** Puts the window back the way it was before the first {@link #span}. */
    public static void restore(boolean putWindowBack) {
        if (!saved) {
            return;
        }

        Core.initialWidth = savedInitialWidth;
        Core.initialHeight = savedInitialHeight;
        saved = false;

        if (!putWindowBack || !ready()) {
            putBack = null;
            return;
        }

        putBack = new Rect(savedX, savedY, savedWidth, savedHeight);
        putBackFullscreen = savedFullscreen;
        putBackMonitor = savedMonitor;
        putBackBorderless = savedBorderless;
        putBackAttempts = 0;
        putBackUntil = System.currentTimeMillis() + PUT_BACK_MILLIS;
        apply(putBack, putBackFullscreen, putBackMonitor, putBackBorderless);
        Log.info("Window back to " + describe(current(), Display.isFullscreen()) + ".");
    }

    /**
     * Puts the window back again if it did not stay put. The game sets a window size of its own
     * whenever it changes the window mode, and the mod is not the only thing allowed to touch the
     * window, so for a couple of seconds after handing it back the geometry is checked and set
     * again - a few times at most, then left alone.
     */
    public static void finishRestore() {
        Rect target = putBack;
        if (target == null) {
            return;
        }
        if (System.currentTimeMillis() > putBackUntil || putBackAttempts >= PUT_BACK_ATTEMPTS) {
            Rect now = current();
            if (!now.equals(target)) {
                Log.warn("The window did not go back to " + describe(target, putBackFullscreen)
                    + "; it is " + describe(now, Display.isFullscreen()) + " and the mod is leaving it there.");
            }
            putBack = null;
            return;
        }

        Rect now = current();
        if (now.equals(target) && Display.isFullscreen() == putBackFullscreen) {
            putBack = null;
            return;
        }
        putBackAttempts++;
        apply(target, putBackFullscreen, putBackMonitor, putBackBorderless);
    }

    /** Puts the window where it is told, in the window mode it is told. */
    private static void apply(final Rect target, final boolean fullscreen, final long monitor,
                              final boolean borderless) {
        onRenderThread(new Runnable() {
            @Override
            public void run() {
                long window = Display.getWindow();
                Display.setBorderlessWindow(borderless);
                // Windowed first, even when the window is to end up fullscreen again: asking for
                // the monitor and the geometry in one call leaves the window on whichever monitor
                // the driver felt like, which is how it used to come back on the wrong screen.
                GLFW.glfwSetWindowMonitor(window, 0L, target.x, target.y, target.w, target.h, GLFW.GLFW_DONT_CARE);
                GLFW.glfwSetWindowPos(window, target.x, target.y);
                GLFW.glfwSetWindowSize(window, target.w, target.h);
                if (fullscreen && monitor != 0L) {
                    GLFW.glfwSetWindowMonitor(window, monitor, target.x, target.y, target.w, target.h,
                        GLFW.GLFW_DONT_CARE);
                }
                tellDisplay(target.w, target.h);
            }
        });
    }

    private static String describe(Rect rect, boolean fullscreen) {
        return rect + (fullscreen ? ", fullscreen" : ", windowed");
    }

    /** True while there is a window state to put back. */
    public static boolean hasSavedWindow() {
        return saved;
    }

    private static void save() {
        savedFullscreen = Display.isFullscreen();
        savedBorderless = Display.isBorderlessWindow();
        savedX = Display.getX();
        savedY = Display.getY();
        savedWidth = Display.getWidth();
        savedHeight = Display.getHeight();
        savedInitialWidth = Core.initialWidth;
        savedInitialHeight = Core.initialHeight;
        savedMonitor = 0L;
        onRenderThread(new Runnable() {
            @Override
            public void run() {
                long handle = GLFW.glfwGetWindowMonitor(Display.getWindow());
                savedMonitor = handle != 0L ? handle : GLFW.glfwGetPrimaryMonitor();
            }
        });
        saved = true;
        Log.info("Window before: " + new Rect(savedX, savedY, savedWidth, savedHeight)
            + (savedFullscreen ? ", fullscreen" : savedBorderless ? ", borderless" : ", windowed"));
    }

    /**
     * Keeps the size the game's own window wrapper believes in. It is only read when the game
     * changes the window mode itself, but a stale value there would make the game skip a change it
     * needs to make, and the mod is not the only thing allowed to touch the window.
     */
    private static void tellDisplay(int width, int height) {
        try {
            Field field = Display.class.getDeclaredField("gameWindowMode");
            field.setAccessible(true);
            field.set(null, new DisplayMode(width, height));
        } catch (Throwable t) {
            if (!warnedAboutDisplayMode) {
                warnedAboutDisplayMode = true;
                Log.warn("Could not update the window wrapper's own size (" + t + "); the layout still works.");
            }
        }
    }

    /**
     * Runs something on the thread that owns the OpenGL context, which is where the game makes its
     * own window changes. Already being on that thread is handled by the game's own helper.
     */
    private static void onRenderThread(final Runnable work) {
        try {
            RenderThread.invokeQueryOnRenderContext(() -> {
                work.run();
                return true;
            });
        } catch (Throwable t) {
            Log.error("A window change did not go through", t);
        }
    }
}
