"""Reusable solutions: search, replayable recipes and Revit MCP fix plans.

A recipe comes from the model-change evidence stored with a review step. A fix plan
maps that recipe onto a new target element as Revit MCP tool calls. Plans are
proposals: an agent must show them to the user and confirm before modifying a model.
"""
import json
import math
import re
from typing import Any, Literal

from fastapi import HTTPException
from pydantic import BaseModel, Field

from .database import connection
from .graph_models import Draft, IssueType

FEET_MM = 304.8
REPLAYABLE = {'set_parameter', 'change_type', 'move', 'flip', 'delete', 'floor_opening'}
MEP_CATEGORIES = {'Pipes', 'Ducts', 'Conduits', 'Cable Trays', 'Flex Pipes', 'Flex Ducts'}


def raw_change(element, name):
    """(before, after) internal values of a recorded read-only parameter, or None."""
    for change in element.get('changes', []):
        action = change.get('action') or {}
        if action.get('op') == 'set_parameter' and action.get('name') == name and                 number(action.get('before_raw')) and number(action.get('after_raw')):
            return float(action['before_raw']), float(action['after_raw'])
    return None


def derived_floor_opening(element):
    """Older floor records only kept Area/Perimeter: a new rectangular hole adds its perimeter
    and removes its area, so its sides solve a+b=ΔP/2, ab=ΔA. Returns sizes in mm or None."""
    if element.get('category') != 'Floors':
        return None
    area, perimeter = raw_change(element, 'Area'), raw_change(element, 'Perimeter')
    if not area or not perimeter:
        return None
    removed, added = (area[0] - area[1]) * FEET_MM ** 2, (perimeter[1] - perimeter[0]) * FEET_MM
    half = added / 2
    disc = half * half - 4 * removed
    if removed <= 1 or added <= 1 or disc < -1:
        return None
    root = math.sqrt(max(disc, 0))
    length, width = round((half + root) / 2), round((half - root) / 2)
    if width <= 0:
        return None
    return dict(op='floor_opening', length_mm=length, width_mm=width, rotation_deg=0, derived=True)


def mep_centre(node):
    return position((node or {}).get('properties') or {}, 'bounding_box_center')


# Which case-graph relation best explains "this step was done relative to that element".
ANCHOR_ORDER = ('INTERSECTS', 'HOSTED_BY', 'ADJACENT_TO', 'CONNECTED_TO', 'CONTAINED_IN', 'NEARBY', 'RELATED_TO')
AXES = ('x', 'y', 'z')


def node_box(node):
    """Recorded bounding box {min, max} in model mm, or None."""
    box = ((node or {}).get('properties') or {}).get('bounding_box') or {}
    low, high = box.get('min') or {}, box.get('max') or {}
    if box.get('units', 'mm') != 'mm' or not all(number(low.get(k)) and number(high.get(k)) for k in AXES):
        return None
    return dict(min={k: float(low[k]) for k in AXES}, max={k: float(high[k]) for k in AXES})


def shifted(box, delta):
    return box and dict(min={k: box['min'][k] + delta.get(k, 0) for k in AXES},
                        max={k: box['max'][k] + delta.get(k, 0) for k in AXES})


def gap(box, anchor, axis, side):
    """Signed face gap from the anchor on `side` (+1: box beyond the anchor's max face); negative = overlap."""
    return box['min'][axis] - anchor['max'][axis] if side > 0 else anchor['min'][axis] - box['max'][axis]


def anchor_rank(element):
    relations = element.get('relations') or []
    return min([ANCHOR_ORDER.index(r) for r in relations if r in ANCHOR_ORDER] or [len(ANCHOR_ORDER)])


def case_anchors(element, elements, nodes, moves):
    """Case-graph neighbours the step was relative to, closest relation first, each with its post-fix case box.
    The root's anchors are its related elements; a related element's anchor is the root."""
    others = [e for e in elements if e is not element and (e.get('root') or element.get('root'))]
    found = []
    for other in sorted(others, key=lambda e: (anchor_rank(element if e.get('root') else e), str(e.get('element_id')))):
        relations = (element if other.get('root') else other).get('relations') or []
        box = shifted(node_box(nodes.get(other.get('node_id'))), moves.get(other.get('element_id'), {}))
        found.append(dict(element_id=other.get('element_id'), name=other.get('name'), category=other.get('category'),
                          relations=relations, root=bool(other.get('root')), case_box_mm=box, state=other.get('state')))
    return found[:3]


def move_relation(step, anchors, nodes, element):
    """What a recorded move achieved relative to its closest anchor: per moved axis, the face gap after the fix."""
    action = step['action']
    before = node_box(nodes.get(element.get('node_id')))
    anchor = next((a for a in anchors if a['case_box_mm']), None)
    if not before or not anchor:
        return None
    after, other = shifted(before, {k: action.get('d'+k, 0) for k in AXES}), anchor['case_box_mm']
    axes = {}
    for k in AXES:
        if abs(action.get('d'+k, 0)) < 1:
            continue
        side = 1 if after['min'][k] + after['max'][k] >= other['min'][k] + other['max'][k] else -1
        axes[k] = dict(side=side, gap_mm=round(gap(after, other, k, side), 1), case_gap_before_mm=round(gap(before, other, k, side), 1))
    return dict(anchor=dict((k, anchor[k]) for k in ('element_id', 'name', 'category', 'relations', 'root')), axes=axes) if axes else None


def live_box(value):
    """get_element_geometry(boundingbox) fields MinX…MaxZ (mm) → {min, max}."""
    return value and dict(min=dict(x=value.MinX, y=value.MinY, z=value.MinZ), max=dict(x=value.MaxX, y=value.MaxY, z=value.MaxZ))


def adaptive_move(relation, box, anchor):
    """Displacement (mm) that restores the case's post-fix gap to the anchor from the current geometry."""
    delta = dict(x=0., y=0., z=0.)
    for k, axis in relation['axes'].items():
        delta[k] = round((axis['gap_mm'] - gap(box, anchor, k, axis['side'])) * axis['side'], 1)
    return delta


def case_clearance(action, node):
    """Per-side gap between the case opening and the case MEP footprint, in mm along the opening's
    length (u) and width (v). The case's practice is this gap, not its absolute size: a different pipe
    needs its own size. None when the snapshot has no usable MEP footprint."""
    box = ((node or {}).get('properties') or {}).get('bounding_box') or {}
    low, high, centre = box.get('min') or {}, box.get('max') or {}, mep_centre(node)
    if not centre or box.get('units', 'mm') != 'mm' or not all(number(low.get(k)) and number(high.get(k)) for k in 'xy'):
        return None
    angle = math.radians(action.get('rotation_deg') or 0)
    cos, sin = math.cos(angle), math.sin(angle)
    dx, dy = high['x'] - low['x'], high['y'] - low['y']
    extent_u, extent_v = abs(dx * cos) + abs(dy * sin), abs(dx * sin) + abs(dy * cos)
    hole = action.get('center_mm') or centre  # Older records only kept the size: centred on the MEP.
    du = (centre['x'] - hole['x']) * cos + (centre['y'] - hole['y']) * sin
    dv = -(centre['x'] - hole['x']) * sin + (centre['y'] - hole['y']) * cos
    length, width = action['length_mm'], action['width_mm']
    sides = dict(length_start=length/2 + du - extent_u/2, length_end=length/2 - du - extent_u/2,
                 width_start=width/2 + dv - extent_v/2, width_end=width/2 - dv - extent_v/2)
    # A footprint wider than the hole is not a vertical riser (e.g. a sloped run): no reliable gap.
    if extent_u <= 0 or extent_v <= 0 or min(sides.values()) < -1:
        return None
    return dict({k: round(max(v, 0), 1) for k, v in sides.items()}, case_mep_mm=dict(length=round(extent_u, 1), width=round(extent_v, 1)))


def adaptive_opening(action, clearance, mep_outer_mm):
    """Case clearance around the current MEP: size and centre offset (mm) for preview_floor_opening."""
    angle = math.radians(action.get('rotation_deg') or 0)
    c = clearance
    along, across = (c['length_end'] - c['length_start']) / 2, (c['width_end'] - c['width_start']) / 2
    return dict(length_mm=math.ceil(mep_outer_mm + c['length_start'] + c['length_end']),
                width_mm=math.ceil(mep_outer_mm + c['width_start'] + c['width_end']),
                offset_mm=dict(x=round(along * math.cos(angle) - across * math.sin(angle), 1),
                               y=round(along * math.sin(angle) + across * math.cos(angle), 1)))


class SolutionSearch(BaseModel):
    query: str = Field(default='', max_length=2000)
    category: str | None = None
    family: str | None = None
    type_name: str | None = None
    issue_type: IssueType | Literal['ALL'] = 'ALL'
    verified_only: bool = True
    recipe_only: bool = False
    limit: int = Field(default=5, ge=1, le=20)


class FixTarget(BaseModel):
    element_id: int
    type_id: int | None = None
    category: str | None = None
    family: str | None = None
    type_name: str | None = None
    document: str | None = None


class LiveBox(BaseModel):
    MinX: float
    MinY: float
    MinZ: float
    MaxX: float
    MaxY: float
    MaxZ: float


class FixPlanRequest(BaseModel):
    target: FixTarget
    # Prefer element:<recorded ID>; relation keys remain supported when unambiguous.
    related: dict[str, int] = Field(default_factory=dict)
    # Element ID of the target type to use for change_type, once resolved in the target model.
    type_ids: dict[str, int] = Field(default_factory=dict)
    # Outer diameter (incl. insulation) of the current MEP element, from preview_floor_opening's mepOuterDiameterMm.
    mep_outer_mm: float | None = Field(default=None, gt=0, le=10000)
    # Current bounding boxes keyed by target-model element ID, as get_element_geometry(boundingbox) returns them (mm).
    geometry: dict[str, LiveBox] = Field(default_factory=dict, max_length=50)


class Application(BaseModel):
    document: str = Field(default='', max_length=500)
    element_ids: list[int] = Field(default_factory=list, max_length=200)
    outcome: Literal['APPLIED', 'PARTIAL', 'FAILED', 'REJECTED']
    steps: list[dict[str, Any]] = Field(default_factory=list, max_length=200)
    notes: str = Field(default='', max_length=5000)
    resolved: bool | None = None  # Whether re-checking confirmed the problem is gone.


def tokens(text):
    """Latin words plus CJK characters and bigrams, so Chinese matches without a segmenter.

    Single characters let "門被牆擋住" meet "門與牆碰撞"; bigrams reward closer wording.
    """
    text = (text or '').lower()
    words = set(re.findall(r'[a-z0-9]+', text))
    for run in re.findall(r'[㐀-鿿]+', text):
        words.update(run)
        words.update(run[i:i+2] for i in range(len(run)-1))
    return words


def same(a, b):
    return bool(a and b and str(a).strip().casefold() == str(b).strip().casefold())


def history_rows(db, case_id):
    return [{**dict(r), 'evidence': json.loads(r['evidence']) if r['evidence'] else None}
            for r in db.execute('SELECT * FROM graph_history WHERE case_id=? ORDER BY id', (case_id,))]


def position(properties, basis):
    """Coordinates from the recorded extractor contract, in model millimetres."""
    def point(value):
        if not isinstance(value, dict):
            return None
        if all(isinstance(value.get(k), (int, float)) and not isinstance(value[k], bool)
               and math.isfinite(value[k]) for k in ('x', 'y', 'z')):
            return {k: value[k] for k in ('x', 'y', 'z')}
    if properties.get('units', 'mm') != 'mm':
        return None
    if basis == 'location':
        return point(properties.get('location'))
    box = properties.get('bounding_box') or {}
    low, high = point(box.get('min')), point(box.get('max'))
    if box.get('units', 'mm') == 'mm' and low and high:
        return {k: (low[k] + high[k]) / 2 for k in low}


def legacy_evidence(case):
    """Recover only our generated, revision-matched move records; never parse free prose as a command."""
    graph = case['draft']['graph']
    text = case['solution'].replace('\r\n', '\n').replace('\r', '\n')
    header = re.search(r'^模型變更紀錄（([^\n；]+)；對比 RFI-(\d+) 第 (\d+) 版快照）$', text, re.M)
    if not header or int(header[2]) != case['id'] or int(header[3]) != case['revision']:
        return None
    # Several appended reports may describe different states. Leave them for AI review.
    if len(re.findall(r'^模型變更紀錄（', text, re.M)) != 1:
        return None
    nodes = {str(n['properties'].get('element_id')): n for n in graph['nodes'] if n['node_type'] == 'ELEMENT'}
    elements = []
    scalar = r'([+-]?\d+(?:\.\d+)?)'
    pattern = r'(位置(?:（外框中心）)?) 移動 ' + scalar + r' mm → X ' + scalar + r'／Y ' + scalar + r'／Z ' + scalar + r' mm'
    for line in text[header.end():].splitlines():
        match = re.fullmatch(r'・(.+)（ID (\d+)）：(.+)', line)
        if not match or match[2] not in nodes:
            continue
        node = nodes[match[2]]
        if match[1] != node['name']:
            continue
        p = node['properties']
        changes = []
        for row in match[3].split('；'):
            move = re.fullmatch(pattern, row)
            if move:
                delta = [float(move[i]) for i in (3, 4, 5)]
                distance = math.sqrt(sum(v*v for v in delta))
                if not math.isfinite(distance) or abs(distance - float(move[2])) > 2 or distance == 0:
                    continue
                changes.append(dict(field=move[1], before='移動 {} mm'.format(move[2]), after=row.split(' → ', 1)[1],
                    action=dict(op='move', dx=delta[0], dy=delta[1], dz=delta[2])))
            else:
                changes.append(dict(field='舊版記錄', before='', after=row, action=None))
        relations = []
        for edge in graph['edges']:
            if edge['source'] == graph['root'] and edge['target'] == node['node_id']:
                relations.append(edge['relationship_type'])
            elif edge['target'] == graph['root'] and edge['source'] == node['node_id']:
                relations.append(edge['relationship_type'] + ':REVERSE')
        elements.append(dict(node_id=node['node_id'], element_id=match[2], unique_id=node['source_element_id'],
            name=node['name'], root=node['node_id'] == graph['root'], category=p.get('category'), family=p.get('family'),
            type_name=p.get('type_name'), relations=sorted(set(relations)), state='CHANGED', changes=changes))
    root = next(n for n in graph['nodes'] if n['node_id'] == graph['root'])
    return dict(checked_at=header[1], revision=int(header[3]), document=root['source_document'],
                source='legacy_generated_record', elements=elements)


def recipe_from(case, history):
    """Steps from the latest review evidence. Rotations and unknown actions stay manual."""
    evidence = next((h['evidence'] for h in reversed(history) if h['evidence']), None) or legacy_evidence(case)
    steps, manual, observations = [], [], []
    nodes = {n['node_id']: n for n in case['draft']['graph']['nodes']}
    for element in (evidence or {}).get('elements', []):
        role = 'target' if element.get('root') else 'related'
        owner = dict(role=role, relations=element.get('relations', []), name=element.get('name'),
                     element_id=element.get('element_id'), category=element.get('category'),
                     family=element.get('family'), type_name=element.get('type_name'),
                     unique_id=element.get('unique_id'), source_document=(evidence or {}).get('document'))
        if element['state'] == 'DELETED':
            steps.append(dict(owner, action=dict(op='delete'), label='刪除元件'))
        for change in element.get('changes', []):
            action = change.get('action')
            label = '{}：{} → {}'.format(change['field'], change['before'], change['after'])
            if action and action.get('op') == 'set_parameter' and action.get('read_only'):
                observations.append(dict(owner, label=label, reason='唯讀計算結果，用於修正後比對，不是修改指令。'))
            elif action and action.get('op') in REPLAYABLE:
                step = dict(owner, action=action, label=label)
                if action['op'] == 'move':
                    basis = action.get('position_basis') or ('bounding_box_center' if '外框中心' in change['field'] else 'location')
                    node = nodes.get(element.get('node_id'), {})
                    # Do not combine a historical action with a different revision's snapshot.
                    before = action.get('before_position_mm') or (position(node.get('properties') or {}, basis)
                        if evidence['revision'] == case['revision'] else None)
                    step['position_basis'] = basis
                    step['before_position_mm'] = before
                    step['after_position_mm'] = action.get('after_position_mm') or (
                        {k: before[k] + action['d'+k] for k in ('x', 'y', 'z')} if before else None)
                steps.append(step)
            elif element['state'] == 'CHANGED':
                manual.append(dict(owner, label=label, reason='沒有可自動重播的動作'))
        # New records carry the hole itself; only older records need the Area/Perimeter estimate.
        opening = None if any((c.get('action') or {}).get('op') == 'floor_opening' or c.get('field') == '樓板開孔'
                              for c in element.get('changes', [])) else derived_floor_opening(element)
        if opening:
            steps.append(dict(owner, action=opening, label='樓板開孔 {} × {} mm（由面積／周長變化推算）'.format(
                opening['length_mm'], opening['width_mm'])))
    # A floor opening is placed around the MEP element that passed through the slab in the case.
    meps = [e for e in (evidence or {}).get('elements', []) if not e.get('root') and e.get('category') in MEP_CATEGORIES
            and 'INTERSECTS' in (e.get('relations') or [])]
    for step in steps:
        if step['action'].get('op') != 'floor_opening':
            continue
        step['mep'] = [dict(element_id=e.get('element_id'), name=e.get('name'), category=e.get('category'),
                            relations=e.get('relations', [])) for e in meps]
        centre = step['action'].get('center_mm')
        anchor = mep_centre(nodes.get(meps[0].get('node_id'))) if meps and centre else None
        step['offset_mm'] = dict(x=round(centre['x'] - anchor['x'], 1), y=round(centre['y'] - anchor['y'], 1)) if anchor else dict(x=0, y=0)
        step['clearance_mm'] = case_clearance(step['action'], nodes.get(meps[0].get('node_id'))) if meps else None
    # Every step is replayed relative to the elements it related to in the case graph, not by its absolute values.
    elements = (evidence or {}).get('elements', [])
    moves = {s['element_id']: {k: s['action'].get('d'+k, 0) for k in AXES} for s in steps if s['action']['op'] == 'move'}
    for step in steps:
        element = next((e for e in elements if e.get('element_id') == step['element_id']), None)
        step['anchors'] = case_anchors(element, elements, nodes, moves) if element else []
        if step['action']['op'] == 'move' and element:
            step['relation'] = move_relation(step, step['anchors'], nodes, element)
    root =next(n for n in case['draft']['graph']['nodes'] if n['node_id'] == case['draft']['graph']['root'])
    return dict(case_id=case['id'], status=case['status'], verified=case['status'] == 'VERIFIED',
                title=case['draft']['title'], issue_type=case['draft']['issue_type'], solution=case['solution'],
                signature=dict(issue_type=case['draft']['issue_type'], category=root['properties'].get('category'),
                               family=root['properties'].get('family'), type_name=root['properties'].get('type_name'),
                               origin=(case['draft'].get('origin') or {}).get('check')),
                evidence=dict(checked_at=evidence['checked_at'], revision=evidence['revision'], document=evidence['document'],
                              source=evidence.get('source', 'structured_review')) if evidence else None,
                steps=steps, manual=manual, observations=observations)


def load_case(db, case_id):
    row = db.execute('SELECT c.*, r.payload FROM graph_cases c JOIN graph_revisions r ON r.case_id=c.id AND r.version=c.revision '
                     'WHERE c.id=?', (case_id,)).fetchone()
    if row is None:
        raise HTTPException(404, 'Case not found')
    case = dict(row)
    case['draft'] = Draft.model_validate_json(case.pop('payload')).model_dump(mode='json')
    return case


def recipe(case_id):
    with connection() as db:
        return recipe_from(load_case(db, case_id), history_rows(db, case_id))


def search_solutions(request: SolutionSearch):
    wanted = tokens(request.query)
    conditions, params = ["c.status!='ARCHIVED'", "trim(c.solution)!=''"], []
    if request.verified_only:
        conditions.append("c.status='VERIFIED'")
    with connection() as db:
        ids = [r[0] for r in db.execute('SELECT c.id FROM graph_cases c WHERE '+' AND '.join(conditions)+' ORDER BY c.id DESC LIMIT 2000', params)]
        results = []
        for case_id in ids:
            case = load_case(db, case_id)
            draft = case['draft']
            if request.issue_type != 'ALL' and draft['issue_type'] != request.issue_type.value:
                continue
            item = recipe_from(case, history_rows(db, case_id))
            if request.recipe_only and not item['steps']:
                continue
            sig, reasons, score = item['signature'], [], 0.
            if request.category:
                if not same(sig['category'], request.category):
                    continue
                score += .05  # Same category stays a candidate for the agent to judge.
                reasons.append('同類別')
            if same(sig['family'], request.family) and same(sig['type_name'], request.type_name):
                score += .4
                reasons.append('同族群與類型')
            elif same(sig['family'], request.family):
                score += .2
                reasons.append('同族群')
            if wanted:
                text = tokens(' '.join([draft['title'], draft['description'], case['solution']]))
                overlap = len(wanted & text) / len(wanted)
                score += .6 * overlap
                if overlap:
                    reasons.append('描述相符 {:.0%}'.format(overlap))
            elif not request.family:
                score += .1
            if score <= 0:
                continue
            applications = db.execute("SELECT COUNT(*), SUM(outcome='APPLIED' AND resolved=1) FROM graph_applications WHERE case_id=?",
                                      (case_id,)).fetchone()
            results.append(dict(case_id=case_id, title=draft['title'], status=case['status'], verified=item['verified'],
                                issue_type=draft['issue_type'], score=round(score, 3), reasons=reasons, signature=sig,
                                solution=case['solution'][:1500], replayable_steps=len(item['steps']),
                                manual_steps=len(item['manual']), applications=applications[0], resolved_applications=applications[1] or 0))
    results.sort(key=lambda r: (-r['score'], not r['verified'], -r['case_id']))
    message = '找到 {} 個解法。'.format(len(results)) if results else (
        '沒有已驗證的相符解法；可設 verified_only=false 查看未驗證解法。' if request.verified_only else '沒有相符的解法。')
    return dict(results=results[:request.limit], message=message, verified_only=request.verified_only)


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def parameter_value(action):
    """String for Revit MCP modify_element_parameter, which calls Parameter.Set with internal units."""
    storage, raw = action.get('storage'), action.get('after_raw')
    if storage == 'ElementId':
        return None, 'Revit MCP 不支援設定元素參照參數（ElementId），需手動處理。'
    if storage == 'Double':
        if number(raw):
            return repr(float(raw)), None
        if number(action.get('after_mm')):
            return repr(action['after_mm']/FEET_MM), None
        return None, '缺少內部單位數值；請以新版擷取重新記錄此案例。'
    if storage == 'Integer' and number(raw):
        return str(int(raw)), None
    if storage == 'String':
        return '' if raw is None else str(raw), None
    if number(action.get('after_mm')):
        return repr(action['after_mm']/FEET_MM), None
    # Older snapshots lack storage data: only plain text is safe to send unchanged.
    after = action.get('after')
    if after is not None and not re.search(r'\d', str(after)):
        return str(after), '舊快照沒有參數型別；已按文字值套用，請先確認。'
    return None, '舊快照沒有參數型別與內部數值，無法安全換算；請手動設定或重新記錄案例。'


def fix_plan(case_id, request: FixPlanRequest):
    plan = recipe(case_id)
    target, steps, warnings = request.target, [], []
    sig = plan['signature']
    if target.category and sig['category'] and not same(target.category, sig['category']):
        warnings.append('目標類別「{}」與案例「{}」不同，不建議套用。'.format(target.category, sig['category']))
    if target.type_name and sig['type_name'] and not same(target.type_name, sig['type_name']):
        warnings.append('目標類型與案例不同；尺寸與參數值可能需要調整。')
    if not plan['verified']:
        warnings.append('此解法尚未經人工驗證。')
    if not plan['steps']:
        warnings.append('此案例沒有結構化動作；請以 casegraph_get_case 讀取完整解法與快照，再查詢現況建立建議。')
    if (plan['evidence'] or {}).get('source') == 'legacy_generated_record':
        warnings.append('步驟由舊版模型變更記錄還原；已保留原快照位置，套用前須以 Revit MCP 比對現況。')
    def current_id(anchor):
        """Target-model ID of a case-graph anchor: the root maps to the target, others through related."""
        return target.element_id if anchor.get('root') else request.related.get('element:' + str(anchor.get('element_id')))

    for index, step in enumerate(plan['steps'], 1):
        action, unresolved, notes, already, after = step['action'], [], [], False, None
        if step['role'] == 'target':
            element_id = target.element_id
        else:
            specific = 'element:' + str(step['element_id'])
            key = specific if specific in request.related else next((r for r in step['relations'] if r in request.related
                and len({s['element_id'] for s in plan['steps'] if s['role'] == 'related' and r in s['relations']}) == 1), None)
            element_id = request.related.get(key) if key else None
            if element_id is None:
                unresolved.append('以 Revit MCP 找出目標關聯元件（{}；案例中為 {} · {}），再以 related["{}"] 提供其 ID。'.format(
                    '、'.join(step['relations']) or '相關', step.get('name'), step.get('category'), specific))
        call = None
        op = action['op']
        if op == 'set_parameter':
            value, problem = parameter_value(action)
            owner = element_id
            if action.get('scope') == 'type':
                owner = target.type_id if step['role'] == 'target' else None
                notes.append('類型參數：會影響此類型的所有實例；如只需改此元件，請先複製類型。')
                if owner is None:
                    unresolved.append('提供目標元素的類型 ID（target.type_id）。')
            if action.get('read_only'):
                unresolved.append('參數唯讀，無法直接設定。')
            if problem:
                (unresolved if value is None else notes).append(problem)
            if action.get('builtin'):
                notes.append('參數按名稱設定；若 Revit 介面語言不同，名稱可能不同（內建參數 {}）。'.format(action['builtin']))
            call = dict(tool='modify_element_parameter', arguments=dict(elementId=owner, parameterName=action['name'], value=value))
            expect = dict(parameter=action['name'], before=action.get('before'))
        elif op == 'change_type':
            label = '{}／{}'.format(action.get('family'), action.get('type_name'))
            type_id = request.type_ids.get(label)
            # Type IDs are only meaningful in the document the fix was recorded in.
            if type_id is None and action.get('to_type_id') and target.document and target.document == (plan['evidence'] or {}).get('document'):
                type_id = int(action['to_type_id'])
            if type_id is None:
                unresolved.append('在目標模型找出類型「{}」的 typeId（get_types_by_category / list_family_symbols），以 type_ids["{}"] 提供；若不存在需先載入族群。'.format(label, label))
            call = dict(tool='change_element_type', arguments=dict(elementId=element_id, typeId=type_id))
            expect = dict(type=step.get('type_name'))
        elif op == 'move':
            delta, relation, after = dict(x=action['dx'], y=action['dy'], z=action['dz']), step.get('relation'), None
            expect = dict(position_mm=step.get('before_position_mm'), basis=step.get('position_basis'),
                          document=(plan['evidence'] or {}).get('document'), element_id=step['element_id'])
            if relation:
                anchor = relation['anchor']
                anchor_id = current_id(anchor)
                box = request.geometry.get(str(element_id)) if element_id else None
                other = request.geometry.get(str(anchor_id)) if anchor_id else None
                gaps = '、'.join('{} {:g} mm'.format(k.upper(), a['gap_mm']) for k, a in relation['axes'].items())
                if box and other:
                    box = live_box(box)
                    delta = adaptive_move(relation, box, live_box(other))
                    centre = {k: (box['min'][k] + box['max'][k]) / 2 for k in AXES}
                    expect = dict(expect, position_mm=centre, basis='bounding_box_center', element_id=element_id, document=target.document)
                    after = {k: round(centre[k] + delta[k], 1) for k in AXES}
                    notes.append('自適應位移：維持案例修正後與 {}（{}）的面距 {}；由目前外框算出，不照抄案例向量 dx {:g} / dy {:g} / dz {:g}。'.format(
                        anchor.get('name'), '、'.join(anchor.get('relations') or []), gaps, action['dx'], action['dy'], action['dz']))
                    if all(abs(v) < 1 for v in delta.values()):
                        already = True
                        notes.append('現況已符合案例修正後的關係，不需移動；此步應略過。')
                else:
                    unresolved.append('以 get_element_geometry(geometryType="boundingbox") 讀取目前元素 {} 及關係元件 {}（案例中為 {} · {}）的外框，'
                                      '以 geometry["元素ID"] 傳入，按案例關係（{}）自適應位移。'.format(
                                          element_id or '（未對應）', anchor_id or '（未對應）', anchor.get('name'), anchor.get('category'), gaps))
            else:
                notes.append('案例圖沒有可用的關係元件外框，暫用案例位移向量；不同模型需按現場條件確認。')
                notes.append('按 basis 比對同一種位置；bounding_box_center 使用 get_element_geometry 的 boundingbox（mm）中心，location 不可當作外框中心。現況已等於修正後位置時不可再次位移。')
            call = dict(tool='move_element', arguments=dict(elementId=element_id, dx=delta['x'], dy=delta['y'], dz=delta['z']))
        elif op == 'flip':
            call = dict(tool='flip_element', arguments=dict(elementId=element_id, flipType=action['flip']))
            expect = None
        elif op == 'floor_opening':
            candidates = step.get('mep') or []
            mep_id = next((request.related['element:' + str(m['element_id'])] for m in candidates
                           if 'element:' + str(m['element_id']) in request.related), None)
            if mep_id is None and 'INTERSECTS' in request.related:
                mep_id = request.related['INTERSECTS']
            if mep_id is None:
                unresolved.append('找出本次穿越樓板的管線（案例中為 {}），以 related["element:{}"] 提供其 ID。'.format(
                    '、'.join('{} · {}'.format(m.get('name'), m.get('category')) for m in candidates) or '管線',
                    candidates[0]['element_id'] if candidates else '案例管線ID'))
            offset = step.get('offset_mm') or dict(x=0, y=0)
            length, width = action['length_mm'], action['width_mm']
            clearance = step.get('clearance_mm')
            adaptive = None
            if clearance:
                adaptive = dict(clearance_mm={k: v for k, v in clearance.items() if k != 'case_mep_mm'},
                                case_mep_mm=clearance['case_mep_mm'], case_opening_mm=dict(length=length, width=width))
                if request.mep_outer_mm:
                    sized = adaptive_opening(action, clearance, request.mep_outer_mm)
                    length, width, offset = sized['length_mm'], sized['width_mm'], sized['offset_mm']
                    adaptive['mep_outer_mm'] = request.mep_outer_mm
                    notes.append('自適應尺寸：本次管線外徑 {:g} mm 加案例每邊餘量 → {} × {} mm（案例 {} × {} mm）。'.format(
                        request.mep_outer_mm, length, width, action['length_mm'], action['width_mm']))
                else:
                    unresolved.append('先以下列參數（不含 expectedGeometry）呼叫 preview_floor_opening 讀取 mepOuterDiameterMm'
                                      '（too_small 也會回傳），再以 mep_outer_mm 重新呼叫 casegraph_fix_plan 取得自適應尺寸。')
            else:
                notes.append('案例快照沒有可用的管線外框，無法推算餘量；暫用案例尺寸 {} × {} mm。'.format(length, width))
            call = dict(tool='create_floor_opening', arguments=dict(
                elementId=element_id, mepId=mep_id, widthMm=width, lengthMm=length,
                rotationDeg=action.get('rotation_deg') or 0, offsetXMm=offset['x'], offsetYMm=offset['y'], expectedGeometry=None))
            notes.append('沿用案例做法（樓板開孔包覆管線），尺寸按本次管線自適應，不改用其他方法。以相同參數（不含 expectedGeometry）'
                         '呼叫 preview_floor_opening：ready 才帶入 expectedGeometry；blocked 表示開孔壓到上下層的牆／柱／梁，'
                         '改用回傳 fit 的全部參數再預覽，fit 為 null 則停止並說明；too_small 依 mepOuterDiameterMm 重新定尺寸。')
            if action.get('derived'):
                notes.append('舊案例只記錄面積／周長，尺寸由此推算為矩形；長邊方向未記錄，預設沿 X 軸，開孔中心對準本次管線。')
            expect = dict(opening_mm=dict(length=length, width=width), adaptive=adaptive)
        else:  # delete
            call = dict(tool='delete_element', arguments=dict(elementId=element_id))
            notes.append('刪除無法由此計畫復原，只能用 Revit 復原。')
            expect = None
        anchors = [a for a in step.get('anchors') or [] if a.get('case_box_mm')]
        if op in ('set_parameter', 'change_type') and anchors:
            a = anchors[0]
            notes.append('數值依案例圖關係自適應：比對案例值與 {}（{}，ID {}）的尺寸／位置，在目前對應元件 {} 上維持相同關係'
                         '（例如同等餘量、對齊或尺寸配合）；看不出關係時才沿用案例值，並在 warnings 說明。'.format(
                             a.get('name'), '、'.join(a.get('relations') or []), a.get('element_id'), current_id(a) or '（未對應）'))
        if any(v is None for v in call['arguments'].values()):
            call['ready'] = False
        steps.append(dict(step=index, role=step['role'], relations=step['relations'], label=step['label'],
                          call=call, expect_before=expect, unresolved=unresolved, notes=notes,
                          source_element=dict(element_id=step['element_id'], unique_id=step.get('unique_id'),
                                              name=step.get('name'), category=step.get('category')),
                          expect_after=(dict(position_mm=after, basis='bounding_box_center') if after else
                                        dict(position_mm=step.get('after_position_mm'), basis=step.get('position_basis'))) if op == 'move' else None,
                          anchors=[dict(a, current_id=current_id(a)) for a in step.get('anchors') or []],
                          already_satisfied=already, requires_confirmation=True, ready=not unresolved and call.get('ready', True)))
    return dict(case_id=case_id, title=plan['title'], verified=plan['verified'], solution=plan['solution'],
                signature=sig, steps=steps, manual=plan['manual'], observations=plan['observations'], warnings=warnings,
                ready=bool(steps) and all(s['ready'] for s in steps),
                instructions=['先以 get_element_info 確認目標元素的現值與 expect_before 相符。',
                              '把每個步驟（含 notes）列給使用者，取得確認後才呼叫 Revit MCP 工具。',
                              '套用後重新檢查問題是否消失，再以 casegraph_record_application 記錄結果。'])


def record_application(case_id, application: Application):
    with connection() as db:
        load_case(db, case_id)
        cursor = db.execute('INSERT INTO graph_applications(case_id,document,element_ids,outcome,steps,notes,resolved) VALUES (?,?,?,?,?,?,?)',
                            (case_id, application.document, json.dumps(application.element_ids), application.outcome,
                             json.dumps(application.steps, ensure_ascii=False), application.notes,
                             None if application.resolved is None else int(application.resolved)))
    return dict(id=cursor.lastrowid, case_id=case_id, outcome=application.outcome)


def applications(case_id):
    with connection() as db:
        rows = db.execute('SELECT * FROM graph_applications WHERE case_id=? ORDER BY id', (case_id,)).fetchall()
    return [{**dict(r), 'element_ids': json.loads(r['element_ids']), 'steps': json.loads(r['steps']),
             'resolved': None if r['resolved'] is None else bool(r['resolved'])} for r in rows]
