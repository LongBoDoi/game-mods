"""Scene construction using the pack's actual prefab stubs and native textures."""
import copy
import math

import UnityPy


def ref(pid, fid=0):
    return {"m_FileID": fid, "m_PathID": pid}


def footprint(s):
    c, r = s['cell']
    w = s.get('width', 1)
    if s.get('axis') == 'row':
        return [(c, int(r-(w-1)/2+i)) for i in range(w)]
    return [(int(c-(w-1)/2+i), r) for i in range(w)]


def cells(layout):
    cols, rows = layout['grid']['columns'], layout['grid']['rows']
    result = {(c, r): 'counter' for c in range(cols) for r in (0,rows-1)}
    result.update({(c,r):'counter' for c in (0,cols-1) for r in range(1,rows-1)})
    for c in layout['divider']['columns']:
        for r in layout['divider']['rows']:
            result[c,r] = 'counter'
    explicit = set()
    for s in layout['stations']:
        for p in footprint(s):
            assert p not in explicit, f'Overlapping stations: {p}'
            assert 0 <= p[0] < cols and 0 <= p[1] < rows
            explicit.add(p)
            result[p] = s['kind']
    return result, explicit


def validate_layout(layout):
    occupied, _ = cells(layout)
    cols, rows = layout['grid']['columns'], layout['grid']['rows']
    walk = {(c,r) for c in range(1,cols-1) for r in range(1,rows-1)}-set(occupied)
    assert all(tuple(p) in walk for p in layout['players']), 'Blocked chef spawn'
    seen, todo = set(), [tuple(layout['players'][0])]
    while todo:
        p = todo.pop()
        if p not in walk or p in seen:
            continue
        seen.add(p)
        c,r=p
        todo += [(c-1,r),(c+1,r),(c,r-1),(c,r+1)]
    assert seen == walk, 'Disconnected walkable area'
    for s in layout['stations']:
        if s['kind']=='conveyor':
            c,r=s['cell']
            assert any(occupied.get((c+dc,r))=='conveyor' for dc in (-1,1)), 'Isolated conveyor'
            continue
        assert any((c+dc,r+dr) in walk for c,r in footprint(s) for dc,dr in ((0,1),(0,-1),(1,0),(-1,0))), f'Cannot reach {s}'
    for passage in layout['passages']:
        assert all((c,r) in walk for c in passage['columns'] for r in passage['rows']), 'Blocked passage'
    # Each family requires ingredients from both banks.
    banks={s['kind']: 'west' if s['cell'][0]<7 else 'east' for s in layout['stations'] if s['kind'] in layout['ingredients']}
    for family in [('bun','meat','tomato','lettuce'),('dough','tomato','cheese','pepperoni'),('rice','fish','seaweed','cucumber')]:
        assert {banks[k] for k in family} == {'west','east'}
    return len(walk)


class SceneBuilder:
    def __init__(self, env, common, helpers):
        self.env, self.common, self.h = env, common, helpers
        self.sf = next(f for f in helpers.files(env) if f.name=='BuildPlayer-s_dmm_1')
        self.source_objects = self.sf.objects.copy()
        self.data = {pid:o.read_typetree() for pid,o in self.sf.objects.items()}
        self.original = copy.deepcopy(self.data)
        self.next_id = max(self.sf.objects) + 100
        self.common_sf = helpers.files(common)[0]
        self.shared = next(f for f in helpers.files(env) if f.name.endswith('.sharedAssets'))
        self.script_ids = {}
        self.common_fid = next(i+1 for i,e in enumerate(self.sf.externals) if 'cab-4558fbec' in e.path.lower())
        self.type_cache = {}
        self.placements = []

    def local_script(self, obj):
        """Scene scripts belong in sharedAssets, as in the original built scenes."""
        d=obj.read_typetree()
        key=(d['m_AssemblyName'],d['m_Namespace'],d['m_ClassName'])
        if key in self.script_ids:return self.script_ids[key]
        for existing in self.shared.objects.values():
            if existing.type.name!='MonoScript':continue
            ed=existing.read_typetree()
            if (ed['m_AssemblyName'],ed['m_Namespace'],ed['m_ClassName'])==key:
                self.script_ids[key]=existing.path_id
                return existing.path_id
        clone=copy.copy(obj)
        clone.path_id=max(self.shared.objects)+1
        clone.assets_file=self.shared
        index=next(i for i,t in enumerate(self.shared.types) if t.class_id==115)
        clone.type_id=index;clone.serialized_type=self.shared.types[index]
        self.shared.objects[clone.path_id]=clone
        clone.save_typetree(d)
        self.script_ids[key]=clone.path_id
        return clone.path_id

    def import_type(self, obj):
        key=(obj.assets_file.name, obj.type_id)
        if key in self.type_cache:
            return self.type_cache[key]
        t=obj.serialized_type
        for i,current in enumerate(self.sf.types):
            if (current.class_id,current.script_id,current.old_type_hash)==(t.class_id,t.script_id,t.old_type_hash):
                self.type_cache[key]=i
                return i
        new=copy.copy(t)
        if t.class_id==114 and t.script_type_index>=0:
            st=copy.copy(obj.assets_file.script_types[t.script_type_index])
            assert st.local_serialized_file_index==0
            assert obj.assets_file==self.common_sf
            script=self.common_sf.objects[st.local_identifier_in_file]
            st.local_serialized_file_index=1
            st.local_identifier_in_file=self.local_script(script)
            new.script_type_index=len(self.sf.script_types)
            self.sf.script_types.append(st)
        index=len(self.sf.types)
        self.sf.types.append(new)
        self.type_cache[key]=index
        return index

    def add_object(self, original, d):
        obj=copy.copy(original)
        pid=self.next_id
        self.next_id+=1
        obj.path_id=pid
        obj.type_id=self.import_type(original)
        obj.serialized_type=self.sf.types[obj.type_id]
        obj.assets_file=self.sf
        self.sf.objects[pid]=obj
        self.data[pid]=d
        return pid

    def remove_children(self, tid, keep=()):
        remaining=[]
        for child in list(self.data[tid]['m_Children']):
            cid=child['m_PathID']
            if cid in keep:
                remaining.append(child)
                continue
            self.remove_children(cid)
            goid=self.data[cid]['m_GameObject']['m_PathID']
            ids=[goid]+[p['component']['m_PathID'] for p in self.data[goid]['m_Component']]
            for pid in ids:
                self.sf.objects.pop(pid)
                self.data.pop(pid)
        self.data[tid]['m_Children']=remaining

    @staticmethod
    def rotation(yaw):
        a=math.radians(yaw)/2
        return dict(x=0.,y=math.sin(a),z=0.,w=math.cos(a))

    def transform(self, tid, parent, position, yaw=0, scale=(1,1,1)):
        d=self.data[tid]
        d['m_Father']=ref(parent)
        d['m_LocalPosition']=dict(zip(('x','y','z'),map(float,position)))
        d['m_LocalRotation']=self.rotation(yaw)
        d['m_LocalScale']=dict(zip(('x','y','z'),map(float,scale)))
        if ref(tid) not in self.data[parent]['m_Children']:
            self.data[parent]['m_Children'].append(ref(tid))

    def prefab(self, path, name, position, yaw=0, parent=200, scale=(1,1,1)):
        ptr=self.common.container[path.lower()]
        go=ptr.read_typetree()
        goid=ptr.path_id
        objects=[self.common_sf.objects[goid]]
        for p in go['m_Component']:
            obj=self.common_sf.objects[p['component']['m_PathID']]
            d=obj.read_typetree()
            # Built community scenes contain BOTH stub and live PseudoPrefab.
            # The runtime loader initializes the live component; omitting it
            # creates valid-looking placeholders that never instantiate models.
            objects.append(obj)
        old_ids={o.path_id for o in objects}
        new_ids={}
        for obj in objects:
            new_ids[obj.path_id]=self.add_object(obj,copy.deepcopy(obj.read_typetree()))
        for old,pid in new_ids.items():
            d=self.data[pid]
            if 'm_Script' in d:
                sp=d['m_Script']
                assert sp['m_FileID']==0
                d['m_Script']=ref(self.local_script(self.common_sf.objects[sp['m_PathID']]),1)
            if pid==new_ids[goid]:
                d['m_Name']=name
                d['m_IsActive']=True
                d['m_Component']=[p for p in d['m_Component'] if p['component']['m_PathID'] in old_ids]
            for p in self.h.refs(d):
                if p['m_FileID']==0 and p['m_PathID']:
                    if p['m_PathID'] in new_ids:
                        p['m_PathID']=new_ids[p['m_PathID']]
                    else:
                        p['m_FileID']=self.common_fid
        tid=next(new_ids[o.path_id] for o in objects if o.type.name=='Transform')
        assert not self.data[tid]['m_Children'], f'Unexpected prefab child objects: {path}'
        self.transform(tid,parent,position,yaw,scale)
        self.placements.append((name,tid,position))
        return list(new_ids.values())

    def clone_scene_go(self, tid, name, parent, position, yaw=0, scale=(1,1,1), keep_collider=False):
        goid=self.original[tid]['m_GameObject']['m_PathID']
        go=self.original[goid]
        old_ids=[goid]+[p['component']['m_PathID'] for p in go['m_Component']]
        if not keep_collider:
            old_ids=[p for p in old_ids if 'Collider' not in self.source_objects[p].type.name]
        mapping={pid:self.add_object(self.source_objects[pid],copy.deepcopy(self.original[pid])) for pid in old_ids}
        for pid in mapping.values():
            for p in self.h.refs(self.data[pid]):
                if p['m_FileID']==0 and p['m_PathID'] in mapping:
                    p['m_PathID']=mapping[p['m_PathID']]
        gd=self.data[mapping[goid]]
        gd['m_Name']=name
        gd['m_IsActive']=True
        gd['m_Component']=[p for p in gd['m_Component'] if p['component']['m_PathID'] in mapping.values()]
        self.data[mapping[tid]]['m_Children']=[]
        self.transform(mapping[tid],parent,position,yaw,scale)
        return list(mapping.values())

    def native_texture(self, obj, name):
        texture=obj.read()
        raw=texture.get_image_data()
        d=obj.read_typetree()
        d['m_Name']=name
        d['image data']=raw
        d['m_StreamData']={'offset':0,'size':0,'path':''}
        return self.add_object(obj,d)

    def floor_material(self, texture_pid, name, repeat, color):
        original=self.source_objects[1]
        d=copy.deepcopy(self.original[1])
        d['m_Name']=name
        props=d['m_SavedProperties']
        for key,v in props['m_TexEnvs']:
            if key=='_MainTex':
                v['m_Texture']=ref(texture_pid)
                v['m_Scale']=dict(x=repeat[0],y=repeat[1])
                v['m_Offset']=dict(x=0.,y=0.)
        props['m_Colors']=[(k,dict(r=color[0],g=color[1],b=color[2],a=1.)) if k=='_Color' else (k,v) for k,v in props['m_Colors']]
        return self.add_object(original,d)

    def floor_panel(self, name, bounds, material, height):
        x0,x1,z0,z1=bounds
        ids=self.clone_scene_go(169,name,261,((x0+x1)/2,height,(z0+z1)/2),scale=((x1-x0)/10,1,(z1-z0)/10))
        for pid in ids:
            if self.sf.objects[pid].type.name=='MeshRenderer':
                self.data[pid]['m_Materials']=[ref(material)]

    def save(self, layout, out):
        for pid,obj in self.sf.objects.items():
            obj.save_typetree(self.data[pid])
        for f in self.h.files(self.env):
            for obj in f.objects.values():
                if obj.type.name=='AssetBundle':
                    d=obj.read_typetree()
                    d['m_Name']=layout['id']+'/'+layout['scene']
                    d['m_AssetBundleName']=d['m_Name']
                    d['m_Container'][0]=(f"Assets/LevelSets/{layout['id']}/scenes/{layout['scene']}.unity",d['m_Container'][0][1])
                    obj.save_typetree(d)
                if obj.type.name=='PreloadData':
                    d=obj.read_typetree()
                    d['m_Dependencies']=[layout['id']+'/info_manhl_relay' if x=='dmm/info_dmm' else x for x in d['m_Dependencies']]
                    for pid in self.script_ids.values():
                        if ref(pid,1) not in d['m_Assets']:d['m_Assets'].append(ref(pid,1))
                    obj.save_typetree(d)
        self.h.rename_files(self.env,{'s_dmm_1':layout['scene'],self.h.DONOR_CAB:self.h.NEW_CAB,self.h.DONOR_CAB.lower():self.h.NEW_CAB.lower()})
        self.h.save(self.env,out/layout['scene'])


def build_scene(source, layout, out, helpers, game_dir):
    env=UnityPy.load(str(source/'levels/Dmm/s_dmm_1'))
    common=UnityPy.load(str(source/'common01'))
    b=SceneBuilder(env,common,helpers)
    for tid in (200,186,261):
        b.remove_children(tid)
    for tid in (200,186):
        b.data[tid]['m_LocalPosition']=dict(x=0.,y=0.,z=0.)
    names={o.read_typetree().get('m_Name'):o.path_id for o in common.objects if o.type.name=='MonoBehaviour' and 'prefabName' in o.read_typetree()}
    def path(n):
        matches=[p for p in common.container if p.endswith('/'+n.lower()+'.prefab')]
        assert len(matches)==1,(n,matches)
        return matches[0]
    def pos(c,r,y=0):
        g=layout['grid'];return (round(g['origin'][0]+c*g['cell'],5),y,round(g['origin'][1]-r*g['cell'],5))
    def set_stub(ids,key,value):
        matches=[pid for pid in ids if key in b.data[pid]]
        assert len(matches)==1,(ids,key)
        b.data[matches[0]][key]=value
        return matches[0]
    def item(kind,c,r,yaw=0):
        return b.prefab(path(kind),'Market_'+kind+f'_{c}_{r}',pos(c,r,.6),yaw,parent=186)
    def counter(kind,c,r,yaw=0):
        return b.prefab(path(kind),'Market_'+kind+f'_{c}_{r}',pos(c,r),yaw)
    occupied,explicit=cells(layout)
    for (c,r) in sorted(set(occupied)-explicit):
        counter('Counter',c,r)
    returns={}
    serving=[]
    for s in layout['stations']:
        kind=s['kind'];c,r=s['cell'];yaw=s.get('yaw',0)
        if kind in layout['ingredients']:
            ids=counter('Dispenser',c,r,yaw)
            set_stub(ids,'spawnerItemPrefabSO',ref(names[layout['ingredients'][kind]],b.common_fid))
        elif kind in ('stove_pan','stove_pot'):
            counter('Cooker',c,r,yaw)
            ids=item('FryPan' if kind=='stove_pan' else 'Pot',c,r,yaw)
            set_stub(ids,'capacity',1)
            set_stub(ids,'allowedIngredientSOs',[ref(names['MeatSO' if kind=='stove_pan' else 'SushiRiceSO'],b.common_fid)])
            set_stub(ids,'allowedCookingStationTypes',[1])
        elif kind in ('plates','extinguisher'):
            counter('Counter',c,r,yaw)
            ids=item('CleanPlateStack' if kind=='plates' else 'FireExtinguisher',c,r,yaw)
            if kind=='plates':
                set_stub(ids,'plateCount',3)
        else:
            prefabs={'chop':'ChoppingCounter','oven':'Oven','sink':'Sink','serve':'ServingStation','return':'PlateReturn','conveyor':'ConveyorStation','bin':'Bin','counter':'Counter'}
            ids=counter(prefabs[kind],c,r,yaw)
            if kind=='return':
                set_stub(ids,'returnClean',0)
                returns[s['key']]=next(pid for pid in ids if 'returnClean' in b.data[pid])
            if kind=='serve':
                serving.append((ids,s['return']))
            if kind=='conveyor':
                set_stub(ids,'conveySpeed',s['speed'])
    for ids,key in serving:
        set_stub(ids,'plateReturn',ref(returns[key]))
        if any('plateReturns' in b.data[pid] for pid in ids):
            set_stub(ids,'plateReturns',[ref(returns[key])])
    for tid,(c,r) in zip((172,289,212,298),layout['players']):
        b.data[tid]['m_LocalPosition']=dict(zip(('x','y','z'),pos(c,r)))
    # Native floor maps, with correct tile density and a timber circulation strip.
    textures=UnityPy.load(str(game_dir/'Overcooked2_Data/StreamingAssets/Windows/bundle40'))
    tex_objects={o.path_id:o for o in textures.objects}
    white=b.native_texture(tex_objects[5674065047702947503],'Market_NativeCeramic')
    navy=b.native_texture(tex_objects[3522752037899232534],'Market_NativeSlate')
    wood=b.native_texture(tex_objects[6124763266615981486],'Market_NativeTimber')
    mat_wood=b.floor_material(wood,'Market_TimberTrim',(4.25,3.5),(1.,.93,.84))
    mat_white=b.floor_material(white,'Market_PizzaCeramic',(3.5,6.5),(1.,.96,.89))
    mat_navy=b.floor_material(navy,'Market_SushiSlate',(3.5,6.5),(.88,.97,1.))
    room=layout['room']
    bounds=(room['left'],room['right'],room['south'],room['north'])
    b.floor_panel('Market_TimberFoundation',bounds,mat_wood,-.06)
    b.floor_panel('Market_WestCeramic',(-4.2,4.2,-11.4,4.2),mat_white,-.04)
    b.floor_panel('Market_EastSlate',(6.6,15.,-11.4,4.2),mat_navy,-.04)
    # Full-length walls and native art now use the SAME bounds as collision.
    # Leave 1.2 world units between each perimeter station centre and the wall.
    art=[('wall_wallpaper_01','Market_WestWall',(-4.8,0,-3.6),-90,(14,1,1)),
         ('wall_wallpaper_01','Market_EastWall',(15.6,0,-3.6),90,(14,1,1)),
         ('wall_brick_01a','Market_LowFrontWall',(5.4,0,-12),180,(17,.38,1)),
         ('wall_brick_window_02_orange','Market_WestWindow',(-.6,0,4.8),0,(2,1,1)),
         ('wall_brick_window_02','Market_EastWindow',(11.4,0,4.8),0,(2,1,1)),
         ('wall_wallpaper_01','Market_BackCentre',(5.4,0,4.8),0,(4,1,1)),
         ('decoration_bamboo_02','Market_BambooNW',(-5.9,0,4.),0,(1,1,1)),
         ('decoration_bamboo_02','Market_BambooNE',(16.7,0,4.),0,(1,1,1)),
         ('decoration_bamboo_02','Market_BambooSW',(-5.9,0,-10.8),0,(1,1,1)),
         ('decoration_bamboo_02','Market_BambooSE',(16.7,0,-10.8),0,(1,1,1)),
         ('exterior_tree_pink_01','Market_Blossom',(-9.,0,3.),0,(1,1,1)),
         ('exterior_tree_green_01','Market_TreeEast',(20.,0,-8.),0,(1,1,1)),
         ('exterior_street_bench','Market_BenchWest',(-7.2,0,-4.),90,(1,1,1)),
         ('exterior_street_bench','Market_BenchEast',(18.,0,-4.),-90,(1,1,1)),
         ('m_city_canopy','Market_WestAwning',(-7.2,0,0.),0,(1,1,1)),
         ('sushi_sign_01_day','Market_SushiSign',(11.4,2.,5.3),0,(1,1,1))]
    for prefab,name,position,yaw,scale in art:
        b.prefab(path(prefab),name,position,yaw,parent=261,scale=scale)
    # Wall lamps are cosmetic; their native prefabs include their light settings.
    for x in (-2.4,2.4,8.4,13.2):
        b.prefab(path('decoration_wall_light 1'),'Market_BackLamp_'+str(x),(x,2.4,4.55),180,parent=261)
    # A warm west kitchen and cooler east kitchen, with readable soft shadows.
    # Three broad lights illuminate the floor. Native wall lamps have short
    # two-unit spots; spacing their fixtures by >=4.8 keeps those pools apart.
    sun=next(o for o in b.source_objects.values() if o.type.name=='Light')
    sun_go=b.original[sun.path_id]['m_GameObject']['m_PathID']
    sun_tid=next(p['component']['m_PathID'] for p in b.original[sun_go]['m_Component'] if b.source_objects[p['component']['m_PathID']].type.name=='Transform')
    b.data[sun.path_id]['m_Color']=dict(r=1.,g=.94,b=.85,a=1.)
    b.data[sun.path_id]['m_Intensity']=1.1
    for name,x,color in [('WestGlow',.6,(1.,.78,.51)),('EastGlow',10.2,(.65,.83,1.))]:
        ids=b.clone_scene_go(sun_tid,'Market_'+name,215,(x,4.,-3.6))
        for pid in ids:
            if b.sf.objects[pid].type.name=='Light':
                ld=b.data[pid]
                ld['m_Type']=2
                ld['m_Color']=dict(zip(('r','g','b','a'),(*color,1.)))
                ld['m_Intensity']=.65
                ld['m_Range']=8.
                ld['m_Shadows']['m_Type']=0
    # Ground and walls, including Ground/Walls layers, use inherited native colliders.
    b.data[193]['m_LocalPosition']=dict(x=0.,y=0.,z=0.)
    b.data[194]['m_LocalPosition']=dict(x=5.4,y=0.,z=-3.6)
    for o in b.sf.objects.values():
        d=b.data[o.path_id]
        if o.type.name=='BoxCollider' and d['m_GameObject']['m_PathID']==36:
            d['m_Size']=dict(x=20.4,y=.4,z=16.8)
    for tid,x,z,yaw,length in ((199,-4.8,-3.6,0,16.8),(184,15.6,-3.6,0,16.8),(205,5.4,-12,90,20.4),(247,5.4,4.8,90,20.4)):
        b.data[tid]['m_LocalPosition']=dict(x=x,y=1.5,z=z)
        b.data[tid]['m_LocalRotation']=b.rotation(yaw)
        b.data[tid]['m_LocalScale']=dict(x=1.,y=1.,z=1.)
        goid=b.data[tid]['m_GameObject']['m_PathID']
        for o in b.sf.objects.values():
            d=b.data[o.path_id]
            if o.type.name=='BoxCollider' and d['m_GameObject']['m_PathID']==goid:
                d['m_Size']=dict(x=.25,y=3.,z=length)
    b.data[b.data[227]['m_GameObject']['m_PathID']]['m_IsActive']=False
    # Keep the full kitchen in frame, with room for the order rail.
    b.data[192]['m_LocalPosition']=dict(x=5.4,y=26.,z=-18.6)
    b.data[415].update(m_minDistance=29.,m_maxDistance=30.,m_maxUDistance=.25,m_maxRDistance=.25)
    # Update grid bounds around the new kitchen rather than relying on a huge donor grid.
    for pid,d in b.data.items():
        if 'm_gridHalfSize' in d:
            d['m_gridHalfSize']=dict(X=16,Y=1,Z=16)
    b.save(layout,out)
    return b.placements


def verify_scene(world, layout):
    data={pid:o.read_typetree() for pid,o in world.objects.items()}
    # Check actual native stub positions, rotations and cooking restrictions,
    # not just the layout JSON used to generate them.
    counters=[];belts=[];walls={};utensils=[];serves=[];plates=0
    room=layout['room']
    for pid,o in world.objects.items():
        d=data[pid]
        if o.type.name=='Transform':
            go=data[d['m_GameObject']['m_PathID']]
            name=go['m_Name']
            if name.startswith('Market_'):
                comp_data=[data[p['component']['m_PathID']] for p in go['m_Component']]
                if any('pseudoPrefabSO' in cd for cd in comp_data):
                    assert any('childGameObject' in cd for cd in comp_data), f'Missing live prefab initializer: {name}'
            if name in ('Market_WestWall','Market_EastWall','Market_BackCentre','Market_LowFrontWall'):
                walls[name]=d['m_LocalPosition']
            if name.startswith('Market_') and d['m_Father']['m_PathID']==200:
                p=d['m_LocalPosition']
                counters.append((name,p))
                assert layout['room']['left']+0.6 < p['x'] < layout['room']['right']-0.6, f'Station outside walls: {name}'
                assert layout['room']['south']+0.6 < p['z'] < layout['room']['north']-0.6, f'Station outside walls: {name}'
                # Native side walls extend 0.314 units inwards. The two-cell
                # appliances rotate their physical footprint with the transform.
                wide=name.startswith(('Market_Sink_','Market_ServingStation_'))
                q=d['m_LocalRotation'];side=abs(abs(q['y'])-.70710678)<.01
                ex,ez=(.6,1.2) if wide and side else (1.2,.6) if wide else (.6,.6)
                assert p['x']-ex>room['left']+.314 and p['x']+ex<room['right']-.314, f'Wall overlaps appliance: {name}'
                assert p['z']-ez>room['south']+.314 and p['z']+ez<room['north']-.314, f'Wall overlaps appliance: {name}'
        if 'conveySpeed' in d:
            belts.append(d)
            assert 0.5<=d['conveySpeed']<=0.8
        if 'capacity' in d and 'allowedIngredientSOs' in d:
            assert d['capacity']==1 and len(d['allowedIngredientSOs'])==1
            utensils.append(d)
        if 'plateCount' in d:
            plates+=d['plateCount']
        if 'plateReturn' in d:
            serves.append(d)
            assert 'returnClean' in data[d['plateReturn']['m_PathID']], 'Serving window points at wrong return'
    assert abs(walls['Market_WestWall']['x']-room['left'])<.001
    assert abs(walls['Market_EastWall']['x']-room['right'])<.001
    assert abs(walls['Market_BackCentre']['z']-room['north'])<.001
    assert abs(walls['Market_LowFrontWall']['z']-room['south'])<.001
    assert len(belts)==8
    assert len(utensils)==4 and len(serves)==2 and plates==6
    assert len(counters)>60
    # The native Rightwards prefab advances along -transform.right.
    for pid,o in world.objects.items():
        if o.type.name!='MonoBehaviour' or 'conveySpeed' not in data[pid]:continue
        go=data[data[pid]['m_GameObject']['m_PathID']]
        tid=next(p['component']['m_PathID'] for p in go['m_Component'] if world.objects[p['component']['m_PathID']].type.name=='Transform')
        t=data[tid];q=t['m_LocalRotation'];p=t['m_LocalPosition']
        dx=-(1-2*q['y']*q['y'])
        row=round((layout['grid']['origin'][1]-p['z'])/layout['grid']['cell'])
        assert (row==6 and dx>.99) or (row==8 and dx<-.99), 'Conveyor runs in wrong direction'
    # Native screenshot textures and floor maps must have pixels embedded.
    for o in world.objects.values():
        if o.type.name=='Texture2D':
            d=data[o.path_id]
            assert d['m_StreamData']['path']==''
            assert len(d['image data'])>0
    return len(counters)
