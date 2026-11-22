"""Icons for prep stations (37-47), cooking and fire (48-58), orders and score (59-71)."""
import math

from PIL import Image, ImageChops, ImageDraw

from iconkit import *  # noqa: F401,F403
from iconkit import P, W, SS, rot


# --- local helpers ----------------------------------------------------------------------------

def sc(cx, cy, s, pts):
    return [(cx + x * s, cy + y * s) for x, y in pts]


def union(*masks):
    m = masks[0]
    for x in masks[1:]:
        m = ImageChops.lighter(m, x)
    return m


def clip_above(ic, mask, y):
    return ImageChops.multiply(mask, ic.m_rect(-10, -10, 140, y))


def ring_rect(ic, x0, y0, x1, y1, r, t):
    outer = ic.m_rect(x0, y0, x1, y1, r)
    inner = ic.m_rect(x0 + t, y0 + t, x1 - t, y1 - t, max(r - t, 0))
    return ImageChops.subtract(outer, inner)


def rotate_mask(mask, angle, cx, cy):
    return mask.rotate(-angle, center=P(cx, cy), resample=Image.BICUBIC)


def knife2(ic, cx, cy, s, angle, blade=STEEL, handle=WOOD):
    b = [(-6, -26), (5, -26), (7, -22), (6, 6), (-6, 6)]
    h = [(-5, 6), (5, 6), (5, 26), (-5, 26)]
    ic.poly(rot(sc(cx, cy, s, b), angle, cx, cy), blade, shine=True)
    ic.poly(rot(sc(cx, cy, s, h), angle, cx, cy), handle)


def pan2(ic, cx, cy, s, body=BLACK, inside=(80, 80, 92, 255), handle=DARKWOOD):
    ic.poly(sc(cx, cy, s, [(16, -3), (42, -5), (42, 3), (16, 3)]), handle)
    ic.ellipse(cx - 22 * s, cy - 12 * s, cx + 22 * s, cy + 12 * s, body, shine=True)
    ic.ellipse(cx - 16 * s, cy - 8 * s, cx + 16 * s, cy + 8 * s, inside, outline=False)


def gear(ic, cx, cy, r, fill, teeth=8):
    pts = []
    for i in range(teeth * 4):
        a = math.radians(i * 360 / (teeth * 4))
        rr = r if (i % 4) in (0, 1) else r * 0.78
        pts.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
    ic.poly(pts, fill, shine=True)
    ic.circle(cx, cy, r * 0.3, (90, 96, 110, 255), line=2.5)


def bubble(ic, cx, cy, r):
    ic.circle(cx, cy, r, (196, 234, 255, 255), line=2.5)
    ic.dot(cx - r * 0.35, cy - r * 0.35, r * 0.26, WHITE)


def plus(ic, cx, cy, size, fill, thick=None):
    t = thick or size * 0.36
    m = union(ic.m_rect(cx - t / 2, cy - size / 2, cx + t / 2, cy + size / 2, t * 0.3),
              ic.m_rect(cx - size / 2, cy - t / 2, cx + size / 2, cy + t / 2, t * 0.3))
    ic.paint(m, fill)


def check(ic, pts, width, fill=GREEN):
    ic.paint(ic.m_line(pts, width), fill)


def wavy(ic, x, y0, y1, amp, width, fill=WHITE):
    pts = []
    n = 12
    for i in range(n + 1):
        t = i / n
        pts.append((x + amp * math.sin(t * math.pi * 2), y0 + (y1 - y0) * t))
    ic.line(pts, width, fill, line=2.5)


def clover(ic, cx, cy, s):
    ic.line([(cx, cy + 4 * s), (cx + 8 * s, cy + 26 * s)], 5 * s, DARKGREEN)
    m = union(*[ic.m_circle(cx + dx * s, cy + dy * s, 11 * s) for dx, dy in [(-9, -9), (9, -9), (-9, 9), (9, 9)]])
    ic.paint(m, GREEN, shine=True)
    ic.dot(cx, cy, 3 * s, DARKGREEN)


def snowflake(ic, cx, cy, r, fill=WHITE):
    m = None
    for k in range(3):
        a = math.radians(90 + k * 60)
        arm = ic.m_line([(cx - r * math.cos(a), cy - r * math.sin(a)), (cx + r * math.cos(a), cy + r * math.sin(a))], r * 0.24)
        m = arm if m is None else union(m, arm)
    for k in range(6):
        a = math.radians(90 + k * 60)
        tx, ty = cx + r * 0.62 * math.cos(a), cy + r * 0.62 * math.sin(a)
        for side in (-1, 1):
            b = a + side * math.radians(45) + math.pi
            m = union(m, ic.m_line([(tx, ty), (tx + r * 0.32 * math.cos(b - math.pi), ty + r * 0.32 * math.sin(b - math.pi))], r * 0.16))
    ic.paint(m, fill)


def ticket(ic, x0, y0, x1, y1, fill=WHITE, lines=LIGHTBLUE):
    ic.rect(x0, y0, x1, y1, fill, r=6, shine=True)
    ic.rect(x0 + 6, y0 + 6, x1 - 6, y0 + 22, lines, r=3, outline=False)
    w = x1 - x0
    for i, k in enumerate((0.8, 0.6, 0.7)):
        y = y0 + 34 + i * 13
        if y < y1 - 8:
            ic.detail([(x0 + 10, y), (x0 + 10 + (w - 20) * k, y)], 4, (190, 196, 210, 255))


def bowtie(ic, cx, cy, s, fill=BLACK):
    m = union(ic.m_poly(sc(cx, cy, s, [(-4, 0), (-26, -13), (-26, 13)])),
              ic.m_poly(sc(cx, cy, s, [(4, 0), (26, -13), (26, 13)])))
    ic.paint(m, fill, shine=True)
    ic.rect(cx - 6 * s, cy - 7 * s, cx + 6 * s, cy + 7 * s, fill, r=3 * s)


def snail(ic, cx, cy, s):
    ic.line([(cx + 20 * s, cy + 2 * s), (cx + 26 * s, cy - 14 * s)], 3.5 * s, (170, 220, 120, 255), line=2.5)
    ic.line([(cx + 26 * s, cy + 2 * s), (cx + 34 * s, cy - 10 * s)], 3.5 * s, (170, 220, 120, 255), line=2.5)
    ic.rect(cx - 30 * s, cy + 2 * s, cx + 34 * s, cy + 16 * s, (170, 220, 120, 255), r=7 * s, shine=True)
    ic.circle(cx - 4 * s, cy - 6 * s, 19 * s, WOOD, shine=True)
    pts = []
    for i in range(40):
        t = i / 39
        a = t * math.pi * 3.2
        r = 14 * s * (1 - t * 0.85)
        pts.append((cx - 4 * s + r * math.cos(a), cy - 6 * s + r * math.sin(a)))
    ic.detail(pts, 3 * s, DARKWOOD)


def die(ic, cx, cy, size, angle, fill, pip, pips):
    m = rotate_mask(ic.m_rect(cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2, size * 0.2), angle, cx, cy)
    ic.paint(m, fill, shine=True)
    q = size * 0.26
    spots = {1: [(0, 0)], 2: [(-q, -q), (q, q)], 3: [(-q, -q), (0, 0), (q, q)],
             4: [(-q, -q), (q, -q), (-q, q), (q, q)], 5: [(-q, -q), (q, -q), (0, 0), (-q, q), (q, q)],
             6: [(-q, -q), (q, -q), (-q, 0), (q, 0), (-q, q), (q, q)]}[pips]
    for x, y in rot([(cx + x, cy + y) for x, y in spots], angle, cx, cy):
        ic.dot(x, y, size * 0.09, pip)


def sink(ic, x0, y0, x1, y1):
    ic.rect(x0, y0, x1, y1, STEEL, r=7, shine=True)
    ic.rect(x0 + 7, y0 + 6, x1 - 7, y1 - 7, (110, 186, 240, 255), r=4, outline=False)


# --- 3. prep stations -------------------------------------------------------------------------

def multitask(ic):
    board(ic, 40, 92, 0.85)
    sink(ic, 62, 74, 114, 108)
    bubble(ic, 80, 88, 5)
    bubble(ic, 96, 94, 4)
    gear(ic, 48, 44, 27, YELLOW)
    gear(ic, 88, 34, 17, ORANGE, teeth=7)


def echo_knife(ic):
    board(ic, 64, 98, 1.2)
    knife2(ic, 44, 56, 1.15, -22, blade=(206, 232, 255, 255), handle=(214, 200, 236, 255))
    knife2(ic, 82, 54, 1.2, 18)
    ic.arc(62, 30, 12, 200, 340, 3.5, WHITE, line=2)
    ic.arc(62, 30, 22, 205, 335, 3.5, WHITE, line=2)


def power_chop(ic):
    board(ic, 58, 94, 1.3)
    knife2(ic, 52, 54, 1.3, -38)
    bolt(ic, 100, 40, 2.3)
    for (a, b) in [((14, 66), (22, 72)), ((96, 68), (106, 62))]:
        ic.line([a, b], 4.5, WHITE, line=2)


def lucky_knife(ic):
    knife2(ic, 46, 60, 1.45, -35)
    clover(ic, 88, 82, 1.15)
    sparkle(ic, 102, 36, 10)


def prep_cook(ic):
    crate(ic, 44, 86, 1.3)
    ic.circle(84, 42, 22, RED, shine=True)
    ic.circle(84, 42, 15, (255, 128, 106, 255), outline=False)
    for a in range(0, 360, 72):
        ic.dot(84 + 8 * math.cos(math.radians(a)), 42 + 8 * math.sin(math.radians(a)), 2.4, (255, 228, 160, 255))
    ic.circle(102, 76, 15, RED, shine=True)
    ic.circle(102, 76, 10, (255, 128, 106, 255), outline=False)
    sparkle(ic, 56, 26, 9)


def dash_chop(ic):
    board(ic, 74, 96, 1.0)
    knife2(ic, 78, 54, 1.3, 55)
    speed_lines(ic, 48, 56, 1.1, n=3, length=30)


def wash_burst(ic):
    plate(ic, 52, 96, 1.15)
    plate(ic, 52, 84, 1.15)
    plate(ic, 52, 72, 1.15)
    bubble(ic, 94, 40, 16)
    bubble(ic, 66, 30, 10)
    bubble(ic, 104, 76, 10)
    sparkle(ic, 28, 38, 12)


def dishwasher(ic):
    plate(ic, 60, 64, 1.35)
    sparkle(ic, 94, 30, 12)
    sparkle(ic, 30, 36, 8)
    curved_arrow(ic, 64, 70, 42, 20, 160, 8, BLUE, head=22)


def butler(ic):
    ic.rect(14, 72, 114, 110, STEEL, r=8)
    ic.rect(22, 78, 106, 104, (110, 186, 240, 255), r=4, outline=False)
    ic.ellipse(42, 36, 86, 98, WHITE, shine=True)
    ic.ellipse(52, 48, 76, 86, (226, 232, 242, 255), outline=False)
    ic.rect(14, 88, 114, 110, STEEL, r=8, shine=True)
    bowtie(ic, 64, 22, 1.0)


def plate_conjurer(ic):
    plate(ic, 52, 88, 1.3)
    ic.line([(110, 110), (88, 54)], 8, BLACK)
    ic.detail([(92, 64), (88, 54)], 4, WHITE)
    star(ic, 86, 44, 14)
    sparkle(ic, 40, 44, 11)
    sparkle(ic, 62, 22, 8)


def spare_pan(ic):
    pan2(ic, 40, 52, 1.05, body=(150, 158, 178, 255), inside=(190, 198, 214, 255), handle=(176, 150, 120, 255))
    pan2(ic, 52, 84, 1.25)
    plus(ic, 102, 30, 26, WHITE)


# --- 4. cooking and fire ----------------------------------------------------------------------

def hot_hands(ic):
    hand(ic, 64, 96, 1.05, WHITE)
    pot(ic, 64, 40, 0.95)
    flame(ic, 22, 88, 0.66)
    flame(ic, 106, 88, 0.66)
    flame(ic, 64, 60, 0.42)


def cold_hands(ic):
    hand(ic, 64, 96, 1.05, LIGHTBLUE)
    pot(ic, 64, 40, 0.95)
    snowflake(ic, 104, 26, 16)
    snowflake(ic, 22, 88, 11)


def rescue(ic):
    cloud(ic, 34, 34, 0.75, GREY)
    pot(ic, 56, 74, 1.25)
    ic.circle(96, 38, 18, WHITE, shine=True)
    plus(ic, 96, 38, 22, RED)


def overclock(ic):
    ic.rect(18, 82, 110, 112, DARKSTEEL, r=6, shine=True)
    ic.dot(36, 98, 4, WHITE)
    ic.dot(92, 98, 4, WHITE)
    ic.circle(64, 58, 38, WHITE, shine=True)
    ic.paint(ic.m_arc(64, 58, 28, 150, 228, 8), GREEN, outline=False)
    ic.paint(ic.m_arc(64, 58, 28, 234, 300, 8), YELLOW, outline=False)
    ic.paint(ic.m_arc(64, 58, 28, 306, 390, 8), RED, outline=False)
    a = math.radians(-18)
    ic.detail([(64, 58), (64 + 30 * math.cos(a), 58 + 30 * math.sin(a))], 6)
    ic.dot(64, 58, 6)
    speed_lines(ic, 20, 40, 0.8, n=2, length=12)


def sizzle_master(ic):
    pan2(ic, 48, 84, 1.3)
    for x in (34, 50, 66):
        wavy(ic, x, 30, 62, 4, 5)
    bolt(ic, 100, 40, 2.1)


def steady_mixer(ic):
    rim = ic.m_ellipse(16, 48, 104, 72)
    body = ImageChops.multiply(ic.m_ellipse(16, 20, 104, 108), ic.m_rect(0, 60, 128, 128))
    ic.paint(union(rim, body), BLUE, shine=True)
    ic.ellipse(24, 52, 96, 68, CREAM, outline=False)
    loop = ImageChops.subtract(ic.m_ellipse(60, 30, 84, 74), ic.m_ellipse(66, 36, 78, 68))
    loop = rotate_mask(loop, 25, 72, 52)
    ic.line([(80, 34), (96, 10)], 7, DARKWOOD)
    ic.paint(loop, STEEL)
    ic.circle(100, 96, 16, GREEN, shine=True)
    check(ic, [(92, 96), (98, 102), (108, 90)], 5, WHITE)


def fireproof(ic):
    shield(ic, 64, 62, 1.65, BLUE)
    flame(ic, 64, 60, 1.0)


def fire_marshal(ic):
    ic.ellipse(8, 66, 120, 92, DARKRED)
    dome = clip_above(ic, ic.m_ellipse(26, 20, 102, 108), 76)
    ic.paint(dome, RED, shine=True)
    ic.detail([(64, 22), (64, 74)], 4, DARKRED)
    shield(ic, 64, 52, 0.62, GOLD)
    star(ic, 64, 50, 7, WHITE, shine=False)


def fire_blast(ic):
    ic.poly(star_pts(88, 60, 36, 22, 9), WHITE, shine=True)
    ic.line([(42, 30), (60, 26), (70, 42)], 6, BLACK)
    ic.rect(20, 36, 54, 114, RED, r=12, shine=True)
    ic.rect(28, 22, 46, 38, DARKSTEEL, r=3)
    ic.rect(26, 60, 48, 80, WHITE, r=3, outline=False)
    sparkle(ic, 104, 24, 9, LIGHTBLUE)


def hose_hands(ic):
    hand(ic, 38, 76, 1.0, WHITE, angle=90)
    ic.line([(62, 70), (82, 62), (104, 56)], 11, LIGHTBLUE)
    drop(ic, 100, 32, 0.9)
    drop(ic, 108, 88, 0.8)
    drop(ic, 84, 96, 0.7)


def slow_spread(ic):
    flame(ic, 42, 52, 1.35)
    snail(ic, 80, 90, 0.95)


# --- 5. orders and score ----------------------------------------------------------------------

def charmer(ic):
    coin(ic, 50, 76, 30)
    heart(ic, 88, 44, 1.6, PINK)


def maitre_d(ic):
    dome = clip_above(ic, ic.m_ellipse(20, 40, 104, 128), 92)
    ic.circle(62, 40, 7, STEEL)
    ic.paint(dome, STEEL, shine=True)
    ic.ellipse(10, 86, 114, 104, DARKSTEEL)
    clock(ic, 96, 36, 22, WHITE, hands=(0, 90))
    plus(ic, 118 - 6, 62, 14, GREEN)


def clutch(ic):
    ic.rect(48, 14, 64, 26, DARKSTEEL, r=3)
    ic.circle(56, 64, 40, WHITE, shine=True)
    m = Image.new("L", (W, W), 0)
    ImageDraw.Draw(m).pieslice([P(56 - 31, 64 - 31), P(56 + 31, 64 + 31)], 180, 270, fill=255)
    ic.paint(m, RED, outline=False)
    a = math.radians(250)
    ic.detail([(56, 64), (56 + 30 * math.cos(a), 64 + 30 * math.sin(a))], 6)
    ic.dot(56, 64, 5)
    star(ic, 100, 96, 20)


def combo_keeper(ic):
    l1 = rotate_mask(ring_rect(ic, 16, 56, 74, 88, 16, 9), -40, 45, 72)
    l2 = rotate_mask(ring_rect(ic, 54, 36, 112, 68, 16, 9), -40, 83, 52)
    ic.paint(l1, GOLD, shine=True)
    ic.paint(l2, GOLD, shine=True)
    # Lay link 1's top bar back over link 2 so they look hooked together.
    over = ImageChops.multiply(l1, ic.m_rect(56, 50, 76, 66))
    ic.paint(over, GOLD, line=2)
    sparkle(ic, 30, 30, 11)
    sparkle(ic, 102, 98, 9)


def second_chance(ic):
    ticket(ic, 18, 16, 80, 106)
    heart(ic, 88, 82, 1.55, RED)


def gambler(ic):
    die(ic, 46, 78, 50, -14, WHITE, BLACK, 3)
    die(ic, 88, 44, 42, 18, RED, WHITE, 5)


def fresh_delivery(ic):
    plate(ic, 60, 94, 1.3)
    ic.ellipse(32, 62, 88, 100, ORANGE, shine=True)
    for x in (46, 60, 74):
        wavy(ic, x, 24, 54, 3.5, 4.5)
    leaf = rotate_mask(ic.m_ellipse(88, 26, 116, 66), 35, 102, 46)
    ic.paint(leaf, GREEN, shine=True)
    ic.detail([(96, 56), (108, 36)], 2.5, DARKGREEN)


def reroll(ic):
    ticket(ic, 36, 28, 92, 100)
    curved_arrow(ic, 64, 64, 50, 200, 315, 8, BLUE, head=22)
    curved_arrow(ic, 64, 64, 50, 20, 135, 8, BLUE, head=22)


def time_out(ic):
    ic.rect(56, 10, 72, 22, DARKSTEEL, r=3)
    ic.line([(96, 28), (104, 20)], 6, DARKSTEEL)
    ic.circle(64, 68, 44, WHITE, shine=True)
    ic.rect(46, 48, 58, 88, BLUE, r=4)
    ic.rect(70, 48, 82, 88, BLUE, r=4)


def close_enough(ic):
    plate(ic, 52, 84, 1.3)
    ic.ellipse(32, 70, 72, 92, (240, 170, 80, 255), outline=False)
    ic.line([(44, 56), (64, 76), (106, 26)], 13, GREEN)


def fortune_teller(ic):
    ic.poly([(38, 112), (90, 112), (80, 90), (48, 90)], WOOD, shine=True)
    ic.circle(64, 56, 38, PURPLE, shine=True)
    ic.circle(70, 64, 22, (186, 146, 240, 255), outline=False)
    sparkle(ic, 56, 52, 10)
    sparkle(ic, 78, 70, 6)
    sparkle(ic, 104, 18, 9)


def bounty_hunter(ic):
    ticket(ic, 16, 18, 76, 100, lines=GOLD)
    ic.circle(86, 78, 30, RED, shine=True)
    ic.circle(86, 78, 21, WHITE, outline=False)
    ic.circle(86, 78, 12, RED, outline=False)
    for a, b in [((86, 40), (86, 54)), ((86, 102), (86, 116)), ((48, 78), (62, 78)), ((110, 78), (124, 78))]:
        ic.line([a, b], 5, BLACK, line=2)


def golden_hour(ic):
    ic.poly(star_pts(64, 64, 58, 44, 12), ORANGE)
    ic.circle(64, 64, 42, YELLOW, shine=True)
    ic.rect(38, 14, 90, 26, WOOD, r=4)
    ic.rect(38, 102, 90, 114, WOOD, r=4)
    ic.poly([(44, 26), (84, 26), (68, 64), (84, 102), (44, 102), (60, 64)], LIGHTBLUE, shine=True)
    ic.poly([(52, 34), (76, 34), (64, 56)], GOLD, outline=False)
    ic.poly([(64, 70), (80, 98), (48, 98)], GOLD, outline=False)


ICONS = {
    37: multitask, 38: echo_knife, 39: power_chop, 40: lucky_knife, 41: prep_cook, 42: dash_chop,
    43: wash_burst, 44: dishwasher, 45: butler, 46: plate_conjurer, 47: spare_pan,
    48: hot_hands, 49: cold_hands, 50: rescue, 51: overclock, 52: sizzle_master, 53: steady_mixer,
    54: fireproof, 55: fire_marshal, 56: fire_blast, 57: hose_hands, 58: slow_spread,
    59: charmer, 60: maitre_d, 61: clutch, 62: combo_keeper, 63: second_chance, 64: gambler,
    65: fresh_delivery, 66: reroll, 67: time_out, 68: close_enough, 69: fortune_teller,
    70: bounty_hunter, 71: golden_hour,
}
