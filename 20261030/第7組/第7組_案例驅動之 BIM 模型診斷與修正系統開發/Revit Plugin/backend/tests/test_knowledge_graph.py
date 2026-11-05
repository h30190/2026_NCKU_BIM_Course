import copy
import math
from test_presentation import element, match
from casegraph.presentation import with_similar_elements
from casegraph.knowledge_graph import build_network, CARD_WIDTH, CARD_HEIGHT


def graph_with_cycle():
    a, b, c = (element(str(i)) for i in range(1, 4))
    return dict(root=a['node_id'], nodes=[a, b, c], edges=[
        dict(source=x['node_id'], target=y['node_id'], relationship_type=kind,
             validation_status='candidate', evidence='Measured', properties={})
        for x, y, kind in [(a, b, 'CONNECTED_TO'), (b, c, 'HOSTED_BY'),
                           (c, a, 'CONNECTED_TO'), (a, a, 'MODEL_CHECK')]])


def test_network_retains_all_directed_cross_links_cycles_and_evidence_without_duplicate_nodes():
    graph = graph_with_cycle()
    original = copy.deepcopy(graph)
    net = build_network(graph)
    assert len(net['nodes']) == 3
    assert [e['record'] for e in net['edges']] == graph['edges']
    assert graph == original
    assert all(not e['grouping'] for e in net['edges'])


def test_comparison_hierarchy_keeps_types_and_documents_separate_and_does_not_create_cliques():
    graph = graph_with_cycle()
    other_doc = element('5')
    other_doc.update(node_id='other:5', source_document='other')
    candidates = [element('2'), element('3'), element('4', type_id='200'), other_doc,
                  element('6', type_id=None), element('7', type_id=None)]
    graph = with_similar_elements(graph, graph['nodes'][0], [match(n) for n in candidates])
    original = copy.deepcopy(graph)
    net = build_network(graph)
    assert graph == original
    assert len(net['matches']) == 6
    assert len([e for e in net['edges'] if not e['grouping']]) == 4
    parents = {e['target']: e['source'] for e in net['edges'] if e['grouping']}
    assert parents['model:2'] == parents['model:3']
    assert len({parents[k] for k in ['model:2', 'model:4', 'other:5', 'model:6', 'model:7']}) == 5
    ancestors = []
    key = 'model:2'
    while key in parents:
        key = parents[key]
        ancestors.append(net['nodes'][key]['properties'].get('hierarchy_level'))
    assert ancestors == ['類型', '族群', '類別', None]
    assert net['nodes'][parents['model:2']]['properties']['element_count'] == 2


def test_relation_filter_removes_other_edges_and_keeps_root_without_mutating_snapshot():
    graph = graph_with_cycle()
    net = build_network(graph, 'HOSTED_BY')
    assert len(net['edges']) == 1
    assert net['edges'][0]['record']['relationship_type'] == 'HOSTED_BY'
    assert graph['root'] in net['nodes']
    empty = build_network(graph, 'HAS_MATERIAL')
    assert list(empty['nodes']) == [graph['root']] and not empty['edges']
    assert build_network(None)['positions'] == {}


def test_layout_is_deterministic_bounded_and_cards_do_not_overlap():
    root = element('0')
    graph = dict(root=root['node_id'], nodes=[root], edges=[])
    for i in range(1, 161):
        n = element(str(i), family='F'+str(i % 4), type_id=str(i % 8))
        graph['nodes'].append(n)
        graph['edges'].append(dict(source=root['node_id'], target=n['node_id'], relationship_type='CONNECTED_TO'))
    graph = with_similar_elements(graph, root, [match(n) for n in graph['nodes'][1:13]])
    net = build_network(graph)
    again = build_network(graph)
    assert net['positions'] == again['positions']
    assert all(math.isfinite(v) for p in net['positions'].values() for v in p)
    points = list(net['positions'].values())
    for i, (x, y) in enumerate(points):
        assert 0 < x < net['width'] and 0 < y < net['height']
        for ox, oy in points[i + 1:]:
            assert abs(x-ox) >= CARD_WIDTH or abs(y-oy) >= CARD_HEIGHT


def test_unconnected_nodes_remain_unconnected_and_missing_endpoints_are_not_fabricated():
    graph = graph_with_cycle()
    graph['nodes'].append(element('9'))
    graph['edges'].append(dict(source='model:1', target='missing', relationship_type='HOSTED_BY'))
    net = build_network(graph)
    assert 'model:9' in net['nodes'] and 'missing' not in net['nodes']
    assert all(e['source'] != 'model:9' and e['target'] != 'model:9' for e in net['edges'])
