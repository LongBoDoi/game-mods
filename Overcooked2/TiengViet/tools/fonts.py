"""Builds TiengViet/fonts.bundle: a Unity asset bundle holding the fonts TiengViet brings.

Unity 2017 cannot load a font file at runtime, only from an asset bundle. This copies the
game's own font bundle (StreamingAssets/Windows/bundle22, built by the game's Unity 2017.4.8),
keeps seven of its font assets with their material and texture, puts each font file from
TiengViet/fonts into one of them, and drops everything else:

- the six title fonts. Their line spacing, ascent and descent are set from the font file the
  way Unity's importer does (hhea metrics at size 16).
- "body": the game's body font repaired (see carlito.py). It takes the metrics and fallbacks of
  the game's own Body_Font asset, so it lays out exactly like it.

    .tools\\oc2-python\\python.exe -I Overcooked2\\TiengViet\\tools\\fonts.py "E:\\Steam\\steamapps\\common\\Overcooked! 2"

Needs UnityPy (the bundled Python has it). Rebuild TiengViet afterwards: build.cmd embeds
fonts.bundle in the DLL.
"""
import hashlib, os, struct, sys
import UnityPy

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONTS = os.path.join(HERE, "fonts")
OUT = os.path.join(HERE, "fonts.bundle")
BUNDLE = "tiengviet_fonts"

# Asset name (what TiengViet loads), font file, family name.
CHOICES = [
    ("Coiny", "Coiny-Regular.ttf", "Coiny"),
    ("Grandstander", "Grandstander-Black.ttf", "Grandstander Black"),
    ("Baloo2", "Baloo2-ExtraBold.ttf", "Baloo 2 ExtraBold"),
    ("Nunito", "Nunito-Black.ttf", "Nunito Black"),
    ("PaytoneOne", "PaytoneOne-Regular.ttf", "Paytone One"),
    ("FiraSans", "FiraSans-ExtraBold.ttf", "Fira Sans ExtraBold"),
]
BODY = ("Body", "TiengVietBody-Bold.ttf", "TiengViet Body")
# Windows fonts Unity may borrow a glyph from when the font lacks it (Chinese in mod names...).
OS_FALLBACKS = ["Segoe UI", "Arial", "Microsoft YaHei", "Malgun Gothic", "Yu Gothic"]
GAME_FONTS = ("Header_Font", "NaughtySquirrel2", "Body_Font")


def metrics(data):
    """unitsPerEm and hhea ascender/descender/lineGap of a TrueType/OpenType file."""
    count = struct.unpack(">H", data[4:6])[0]
    tables = {}
    for i in range(count):
        tag, _, offset, _ = struct.unpack(">4sIII", data[12 + 16 * i: 28 + 16 * i])
        tables[tag] = offset
    upm = struct.unpack(">H", data[tables[b"head"] + 18: tables[b"head"] + 20])[0]
    asc, desc, gap = struct.unpack(">hhh", data[tables[b"hhea"] + 4: tables[b"hhea"] + 10])
    return upm, asc, desc, gap


def main(game):
    source = os.path.join(game, "Overcooked2_Data", "StreamingAssets", "Windows", "bundle22")
    env = UnityPy.load(source)
    bundle = env.file
    (cab_name, serialized), = [(n, f) for n, f in bundle.files.items() if not n.endswith(".resS")]
    objects = serialized.objects

    fonts = sorted((o for o in objects.values() if o.type.name == "Font"), key=lambda o: o.path_id)
    named = {o.read().m_Name: o for o in fonts}
    header = named["Header_Font"].read_typetree()
    body = named["Body_Font"].read_typetree()
    slots = [o for o in fonts if o.read().m_Name not in GAME_FONTS][:len(CHOICES) + 1]

    keep, container, preload = set(), [], []
    for (asset, file, family), obj in zip(CHOICES + [BODY], slots):
        data = open(os.path.join(FONTS, file), "rb").read()
        tree = obj.read_typetree()
        if asset == BODY[0]:
            like = {k: body[k] for k in ("m_FontSize", "m_Ascent", "m_Descent", "m_LineSpacing")}
            names = [family] + [n for n in body["m_FontNames"] if n != "Carlito"]
            model = body
        else:
            upm, asc, desc, gap = metrics(data)
            scale = tree["m_FontSize"] / upm
            like = {"m_Ascent": asc * scale, "m_Descent": desc * scale, "m_LineSpacing": (asc - desc + gap) * scale}
            names = [family] + OS_FALLBACKS
            model = header
        tree.update(like)
        tree.update({
            "m_Name": asset,
            "m_FontData": list(data) if isinstance(tree["m_FontData"], list) else data,
            "m_FontNames": names,
            # The game's Chinese and Thai fallbacks, as its own font has them.
            "m_FallbackFonts": [p for p in model["m_FallbackFonts"] if p["m_FileID"] != 0],
            "m_CharacterRects": [], "m_KerningValues": [],
        })
        obj.save_typetree(tree)
        deps = [obj.path_id] + [tree[k]["m_PathID"] for k in ("m_DefaultMaterial", "m_Texture") if tree[k]["m_FileID"] == 0 and tree[k]["m_PathID"]]
        keep.update(deps)
        container.append((f"assets/tiengviet/{asset.lower()}.ttf", {
            "preloadIndex": len(preload), "preloadSize": len(deps),
            "asset": {"m_FileID": 0, "m_PathID": obj.path_id}}))
        preload.extend({"m_FileID": 0, "m_PathID": p} for p in deps)
        print(f"{asset}: {file}, {len(data)} bytes, ascent {tree['m_Ascent']:.2f}, line {tree['m_LineSpacing']:.2f}")

    manifest = next(o for o in objects.values() if o.type.name == "AssetBundle")
    keep.add(manifest.path_id)
    for path_id in [p for p in objects if p not in keep]:
        del objects[path_id]

    tree = manifest.read_typetree()
    tree.update({"m_Name": BUNDLE, "m_AssetBundleName": BUNDLE, "m_Container": container,
                 "m_PreloadTable": preload, "m_Dependencies": []})
    manifest.save_typetree(tree)

    # A new inner name, so the game's bundle22 and this one can be loaded side by side.
    new_cab = "CAB-" + hashlib.md5(BUNDLE.encode()).hexdigest()
    serialized.name = new_cab
    bundle.files = {new_cab: serialized}
    open(OUT, "wb").write(bundle.save(packer="lz4"))
    print(f"wrote {OUT} ({os.path.getsize(OUT)} bytes)")

    check = UnityPy.load(OUT)
    names = sorted(o.read().m_Name for o in check.objects if o.type.name == "Font")
    print("check:", len(check.objects), "objects; fonts", names, "; files", list(check.file.files))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv[1] if len(sys.argv) > 1 else r"E:\Steam\steamapps\common\Overcooked! 2")
