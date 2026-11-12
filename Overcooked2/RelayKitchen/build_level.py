"""Build a separate OC2DIYLevel map from local, user-supplied runtime assets.

No Unity Editor or additional in-game plugin is required. The Dmm scene supplies
the working engine scaffolding and object prototypes; its kitchen is removed
and replaced with layout.json. Third-party inputs are never modified.
"""
import argparse
import copy
import hashlib
import json
import math
from pathlib import Path
import uuid
import sys

import UnityPy
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import scene_v2
INFO_ID = 28940728314686416
SET_ID = 7518678286840630175
DONOR_CAB = "CAB-5532a29ff4f336d166080004ecfe83a7"
NEW_CAB = "CAB-" + hashlib.md5(b"manhl-relay-kitchen-info-2.0.0").hexdigest()
ZERO = {"m_FileID": 0, "m_PathID": 0}


def refs(value):
    if isinstance(value, dict):
        if "m_FileID" in value and "m_PathID" in value:
            yield value
        else:
            for item in value.values():
                yield from refs(item)
    elif isinstance(value, (list, tuple)):
        for item in value:
            yield from refs(item)


def replace_strings(value, replacements):
    if isinstance(value, str):
        for old, new in replacements.items():
            value = value.replace(old, new)
        return value
    if isinstance(value, dict):
        return {k: replace_strings(v, replacements) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [replace_strings(v, replacements) for v in value]
    return value


def files(env):
    return [f for b in env.files.values() for f in b.files.values() if hasattr(f, "objects")]


def rename_files(env, replacements):
    for bundle in env.files.values():
        updated = {}
        for name, f in bundle.files.items():
            new_name = replace_strings(name, replacements)
            if hasattr(f, "name"):
                f.name = new_name
            if hasattr(f, "externals"):
                for ext in f.externals:
                    ext.path = replace_strings(ext.path, replacements)
            updated[new_name] = f
        bundle.files = updated


def save(env, path):
    path.write_bytes(next(iter(env.files.values())).save(packer="lz4"))


def preview(layout, output):
    cols, rows = layout['grid']['columns'], layout['grid']['rows']
    cell, x0, y0 = 55, 40, 100
    im = Image.new('RGB', (960, 900), '#112332')
    d = ImageDraw.Draw(im)
    font = ImageFont.truetype('C:/Windows/Fonts/arial.ttf', 12)
    heading = ImageFont.truetype('C:/Windows/Fonts/arialbd.ttf', 30)
    d.text((40,20), 'NIGHT MARKET RELAY', font=heading, fill='#ffdaa0')
    d.text((40,65), 'PIZZA + GRILL   /   TWO-WAY DELIVERY   /   SUSHI + GRILL',font=font,fill='#bad9dd')
    colors={'counter':'#977b55','conveyor':'#5482a5','chop':'#e9c382','oven':'#d28156','stove_pan':'#db665b','stove_pot':'#69a8b4','serve':'#75bd89','sink':'#83c7d5','plates':'#e8e3d6','return':'#ae8fbd','bin':'#636e79','extinguisher':'#c44c4f'}
    labels={'counter':'','conveyor':'>','chop':'CUT','oven':'OVEN','stove_pan':'PAN','stove_pot':'RICE POT','serve':'SERVE','sink':'WASH','plates':'3 PLATES','return':'DIRTY','bin':'BIN','extinguisher':'FIRE'}
    occupied,_=scene_v2.cells(layout)
    directions={tuple(t['cell']): ('>' if t.get('yaw',0)==180 else '<') for t in layout['stations'] if t['kind']=='conveyor'}
    for r in range(rows):
        for c in range(cols):
            kind=occupied.get((c,r));bg=colors.get(kind,'#447464' if c<7 else '#353e4e')
            if not kind and c in (7,8):bg='#77614d'
            if kind in layout['ingredients']:bg='#ccbc91'
            x,y=x0+c*cell,y0+r*cell
            d.rounded_rectangle((x+1,y+1,x+cell-2,y+cell-2),radius=4,fill=bg)
            text=labels.get(kind,kind.upper() if kind else '')
            if kind=='conveyor':text=directions.get((c,r),'>')
            box=d.textbbox((0,0),text,font=font)
            d.text((x+(cell-box[2])/2,y+21),text,font=font,fill='#122939')
    for i,(c,r) in enumerate(layout['players'],1):
        x,y=x0+c*cell+27,y0+r*cell+27
        d.ellipse((x-14,y-14,x+14,y+14),fill='#ffedc4');d.text((x-4,y-7),str(i),font=font,fill='#183545')
    d.text((40,835),'6 MINUTES / 1-4 PLAYERS / 9 RECIPES / TWO CROSSINGS + TWO CONVEYOR LANES',font=font,fill='#e4dfce')
    d.text((40,860),'Both banks chop, cook, wash and serve. Each recipe family uses supplies from both sides.',font=font,fill='#bcd0d3')
    im.save(output)
    return im


def station_cells(layout):
    return scene_v2.cells(layout)[0]


def build_info(source, layout, out, image):
    env = UnityPy.load(str(source / "levels/Dmm/info_dmm"))
    sf = files(env)[0]
    input_objects = sf.objects.copy()
    data = {pid: o.read_typetree() for pid, o in sf.objects.items()}
    level = data[INFO_ID]
    level.update(m_Name="LevelInfo_relay_1", levelName=layout["name"],
                 levelNameZH=layout["name"], sceneName=layout["scene"],
                 minOrderCount=2, maxOrderCount=5, disableDynamicParenting=1)
    common = UnityPy.load(str(source / 'common01'))
    catalog = {o.read_typetree().get('m_Name'):o for o in common.objects if o.type.name=='MonoBehaviour'}
    level['recipes'] = [{'m_FileID':1,'m_PathID':catalog[n].path_id} for n in layout['recipes']]
    level['allIngredients'] = [{'m_FileID':1,'m_PathID':catalog[n].path_id} for n in layout['ingredients'].values()]
    level['optionalRecipeMatchListItems'] = []
    level['dependencies'] = ['bundle18','bundle47','bundle359','bundle40','bundle16']
    level['inLevelMusicSO'] = {'m_FileID':1,'m_PathID':catalog['TheNeonCitySO'].path_id}
    # Base-game audio only: keep the needed cooking, UI, city and VO directories.
    level['audioDirectorySOs'] = [{'m_FileID':1,'m_PathID':catalog[n].path_id} for n in
        ('SharedAudioDirectorySO','SushiAudioDirectorySO','StepAudioDirectorySO','UIAudioDirectorySO','VOAudioDirectorySO')]
    level['inLevelAmbiences'] = [28]  # SushiNightAmbience in the pack's enum.
    for i, stars in enumerate(layout["stars"], 1):
        conf = data[level[f"config_{i}p"]["m_PathID"]]
        conf.update(roundTime=layout["round_seconds"], orderLifeTime=layout["order_lifetimes"][i-1],
                    timeBetweenOrders=layout["order_intervals"][i-1], plateReturnTime=6)
        for key, score in zip(("m_OneStarScore", "m_TwoStarScore", "m_ThreeStarScore", "m_FourStarScore"), stars):
            conf[key] = score
    info = data[SET_ID]
    uid = str(uuid.uuid5(uuid.NAMESPACE_URL, "GameMods/RelayKitchen/2.0.0"))
    info.update(levelSetName="GameMods Originals", levelSetNameZH="GameMods Originals",
                author="manhl + Codex (layout); OC2DIYLevel community (runtime/assets)",
                uid=uid, baseUID=uid, version=layout["version"], lastVersion=layout["version"],
                levelInfos=[{"m_FileID": 0, "m_PathID": INFO_ID}])
    # Replace the donor screenshot with the new layout diagram.
    sprite = data[level["screenshot"]["m_PathID"]]
    tex_id = sprite["m_RD"]["texture"]["m_PathID"]
    tex = sf.objects[tex_id].read()
    tex.m_Name = "RelayKitchen_Layout"
    tex.image = image.resize((int(tex.m_Width), int(tex.m_Height)))
    tex.save()
    sprite["m_Name"] = "RelayKitchen_Layout"
    # Keep only the assets reachable from this one level set; other donor maps,
    # screenshots and private recipes are not included in the output.
    keep, todo = set(), [SET_ID]
    while todo:
        pid = todo.pop()
        if pid in keep:
            continue
        keep.add(pid)
        todo.extend(p["m_PathID"] for p in refs(data[pid]) if p["m_FileID"] == 0 and p["m_PathID"] and p["m_PathID"] not in keep)
    ab = next(o for o in sf.objects.values() if o.type.name == "AssetBundle")
    bd = data[ab.path_id]
    bd["m_Name"] = layout["id"] + "/info_manhl_relay"
    bd["m_AssetBundleName"] = bd["m_Name"]
    bd["m_Dependencies"] = ["common01", "common02", "common03"]
    required = {(p["m_FileID"], p["m_PathID"]) for pid in keep for p in refs(data[pid])}
    preload = [p for p in bd["m_PreloadTable"] if (p["m_FileID"], p["m_PathID"]) in required]
    bd["m_PreloadTable"] = preload
    bd["m_Container"] = [("assets/levelsets/manhl_relay/data/levelsetinfo.asset", {
        "preloadIndex": 0, "preloadSize": len(preload), "asset": {"m_FileID": 0, "m_PathID": SET_ID}})]
    bd["m_MainAsset"] = {"preloadIndex": 0, "preloadSize": 0, "asset": ZERO.copy()}
    keep.add(ab.path_id)
    sf.objects = {pid: o for pid, o in sf.objects.items() if pid in keep}
    # Serialized type script indices remain stable: retain their MonoScripts.
    for st in sf.script_types:
        if st.local_serialized_file_index == 0 and st.local_identifier_in_file not in sf.objects:
            pid = st.local_identifier_in_file
            # MonoScripts have no gameplay data and cannot introduce another level.
            original = input_objects[pid]
            cloned = copy.copy(original)
            cloned.assets_file = sf
            cloned.set_raw_data(original.get_raw_data())
            sf.objects[pid] = cloned
            data[pid] = original.read_typetree()
    for pid, obj in sf.objects.items():
        # UnityPy 1.25 reads from the original reader even after save(). Keep
        # the texture's newly serialized bytes instead of restoring its old tree.
        if pid != tex_id:
            obj.save_typetree(data[pid])
    rename_files(env, {DONOR_CAB: NEW_CAB, DONOR_CAB.lower(): NEW_CAB.lower()})
    # Screenshot pixel data is now inline. No donor audio or texture streams needed.
    for b in env.files.values():
        b.files = {k: f for k, f in b.files.items() if hasattr(f, "objects")}
    save(env, out / "info_manhl_relay")


def verify_geometry(layout):
    return scene_v2.validate_layout(layout)


def verify_bundles(source, layout, out):
    # Resolve every serialized pointer, including scene/sharedAssets/common* links.
    envs = [UnityPy.load(str(source / n)) for n in ("common01", "common02", "common03", "common01_textures")]
    built = [UnityPy.load(str(out / n)) for n in ("info_manhl_relay", layout["scene"])]
    registry = {f.name.lower(): f for e in envs+built for f in files(e)}
    checked = 0
    for e in built:
        for f in files(e):
            for obj in f.objects.values():
                d = obj.read_typetree()
                if obj.type.name == "Texture2D":
                    assert not d.get("m_StreamData", {}).get("path"), "Unexpected external screenshot pixels"
                    assert obj.read().image.size[0] > 0
                if obj.type.name == "AssetBundle":
                    assert d["m_Name"].startswith(layout["id"] + "/")
                    if d["m_IsStreamedSceneAssetBundle"]:
                        assert d["m_Container"][0][0].endswith("/"+layout["scene"]+".unity")
                for ref in refs(d):
                    if not ref["m_PathID"]:
                        continue
                    fid = ref["m_FileID"]
                    target = f
                    if fid:
                        ext = f.externals[fid-1].path.lower()
                        if ext in ("resources/unity_builtin_extra", "library/unity default resources"):
                            continue
                        target = registry.get(ext.rsplit("/", 1)[-1])
                        assert target, f"Unknown dependency {ext}"
                    assert ref["m_PathID"] in target.objects, f"Broken reference: {f.name}:{obj.path_id} -> {ref}"
                    checked += 1
    info = next(o.read_typetree() for o in built[0].objects if o.path_id==SET_ID)
    assert len(info["levelInfos"]) == 1
    li = next(o.read_typetree() for o in built[0].objects if o.path_id==INFO_ID)
    assert li["sceneName"] == layout["scene"] and len(li["recipes"]) == len(layout['recipes'])
    for p in (1,2,3,4):
        conf = next(o.read_typetree() for o in built[0].objects if o.path_id==li[f"config_{p}p"]["m_PathID"])
        assert conf["roundTime"]==layout["round_seconds"]
    world = next(f for f in files(built[1]) if f.name == "BuildPlayer-"+layout["scene"])
    wd = {pid: o.read_typetree() for pid, o in world.objects.items()}
    scene_v2.verify_scene(world, layout)
    return checked


def verify_native_assets(source, layout, out, game_dir):
    """Resolve the used SOs all the way into the installed game's real bundles."""
    common=UnityPy.load(str(source/'common01'))
    common_sf=files(common)[0]
    needed={}
    for name in ('info_manhl_relay',layout['scene']):
        env=UnityPy.load(str(out/name))
        for f in files(env):
            for obj in f.objects.values():
                if obj.type.name!='MonoBehaviour':continue
                for p in refs(obj.read_typetree()):
                    if not p['m_FileID'] or not p['m_PathID']:continue
                    ext=f.externals[p['m_FileID']-1].path.lower().rsplit('/',1)[-1]
                    if ext!=common_sf.name.lower():continue
                    target=common_sf.objects[p['m_PathID']]
                    if target.type.name!='MonoBehaviour':continue
                    d=target.read_typetree()
                    if d.get('bundleName') and d.get('assetPath'):
                        needed[d['assetPath'].lower()]=d['bundleName']
    loaded={}
    root=game_dir/'Overcooked2_Data/StreamingAssets/Windows'
    for asset,bundle in needed.items():
        if bundle not in loaded:loaded[bundle]=UnityPy.load(str(root/bundle))
        assert asset in loaded[bundle].container, f'Missing native asset: {bundle}/{asset}'
    # Native Rightwards=1, used by ServerConveyorStation's -transform.right rule.
    import struct
    prefab=loaded['bundle47'].container['assets/prefabs/overcooked_legacy/stations/conveyorstation.prefab']
    go=prefab.read_typetree();f=prefab.assetsfile
    found=False
    for p in go['m_Component']:
        obj=f.objects[p['component']['m_PathID']]
        if obj.type.name!='MonoBehaviour':continue
        base=obj.read(check_read=False)
        if base.m_Script.deref().read().m_ClassName=='ConveyorStation':
            raw=obj.get_raw_data()
            assert len(raw)==52 and struct.unpack_from('<i',raw,36)[0]==1, 'Native conveyor direction differs from validated game version'
            found=True
    assert found
    return len(needed)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=HERE.parent / "installer/plugins/OC2DIYLevel")
    parser.add_argument("--output", type=Path, default=HERE / "build/manhl_relay_kitchen")
    parser.add_argument("--game-dir",type=Path,default=Path("E:/Steam/steamapps/common/Overcooked! 2"))
    args = parser.parse_args()
    layout = json.loads((HERE / "layout.json").read_text(encoding="utf8"))
    source, out = args.source.resolve(), args.output.resolve()
    assert source != out and source not in out.parents, "Output must be outside the input pack"
    for p in ("levels/Dmm/info_dmm", "levels/Dmm/s_dmm_1", "common01", "common02", "common03"):
        assert (source/p).is_file(), f"Missing input: {source/p}"
    walk_count = verify_geometry(layout)
    out.mkdir(parents=True, exist_ok=True)
    im = preview(layout, out / "layout.png")
    build_info(source, layout, out, im)
    scene_v2.build_scene(source, layout, out, sys.modules[__name__], args.game_dir.resolve())
    count = verify_bundles(source, layout, out)
    native_count = verify_native_assets(source, layout, out, args.game_dir.resolve())
    report = {"level": layout["name"], "version": layout["version"], "scene": layout["scene"],
              "connected_walk_cells": walk_count, "resolved_references": count,
              "runtime_playtest": "pending", "inputs": {}, "outputs": {}, "recipe_families": ["burger", "pizza", "sushi"],
              "validated_native_assets":native_count}
    for p in ("levels/Dmm/info_dmm", "levels/Dmm/s_dmm_1", "common01", "common02", "common03"):
        report["inputs"][p] = hashlib.sha256((source/p).read_bytes()).hexdigest()
    for name in ("info_manhl_relay", layout["scene"], "layout.png"):
        report["outputs"][name] = hashlib.sha256((out/name).read_bytes()).hexdigest()
    report['inputs']['game/bundle40']=hashlib.sha256((args.game_dir/'Overcooked2_Data/StreamingAssets/Windows/bundle40').read_bytes()).hexdigest()
    (out / "build-report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf8")
    print(f"Built {out}; {walk_count} connected walk cells; {count} resolved references; {native_count} native assets.")


if __name__ == "__main__":
    main()
