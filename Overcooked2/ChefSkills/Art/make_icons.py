"""
Builds SpecialIcons.png (the sheet the mod embeds) and preview.png (for looking at them).

    .tools/oc2-python/python.exe -I Overcooked2/ChefSkills/Art/make_icons.py

The sheet is COLS x ROWS cells of CELL px; the icon for Special value n sits in cell n (row-major,
top-left first). Cell 0 (None) is left empty. Icons come from the icons_*.py files next to this
one: each has ICONS = {special value: draw function taking an iconkit.Icon}.
"""
import glob
import importlib.util
import os
import sys

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import iconkit  # noqa: E402

COLS, ROWS = 16, 8
CELL = iconkit.CELL

# Special values -> names, read from Catalog.cs so the preview can label them.
NAMES = {}
GROUPS = [(20, (77, 209, 255)), (36, (255, 158, 51)), (47, (115, 235, 97)), (58, (255, 92, 51)),
          (71, (255, 214, 56)), (78, (255, 117, 194)), (89, (189, 133, 255)), (94, (71, 255, 204)),
          (100, (255, 61, 77)), (112, (184, 189, 204))]


def group_colour(n):
    for last, colour in GROUPS:
        if n <= last:
            return colour
    return GROUPS[-1][1]


def read_names():
    import re
    path = os.path.join(HERE, "..", "Specials", "Catalog.cs")
    text = open(path, encoding="utf-8").read()
    enum = re.search(r"enum Special\s*\{(.*?)\}", text, re.S).group(1)
    ids = {m.group(1): int(m.group(2)) for m in re.finditer(r"(\w+)\s*=\s*(\d+)", enum)}
    for m in re.finditer(r'[PAQ]\(Special\.(\w+),\s*"([^"]+)"', text):
        if m.group(1) in ids:
            NAMES[ids[m.group(1)]] = m.group(2)
    return ids


def load_icons():
    icons = {}
    for path in sorted(glob.glob(os.path.join(HERE, "icons_*.py"))):
        spec = importlib.util.spec_from_file_location(os.path.basename(path)[:-3], path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        for key, fn in module.ICONS.items():
            icons[key] = fn
    return icons


def render(fn):
    ic = iconkit.Icon()
    fn(ic)
    return ic.finish()


def main():
    ids = read_names()
    icons = load_icons()
    only = set(int(a) for a in sys.argv[1:] if a.isdigit())
    sheet = Image.new("RGBA", (COLS * CELL, ROWS * CELL), (0, 0, 0, 0))
    rendered = {}
    for value in range(1, max(ids.values()) + 1):
        fn = icons.get(value)
        if fn is None or (only and value not in only):
            continue
        try:
            img = render(fn)
        except Exception as e:  # keep going: show which one failed
            print("FAILED", value, NAMES.get(value), e)
            continue
        rendered[value] = img
        sheet.paste(img, ((value % COLS) * CELL, (value // COLS) * CELL))
    if not only:
        # 256 colours (no dithering) look the same on these flat icons and keep the DLL small;
        # saved back as RGBA so any PNG loader takes it.
        small = sheet.quantize(colors=256, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.NONE)
        small.convert("RGBA").save(os.path.join(HERE, "SpecialIcons.png"), optimize=True)
    missing = [NAMES.get(v, v) for v in range(1, max(ids.values()) + 1) if v not in icons]
    print("icons:", len(rendered), "missing:", len(missing), missing[:20])

    # Preview: each icon on its group-coloured disc (as the game shows it), with its name.
    values = sorted(rendered)
    per_row = 10
    tile = 150
    rows = (len(values) + per_row - 1) // per_row
    preview = Image.new("RGBA", (per_row * tile, rows * (tile + 22)), (40, 46, 60, 255))
    d = ImageDraw.Draw(preview)
    try:
        font = ImageFont.truetype("arialbd.ttf", 13)
    except Exception:
        font = ImageFont.load_default()
    for i, value in enumerate(values):
        x = (i % per_row) * tile
        y = (i // per_row) * (tile + 22)
        r = 62
        cx, cy = x + tile // 2, y + tile // 2
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=group_colour(value) + (255,))
        icon = rendered[value].resize((112, 112), Image.LANCZOS)
        preview.alpha_composite(icon, (cx - 56, cy - 56))
        name = NAMES.get(value, str(value))
        w = d.textlength(name, font=font)
        d.text((cx - w / 2, y + tile + 2), name, fill=(235, 240, 250), font=font)
    out = os.path.join(HERE, "preview.png") if not only else os.path.join(HERE, "preview_part_%d.png" % min(only))
    preview.save(out)
    print("preview:", out)


if __name__ == "__main__":
    main()
