"""Opt-in Claude regression for reusing a fictional legacy repair and avoiding a repeat.

Uses the signed-in Claude Code account with two synthetic stdio MCP servers. Never reads
SQLite, the CaseGraph service, the real MCP configuration, or a Revit model.
"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import casegraph_mcp
import claude_agent
from app import solutions


def synthetic_case():
    def node(ident, name, category, low, high):
        return dict(node_id='fictional:'+str(ident), node_type='ELEMENT', name=name,
                    source_document='fictional', source_element_id='fictional-uid-'+str(ident),
                    properties=dict(element_id=str(ident), category=category, family=None, type_name=None,
                                    units='mm', bounding_box=dict(units='mm', min=low, max=high)))
    wall = node(101, 'Synthetic wall', 'Walls', dict(x=0, y=0, z=0), dict(x=200, y=4000, z=3000))
    opening = node(102, 'Synthetic opening', 'Rectangular Straight Wall Opening',
                   dict(x=0, y=1000, z=2000), dict(x=200, y=1400, z=2600))
    return dict(id=42, revision=1, status='VERIFIED', reviewer='Synthetic reviewer', history=[],
        solution='模型變更紀錄（2026-01-01 10:00；對比 RFI-42 第 1 版快照）\n'
                 '・Synthetic wall（ID 101）：參數 · Volume 1.0 m³ → 0.9 m³\n'
                 '・Synthetic opening（ID 102）：位置（外框中心） 移動 150 mm → X +0／Y -150／Z +0 mm',
        draft=dict(title='Synthetic opening adjustment', description='Opening is displaced from its intended position.',
                   issue_type='GEOMETRY_CLASH', graph=dict(root=wall['node_id'], nodes=[wall, opening], edges=[dict(
                       source=wall['node_id'], target=opening['node_id'], relationship_type='NEARBY',
                       evidence='Synthetic bounding box candidate', validation_status='candidate')])))


def fixture_api(route, payload=None):
    case = synthetic_case()
    recipe = solutions.recipe_from(case, [])
    if route == '/cases/42':
        return case
    if route == '/cases/42/recipe':
        return recipe
    if route == '/cases/42/applications':
        return []
    if route == '/cases/42/fix-plan':
        solutions.recipe = lambda case_id: recipe
        return solutions.fix_plan(42, solutions.FixPlanRequest.model_validate(payload))
    raise ValueError('Synthetic fixture does not support '+route)


def serve(role, state):
    for line in sys.stdin:
        message = json.loads(line)
        if role == 'casegraph':
            response = casegraph_mcp.handle(message, fixture_api)
        elif 'id' not in message:
            response = None
        else:
            method = message['method']
            if method == 'initialize':
                result = dict(protocolVersion=message['params']['protocolVersion'], capabilities=dict(tools={}),
                              serverInfo=dict(name='fictional-revit', version='1'))
            elif method == 'tools/list':
                result = dict(tools=[dict(name=name, description='Read synthetic element data; geometry coordinates are mm.',
                    inputSchema=dict(type='object', properties=dict(elementId=dict(type='integer'), geometryType=dict(type='string')),
                                     required=['elementId']), annotations=dict(readOnlyHint=True))
                    for name in ('get_element_info', 'get_element_geometry')])
            elif method == 'tools/call':
                args = message['params']['arguments']
                node = next(n for n in synthetic_case()['draft']['graph']['nodes'] if int(n['properties']['element_id']) == args['elementId'])
                if message['params']['name'] == 'get_element_info':
                    data = dict(ElementId=args['elementId'], Name=node['name'], Category=node['properties']['category'], Parameters=[])
                elif message['params']['name'] == 'get_element_geometry':
                    box = node['properties']['bounding_box']
                    offset = -150 if state == 'after' and args['elementId'] == 102 else 0
                    data = dict(ElementId=args['elementId'], BoundingBox=dict(HasBoundingBox=True,
                        **{side.title()+axis.upper(): box[side][axis] + (offset if axis == 'y' else 0)
                           for side in ('min', 'max') for axis in ('x', 'y', 'z')}))
                else:
                    raise ValueError('Unexpected tool')
                result = dict(content=[dict(type='text', text=json.dumps(data))])
            else:
                result = {}
            response = dict(jsonrpc='2.0', id=message['id'], result=result)
        if response is not None:
            print(json.dumps(response), flush=True)


def smoke(state):
    fixture = str(Path(__file__).resolve())
    revit = dict(command=sys.executable, args=['-I', fixture, '--serve', 'revit', state])
    case = synthetic_case()
    with tempfile.TemporaryDirectory(prefix='casegraph-synthetic-reuse-') as directory:
        folder = Path(directory)
        command = claude_agent.claude_command(claude_agent.find_claude(), folder, revit, 'http://127.0.0.1:1',
            casegraph_server=dict(command=sys.executable, args=['-I', fixture, '--serve', 'casegraph', state]))
        prompt = claude_agent.plan_prompt(case['draft']['graph']['nodes'][0], [dict(case_id=42, title=case['draft']['title'],
            verified=True, status='VERIFIED', solution=case['solution'], replayable_steps=0)],
            '參考過去修正記錄去修正現有相同問題，請自行查詢現況並產生可審閱步驟。', members=case['draft']['graph']['nodes'][1:])
        prompt += '\nThis is entirely fictional test data. Return MCP arguments as arguments_json. Do not perform modifications.'
        result = subprocess.run(command, input=prompt, capture_output=True, encoding='utf-8', timeout=180,
                                creationflags=claude_agent.CREATE_NO_WINDOW, cwd=str(folder))
        if result.returncode:
            raise RuntimeError((result.stderr or result.stdout)[-1800:])
        calls = claude_agent.tool_call_status(result.stdout)
        value = claude_agent.parse_plan(claude_agent.claude_result(result.stdout),
            claude_agent.repair_scope(case['draft']['graph']['nodes'][0], case['draft']['graph']['nodes'][1:]))
        print(json.dumps(dict(synthetic=True, state=state, tool_calls=calls, plan=value), ensure_ascii=False))
        for server, tool in [('casegraph', 'casegraph_get_case'), ('casegraph', 'casegraph_fix_plan'), ('revit', 'get_element_geometry')]:
            assert any(c['server'] == server and c['tool'] == tool and c['ok'] for c in calls), tool
        assert not value['questions'], value
        if state == 'before':
            assert len(value['steps']) == 1, value
            step = value['steps'][0]
            assert step['tool'] == 'move_element' and step['source_case'] == 42, step
            assert step['arguments'] == dict(elementId=102, dx=0, dy=-150, dz=0), step
        else:
            assert value['steps'] == [], value
        print('PASS: synthetic legacy reuse '+state)


if __name__ == '__main__':
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8')
    if len(sys.argv) == 4 and sys.argv[1] == '--serve':
        serve(sys.argv[2], sys.argv[3])
    else:
        for state in ('before', 'after'):
            smoke(state)
