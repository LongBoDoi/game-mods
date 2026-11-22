"""Icons for Movement (1-20) and Carrying / throwing (21-36)."""
import math

from PIL import ImageChops

from iconkit import *


# --- local helpers ----------------------------------------------------------------------------

def union(*masks):
    m = masks[0]
    for x in masks[1:]:
        m = ImageChops.lighter(m, x)
    return m


def counter(ic, x0, y0, x1, y1):
    """A kitchen counter: wooden body with a steel top."""
    ic.rect(x0 + 4, y0 + 8, x1 - 4, y1, WOOD, r=5, shine=True)
    ic.rect(x0, y0, x1, y0 + 12, STEEL, r=4)


def shoe(ic, cx, cy, s, fill=RED, sole=WHITE):
    """A running shoe, toe right; (cx, cy) is the middle of the sole."""
    body = [(-24, -2), (-22, -18), (-8, -20), (2, -10), (18, -6), (26, 0), (26, 4), (-24, 4)]
    ic.poly([(cx + x * s, cy + y * s) for x, y in body], fill, shine=True)
    ic.rect(cx - 26 * s, cy + 1 * s, cx + 28 * s, cy + 9 * s, sole, r=4 * s)
    ic.detail([(cx - 6 * s, cy - 14 * s), (cx + 2 * s, cy - 9 * s)], 2.5 * s, WHITE)
    ic.detail([(cx - 1 * s, cy - 16 * s), (cx + 7 * s, cy - 11 * s)], 2.5 * s, WHITE)


def chevron(ic, cx, cy, s, fill):
    pts = [(-12, -20), (2, -20), (16, 0), (2, 20), (-12, 20), (2, 0)]
    ic.poly([(cx + x * s, cy + y * s) for x, y in pts], fill, shine=True)


def tomato(ic, cx, cy, r):
    ic.circle(cx, cy, r, RED, shine=True)
    ic.poly(star_pts(cx, cy - r * 0.8, r * 0.55, r * 0.22, 5), GREEN, line=2.5)


def ball(ic, cx, cy, r, fill):
    ic.circle(cx, cy, r, fill, shine=True)


def wing(ic, x, y, s, flip=False):
    """A little feathered wing attached at (x, y), spreading up and out."""
    k = -1 if flip else 1
    pts = [(0, 0), (k * 10, -16), (k * 26, -22), (k * 30, -16), (k * 22, -10), (k * 28, -6),
           (k * 18, 0), (k * 22, 4), (k * 8, 6)]
    ic.poly([(x + px * s, y + py * s) for px, py in pts], WHITE, shine=True)


def glove(ic, cx, cy, s, fill=WHITE, angle=0):
    hand(ic, cx, cy, s, fill, angle)


# --- 1. Movement ------------------------------------------------------------------------------

def vault(ic):
    counter(ic, 22, 66, 106, 104)
    curved_arrow(ic, 64, 72, 42, 195, 345, 9, WHITE, head=24)


def blink(ic):
    # A faded chef left, a bright chef right, sparkles between.
    m = ic.m_rect(14, 58, 40, 98, 9)
    m = union(m, ic.m_circle(27, 46, 12))
    ic.paint(m, (150, 200, 240, 255))
    chef(ic, 84, 50, 1.0)
    sparkle(ic, 52, 32, 11, YELLOW)
    sparkle(ic, 56, 74, 8, WHITE)
    sparkle(ic, 44, 96, 6, YELLOW)


def sprinter(ic):
    speed_lines(ic, 40, 66, 1.1, n=3, length=24)
    shoe(ic, 74, 78, 1.25)


def double_dash(ic):
    chevron(ic, 46, 64, 1.7, WHITE)
    chevron(ic, 84, 64, 1.7, YELLOW)


def charge_dash(ic):
    arrow(ic, [(16, 80), (84, 80)], 12, WHITE, head=34)
    bolt(ic, 50, 42, 2.3, YELLOW, angle=10)


def momentum(ic):
    # Three arrows growing in size, rising to the right.
    arrow(ic, [(14, 98), (34, 98)], 7, WHITE, head=18)
    arrow(ic, [(38, 76), (64, 76)], 9, WHITE, head=24)
    arrow(ic, [(64, 48), (98, 48)], 12, YELLOW, head=32)


def slipstream(ic):
    chef(ic, 86, 44, 0.85)
    chef(ic, 40, 56, 0.7, body=LIGHTBLUE)
    for y, ln in ((48, 22), (62, 30), (76, 22)):
        ic.line([(70 - ln, y + 10), (68, y + 10)], 3.5, WHITE, line=2)


def sure_footed(ic):
    # Ice block with a spiked boot planted on it.
    ic.rect(14, 92, 114, 112, LIGHTBLUE, r=6, shine=True)
    ic.detail([(30, 100), (44, 100)], 2.5, WHITE)
    ic.detail([(80, 104), (96, 104)], 2.5, WHITE)
    for x in (38, 52, 66, 80):
        ic.poly([(x - 5, 86), (x + 5, 86), (x, 96)], STEEL, line=2.5)
    boot(ic, 58, 78, 1.5, fill=WOOD)


def immovable(ic):
    # An anchor.
    ic.arc(64, 58, 38, 20, 160, 10, STEEL)
    ic.line([(64, 30), (64, 98)], 11, STEEL)
    ic.poly([(26, 64), (20, 84), (38, 76)], STEEL, line=2.5)
    ic.poly([(102, 64), (108, 84), (90, 76)], STEEL, line=2.5)
    ic.rect(44, 40, 84, 50, DARKSTEEL, r=4)
    ic.arc(64, 22, 10, 0, 360, 6, DARKSTEEL)


def ghost(ic):
    pts = [(28, 60)]
    for i in range(0, 181, 10):
        a = math.radians(180 + i)
        pts.append((64 + 36 * math.cos(a), 60 + 40 * math.sin(a)))
    pts += [(100, 100), (91, 92), (82, 104), (73, 92), (64, 104), (55, 92), (46, 104), (37, 92), (28, 104)]
    ic.poly(pts, WHITE, shine=True)
    ic.ellipse(46, 48, 56, 62, INK, outline=False)
    ic.ellipse(72, 48, 82, 62, INK, outline=False)
    ic.ellipse(58, 68, 70, 80, INK, outline=False)


def recall(ic):
    flag(ic, 38, 106, 1.5, RED)
    curved_arrow(ic, 70, 70, 30, 10, 250, 8, YELLOW, head=22)


def swap(ic):
    curved_arrow(ic, 64, 64, 36, 200, 340, 9, WHITE, head=24)
    curved_arrow(ic, 64, 64, 36, 20, 160, 9, YELLOW, head=24)
    ic.circle(26, 76, 10, SKIN, shine=True)
    ic.circle(102, 52, 10, SKIN, shine=True)


def summon(ic):
    chef(ic, 86, 46, 0.9)
    # A big curved arrow pulling toward the left.
    arrow(ic, [(70, 104), (46, 104), (30, 92), (24, 74)], 9, YELLOW, head=24)
    sparkle(ic, 30, 36, 12, WHITE)


def quick_respawn(ic):
    stopwatch(ic, 52, 70, 34, WHITE, hand=60)
    arrow(ic, [(98, 104), (98, 46)], 11, GREEN, head=32)


def safety_net(ic):
    # A net slung between two posts, a chef dropping in.
    ic.rect(14, 60, 24, 110, DARKSTEEL, r=3)
    ic.rect(104, 60, 114, 110, DARKSTEEL, r=3)
    pts = [(20, 66)]
    for i in range(1, 20):
        t = i / 20
        pts.append((20 + 88 * t, 66 + 30 * math.sin(math.pi * t)))
    pts.append((108, 66))
    net = ic.m_poly(pts + [(108, 66)])
    ic.paint(net, (255, 240, 200, 255))
    for i in range(1, 6):
        x = 20 + 88 * i / 6
        ic.detail([(x, 68), (x, 66 + 30 * math.sin(math.pi * i / 6) - 2)], 2.2, DARKWOOD)
    ic.detail([(26, 78), (102, 78)], 2.2, DARKWOOD)
    chef(ic, 64, 30, 0.6)


def conveyor_rider(ic):
    ic.rect(10, 74, 118, 102, DARKSTEEL, r=14, shine=True)
    for x in (26, 50, 74, 98):
        ic.circle(x, 88, 7, STEEL)
    for x in (30, 54, 78):
        ic.poly([(x, 44), (x + 14, 56), (x, 68)], YELLOW, line=2.5)
    ic.detail([(16, 72), (112, 72)], 3, BLACK)


def big_chef(ic):
    chef(ic, 58, 44, 1.15)
    arrow(ic, [(96, 94), (110, 40)], 8, GREEN, head=24)
    arrow(ic, [(20, 96), (14, 72)], 6, GREEN, head=18)


def tiny_chef(ic):
    chef(ic, 52, 66, 0.6)
    arrow(ic, [(104, 24), (82, 54)], 8, YELLOW, head=22)
    arrow(ic, [(104, 108), (82, 84)], 8, YELLOW, head=22)


def hover(ic):
    # A boot with wings over a gap.
    ic.rect(10, 96, 40, 116, WOOD, r=4)
    ic.rect(88, 96, 118, 116, WOOD, r=4)
    wing(ic, 44, 60, 1.25, flip=True)
    boot(ic, 64, 74, 1.2, fill=RED)
    wing(ic, 74, 50, 1.25)


def grapple(ic):
    # A big three-pronged grappling hook, its rope swinging in from the bottom left.
    # Drawn upright round (cx, 64), then tilted to fly up and to the right.
    cx, tilt = 76, 35
    hook = ic.m_line([(cx, 96), (cx, 44)], 11)
    for side in (-1, 1):
        # Each outer prong curls down and out, then up to a point bent outwards (a barb).
        hook = union(hook, ic.m_arc(cx + side * 15, 44, 15, 0, 180, 10))
        hook = union(hook, ic.m_line([(cx + side * 30, 44), (cx + side * 30, 30), (cx + side * 36, 22)], 10))
    hook = union(hook, ic.m_line([(cx, 44), (cx, 16)], 10), ic.m_poly([(cx - 9, 20), (cx, 4), (cx + 9, 20)]))
    hook = union(hook, ic.m_circle(cx, 100, 11))
    a = math.radians(tilt)
    ring = (cx - 36 * math.sin(a), 64 + 36 * math.cos(a))
    ic.line([(8, 118), (24, 116), (42, 108), (ring[0], ring[1])], 8, WOOD, line=2.5)
    ic.paint(hook.rotate(-tilt, center=P(cx, 64), resample=Image.BICUBIC), STEEL, shine=True)
    ic.dot(ring[0], ring[1], 4.5)



# --- 2. Carrying and throwing ----------------------------------------------------------------

def pack_mule(ic):
    # A backpack with a tomato peeking out.
    tomato(ic, 64, 26, 13)
    ic.rect(30, 30, 98, 108, (196, 120, 62, 255), r=18, shine=True)
    ic.rect(30, 30, 98, 60, (166, 96, 48, 255), r=16)
    ic.rect(44, 72, 84, 100, (166, 96, 48, 255), r=8)
    ic.rect(58, 52, 70, 66, YELLOW, r=3)


def pocket(ic):
    # An apron (neck strap) with a tomato peeking out of its pocket.
    ic.arc(64, 30, 18, 180, 360, 5, WHITE, line=2.5)
    ic.poly([(40, 30), (88, 30), (92, 50), (110, 56), (104, 112), (24, 112), (18, 56), (36, 50)], WHITE, shine=True)
    tomato(ic, 64, 64, 18)
    ic.rect(34, 72, 94, 106, (220, 228, 240, 255), r=8)
    ic.detail([(40, 80), (88, 80)], 2.5, GREY)



def waiter(ic):
    plate(ic, 64, 96, 1.4)
    plate(ic, 64, 70, 1.25)
    ic.ellipse(46, 56, 82, 72, ORANGE, shine=True)
    ic.ellipse(50, 82, 78, 96, GREEN, shine=True)


def long_arms(ic):
    # A stretched arm with a glove reaching right.
    ic.line([(14, 86), (80, 64)], 14, WHITE)
    for x in (30, 44, 58):
        ic.detail([(x, 78 - (x - 14) * 0.33), (x + 4, 92 - (x - 14) * 0.33)], 2.5, GREY)
    glove(ic, 96, 56, 1.1, WHITE, angle=70)
    speed_lines(ic, 116, 30, 0.6, n=2, length=14)


def magnet(ic):
    ic.arc(64, 54, 30, 180, 360, 20, RED)
    ic.rect(24, 52, 44, 92, RED, shine=True)
    ic.rect(84, 52, 104, 92, RED)
    ic.rect(24, 84, 44, 104, STEEL)
    ic.rect(84, 84, 104, 104, STEEL)
    tomato(ic, 64, 100, 10)
    ic.arc(64, 96, 24, 200, 250, 3.5, YELLOW, line=1.5)
    ic.arc(64, 96, 24, 290, 340, 3.5, YELLOW, line=1.5)



def telekinesis(ic):
    glove(ic, 48, 84, 1.15, (210, 180, 255, 255))
    tomato(ic, 92, 34, 16)
    for (x, y, r) in ((70, 56, 6), (80, 70, 5), (60, 40, 4)):
        sparkle(ic, x, y, r * 1.8, YELLOW)


def bulk_buyer(ic):
    crate(ic, 44, 82, 1.1)
    crate(ic, 86, 64, 1.0)
    tomato(ic, 44, 56, 11)
    ic.circle(88, 32, 10, GREEN, shine=True)
    ic.circle(100, 102, 13, WHITE)
    ic.line([(100, 95), (100, 109)], 3.5, GREEN, line=0)
    ic.line([(93, 102), (107, 102)], 3.5, GREEN, line=0)


def catchers_mitt(ic):
    # A big brown baseball mitt, open, with a tomato flying into it.
    hand(ic, 52, 74, 1.55, WOOD)
    for x in (38, 47, 57, 66):
        ic.detail([(x, 46), (x, 58)], 2.5, DARKWOOD)
    ic.ellipse(40, 72, 66, 98, DARKWOOD, outline=False)
    tomato(ic, 98, 34, 15)
    for y in (24, 36, 48):
        ic.line([(116, y + 2), (124, y + 4)], 4, WHITE, line=2)


def sticky_fingers(ic):
    glove(ic, 60, 60, 1.35, WHITE)
    drop(ic, 36, 104, 0.9, GREEN)
    drop(ic, 84, 106, 0.8, GREEN)
    ic.ellipse(36, 44, 86, 64, GREEN, outline=False)


def strong_arm(ic):
    # A flexed arm (bicep bump, fist up) and a pot flying away.
    # Upper arm along the bottom with its bicep bulging up, forearm raised, fist on top.
    arm = ic.m_line([(4, 108), (64, 104)], 24)
    arm = union(arm, ic.m_ellipse(14, 70, 60, 106))
    arm = union(arm, ic.m_line([(64, 104), (84, 58)], 20))
    ic.paint(arm, SKIN, shine=True)
    ic.rect(68, 26, 106, 62, SKIN, r=12, shine=True)
    for x in (78, 88, 98):
        ic.detail([(x, 30), (x, 42)], 2.5, (200, 140, 100, 255))
    ic.detail([(70, 46), (84, 50)], 2.5, (200, 140, 100, 255))
    pot(ic, 32, 30, 0.48)
    for y in (22, 32):
        ic.line([(52, y), (60, y + 4)], 4, WHITE, line=2)



def homing_throw(ic):
    ic.circle(92, 42, 24, WHITE, shine=True)
    ic.circle(92, 42, 16, RED, outline=False)
    ic.circle(92, 42, 8, WHITE, outline=False)
    arrow(ic, [(14, 100), (40, 98), (58, 82), (72, 62)], 8, YELLOW, head=24)


def lob(ic):
    ic.rect(52, 70, 76, 112, DARKWOOD, r=3, shine=True)
    ic.rect(48, 64, 80, 74, STEEL, r=3)
    curved_arrow(ic, 64, 96, 48, 190, 335, 7, WHITE, head=22)
    tomato(ic, 64, 34, 12)


def assembly_line(ic):
    crate(ic, 30, 80, 0.75)
    tomato(ic, 30, 54, 10)
    arrow(ic, [(50, 80), (72, 80)], 9, YELLOW, head=24)
    chef(ic, 98, 60, 0.6)


def juggler(ic):
    ic.arc(64, 82, 40, 200, 340, 3.5, WHITE, line=1.5)
    ball(ic, 28, 70, 14, RED)
    ball(ic, 64, 34, 14, YELLOW)
    ball(ic, 100, 70, 14, GREEN)
    hand(ic, 64, 100, 0.65, WHITE)


def rebound(ic):
    # A thrown tomato bounces off the floor up onto a counter.
    ic.rect(8, 100, 70, 112, DARKSTEEL, r=4)
    counter(ic, 78, 72, 118, 112)
    arrow(ic, [(14, 24), (24, 52), (34, 80), (40, 96), (48, 80), (58, 64), (70, 56), (82, 54)], 7, WHITE, head=22)
    tomato(ic, 100, 58, 11)



def hand_off(ic):
    plate(ic, 64, 48, 1.0)
    ic.ellipse(50, 38, 78, 52, ORANGE, shine=True)
    glove(ic, 30, 84, 0.95, WHITE, angle=40)
    glove(ic, 98, 84, 0.95, YELLOW, angle=-40)


ICONS = {
    1: vault, 2: blink, 3: sprinter, 4: double_dash, 5: charge_dash, 6: momentum, 7: slipstream,
    8: sure_footed, 9: immovable, 10: ghost, 11: recall, 12: swap, 13: summon, 14: quick_respawn,
    15: safety_net, 16: conveyor_rider, 17: big_chef, 18: tiny_chef, 19: hover, 20: grapple,
    21: pack_mule, 22: pocket, 23: waiter, 24: long_arms, 25: magnet, 26: telekinesis, 27: bulk_buyer,
    28: catchers_mitt, 29: sticky_fingers, 30: strong_arm, 31: homing_throw, 32: lob, 33: assembly_line,
    34: juggler, 35: rebound, 36: hand_off,
}
