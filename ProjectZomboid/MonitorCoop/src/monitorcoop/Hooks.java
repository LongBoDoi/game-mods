package monitorcoop;

import org.joml.Matrix4f;
import org.lwjgl.opengl.GL11;
import zombie.characters.IsoPlayer;
import zombie.core.Core;
import zombie.core.SpriteRenderer;
import zombie.core.opengl.ShaderProgram;
import zombie.iso.Vector2;
import zombie.ui.Clock;
import zombie.ui.MoodlesUI;
import zombie.ui.UIManager;

/**
 * The methods the patched game code calls. Every one of them answers exactly what the game would
 * have worked out for itself while no layout is live, so the game behaves as it ships whenever the
 * mod is off, between sessions, or at the main menu.
 *
 * <p>The game decides where a split-screen player's view goes in one place -
 * {@code IsoCamera.getScreenLeft/Top/Width/Height} - and almost everything downstream reads it from
 * there, including the Lua UI through {@code getPlayerScreenLeft} and friends. Handing those four a
 * different rectangle per player is what gives each player a whole monitor; the rest of the hooks
 * below cover the handful of places that work out the same split with their own arithmetic.</p>
 */
public final class Hooks {
    /** The live layout, or null when the game should behave as it ships. */
    static volatile Plan plan;

    /** What the frame being set up is for, so the viewport hooks know which rules apply. */
    private static final class FrameState {
        int player = -1;
        boolean textFrame;
        boolean smartTexture;
    }

    private static final ThreadLocal<FrameState> FRAME = new ThreadLocal<FrameState>() {
        @Override
        protected FrameState initialValue() {
            return new FrameState();
        }
    };

    private Hooks() {
    }

    // --- IsoCamera: where each player's view goes ---------------------------------------------

    public static int screenLeft(int player) {
        Rect rect = rect(player);
        if (rect != null) {
            return rect.x;
        }
        return player != 1 && player != 3 ? 0 : Core.getInstance().getScreenWidth() / 2;
    }

    public static int screenTop(int player) {
        Rect rect = rect(player);
        if (rect != null) {
            return rect.y;
        }
        return player != 2 && player != 3 ? 0 : Core.getInstance().getScreenHeight() / 2;
    }

    public static int screenWidth(int player) {
        Rect rect = rect(player);
        if (rect != null) {
            return rect.w;
        }
        return IsoPlayer.numPlayers > 1 ? Core.getInstance().getScreenWidth() / 2 : Core.getInstance().getScreenWidth();
    }

    public static int screenHeight(int player) {
        Rect rect = rect(player);
        if (rect != null) {
            return rect.h;
        }
        return IsoPlayer.numPlayers > 2 ? Core.getInstance().getScreenHeight() / 2 : Core.getInstance().getScreenHeight();
    }

    /**
     * Where the player's view sits inside the offscreen buffer. The buffer is one texture the size
     * of the whole window that every player draws into at their own screen position, and the
     * finished picture is blitted back to the same place, so this is the screen rectangle again.
     */
    public static int offscreenLeft(int player) {
        Rect rect = rect(player);
        if (rect != null) {
            return rect.x;
        }
        return player != 1 && player != 3 ? 0 : Core.getInstance().getScreenWidth() / 2;
    }

    public static int offscreenTop(int player) {
        Rect rect = rect(player);
        if (rect != null) {
            return rect.y;
        }
        return player >= 2 ? Core.getInstance().getScreenHeight() / 2 : 0;
    }

    // --- Core: the OpenGL viewport for one player's frame -------------------------------------

    /**
     * Called first thing in the frame-setup methods of Core, which work out the viewport for a
     * player with their own halving arithmetic. Remembering the arguments here is what lets the
     * calls below put the player's own rectangle in place of that arithmetic.
     */
    public static void frameSetup(int player, boolean textFrame, boolean fx, boolean smartTexture) {
        FrameState state = FRAME.get();
        state.player = player;
        state.textFrame = textFrame;
        state.smartTexture = smartTexture;
    }

    /**
     * The projection for the frame. The game asks for a box as wide and tall as the player's share
     * of the window (times the zoom, and whichever way up this frame is drawn), so scaling the
     * extents by how much bigger that share now is keeps every other case - full-screen effects,
     * smart textures - in proportion.
     */
    public static Matrix4f ortho2D(Matrix4f matrix, float left, float right, float bottom, float top) {
        FrameState state = FRAME.get();
        Rect rect = state.smartTexture ? null : rect(state.player);
        if (rect == null) {
            return matrix.setOrtho2D(left, right, bottom, top);
        }

        float wide = vanillaWidth(state.player);
        float tall = vanillaHeight(state.player);
        float scaleX = wide > 0.0F ? rect.w / wide : 1.0F;
        float scaleY = tall > 0.0F ? rect.h / tall : 1.0F;
        return matrix.setOrtho2D(left * scaleX, right * scaleX, bottom * scaleY, top * scaleY);
    }

    public static void glViewport(int x, int y, int w, int h) {
        int[] box = viewportBox(x, y, w, h);
        GL11.glViewport(box[0], box[1], box[2], box[3]);
    }

    public static void glScissor(int x, int y, int w, int h) {
        int[] box = viewportBox(x, y, w, h);
        GL11.glScissor(box[0], box[1], box[2], box[3]);
    }

    /**
     * The player's rectangle in OpenGL terms. Text frames are drawn straight to the window, which
     * counts rows from the bottom, so those are flipped exactly as the game flips them.
     */
    private static int[] viewportBox(int x, int y, int w, int h) {
        FrameState state = FRAME.get();
        Rect rect = rect(state.player);
        if (rect == null) {
            return new int[] { x, y, w, h };
        }

        // A smart texture is the caller's own buffer: it goes at the player's corner, at the size
        // the caller asked for.
        int width = state.smartTexture ? w : rect.w;
        int height = state.smartTexture ? h : rect.h;
        int top = state.textFrame ? Core.getInstance().getScreenHeight() - height - rect.y : rect.y;
        return new int[] { rect.x, top, width, height };
    }

    // --- The dead body and world item atlases -------------------------------------------------

    /**
     * Both atlases clip their drawing to the player's quarter of the offscreen buffer, worked out
     * from the buffer's own size. The same rectangle in those terms is the player's screen
     * rectangle scaled by how much bigger the buffer is than the screen.
     *
     * <p>Each of those methods also clips to an atlas slot, which is nothing to do with the split;
     * this hook recognises the split rectangle by its size and leaves everything else alone.</p>
     */
    public static void atlasScissor(int x, int y, int w, int h) {
        try {
            Plan live = plan;
            if (live != null) {
                int player = SpriteRenderer.instance.getRenderingPlayerIndex();
                Rect rect = live.slot(player);
                Core core = Core.getInstance();
                int bufferW = core.getOffscreenTrueWidth();
                int bufferH = core.getOffscreenTrueHeight();
                int splitW = bufferW / (IsoPlayer.numPlayers > 1 ? 2 : 1);
                int splitH = bufferH / (IsoPlayer.numPlayers > 2 ? 2 : 1);
                if (rect != null && w == splitW && h == splitH) {
                    int screenW = core.getScreenWidth();
                    int screenH = core.getScreenHeight();
                    if (screenW > 0 && screenH > 0) {
                        GL11.glScissor(
                            Math.round((float)rect.x * bufferW / screenW),
                            Math.round((float)rect.y * bufferH / screenH),
                            Math.round((float)rect.w * bufferW / screenW),
                            Math.round((float)rect.h * bufferH / screenH));
                        return;
                    }
                }
            }
        } catch (Throwable t) {
            // Never let a clipping detail take the game down: fall through to the game's own box.
        }
        GL11.glScissor(x, y, w, h);
    }

    // --- The view cone blur -------------------------------------------------------------------

    /**
     * The view cone blur tells its shader where the player's view sits in the offscreen buffer.
     * Two of those three values come from IsoCamera and so are already right; the size of the
     * player's part of the buffer is worked out from the buffer and the split instead, and this
     * replaces it with the same thing measured from the player's own rectangle.
     */
    public static void vispolySetValue(ShaderProgram program, String name, Vector2 value) {
        try {
            Plan live = plan;
            if (live != null && "displaySize".equals(name)) {
                Rect rect = live.slot(SpriteRenderer.instance.getRenderingPlayerIndex());
                Core core = Core.getInstance();
                int screenW = core.getScreenWidth();
                int screenH = core.getScreenHeight();
                if (rect != null && screenW > 0 && screenH > 0) {
                    value.set(
                        Math.round((float)rect.w * core.getOffscreenTrueWidth() / screenW),
                        Math.round((float)rect.h * core.getOffscreenTrueHeight() / screenH));
                }
            }
        } catch (Throwable t) {
            // Leave the game's own value in place.
        }
        program.setValue(name, value);
    }

    // --- The game's own moodles, clock and speed controls -------------------------------------

    /**
     * Runs after UIManager.resize, which puts the moodles against the right-hand edge of each
     * player's half of the window and the clock and speed controls in the middle of the bottom of
     * it. Those are the game's own Java UI elements; everything else in the HUD is Lua, which
     * follows the player rectangles by itself.
     */
    public static void uiResized() {
        try {
            Session.confineMouse();

            Plan live = plan;
            if (live == null || UIManager.getClock() == null) {
                return;
            }

            int offset = Clock.instance != null && Clock.instance.isVisible() ? 64 : 24;
            for (int i = 0; i < Plan.MAX_PLAYERS; i++) {
                MoodlesUI moodles = UIManager.MoodleUI[i];
                Rect rect = i < live.count ? live.slot(i) : null;
                if (moodles == null || rect == null) {
                    continue;
                }
                moodles.setX(rect.right() - (10.0F + moodles.width));
                moodles.setY(rect.y + offset);
            }

            // One clock for everybody, so it goes on player one's screen.
            Rect first = live.slot(0);
            Clock clock = UIManager.clock;
            if (first != null && clock != null) {
                float half = Core.getInstance().getOptionClockSize() == 2 ? 83.0F : 45.5F;
                clock.setX(first.x + first.w / 2.0F - half);
                clock.setY(first.bottom() - 70);
                if (UIManager.speedControls != null) {
                    UIManager.speedControls.setX(first.x + first.w / 2.0F - UIManager.speedControls.width - 10.0F);
                    UIManager.speedControls.setY(clock.getY() + clock.getHeight() + 10.0);
                }
            }
        } catch (Throwable t) {
            Log.error("Could not place the moodles and clock", t);
        }
    }

    /**
     * Runs after the game has drawn the UI, and outlines each player's area when
     * {@code ShowPlayerAreas} is on: a frame in that player's colour, drawn over everything, right
     * on the edges of the rectangle the mod gave them. A photograph or screenshot of the monitors
     * then says whether the mod and the monitors agree - and whether what is inside the frame sits
     * where the frame is.
     */
    public static void uiRendered() {
        try {
            Plan live = plan;
            if (live == null || !Session.showPlayerAreas()) {
                return;
            }
            for (int i = 0; i < live.count; i++) {
                Rect rect = live.slot(i);
                if (rect == null) {
                    continue;
                }
                float[] colour = AREA_COLOURS[i % AREA_COLOURS.length];
                int thick = 4;
                box(rect.x, rect.y, rect.w, thick, colour);                      // top
                box(rect.x, rect.bottom() - thick, rect.w, thick, colour);       // bottom
                box(rect.x, rect.y, thick, rect.h, colour);                      // left
                box(rect.right() - thick, rect.y, thick, rect.h, colour);        // right
                // A short bar inwards from the top left corner, one per player, so it is obvious
                // which frame belongs to which player on a photograph.
                for (int mark = 0; mark <= i; mark++) {
                    box(rect.x + thick + mark * 3 * thick, rect.y + thick, 2 * thick, 6 * thick, colour);
                }
            }
        } catch (Throwable t) {
            // A diagnostic is never worth a crash.
        }
    }

    /** Player one red, player two green, player three blue, player four yellow. */
    private static final float[][] AREA_COLOURS = {
        { 1.0F, 0.1F, 0.1F },
        { 0.1F, 1.0F, 0.1F },
        { 0.3F, 0.5F, 1.0F },
        { 1.0F, 0.9F, 0.1F },
    };

    private static void box(int x, int y, int w, int h, float[] colour) {
        SpriteRenderer.instance.renderi(null, x, y, w, h, colour[0], colour[1], colour[2], 1.0F, null);
    }

    // --- The driver ---------------------------------------------------------------------------

    /** Runs once a frame, from GameWindow.frameStep. */
    public static void tick() {
        Session.tick();
    }

    // --- Shared ------------------------------------------------------------------------------

    /** The live rectangle for one of the game's player slots, or null when there is no layout. */
    private static Rect rect(int player) {
        Plan live = plan;
        return live == null ? null : live.slot(player);
    }

    private static int vanillaWidth(int player) {
        int width = Core.getInstance().getScreenWidth();
        return IsoPlayer.numPlayers > 1 ? width / 2 : width;
    }

    private static int vanillaHeight(int player) {
        int height = Core.getInstance().getScreenHeight();
        return IsoPlayer.numPlayers > 2 ? height / 2 : height;
    }
}
