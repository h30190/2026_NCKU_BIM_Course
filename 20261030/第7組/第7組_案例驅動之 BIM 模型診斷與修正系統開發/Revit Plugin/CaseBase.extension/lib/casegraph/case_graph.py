# -*- coding: utf-8 -*-
"""Live case-library projection. Never changes saved model evidence or case status."""
from copy import deepcopy
from casegraph.case_scope import related_matches, case_matches

CASE_RELATIONS = ('CASE_REFERENCE', 'HAS_ISSUE', 'RESOLVED_BY')


def with_related_cases(graph, cases, current=None, issue_type=None):
    preview = deepcopy(graph)
    nodes = dict((n['node_id'], n) for n in preview['nodes'])
    rows = list(cases)
    saved = current['draft']['graph'] if current else None
    root = nodes[graph['root']]
    if (current and saved['root'] == graph['root'] and current['status'] != 'ARCHIVED'
            and case_matches(root, issue_type, current['draft'])
            and not any(r['case_id'] == current['id'] for r in rows)):
        element = next(n for n in saved['nodes'] if n['node_id'] == saved['root'])
        rows.append(dict(case_id=current['id'], title=current['draft']['title'],
                         description=current['draft']['description'], status=current['status'],
                         element=element, issue_type=current['draft']['issue_type'],
                         solution=current.get('solution', ''), score=1., evidence=[]))
    seen = set()

    def put(record):
        old = nodes.get(record['node_id'])
        if old is None:
            preview['nodes'].append(record)
        else:
            old.update(record)
        nodes[record['node_id']] = old or record

    def edge(source, target, relation, evidence, verified=True, **properties):
        if any(e['source'] == source and e['target'] == target and e['relationship_type'] == relation
               for e in preview['edges']):
            return
        preview['edges'].append(dict(source=source, target=target, relationship_type=relation,
                                    evidence=evidence, validation_status='verified' if verified else 'candidate',
                                    properties=properties))

    for row in rows:
        case_id = row['case_id']
        if case_id in seen or row['status'] == 'ARCHIVED' or not related_matches(root, issue_type, row):
            continue
        seen.add(case_id)
        element = row['element']
        owner = element['node_id']
        if owner not in nodes:
            recorded = deepcopy(element)
            recorded['_recorded'] = True
            put(recorded)
        # Preserve the open case's saved issue identity without editing that snapshot.
        issue_id = 'case:{}'.format(case_id)
        if current and current['id'] == case_id:
            linked = set(e['target'] for e in saved['edges'] if e['source'] == owner
                         and e['relationship_type'] == 'HAS_ISSUE')
            issue_id = next((n['node_id'] for n in saved['nodes']
                             if n['node_type'] == 'ISSUE' and n['node_id'] in linked), issue_id)
        text = row.get('solution') or row.get('resolution_note') or ''
        verified = row['status'] == 'VERIFIED'
        props = dict(case_id=case_id, status=row['status'], description=row.get('description', ''),
                     issue_type=row.get('issue_type', ''), related_element=owner,
                     element_id=element['properties'].get('element_id'), element_name=element['name'],
                     document_title=element['properties'].get('document_title'),
                     solution=text, solution_state=u'已驗證' if verified else u'未驗證',
                     score=row.get('score', 0), match_evidence=row.get('evidence', []),
                     case_scope=u'此元素的案例' if owner == graph['root'] else u'其他元素的參考案例')
        put(dict(node_id=issue_id, node_type='ISSUE', name=u'RFI-{} · {}'.format(case_id, row['title']),
                 source_document=element['source_document'], source_element_id=None, properties=props))
        if owner != graph['root']:
            edge(graph['root'], owner, 'CASE_REFERENCE',
                 u'同元件類型、符合目前問題類型篩選的案例；只供參考，不表示基準元素有相同問題。', False,
                 score=row.get('score', 0), match_evidence=row.get('evidence', []))
        edge(owner, issue_id, 'HAS_ISSUE', u'案例庫 RFI-{} 記錄的基準元素。'.format(case_id), case_id=case_id)
        if text.strip():
            solution_id = 'case:{}:solution'.format(case_id)
            solution_props = deepcopy(props)
            solution_props['text'] = text
            put(dict(node_id=solution_id, node_type='SOLUTION',
                     name=u'RFI-{} · {}'.format(case_id, u'已驗證解法' if verified else u'解法（未驗證）'),
                     source_document=element['source_document'], source_element_id=None, properties=solution_props))
            edge(issue_id, solution_id, 'RESOLVED_BY',
                 u'案例庫記錄的解法；驗證狀態依案例目前狀態。', verified, case_id=case_id)
    return preview


def navigation_record(record, graph):
    """A case/solution navigates only to its actual recorded model element."""
    if not record:
        return None
    if record['node_type'] in ('ISSUE', 'SOLUTION'):
        nodes = (graph or {}).get('nodes', [])
        edges = (graph or {}).get('edges', [])
        key = record['properties'].get('related_element')
        if not key:
            # Historical snapshots lack UI metadata; follow their recorded ownership.
            issue = record['node_id']
            if record['node_type'] == 'SOLUTION':
                issue = next((e['source'] for e in edges if e['target'] == issue
                              and e['relationship_type'] == 'RESOLVED_BY'), None)
            key = next((e['source'] for e in edges if e['target'] == issue
                        and e['relationship_type'] == 'HAS_ISSUE'), None)
        record = next((n for n in nodes if n['node_id'] == key and n['node_type'] == 'ELEMENT'), None)
    return record if record and record.get('source_element_id') and record['node_type'] not in (
        'ISSUE', 'SOLUTION', 'PROJECT', 'GROUP') else None
