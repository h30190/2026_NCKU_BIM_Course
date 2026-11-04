# -*- coding: utf-8 -*-
"""Issue-specific selection of bounded extraction strategies."""
from Autodesk.Revit import DB

NEARBY_LIMIT = 20  # Nearest non-intersecting neighbours kept as NEARBY candidates.


class PropertiesStrategy(object):
    def extract(self, context):
        pass  # Base node already contains instance/type parameters with provenance.


def note(context, text):
    if text not in context.data['warnings']:
        context.data['warnings'].append(text)


def model_element(element, primary):
    """Physical model elements only: no annotations, view-specific items, links or the element itself."""
    category = element.Category
    return (element.Id != primary.Id and category is not None and category.CategoryType == DB.CategoryType.Model
            and not element.ViewSpecific and not isinstance(element, DB.RevitLinkInstance))


def centre(element):
    box = element.get_BoundingBox(None)
    if box is None:
        return None
    return ((box.Min.X+box.Max.X)/2., (box.Min.Y+box.Max.Y)/2., (box.Min.Z+box.Max.Z)/2.)


class SpatialStrategy(object):
    def __init__(self, verify=False):
        self.verify = verify

    def extract(self, context):
        box = context.primary.get_BoundingBox(None)
        if not box:
            note(context, u'此元件沒有外框，未擷取鄰近元件。')
            return
        # Expanded model-coordinate outline is only a candidate discovery filter.
        bounds = context.data['nodes'][0]['properties']['bounding_box']
        lo, hi = bounds['min'], bounds['max']
        pad = 1000.0
        outline = DB.Outline(DB.XYZ((lo['x']-pad)/304.8,(lo['y']-pad)/304.8,(lo['z']-pad)/304.8),
            DB.XYZ((hi['x']+pad)/304.8,(hi['y']+pad)/304.8,(hi['z']+pad)/304.8))

        def collector():
            return DB.FilteredElementCollector(context.doc).WhereElementIsNotElementType().WherePasses(DB.BoundingBoxIntersectsFilter(outline))
        intersecting = set()
        if self.verify:
            # Verified intersections first, so the node budget can never drop them for neighbours.
            try:
                for candidate in collector().WherePasses(DB.ElementIntersectsElementFilter(context.primary)):
                    if model_element(candidate, context.primary):
                        intersecting.add(candidate.Id)
                        context.add(context.primary, candidate, 'INTERSECTS', 'Revit ElementIntersectsElementFilter', True)
            except Exception:
                note(context, u'部分元件無法判定是否相交，已改列為鄰近元件。')
        origin = centre(context.primary)
        nearby = [e for e in collector() if model_element(e, context.primary) and e.Id not in intersecting]

        def distance(element):
            point = centre(element)
            return sum((a-b)**2 for a, b in zip(point, origin)) if point and origin else float('inf')
        nearby.sort(key=distance)
        for candidate in nearby[:NEARBY_LIMIT]:
            context.add(context.primary, candidate, 'NEARBY', 'Expanded bounding-box candidate within 1000 mm; clearance not verified', False)
        if len(nearby) > NEARBY_LIMIT:
            note(context, u'1000 mm 範圍內另有 {} 個元件，只列入最近的 {} 個。'.format(len(nearby)-NEARBY_LIMIT, NEARBY_LIMIT))
        if not self.verify:
            note(context, u'門扇開啟範圍與所需淨空需人工確認。')


class ConnectionStrategy(object):
    def extract(self, context):
        model = getattr(context.primary, 'MEPModel', None)
        manager = getattr(context.primary, 'ConnectorManager', None) or getattr(model, 'ConnectorManager', None)
        if manager is None:
            note(context, u'此元件沒有連接器，未擷取系統連接。')
            return
        for connector in list(manager.Connectors)[:30]:
            context.add(context.primary, getattr(connector, 'MEPSystem', None), 'BELONGS_TO_SYSTEM', 'Revit Connector.MEPSystem')
            for reference in list(connector.AllRefs)[:20]:
                if reference.Owner.Id != context.primary.Id:
                    try:
                        if connector.IsConnectedTo(reference):
                            context.add(context.primary, reference.Owner, 'CONNECTED_TO', 'Revit Connector.IsConnectedTo')
                    except Exception:
                        continue


def strategy_for(issue_type):
    return {'GEOMETRY_CLASH': SpatialStrategy(True), 'SPATIAL_CLEARANCE': SpatialStrategy(),
        'DESIGN_CONFLICT': SpatialStrategy(), 'CONNECTION_ERROR': ConnectionStrategy(),
        'CONSTRUCTION_ISSUE': ConnectionStrategy()}.get(issue_type, PropertiesStrategy())
