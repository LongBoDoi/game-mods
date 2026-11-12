package monitorcoop;

import java.lang.classfile.ClassBuilder;
import java.lang.classfile.ClassElement;
import java.lang.classfile.ClassFile;
import java.lang.classfile.ClassModel;
import java.lang.classfile.ClassTransform;
import java.lang.classfile.CodeTransform;
import java.lang.classfile.MethodModel;
import java.lang.classfile.MethodTransform;
import java.lang.classfile.instruction.InvokeInstruction;
import java.lang.classfile.instruction.ReturnInstruction;
import java.lang.constant.ClassDesc;
import java.lang.constant.MethodTypeDesc;
import java.lang.instrument.ClassFileTransformer;
import java.lang.reflect.AccessFlag;
import java.security.ProtectionDomain;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeSet;

/**
 * Rewrites the handful of methods that work out where a split-screen player's view goes, so they
 * ask this mod instead. Each one is replaced or redirected as it is loaded, using the class file
 * tools in the Java runtime itself, so there is nothing to ship alongside.
 *
 * <p>Nothing here guesses. Every method and call it means to change is named with its exact
 * signature and counted; if a game update moves one of them, the class is left exactly as the game
 * shipped it and the mod turns itself off with a line in the log rather than drawing nonsense.</p>
 */
final class Patch implements ClassFileTransformer {
    private static final ClassDesc HOOKS = ClassDesc.of("monitorcoop.Hooks");

    private static final String GL11 = "org/lwjgl/opengl/GL11";
    private static final String MATRIX4F = "org/joml/Matrix4f";
    private static final String SHADER_PROGRAM = "zombie/core/opengl/ShaderProgram";

    private static final MethodTypeDesc INT_TO_INT = MethodTypeDesc.ofDescriptor("(I)I");
    private static final MethodTypeDesc VOID = MethodTypeDesc.ofDescriptor("()V");
    private static final MethodTypeDesc BOX = MethodTypeDesc.ofDescriptor("(IIII)V");
    private static final MethodTypeDesc FRAME_SETUP = MethodTypeDesc.ofDescriptor("(IZZZ)V");
    private static final MethodTypeDesc ORTHO =
        MethodTypeDesc.ofDescriptor("(Lorg/joml/Matrix4f;FFFF)Lorg/joml/Matrix4f;");

    /** What the call in the game looks like: the same thing without the matrix as an argument. */
    private static final String SET_ORTHO = "(FFFF)Lorg/joml/Matrix4f;";
    private static final MethodTypeDesc SET_VALUE =
        MethodTypeDesc.ofDescriptor("(Ljava/lang/String;Lzombie/iso/Vector2;)V");
    private static final MethodTypeDesc SET_VALUE_HOOK = MethodTypeDesc.ofDescriptor(
        "(Lzombie/core/opengl/ShaderProgram;Ljava/lang/String;Lzombie/iso/Vector2;)V");

    /** The game's own split of the screen, replaced by one rectangle per player. */
    private static final Map<String, String> SCREEN_METHODS = new LinkedHashMap<>();

    /** The frame setup in Core that works out a player's viewport with its own halving. */
    private static final List<String> FRAME_METHODS =
        Arrays.asList("DoStartFrameStuffInternal", "DoStartFrameNoZoom");

    /** The signature those two share: (w, h, zoom, player, isTextFrame, isFx, isSmartTexture). */
    private static final String FRAME_SIGNATURE = "(IIFIZZZ)V";

    static {
        SCREEN_METHODS.put("getScreenLeft", "screenLeft");
        SCREEN_METHODS.put("getScreenTop", "screenTop");
        SCREEN_METHODS.put("getScreenWidth", "screenWidth");
        SCREEN_METHODS.put("getScreenHeight", "screenHeight");
        SCREEN_METHODS.put("getOffscreenLeft", "offscreenLeft");
        SCREEN_METHODS.put("getOffscreenTop", "offscreenTop");
    }

    /** Set when a patch the layout cannot do without did not go in, which turns the mod off. */
    private static volatile String broken;

    /** What has been patched, in order, for the log once the game's own logging is up. */
    private static final List<String> applied = new ArrayList<>();

    /** Why the mod has turned itself off, or null while all is well. */
    static String broken() {
        return broken;
    }

    /**
     * What went in, one line. The patches happen while the game is starting, before the game has
     * taken over the log, so this is said again from the first frame where it will be seen.
     */
    static synchronized String summary() {
        return applied.isEmpty() ? "nothing" : String.join("; ", applied);
    }

    private static synchronized void done(String what) {
        applied.add(what);
        Log.info("Patched " + what + ".");
    }

    @Override
    public byte[] transform(ClassLoader loader, String name, Class<?> beingRedefined,
                            ProtectionDomain domain, byte[] bytes) {
        if (name == null || bytes == null) {
            return null;
        }

        try {
            if (name.equals("zombie/iso/IsoCamera")) {
                return screenRectangles(bytes);
            }
            if (name.equals("zombie/core/Core")) {
                return frameSetup(bytes);
            }
            if (name.equals("zombie/GameWindow")) {
                return prologue(bytes, "frameStep", "()V", "tick", "GameWindow.frameStep");
            }
            if (name.equals("zombie/ui/UIManager")) {
                return uiManager(bytes);
            }
            if (name.startsWith("zombie/core/skinnedmodel/DeadBodyAtlas")
                || name.startsWith("zombie/core/skinnedmodel/model/WorldItemAtlas")) {
                return atlasScissors(bytes, name);
            }
            if (name.startsWith("zombie/vispoly/VisibilityPolygon2")) {
                return viewCone(bytes, name);
            }
        } catch (Throwable t) {
            Log.error("Could not patch " + name + "; leaving it as the game shipped it", t);
            if (name.equals("zombie/iso/IsoCamera") || name.equals("zombie/core/Core")) {
                turnOff(name + " could not be patched");
            }
        }
        return null;
    }

    // --- IsoCamera -----------------------------------------------------------------------------

    /**
     * Replaces the six one-line methods in IsoCamera that cut the screen into halves and quarters.
     * Everything that draws a player's view reads it from there, and so does the Lua UI through
     * {@code getPlayerScreenLeft} and friends, which is why one monitor each needs nothing else.
     */
    private byte[] screenRectangles(byte[] bytes) {
        final TreeSet<String> replaced = new TreeSet<>();
        ClassFile files = ClassFile.of();
        byte[] out = files.transformClass(files.parse(bytes), new ClassTransform() {
            @Override
            public void accept(ClassBuilder builder, ClassElement element) {
                if (element instanceof MethodModel method) {
                    String name = method.methodName().stringValue();
                    String hook = SCREEN_METHODS.get(name);
                    if (hook != null && INT_TO_INT.descriptorString().equals(method.methodType().stringValue())
                        && method.flags().has(AccessFlag.STATIC)) {
                        builder.withMethodBody(name, method.methodTypeSymbol(), method.flags().flagsMask(),
                            code -> code.iload(0).invokestatic(HOOKS, hook, INT_TO_INT).ireturn());
                        replaced.add(name);
                        return;
                    }
                }
                builder.with(element);
            }
        });

        if (replaced.size() != SCREEN_METHODS.size()) {
            TreeSet<String> missing = new TreeSet<>(SCREEN_METHODS.keySet());
            missing.removeAll(replaced);
            turnOff("IsoCamera has no " + missing + " taking an int and returning an int");
            return null;
        }
        done("IsoCamera: " + replaced.size() + " methods (the split-screen rectangles)");
        return out;
    }

    // --- Core ----------------------------------------------------------------------------------

    /**
     * Core sets up the OpenGL viewport for a player's frame by halving the screen itself. The
     * arguments are handed to the mod at the top of the method, and the three calls that use that
     * arithmetic - the projection, the viewport and the clipping box - are pointed at hooks that
     * put the player's own rectangle in place of it.
     */
    private byte[] frameSetup(byte[] bytes) {
        final TreeSet<String> patched = new TreeSet<>();
        final int[] redirects = new int[1];
        ClassFile files = ClassFile.of();
        byte[] out = files.transformClass(files.parse(bytes), new ClassTransform() {
            @Override
            public void accept(ClassBuilder builder, ClassElement element) {
                if (element instanceof MethodModel method
                    && FRAME_METHODS.contains(method.methodName().stringValue())
                    && FRAME_SIGNATURE.equals(method.methodType().stringValue())) {
                    patched.add(method.methodName().stringValue());
                    builder.transformMethod(method, MethodTransform.transformingCode(frameCode(method, redirects)));
                    return;
                }
                builder.with(element);
            }
        });

        if (patched.size() != FRAME_METHODS.size()) {
            TreeSet<String> missing = new TreeSet<>(FRAME_METHODS);
            missing.removeAll(patched);
            turnOff("Core has no " + missing + " with the arguments " + FRAME_SIGNATURE);
            return null;
        }
        // Each of them has a projection, a viewport and a clipping box to redirect, and the game's
        // own version has five of those calls between the branches for a player and no player.
        if (redirects[0] < 3 * FRAME_METHODS.size()) {
            turnOff("Core." + patched + " no longer set up the viewport the way the mod expects"
                + " (found " + redirects[0] + " calls to redirect)");
            return null;
        }
        done("Core: " + patched + ", " + redirects[0] + " calls (the player viewport)");
        return out;
    }

    /** The code transform for one of Core's frame-setup methods. */
    private static CodeTransform frameCode(MethodModel method, final int[] redirects) {
        // (w, h, zoom, player, isTextFrame, isFx, isSmartTexture) after this, if there is a this.
        final int slot = method.flags().has(AccessFlag.STATIC) ? 0 : 1;
        final boolean[] started = { false };
        return (code, element) -> {
            if (!started[0]) {
                started[0] = true;
                code.iload(slot + 3).iload(slot + 4).iload(slot + 5).iload(slot + 6)
                    .invokestatic(HOOKS, "frameSetup", FRAME_SETUP);
            }

            if (element instanceof InvokeInstruction call) {
                String owner = call.owner().asInternalName();
                String name = call.name().stringValue();
                String signature = call.type().stringValue();
                boolean box = GL11.equals(owner) && BOX.descriptorString().equals(signature)
                    && (name.equals("glViewport") || name.equals("glScissor"));
                if (box) {
                    code.invokestatic(HOOKS, name, BOX);
                    redirects[0]++;
                    return;
                }
                if (MATRIX4F.equals(owner) && name.equals("setOrtho2D")
                    && SET_ORTHO.equals(signature)) {
                    code.invokestatic(HOOKS, "ortho2D", ORTHO);
                    redirects[0]++;
                    return;
                }
            }
            code.with(element);
        };
    }

    // --- UIManager -----------------------------------------------------------------------------

    /**
     * UIManager.resize puts the game's own moodles, clock and speed controls against the edges of
     * each player's half of the window. A call at the end of it moves them onto the player's own
     * monitor instead - and is also the one place per frame that runs while the screen is being
     * drawn, which is where the mouse pointer is kept on player one's monitor.
     */
    private byte[] uiManager(byte[] bytes) {
        final int[] returns = new int[2];
        final boolean[] found = { false, false };
        ClassFile files = ClassFile.of();
        byte[] out = files.transformClass(files.parse(bytes), new ClassTransform() {
            @Override
            public void accept(ClassBuilder builder, ClassElement element) {
                if (element instanceof MethodModel method
                    && method.methodType().stringValue().equals("()V")
                    && method.flags().has(AccessFlag.STATIC)) {
                    // resize places the game's own UI; render is where the mod can draw over it.
                    final int which = method.methodName().stringValue().equals("resize") ? 0
                        : method.methodName().stringValue().equals("render") ? 1 : -1;
                    if (which >= 0) {
                        found[which] = true;
                        final String hook = which == 0 ? "uiResized" : "uiRendered";
                        builder.transformMethod(method, MethodTransform.transformingCode((code, instruction) -> {
                            if (instruction instanceof ReturnInstruction) {
                                code.invokestatic(HOOKS, hook, VOID);
                                returns[which]++;
                            }
                            code.with(instruction);
                        }));
                        return;
                    }
                }
                builder.with(element);
            }
        });

        if (!found[0] || returns[0] == 0) {
            Log.warn("UIManager.resize is not there any more; the game's own moodles and clock will"
                + " sit where the game puts them.");
            return null;
        }
        done("UIManager.resize (the moodles, clock and pointer)"
            + (found[1] && returns[1] > 0 ? " and UIManager.render (ShowPlayerAreas)" : ""));
        return out;
    }

    // --- The atlases and the view cone ---------------------------------------------------------

    /**
     * The dead body and world item atlases clip their drawing to the player's quarter of the
     * offscreen buffer. Both of them also clip to a slot in their own texture, which has nothing to
     * do with the split, so every clipping box in them goes through one hook that recognises the
     * split box and leaves the others alone.
     */
    private byte[] atlasScissors(byte[] bytes, String name) {
        final int[] redirects = new int[1];
        ClassFile files = ClassFile.of();
        byte[] out = files.transformClass(files.parse(bytes),
            ClassTransform.transformingMethodBodies((code, element) -> {
                if (element instanceof InvokeInstruction call
                    && GL11.equals(call.owner().asInternalName())
                    && call.name().stringValue().equals("glScissor")
                    && BOX.descriptorString().equals(call.type().stringValue())) {
                    code.invokestatic(HOOKS, "atlasScissor", BOX);
                    redirects[0]++;
                    return;
                }
                code.with(element);
            }));

        if (redirects[0] == 0) {
            return null;
        }
        done(simple(name) + ": " + redirects[0] + " clipping boxes");
        return out;
    }

    /**
     * The view cone blur tells its shader how big the player's part of the offscreen buffer is,
     * worked out from the buffer and the split rather than from IsoCamera. Every value it sends the
     * shader goes through one hook, which replaces that one and passes the rest straight on.
     */
    private byte[] viewCone(byte[] bytes, String name) {
        final int[] redirects = new int[1];
        ClassFile files = ClassFile.of();
        byte[] out = files.transformClass(files.parse(bytes),
            ClassTransform.transformingMethodBodies((code, element) -> {
                if (element instanceof InvokeInstruction call
                    && SHADER_PROGRAM.equals(call.owner().asInternalName())
                    && call.name().stringValue().equals("setValue")
                    && SET_VALUE.descriptorString().equals(call.type().stringValue())) {
                    code.invokestatic(HOOKS, "vispolySetValue", SET_VALUE_HOOK);
                    redirects[0]++;
                    return;
                }
                code.with(element);
            }));

        if (redirects[0] == 0) {
            return null;
        }
        done(simple(name) + ": " + redirects[0] + " shader values (the view cone)");
        return out;
    }

    // --- Shared --------------------------------------------------------------------------------

    /** Calls a hook first thing in one method, used for the once-a-frame tick. */
    private byte[] prologue(byte[] bytes, String method, String signature, String hook, String what) {
        final boolean[] found = { false };
        ClassFile files = ClassFile.of();
        byte[] out = files.transformClass(files.parse(bytes), new ClassTransform() {
            @Override
            public void accept(ClassBuilder builder, ClassElement element) {
                if (element instanceof MethodModel model
                    && model.methodName().stringValue().equals(method)
                    && model.methodType().stringValue().equals(signature)) {
                    found[0] = true;
                    final boolean[] started = { false };
                    builder.transformMethod(model, MethodTransform.transformingCode((code, instruction) -> {
                        if (!started[0]) {
                            started[0] = true;
                            code.invokestatic(HOOKS, hook, VOID);
                        }
                        code.with(instruction);
                    }));
                    return;
                }
                builder.with(element);
            }
        });

        if (!found[0]) {
            turnOff(what + " is not there any more, so the mod has nothing to run from");
            return null;
        }
        done(what + " (the once-a-frame check)");
        return out;
    }

    private static void turnOff(String why) {
        if (broken == null) {
            broken = why;
            Log.error(why + ". Local split-screen is left exactly as the game ships it."
                + " A game update most likely moved it; the mod needs a new build.");
        }
    }

    private static String simple(String internalName) {
        int slash = internalName.lastIndexOf('/');
        return slash < 0 ? internalName : internalName.substring(slash + 1);
    }
}
