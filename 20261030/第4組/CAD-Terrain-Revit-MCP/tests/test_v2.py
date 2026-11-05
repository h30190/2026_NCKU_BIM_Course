import json, os, sys, tempfile, unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
import cad_pipeline as p
import mcp_server as m

def entity(layer,points,typ='LWPOLYLINE',text='',closed=False):
    return dict(layer=layer,points=points,type=typ,text=text,closed=closed,status='ok',handle='1')

class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.test_root=Path(os.environ.get('CAD_TEST_TMP',str(Path.cwd()/'work'/'v2-tests'))).resolve();self.test_root.mkdir(parents=True,exist_ok=True)
        self.tmp=tempfile.TemporaryDirectory(dir=self.test_root);os.environ['CAD_TERRAIN_RUNTIME']=self.tmp.name
        self.cfg=dict(drawing_unit_m=1,elevation_unit_m=1,grid_spacing_m=5,default_building_height_m=9,terrain_mode='cad_z',contour_layer='terrain',building_layer='houses',accept_inferred=True)
    def tearDown(self):
        assert Path(self.tmp.name).resolve().is_relative_to(self.test_root)
        self.tmp.cleanup();os.environ.pop('CAD_TERRAIN_RUNTIME',None)
    def fixture(self,sid='a'*32,flat=False):
        folder=p.runtime()/'sessions'/sid;folder.mkdir(parents=True,exist_ok=True)
        entities=[entity('terrain',[[0,0,0],[20,0,0],[20,20,0 if flat else 5],[0,20,0 if flat else 5]],closed=True),
                  entity('houses',[[2,2,0],[8,2,0],[8,8,0],[2,8,0]],closed=True)]
        p.write_json(folder/'scan.json',dict(source_name=sid+'.dwg',source_path='fixture',source_sha256=sid))
        p.write_json(folder/'entities.json',entities)
        return sid,folder,entities
    def test_real_z_and_scale(self):
        sid,_,_=self.fixture();a=p.prepare(sid,self.cfg)
        self.assertEqual(a['report']['width_m'],20);self.assertEqual(a['report']['buildings_prepared'],1)
        cfg={**self.cfg,'drawing_unit_m':2};b=p.prepare(sid,cfg)
        self.assertEqual(b['report']['width_m'],40);self.assertEqual(b['report']['height_range_m'],[0,10])
        self.assertNotEqual(a['dataset_id'],b['dataset_id'])
        self.assertFalse((p.runtime()/'active-selection.json').exists())
    def test_dense_constant_elevation_lines_simplify(self):
        sid,folder,_=self.fixture()
        entities=[entity('terrain',[[i/100,0,25] for i in range(10001)]),entity('terrain',[[i/100,20,50] for i in range(10001)])]
        p.write_json(folder/'entities.json',entities)
        cfg={**self.cfg,'building_layer':None,'contour_simplify_m':.1}
        result=p.prepare(sid,cfg)
        self.assertEqual(result['report']['terrain_points'],4)
        self.assertEqual(result['report']['height_range_m'],[25,50])
        self.assertEqual(result['report']['width_m'],100)
    def test_variable_z_line_not_flattened(self):
        sid,folder,_=self.fixture()
        entities=[entity('terrain',[[0,0,0],[10,0,5],[20,0,0],[20,20,0],[0,20,0]])]
        p.write_json(folder/'entities.json',entities)
        result=p.prepare(sid,{**self.cfg,'building_layer':None,'contour_simplify_m':10})
        data=json.loads(Path(result['prepared_path']).read_text(encoding='utf-8'))
        self.assertIn([0,-10,5],data['terrain'])
    def test_flat_is_rejected_without_fallback(self):
        sid,_,_=self.fixture(flat=True)
        with self.assertRaisesRegex(ValueError,'Z高度相同'):p.prepare(sid,self.cfg)
    def test_conflicting_z(self):
        sid,folder,entities=self.fixture();entities.append(entity('terrain',[[0,0,8]],typ='POINT'));p.write_json(folder/'entities.json',entities)
        with self.assertRaisesRegex(ValueError,'衝突'):p.prepare(sid,self.cfg)
    def test_switch_dataset_and_stale_rejection(self):
        a,_,_=self.fixture();b,_,_=self.fixture('b'*32)
        ar=p.prepare(a,self.cfg);br=p.prepare(b,self.cfg)
        p.activate_dataset(a,ar['dataset_id']);p.activate_dataset(b,br['dataset_id'])
        selected=json.loads((p.runtime()/'active-selection.json').read_text(encoding='utf-8'))
        self.assertEqual(selected['session_id'],b)
        with self.assertRaises(ValueError):p.activate_dataset(a,br['dataset_id'])
        self.assertEqual(json.loads((p.runtime()/'active-selection.json').read_text(encoding='utf-8'))['session_id'],b)
    def test_text_mode_acceptance_and_control_values(self):
        sid,folder,entities=self.fixture(flat=True)
        entities += [entity('labels',[[x,y,0]],'TEXT',str(z)) for x,y,z in [(0,0,40),(20,0,45),(0,20,50)]]
        p.write_json(folder/'entities.json',entities)
        cfg={**self.cfg,'terrain_mode':'text_idw','label_layer':'labels','accept_inferred':False}
        with self.assertRaises(ValueError):p.prepare(sid,cfg)
        cfg['accept_inferred']=True;r=p.prepare(sid,cfg)
        data=json.loads(Path(r['prepared_path']).read_text(encoding='utf-8'))
        self.assertIn([-10,-10,0],data['terrain']);self.assertIn([10,-10,5],data['terrain']);self.assertIn([-10,10,10],data['terrain'])
    def test_path_traversal(self):
        with self.assertRaises(ValueError):p.session_dir('../outside')
    def test_missing_layers(self):
        sid,_,_=self.fixture()
        with self.assertRaisesRegex(ValueError,'找不到圖層'):p.prepare(sid,{**self.cfg,'building_layer':'absent'})
    def test_build_rejects_unaccepted_or_stale(self):
        call=lambda args:m.dispatch({'method':'tools/call','params':{'name':'site_build','arguments':args}})
        self.assertTrue(call({})['isError']);self.assertTrue(call({'accept_inferred':True})['isError'])
        old=m.bridge;m.bridge=lambda *a:{'kit_version':'3.0','dataset_id':'new'}
        try:self.assertTrue(call({'accept_inferred':True,'expected_dataset_id':'old'})['isError'])
        finally:m.bridge=old
    def test_mcp_tools(self):
        tools=m.dispatch({'method':'tools/list'})['tools'];self.assertEqual(len(tools),6)
        build=next(t for t in tools if t['name']=='site_build');self.assertIn('expected_dataset_id',build['inputSchema']['required'])
if __name__=='__main__':unittest.main()
