import copy
from test_presentation import element, match
from casegraph.presentation import with_similar_elements
from casegraph.hierarchy import build_hierarchy, visible_rows, path_to


def tree_with_candidates():
    root = element('1')
    graph = dict(root=root['node_id'], nodes=[root], edges=[])
    rows = [match(element('2')), match(element('3')), match(element('4', type_id='200', type_name='RC 300'))]
    return with_similar_elements(graph, root, rows)


def test_results_have_category_family_type_and_individual_levels():
    graph = tree_with_candidates()
    original = copy.deepcopy(graph)
    tree = build_hierarchy(graph)
    levels = [tree['entries'][k]['record']['properties'].get('hierarchy_level') for k in path_to(tree,'match:model:2')]
    assert levels == ['類型','族群','類別',None]
    assert tree['parents']['match:model:2'] == tree['parents']['match:model:3']
    assert tree['parents']['match:model:2'] != tree['parents']['match:model:4']
    assert graph == original
    assert not any(k.startswith('match:') for k in tree['entries']['root']['children'])


def test_collapse_only_hides_its_own_descendants_and_path_can_be_revealed():
    tree = build_hierarchy(tree_with_candidates())
    typ = tree['parents']['match:model:2']
    collapsed = {typ}
    visible = dict(visible_rows(tree,collapsed))
    assert typ in visible and 'match:model:2' not in visible and 'match:model:3' not in visible
    assert 'match:model:4' in visible
    collapsed.difference_update(path_to(tree,'match:model:2'))
    assert 'match:model:2' in dict(visible_rows(tree,collapsed))


def test_same_named_types_in_different_documents_stay_separate():
    a,b,c = element('1'),element('2'),element('3')
    c.update(source_document='other',node_id='other:3')
    graph = with_similar_elements(dict(root=a['node_id'],nodes=[a],edges=[]), a, [match(b),match(c)])
    tree = build_hierarchy(graph)
    assert tree['parents']['match:model:2'] != tree['parents']['match:other:3']


def test_physical_cycles_remain_in_evidence_without_recursive_tree_loops():
    a,b,c = element('1'),element('2'),element('3')
    graph = dict(root=a['node_id'],nodes=[a,b,c],edges=[dict(source=x['node_id'],target=y['node_id'],relationship_type='CONNECTED_TO') for x,y in [(a,b),(b,c),(c,a)]])
    original = copy.deepcopy(graph)
    tree = build_hierarchy(graph)
    assert len([k for k in tree['entries'] if k.startswith('context:')]) == 2
    assert len(visible_rows(tree,set())) == len(tree['entries'])
    assert graph == original
