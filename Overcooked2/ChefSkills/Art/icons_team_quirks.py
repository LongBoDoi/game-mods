"""Icons for Team (72-78), Temporary buffs (79-89), Hazards (90-94), Versus (95-100) and Quirks (101-112)."""
import math

from PIL import Image, ImageChops

from iconkit import *


# --- local helpers --------------------------------------------------------------------------

def u(*masks):
    m = masks[0]
    for x in masks[1:]:
        m = ImageChops.lighter(m, x)
    return m


def cut(a, b):
    return ImageChops.subtract(a, b)


def inter(a, b):
    return ImageChops.multiply(a, b)


def shift(mask, dx, dy):
    out = Image.new("L", mask.size, 0)
    out.paste(mask, (int(dx * SS), int(dy * SS)))
    return out


def petal_pts(bx, by, length, width, angle):
    a = math.radians(angle)
    dx, dy = math.cos(a), math.sin(a)
    nx, ny = -dy, dx
    side1, side2 = [], []
    for i in range(25):
        t = i / 24
        w = width * math.sin(math.pi * t) ** 0.8
        px, py = bx + dx * length * t, by + dy * length * t
        side1.append((px + nx * w, py + ny * w))
        side2.append((px - nx * w, py - ny * w))
    return side1 + side2[::-1]


def heart_pts(cx, cy, s):
    pts = []
    for i in range(64):
        t = math.radians(i * 360 / 64)
        x = 16 * math.sin(t) ** 3
        y = -(13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t))
        pts.append((cx + x * s, cy + y * s))
    return pts


def spiral_pts(cx, cy, r0, r1, turns, start=0):
    pts = []
    n = int(turns * 40)
    for i in range(n + 1):
        t = i / n
        a = math.radians(start + 360 * turns * t)
        r = r0 + (r1 - r0) * t
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def wind_line(ic, x0, y, length, r, width=8, fill=WHITE):
    xe = x0 + length
    pts = [(x0, y), (xe, y)]
    for i in range(1, 31):
        a = math.radians(90 - 270 * i / 30)
        pts.append((xe + r * math.cos(a), y - r + r * math.sin(a)))
    ic.line(pts, width, fill, line=2.5)


def tomato(ic, cx, cy, r):
    ic.circle(cx, cy, r, RED, shine=True)
    ic.poly(star_pts(cx, cy - r * 0.85, r * 0.45, r * 0.2, 5), GREEN, line=2.5)


def germ(ic, cx, cy, r):
    ic.poly(star_pts(cx, cy, r, r * 0.78, 10), GREEN, shine=True)
    for ex in (-0.32, 0.32):
        ic.dot(cx + ex * r, cy - 0.1 * r, r * 0.24, WHITE)
        ic.dot(cx + ex * r, cy - 0.05 * r, r * 0.12, INK)
    ic.detail([(cx - 0.25 * r, cy + 0.38 * r), (cx + 0.25 * r, cy + 0.32 * r)], max(2.0, r * 0.1))


def barrel(ic, cx, cy, half_len, half_w, angle, fill, shine=True):
    pts = [(cx - half_len, cy - half_w), (cx + half_len, cy - half_w * 1.15),
           (cx + half_len, cy + half_w * 1.15), (cx - half_len, cy + half_w)]
    ic.poly(rot(pts, angle, cx, cy), fill, shine=shine)
    a = math.radians(angle)
    mx, my = cx + half_len * math.cos(a), cy + half_len * math.sin(a)
    ring = [(mx - 5, my - half_w * 1.4), (mx + 5, my - half_w * 1.4), (mx + 5, my + half_w * 1.4), (mx - 5, my + half_w * 1.4)]
    ic.poly(rot(ring, angle, mx, my), fill)
    return mx, my


# --- 6. Team support ------------------------------------------------------------------------

def rally_cry(ic):
    ic.rect(38, 72, 50, 98, BLACK, r=4)
    ic.rect(12, 54, 28, 74, DARKSTEEL, r=4)
    ic.poly([(24, 52), (72, 26), (72, 102), (24, 76)], RED, shine=True)
    ic.ellipse(64, 24, 84, 104, WHITE)
    ic.ellipse(69, 36, 80, 92, (226, 232, 242, 255), outline=False)
    ic.arc(84, 64, 16, -50, 50, 6, WHITE, line=2.5)
    ic.arc(84, 64, 29, -45, 45, 6, WHITE, line=2.5)


def inspiring(ic):
    for i in range(8):
        a = math.radians(i * 45 - 90 + 22.5)
        ic.line([(64 + 40 * math.cos(a), 66 + 40 * math.sin(a)), (64 + 51 * math.cos(a), 66 + 51 * math.sin(a))],
                7, WHITE, line=2.5)
    star(ic, 64, 68, 38, YELLOW, inner=0.48)


def medic(ic):
    ic.arc(64, 32, 15, 180, 360, 7, DARKSTEEL)
    ic.rect(16, 32, 112, 104, WHITE, r=14, shine=True)
    ic.paint(u(ic.m_rect(53, 44, 75, 94, 3), ic.m_rect(39, 58, 89, 80, 3)), RED, line=2.5)


def bodyguard(ic):
    shield(ic, 64, 60, 1.95, BLUE)
    ic.paint(ic.m_poly([(64, 26), (98, 26), (98, 60), (90, 82), (64, 98)]), (110, 180, 245, 255), outline=False)
    star(ic, 64, 60, 21, YELLOW)


def battery(ic):
    ic.rect(100, 52, 112, 76, DARKSTEEL, r=3)
    ic.rect(14, 36, 102, 92, WHITE, r=10, shine=True)
    ic.rect(22, 44, 64, 84, GREEN, r=5, outline=False)
    bolt(ic, 62, 64, 2.4, YELLOW)


def buddy_system(ic):
    chef(ic, 40, 50, 0.95, body=WHITE)
    chef(ic, 88, 50, 0.95, body=LIGHTBLUE)
    heart(ic, 64, 99, 0.95, RED)


def quartermaster(ic):
    crate(ic, 50, 78, 1.5)
    ic.paint(u(ic.m_rect(86, 18, 99, 60, 3), ic.m_rect(72, 32, 113, 46, 3)), GREEN, line=2.5)


# --- 7. Temporary buffs ---------------------------------------------------------------------

def frenzy(ic):
    bolt(ic, 46, 64, 3.0, ORANGE, angle=-22)
    bolt(ic, 82, 64, 3.0, YELLOW, angle=22)


def overdrive(ic):
    ic.circle(64, 72, 46, WHITE, shine=True)
    for start, colour in ((150, GREEN), (210, YELLOW), (270, ORANGE), (330, RED)):
        ic.paint(ic.m_arc(64, 72, 34, start, start + 60, 9), colour, outline=False)
    a = math.radians(340)
    ic.detail([(64, 72), (64 + 32 * math.cos(a), 72 + 32 * math.sin(a))], 6)
    ic.dot(64, 72, 7)
    speed_lines(ic, 22, 102, 0.8, n=2, length=12)


def second_wind(ic):
    wind_line(ic, 12, 42, 58, 13)
    wind_line(ic, 26, 76, 62, 12)
    heart(ic, 42, 100, 1.0, RED)


def adrenaline(ic):
    heart(ic, 64, 62, 2.8, RED)
    bolt(ic, 64, 66, 2.1, YELLOW)


def zen(ic):
    ic.ellipse(20, 92, 108, 114, GREEN)
    ic.poly(petal_pts(64, 98, 46, 12, -165), PINK, shine=True)
    ic.poly(petal_pts(64, 98, 46, 12, -15), PINK, shine=True)
    ic.poly(petal_pts(64, 98, 56, 15, -128), PINK, shine=True)
    ic.poly(petal_pts(64, 98, 56, 15, -52), PINK, shine=True)
    ic.poly(petal_pts(64, 98, 70, 17, -90), (255, 200, 225, 255), shine=True)


def coffee_run(ic):
    for x in (50, 66, 80):
        pts = [(x + 5 * math.sin(i / 10 * math.pi * 1.6), 36 - i * 2.4) for i in range(11)]
        ic.line(pts, 5, WHITE, line=2.5)
    ic.arc(86, 74, 14, -90, 90, 7, WHITE)
    ic.poly([(30, 48), (92, 48), (85, 106), (37, 106)], WHITE, shine=True)
    ic.rect(32, 66, 90, 84, (186, 110, 60, 255), outline=False)
    ic.ellipse(28, 40, 94, 58, WHITE)
    ic.ellipse(34, 44, 88, 55, (110, 66, 40, 255), outline=False)


def glass_cannon(ic):
    mx, my = barrel(ic, 58, 60, 36, 15, -32, LIGHTBLUE)
    ic.detail([(40, 64), (50, 56), (46, 50), (58, 44), (62, 50), (72, 42)], 3, WHITE)
    ic.circle(46, 88, 18, WOOD, shine=True)
    ic.dot(46, 88, 5, DARKWOOD)
    sparkle(ic, mx + 14, my - 10, 13, WHITE)


def slow_starter(ic):
    body = (244, 206, 126, 255)
    ic.line([(90, 82), (94, 52)], 4, body, line=2.5)
    ic.line([(100, 82), (110, 56)], 4, body, line=2.5)
    ic.dot(94, 52, 5, INK)
    ic.dot(110, 56, 5, INK)
    ic.rect(14, 86, 104, 104, body, r=9)
    ic.circle(96, 82, 13, body, shine=True)
    ic.dot(100, 80, 2.5)
    ic.circle(52, 66, 30, ORANGE, shine=True)
    ic.detail(spiral_pts(52, 66, 3, 20, 2.0, 180), 4, DARKWOOD)
    arrow(ic, [(24, 46), (24, 22)], 7, GREEN, head=20)


def under_pressure(ic):
    pot(ic, 58, 78, 1.35)
    ic.line([(78, 56), (90, 46)], 6, DARKSTEEL)
    ic.circle(92, 38, 19, WHITE, shine=True)
    a = math.radians(-20)
    ic.detail([(92, 38), (92 + 14 * math.cos(a), 38 + 14 * math.sin(a))], 4, RED)
    ic.dot(92, 38, 3.5)
    cloud(ic, 36, 34, 0.75)


def lucky_charm(ic):
    ic.line([(64, 66), (70, 92), (82, 110)], 7, DARKGREEN)
    leaves = [ic.m_circle(64 + dx * 19, 58 + dy * 19, 20) for dx, dy in ((1, 1), (1, -1), (-1, 1), (-1, -1))]
    ic.paint(u(ic.m_circle(64, 58, 16), *leaves), GREEN, shine=True)
    for dx, dy in ((1, 1), (1, -1), (-1, 1), (-1, -1)):
        ic.detail([(64, 58), (64 + dx * 26, 58 + dy * 26)], 3, DARKGREEN)


def hot_streak(ic):
    flame(ic, 52, 66, 2.0)
    arrow(ic, [(98, 104), (98, 44)], 11, GREEN, head=28)


# --- 8. Level hazards and vehicles ----------------------------------------------------------

def portal_pro(ic):
    ic.ellipse(12, 22, 100, 106, PURPLE, shine=True)
    ic.ellipse(24, 34, 88, 94, (110, 70, 200, 255), outline=False)
    ic.detail(spiral_pts(56, 64, 3, 26, 1.8, 0), 5, (200, 170, 255, 255))
    arrow(ic, [(58, 64), (100, 64)], 10, WHITE, head=26)


def cannoneer(ic):
    barrel(ic, 54, 72, 32, 14, -40, BLACK)
    ic.circle(46, 92, 18, WOOD, shine=True)
    ic.dot(46, 92, 5, DARKWOOD)
    cloud(ic, 84, 52, 0.55, (230, 230, 236, 255))
    chef_hat(ic, 96, 38, 0.72)


def driver(ic):
    spokes = u(ic.m_line([(64, 64), (26, 56)], 10), ic.m_line([(64, 64), (102, 56)], 10), ic.m_line([(64, 64), (64, 102)], 10))
    ic.paint(spokes, STEEL)
    ic.arc(64, 64, 41, 0, 360, 13, BLACK)
    ic.circle(64, 64, 14, RED, shine=True)


def weatherproof(ic):
    ic.line([(64, 56), (64, 96)], 6, DARKSTEEL)
    ic.arc(56, 96, 8, 0, 180, 6, DARKSTEEL)
    canopy = inter(ic.m_ellipse(12, 18, 116, 102), ic.m_rect(0, 0, 128, 60))
    bumps = u(*[ic.m_circle(x, 60, 13.5) for x in (25, 51, 77, 103)])
    ic.paint(cut(canopy, bumps), RED, shine=True)
    for x in (38, 64, 90):
        ic.detail([(64, 22), (x, 52)], 2.5)
    drop(ic, 22, 96, 0.75)
    drop(ic, 104, 96, 0.75)


def lifeguard(ic):
    ic.arc(64, 64, 34, 0, 360, 22, WHITE, shine=True)
    for start in (25, 115, 205, 295):
        ic.paint(ic.m_arc(64, 64, 34, start, start + 40, 20), RED, outline=False)
    ic.detail([(64 + 22 * math.cos(math.radians(a)), 64 + 22 * math.sin(math.radians(a))) for a in range(0, 361, 10)], 2)


# --- 9. Versus / party ----------------------------------------------------------------------

def shove(ic):
    ic.poly(star_pts(96, 64, 21, 11, 8), YELLOW, shine=True)
    speed_lines(ic, 28, 64, 0.9, n=3, length=14)
    hand(ic, 54, 64, 1.35, WHITE, angle=90)


def pickpocket(ic):
    tomato(ic, 84, 44, 22)
    hand(ic, 46, 82, 1.3, WHITE, angle=45)
    speed_lines(ic, 24, 102, 0.7, n=2, length=10)


def grease_trail(ic):
    grease = (214, 170, 60, 255)
    puddle = u(ic.m_ellipse(14, 76, 92, 110), ic.m_ellipse(50, 68, 114, 102), ic.m_ellipse(30, 64, 74, 90))
    ic.paint(puddle, grease, shine=True)
    ic.detail([(34, 90), (54, 84), (74, 88)], 3, WHITE)
    drop(ic, 66, 36, 1.3, grease)
    curved_arrow(ic, 30, 54, 16, 160, 330, 5, WHITE, head=14)


def sneeze_bomb(ic):
    cloud(ic, 56, 56, 1.8, (196, 238, 150, 255))
    ic.arc(42, 56, 6, 200, 340, 3, INK, outline=False)
    ic.arc(66, 56, 6, 200, 340, 3, INK, outline=False)
    ic.dot(54, 70, 4)
    drop(ic, 100, 82, 0.6)
    drop(ic, 84, 100, 0.55)
    drop(ic, 108, 104, 0.5)


def hex_(ic):
    ic.circle(64, 64, 40, PURPLE, shine=True)
    ic.detail(spiral_pts(64, 64, 2, 30, 2.6, 0), 5, (230, 210, 255, 255))
    sparkle(ic, 102, 24, 13, YELLOW)
    sparkle(ic, 24, 104, 10, YELLOW)


def decoy_order(ic):
    ic.rect(24, 14, 104, 110, CREAM, r=8, shine=True)
    ic.paint(ic.m_rect(24, 14, 104, 34, 8), RED, outline=False)
    pts = [(64 + 15 * math.cos(math.radians(a)), 58 + 15 * math.sin(math.radians(a))) for a in range(190, 411, 10)]
    pts += [(64, 78), (64, 84)]
    ic.line(pts, 9, BLUE, line=2.5)
    ic.circle(64, 97, 5.5, BLUE, line=2.5)


# --- 10. Quirks -----------------------------------------------------------------------------

def heavy_boots(ic):
    leather = (150, 92, 50, 255)
    boot_m = u(ic.m_rect(44, 26, 74, 84, 6), ic.m_rect(44, 62, 106, 94, 14))
    ic.paint(boot_m, leather, shine=True)
    ic.rect(40, 88, 110, 102, BLACK, r=4)
    ic.rect(42, 22, 76, 32, (110, 66, 36, 255), r=3)
    for y in (42, 54, 66):
        ic.detail([(52, y), (66, y + 4)], 3, CREAM)
    for x, y in ((38, 86), (31, 92)):
        ic.ellipse(x - 5, y - 3.5, x + 5, y + 3.5, STEEL, line=2)
    ic.circle(20, 101, 13, BLACK, shine=True)
    ic.line([(20, 112), (8, 116)], 3, WHITE, line=1.5)


def clumsy(ic):
    plate_m = ic.m_ellipse(14, 52, 114, 102)
    left = inter(plate_m, ic.m_poly([(0, 0), (60, 0), (66, 60), (56, 70), (70, 84), (62, 128), (0, 128)]))
    right = cut(plate_m, left)
    ic.paint(shift(left, -6, 6), WHITE, shine=True)
    ic.paint(shift(right, 6, -2), WHITE, shine=True)
    sparkle(ic, 64, 30, 13, YELLOW)
    ic.line([(30, 34), (40, 42)], 5, WHITE, line=2.5)
    ic.line([(98, 34), (88, 42)], 5, WHITE, line=2.5)


def no_badge(ic, cx, cy, r):
    """A white disc with a red 'not allowed' sign: the quirk's 'can't' mark, clear of the object."""
    ic.circle(cx, cy, r, WHITE)
    no_sign(ic, cx, cy, r * 0.74)


def weak_arm(ic):
    # A glove with a tomato, the throw arc it can't make, and the 'can't' badge.
    hand(ic, 44, 80, 1.25, WHITE)
    tomato(ic, 44, 30, 13)
    arrow(ic, [(62, 26), (82, 18), (98, 26), (106, 42)], 7, WHITE, head=18)
    no_badge(ic, 94, 94, 22)


def butterfingers(ic):
    hand(ic, 42, 82, 1.3, WHITE)
    tomato(ic, 92, 40, 18)
    sparkle(ic, 70, 56, 10, YELLOW)
    ic.line([(108, 70), (112, 82)], 4, WHITE, line=2.5)
    ic.line([(96, 72), (98, 84)], 4, WHITE, line=2.5)


def pyrophobe(ic):
    # A fire extinguisher, and the 'can't' badge beside it.
    ic.line([(62, 22), (84, 22), (92, 38)], 6, BLACK)
    ic.rect(30, 30, 70, 112, RED, r=13, shine=True)
    ic.rect(40, 14, 60, 32, BLACK, r=3)
    ic.rect(34, 58, 66, 74, WHITE, r=3, outline=False)
    no_badge(ic, 94, 92, 23)


def germaphobe(ic):
    plate(ic, 64, 88, 1.4)
    germ(ic, 46, 52, 19)
    germ(ic, 86, 46, 15)
    germ(ic, 76, 84, 11)


def vegetarian(ic):
    ic.poly(petal_pts(18, 112, 86, 20, -62), GREEN, shine=True)
    ic.detail([(22, 106), (54, 44)], 3, DARKGREEN)
    meat = (210, 120, 74, 255)
    ic.line([(92, 84), (106, 100)], 8, WHITE)
    ic.paint(u(ic.m_circle(104, 104, 6), ic.m_circle(110, 98, 6)), WHITE, line=2)
    ic.paint(u(ic.m_circle(78, 54, 22), ic.m_poly([(62, 64), (88, 72), (96, 88), (88, 92)])), meat, shine=True)
    ic.line([(62, 38), (96, 72)], 7, RED, line=2.5)
    ic.line([(96, 38), (62, 72)], 7, RED, line=2.5)


def sleepy(ic):
    ic.rect(14, 66, 114, 108, WHITE, r=18, shine=True)
    ic.detail([(30, 87), (98, 87)], 2.5, (210, 216, 230, 255))
    moon = cut(ic.m_circle(70, 38, 24), ic.m_circle(84, 28, 20))
    ic.paint(moon, YELLOW, shine=True)
    sparkle(ic, 30, 34, 10, WHITE)
    sparkle(ic, 106, 50, 8, WHITE)


def fragile(ic):
    h = ic.m_poly(heart_pts(64, 62, 2.9))
    zig = ic.m_poly([(0, 0), (66, 0), (56, 46), (72, 62), (58, 80), (68, 128), (0, 128)])
    left = inter(h, zig)
    right = cut(h, left)
    ic.paint(shift(left, -5, 2), RED, shine=True)
    ic.paint(shift(right, 5, -2), RED, shine=True)


def show_off(ic):
    ic.line([(12, 52), (22, 58)], 5, BLACK)
    ic.line([(116, 52), (106, 58)], 5, BLACK)
    ic.line([(54, 58), (74, 58)], 6, BLACK)
    for x0 in (18, 70):
        ic.paint(ic.m_poly([(x0, 52), (x0 + 40, 52), (x0 + 36, 78), (x0 + 20, 84), (x0 + 4, 78)]), BLACK, shine=True)
        ic.detail([(x0 + 10, 58), (x0 + 18, 58)], 3, (120, 130, 150, 255))
    sparkle(ic, 98, 28, 14, YELLOW)
    sparkle(ic, 28, 100, 10, YELLOW)


def tunnel_vision(ic):
    ic.poly([(10, 28), (26, 40), (26, 88), (10, 100)], DARKWOOD, shine=True)
    ic.poly([(118, 28), (102, 40), (102, 88), (118, 100)], DARKWOOD, shine=True)
    eye = inter(ic.m_circle(64, 64 + 32, 48), ic.m_circle(64, 64 - 32, 48))
    ic.paint(eye, WHITE, shine=True)
    ic.circle(64, 64, 14, BLUE, line=2.5)
    ic.dot(64, 64, 6.5)
    ic.dot(59, 59, 3, WHITE)


def lone_wolf(ic):
    grey = (150, 156, 176, 255)
    head = [(28, 14), (50, 38), (64, 34), (78, 38), (100, 14), (106, 58), (92, 82), (64, 110), (36, 82), (22, 58)]
    ic.poly(head, grey, shine=True)
    ic.paint(ic.m_poly([(34, 26), (46, 40), (38, 48)]), (110, 114, 134, 255), outline=False)
    ic.paint(ic.m_poly([(94, 26), (82, 40), (90, 48)]), (110, 114, 134, 255), outline=False)
    ic.paint(ic.m_poly([(48, 66), (64, 60), (80, 66), (86, 84), (64, 106), (42, 84)]), CREAM, outline=False)
    ic.paint(ic.m_poly([(38, 54), (54, 58), (52, 62), (40, 60)]), YELLOW, line=2)
    ic.paint(ic.m_poly([(90, 54), (74, 58), (76, 62), (88, 60)]), YELLOW, line=2)
    ic.ellipse(55, 80, 73, 92, BLACK, line=2)


ICONS = {
    72: rally_cry, 73: inspiring, 74: medic, 75: bodyguard, 76: battery, 77: buddy_system, 78: quartermaster,
    79: frenzy, 80: overdrive, 81: second_wind, 82: adrenaline, 83: zen, 84: coffee_run, 85: glass_cannon,
    86: slow_starter, 87: under_pressure, 88: lucky_charm, 89: hot_streak,
    90: portal_pro, 91: cannoneer, 92: driver, 93: weatherproof, 94: lifeguard,
    95: shove, 96: pickpocket, 97: grease_trail, 98: sneeze_bomb, 99: hex_, 100: decoy_order,
    101: heavy_boots, 102: clumsy, 103: weak_arm, 104: butterfingers, 105: pyrophobe, 106: germaphobe,
    107: vegetarian, 108: sleepy, 109: fragile, 110: show_off, 111: tunnel_vision, 112: lone_wolf,
}
