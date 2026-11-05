# -*- coding: utf-8 -*-
"""Human-readable element details and comparison, shared by the WPF views."""
from copy import deepcopy
import math

try:
    text_type = unicode
except NameError:
    text_type = str

RELATIONS = {
    'ALL': u'全部關係', 'CASES': u'案例與解法', 'CASE_REFERENCE': u'參考案例元素',
    'HOSTED_BY': u'依附主體', 'CONTAINED_IN': u'位於空間',
    'ADJACENT_TO': u'相鄰', 'CONNECTED_TO': u'連接', 'INTERSECTS': u'幾何相交',
    'BELONGS_TO_SYSTEM': u'所屬系統', 'LOCATED_ON_LEVEL': u'所在樓層',
    'HAS_MATERIAL': u'使用材質', 'HAS_ISSUE': u'相關問題', 'RESOLVED_BY': u'解決方案',
    'RELATED_TO': u'指定相關元素', 'NEARBY': u'鄰近候選',
    'SAME_TYPE': u'相同類型', 'SIMILAR_TO': u'同類別比較候選', 'RULE_VIOLATION': u'違反相交規則', 'MODEL_CHECK': u'模型檢查結果'}
KINDS = dict(GROUP=u'瀏覽分組', ELEMENT=u'模型元素', LEVEL=u'樓層', SPACE=u'空間', MATERIAL=u'材質',
             SYSTEM=u'系統', ISSUE=u'問題案例', SOLUTION=u'解決方案', PROJECT=u'專案')
DIMENSIONS = dict(Width=u'寬度', Height=u'高度', Length=u'長度', Thickness=u'厚度')
FACTORS = dict(mm=1., cm=10., m=1000., ft=304.8, **{'in':25.4})
MISSING = u'未提供'
STATUS_NAMES = dict(DRAFT=u'草稿', REPORTED=u'已提交', ANALYZED=u'已分析', UNDER_REVIEW=u'審查中',
                    RESOLVED=u'已提出解法', VERIFIED=u'已驗證', ARCHIVED=u'已封存')
ISSUE_NAMES = dict(GEOMETRY_CLASH=u'幾何碰撞', SPATIAL_CLEARANCE=u'空間淨距', PARAMETER_ERROR=u'參數錯誤',
                   IFC_MAPPING_ERROR=u'IFC 分類錯誤', CONNECTION_ERROR=u'連接問題', DESIGN_CONFLICT=u'設計衝突',
                   CONSTRUCTION_ISSUE=u'施工問題', OTHER=u'其他')


class Row(object):
    def __init__(self, label, value):
        self.label, self.value = label, value


class CompareRow(object):
    def __init__(self, label, left, right, status):
        self.label, self.left, self.right, self.status = label, left, right, status


def number(value):
    return u'{:.3f}'.format(value).rstrip('0').rstrip('.')


def readable(value):
    if value is None or value == '':
        return MISSING
    if isinstance(value, bool):
        return u'是' if value else u'否'
    if isinstance(value, (float, int)):
        return number(value)
    if isinstance(value, (list, tuple)):
        return u'、'.join(readable(v) for v in value) or MISSING
    if isinstance(value, dict):
        return readable(value.get('value'))  # Never render a raw object as JSON.
    return text_type(value)


def same_category(left, right):
    a, b = left['properties'], right['properties']
    if a.get('category_id') and b.get('category_id'):
        return text_type(a['category_id']) == text_type(b['category_id'])
    return bool(a.get('category') and b.get('category') and a['category'].lower() == b['category'].lower())


def same_type(left, right):
    a, b = left['properties'], right['properties']
    return bool(same_category(left, right) and left['source_document'] == right['source_document']
                and a.get('type_id') not in (None, '', '-1') and a.get('type_id') == b.get('type_id'))


def caption(record):
    if record['node_type'] in ('GROUP', 'ISSUE', 'SOLUTION'):
        return record['name']
    return u'{} · ID {}'.format(record['name'], record['properties'].get('element_id') or u'—')


def fields(record, graph=None):
    """Return ordered (key, label, display, comparison-value, identity-only) fields."""
    p = record['properties']
    values = []

    def add(key, label, value, identity=False, display=None):
        known = value is not None and value != '' and value != []
        values.append((key, label, display or readable(value), value if known else None, identity))

    add('id', u'元素 ID', p.get('element_id'), True)
    add('document', u'來源文件', p.get('document_title'), True)
    add('category', u'類別', p.get('category_id') or p.get('category'), display=readable(p.get('category')))
    add('family', u'族群', p.get('family'))
    add('type_name', u'類型', p.get('type_name'))
    add('ifc_class', u'IFC 類別', p.get('ifc_class'), display=p.get('ifc_class') or u'未設定（依 IFC 匯出設定決定）')
    if p.get('ifc_global_id'):
        add('ifc_global_id', u'IFC GlobalId', p['ifc_global_id'], True)
    context = {}
    if graph:
        nodes = dict((n['node_id'], n) for n in graph['nodes'])
        for edge in graph['edges']:
            if edge['source'] == record['node_id'] and edge['target'] in nodes:
                context.setdefault(edge['relationship_type'], []).append(nodes[edge['target']]['name'])
    add('level', u'所在樓層', p.get('level') or context.get('LOCATED_ON_LEVEL'))
    add('view', u'所屬視圖', p.get('owner_view_name'))
    add('host', u'依附主體', p.get('host_name') or context.get('HOSTED_BY'))
    materials = p.get('materials') or context.get('HAS_MATERIAL')
    add('materials', u'材質', sorted(materials) if isinstance(materials, (list, tuple)) else materials)
    for key, value in sorted((p.get('dimensions') or {}).items()):
        unit = p.get('units')
        factor = FACTORS.get(unit)
        numeric = isinstance(value, (int, float)) and not isinstance(value, bool) and not math.isnan(value) and not math.isinf(value)
        normalized = value*factor if numeric and factor else None
        display = number(normalized)+u' mm' if normalized is not None else readable(value)+u' '+(unit or u'（單位未提供）')
        add('dimension:'+key, DIMENSIONS.get(key, key), normalized, display=display)
    for key, value in sorted((p.get('search_parameters') or {}).items()):
        add('search:'+key, key, value)
    for key, entry in sorted((p.get('parameters') or {}).items()):
        if isinstance(entry, dict):
            value = entry.get('value')
        else:
            value = entry
        scope, sep, name = key.partition(':')
        label = (u'實例 · ' if scope == 'instance' else u'類型 · ' if scope == 'type' else '')+(name if sep else key)
        add('parameter:'+key, label, value)
    return values


def detail_rows(record, graph=None):
    if record['node_type'] in ('ISSUE', 'SOLUTION'):
        p = record['properties']
        rows = [Row(u'類別', KINDS[record['node_type']]), Row(u'名稱', record['name'])]
        for key, label in [('case_id', u'案例編號'), ('case_scope', u'與基準的關係'), ('issue_type', u'問題類型'), ('status', u'案例狀態'),
                           ('description', u'問題描述'), ('element_name', u'案例對應元素'),
                           ('element_id', u'元素 ID'), ('document_title', u'來源文件'),
                           ('solution_state', u'解法驗證'), ('solution', u'解決方案'),
                           ('match_evidence', u'比對依據'), ('reviewer', u'審查人')]:
            if p.get(key) is not None and p.get(key) != '':
                value = STATUS_NAMES.get(p[key], p[key]) if key == 'status' else p[key]
                if key == 'case_id':
                    value = u'RFI-{}'.format(value)
                elif key == 'issue_type':
                    value = ISSUE_NAMES.get(value, value)
                rows.append(Row(label, readable(value)))
        if not p.get('solution'):
            rows.append(Row(u'解決方案', p.get('text') or u'尚未記錄解法'))
        return rows
    return [Row(label, display) for key, label, display, value, identity in fields(record, graph)]


def compare_rows(left, right):
    if left['node_id'] == right['node_id']:
        raise ValueError(u'請選擇兩個不同的元素。')
    if not same_category(left, right):
        raise ValueError(u'請選擇同類別元素，例如牆與牆；可比較不同族群或類型。')
    a, b = fields(left), fields(right)
    am, bm = dict((f[0], f) for f in a), dict((f[0], f) for f in b)
    keys = [f[0] for f in a] + [f[0] for f in b if f[0] not in am]
    rows = []
    for key in keys:
        af, bf = am.get(key), bm.get(key)
        label = (af or bf)[1]
        av, bv = af[3] if af else None, bf[3] if bf else None
        if (af or bf)[4]:
            status = u'識別資料'
        elif av is None or bv is None:
            status = u'資料不足'
        elif isinstance(av, (int, float)) and isinstance(bv, (int, float)):
            status = u'相同' if abs(av-bv) < 1e-6 else u'不同'
        else:
            status = u'相同' if readable(av).strip().lower() == readable(bv).strip().lower() else u'不同'
        rows.append(CompareRow(label, af[2] if af else MISSING, bf[2] if bf else MISSING, status))
    return rows


def relationship_rows(record, graph):
    nodes = dict((n['node_id'], n) for n in graph['nodes'])
    rows = []
    shown_checks = set()
    for edge in graph['edges']:
        if record['node_id'] not in (edge['source'], edge['target']):
            continue
        source, target = nodes[edge['source']], nodes[edge['target']]
        relation = edge['relationship_type']
        if relation == 'MODEL_CHECK':
            p = edge['properties']
            identity = p['result_id']
            if identity in shown_checks:
                continue
            shown_checks.add(identity)
            related = [nodes[e['target']] for e in graph['edges'] if e['relationship_type'] == 'MODEL_CHECK' and e['properties']['result_id'] == identity]
            names = u'、'.join(caption(n) for n in related[:8])
            if len(related) > 8:
                names += u' …（共 {} 個）'.format(len(related))
            detail = u'{}\n{}\n{}\n建議：{}\n視圖：{} · {}'.format(p['status_label'], names, edge['evidence'], p['advice'], p['view_name'], p['checked_at'])
            rows.append(Row(p['label'], detail))
            continue
        label = RELATIONS.get(relation, u'相關')
        status = u'模型關係已確認' if edge.get('validation_status') == 'verified' else u'候選關係，尚待確認'
        if relation == 'SAME_TYPE':
            status = u'同文件、同類別、相同 Revit 類型；實例尺寸仍可能不同'
        elif relation == 'SIMILAR_TO':
            status = u'同類別比較候選；並非實體連接或碰撞'
        elif relation == 'CASE_REFERENCE':
            status = u'案例屬於此參考元素；不表示基準元素有相同問題。\n相似度 {:.0f}%'.format(edge.get('properties', {}).get('score', 0)*100)
        elif relation == 'HAS_ISSUE':
            status = u'案例記錄的對應元素；案例狀態：'+STATUS_NAMES.get(target['properties'].get('status'), u'未提供')
        elif relation == 'RESOLVED_BY':
            status = u'解法已驗證' if source['properties'].get('status') == 'VERIFIED' else u'解法尚未驗證，應先審查適用性'
        detail = u'{} → {}\n{}'.format(caption(source), caption(target), status)
        if relation in ('SAME_TYPE', 'SIMILAR_TO'):
            p = edge.get('properties', {})
            detail += u'\n相似度 {:.0f}% · 可比較資料 {:.0f}%'.format(p.get('score', 0)*100, p.get('completeness', 0)*100)
        rows.append(Row(label, detail))
    return rows or [Row(u'關係', u'目前範圍沒有此節點的關係資料。')]


def with_similar_elements(graph, target, matches, limit=12):
    """UI-only overlay: never add comparison edges to the saved evidence graph."""
    result = deepcopy(graph)
    ids = set(n['node_id'] for n in result['nodes'])
    seen = set([graph['root']])
    count = 0
    for match in matches:
        candidate = match['element']
        if candidate['node_id'] in seen or not same_category(target, candidate):
            continue
        seen.add(candidate['node_id'])
        if count >= limit:
            break
        if candidate['node_id'] not in ids:
            result['nodes'].append(deepcopy(candidate))
            ids.add(candidate['node_id'])
        relation = 'SAME_TYPE' if same_type(target, candidate) else 'SIMILAR_TO'
        result['edges'].append(dict(source=graph['root'], target=candidate['node_id'], relationship_type=relation,
            properties=dict(score=match['score'], completeness=match['completeness']),
            evidence=u'元素屬性比較', validation_status='candidate'))
        count += 1
    return result
