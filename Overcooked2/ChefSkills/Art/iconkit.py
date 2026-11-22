"""
Drawing kit for Chef Skills' special icons: original cartoon pictograms in the game's look
(chunky shapes, thick dark outlines, bright flat fills, a soft highlight).

Every shape is drawn as a sticker: a dark outline round it, its fill, and an optional shine.
Icons are drawn at 4x and scaled down, on a transparent background; the game puts them on a
disc in the special's colour.

Run make_icons.py (with .tools/oc2-python) to build SpecialIcons.png.
"""
import math
from PIL import Image, ImageDraw, ImageFilter, ImageChops

CELL = 128          # final icon size
SS = 4              # supersampling
W = CELL * SS       # drawing size
LINE = 7 * SS // 2  # outline thickness (drawing pixels) ~3.5 px at 128

# Palette (bright, saturated, like the game's UI art).
INK = (34, 30, 46, 255)
WHITE = (255, 255, 255, 255)
CREAM = (255, 244, 222, 255)
RED = (234, 74, 64, 255)
DARKRED = (170, 40, 40, 255)
ORANGE = (248, 150, 48, 255)
YELLOW = (255, 208, 66, 255)
GOLD = (246, 184, 40, 255)
GREEN = (112, 196, 74, 255)
DARKGREEN = (58, 140, 60, 255)
TEAL = (64, 196, 190, 255)
BLUE = (74, 152, 232, 255)
LIGHTBLUE = (160, 214, 250, 255)
NAVY = (52, 70, 140, 255)
PURPLE = (150, 100, 214, 255)
PINK = (246, 120, 170, 255)
STEEL = (196, 206, 222, 255)
DARKSTEEL = (120, 132, 156, 255)
WOOD = (186, 122, 64, 255)
DARKWOOD = (128, 80, 42, 255)
SKIN = (255, 206, 160, 255)
GREY = (150, 150, 160, 255)
BLACK = (40, 40, 48, 255)


def P(x, y):
    """A point in icon units (0..128) to drawing pixels."""
    return (x * SS, y * SS)


def rot(points, angle, cx, cy):
    """Rotate icon-unit points by angle (degrees) round (cx, cy)."""
    a = math.radians(angle)
    ca, sa = math.cos(a), math.sin(a)
    out = []
    for x, y in points:
        dx, dy = x - cx, y - cy
        out.append((cx + dx * ca - dy * sa, cy + dx * sa + dy * ca))
    return out


class Icon:
    def __init__(self):
        self.img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
        self.union = Image.new("L", (W, W), 0)

    # --- masks ------------------------------------------------------------------------------
    @staticmethod
    def _mask():
        return Image.new("L", (W, W), 0)

    def m_ellipse(self, x0, y0, x1, y1):
        m = self._mask()
        ImageDraw.Draw(m).ellipse([P(x0, y0), P(x1, y1)], fill=255)
        return m

    def m_circle(self, cx, cy, r):
        return self.m_ellipse(cx - r, cy - r, cx + r, cy + r)

    def m_rect(self, x0, y0, x1, y1, r=0):
        m = self._mask()
        ImageDraw.Draw(m).rounded_rectangle([P(x0, y0), P(x1, y1)], radius=r * SS, fill=255)
        return m

    def m_poly(self, pts):
        m = self._mask()
        ImageDraw.Draw(m).polygon([P(x, y) for x, y in pts], fill=255)
        return m

    def m_line(self, pts, width, round_caps=True):
        m = self._mask()
        d = ImageDraw.Draw(m)
        pp = [P(x, y) for x, y in pts]
        d.line(pp, fill=255, width=int(width * SS), joint="curve")
        if round_caps:
            r = width * SS / 2
            for x, y in (pp[0], pp[-1]):
                d.ellipse([x - r, y - r, x + r, y + r], fill=255)
        return m

    def m_arc(self, cx, cy, r, start, end, width):
        """A thick arc (degrees, 0 = right, clockwise as in PIL)."""
        pts = []
        steps = max(8, int(abs(end - start) / 4))
        for i in range(steps + 1):
            a = math.radians(start + (end - start) * i / steps)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
        return self.m_line(pts, width)

    # --- painting ---------------------------------------------------------------------------
    def paint(self, mask, fill, outline=True, shine=False, line=None):
        """Draws a shape as a sticker: its outline, then its fill, then an optional shine."""
        line = LINE if line is None else line * SS
        if outline:
            grown = mask.filter(ImageFilter.MaxFilter(_odd(line * 2 + 1)))
            self._fill(grown, INK)
            self.union = ImageChops.lighter(self.union, grown)
        self._fill(mask, fill)
        self.union = ImageChops.lighter(self.union, mask)
        if shine:
            self._shine(mask)
        return mask

    def _fill(self, mask, colour):
        layer = Image.new("RGBA", (W, W), colour)
        self.img.paste(layer, (0, 0), mask)

    def _shine(self, mask):
        """A soft white highlight on the upper-left of the shape."""
        bbox = mask.getbbox()
        if not bbox:
            return
        x0, y0, x1, y1 = bbox
        w, h = x1 - x0, y1 - y0
        hl = Image.new("L", (W, W), 0)
        ImageDraw.Draw(hl).ellipse([x0 + w * 0.12, y0 + h * 0.08, x0 + w * 0.55, y0 + h * 0.42], fill=110)
        hl = hl.filter(ImageFilter.GaussianBlur(w * 0.06 + 1))
        hl = ImageChops.multiply(hl, mask.filter(ImageFilter.MinFilter(_odd(LINE))))
        self._fill(hl, (255, 255, 255, 255))

    # Shortcuts --------------------------------------------------------------------------------
    def ellipse(self, x0, y0, x1, y1, fill, **kw):
        return self.paint(self.m_ellipse(x0, y0, x1, y1), fill, **kw)

    def circle(self, cx, cy, r, fill, **kw):
        return self.paint(self.m_circle(cx, cy, r), fill, **kw)

    def rect(self, x0, y0, x1, y1, fill, r=0, **kw):
        return self.paint(self.m_rect(x0, y0, x1, y1, r), fill, **kw)

    def poly(self, pts, fill, **kw):
        return self.paint(self.m_poly(pts), fill, **kw)

    def line(self, pts, width, fill, **kw):
        return self.paint(self.m_line(pts, width), fill, **kw)

    def arc(self, cx, cy, r, start, end, width, fill, **kw):
        return self.paint(self.m_arc(cx, cy, r, start, end, width), fill, **kw)

    def detail(self, pts, width, colour=INK):
        """A plain stroke (no outline): seams, shading lines, faces."""
        self._fill(self.m_line(pts, width), colour)

    def dot(self, cx, cy, r, colour=INK):
        self._fill(self.m_circle(cx, cy, r), colour)

    # --- finishing --------------------------------------------------------------------------
    def finish(self):
        """A thicker dark rim round the whole silhouette and a small drop shadow, then 128 px."""
        rim = self.union.filter(ImageFilter.MaxFilter(_odd(SS * 3)))
        shadow = rim.filter(ImageFilter.GaussianBlur(SS * 2))
        out = Image.new("RGBA", (W, W), (0, 0, 0, 0))
        sh = Image.new("RGBA", (W, W), (0, 0, 0, 120))
        out.paste(sh, (SS * 2, SS * 4), shadow)
        ink = Image.new("RGBA", (W, W), INK)
        out.paste(ink, (0, 0), rim)
        out = Image.alpha_composite(out, self.img)
        return out.resize((CELL, CELL), Image.LANCZOS)


def _odd(n):
    n = int(n)
    return n if n % 2 == 1 else n + 1


# --- shared objects (icon units, 0..128) ----------------------------------------------------

def star_pts(cx, cy, r_out, r_in, n=5, angle=-90):
    pts = []
    for i in range(n * 2):
        r = r_out if i % 2 == 0 else r_in
        a = math.radians(angle + i * 180 / n)
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def star(ic, cx, cy, r, fill=YELLOW, n=5, inner=0.45, shine=True):
    ic.poly(star_pts(cx, cy, r, r * inner, n), fill, shine=shine)


def sparkle(ic, cx, cy, r, fill=WHITE):
    """A four-point twinkle."""
    ic.poly(star_pts(cx, cy, r, r * 0.3, 4, -90), fill, line=2.5)


def bolt(ic, cx, cy, s, fill=YELLOW, angle=0):
    pts = [(-2, -10), (6, -10), (1, -2), (7, -2), (-5, 11), (-1, 1), (-7, 1)]
    pts = [(cx + x * s, cy + y * s) for x, y in pts]
    ic.poly(rot(pts, angle, cx, cy), fill, shine=True)


def arrow(ic, pts, width, fill, head=None):
    """A thick arrow along a polyline, with a triangle head at the end."""
    head = head or width * 2.2
    (x0, y0), (x1, y1) = pts[-2], pts[-1]
    a = math.atan2(y1 - y0, x1 - x0)
    tip = (x1 + math.cos(a) * head * 0.55, y1 + math.sin(a) * head * 0.55)
    left = (x1 + math.cos(a + 2.4) * head * 0.75, y1 + math.sin(a + 2.4) * head * 0.75)
    right = (x1 + math.cos(a - 2.4) * head * 0.75, y1 + math.sin(a - 2.4) * head * 0.75)
    shaft = ic.m_line(pts, width)
    tri = ic.m_poly([tip, left, right])
    ic.paint(ImageChops.lighter(shaft, tri), fill)


def curved_arrow(ic, cx, cy, r, start, end, width, fill, head=None):
    pts = []
    steps = max(8, int(abs(end - start) / 5))
    for i in range(steps + 1):
        a = math.radians(start + (end - start) * i / steps)
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    arrow(ic, pts, width, fill, head)


def flame(ic, cx, cy, s, outer=ORANGE, inner=YELLOW):
    """A cartoon flame, base at (cx, cy + 20 s), about 44 s tall."""
    def shape(k):
        return [(cx + x * s * k, cy + (y * k + (1 - k) * 14) * s) for x, y in
                [(0, -24), (6, -12), (13, -6), (15, 6), (10, 16), (0, 20), (-10, 16), (-15, 6), (-12, -4),
                 (-7, 2), (-5, -10)]]
    ic.poly(shape(1.0), outer)
    ic.poly(shape(0.55), inner, outline=False)


def drop(ic, cx, cy, s, fill=LIGHTBLUE):
    pts = []
    for i in range(33):
        a = math.radians(-90 + i * 360 / 32)
        r = 10 * s
        x, y = math.cos(a), math.sin(a)
        if y < 0:
            # Narrows to a point at the top.
            k = 1 + y * 0.9
            pts.append((cx + x * r * k, cy + y * r * 1.7))
        else:
            pts.append((cx + x * r, cy + y * r))
    ic.poly(pts, fill, shine=True)


def coin(ic, cx, cy, r, fill=GOLD):
    ic.circle(cx, cy, r, fill, shine=True)
    ic.circle(cx, cy, r * 0.68, (255, 222, 110, 255), outline=False)
    ic.detail([(cx, cy - r * 0.38), (cx, cy + r * 0.38)], r * 0.16, DARKWOOD)


def heart(ic, cx, cy, s, fill=RED):
    pts = []
    for i in range(64):
        t = math.radians(i * 360 / 64)
        x = 16 * math.sin(t) ** 3
        y = -(13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t))
        pts.append((cx + x * s, cy + y * s))
    ic.poly(pts, fill, shine=True)


def boot(ic, cx, cy, s, fill=WOOD, sole=DARKWOOD):
    """A chef's boot, toe pointing right; (cx, cy) is the middle of the sole."""
    body = [(-12, -28), (6, -28), (6, -10), (20, -6), (24, 0), (24, 4), (-12, 4)]
    ic.poly([(cx + x * s, cy + y * s) for x, y in body], fill, shine=True)
    ic.rect(cx - 13 * s, cy + 2 * s, cx + 25 * s, cy + 10 * s, sole, r=3 * s)


def knife(ic, cx, cy, s, angle=-35):
    blade = [(-6, -26), (5, -26), (7, -22), (6, 6), (-6, 6)]
    handle = [(-5, 6), (5, 6), (5, 26), (-5, 26)]
    ic.poly(rot([(cx + x * s, cy + y * s) for x, y in blade], angle, cx, cy), STEEL, shine=True)
    ic.poly(rot([(cx + x * s, cy + y * s) for x, y in handle], angle, cx, cy), WOOD)


def board(ic, cx, cy, s):
    """A chopping board, seen at an angle."""
    ic.rect(cx - 30 * s, cy - 14 * s, cx + 30 * s, cy + 14 * s, WOOD, r=7 * s, shine=True)
    ic.circle(cx + 21 * s, cy - 5 * s, 3.5 * s, DARKWOOD, outline=False)


def pot(ic, cx, cy, s, fill=STEEL):
    ic.rect(cx - 30 * s, cy - 6 * s, cx - 20 * s, cy + 2 * s, fill, r=3 * s)
    ic.rect(cx + 20 * s, cy - 6 * s, cx + 30 * s, cy + 2 * s, fill, r=3 * s)
    ic.rect(cx - 22 * s, cy - 12 * s, cx + 22 * s, cy + 20 * s, fill, r=8 * s, shine=True)
    ic.rect(cx - 25 * s, cy - 16 * s, cx + 25 * s, cy - 8 * s, DARKSTEEL, r=4 * s)


def pan(ic, cx, cy, s, angle=0):
    handle = [(16, -3), (42, -5), (42, 3), (16, 3)]
    ic.poly(rot([(cx + x * s, cy + y * s) for x, y in handle], angle, cx, cy), DARKWOOD)
    ic.ellipse(cx - 22 * s, cy - 12 * s, cx + 22 * s, cy + 12 * s, BLACK)
    ic.ellipse(cx - 16 * s, cy - 8 * s, cx + 16 * s, cy + 8 * s, (80, 80, 92, 255), outline=False)


def plate(ic, cx, cy, s, fill=WHITE):
    ic.ellipse(cx - 30 * s, cy - 14 * s, cx + 30 * s, cy + 14 * s, fill, shine=True)
    ic.ellipse(cx - 18 * s, cy - 8 * s, cx + 18 * s, cy + 8 * s, (226, 232, 242, 255), outline=False)


def hand(ic, cx, cy, s, fill=WHITE, angle=0):
    """A cartoon glove (open palm, fingers up)."""
    parts = []
    palm = ic.m_rect(cx - 13 * s, cy - 6 * s, cx + 13 * s, cy + 18 * s, 9 * s)
    parts.append(palm)
    for i, (fx, top) in enumerate([(-9, -22), (-3, -26), (3, -26), (9, -22)]):
        parts.append(ic.m_line([(cx + fx * s, cy - 2 * s), (cx + fx * s, cy + top * s)], 6.5 * s))
    parts.append(ic.m_line([(cx - 12 * s, cy + 8 * s), (cx - 22 * s, cy - 4 * s)], 7 * s))
    m = parts[0]
    for p in parts[1:]:
        m = ImageChops.lighter(m, p)
    if angle:
        m = m.rotate(-angle, center=P(cx, cy), resample=Image.BICUBIC)
    ic.paint(m, fill, shine=True)


def chef_hat(ic, cx, cy, s, fill=WHITE):
    puff = ImageChops.lighter(ic.m_circle(cx - 12 * s, cy - 8 * s, 12 * s), ic.m_circle(cx + 12 * s, cy - 8 * s, 12 * s))
    puff = ImageChops.lighter(puff, ic.m_circle(cx, cy - 16 * s, 14 * s))
    puff = ImageChops.lighter(puff, ic.m_rect(cx - 16 * s, cy - 8 * s, cx + 16 * s, cy + 14 * s, 3 * s))
    ic.paint(puff, fill, shine=True)
    ic.detail([(cx - 16 * s, cy + 6 * s), (cx + 16 * s, cy + 6 * s)], 2.5 * s, GREY)


def clock(ic, cx, cy, r, fill=WHITE, hands=(0, 120)):
    ic.circle(cx, cy, r, fill, shine=True)
    for a in hands:
        rad = math.radians(a - 90)
        ln = r * (0.55 if a == hands[0] else 0.75)
        ic.detail([(cx, cy), (cx + ln * math.cos(rad), cy + ln * math.sin(rad))], r * 0.14)
    ic.dot(cx, cy, r * 0.12)


def stopwatch(ic, cx, cy, r, fill=WHITE, hand=60):
    ic.rect(cx - r * 0.22, cy - r * 1.32, cx + r * 0.22, cy - r * 0.9, DARKSTEEL, r=2)
    clock(ic, cx, cy, r, fill, hands=(hand,))


def crate(ic, cx, cy, s, fill=WOOD):
    ic.rect(cx - 22 * s, cy - 18 * s, cx + 22 * s, cy + 18 * s, fill, r=3 * s, shine=True)
    ic.detail([(cx - 20 * s, cy - 6 * s), (cx + 20 * s, cy - 6 * s)], 2.5 * s, DARKWOOD)
    ic.detail([(cx - 20 * s, cy + 6 * s), (cx + 20 * s, cy + 6 * s)], 2.5 * s, DARKWOOD)


def flag(ic, x, y, s, fill=RED):
    """A flag on a pole; (x, y) is the foot of the pole."""
    ic.line([(x, y), (x, y - 50 * s)], 4.5 * s, DARKSTEEL)
    ic.poly([(x + 2 * s, y - 50 * s), (x + 30 * s, y - 42 * s), (x + 2 * s, y - 32 * s)], fill, shine=True)


def speed_lines(ic, x, y, s, n=3, length=20, colour=WHITE):
    for i in range(n):
        yy = y + (i - (n - 1) / 2) * 9 * s
        ic.line([(x, yy), (x - (length - i % 2 * 6) * s, yy)], 4.5 * s, colour, line=2.5)


def cloud(ic, cx, cy, s, fill=WHITE):
    m = ic.m_circle(cx - 14 * s, cy + 2 * s, 11 * s)
    for (x, y, r) in [(0, -6, 15), (14, 1, 12), (4, 8, 10), (-6, 8, 10)]:
        m = ImageChops.lighter(m, ic.m_circle(cx + x * s, cy + y * s, r * s))
    ic.paint(m, fill, shine=True)


def chef(ic, cx, cy, s, body=WHITE, face=SKIN):
    """A little chef figure: hat, round head, body. (cx, cy) is the head's centre."""
    ic.rect(cx - 16 * s, cy + 12 * s, cx + 16 * s, cy + 40 * s, body, r=10 * s, shine=True)
    ic.circle(cx, cy, 14 * s, face, shine=True)
    ic.dot(cx - 5 * s, cy + 1 * s, 2 * s)
    ic.dot(cx + 5 * s, cy + 1 * s, 2 * s)
    hat = ImageChops.lighter(ic.m_circle(cx - 7 * s, cy - 17 * s, 8 * s), ic.m_circle(cx + 7 * s, cy - 17 * s, 8 * s))
    hat = ImageChops.lighter(hat, ic.m_circle(cx, cy - 22 * s, 9 * s))
    hat = ImageChops.lighter(hat, ic.m_rect(cx - 11 * s, cy - 18 * s, cx + 11 * s, cy - 10 * s, 2 * s))
    ic.paint(hat, WHITE)


def shield(ic, cx, cy, s, fill=BLUE):
    pts = [(-22, -24), (22, -24), (22, 0), (14, 16), (0, 26), (-14, 16), (-22, 0)]
    ic.poly([(cx + x * s, cy + y * s) for x, y in pts], fill, shine=True)


def no_sign(ic, cx, cy, r):
    """A red 'not allowed' ring with a bar, drawn over whatever is under it."""
    ic.arc(cx, cy, r, 0, 360, r * 0.26, RED, line=2.5)
    a = math.radians(-45)
    ic.line([(cx - r * 0.7 * math.cos(a), cy - r * 0.7 * math.sin(a)),
             (cx + r * 0.7 * math.cos(a), cy + r * 0.7 * math.sin(a))], r * 0.24, RED, line=2.5)
