package monitorcoop;

/** Which window to open, and which part of it each split-screen player gets. */
public final class Plan {
    /** The game never has more than four split-screen players. */
    public static final int MAX_PLAYERS = 4;

    /** The window to span, in desktop pixels: the bounding box of the monitors in use. */
    public final Rect window;

    /**
     * A rectangle for every one of the game's four player slots, in window-relative pixels.
     * The first {@link #count} are the players; the rest are the slots the game paints black
     * when there is an odd number of players, kept out of everybody's way.
     */
    public final Rect[] slots;

    /** The monitor each player sits on, for the log. -1 for a slot with no player in it. */
    public final int[] monitorForSlot;

    /** How many of the slots are real players. */
    public final int count;

    public Plan(Rect window, Rect[] slots, int[] monitorForSlot, int count) {
        this.window = window;
        this.slots = slots;
        this.monitorForSlot = monitorForSlot;
        this.count = count;
    }

    /** The rectangle for one of the game's player slots, or null when the index is not one. */
    public Rect slot(int index) {
        return index >= 0 && index < this.slots.length ? this.slots[index] : null;
    }

    public boolean matches(Plan other) {
        if (other == null || other.count != this.count || !other.window.equals(this.window)) {
            return false;
        }
        for (int i = 0; i < this.slots.length; i++) {
            if (!other.slots[i].equals(this.slots[i]) || other.monitorForSlot[i] != this.monitorForSlot[i]) {
                return false;
            }
        }
        return true;
    }

    public String describe() {
        StringBuilder text = new StringBuilder();
        text.append("window ").append(this.window);
        for (int i = 0; i < this.count; i++) {
            text.append("; P").append(i + 1).append(" on monitor ").append(this.monitorForSlot[i] + 1)
                .append(" -> ").append(this.slots[i]);
        }
        return text.toString();
    }
}
