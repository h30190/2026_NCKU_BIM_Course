# -*- coding: utf-8 -*-
"""Bounded, on-demand extraction. Call only inside an ExternalEvent."""
from Autodesk.Revit import DB
import hashlib


def document_id(doc):
    # Fingerprint survives sessions; path distinguishes independently saved copies.
    try:
        identity = str(doc.GetFingerprintGUID())
    except Exception:
        identity = doc.ProjectInformation.UniqueId
    path = doc.PathName.lower()
    return identity + ':' + hashlib.sha256(path.encode('utf-8')).hexdigest()[:16]


def id_value(element_id):
    try:
        return str(element_id.Value)
    except Exception:
        return str(element_id.IntegerValue)


def parameter(element, name):
    p = element.LookupParameter(name) if element else None
    if p is None or not p.HasValue:
        return None
    try:
        return p.AsString() or p.AsValueString()
    except Exception:
        return None


def raw_parameter(p):
    """Internal value and identity needed to set the parameter again (e.g. through Revit MCP).

    Doubles are in Revit internal units (feet for lengths), exactly what Parameter.Set expects.
    """
    storage = str(p.StorageType)
    if storage == 'Double':
        raw = p.AsDouble()
    elif storage == 'Integer':
        raw = p.AsInteger()
    elif storage == 'ElementId':
        raw = id_value(p.AsElementId())
    else:
        raw = p.AsString()
    info = dict(storage=storage, raw=raw, read_only=bool(p.IsReadOnly))
    try:
        builtin = p.Definition.BuiltInParameter
        if builtin != DB.BuiltInParameter.INVALID:
            info['builtin'] = str(builtin)
    except Exception:
        pass  # Shared and project parameters have no BuiltInParameter.
    try:
        if p.IsShared:
            info['guid'] = str(p.GUID)
    except Exception:
        pass
    return info


def xyz(point):
    return dict(x=point.X * 304.8, y=point.Y * 304.8, z=point.Z * 304.8)


def floor_openings(doc, floor):
    """Holes in a floor: inner sketch loops plus hosted Opening elements, as rectangles where possible."""
    from casegraph.model_changes import opening_shape
    holes = []
    try:
        loops = [list(loop) for loop in doc.GetElement(floor.SketchId).Profile]
    except Exception:
        loops = []  # Revit 2021 and older floors have no sketch access.
    def vertices(curves):
        return [xyz(c.GetEndPoint(0)) for c in curves]
    def extent(curves):
        pts = vertices(curves)
        return (max(p['x'] for p in pts)-min(p['x'] for p in pts))*(max(p['y'] for p in pts)-min(p['y'] for p in pts)) if pts else 0
    if len(loops) > 1:
        outer = max(loops, key=extent)
        for loop in loops:
            if loop is not outer:
                holes.append((u'sketch', vertices(loop)))
    try:
        for opening in DB.FilteredElementCollector(doc).OfClass(DB.Opening):
            if opening.Host is not None and opening.Host.Id == floor.Id:
                holes.append((u'opening', vertices(list(opening.BoundaryCurves))))
    except Exception:
        pass
    found = []
    for source, points in holes:
        shape = opening_shape(points)
        if shape:
            shape['source'] = source
            found.append(shape)
    return sorted(found, key=lambda h: (h['center_mm']['x'], h['center_mm']['y']))


def node(doc, element):
    typ = doc.GetElement(element.GetTypeId())
    try:
        name = DB.Element.Name.GetValue(element)
    except Exception:
        name = id_value(element.Id)
    properties = dict(element_id=id_value(element.Id), category=element.Category.Name if element.Category else None,
        category_id=id_value(element.Category.Id) if element.Category else None, document_title=doc.Title,
        family=getattr(typ, 'FamilyName', None), type_name=parameter(typ, 'Type Name'), type_id=id_value(element.GetTypeId()),
        ifc_class=parameter(element, 'IfcExportAs') or parameter(typ, 'IfcExportAs'),
        ifc_global_id=parameter(element, 'IfcGUID'), dimensions={}, parameters={}, units='mm')
    if typ:
        try:
            properties['type_name'] = DB.Element.Name.GetValue(typ)
        except Exception:
            pass
    for source, owner in [('instance', element), ('type', typ)]:
        if owner is None:
            continue
        for p in list(owner.Parameters)[:80]:
            try:
                if p.HasValue:
                    entry = dict(value=p.AsValueString() or p.AsString() or str(p.AsInteger()),
                        source_id=id_value(p.Id), unit=str(p.GetUnitTypeId()) if p.StorageType == DB.StorageType.Double else None)
                    entry.update(raw_parameter(p))
                    properties['parameters'][source + ':' + p.Definition.Name] = entry
            except Exception:
                continue
    for key in ['Width', 'Height', 'Length', 'Thickness']:
        for owner in [element, typ]:
            p = owner.LookupParameter(key) if owner else None
            if p and p.HasValue:
                try:
                    properties['dimensions'][key] = p.AsDouble() * 304.8
                    break
                except Exception:
                    pass
    box = element.get_BoundingBox(None)
    if box:
        corners = [box.Transform.OfPoint(DB.XYZ(x,y,z)) for x in (box.Min.X, box.Max.X)
            for y in (box.Min.Y, box.Max.Y) for z in (box.Min.Z, box.Max.Z)]
        properties['bounding_box'] = dict(min=dict(x=min(p.X for p in corners)*304.8,y=min(p.Y for p in corners)*304.8,z=min(p.Z for p in corners)*304.8),
            max=dict(x=max(p.X for p in corners)*304.8,y=max(p.Y for p in corners)*304.8,z=max(p.Z for p in corners)*304.8), units='mm')
    floor_class = getattr(DB, 'Floor', None)
    if floor_class is not None and isinstance(element, floor_class):
        try:
            properties['openings'] = floor_openings(doc, element)
        except Exception:
            pass  # Missing hole data is never reported as a change.
    for attr in ['FacingOrientation', 'HandOrientation']:
        try:
            direction = getattr(element, attr, None)
            if direction:
                properties[attr] = dict(x=direction.X, y=direction.Y, z=direction.Z)
        except Exception:
            pass  # Detail components and unsupported categories may reject orientation access.
    location = getattr(getattr(element, 'Location', None), 'Point', None)
    if location:
        properties['location'] = xyz(location)
    kind = 'LEVEL' if isinstance(element, DB.Level) else 'SPACE' if isinstance(element, DB.SpatialElement) else 'MATERIAL' if isinstance(element, DB.Material) else 'SYSTEM' if isinstance(element, DB.MEPSystem) else 'ELEMENT'
    return dict(node_id=document_id(doc) + ':' + element.UniqueId, node_type=kind, name=name,
                properties=properties, source_document=document_id(doc), source_element_id=element.UniqueId)


class Context(object):
    def __init__(self, doc, primary, limit):
        self.doc, self.primary, self.limit = doc, primary, limit
        root = node(doc, primary)
        # Rooms/MEP spaces are persistent Revit elements when they are the case
        # target. SPACE is a context role for surrounding rooms, not a root type.
        # Match element_index.snapshot and retain the spatial classification.
        if root['node_type'] == 'SPACE':
            root['properties']['revit_node_type'] = 'SPACE'
            root['node_type'] = 'ELEMENT'
        self.data = dict(root=root['node_id'], nodes=[root], edges=[], warnings=[], model_version=str(DB.Document.GetDocumentVersion(doc).VersionGUID))
        self.ids = {root['node_id']}

    def add(self, source, target, relation, method, verified=True, properties=None):
        if target is None or target.Id == source.Id:
            return
        target_node = node(self.doc, target)
        if target_node['node_id'] not in self.ids:
            if len(self.ids) >= self.limit:
                message = u'關係節點已達上限（{} 個），其餘關係未列入。'.format(self.limit)
                if message not in self.data['warnings']:
                    self.data['warnings'].append(message)
                return
            self.ids.add(target_node['node_id'])
            self.data['nodes'].append(target_node)
        self.data['edges'].append(dict(source=document_id(self.doc)+':'+source.UniqueId,
            target=target_node['node_id'], relationship_type=relation, properties=dict(properties or {}),
            evidence=method, validation_status='verified' if verified else 'candidate'))


def explicit(context, element):
    doc = context.doc
    context.add(element, doc.GetElement(element.LevelId), 'LOCATED_ON_LEVEL', 'Revit Element.LevelId')
    context.add(element, getattr(element, 'Host', None), 'HOSTED_BY', 'Revit FamilyInstance.Host')
    for attr, relation in [('FromRoom', 'ADJACENT_TO'), ('ToRoom', 'ADJACENT_TO'), ('Room', 'CONTAINED_IN'), ('Space', 'CONTAINED_IN')]:
        try:
            room = getattr(element, attr, None)
            if not isinstance(room, DB.SpatialElement):
                getter = getattr(element, 'get_' + attr, None)
                if getter:
                    room = getter(doc.Phases.get_Item(doc.Phases.Size - 1))
            if isinstance(room, DB.SpatialElement):
                # Doors connect rooms; do not claim exclusive containment.
                context.add(element, room, 'ADJACENT_TO' if isinstance(element, DB.FamilyInstance) and element.Category.Id == DB.ElementId(DB.BuiltInCategory.OST_Doors) else relation, 'Revit ' + attr + ' (current phase)')
        except Exception:
            continue
    try:
        materials = list(element.GetMaterialIds(False))[:10]
    except Exception:
        materials = []
    for material_id in materials:
        context.add(element, doc.GetElement(material_id), 'HAS_MATERIAL', 'Revit GetMaterialIds')


def extract(doc, primary, issue_type, related=None, depth=1, limit=80, findings=None):
    """findings: (element, evidence, verified, properties) from a model-check result."""
    from casegraph.strategies import strategy_for
    context = Context(doc, primary, min(limit, 200))
    explicit(context, primary)
    for element, evidence, verified, properties in (findings or [])[:20]:
        context.add(primary, element, 'RELATED_TO', evidence, verified, properties)
    for element in (related or [])[:20]:
        context.add(primary, element, 'RELATED_TO', 'User selected related element')
    strategy_for(issue_type).extract(context)
    if depth == 2:
        for item in list(context.data['nodes'])[1:]:
            element = doc.GetElement(item['source_element_id'])
            if element:
                explicit(context, element)
    return context.data
