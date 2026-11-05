"""Opt-in Claude check using fictional SOLVE evidence only; no real Revit connection."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import claude_agent
import claude_reuse_smoke as reuse


def case():
    value = reuse.synthetic_case()
    value['draft']['graph']['nodes'].append(dict(node_id='fictional:103', node_type='ELEMENT',
        name='Synthetic duct', source_document='fictional', source_element_id='fictional-uid-103',
        properties=dict(element_id='103', category='Ducts', family='Oval Duct', type_name='Synthetic',
            units='mm', bounding_box=dict(units='mm', min=dict(x=-500,y=900,z=2150), max=dict(x=100,y=1200,z=2450)))))
    return value


def serve():
    for line in sys.stdin:
        message = json.loads(line)
        if 'id' not in message:
            continue
        method, params = message['method'], message.get('params', {})
        if method == 'initialize':
            result = dict(protocolVersion=params['protocolVersion'], capabilities=dict(tools={}), serverInfo=dict(name='fictional-opening', version='1'))
        elif method == 'tools/list':
            names = ['get_element_info','get_selected_elements','query_elements','get_element_geometry',
                     'preview_wall_opening','casegraph_get_case','casegraph_fix_plan']
            properties=dict(elementId=dict(type='integer'),geometryType=dict(type='string'),category=dict(type='string'),
                            case_id=dict(type='integer'),target_element_id=dict(type='integer'),
                            ductId=dict(type='integer'),widthMm=dict(type='number'),heightMm=dict(type='number'),
                            offsetAlongWallMm=dict(type='number'),offsetZMm=dict(type='number'))
            required={'get_element_info':['elementId'],'get_element_geometry':['elementId'],
                      'preview_wall_opening':['elementId','ductId','widthMm','heightMm'],
                      'casegraph_get_case':['case_id'],'casegraph_fix_plan':['case_id','target_element_id']}
            result = dict(tools=[dict(name=n, description='Read fictional test data; lengths are mm.',
                inputSchema=dict(type='object', properties=properties,required=required.get(n,[])), annotations=dict(readOnlyHint=True)) for n in names])
        elif method == 'tools/call':
            name, args = params['name'], params.get('arguments', {})
            if name == 'casegraph_get_case':
                v=case()
                data=dict(case_id=42, status='VERIFIED', solution=v['solution'],
                          snapshot=dict(nodes=v['draft']['graph']['nodes'], revision=1), recipe=dict(steps=[]))
            elif name == 'casegraph_fix_plan':
                data=dict(steps=[], unresolved=['No existing opening on the current wall; the recorded move needs an opening.'])
            elif name == 'get_element_info':
                data=dict(ElementId=args['elementId'], Category='Walls' if args['elementId']==201 else 'Ducts',
                          Name='Synthetic wall' if args['elementId']==201 else 'Synthetic duct',
                          Family='Basic Wall' if args['elementId']==201 else 'Oval Duct',
                          WidthMm=200 if args['elementId']==201 else 300, HeightMm=300)
            elif name == 'get_selected_elements':
                data=dict(Count=2, Elements=[dict(Id=201,Category='Walls'),dict(Id=203,Category='Ducts')])
            elif name == 'query_elements':
                data=dict(Count=0,Elements=[])
            elif name == 'get_element_geometry':
                data=dict(BoundingBox=dict(MinX=9500,MaxX=10100,MinY=2900,MaxY=3200,MinZ=2150,MaxZ=2450))
            elif name == 'preview_wall_opening':
                if args.get('elementId') != 201 or args.get('ductId') != 203 or abs(args.get('widthMm',0)-400)>0.01 or abs(args.get('heightMm',0)-600)>0.01:
                    raise ValueError('Expected current IDs and dimensions derived from the fictional SOLVE opening.')
                data=dict(status='ready', expectedGeometry='b'*64, **args)
            else:
                raise ValueError(name)
            result=dict(content=[dict(type='text',text=json.dumps(data))])
        else:
            result={}
        print(json.dumps(dict(jsonrpc='2.0',id=message['id'],result=result)),flush=True)


def smoke():
    fixture=dict(command=sys.executable,args=['-I',str(Path(__file__).resolve()),'--serve'])
    target=dict(node_id='fictional:201', name='Synthetic wall', source_document='fictional',
                properties=dict(element_id='201',category='Walls',family='Basic Wall',type_name='Synthetic'))
    members=[dict(name='Synthetic duct', source_document='fictional', properties=dict(element_id='203'))]
    with tempfile.TemporaryDirectory(prefix='casegraph-fictional-opening-') as directory:
        folder=Path(directory)
        command=claude_agent.claude_command(claude_agent.find_claude(),folder,fixture,'http://127.0.0.1:1',casegraph_server=fixture)
        prompt=claude_agent.plan_prompt(target,[dict(case_id=42,title='Synthetic SOLVE',verified=True,status='VERIFIED',
            solution=case()['solution'],replayable_steps=0)],
            '在目前牆上建立配合所選風管的開口。所有尺寸都參考 SOLVE；沒有舊開口，請自行查詢記錄尺寸，不要詢問已有的資料。', members=members)
        prompt+='\nReturn tool arguments as arguments_json, a JSON-encoded object.'
        completed=subprocess.run(command,input=prompt,text=True,encoding='utf-8',capture_output=True,cwd=folder,
                                 timeout=240,creationflags=claude_agent.CREATE_NO_WINDOW)
        if completed.returncode:
            raise RuntimeError(completed.stderr[-1000:])
        result=claude_agent.parse_plan(claude_agent.claude_result(completed.stdout), claude_agent.repair_scope(target, members))
        assert not result['questions'] and not result['problems'],result
        assert len(result['steps'])==1,result
        step=result['steps'][0]
        assert step['tool']=='create_wall_opening',result
        args=step['arguments']
        assert args['elementId']==201 and args['ductId']==203 and args['widthMm']==400 and args['heightMm']==600,result
        assert args['expectedGeometry']=='b'*64,result
        print('PASS: Claude derived opening dimensions from fictional SOLVE, previewed current geometry, and produced one executable creation step.')


if __name__=='__main__':
    serve() if '--serve' in sys.argv else smoke()
