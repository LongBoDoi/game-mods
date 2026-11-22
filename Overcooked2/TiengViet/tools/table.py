"""Builds and checks vi.tsv, the Vietnamese table TiengViet.dll carries.

    python -I table.py check  "<game dir>"                 compare vi.tsv with the game's English
    python -I table.py build  "<game dir>" a.json b.json   write vi.tsv from {tag: vietnamese} files

Run it with the repo's bundled Python, which has UnityPy:
    .tools\\oc2-python\\python.exe -I Overcooked2\\TiengViet\\tools\\table.py check "E:\\Steam\\steamapps\\common\\Overcooked! 2"

`check` reads the game's own text tables exactly the way the game does (the same files, in the
same order, first tag wins) and reports tags the table lacks, tags the game no longer has, texts
whose English changed since they were translated, and placeholders/markup that differ.
"""
import glob, json, os, re, sys

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
TABLE = os.path.join(HERE, "..", "vi.tsv")
TOKEN = re.compile(r"<[^>]*>|\[[^\]]*\]|\{[^}]*\}|%[A-Za-z0-9_]+%|\\n")
# Localization.c_localisationDataPaths, with "Shared" then "PC" for each.
SOURCES = ["Localization - {0}"] + ["Localization_DLC%02d - {0}" % i for i in (2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13)]


def game_english(game):
    import UnityPy
    assets = {}
    for path in glob.glob(os.path.join(game, "Overcooked2_Data", "*.assets")):
        env = UnityPy.load(path)
        for obj in env.objects:
            if obj.type.name != "TextAsset":
                continue
            data = obj.read()
            if data.m_Name.startswith("Localization"):
                raw = data.m_Script
                assets[data.m_Name] = raw.encode("utf-8", "surrogateescape") if isinstance(raw, str) else raw
    table = {}
    for source in SOURCES:
        for kind in ("Shared", "PC"):
            raw = assets.get(source.format(kind))
            if raw is None:
                continue
            # TVSReader: a line ends at \n or \r (plus one more of either); cells split on tabs.
            for line in re.split(rb"\r\n|\n\r|\r|\n", raw)[1:]:
                cells = line.decode("utf-8", "replace").split("\t")
                if not cells[0]:
                    continue
                if cells[0].lower() == "end":
                    break
                english = cells[1] if len(cells) > 1 else ""
                if cells[0] not in table and english.strip():
                    table[cells[0]] = english.replace("\\n", "\n")
    return table


def read_table():
    rows = {}
    with open(TABLE, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line or line.startswith("#"):
                continue
            tag, english, vietnamese = line.split("\t")
            rows[tag] = (english.replace("\\n", "\n"), vietnamese.replace("\\n", "\n"))
    return rows


def esc(text):
    return text.replace("\r", "").replace("\n", "\\n").replace("\t", " ")


def build(game, files):
    english = game_english(game)
    # Rows for literal texts other mods show (quoted tags) are kept as they are.
    literals = [line for line in open(TABLE, encoding="utf-8") if line.startswith('"')] if os.path.exists(TABLE) else []
    vietnamese = {}
    for path in files:
        vietnamese.update(json.load(open(path, encoding="utf-8")))
    with open(TABLE, "w", encoding="utf-8", newline="\n") as out:
        out.write("# Overcooked! 2 in Vietnamese: Tag<TAB>English (reference only)<TAB>Vietnamese\n")
        out.write("# \\n is a line break. Keep [tokens], <markup> and [A]/[B]/[X]/[Y] exactly as in the English.\n")
        out.write("# Check against the game with: tools\\table.py check \"<game dir>\"\n")
        for tag, text in english.items():
            if tag in vietnamese:
                out.write("%s\t%s\t%s\n" % (tag, esc(text), esc(vietnamese[tag])))
        if literals:
            out.write("# Literal texts other mods put on the game's dialogs (tag = the quoted text).\n")
            out.writelines(literals)
    print("wrote %d of %d game texts to %s" % (sum(1 for t in english if t in vietnamese), len(english), TABLE))


def check(game):
    english = game_english(game)
    rows = read_table()
    missing = [t for t in english if t not in rows]
    gone = [t for t in rows if t not in english and not t.startswith('"')]
    changed = [t for t in rows if t in english and rows[t][0] != english[t]]
    tokens = [t for t, (en, vi) in rows.items() if sorted(TOKEN.findall(en)) != sorted(TOKEN.findall(vi))]
    for title, tags in (("untranslated", missing), ("no longer in the game", gone),
                        ("English changed since translation", changed), ("placeholders differ", tokens)):
        print("%s: %d" % (title, len(tags)))
        for tag in tags[:40]:
            print("   ", tag)
    print("%d of %d game texts translated" % (len(english) - len(missing), len(english)))
    return 1 if missing or tokens else 0


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "check":
        sys.exit(check(sys.argv[2]))
    if len(sys.argv) >= 4 and sys.argv[1] == "build":
        build(sys.argv[2], sys.argv[3:])
        sys.exit(0)
    print(__doc__)
    sys.exit(2)
