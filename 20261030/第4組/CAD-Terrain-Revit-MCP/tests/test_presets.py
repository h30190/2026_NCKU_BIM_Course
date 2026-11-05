import unittest,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
from cad_presets import nlsc_defaults
import test_v2 as fixtures
from test_v2 import entity
import cad_pipeline as p
class PresetTests(unittest.TestCase):
    def test_pair_and_units(self):
        r=nlsc_defaults([{'name':n,'curve_count':1} for n in ['9810101','9810102_首曲線','9310100']])
        self.assertEqual(r['contour_layer'],'9810101');self.assertEqual(r['contour_layer_secondary'],'9810102_首曲線')
        self.assertEqual(r['drawing_unit_m'],1)
    def test_unknown_codes_no_guess(self):
        self.assertIsNone(nlsc_defaults([{'name':'98112_首曲線','curve_count':1}]))
    def test_ambiguous_code_not_chosen(self):
        self.assertIsNone(nlsc_defaults([{'name':n,'curve_count':1} for n in ['9810101_a','9810101_b']]))
class CombinedTests(unittest.TestCase):
    setUp=fixtures.PipelineTests.setUp
    tearDown=fixtures.PipelineTests.tearDown
    fixture=fixtures.PipelineTests.fixture
    def test_two_layers_preserve_actual_heights(self):
        sid,folder,_=self.fixture()
        p.write_json(folder/'entities.json',[entity('terrain',[[0,0,25],[20,0,25]]),entity('fine',[[0,20,30],[20,20,30]])])
        result=p.prepare(sid,{**self.cfg,'building_layer':None,'contour_layer_secondary':'fine'})
        self.assertEqual(result['report']['contour_layers'],['terrain','fine'])
        self.assertEqual(result['report']['height_range_m'],[25,30])
if __name__=='__main__':unittest.main()
