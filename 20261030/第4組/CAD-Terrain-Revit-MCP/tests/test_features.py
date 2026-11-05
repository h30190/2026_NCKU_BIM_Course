import sys,unittest,copy
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
import cad_pipeline
from site_features import height_label,apply_heights,road_surfaces
from shapely.geometry import Polygon

def e(layer,points,typ='LWPOLYLINE',text='',closed=False):return dict(layer=layer,points=points,type=typ,text=text,closed=closed,handle='A')
def building(x=0):return dict(id='B1',points=[[x,0],[x+4,0],[x+4,4],[x,4]],base_z=-.3,height=9.3,assumed_height_above_highest_edge_m=9)

class FeatureTests(unittest.TestCase):
    def cfg(self,**kw):return dict(drawing_unit_m=1,default_building_height_m=9,**kw)
    def test_height_units_and_floor_labels(self):
        self.assertEqual(height_label('H=12500mm','height_text',1,3)[0],12.5)
        self.assertEqual(height_label('4層','floors',1,3.2)[0],12.8)
        self.assertIsNone(height_label('北新路169巷','height_text',1,3))
        self.assertIsNone(height_label('EL=42','height_text',1,3))
    def test_assign_height_only_inside(self):
        b,r,issues=apply_heights([building(),building(10)],[e('heights',[[2,2,0]],'TEXT','H=12m')],self.cfg(building_height_mode='height_text',building_height_layer='heights'),0,0,0)
        self.assertEqual(b[0]['height_above_ground_m'],12);self.assertFalse(b[0]['height_is_assumed'])
        self.assertEqual(b[1]['height_above_ground_m'],9);self.assertTrue(b[1]['height_is_assumed']);self.assertEqual(r['height_assumed'],1)
    def test_conflict_falls_back_or_skips(self):
        labels=[e('h',[[1,1,0]],'TEXT','12'),e('h',[[3,3,0]],'TEXT','16')]
        cfg=self.cfg(building_height_mode='height_text',building_height_layer='h')
        b,r,issues=apply_heights([building()],labels,cfg,0,0,0);self.assertTrue(b[0]['height_is_assumed']);self.assertEqual(len(issues),1)
        b,r,_=apply_heights([building()],labels,{**cfg,'missing_height_policy':'skip'},0,0,0);self.assertEqual(b,[]);self.assertEqual(r['height_skipped'],1)
    def test_roof_elevation_is_not_building_height(self):
        b=building();b['height']=11.3 # highest estimated ground is local 2m
        out,_,_=apply_heights([b],[e('roof',[[0,0,55],[4,0,55],[4,4,55],[0,4,55]],closed=True)],self.cfg(building_height_mode='roof_z',building_height_layer='roof'),0,0,40)
        self.assertEqual(out[0]['height_above_ground_m'],13)
    def test_road_width_area_and_slope(self):
        terrain=[[-10,-10,0],[10,-10,0],[10,10,10],[-10,10,10]]
        roads,r,_=road_surfaces([e('road',[[-5,0,0],[5,0,0]],'LINE')],self.cfg(road_mode='centerlines',road_layers=['road'],road_width_m=4,accept_road_interpretation=True),terrain,0,0)
        self.assertAlmostEqual(r['road_area_m2'],40)
        area=0
        for road in roads:
            for face in road['triangles']:
                area+=Polygon([(x,y) for x,y,z in face]).area
                for x,y,z in face:self.assertAlmostEqual(z,.5*y+5.05)
        self.assertAlmostEqual(area,40)
    def test_road_centerline_requires_explicit_interpretation(self):
        with self.assertRaisesRegex(ValueError,'請確認道路'):road_surfaces([e('road',[[0,0,0],[1,0,0]],'LINE')],self.cfg(road_mode='centerlines',road_layers=['road']),[],0,0)
    def test_open_road_boundaries_are_not_closed_automatically(self):
        with self.assertRaisesRegex(ValueError,'沒有封閉'):road_surfaces([e('r',[[0,0,0],[5,0,0]],'LINE')],self.cfg(road_mode='closed_areas',road_layers=['r'],accept_road_interpretation=True),[[0,0,0],[10,0,0],[0,10,1]],0,0)
    def test_closed_road_surface_clipped_to_terrain(self):
        roads,r,_=road_surfaces([e('r',[[-5,-5,0],[15,-5,0],[15,15,0],[-5,15,0]],closed=True)],self.cfg(road_mode='closed_areas',road_layers=['r'],accept_road_interpretation=True),[[0,0,0],[10,0,0],[10,10,1],[0,10,1]],0,0)
        self.assertAlmostEqual(r['road_area_m2'],100)
if __name__=='__main__':unittest.main()
