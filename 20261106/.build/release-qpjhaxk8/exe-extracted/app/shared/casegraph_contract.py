# -*- coding: utf-8 -*-
"""Canonical CASE classification/matching contract, Python 2.7 and 3 compatible."""
try:
    text_type = unicode
except NameError:
    text_type = str


def clean(value):
    return text_type(value).strip() if value is not None else ''


def normalize(value):
    return clean(value).lower()


def type_identity(node):
    p = node.get('properties') or {}
    type_id = clean(p.get('type_id'))
    if type_id in ('', '-1', '0', 'None'):
        type_id = ''
    result = dict(document=clean(node.get('source_document')), type_id=type_id,
                  category_id=clean(p.get('category_id')), category=clean(p.get('category')),
                  family=clean(p.get('family')), type_name=clean(p.get('type_name')))
    result['complete'] = bool((result['category_id'] or result['category']) and (
        result['document'] and type_id or result['family'] and result['type_name']))
    return result


def type_match(left, right):
    """Return the matching basis; IDs are scoped to a document, never cross-model."""
    a, b = type_identity(left), type_identity(right)
    if not a['complete'] or not b['complete']:
        return None
    if a['category_id'] and b['category_id']:
        category_equal = a['category_id'] == b['category_id']
    else:
        category_equal = bool(a['category'] and b['category'] and normalize(a['category']) == normalize(b['category']))
    if not category_equal:
        return None
    if a['document'] and a['document'] == b['document'] and a['type_id'] and b['type_id']:
        return 'document_type_id' if a['type_id'] == b['type_id'] else None
    if all(a[k] and b[k] and normalize(a[k]) == normalize(b[k]) for k in ('family', 'type_name')):
        return 'family_and_type_name'
    return None


def draft_root(draft):
    graph = draft.get('graph') or {}
    return next((n for n in graph.get('nodes', []) if n['node_id'] == graph.get('root')), None)


def classify(draft):
    return dict(schema_version=1, element_type=type_identity(draft_root(draft) or {}),
                issue_type=clean(draft.get('issue_type')))


def issue_matches(requested, actual):
    # ALL is a query option; it is never a classification stored on a case.
    return bool(actual and actual != 'ALL' and (requested == 'ALL' or requested == actual))


def case_matches(target, issue_type, draft):
    root = draft_root(draft)
    return bool(target and root and issue_matches(issue_type, draft.get('issue_type'))
                and type_match(target, root))


def related_matches(target, issue_type, row):
    return bool(target and issue_matches(issue_type, row.get('issue_type'))
                and row.get('element') and type_match(target, row['element']))
