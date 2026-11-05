# -*- coding: utf-8 -*-
"""Suggestion reads and verification; call only inside a Revit ExternalEvent."""
from copy import deepcopy
from System import Int64
from Autodesk.Revit import DB
from casegraph.extractor import id_value
from casegraph.element_index import discover, name_of


def element_id(number):
    try:
        return DB.ElementId(Int64(number))
    except Exception:
        return DB.ElementId(int(number))


def other_same_type(uidoc, records, limit=20):
    """Bounded, read-only candidates; never add them to the repair scope."""
    selected = set(r['source_element_id'] for r in records)
    candidates, seen, types = [], set(), set()
    for record in records:
        type_id = record['properties'].get('type_id')
        if not type_id or type_id == '-1' or type_id in types:
            continue
        types.add(type_id)
        element = uidoc.Document.GetElement(record['source_element_id'])
        if element is None:
            continue
        result = discover(uidoc, element, limit=limit+len(records))
        for candidate in result['candidates']:
            key = candidate['source_element_id']
            if (key in selected or key in seen or candidate['source_document'] != record['source_document'] or
                    candidate['properties'].get('type_id') != type_id):
                continue
            seen.add(key)
            candidates.append(candidate)
            if len(candidates) == limit:
                return candidates
    return candidates


def inspect_type_changes(uidoc, steps):
    """Capture exact type identities before writes, including no-op assignments."""
    states = {}
    for index, step in enumerate(steps, 1):
        if step['tool'] != 'change_element_type':
            continue
        args = step['arguments']
        element = uidoc.Document.GetElement(element_id(args['elementId']))
        desired = uidoc.Document.GetElement(element_id(args['typeId']))
        if element is None or isinstance(element, DB.ElementType):
            raise ValueError(u'找不到要變更類型的模型實例，請重新選取。')
        if not isinstance(desired, DB.ElementType) or not element.IsValidType(desired.Id):
            raise ValueError(u'元素 {} 不能使用 typeId {}，請重新產生建議。'.format(args['elementId'], args['typeId']))
        current = element.GetTypeId()
        states[index] = dict(unique_id=element.UniqueId, before_id=id_value(current),
                             before_name=name_of(uidoc.Document.GetElement(current)),
                             desired_id=id_value(desired.Id), desired_name=name_of(desired))
    return states


def verify_type_changes(uidoc, report, states):
    """Do not report an assignment as applied until Revit exposes the exact type ID."""
    report = deepcopy(report)
    for result in report.get('results') or []:
        state = states.get(result.get('step'))
        if state is None or not result.get('ok'):
            continue
        element = uidoc.Document.GetElement(state['unique_id'])
        actual = id_value(element.GetTypeId()) if element is not None else None
        result.update(before_type_id=state['before_id'], after_type_id=actual)
        if actual != state['desired_id']:
            result.update(ok=False, verified=False,
                          message=u'類型核對未通過：預期 typeId {}，實際為 {}。請檢查模型後重新規劃。'.format(
                              state['desired_id'], actual or u'元素不存在'))
        elif actual == state['before_id']:
            result.update(verified=True, changed=False,
                          message=u'目前已是「{}」（typeId {}），本次沒有變更類型。'.format(state['desired_name'], actual))
        else:
            result.update(verified=True, changed=True,
                          message=u'已核對類型：{} → {}（typeId {}）。'.format(
                              state['before_name'] or state['before_id'], state['desired_name'], actual))
    return report
