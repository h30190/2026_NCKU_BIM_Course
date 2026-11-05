import copy
from test_presentation import element
from casegraph.case_graph import with_related_cases, navigation_record
from casegraph.knowledge_graph import build_network, CARD_WIDTH, CARD_HEIGHT
from casegraph.presentation import detail_rows, relationship_rows


def case(case_id, owner, status='RESOLVED', solution='Adjust clearance'):
    return dict(case_id=case_id, element=owner, title='Door clearance', description='Recorded condition',
                status=status, issue_type='SPATIAL_CLEARANCE', resolution_note=solution if status != 'VERIFIED' else '',
                solution=solution if status == 'VERIFIED' else '', score=.8, evidence=['Same category'])


def test_case_ownership_solution_status_and_snapshot_immutability():
    root, other = element('1'), element('2')
    graph = dict(root=root['node_id'], nodes=[root], edges=[])
    rows = [case(1, root), case(2, other, 'VERIFIED'), case(3, other, solution=''), case(4, root, 'ARCHIVED')]
    original = copy.deepcopy((graph, rows))
    preview = with_related_cases(graph, rows+rows, issue_type='SPATIAL_CLEARANCE')
    assert (graph, rows) == original
    assert len(preview['nodes']) == 7  # Two real elements, three cases, two solutions.
    assert len({n['node_id'] for n in preview['nodes']}) == 7
    assert not any(e['source'] == root['node_id'] and e['target'] == 'case:2' for e in preview['edges'])
    assert any(e['source'] == other['node_id'] and e['target'] == 'case:2' and e['relationship_type'] == 'HAS_ISSUE' for e in preview['edges'])
    assert len([e for e in preview['edges'] if e['relationship_type'] == 'CASE_REFERENCE']) == 1
    assert {e['source']: e['validation_status'] for e in preview['edges'] if e['relationship_type'] == 'RESOLVED_BY'} == {
        'case:1': 'candidate', 'case:2': 'verified'}
    nodes = {n['node_id']: n for n in preview['nodes']}
    assert navigation_record(nodes['case:2:solution'], preview)['node_id'] == other['node_id']
    assert '不表示基準元素有相同問題' in relationship_rows(root, preview)[1].value
    assert dict((r.label, r.value) for r in detail_rows(nodes['case:1']))['解法驗證'] == '未驗證'


def test_latest_case_result_overrides_open_case_and_keeps_document_identity():
    root = element('1')
    graph = dict(root=root['node_id'], nodes=[root], edges=[])
    current = dict(id=1, status='RESOLVED', solution='Old solution', draft=dict(
        title='Title', description='Description', issue_type='SPATIAL_CLEARANCE', graph=copy.deepcopy(graph)))
    current['draft']['graph']['nodes'].append(dict(node_id='saved:issue', node_type='ISSUE', properties={}))
    current['draft']['graph']['edges'].append(dict(source=root['node_id'], target='saved:issue', relationship_type='HAS_ISSUE'))
    original = copy.deepcopy(current)
    foreign = element('1')
    foreign.update(node_id='other:1', source_document='other')
    preview = with_related_cases(graph, [case(1, root, 'VERIFIED', 'Reviewed solution'), case(2, foreign)], current, 'SPATIAL_CLEARANCE')
    nodes = {n['node_id']: n for n in preview['nodes']}
    assert nodes['saved:issue']['properties']['status'] == 'VERIFIED'
    assert nodes['saved:issue']['properties']['solution'] == 'Reviewed solution'
    assert navigation_record(nodes['case:2'], preview)['source_document'] == 'other'
    assert navigation_record(nodes['case:2'], graph) is None  # No stale-target fallback.
    assert current == original
    snapshot = current['draft']['graph']
    assert navigation_record(snapshot['nodes'][-1], snapshot)['node_id'] == root['node_id']
    assert current == original


def test_case_mode_retains_case_chains_and_layout_separates_cards():
    root, other = element('1'), element('2')
    graph = dict(root=root['node_id'], nodes=[root, other], edges=[dict(
        source=root['node_id'], target=other['node_id'], relationship_type='HOSTED_BY')])
    preview = with_related_cases(graph, [case(1, root)] + [case(i, other) for i in range(2, 12)], issue_type='SPATIAL_CLEARANCE')
    net = build_network(preview, 'CASES')
    assert len(net['nodes']) == 24
    assert all(e['record']['relationship_type'] != 'HOSTED_BY' for e in net['edges'])
    assert net['positions'] == build_network(preview, 'CASES')['positions']
    points = list(net['positions'].values())
    for i, (x, y) in enumerate(points):
        for ox, oy in points[i+1:]:
            assert abs(x-ox) >= CARD_WIDTH or abs(y-oy) >= CARD_HEIGHT
