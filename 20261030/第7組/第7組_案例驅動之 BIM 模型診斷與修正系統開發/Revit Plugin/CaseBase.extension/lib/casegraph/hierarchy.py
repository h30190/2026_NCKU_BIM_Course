# -*- coding: utf-8 -*-
"""Build a collapsible presentation tree without changing graph evidence."""
import hashlib
from collections import deque
from casegraph.presentation import RELATIONS, text_type

COMPARISONS = ('SAME_TYPE', 'SIMILAR_TO')


def group_key(*parts):
    raw = u'\x1f'.join(text_type(p or '') for p in parts)
    return 'group:'+hashlib.sha1(raw.encode('utf-8')).hexdigest()


def build_hierarchy(graph, relation='ALL'):
    """One physical spanning tree plus document/family/type/instance branches.

    Cross-links/cycles remain available in the relationship inspector. An element
    can occur once in each branch; occurrence IDs are separate from Revit IDs.
    """
    nodes = dict((n['node_id'], n) for n in graph['nodes'])
    root = graph['root']
    entries, parents = {}, {}

    def add(key, record, parent=None, match=None):
        if key not in entries:
            entries[key] = dict(key=key, record=record, children=[], match=match)
            if parent:
                entries[parent]['children'].append(key)
                parents[key] = parent
        return key

    def group(parent, level, name, discriminator='', **properties):
        key = group_key(root, parent, level, discriminator or name)
        props = dict(hierarchy_level=level, **properties)
        record = dict(node_id=key, node_type='GROUP', name=name, properties=props,
                      source_document='', source_element_id=None)
        return add(key, record, parent)

    add('root', nodes[root])
    edges = [e for e in graph['edges'] if relation == 'ALL' or e['relationship_type'] == relation]
    physical = [e for e in edges if e['relationship_type'] not in COMPARISONS+('RULE_VIOLATION', 'MODEL_CHECK')]
    adjacency = {}
    for edge in physical:
        adjacency.setdefault(edge['source'], []).append((edge['target'], edge, True))
        adjacency.setdefault(edge['target'], []).append((edge['source'], edge, False))
    visited, queue = set([root]), deque([(root, 'root')])
    physical_count = 0
    while queue:
        identity, parent = queue.popleft()
        for neighbor, edge, outgoing in adjacency.get(identity, []):
            if neighbor in visited or neighbor not in nodes:
                continue
            visited.add(neighbor)
            if identity == root:
                case_branch = edge['relationship_type'] in ('CASE_REFERENCE', 'HAS_ISSUE', 'RESOLVED_BY')
                branch = group('root', u'分支', u'案例與解法' if case_branch else u'模型關係', 'cases' if case_branch else 'model')
            else:
                branch = parent
            code = edge['relationship_type']
            name = RELATIONS.get(code, code) + (u'' if outgoing else u'（指向此元素）')
            bucket = group(branch, u'模型關係', name, code+('out' if outgoing else 'in'))
            key = add('context:'+neighbor, nodes[neighbor], bucket)
            physical_count += 1
            queue.append((neighbor, key))
    # A filtered edge can be disconnected from the root; keep it discoverable.
    for edge in physical:
        for identity in (edge['source'], edge['target']):
            if identity not in visited and identity in nodes:
                branch = group('root', u'分支', u'其他篩選結果', 'filtered')
                add('context:'+identity, nodes[identity], branch)
                visited.add(identity)
    seen = set()
    for edge in edges:
        if edge['relationship_type'] != 'MODEL_CHECK':
            continue
        detail = edge['properties']
        branch = group('root', u'分支', u'常見錯誤檢查', 'checks')
        bucket = group(branch, u'檢查', detail['label'], detail['check'])
        finding = group(bucket, u'結果', detail['status_label']+u' · #'+detail['result_id'], detail['result_id'])
        add('check-element:'+detail['result_id']+':'+edge['target'], nodes[edge['target']], finding)
    for edge in edges:
        if edge['relationship_type'] != 'RULE_VIOLATION':
            continue
        rule = edge['properties']
        branch = group('root', u'分支', u'相交規則違規', 'violations')
        bucket = group(branch, u'規則', rule['rule_name'], rule['rule_id'])
        source_key = 'violation-source:'+rule['rule_id']+':'+edge['source']
        source = add(source_key, nodes[edge['source']], bucket)
        add(source_key+':target:'+edge['target'], nodes[edge['target']], source)
    for edge in edges:
        if edge['relationship_type'] not in COMPARISONS or edge['target'] in seen:
            continue
        record = nodes[edge['target']]
        p = record['properties']
        seen.add(edge['target'])
        category = group('root', u'類別', u'同類元素 · '+(p.get('category') or u'未提供類別'), 'similar')
        family = group(category, u'族群', p.get('family') or u'未提供族群',
            group_key(record['source_document'], p.get('category_id') or p.get('category'), p.get('family')),
            document_title=p.get('document_title') or u'來源文件名稱未提供')
        type_id = p.get('type_id')
        # Unknown types stay separate instead of falsely claiming equivalence.
        type_identity = type_id if type_id not in (None, '', '-1') else record['node_id']
        typ = group(family, u'類型', p.get('type_name') or u'未提供類型', type_identity,
            same_type=edge['relationship_type']=='SAME_TYPE', document_title=p.get('document_title'))
        add('match:'+record['node_id'], record, typ, edge)

    def count(key):
        entry = entries[key]
        total = sum(count(child) for child in entry['children'])
        if entry['record']['node_type'] == 'GROUP':
            entry['record']['properties']['element_count'] = total
        return total + (0 if entry['record']['node_type'] == 'GROUP' else 1)
    count('root')
    return dict(root='root', entries=entries, parents=parents, comparison_count=len(seen), physical_count=physical_count)


def visible_rows(tree, collapsed):
    """Preorder rows for a compact indented tree with orthogonal connectors."""
    rows = []

    def walk(key, depth):
        rows.append((key, depth))
        if key not in collapsed:
            for child in tree['entries'][key]['children']:
                walk(child, depth+1)
    walk(tree['root'], 0)
    return rows


def path_to(tree, key):
    path = []
    while key in tree['parents']:
        key = tree['parents'][key]
        path.append(key)
    return path
