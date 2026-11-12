package monitorcoop;

/**
 * A rectangle in pixels. Deliberately free of game and GLFW types so the layout maths can be
 * tested without the game.
 */
public final class Rect {
    public final int x;
    public final int y;
    public final int w;
    public final int h;

    public Rect(int x, int y, int w, int h) {
        this.x = x;
        this.y = y;
        this.w = w;
        this.h = h;
    }

    public int right() {
        return this.x + this.w;
    }

    public int bottom() {
        return this.y + this.h;
    }

    @Override
    public boolean equals(Object other) {
        if (!(other instanceof Rect)) {
            return false;
        }
        Rect that = (Rect)other;
        return this.x == that.x && this.y == that.y && this.w == that.w && this.h == that.h;
    }

    @Override
    public int hashCode() {
        return ((this.x * 31 + this.y) * 31 + this.w) * 31 + this.h;
    }

    @Override
    public String toString() {
        return this.w + "x" + this.h + " at " + this.x + "," + this.y;
    }
}
