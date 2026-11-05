"""Element retrieval is independent of the issue/verified-solution lifecycle."""
import json
import math
from .database import connection
from .graph_models import Node, ElementSearch, RelatedCaseSearch, Draft
from .case_scope import type_match, type_identity


def identity(node):
    return node.source_document, node.source_element_id


def normalized(value):
    return str(value).strip().casefold() if value is not None else ''


def compatible(a, b):
    ac, bc = a.properties.get('category_id'), b.properties.get('category_id')
    if ac and bc:
        return str(ac) == str(bc)
    ac, bc = a.properties.get('category'), b.properties.get('category')
    return bool(ac and bc and normalized(ac) == normalized(bc))


def compare_elements(target: Node, candidate: Node):
    """Unknown data is omitted from the denominator, never treated as a mismatch."""
    weights = dict(category=.10, family=.25, type_name=.35, dimensions=.20, parameters=.10)
    components = dict.fromkeys(weights)
    evidence, differences = [], []
    labels = dict(category='類別', family='族群', type_name='類型')
    a, b = target.properties, candidate.properties
    for key, label in labels.items():
        av, bv = a.get(key), b.get(key)
        if av and bv:
            same = compatible(target, candidate) if key == 'category' else normalized(av) == normalized(bv)
            components[key] = float(same)
            if same:
                evidence.append('{}相同：{}'.format(label, av))
            else:
                differences.append(dict(field=label, target=str(av), candidate=str(bv)))
    values = []
    factors = {'mm':1., 'cm':10., 'm':1000., 'ft':304.8, 'in':25.4}
    au, bu = factors.get(a.get('units')), factors.get(b.get('units'))
    ad, bd = a.get('dimensions') or {}, b.get('dimensions') or {}
    if isinstance(ad, dict) and isinstance(bd, dict) and au and bu:
        for key in sorted(ad.keys() & bd.keys()):
            av, bv = ad[key], bd[key]
            if not all(isinstance(v,(int,float)) and not isinstance(v,bool) and math.isfinite(v) for v in (av,bv)):
                continue
            av, bv = av*au, bv*bu
            values.append(max(0., 1-abs(av-bv)/max(abs(av),abs(bv),1.)))
            if abs(av-bv) <= max(1., max(abs(av),abs(bv))*.02):
                evidence.append('{} 接近：{} / {} mm'.format(key,round(av,1),round(bv,1)))
            else:
                differences.append(dict(field=key+' (mm)',target=str(round(av,1)),candidate=str(round(bv,1))))
    if values:
        components['dimensions'] = sum(values)/len(values)
    # Compare engineering properties, not instance-specific marks/comments/IDs.
    values = []
    ap, bp = a.get('search_parameters') or {}, b.get('search_parameters') or {}
    if isinstance(ap, dict) and isinstance(bp, dict):
        for key in sorted(ap.keys() & bp.keys()):
            if ap[key] in (None,'') or bp[key] in (None,''):
                continue
            same = normalized(ap[key]) == normalized(bp[key])
            values.append(float(same))
            if same:
                evidence.append('{} 相同：{}'.format(key,ap[key]))
            else:
                differences.append(dict(field=key,target=str(ap[key]),candidate=str(bp[key])))
    if values:
        components['parameters'] = sum(values)/len(values)
    available = sum(weights[k] for k,v in components.items() if v is not None)
    score = sum(weights[k]*v for k,v in components.items() if v is not None)/available if available else 0
    return dict(score=round(score,4), completeness=round(available,4), components=components,
                evidence=evidence, differences=differences)


def category_clause(node, expression):
    """SQL expressions come only from constants in this module, never requests."""
    cat_id = node.properties.get('category_id')
    category = node.properties.get('category')
    if cat_id:
        return (f"(CAST(json_extract({expression}, '$.properties.category_id') AS TEXT)=? OR "
                f"(json_extract({expression}, '$.properties.category_id') IS NULL AND "
                f"lower(json_extract({expression}, '$.properties.category'))=lower(?)))", (str(cat_id),category))
    return f"lower(json_extract({expression}, '$.properties.category'))=lower(?)", (category,)


def search_elements(request: ElementSearch):
    candidates = {}
    truncated = request.truncated
    stored_count = 0
    if request.include_recorded or request.scope == 'recorded':
        clause, params = category_clause(request.target, 'payload')
        with connection() as db:
            stored_count = db.execute('SELECT COUNT(*) FROM graph_elements').fetchone()[0]
            rows = db.execute('SELECT payload FROM graph_elements WHERE '+clause+' ORDER BY document,unique_id LIMIT ?',
                              (*params,request.candidate_limit+1)).fetchall()
        truncated = truncated or len(rows) > request.candidate_limit
        for row in rows[:request.candidate_limit]:
            n = Node.model_validate_json(row['payload'])
            candidates[identity(n)] = (n,'recorded')
    # Fresh Revit metadata takes precedence over a recorded snapshot of the same element.
    for n in request.candidates:
        candidates[identity(n)] = (n,request.scope)
    candidates.pop(identity(request.target),None)
    results, comparable = [], 0
    for n, source in candidates.values():
        if not compatible(request.target,n):
            continue
        comparable += 1
        comparison = compare_elements(request.target,n)
        if comparison['score'] < request.minimum_score:
            continue
        results.append(dict(element=n.model_dump(), source=source, **comparison))
    results.sort(key=lambda r:(-r['score'],-r['completeness'],r['element']['source_document'],r['element']['source_element_id']))
    if results:
        message = '找到 {} 個相似元素，顯示前 {} 個。'.format(len(results),min(len(results),request.limit))
    elif request.scope == 'recorded' and stored_count == 0:
        message = '尚未記錄其他元素。請改用「目前模型」搜尋，或先記錄其他專案的元素。'
    elif comparable:
        message = '有 {} 個同類別元素，但未達相似度門檻；請降低門檻。'.format(comparable)
    else:
        message = '此範圍沒有其他同類別元素。請改選「目前模型」，或確認模型中有其他同類別實例。'
    if truncated:
        message += ' 已達候選上限；結果僅涵蓋已讀取的元素，可縮小至目前視圖。'
    return dict(results=results[:request.limit], message=message, compared_count=comparable, matched_count=len(results),truncated=truncated)


def case_candidates(request: RelatedCaseSearch):
    """Apply the CASE contract before the candidate limit, shared with verified analysis."""
    target = request.target.model_dump()
    conditions = ["c.status!='ARCHIVED'",
                  "EXISTS (SELECT 1 FROM json_each(r.payload,'$.graph.nodes') n "
                  "WHERE json_extract(n.value,'$.node_id')=json_extract(r.payload,'$.graph.root') "
                  "AND case_type_matches(n.value)=1)"]
    params = []
    if request.issue_type != 'ALL':
        conditions.append("json_extract(r.payload,'$.issue_type')=?")
        params.append(request.issue_type.value)
    if request.verified_only:
        conditions.append("c.status='VERIFIED'")
    if request.exclude_case_id is not None:
        conditions.append('c.id!=?')
        params.append(request.exclude_case_id)
    with connection() as db:
        db.create_function('case_type_matches', 1, lambda raw: int(bool(type_match(target, json.loads(raw)))))
        total = db.execute('SELECT COUNT(*) FROM graph_cases').fetchone()[0]
        rows = db.execute('SELECT c.*,r.payload FROM graph_cases c JOIN graph_revisions r ON r.case_id=c.id '
                          'AND r.version=c.revision WHERE '+' AND '.join(conditions)+' ORDER BY c.id DESC LIMIT ?',
                          (*params, request.candidate_limit+1)).fetchall()
    return rows, total


def search_related_cases(request: RelatedCaseSearch):
    target = request.target.model_dump()
    issue_type = request.issue_type if request.issue_type == 'ALL' else request.issue_type.value
    classification = dict(schema_version=1, element_type=type_identity(target), issue_type=issue_type)
    if not classification['element_type']['complete']:
        return dict(results=[], message='基準元素缺少類型資料，無法比對同類型案例。請重新讀取模型元素。',
                    classification=classification, matched_count=0, truncated=False)
    rows, total = case_candidates(request)
    results = []
    for row in rows[:request.candidate_limit]:
        draft = Draft.model_validate_json(row['payload'])
        element = next(n for n in draft.graph.nodes if n.node_id == draft.graph.root)
        verified = row['status'] == 'VERIFIED'
        results.append(dict(case_id=row['id'],title=draft.title,description=draft.description,
            issue_type=draft.issue_type.value, status=row['status'], reliable_solution=verified,
            solution=row['solution'] if verified else '', resolution_note=row['solution'] if not verified else '',
            element=element.model_dump(), classification=draft.classification.model_dump(mode='json'),
            match_basis=type_match(target, element.model_dump()), **compare_elements(request.target,element)))
    results.sort(key=lambda r:(identity(request.target) != (r['element']['source_document'], r['element']['source_element_id']),
                               -r['score'],not r['reliable_solution'],-r['case_id']))
    scope_label = '同元件類型 · 所有問題類型' if issue_type == 'ALL' else '同元件類型＋同問題類型'
    message = (scope_label+'：找到 {} 個案例，顯示 {} 個。'.format(len(results), min(len(results), request.limit))) if results else (
        '案例庫尚無案例。' if total == 0 else '沒有符合「'+scope_label+'」的案例。')
    return dict(results=results[:request.limit], message=message, classification=classification,
                matched_count=len(results), truncated=len(rows)>request.candidate_limit or len(results)>request.limit)
