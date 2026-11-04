# -*- coding: utf-8 -*-
"""Lightweight same-category candidates, read only inside a Revit API context."""
from Autodesk.Revit import DB
from casegraph.extractor import document_id, id_value, parameter


def name_of(element):
    if element is None:
        return None
    try:
        return DB.Element.Name.GetValue(element)
    except Exception:
        return id_value(element.Id)


def dimensions(element, typ):
    result = {}
    for key, names in [('Width', ['FAMILY_WIDTH_PARAM','DOOR_WIDTH','WINDOW_WIDTH']),
                       ('Height', ['FAMILY_HEIGHT_PARAM','DOOR_HEIGHT','WINDOW_HEIGHT']),
                       ('Length', ['CURVE_ELEM_LENGTH']), ('Thickness', ['WALL_ATTR_WIDTH_PARAM'])]:
        for owner in (element,typ):
            if owner is None:
                continue
            for builtin in names:
                try:
                    p = owner.get_Parameter(getattr(DB.BuiltInParameter,builtin))
                    if p and p.HasValue and p.StorageType == DB.StorageType.Double:
                        result[key] = round(p.AsDouble()*304.8,3)
                        break
                except Exception:
                    pass
            if key in result:
                break
            p = owner.LookupParameter(key)
            if p and p.HasValue and p.StorageType == DB.StorageType.Double:
                try:
                    result[key] = round(p.AsDouble()*304.8,3)
                    break
                except Exception:
                    pass
    return result


def snapshot(doc, element, type_cache=None, doc_id=None):
    cache = type_cache if type_cache is not None else {}
    type_id = id_value(element.GetTypeId())
    if type_id not in cache:
        typ = doc.GetElement(element.GetTypeId())
        cache[type_id] = (typ, getattr(typ,'FamilyName',None) if typ else None, name_of(typ))
    typ, family, type_name = cache[type_id]
    identity = doc_id or document_id(doc)
    level = doc.GetElement(element.LevelId)
    owner_view = doc.GetElement(element.OwnerViewId) if element.ViewSpecific else None
    params = {}
    for label,builtin in [('Fire Rating','FIRE_RATING'),('Structural','WALL_STRUCTURAL_SIGNIFICANT')]:
        for owner in (element,typ):
            if owner:
                try:
                    p = owner.get_Parameter(getattr(DB.BuiltInParameter,builtin))
                    if p and p.HasValue:
                        value = p.AsValueString() or p.AsString()
                        if value:
                            params[label] = value
                            break
                except Exception:
                    pass
    return dict(node_id=identity+':'+element.UniqueId, node_type='ELEMENT', name=name_of(element),
        source_document=identity,source_element_id=element.UniqueId,
        properties=dict(element_id=id_value(element.Id),category=element.Category.Name if element.Category else None,
            category_id=id_value(element.Category.Id) if element.Category else None,
            family=family,type_name=type_name,type_id=type_id,level=name_of(level),
            document_title=doc.Title,owner_view_id=id_value(element.OwnerViewId) if owner_view else None,
            owner_view_name=name_of(owner_view),dimensions=dimensions(element,typ),units='mm',
            search_parameters=params,ifc_class=parameter(element,'IfcExportAs') or parameter(typ,'IfcExportAs')))


def discover(uidoc, target, scope='model', limit=500):
    doc = uidoc.Document
    if not target.Category:
        raise ValueError(u'此元素沒有類別，請改選模型或詳圖中的實例。')
    if scope == 'view' and not DB.FilteredElementCollector.IsViewValidForElementIteration(doc,uidoc.ActiveView.Id):
        raise ValueError(u'目前視圖不支援元素搜尋，請改用「目前模型」。')
    cache, candidates, seen = {}, [], set()
    identity = document_id(doc)
    def collector():
        c = DB.FilteredElementCollector(doc,uidoc.ActiveView.Id) if scope == 'view' else DB.FilteredElementCollector(doc)
        return c.OfCategoryId(target.Category.Id).WhereElementIsNotElementType()
    # Give instances of the exact type first access to the bounded candidate budget.
    # The second pass excludes that type so even a huge same-type set is read once.
    type_id = target.GetTypeId()
    filters = [None]
    if type_id != DB.ElementId.InvalidElementId:
        rule = DB.ParameterFilterRuleFactory.CreateEqualsRule(DB.ElementId(DB.BuiltInParameter.ELEM_TYPE_PARAM),type_id)
        filters = [DB.ElementParameterFilter(rule),DB.ElementParameterFilter(rule,True)]
    truncated = False
    skipped = 0
    for filter_ in filters:
        c = collector()
        if filter_:
            c = c.WherePasses(filter_)
        try:
            for candidate in c:
                if candidate.Id == target.Id:
                    continue
                if len(candidates) >= limit:
                    truncated = True
                    break
                try:
                    record = snapshot(doc,candidate,cache,identity)
                except Exception:
                    skipped += 1
                    if skipped >= 20:
                        truncated = True
                        break
                    continue
                if record['source_element_id'] not in seen:
                    seen.add(record['source_element_id'])
                    candidates.append(record)
        finally:
            c.Dispose()
        if truncated:
            break
    return dict(target=snapshot(doc,target,cache,identity),candidates=candidates,truncated=truncated,skipped=skipped)


def detail_snapshot(doc, element):
    """Read a single selected instance for the inspector/comparison, in API context."""
    from casegraph.extractor import node
    record = node(doc, element)
    light = snapshot(doc, element)
    dims = dict(record['properties'].get('dimensions') or {})
    dims.update(light['properties'].get('dimensions') or {})
    record['properties'].update(light['properties'])
    record['properties']['dimensions'] = dims
    try:
        host = getattr(element, 'Host', None)
        record['properties']['host_name'] = name_of(host) if host else None
    except Exception:
        pass
    try:
        record['properties']['materials'] = sorted(set(name_of(doc.GetElement(i)) for i in list(element.GetMaterialIds(False))[:10]))
    except Exception:
        pass
    return record
