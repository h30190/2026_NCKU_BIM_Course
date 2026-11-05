"""MCP stdio adapter. Preprocessing requires Python 3.12 x64 and bundled Shapely."""
import json, os, socket, sys
from pathlib import Path

TOOLS=[
    {'name':'site_inspect','description':'檢查已安裝的 CAD 示意基地資料與 Revit 連線。唯讀。',
     'inputSchema':{'type':'object','properties':{},'additionalProperties':False},
     'annotations':{'readOnlyHint':True}},
    {'name':'site_build','description':'在作用中的 Revit 2024 專案新增插值地形與候選房屋實體。不是實測模型。不得因逾時直接重試，請先查 site_status。',
     'inputSchema':{'type':'object','properties':{'expected_dataset_id':{'type':'string'},'accept_inferred':{'type':'boolean','description':'明確接受插值地形、外推區和假設樓高'}},'required':['accept_inferred','expected_dataset_id'],'additionalProperties':False},
     'annotations':{'readOnlyHint':False,'destructiveHint':False,'idempotentHint':True}},
    {'name':'site_status','description':'查詢最近一次建模結果。唯讀；含略過房屋與錯誤清單。',
     'inputSchema':{'type':'object','properties':{},'additionalProperties':False},
     'annotations':{'readOnlyHint':True}}
]

TOOLS.extend([
 {'name':'site_scan_cad','description':'Read a user-selected DWG/DXF copy and list layers. Requires AutoCAD. No Revit changes.',
  'inputSchema':{'type':'object','properties':{'source_path':{'type':'string'}},'required':['source_path'],'additionalProperties':False}},
 {'name':'site_prepare_cad','description':'Prepare preview using confirmed units/layers. Does not select or build. Show report before activation.',
  'inputSchema':{'type':'object','properties':{'session_id':{'type':'string'},'options':{'type':'object','properties':{
   'drawing_unit_m':{'type':'number'},'elevation_unit_m':{'type':'number'},'grid_spacing_m':{'type':'number'},'contour_simplify_m':{'type':'number','minimum':0,'maximum':100},'default_building_height_m':{'type':'number'},
   'building_height_mode':{'type':'string','enum':['assumed','height_text','floors','roof_z']},
   'building_height_layer':{'type':'string'},'building_height_unit_m':{'type':'number'},'storey_height_m':{'type':'number'},
   'missing_height_policy':{'type':'string','enum':['assume','skip']},'road_mode':{'type':'string','enum':['none','closed_areas','centerlines']},
   'road_layers':{'type':'array','items':{'type':'string'}},'road_width_m':{'type':'number'},'accept_road_interpretation':{'type':'boolean'},
   'terrain_mode':{'type':'string','enum':['cad_z','text_idw']},'contour_layer':{'type':'string'},'contour_layer_secondary':{'type':'string','description':'Optional second contour layer; combined using existing CAD Z values'},'label_layer':{'type':'string'},'building_layer':{'type':'string'},'accept_inferred':{'type':'boolean'}},
   'required':['drawing_unit_m','elevation_unit_m','grid_spacing_m','default_building_height_m','terrain_mode','contour_layer'],'additionalProperties':False}},'required':['session_id','options'],'additionalProperties':False}},
 {'name':'site_activate_cad','description':'Select checked dataset for next build. Present source, units, report and limitations first.',
  'inputSchema':{'type':'object','properties':{'session_id':{'type':'string'},'dataset_id':{'type':'string'}},'required':['session_id','dataset_id'],'additionalProperties':False}}
])

def bridge(action, accept=False, expected=None):
    folder=Path(os.environ.get('LOCALAPPDATA',str(Path.home())))/'CADTerrainMCP'
    token=(folder/'bridge-token.txt').read_text(encoding='ascii').strip()
    req={'token':token,'action':action,'accept_inferred':accept,'expected_dataset_id':expected}
    with socket.create_connection(('127.0.0.1',18765),timeout=5) as sock:
        sock.settimeout(130)
        sock.sendall((json.dumps(req)+'\n').encode())
        data=bytearray()
        while not data.endswith(b'\n'):
            chunk=sock.recv(65536)
            if not chunk:break
            data.extend(chunk)
            if len(data)>4000000:raise ValueError('Bridge response too large')
    if not data:raise ConnectionError('No response from Revit bridge')
    return json.loads(data)

def dispatch(req):
    method=req.get('method'); params=req.get('params') or {}
    if method=='initialize':
        requested=params.get('protocolVersion')
        supported=('2024-11-05','2025-03-26','2025-06-18')
        return {'protocolVersion':requested if requested in supported else '2024-11-05',
                'capabilities':{'tools':{}},'serverInfo':{'name':'cad-terrain-revit2024','version':'3.0.0'}}
    if method=='ping':return {}
    if method=='tools/list':return {'tools':TOOLS}
    if method=='tools/call':
        name=params.get('name'); args=params.get('arguments') or {}
        try:
            if name=='site_build':
                if args.get('accept_inferred') is not True:raise ValueError('須明確接受示意地形與假設樓高：accept_inferred=true')
                if not args.get('expected_dataset_id'):raise ValueError('請先 site_inspect，提供 expected_dataset_id')
                current=bridge('inspect')
                if current.get('kit_version')!='3.0':raise ValueError('請重新啟動Revit載入v3外掛。')
                if current.get('dataset_id')!=args['expected_dataset_id']:raise ValueError('資料已更換，請重新檢查。')
                result=bridge('build',True,args['expected_dataset_id'])
            elif name=='site_inspect':result=bridge('inspect')
            elif name=='site_status':result=bridge('status')
            elif name in ('site_scan_cad','site_prepare_cad','site_activate_cad'):
                import cad_pipeline as pipeline
                if name=='site_scan_cad':result=pipeline.scan(args['source_path'])
                elif name=='site_prepare_cad':result=pipeline.prepare(args['session_id'],args['options'])
                else:result=pipeline.activate_dataset(args['session_id'],args['dataset_id'])
            else:raise ValueError('Unknown tool: '+str(name))
            return {'content':[{'type':'text','text':json.dumps(result,ensure_ascii=False)}],
                    'isError':result.get('status') in ('error','expired','busy')}
        except Exception as exc:
            return {'content':[{'type':'text','text':str(exc)}],'isError':True}
    raise KeyError('Method not found: '+str(method))

def main():
    sys.stdin.reconfigure(encoding='utf-8');sys.stdout.reconfigure(encoding='utf-8')
    # MCP stdio is newline-delimited JSON-RPC. Never print banners on stdout.
    for line in sys.stdin:
        req=None
        try:
            if len(line)>1000000:raise ValueError('Request too large')
            req=json.loads(line)
            if not isinstance(req,dict):raise ValueError('Expected JSON object')
            if 'id' not in req:continue
            result=dispatch(req)
            response={'jsonrpc':'2.0','id':req['id'],'result':result}
        except Exception as exc:
            response={'jsonrpc':'2.0','id':req.get('id') if isinstance(req,dict) else None,
                      'error':{'code':-32601 if isinstance(exc,KeyError) else -32600,'message':str(exc)}}
        sys.stdout.write(json.dumps(response,ensure_ascii=False)+'\n');sys.stdout.flush()

if __name__=='__main__':main()
