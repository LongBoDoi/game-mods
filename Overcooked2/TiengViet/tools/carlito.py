"""Makes TiengViet/fonts/TiengVietBody-Bold.ttf: Carlito Bold with Ậ, Ệ and Ộ repaired.

The game's body font (Body_Font) is Carlito Bold 1.103, whose capitals Ậ, Ệ and Ộ were built
with the dot below but without the circumflex ("Beta1 ... some composites may be off"), so BẬT
showed as BẠT and MỞ RỘNG as MỞ RỌNG. Carlito 1.104 (Google Fonts) still has the fault. This
rebuilds the three from the font's own parts, Ạ/Ẹ/Ọ plus the circumflex of Â/Ê/Ô, and gives
the result the line metrics of the game's copy so it lays out exactly like Body_Font.

Carlito's licence (SIL OFL 1.1) reserves the name "Carlito", so the repaired font is renamed
"TiengViet Body".

    python carlito.py Carlito-Bold.ttf

Carlito-Bold.ttf is Google Fonts' ofl/carlito/Carlito-Bold.ttf. Needs fontTools
(pip install fonttools); the bundled Python does not have it. Run tools/fonts.py afterwards.
"""
import os, sys
from fontTools.ttLib import TTFont

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(HERE, "fonts", "TiengVietBody-Bold.ttf")
FAMILY = "TiengViet Body"

# Broken letter, the same letter with only the dot below, the same letter with only the hat.
REPAIRS = [("Ậ", "Ạ", "Â"), ("Ệ", "Ẹ", "Ê"), ("Ộ", "Ọ", "Ô")]

# hhea and OS/2 line metrics of the game's Body_Font (upm 2048).
ASCENT, DESCENT, GAP = 1536, -512, 452
WIN_ASCENT, WIN_DESCENT = 1950, 550


def main(source):
    font = TTFont(source)
    cmap, glyf = font.getBestCmap(), font["glyf"]
    base = lambda ch: cmap[ord(ch)]
    for broken, dotted, hatted in REPAIRS:
        letter = glyf[base(hatted)]
        below = glyf[base(dotted)]
        if not (letter.isComposite() and below.isComposite()):
            sys.exit(f"{dotted} or {hatted} is not built from parts; repair {broken} by hand.")
        plain = {c.glyphName for c in below.components} & {c.glyphName for c in letter.components}
        marks = [c for c in letter.components if c.glyphName not in plain]
        fixed = glyf[base(broken)]
        fixed.components = list(below.components) + marks
        fixed.recalcBounds(glyf)
        font["hmtx"][base(broken)] = font["hmtx"][base(hatted)]
        print(f"{broken}: {', '.join(c.glyphName for c in fixed.components)}; top {fixed.yMax}")

    hhea, os2 = font["hhea"], font["OS/2"]
    hhea.ascent, hhea.descent, hhea.lineGap = ASCENT, DESCENT, GAP
    os2.sTypoAscender, os2.sTypoDescender, os2.sTypoLineGap = ASCENT, DESCENT, GAP
    os2.usWinAscent, os2.usWinDescent = WIN_ASCENT, WIN_DESCENT

    for record in font["name"].names:
        text = record.toUnicode()
        if "Carlito" in text:
            text = text.replace("Carlito", FAMILY)
            if record.nameID == 6:  # PostScript name: no spaces
                text = text.replace(" ", "")
            record.string = text
    font.save(OUT)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv[1])
