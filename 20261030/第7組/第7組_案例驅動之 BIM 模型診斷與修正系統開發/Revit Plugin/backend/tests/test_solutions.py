import copy
import json
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from casegraph.model_changes import change_report, element_changes
from app.solutions import recipe_from
import casegraph_mcp
from test_element_search import client

FT = 304.8
MM_UNIT = 'autodesk.unit.unit:millimeters-1.0.1'


def door(width=900., type_id='100', type_name='900 x 2100', facing=1., mark='D-01'):
    return dict(node_id='doc:door', node_type='ELEMENT', name='門 D-01', source_document='doc', source_element_id='door', properties=dict(
        element_id='334206', category='Doors', family='單開門', type_name=type_name, type_id=type_id,
        dimensions={'Width': width}, location=dict(x=0., y=0., z=0.), FacingOrientation=dict(x=0., y=facing, z=0.),
        parameters={'instance:Width': dict(value=str(int(width)), storage='Double', raw=width/FT, unit=MM_UNIT, builtin='DOOR_WIDTH'),
                    'instance:Mark': dict(value=mark, storage='String', raw=mark, builtin='ALL_MODEL_MARK'),
                    'type:Fire Rating': dict(value='60 min', storage='String', raw='60 min'),
                    'instance:Level': dict(value='1FL', storage='ElementId', raw='311'),
                    'instance:Comments': dict(value=''), 'instance:Sill Height': dict(value='900')}))


def wall(x=0.):
    return dict(node_id='doc:wall', node_type='ELEMENT', name='RC 牆', source_document='doc', source_element_id='wall', properties=dict(
        element_id='334210', category='Walls', family='Basic Wall', type_name='RC 200', type_id='500',
        bounding_box=dict(min=dict(x=x, y=0., z=0.), max=dict(x=x+4000., y=200., z=3000.))))


def fixed_door():
    d = door(width=1000., type_id='101', type_name='1000 x 2100', facing=-1., mark='D-01A')
    d['properties']['parameters']['type:Fire Rating'] = dict(value='90 min', storage='String', raw='90 min')
    d['properties']['parameters']['instance:Level'] = dict(value='2FL', storage='ElementId', raw='312')
    d['properties']['parameters']['instance:Comments'] = dict(value='checked')
    d['properties']['parameters']['instance:Sill Height'] = dict(value='1000')
    return d


def record_case(client, verify=True):
    graph = dict(root='doc:door', nodes=[door(), wall()], edges=[dict(
        source='doc:door', target='doc:wall', relationship_type='HOSTED_BY', evidence='Revit FamilyInstance.Host', validation_status='verified')])
    case = client.post('/graph/cases', json=dict(title='門與牆碰撞', description='門扇開啟時與 RC 牆碰撞，需要調整門寬及開向',
                                               issue_type='GEOMETRY_CLASH', graph=graph)).json()
    client.post('/graph/cases/{}/status'.format(case['id']), json=dict(status='REPORTED'))
    live = {'door': fixed_door(), 'wall': wall(150.)}
    report = change_report(case, 'doc', '2026-10-30 10:00', lambda r: (r['source_element_id'], 'Revit UniqueId'),
                           lambda uid: copy.deepcopy(live[uid]))
    client.post('/graph/cases/{}/status'.format(case['id']),
                json=dict(status='RESOLVED', solution='改用 1000 寬門型並反轉開向；牆移開 150 mm。', changes=report))
    if verify:
        client.post('/graph/cases/{}/status'.format(case['id']), json=dict(status='VERIFIED', reviewer='審核者'))
    return case['id']


def test_changes_carry_replayable_actions_with_internal_units():
    rows = element_changes(door(), fixed_door())
    actions = dict((r['field'], r['action']) for r in rows)
    assert actions['類型'] == dict(op='change_type', from_type_id='100', to_type_id='101', family='單開門',
                                   type_name='1000 x 2100', category='Doors')
    width = actions['寬度']
    assert width['op'] == 'set_parameter' and width['name'] == 'Width' and width['storage'] == 'Double'
    assert width['after_raw'] == pytest.approx(1000/FT) and width['after_mm'] == 1000. and width['builtin'] == 'DOOR_WIDTH'
    assert actions['朝向'] == dict(op='flip', flip='facing')
    assert actions['參數 · Mark']['after_raw'] == 'D-01A'
    assert actions['類型參數 · Fire Rating']['scope'] == 'type'
    assert actions['參數 · Level']['storage'] == 'ElementId'
    turned = door()
    turned['properties']['FacingOrientation'] = dict(x=1., y=0., z=0.)
    assert element_changes(door(), turned)[0]['action']['op'] == 'rotate'


def test_recipe_keeps_roles_and_relations(client):
    case_id = record_case(client)
    recipe = client.get('/graph/cases/{}/recipe'.format(case_id)).json()
    assert recipe['verified'] and recipe['signature']['category'] == 'Doors'
    ops = [(s['role'], s['action']['op']) for s in recipe['steps']]
    assert ('target', 'change_type') in ops and ('target', 'flip') in ops and ('related', 'move') in ops
    move = next(s for s in recipe['steps'] if s['action']['op'] == 'move')
    assert move['relations'] == ['HOSTED_BY'] and move['action']['dx'] == 150.
    assert move['before_position_mm'] == dict(x=2000., y=100., z=1500.)
    assert move['after_position_mm'] == dict(x=2150., y=100., z=1500.)


def legacy_case(client, move_text='X +0／Y -200／Z +0 mm'):
    root, opening = wall(), door()
    opening['properties'].pop('location')
    opening['properties']['bounding_box'] = dict(units='mm', min=dict(x=0., y=1000., z=2000.), max=dict(x=200., y=1400., z=2600.))
    graph = dict(root=root['node_id'], nodes=[root, opening], edges=[dict(source=root['node_id'], target=opening['node_id'],
        relationship_type='NEARBY', evidence='Expanded bounding box candidate', validation_status='candidate')])
    case = client.post('/graph/cases', json=dict(title='Opening clashes', description='Move the opening to resolve the clash',
        issue_type='GEOMETRY_CLASH', graph=graph)).json()
    solution = ('模型變更紀錄（2026-10-30 10:00；對比 RFI-{} 第 1 版快照）\n'
        '・RC 牆（ID 334210）：參數 · Volume 5.18 m³ → 5.14 m³\n'
        '・門 D-01（ID 334206）：位置（外框中心） 移動 200 mm → {}').format(case['id'], move_text)
    client.post('/graph/cases/{}/status'.format(case['id']), json=dict(status='REPORTED'))
    client.post('/graph/cases/{}/status'.format(case['id']), json=dict(status='RESOLVED', solution=solution))
    verified = client.post('/graph/cases/{}/status'.format(case['id']), json=dict(status='VERIFIED', reviewer='Test reviewer'))
    assert verified.status_code == 200, verified.text
    return verified.json()


def test_legacy_record_recovers_move_and_original_coordinates_without_database_rewrite(client):
    case = legacy_case(client)
    result = client.get('/graph/cases/{}/recipe'.format(case['id'])).json()
    assert result['verified'] and result['evidence']['source'] == 'legacy_generated_record'
    assert len(result['steps']) == 1
    move = result['steps'][0]
    assert move['action'] == dict(op='move', dx=0., dy=-200., dz=0.)
    assert move['before_position_mm'] == dict(x=100., y=1200., z=2300.)
    assert move['after_position_mm'] == dict(x=100., y=1000., z=2300.)
    windows_case = dict(case, solution=case['solution'].replace('\n', '\r\n'))
    assert recipe_from(windows_case, windows_case['history'])['steps'] == result['steps']
    # The move is relative to the case-graph anchor (the NEARBY wall): without live geometry it is not ready.
    assert str(move['relation']['anchor']['element_id']) == '334210' and move['relation']['axes'] == dict(
        y=dict(side=1, gap_mm=600., case_gap_before_mm=800.))
    route = '/graph/cases/{}/fix-plan'.format(case['id'])
    waiting = client.post(route, json=dict(target=dict(element_id=800), related={'element:334206': 801})).json()
    assert not waiting['ready'] and 'geometry' in waiting['steps'][0]['unresolved'][0]
    box = lambda x0, y0, z0, x1, y1, z1: dict(MinX=x0, MinY=y0, MinZ=z0, MaxX=x1, MaxY=y1, MaxZ=z1)
    same_model = {'800': box(0, 0, 0, 4000, 200, 3000), '801': box(0, 1000, 2000, 200, 1400, 2600)}
    plan = client.post(route, json=dict(target=dict(element_id=800), related={'element:334206': 801}, geometry=same_model)).json()
    assert plan['ready'] and len(plan['steps']) == 1
    assert plan['steps'][0]['call'] == dict(tool='move_element', arguments=dict(elementId=801, dx=0., dy=-200., dz=0.))
    assert plan['steps'][0]['expect_before']['position_mm']['y'] == 1200.
    assert plan['steps'][0]['expect_after']['position_mm']['y'] == 1000.
    assert plan['steps'][0]['anchors'][0]['current_id'] == 800
    # A thicker wall in another model: keep the case's 600 mm gap, not the case's -200 mm vector.
    thicker = dict(same_model, **{'800': box(0, 0, 0, 4000, 300, 3000)})
    adapted = client.post(route, json=dict(target=dict(element_id=800), related={'element:334206': 801}, geometry=thicker)).json()
    assert adapted['steps'][0]['call']['arguments']['dy'] == -100.
    # Already at the case relation: nothing to move.
    done = dict(same_model, **{'801': box(0, 800, 2000, 200, 1200, 2600)})
    settled = client.post(route, json=dict(target=dict(element_id=800), related={'element:334206': 801}, geometry=done)).json()
    assert settled['steps'][0]['already_satisfied'] and settled['steps'][0]['call']['arguments']['dy'] == 0.
    assert client.get('/graph/cases/{}/applications'.format(case['id'])).json() == []
    assert client.get('/graph/cases/{}'.format(case['id'])).json()['history'] == case['history']
    found = client.post('/graph/solutions/search', json=dict(category='Walls')).json()
    assert found['results'][0]['replayable_steps'] == 1


def test_legacy_text_is_not_blindly_converted_to_commands(client):
    case = legacy_case(client)
    for solution in ('Move opening 200 mm; delete everything else',
                     case['solution'].replace('第 1 版', '第 9 版'),
                     case['solution'].replace('ID 334206', 'ID 999999'),
                     case['solution'].replace('Y -200', 'Y -2000'),
                     case['solution'] + '\n' + case['solution']):
        changed = dict(case, solution=solution)
        assert not recipe_from(changed, changed['history'])['steps']


def test_computed_readonly_changes_are_observations_not_commands(client):
    case_id = record_case(client)
    case = client.get('/graph/cases/{}'.format(case_id)).json()
    evidence = next(h['evidence'] for h in case['history'] if h['evidence'])
    evidence['elements'][0]['changes'].append(dict(field='Volume', before='5.18', after='5.14',
        action=dict(op='set_parameter', name='Volume', read_only=True, storage='Double', after_raw=100.)))
    result = recipe_from(case, case['history'])
    assert len(result['observations']) == 1
    assert not any(s['action'].get('name') == 'Volume' for s in result['steps'])


def test_related_mapping_does_not_merge_several_nearby_elements(client):
    first, second = wall(), wall(400.)
    second.update(node_id='doc:second', source_element_id='second')
    second['properties']['element_id'] = '400'
    root = door()
    graph = dict(root=root['node_id'], nodes=[root, first, second], edges=[dict(
        source=root['node_id'], target=n['node_id'], relationship_type='NEARBY',
        evidence='Bounding box candidate', validation_status='candidate') for n in (first, second)])
    case = client.post('/graph/cases', json=dict(title='Two nearby walls', description='Shift both walls',
        issue_type='GEOMETRY_CLASH', graph=graph)).json()
    client.post('/graph/cases/{}/status'.format(case['id']), json=dict(status='REPORTED'))
    moved_second = copy.deepcopy(second)
    for side in ('min', 'max'):
        moved_second['properties']['bounding_box'][side]['x'] += 100.
    live = dict(door=root, wall=wall(100.), second=moved_second)
    report = change_report(case, 'doc', '2026-10-30 10:00', lambda r: (r['source_element_id'], 'UniqueId'), lambda uid: live[uid])
    client.post('/graph/cases/{}/status'.format(case['id']), json=dict(status='RESOLVED', solution='Shift both walls', changes=report))
    route = '/graph/cases/{}/fix-plan'.format(case['id'])
    ambiguous = client.post(route, json=dict(target=dict(element_id=1), related=dict(NEARBY=2))).json()
    assert len(ambiguous['steps']) == 2 and all(not s['ready'] for s in ambiguous['steps'])
    precise = client.post(route, json=dict(target=dict(element_id=1), related={'element:334210': 2, 'element:400': 3})).json()
    assert precise['ready']
    assert [s['call']['arguments']['elementId'] for s in precise['steps']] == [2, 3]


def test_natural_language_search_prefers_verified_matching_solutions(client):
    verified = record_case(client)
    unverified = record_case(client, verify=False)
    found = client.post('/graph/solutions/search', json=dict(query='門和牆碰撞了', category='Doors')).json()
    assert [r['case_id'] for r in found['results']] == [verified]
    top = found['results'][0]
    assert top['verified'] and top['replayable_steps'] >= 5 and any('描述相符' in r for r in top['reasons'])
    both = client.post('/graph/solutions/search', json=dict(query='door', category='Doors', verified_only=False,
                                                           family='單開門', type_name='900 x 2100')).json()
    assert set(r['case_id'] for r in both['results']) == {verified, unverified}
    assert client.post('/graph/solutions/search', json=dict(query='門', category='Windows')).json()['results'] == []
    assert 'verified_only=false' in client.post('/graph/solutions/search', json=dict(query='排水管')).json()['message']


def test_fix_plan_maps_steps_to_revit_mcp_tools(client):
    case_id = record_case(client)
    plan = client.post('/graph/cases/{}/fix-plan'.format(case_id),
                       json=dict(target=dict(element_id=900001, category='Doors', type_name='900 x 2100'))).json()
    steps = dict((s['label'].split('：')[0], s) for s in plan['steps'])
    width = steps['寬度']['call']
    # Revit MCP calls Parameter.Set(double): lengths must be sent in feet.
    assert width['tool'] == 'modify_element_parameter' and float(width['arguments']['value']) == pytest.approx(1000/FT)
    assert width['arguments'] == dict(elementId=900001, parameterName='Width', value=width['arguments']['value'])
    assert steps['寬度']['expect_before'] == dict(parameter='Width', before='900') and steps['寬度']['ready']
    assert steps['朝向']['call'] == dict(tool='flip_element', arguments=dict(elementId=900001, flipType='facing'))
    assert not steps['類型']['ready'] and 'type_ids["單開門／1000 x 2100"]' in steps['類型']['unresolved'][0]
    assert not steps['類型參數 · Fire Rating']['ready'] and any('類型 ID' in u for u in steps['類型參數 · Fire Rating']['unresolved'])
    assert not steps['參數 · Level']['ready'] and 'ElementId' in steps['參數 · Level']['unresolved'][0]
    # Old snapshots lack storage data: plain text is sent with a note, numbers are never guessed.
    assert steps['參數 · Comments']['ready'] and steps['參數 · Comments']['call']['arguments']['value'] == 'checked'
    assert any('舊快照' in n for n in steps['參數 · Comments']['notes'])
    assert not steps['參數 · Sill Height']['ready'] and steps['參數 · Sill Height']['call']['arguments']['value'] is None
    move = steps['位置（外框中心）']
    assert not move['ready'] and 'HOSTED_BY' in move['unresolved'][0] and move['call']['arguments']['dx'] == 150.
    assert all(s['requires_confirmation'] for s in plan['steps']) and not plan['ready']
    resolved = client.post('/graph/cases/{}/fix-plan'.format(case_id), json=dict(
        target=dict(element_id=900001, type_id=7001), related=dict(HOSTED_BY=900050),
        type_ids={'單開門／1000 x 2100': 7002})).json()
    steps = dict((s['label'].split('：')[0], s) for s in resolved['steps'])
    assert steps['類型']['call']['arguments'] == dict(elementId=900001, typeId=7002) and steps['類型']['ready']
    assert steps['類型參數 · Fire Rating']['call']['arguments']['elementId'] == 7001
    assert steps['位置（外框中心）']['call']['arguments']['elementId'] == 900050
    wrong = client.post('/graph/cases/{}/fix-plan'.format(case_id), json=dict(target=dict(element_id=1, category='Windows'))).json()
    assert any('不建議套用' in w for w in wrong['warnings'])


def test_applications_are_logged_and_counted_in_search(client):
    case_id = record_case(client)
    assert client.post('/graph/cases/{}/applications'.format(case_id), json=dict(
        outcome='APPLIED', element_ids=[900001], resolved=True, notes='via MCP',
        steps=[dict(tool='flip_element', ok=True)])).status_code == 201
    logged = client.get('/graph/cases/{}/applications'.format(case_id)).json()
    assert logged[0]['element_ids'] == [900001] and logged[0]['resolved'] is True
    top = client.post('/graph/solutions/search', json=dict(query='門 牆')).json()['results'][0]
    assert top['applications'] == 1 and top['resolved_applications'] == 1
    assert client.post('/graph/cases/999/applications', json=dict(outcome='APPLIED')).status_code == 404


def test_deleting_a_solved_case_removes_its_history_and_applications(client):
    case_id = record_case(client)
    client.post('/graph/cases/{}/applications'.format(case_id), json=dict(outcome='APPLIED'))
    assert client.delete('/graph/cases/{}'.format(case_id)).json() == dict(removed=True, case_id=case_id)
    assert client.get('/graph/cases/{}'.format(case_id)).status_code == 404
    assert client.get('/graph/cases/{}/applications'.format(case_id)).json() == []
    assert case_id not in [c['id'] for c in client.get('/graph/cases').json()]
    assert not client.post('/graph/solutions/search', json=dict(query='門 牆', verified_only=False)).json()['results']
    assert client.delete('/graph/cases/{}'.format(case_id)).status_code == 404


def test_mcp_protocol_and_tools_over_the_api(client):
    case_id = record_case(client)

    def api(path, payload=None):
        response = client.post('/graph'+path, json=payload) if payload is not None else client.get('/graph'+path)
        if response.status_code >= 400:
            raise ValueError(response.json()['detail'])
        return response.json()

    init = casegraph_mcp.handle(dict(jsonrpc='2.0', id=1, method='initialize', params=dict(protocolVersion='2025-06-18')), api)
    assert init['result']['serverInfo']['name'] == 'casegraph' and 'confirmation' in init['result']['instructions']
    assert casegraph_mcp.handle(dict(jsonrpc='2.0', method='notifications/initialized'), api) is None
    names = [t['name'] for t in casegraph_mcp.handle(dict(jsonrpc='2.0', id=2, method='tools/list'), api)['result']['tools']]
    assert names == ['casegraph_search_solutions', 'casegraph_get_case', 'casegraph_fix_plan', 'casegraph_record_application']
    tools = {tool['name']: tool for tool in casegraph_mcp.TOOLS}
    for name in names[:3]:
        assert tools[name]['annotations'] == dict(readOnlyHint=True, destructiveHint=False, idempotentHint=True, openWorldHint=False)
    assert tools['casegraph_record_application']['annotations']['readOnlyHint'] is False
    assert tools['casegraph_record_application']['annotations']['idempotentHint'] is False

    def call(name, **arguments):
        response = casegraph_mcp.handle(dict(jsonrpc='2.0', id=3, method='tools/call', params=dict(name=name, arguments=arguments)), api)
        return response['result']
    found = json.loads(call('casegraph_search_solutions', query='門被牆擋住', category='Doors')['content'][0]['text'])
    assert found['results'][0]['case_id'] == case_id
    case = json.loads(call('casegraph_get_case', case_id=case_id)['content'][0]['text'])
    assert case['status'] == 'VERIFIED' and case['recipe']['steps'] and case['history'][-1]['status'] == 'VERIFIED'
    recorded = next(n for n in case['snapshot']['nodes'] if n['properties']['element_id'] == '334206')
    assert recorded['properties']['location'] == dict(x=0., y=0., z=0.)
    assert 'parameters' not in recorded['properties']
    assert case['snapshot']['edges'][0]['relationship_type'] == 'HOSTED_BY'
    assert case['applications'] == []
    plan = json.loads(call('casegraph_fix_plan', case_id=case_id, target_element_id=5, related={'HOSTED_BY': 6})['content'][0]['text'])
    assert any(s['call']['tool'] == 'move_element' and s['call']['arguments']['elementId'] == 6 for s in plan['steps'])
    assert client.get('/graph/cases/{}/applications'.format(case_id)).json() == []
    logged = json.loads(call('casegraph_record_application', case_id=case_id, outcome='REJECTED', notes='user declined')['content'][0]['text'])
    assert logged['outcome'] == 'REJECTED'
    failed = call('casegraph_get_case', case_id=999)
    assert failed['isError'] and 'not found' in failed['content'][0]['text']
    unknown = casegraph_mcp.handle(dict(jsonrpc='2.0', id=4, method='resources/list'), api)
    assert unknown['error']['code'] == -32601


def test_mcp_server_runs_over_stdio():
    server = Path(__file__).resolve().parents[1] / 'casegraph_mcp.py'
    lines = [dict(jsonrpc='2.0', id=1, method='initialize', params=dict(protocolVersion='2025-06-18', capabilities={},
                                                                       clientInfo=dict(name='test', version='1'))),
             dict(jsonrpc='2.0', method='notifications/initialized'), dict(jsonrpc='2.0', id=2, method='tools/list')]
    output = subprocess.run([sys.executable, '-I', str(server)], input=''.join(json.dumps(m)+'\n' for m in lines),
                            capture_output=True, text=True, encoding='utf-8', timeout=30).stdout.splitlines()
    responses = [json.loads(line) for line in output]
    assert [r['id'] for r in responses] == [1, 2] and len(responses[1]['result']['tools']) == 4


def test_older_floor_hole_record_is_derived_and_planned_around_the_new_pipe():
    from app.solutions import derived_floor_opening
    floor = dict(category='Floors', changes=[
        dict(field='參數 · Area', action=dict(op='set_parameter', name='Area', read_only=True,
                                             before_raw=5498.760858632831, after_raw=5487.1358353827845)),
        dict(field='參數 · Perimeter', action=dict(op='set_parameter', name='Perimeter', read_only=True,
                                                  before_raw=334.64566929133855, after_raw=350.3937007874015))])
    assert derived_floor_opening(floor) == dict(op='floor_opening', length_mm=1800, width_mm=600, rotation_deg=0, derived=True)
    assert derived_floor_opening(dict(floor, category='Walls')) is None


def test_floor_opening_reuses_case_clearance_not_case_size():
    from app.solutions import adaptive_opening, case_clearance
    pipe = dict(properties=dict(bounding_box=dict(min=dict(x=950., y=-50., z=0.), max=dict(x=1050., y=50., z=3000.))))
    # Case: a 100 mm pipe in a 400 × 300 hole, hole centre 20 mm +X of the pipe.
    action = dict(op='floor_opening', length_mm=400, width_mm=300, rotation_deg=0, center_mm=dict(x=1020., y=0., z=0.))
    clearance = case_clearance(action, pipe)
    assert clearance == dict(length_start=130., length_end=170., width_start=100., width_end=100.,
                             case_mep_mm=dict(length=100., width=100.))
    # Same pipe size gives back the case opening and offset; a 200 mm pipe keeps the gaps, not the size.
    assert adaptive_opening(action, clearance, 100.) == dict(length_mm=400, width_mm=300, offset_mm=dict(x=20., y=0.))
    assert adaptive_opening(action, clearance, 200.) == dict(length_mm=500, width_mm=400, offset_mm=dict(x=20., y=0.))
    # Rotated 90°: the length runs along Y.
    turned = dict(action, rotation_deg=90, center_mm=dict(x=1000., y=20., z=0.))
    sized = adaptive_opening(turned, case_clearance(turned, pipe), 100.)
    assert sized['length_mm'] == 400 and sized['width_mm'] == 300 and sized['offset_mm'] == dict(x=0., y=20.)
    # A sloped run's box is wider than the hole: no reliable clearance.
    assert case_clearance(action, dict(properties=dict(bounding_box=dict(
        min=dict(x=0., y=-50., z=0.), max=dict(x=3000., y=50., z=3000.))))) is None


def test_floor_opening_fix_plan_asks_for_the_pipe_size_then_adapts():
    from app.solutions import FixPlanRequest, fix_plan
    import app.solutions as solutions
    plan = dict(case_id=2, title='開孔', verified=True, solution='', signature=dict(category='Floors', type_name=None),
                evidence=dict(document='doc'), manual=[], observations=[], steps=[dict(
                    role='target', relations=[], label='樓板開孔', element_id=1, name='FL', category='Floors',
                    action=dict(op='floor_opening', length_mm=1800, width_mm=600, rotation_deg=0, derived=True),
                    mep=[dict(element_id=7, name='管', category='Pipes', relations=['INTERSECTS'])], offset_mm=dict(x=0, y=0),
                    clearance_mm=dict(length_start=850., length_end=850., width_start=250., width_end=250.,
                                      case_mep_mm=dict(length=100., width=100.)))])
    original = solutions.recipe
    solutions.recipe = lambda case_id: copy.deepcopy(plan)
    try:
        first = fix_plan(2, FixPlanRequest(target=dict(element_id=883400), related={'element:7': 883848}))
        step = first['steps'][0]
        assert not step['ready'] and 'mep_outer_mm' in step['unresolved'][0]
        assert step['call']['arguments']['mepId'] == 883848
        sized = fix_plan(2, FixPlanRequest(target=dict(element_id=883400), related={'element:7': 883848}, mep_outer_mm=60.))
        args = sized['steps'][0]['call']['arguments']
        assert (args['lengthMm'], args['widthMm']) == (1760, 560) and sized['steps'][0]['unresolved'] == []
        assert any('blocked' in n for n in sized['steps'][0]['notes'])
        assert sized['steps'][0]['expect_before']['adaptive']['case_opening_mm'] == dict(length=1800, width=600)
    finally:
        solutions.recipe = original
