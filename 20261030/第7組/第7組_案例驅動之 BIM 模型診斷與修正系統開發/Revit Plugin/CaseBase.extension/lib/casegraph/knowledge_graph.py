# -*- coding: utf-8 -*-
"""Read-only knowledge graph projection and deterministic layered layout.

Actual nodes occur once and all non-comparison edges retain direction, cycles,
evidence and validation status. Comparison edges become an explicitly visual
classification hierarchy; their original evidence stays in the source graph.
No Revit or WPF dependencies, compatible with IronPython 2.7.
"""
from collections import deque
from casegraph.hierarchy import group_key, COMPARISONS
from casegraph.presentation import RELATIONS, text_type
from casegraph.case_graph import CASE_RELATIONS

CARD_WIDTH, CARD_HEIGHT = 190., 72.
X_GAP, Y_GAP = 300., 112.


def build_network(graph, relation='ALL'):
    if not graph:
        return dict(root=None, nodes={}, edges=[], children={}, matches={}, positions={}, width=600., height=400.)
    source_nodes = dict((n['node_id'], n) for n in graph['nodes'])
    root = graph['root']
    nodes, edges, children, matches = {root: source_nodes[root]}, [], {}, {}
    selected = [e for e in graph['edges'] if (relation == 'ALL' or e['relationship_type'] == relation
                or relation == 'CASES' and e['relationship_type'] in CASE_RELATIONS)
                and e['source'] in source_nodes and e['target'] in source_nodes]

    def group(parent, level, name, identity, **props):
        key = group_key('knowledge', parent, level, identity)
        if key not in nodes:
            props.update(hierarchy_level=level, element_count=0)
            nodes[key] = dict(node_id=key, node_type='GROUP', name=name, properties=props,
                              source_document='', source_element_id=None)
            link(parent, key, u'比較分類' if parent == root else u'包含'+level)
        return key

    def link(a, b, label):
        children.setdefault(a, []).append(b)
        edges.append(dict(source=a, target=b, label=label, grouping=True, record=None))

    for edge in selected:
        a, b = edge['source'], edge['target']
        nodes[a], nodes[b] = source_nodes[a], source_nodes[b]
        if edge['relationship_type'] in COMPARISONS and a == root and b != root:
            if b in matches:
                continue
            record, p = source_nodes[b], source_nodes[b]['properties']
            category_id = p.get('category_id') or p.get('category') or b
            category = group(root, u'類別', p.get('category') or u'未提供類別', category_id)
            family = group(category, u'族群', p.get('family') or u'未提供族群',
                           group_key(record['source_document'], p.get('family') or b),
                           document_title=p.get('document_title'))
            type_id = p.get('type_id')
            identity = type_id if type_id not in (None, '', '-1', -1) else b
            typ = group(family, u'類型', p.get('type_name') or u'未提供類型', identity,
                        document_title=p.get('document_title'), same_type=edge['relationship_type'] == 'SAME_TYPE')
            link(typ, b, u'類型成員' if type_id not in (None, '', '-1', -1) else u'比較候選')
            matches[b] = edge
        else:
            label = RELATIONS.get(edge['relationship_type'], edge['relationship_type'])
            if edge['relationship_type'] == 'MODEL_CHECK':
                p = edge.get('properties') or {}
                label = p.get('label') or label
                if p.get('status_label'):
                    label += u' · '+p['status_label']
            edges.append(dict(source=a, target=b, label=label, grouping=False, record=edge))
    if relation == 'ALL':
        nodes.update(source_nodes)  # Unconnected observations stay unconnected.

    def count(key):
        descendants = children.get(key, [])
        total = sum(count(child) for child in descendants) if descendants else 1
        if nodes[key]['node_type'] == 'GROUP':
            nodes[key]['properties']['element_count'] = total
        return total
    count(root)
    network = dict(root=root, nodes=nodes, edges=edges, children=children, matches=matches)
    network.update(case_layout(network) if relation == 'CASES' else layout(network))
    return network


def case_layout(network):
    """One node per element/case/solution; aligned rows make ownership explicit."""
    nodes, root = network['nodes'], network['root']
    owners, solutions = {}, {}
    for edge in network['edges']:
        kind = edge['record']['relationship_type']
        if kind == 'HAS_ISSUE':
            owners[edge['target']] = edge['source']
        elif kind == 'RESOLVED_BY':
            solutions.setdefault(edge['source'], []).append(edge['target'])
    issues = sorted((k for k in nodes if nodes[k]['node_type'] == 'ISSUE'),
                    key=lambda k:(owners.get(k, '') != root, owners.get(k, ''), nodes[k]['properties'].get('case_id', 0), k))
    owner_column = int(any(owner != root for owner in owners.values()))
    positions, owner_rows, cursor = {}, {}, 100.
    for key in issues:
        positions[key] = (140.+(1+owner_column)*X_GAP, cursor)
        owner_rows.setdefault(owners.get(key, root), []).append(cursor)
        for index, solution in enumerate(sorted(solutions.get(key, []))):
            positions[solution] = (140.+(2+owner_column)*X_GAP, cursor+index*Y_GAP)
        cursor += max(1, len(solutions.get(key, [])))*Y_GAP
    for owner, rows in owner_rows.items():
        if owner != root and owner not in positions:
            positions[owner] = (140.+X_GAP, (min(rows)+max(rows))/2.)
    positions[root] = (140., (100.+max(100., cursor-Y_GAP))/2.)
    for key in sorted(nodes):
        if key not in positions:
            positions[key] = (140.+X_GAP, cursor)
            cursor += Y_GAP
    return dict(positions=positions, width=max(x for x, y in positions.values())+160.,
                height=max(y for x, y in positions.values())+120.)


def layout(network):
    """Comparison hierarchy on the right, model network on the left.

    Columns separate hierarchy levels, peers occupy different rows, and cross
    links are drawn separately from this layout's BFS, including back edges.
    """
    nodes, root = network['nodes'], network['root']
    positions, cursor = {}, [0.]
    children = network['children']

    def sort_key(key):
        n = nodes[key]
        return (text_type(n.get('source_document') or ''), text_type(n['name']), key)

    def branch(key, depth):
        members = sorted(children.get(key, []), key=sort_key)
        if members:
            ys = [branch(child, depth+1) for child in members]
            y = (min(ys)+max(ys))/2.
        else:
            y = cursor[0]
            cursor[0] += Y_GAP
        positions[key] = (depth*X_GAP, y)
        return y
    branch(root, 0)
    root_y = positions[root][1]
    adjacency = {}
    for edge in network['edges']:
        if edge['grouping']:
            continue
        adjacency.setdefault(edge['source'], []).append(edge['target'])
        adjacency.setdefault(edge['target'], []).append(edge['source'])
    distances, queue = {root: 0}, deque([root])
    while queue:
        key = queue.popleft()
        for other in sorted(adjacency.get(key, [])):
            if other not in distances:
                distances[other] = distances[key]+1
                queue.append(other)
    columns = {}
    for key in sorted(nodes, key=sort_key):
        if key not in positions:
            columns.setdefault(min(3, max(1, distances.get(key, 1))), []).append(key)
    for depth, members in columns.items():
        for i, key in enumerate(members):
            positions[key] = (-depth*X_GAP, root_y+(i-(len(members)-1)/2.)*Y_GAP)
    min_x = min(x for x, y in positions.values())-140.
    min_y = min(y for x, y in positions.values())-100.
    positions = dict((key, (x-min_x, y-min_y)) for key, (x, y) in positions.items())
    return dict(positions=positions, width=max(x for x, y in positions.values())+160.,
                height=max(y for x, y in positions.values())+120.)
