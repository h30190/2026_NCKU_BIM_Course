# -*- coding: utf-8 -*-
"""Compare a case's saved element snapshots with the live model.

Only fields present on both sides are compared; missing data is never reported
as a change. Elements are matched by Revit UniqueId in the source document,
then by IFC GlobalId (which also finds them in a copy saved under another path).
"""
import math

try:
    text_type = unicode
except NameError:
    text_type = str

DIMENSION_MM = .5
MOVE_MM = 1.
MAX_ELEMENTS, MAX_PARAMETERS = 40, 30
DIMENSIONS = dict(Width=u'寬度', Height=u'高度', Length=u'長度', Thickness=u'厚度')
# Covered by the type/dimension rows, or never meaningful as a fix.
SKIPPED_PARAMETERS = set(['Family and Type', 'Family', 'Type', 'Type Id', 'Type Name', 'Category',
                          'Width', 'Height', 'Length', 'Thickness'])
STATES = dict(CHANGED=u'已修改', UNCHANGED=u'未變更', DELETED=u'已刪除', UNAVAILABLE=u'未開啟來源模型')


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and not (math.isnan(value) or math.isinf(value))


def point(value):
    if isinstance(value, dict) and all(number(value.get(k)) for k in 'xyz'):
        return tuple(float(value[k]) for k in 'xyz')
    return None


def distance(a, b):
    return math.sqrt(sum((x-y)**2 for x, y in zip(a, b)))


def mm(value):
    return u'{:g} mm'.format(round(value, 1))


def opening_shape(vertices):
    """Describe one hole loop from its vertices (mm dicts x/y/z): a rectangle gets length/width/rotation."""
    pts = [(float(v['x']), float(v['y']), float(v['z'])) for v in vertices]
    if len(pts) < 3:
        return None
    centre = dict(x=round(sum(p[0] for p in pts)/len(pts), 1), y=round(sum(p[1] for p in pts)/len(pts), 1),
                  z=round(sum(p[2] for p in pts)/len(pts), 1))
    edges = [(pts[(i+1) % len(pts)][0]-pts[i][0], pts[(i+1) % len(pts)][1]-pts[i][1]) for i in range(len(pts))]
    sizes = [math.hypot(*e) for e in edges]
    if len(pts) == 4 and min(sizes) > 0 and all(abs(edges[i][0]*edges[(i+1) % 4][0] + edges[i][1]*edges[(i+1) % 4][1])
                                                < 1e-3*sizes[i]*sizes[(i+1) % 4] for i in range(4)):
        long_edge = edges[0] if sizes[0] >= sizes[1] else edges[1]
        angle = math.degrees(math.atan2(long_edge[1], long_edge[0])) % 180
        return dict(shape='rectangle', length_mm=round(max(sizes), 1), width_mm=round(min(sizes), 1),
                    rotation_deg=round(0. if abs(angle-180) < 1e-6 else angle, 3), center_mm=centre)
    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
    return dict(shape='polygon', length_mm=round(max(xs)-min(xs), 1), width_mm=round(max(ys)-min(ys), 1),
                rotation_deg=0., center_mm=centre)


def same_opening(a, b):
    return all(abs(float(a['center_mm'][k]) - float(b['center_mm'][k])) <= MOVE_MM for k in ('x', 'y')) and         all(abs(float(a[k]) - float(b[k])) <= MOVE_MM for k in ('length_mm', 'width_mm'))


def caption(p):
    return u'／'.join(text_type(p[k]) for k in ('family', 'type_name') if p.get(k)) or text_type(p.get('type_id') or u'')


def parameter_action(key, old, new):
    """A replayable set_parameter step; raw values are Revit internal units when recorded."""
    scope, _, name = key.partition(':')
    old = old if isinstance(old, dict) else dict(value=old)
    new = new if isinstance(new, dict) else dict(value=new)
    action = dict(op='set_parameter', scope=scope if name else 'instance', name=name or scope,
                  before=old.get('value'), after=new.get('value'), unit=new.get('unit'))
    for field in ('storage', 'builtin', 'guid', 'read_only'):
        if new.get(field) is not None:
            action[field] = new[field]
    if 'raw' in new:
        action['after_raw'] = new['raw']
    if 'raw' in old:
        action['before_raw'] = old['raw']
    return action


def element_changes(before, after):
    """Rows of dict(field, before, after, action) describing what changed on one element.

    `action` is a replayable step (set_parameter, change_type, move, flip, rotate) or
    None when the change is informational only.
    """
    b, a = before.get('properties') or {}, after.get('properties') or {}
    rows = []

    def add(field, old, new, action=None):
        rows.append(dict(field=field, before=text_type(old), after=text_type(new), action=action))

    type_action = dict(op='change_type', from_type_id=text_type(b.get('type_id') or u''), to_type_id=text_type(a.get('type_id') or u''),
                       family=a.get('family'), type_name=a.get('type_name'), category=a.get('category'))
    if b.get('type_id') and a.get('type_id') and b.get('type_id') not in ('-1', '0'):
        if text_type(b['type_id']) != text_type(a['type_id']):
            add(u'類型', caption(b), caption(a), type_action)
    elif b.get('type_name') and a.get('type_name') and caption(b) != caption(a):
        add(u'類型', caption(b), caption(a), type_action)
    bd, ad = b.get('dimensions') or {}, a.get('dimensions') or {}
    bp, ap = b.get('parameters') or {}, a.get('parameters') or {}
    for key in sorted(set(bd) & set(ad)):
        if number(bd[key]) and number(ad[key]) and abs(bd[key]-ad[key]) > DIMENSION_MM:
            # Dimensions are read from the same-named instance, else type, parameter.
            source = next((k for k in ('instance:'+key, 'type:'+key) if k in ap), None)
            action = parameter_action(source, bp.get(source) or {}, ap[source]) if source else None
            if action is not None:
                action['after_mm'] = ad[key]
            add(DIMENSIONS.get(key, key), mm(bd[key]), mm(ad[key]), action)
    old, new = point(b.get('location')), point(a.get('location'))
    label = u'位置'
    if old is None or new is None:
        # Walls and other curve-based elements: compare bounding-box centres instead.
        boxes = [p.get('bounding_box') or {} for p in (b, a)]
        centres = [tuple((x+y)/2. for x, y in zip(point(box.get('min')), point(box.get('max'))))
                   if point(box.get('min')) and point(box.get('max')) else None for box in boxes]
        old, new, label = centres[0], centres[1], u'位置（外框中心）'
    if old and new and distance(old, new) > MOVE_MM:
        delta = [round(y-x, 1) for x, y in zip(old, new)]
        add(label, u'移動 {}'.format(mm(distance(old, new))), u'X {:+.0f}／Y {:+.0f}／Z {:+.0f} mm'.format(*delta),
            dict(op='move', dx=delta[0], dy=delta[1], dz=delta[2],
                 position_basis='bounding_box_center' if u'外框中心' in label else 'location',
                 before_position_mm=dict(zip(('x', 'y', 'z'), old)), after_position_mm=dict(zip(('x', 'y', 'z'), new))))
    for key, name, flip in (('FacingOrientation', u'朝向', 'facing'), ('HandOrientation', u'開向', 'hand')):
        old, new = point(b.get(key)), point(a.get(key))
        if old and new:
            dot = sum(x*y for x, y in zip(old, new))
            if dot < .9999:
                # A reversed vector is a flip; any other turn is a rotation without a replay tool.
                add(name, u'({:.2f}, {:.2f}, {:.2f})'.format(*old), u'({:.2f}, {:.2f}, {:.2f})'.format(*new),
                    dict(op='flip', flip=flip) if dot < -.9999 else dict(op='rotate', axis=flip, before=list(old), after=list(new)))
    before_holes, after_holes = b.get('openings'), a.get('openings')
    if isinstance(before_holes, list) and isinstance(after_holes, list):
        for hole in after_holes:
            if not any(same_opening(hole, old) for old in before_holes):
                # Only rectangles have a replay tool; other shapes stay a manual record.
                action = dict(op='floor_opening', length_mm=hole['length_mm'], width_mm=hole['width_mm'],
                              rotation_deg=hole.get('rotation_deg') or 0, center_mm=hole['center_mm'],
                              source=hole.get('source')) if hole.get('shape') == 'rectangle' else None
                add(u'樓板開孔', u'（無）', u'{:g} × {:g} mm{}'.format(hole['length_mm'], hole['width_mm'],
                    u'' if action else u'（非矩形）'), action)
        for hole in before_holes:
            if not any(same_opening(hole, new) for new in after_holes):
                add(u'樓板開孔', u'{:g} × {:g} mm'.format(hole['length_mm'], hole['width_mm']), u'已移除')
    if b.get('ifc_class') and a.get('ifc_class') and b['ifc_class'] != a['ifc_class']:
        source = next((k for k in ('instance:IfcExportAs', 'type:IfcExportAs') if k in ap), 'instance:IfcExportAs')
        add(u'IFC 類別', b['ifc_class'], a['ifc_class'],
            parameter_action(source, bp.get(source) or b['ifc_class'], ap.get(source) or a['ifc_class']))
    if b.get('ifc_global_id') and a.get('ifc_global_id') and b['ifc_global_id'] != a['ifc_global_id']:
        add(u'IFC GlobalId', b['ifc_global_id'], a['ifc_global_id'])
    changed = 0
    for key in sorted(set(bp) & set(ap)):
        if key.split(':', 1)[-1] in SKIPPED_PARAMETERS:
            continue
        old = (bp[key] or {}).get('value') if isinstance(bp[key], dict) else bp[key]
        new = (ap[key] or {}).get('value') if isinstance(ap[key], dict) else ap[key]
        if old != new:
            changed += 1
            if changed <= MAX_PARAMETERS:
                source, _, name = key.partition(':')
                add(u'{} · {}'.format(u'類型參數' if source == 'type' else u'參數', name or source),
                    old if old not in (None, '') else u'（空）', new if new not in (None, '') else u'（空）',
                    parameter_action(key, bp[key], ap[key]))
    if changed > MAX_PARAMETERS:
        add(u'其他參數', u'', u'另有 {} 項參數變更'.format(changed-MAX_PARAMETERS))
    return rows


def recorded_elements(case):
    """Root first, then other model elements in the saved snapshot."""
    graph = case['draft']['graph']
    nodes = [n for n in graph['nodes'] if n['node_type'] == 'ELEMENT' and n.get('source_element_id')]
    nodes.sort(key=lambda n: n['node_id'] != graph['root'])
    return nodes[:MAX_ELEMENTS]


def relations(graph, node_id):
    """How an element relates to the case root, e.g. ['HOSTED_BY'] for a door's wall."""
    if node_id == graph['root']:
        return []
    found = set()
    for edge in graph['edges']:
        if edge['source'] == graph['root'] and edge['target'] == node_id:
            found.add(edge['relationship_type'])
        elif edge['target'] == graph['root'] and edge['source'] == node_id:
            found.add(edge['relationship_type']+u':REVERSE')
    return sorted(found)


def change_report(case, document, checked_at, find, read):
    """find(record) -> (element, match basis) or (None, None); read(element) -> live snapshot."""
    elements = []
    graph = case['draft']['graph']
    for record in recorded_elements(case):
        p = record['properties']
        row = dict(node_id=record['node_id'], name=record['name'], element_id=text_type(p.get('element_id') or u''),
                   unique_id=record['source_element_id'], ifc_global_id=p.get('ifc_global_id'),
                   root=record['node_id'] == graph['root'], match=u'', changes=[],
                   category=p.get('category'), family=p.get('family'), type_name=p.get('type_name'),
                   type_id=text_type(p.get('type_id') or u'') or None, relations=relations(graph, record['node_id']))
        element, match = find(record)
        if element is None:
            row['state'] = 'DELETED' if record['source_document'] == document else 'UNAVAILABLE'
        else:
            row['changes'] = element_changes(record, read(element))
            row.update(match=match, state='CHANGED' if row['changes'] else 'UNCHANGED')
        elements.append(row)
    return dict(schema_version=1, case_id=case['id'], revision=case['revision'], document=document,
                checked_at=checked_at, elements=elements)


def summary(report):
    counts = dict((state, sum(e['state'] == state for e in report['elements'])) for state in STATES)
    parts = [u'{} 個{}'.format(counts[s], STATES[s]) for s in ('CHANGED', 'DELETED', 'UNCHANGED', 'UNAVAILABLE') if counts[s]]
    return u'、'.join(parts) or u'沒有已記錄的模型元件'


def solution_text(report):
    """A plain-text change record to append to the solution."""
    lines = [u'模型變更紀錄（{}；對比 RFI-{} 第 {} 版快照）'.format(report['checked_at'], report['case_id'], report['revision'])]
    for element in report['elements']:
        if element['state'] == 'UNCHANGED':
            continue
        head = u'・{}（ID {}）'.format(element['name'], element['element_id'] or u'—')
        if element['state'] != 'CHANGED':
            lines.append(head+u'：'+STATES[element['state']])
        else:
            lines.append(head+u'：'+u'；'.join(u'{} {} → {}'.format(c['field'], c['before'], c['after']).replace(u'  ', u' ')
                                              for c in element['changes']))
    if len(lines) == 1:
        lines.append(u'・已記錄元件與快照相比沒有變更。')
    return u'\n'.join(lines)


class RevitLocator(object):
    """Find recorded elements in the active document. Requires Revit API context."""
    def __init__(self, doc):
        from Autodesk.Revit import DB
        from casegraph.extractor import document_id
        self.DB, self.doc, self.document = DB, doc, document_id(doc)

    def find(self, record):
        if record['source_document'] == self.document:
            element = self.doc.GetElement(record['source_element_id'])
            if element is not None:
                return element, u'Revit UniqueId'
        guid = (record.get('properties') or {}).get('ifc_global_id')
        if guid:
            element = self.by_ifc_guid(guid)
            if element is not None:
                return element, u'IFC GlobalId'
        return None, None

    def by_ifc_guid(self, guid):
        DB = self.DB
        parameter = DB.ElementId(DB.BuiltInParameter.IFC_GUID)
        try:
            rule = DB.ParameterFilterRuleFactory.CreateEqualsRule(parameter, guid)
        except Exception:  # Revit 2022 requires the case-sensitivity argument.
            rule = DB.ParameterFilterRuleFactory.CreateEqualsRule(parameter, guid, True)
        collector = DB.FilteredElementCollector(self.doc).WhereElementIsNotElementType().WherePasses(DB.ElementParameterFilter(rule))
        try:
            return collector.FirstElement()
        finally:
            collector.Dispose()

    def read(self, element):
        from casegraph.extractor import node
        return node(self.doc, element)
