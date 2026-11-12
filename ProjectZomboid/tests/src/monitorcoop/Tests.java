package monitorcoop;

import java.io.IOException;
import java.io.InputStream;
import java.lang.classfile.ClassFile;
import java.lang.classfile.ClassModel;
import java.lang.classfile.CodeElement;
import java.lang.classfile.CodeModel;
import java.lang.classfile.MethodModel;
import java.lang.classfile.instruction.InvokeInstruction;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.jar.JarFile;
import java.util.zip.ZipEntry;

/**
 * The offline checks for MonitorCoop: the layout maths with no game involved, and - against the
 * installed projectzomboid.jar - that every patch still lands where the mod expects it to.
 *
 * <p>Run it with tests\run.cmd. What neither half can check is the picture on the monitors, which
 * needs a real split-screen session; MonitorCoop\README.md has that checklist.</p>
 */
public final class Tests {
    private static int checks;
    private static final List<String> failures = new ArrayList<>();

    public static void main(String[] args) throws Exception {
        layout();

        String gameDir = args.length > 0 ? args[0] : null;
        if (gameDir == null || gameDir.isEmpty()) {
            System.out.println("No game folder given, so the patches were not checked against the game.");
        } else {
            patches(Path.of(gameDir, "projectzomboid.jar"));
        }

        System.out.println();
        if (failures.isEmpty()) {
            System.out.println("All " + checks + " checks passed.");
            return;
        }
        System.out.println(failures.size() + " of " + checks + " checks FAILED:");
        for (String failure : failures) {
            System.out.println("  - " + failure);
        }
        System.exit(1);
    }

    // --- The layout maths ----------------------------------------------------------------------

    private static void layout() {
        System.out.println("The layout maths");

        // Spreading players over monitors: the first monitors fill up first.
        is("two on two", "1,1", join(Layout.distribute(2, 2)));
        is("three on two", "2,1", join(Layout.distribute(3, 2)));
        is("four on two", "2,2", join(Layout.distribute(4, 2)));
        is("four on three", "2,1,1", join(Layout.distribute(4, 3)));
        is("two on three", "1,1", join(Layout.distribute(2, 3)));
        is("nobody", "", join(Layout.distribute(0, 2)));

        // One seat is the whole monitor; two meet in the middle with no seam, odd widths included.
        Rect monitor = new Rect(100, 50, 1921, 1081);
        is("one seat", "1921x1081 at 100,50", Layout.seat(0, 1, monitor).toString());
        is("left seat", "960x1081 at 100,50", Layout.seat(0, 2, monitor).toString());
        is("right seat", "961x1081 at 1060,50", Layout.seat(1, 2, monitor).toString());
        is("seats meet", Layout.seat(0, 2, monitor).right(), Layout.seat(1, 2, monitor).x);
        is("top left of four", "960x540 at 100,50", Layout.seat(0, 4, monitor).toString());
        is("bottom right of four", "961x541 at 1060,590", Layout.seat(3, 4, monitor).toString());
        is("quarters fill the monitor", monitor.bottom(), Layout.seat(3, 4, monitor).bottom());

        // Seating order: the primary monitor first, then left to right, top row first.
        List<Rect> sideBySide = Arrays.asList(
            new Rect(0, 0, 2560, 1440),          // primary
            new Rect(-1920, 356, 1920, 1080));   // to the left of it, and lower down
        is("primary first", "[0, 1]", Layout.order(sideBySide, none(), true).toString());
        is("purely left to right", "[1, 0]", Layout.order(sideBySide, none(), false).toString());
        is("the monitors asked for", "[1, 0]", Layout.order(sideBySide, Arrays.asList(1, 0), true).toString());
        is("nonsense monitor numbers are ignored",
            "[0, 1]", Layout.order(sideBySide, Arrays.asList(7, 9), true).toString());

        List<Rect> stacked = Arrays.asList(
            new Rect(0, 1080, 1920, 1080),       // primary, underneath
            new Rect(0, 0, 1920, 1080));
        is("rows top to bottom", "[0, 1]", Layout.order(stacked, none(), true).toString());
        is("top row first", "[1, 0]", Layout.order(stacked, none(), false).toString());

        List<Rect> grid = Arrays.asList(
            new Rect(1920, 0, 1920, 1080),       // primary, top right
            new Rect(0, 0, 1920, 1080),
            new Rect(0, 1080, 1920, 1080),
            new Rect(1920, 1080, 1920, 1080));
        is("a two by two grid", "[0, 1, 2, 3]", Layout.order(grid, none(), true).toString());
        is("reading order", "[1, 0, 2, 3]", Layout.order(grid, none(), false).toString());

        // A real desktop: a 1440p primary with a 1080p monitor to its left, lower down.
        Plan two = Layout.build(sideBySide, Layout.order(sideBySide, none(), true), 2);
        is("the window covers both", "4480x1440 at -1920,0", two.window.toString());
        is("player one has the primary monitor", "2560x1440 at 1920,0", two.slots[0].toString());
        is("player two has the other one", "1920x1080 at 0,356", two.slots[1].toString());
        is("the empty slots are out of sight", "1x1 at 0,0", two.slots[3].toString());
        is("who sits where", "window 4480x1440 at -1920,0; P1 on monitor 1 -> 2560x1440 at 1920,0;"
            + " P2 on monitor 2 -> 1920x1080 at 0,356", two.describe());

        // Three players on two monitors: two share the primary, split as the game splits a screen.
        Plan three = Layout.build(sideBySide, Layout.order(sideBySide, none(), true), 3);
        is("three, player one", "1280x1440 at 1920,0", three.slots[0].toString());
        is("three, player two", "1280x1440 at 3200,0", three.slots[1].toString());
        is("three, player three", "1920x1080 at 0,356", three.slots[2].toString());
        is("three, nobody in slot four", "1x1 at 0,0", three.slots[3].toString());
        is("three players counted", 3, three.count);

        // Four players on two monitors: two on each.
        Plan four = Layout.build(sideBySide, Layout.order(sideBySide, none(), true), 4);
        is("four, player two", "1280x1440 at 3200,0", four.slots[1].toString());
        is("four, player three", "960x1080 at 0,356", four.slots[2].toString());
        is("four, player four", "960x1080 at 960,356", four.slots[3].toString());

        // Four monitors, one player each.
        Plan grid4 = Layout.build(grid, Layout.order(grid, none(), true), 4);
        is("one each on four monitors", "3840x2160 at 0,0", grid4.window.toString());
        is("grid, player one", "1920x1080 at 1920,0", grid4.slots[0].toString());
        is("grid, player four", "1920x1080 at 1920,1080", grid4.slots[3].toString());

        // Nothing to do: one player, one monitor, or more players than the game has slots.
        isNull("one player", Layout.build(sideBySide, Layout.order(sideBySide, none(), true), 1));
        isNull("one monitor", Layout.build(Collections.singletonList(new Rect(0, 0, 1920, 1080)),
            Layout.order(Collections.singletonList(new Rect(0, 0, 1920, 1080)), none(), true), 2));
        isNull("five players", Layout.build(sideBySide, Layout.order(sideBySide, none(), true), 5));
        isNull("one monitor asked for",
            Layout.build(sideBySide, Layout.order(sideBySide, Collections.singletonList(1), true), 2));

        // A monitor chosen per player: PlayerMonitors=2,1 swaps who sits where.
        List<Integer> auto = Layout.order(sideBySide, none(), true);
        Plan swapped = Layout.buildAssigned(sideBySide, new int[] { 1, 0 }, auto, 2);
        is("swapped, player one", "1920x1080 at 0,356", swapped.slots[0].toString());
        is("swapped, player two", "2560x1440 at 1920,0", swapped.slots[1].toString());
        is("swapped, the window is the same", "4480x1440 at -1920,0", swapped.window.toString());
        is("swapped, who sits where", "window 4480x1440 at -1920,0; P1 on monitor 2 -> 1920x1080 at 0,356;"
            + " P2 on monitor 1 -> 2560x1440 at 1920,0", swapped.describe());

        // Two players told to share one monitor: the window is that monitor, split in half.
        Plan shared = Layout.buildAssigned(sideBySide, new int[] { 0, 0 }, auto, 2);
        is("shared, the window is one monitor", "2560x1440 at 0,0", shared.window.toString());
        is("shared, left half", "1280x1440 at 0,0", shared.slots[0].toString());
        is("shared, right half", "1280x1440 at 1280,0", shared.slots[1].toString());

        // Three players, two of them sharing a monitor on purpose.
        Plan mixed = Layout.buildAssigned(sideBySide, new int[] { 1, 1, 0 }, auto, 3);
        is("mixed, player one", "960x1080 at 0,356", mixed.slots[0].toString());
        is("mixed, player two", "960x1080 at 960,356", mixed.slots[1].toString());
        is("mixed, player three", "2560x1440 at 1920,0", mixed.slots[2].toString());

        // A player the setting does not name goes where there is most room.
        Plan partial = Layout.buildAssigned(sideBySide, new int[] { 1 }, auto, 2);
        is("unnamed player takes the free monitor", "2560x1440 at 1920,0", partial.slots[1].toString());
        is("named player keeps theirs", "1920x1080 at 0,356", partial.slots[0].toString());

        // A monitor number that is not a monitor is treated as "not named", and the players after
        // it keep their own place in the list.
        Plan bad = Layout.buildAssigned(sideBySide, new int[] { 7, 1 }, auto, 2);
        is("nonsense monitor, player two still named", "1920x1080 at 0,356", bad.slots[1].toString());
        is("nonsense monitor, player one put somewhere", "2560x1440 at 1920,0", bad.slots[0].toString());

        isNull("a monitor each for one player", Layout.buildAssigned(sideBySide, new int[] { 1, 0 }, auto, 1));
        isNull("no monitors at all", Layout.buildAssigned(new ArrayList<Rect>(), new int[] { 0, 0 }, auto, 2));

        // Monitors that touch leave no corner out of sight, so the empty slot takes the far pixel.
        List<Rect> flush = Arrays.asList(new Rect(0, 0, 1920, 1080), new Rect(1920, 0, 1920, 1080));
        is("no corner to hide in", "1x1 at 3839,1079",
            Layout.build(flush, Layout.order(flush, none(), true), 3).slots[3].toString());

        // A cursor kept on its own screen stops one pixel short of the next one along.
        is("clamped to the left edge", 1920, Layout.clamp(1900, 1920, 2560));
        is("clamped to the right edge", 4479, Layout.clamp(5000, 1920, 2560));
        is("left where it is", 2000, Layout.clamp(2000, 1920, 2560));
    }

    // --- The patches, against the game ---------------------------------------------------------

    /**
     * Runs the real transformer over the real game classes and checks what came out. This is the
     * half that notices a game update moving something: the mod turns itself off when a patch
     * cannot be applied, and this says so before anybody starts the game.
     */
    private static void patches(Path jar) throws Exception {
        System.out.println();
        System.out.println("The patches, against " + jar);
        if (!Files.exists(jar)) {
            fail("the game jar", "not found at " + jar);
            return;
        }

        Patch patch = new Patch();
        try (JarFile game = new JarFile(jar.toFile())) {
            // IsoCamera: the six methods that cut the screen up now ask the mod.
            byte[] isoCamera = patched(patch, game, "zombie/iso/IsoCamera");
            if (isoCamera != null) {
                Map<String, String> expected = new LinkedHashMap<>();
                expected.put("getScreenLeft", "screenLeft");
                expected.put("getScreenTop", "screenTop");
                expected.put("getScreenWidth", "screenWidth");
                expected.put("getScreenHeight", "screenHeight");
                expected.put("getOffscreenLeft", "offscreenLeft");
                expected.put("getOffscreenTop", "offscreenTop");
                for (Map.Entry<String, String> entry : expected.entrySet()) {
                    is("IsoCamera." + entry.getKey() + " asks the mod",
                        1, calls(isoCamera, entry.getKey(), "monitorcoop/Hooks", entry.getValue()));
                }
                liveRectangles(jar, isoCamera);
            }

            // Core: the frame setup hands over its arguments and its three calls.
            byte[] core = patched(patch, game, "zombie/core/Core");
            if (core != null) {
                for (String method : Arrays.asList("DoStartFrameStuffInternal", "DoStartFrameNoZoom")) {
                    is("Core." + method + " hands over its arguments",
                        1, calls(core, method, "monitorcoop/Hooks", "frameSetup"));
                    is("Core." + method + " viewport goes through the mod",
                        2, calls(core, method, "monitorcoop/Hooks", "glViewport"));
                    is("Core." + method + " clipping goes through the mod",
                        1, calls(core, method, "monitorcoop/Hooks", "glScissor"));
                    is("Core." + method + " projection goes through the mod",
                        2, calls(core, method, "monitorcoop/Hooks", "ortho2D"));
                    is("Core." + method + " has no viewport call left to the game",
                        0, calls(core, method, "org/lwjgl/opengl/GL11", "glViewport"));
                    is("Core." + method + " has no projection call left to the game",
                        0, calls(core, method, "org/joml/Matrix4f", "setOrtho2D"));
                }
            }

            // GameWindow: the once-a-frame tick.
            byte[] gameWindow = patched(patch, game, "zombie/GameWindow");
            if (gameWindow != null) {
                is("GameWindow.frameStep runs the mod once a frame",
                    1, calls(gameWindow, "frameStep", "monitorcoop/Hooks", "tick"));
            }

            // UIManager: the moodles, the clock and the mouse pointer.
            byte[] uiManager = patched(patch, game, "zombie/ui/UIManager");
            if (uiManager != null) {
                int atEveryReturn = calls(uiManager, "resize", "monitorcoop/Hooks", "uiResized");
                if (atEveryReturn < 1) {
                    fail("UIManager.resize", "the mod is not called at the end of it");
                } else {
                    pass();
                }
            }

            // The atlases clip to the player's part of the offscreen buffer.
            atlasesCovered(patch, game);

            // The view cone blur sends its sizes through the mod.
            vispolyCovered(patch, game);
        }
    }

    /** Transforms one class and says so, or records a failure when the patch did not apply. */
    private static byte[] patched(Patch patch, JarFile game, String internalName) throws IOException {
        byte[] bytes = read(game, internalName);
        if (bytes == null) {
            fail(internalName, "is not in the game jar any more");
            return null;
        }
        byte[] out = patch.transform(null, internalName, null, null, bytes);
        if (out == null) {
            fail(internalName, "was left alone: " + (Patch.broken() == null ? "no reason given" : Patch.broken()));
            return null;
        }
        verify(internalName, out);
        pass();
        return out;
    }

    /** What came out has to be a class file the runtime will accept. */
    private static void verify(String internalName, byte[] bytes) {
        List<VerifyError> errors = ClassFile.of().verify(bytes);
        checks++;
        if (!errors.isEmpty()) {
            failures.add(internalName + " came out of the patch malformed: " + errors.get(0).getMessage());
        }
    }

    /** Every class that clips to a player's part of the offscreen buffer must be patched. */
    private static void atlasesCovered(Patch patch, JarFile game) throws IOException {
        int patchedClasses = 0;
        for (ZipEntry entry : Collections.list(game.entries())) {
            String name = entry.getName();
            if (!name.endsWith(".class")) {
                continue;
            }
            String internalName = name.substring(0, name.length() - ".class".length());
            if (!internalName.startsWith("zombie/core/skinnedmodel/DeadBodyAtlas")
                && !internalName.startsWith("zombie/core/skinnedmodel/model/WorldItemAtlas")) {
                continue;
            }
            byte[] bytes = read(game, internalName);
            if (callsAnywhere(bytes, "org/lwjgl/opengl/GL11", "glScissor") == 0) {
                continue;
            }
            byte[] out = patch.transform(null, internalName, null, null, bytes);
            if (out == null) {
                fail(internalName, "clips the screen but was not patched");
                continue;
            }
            is(internalName + " clips through the mod", 0, callsAnywhere(out, "org/lwjgl/opengl/GL11", "glScissor"));
            verify(internalName, out);
            patchedClasses++;
        }
        if (patchedClasses == 0) {
            fail("the atlases", "no class in them clips the screen any more; the mod expects two");
        } else {
            pass();
        }
    }

    /** The view cone blur tells its shader the size of the player's part of the buffer. */
    private static void vispolyCovered(Patch patch, JarFile game) throws IOException {
        int patchedClasses = 0;
        for (ZipEntry entry : Collections.list(game.entries())) {
            String name = entry.getName();
            if (!name.endsWith(".class") || !name.startsWith("zombie/vispoly/VisibilityPolygon2")) {
                continue;
            }
            String internalName = name.substring(0, name.length() - ".class".length());
            byte[] bytes = read(game, internalName);
            if (callsAnywhere(bytes, "zombie/core/opengl/ShaderProgram", "setValue") == 0) {
                continue;
            }
            byte[] out = patch.transform(null, internalName, null, null, bytes);
            if (out == null) {
                fail(internalName, "sends values to a shader but was not patched");
                continue;
            }
            verify(internalName, out);
            patchedClasses++;
        }
        if (patchedClasses == 0) {
            fail("the view cone", "VisibilityPolygon2 no longer sends sizes to its shader");
        } else {
            pass();
        }
    }

    /**
     * Loads the patched IsoCamera for real and asks it where each player's view goes. This is the
     * one patch that can be run without a graphics card behind it, and it is the one everything
     * else depends on.
     */
    private static void liveRectangles(Path jar, final byte[] isoCamera) {
        try {
            Plan plan = Layout.build(
                Arrays.asList(new Rect(0, 0, 2560, 1440), new Rect(-1920, 356, 1920, 1080)),
                Arrays.asList(0, 1), 2);
            ClassLoader loader = new ClassLoader(Tests.class.getClassLoader()) {
                @Override
                protected Class<?> findClass(String name) throws ClassNotFoundException {
                    if (name.equals("zombie.iso.IsoCamera")) {
                        return defineClass(name, isoCamera, 0, isoCamera.length);
                    }
                    throw new ClassNotFoundException(name);
                }

                @Override
                protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
                    if (name.equals("zombie.iso.IsoCamera")) {
                        Class<?> found = findLoadedClass(name);
                        if (found == null) {
                            found = findClass(name);
                        }
                        if (resolve) {
                            resolveClass(found);
                        }
                        return found;
                    }
                    return super.loadClass(name, resolve);
                }
            };

            Class<?> camera = loader.loadClass("zombie.iso.IsoCamera");
            Hooks.plan = plan;
            try {
                is("player one's view starts at", 1920, call(camera, "getScreenLeft", 0));
                is("player one's view is", 2560, call(camera, "getScreenWidth", 0));
                is("player one's view is tall", 1440, call(camera, "getScreenHeight", 0));
                is("player two's view starts at", 0, call(camera, "getScreenLeft", 1));
                is("player two's view starts down", 356, call(camera, "getScreenTop", 1));
                is("player two's view is", 1920, call(camera, "getScreenWidth", 1));
                is("the offscreen buffer agrees", 1920, call(camera, "getOffscreenLeft", 0));
                is("the offscreen buffer agrees downwards", 356, call(camera, "getOffscreenTop", 1));
            } finally {
                Hooks.plan = null;
            }
        } catch (Throwable t) {
            fail("the patched IsoCamera", "could not be loaded and called: " + t);
        }
    }

    private static int call(Class<?> type, String name, int player) throws Exception {
        Method method = type.getMethod(name, int.class);
        return ((Integer)method.invoke(null, Integer.valueOf(player))).intValue();
    }

    // --- Reading class files -------------------------------------------------------------------

    private static byte[] read(JarFile jar, String internalName) throws IOException {
        ZipEntry entry = jar.getEntry(internalName + ".class");
        if (entry == null) {
            return null;
        }
        try (InputStream in = jar.getInputStream(entry)) {
            return in.readAllBytes();
        }
    }

    /** How many times one method calls another. */
    private static int calls(byte[] bytes, String inMethod, String owner, String name) {
        int found = 0;
        ClassModel model = ClassFile.of().parse(bytes);
        for (MethodModel method : model.methods()) {
            if (!method.methodName().stringValue().equals(inMethod)) {
                continue;
            }
            found += count(method, owner, name);
        }
        return found;
    }

    /** How many times a whole class calls a method. */
    private static int callsAnywhere(byte[] bytes, String owner, String name) {
        int found = 0;
        ClassModel model = ClassFile.of().parse(bytes);
        for (MethodModel method : model.methods()) {
            found += count(method, owner, name);
        }
        return found;
    }

    private static int count(MethodModel method, String owner, String name) {
        int found = 0;
        if (method.code().isPresent()) {
            CodeModel code = method.code().get();
            for (CodeElement element : code) {
                if (element instanceof InvokeInstruction call
                    && call.owner().asInternalName().equals(owner)
                    && call.name().stringValue().equals(name)) {
                    found++;
                }
            }
        }
        return found;
    }

    // --- Saying what happened ------------------------------------------------------------------

    private static List<Integer> none() {
        return Collections.emptyList();
    }

    private static String join(int[] values) {
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < values.length; i++) {
            if (i > 0) {
                text.append(',');
            }
            text.append(values[i]);
        }
        return text.toString();
    }

    private static void is(String what, String expected, String actual) {
        checks++;
        if (!expected.equals(actual)) {
            failures.add(what + ": expected " + expected + ", got " + actual);
        }
    }

    private static void is(String what, int expected, int actual) {
        checks++;
        if (expected != actual) {
            failures.add(what + ": expected " + expected + ", got " + actual);
        }
    }

    private static void isNull(String what, Object actual) {
        checks++;
        if (actual != null) {
            failures.add(what + ": expected nothing to do, got " + actual);
        }
    }

    private static void pass() {
        checks++;
    }

    private static void fail(String what, String why) {
        checks++;
        failures.add(what + ": " + why);
    }
}
