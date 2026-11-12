"""Geometry and binary regressions for the real generated map (unittest)."""
import copy
import json
import unittest
from pathlib import Path

import scene_v2
import UnityPy

HERE=Path(__file__).resolve().parent


class GeometryTests(unittest.TestCase):
    def setUp(self):
        self.layout=json.loads((HERE/'layout.json').read_text(encoding='utf8'))

    def test_map_is_connected_and_both_banks_have_jobs(self):
        self.assertGreater(scene_v2.validate_layout(self.layout),100)

    def test_blocked_spawn_is_rejected(self):
        self.layout['players'][0]=[7,5]
        with self.assertRaisesRegex(AssertionError,'spawn'):
            scene_v2.validate_layout(self.layout)

    def test_sealing_both_crossings_disconnects_kitchen(self):
        for p in self.layout['passages']:
            for c in p['columns']:
                for r in p['rows']:
                    self.layout['stations'].append(dict(kind='counter',cell=[c,r]))
        with self.assertRaisesRegex(AssertionError,'Disconnected'):
            scene_v2.validate_layout(self.layout)

    def test_two_cell_sink_overlap_is_rejected(self):
        self.layout['stations'].append(dict(kind='tomato',cell=[0,9]))
        with self.assertRaisesRegex(AssertionError,'Overlapping'):
            scene_v2.validate_layout(self.layout)

    def test_one_bank_cannot_supply_all_three_menus(self):
        # Move the sushi raw fish and seaweed from west to east, with no overlaps.
        for s in self.layout['stations']:
            if s['kind']=='fish':s['cell']=[15,4]
            if s['kind']=='seaweed':s['cell']=[15,5]
        with self.assertRaises(AssertionError):
            scene_v2.validate_layout(self.layout)


class BuiltSceneTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.layout=json.loads((HERE/'layout.json').read_text(encoding='utf8'))
        e=UnityPy.load(str(HERE/'build/manhl_relay_kitchen'/cls.layout['scene']))
        cls.world=next(f for b in e.files.values() for f in b.files.values() if f.name=='BuildPlayer-'+cls.layout['scene'])
        cls.env=e

    def test_native_walls_appliances_and_conveyor_directions(self):
        self.assertGreater(scene_v2.verify_scene(self.world,self.layout),60)

    def test_placeholder_without_live_initializer_is_rejected(self):
        e=UnityPy.load(next(iter(self.env.files.values())).save())
        world=next(f for b in e.files.values() for f in b.files.values() if f.name=='BuildPlayer-'+self.layout['scene'])
        for obj in world.objects.values():
            if obj.type.name!='GameObject':continue
            go=obj.read_typetree()
            if go['m_Name']=='Market_Counter_0_0':
                go['m_Component']=[p for p in go['m_Component'] if 'childGameObject' not in world.objects[p['component']['m_PathID']].read_typetree()]
                obj.save_typetree(go)
        e2=UnityPy.load(next(iter(e.files.values())).save())
        changed=next(f for b in e2.files.values() for f in b.files.values() if f.name=='BuildPlayer-'+self.layout['scene'])
        with self.assertRaisesRegex(AssertionError,'Missing live prefab initializer'):
            scene_v2.verify_scene(changed,self.layout)

    def test_old_wall_position_regression_is_rejected(self):
        original=self.world
        # A fresh serialization keeps this mutation isolated from the other checks.
        e=UnityPy.load(next(iter(self.env.files.values())).save())
        world=next(f for b in e.files.values() for f in b.files.values() if f.name=='BuildPlayer-'+self.layout['scene'])
        for obj in world.objects.values():
            if obj.type.name=='Transform':
                t=obj.read_typetree()
                go=world.objects[t['m_GameObject']['m_PathID']].read_typetree()
                if go['m_Name']=='Market_WestWall':
                    t['m_LocalPosition']['x']+=1.2
                    obj.save_typetree(t)
        # UnityPy rereads original data; re-open the changed bytes as the game does.
        e2=UnityPy.load(next(iter(e.files.values())).save())
        changed=next(f for b in e2.files.values() for f in b.files.values() if f.name=='BuildPlayer-'+self.layout['scene'])
        with self.assertRaises(AssertionError):scene_v2.verify_scene(changed,self.layout)

    def test_reversed_upper_conveyor_is_rejected(self):
        e=UnityPy.load(next(iter(self.env.files.values())).save())
        world=next(f for b in e.files.values() for f in b.files.values() if f.name=='BuildPlayer-'+self.layout['scene'])
        for obj in world.objects.values():
            if obj.type.name=='Transform':
                t=obj.read_typetree();go=world.objects[t['m_GameObject']['m_PathID']].read_typetree()
                if go['m_Name']=='Market_ConveyorStation_7_6':
                    t['m_LocalRotation']=dict(x=0.,y=0.,z=0.,w=1.)
                    obj.save_typetree(t)
        e2=UnityPy.load(next(iter(e.files.values())).save())
        world2=next(f for b in e2.files.values() for f in b.files.values() if f.name=='BuildPlayer-'+self.layout['scene'])
        with self.assertRaisesRegex(AssertionError,'wrong direction'):scene_v2.verify_scene(world2,self.layout)


if __name__=='__main__':unittest.main(verbosity=2)
