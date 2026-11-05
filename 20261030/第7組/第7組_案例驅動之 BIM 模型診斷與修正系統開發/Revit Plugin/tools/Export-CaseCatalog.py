# -*- coding: utf-8 -*-
"""Export actual case roots and native evidence; no transactions or model saves."""
import os
import sys
import codecs
import traceback
from datetime import datetime
from Autodesk.Revit import DB

# Paths come from the environment, not from one machine:
#   CASEGRAPH_ROOT  - plugin folder (default: the folder that contains tools/)
#   CASEGRAPH_MODEL - the .rvt model to read (required when a model is opened)
try:
    _TOOLS = os.path.dirname(os.path.abspath(__file__))
except NameError:  # Some pyRevit hosts do not define __file__.
    _TOOLS = os.path.join(os.getcwd(), 'tools')
ROOT = os.environ.get('CASEGRAPH_ROOT') or os.path.dirname(_TOOLS)
MODEL = os.environ.get('CASEGRAPH_MODEL') or u''
OUTPUT = os.path.join(ROOT, 'backend', 'data', 'case-catalog-export.json')
STATUS = os.path.join(ROOT, 'backend', 'data', 'case-catalog-export-status.json')
sys.path.insert(0, os.path.join(ROOT, 'CaseBase.extension', 'lib'))
from casegraph.element_index import detail_snapshot, name_of
from casegraph.extractor import Context, document_id, explicit
from casegraph.serialization import dumps

CATEGORIES = ['OST_Doors', 'OST_Windows', 'OST_Walls', 'OST_Floors', 'OST_StructuralColumns',
              'OST_Columns', 'OST_DetailComponents', 'OST_Ceilings', 'OST_StructuralFraming',
              'OST_Furniture', 'OST_GenericModel', 'OST_PlumbingFixtures', 'OST_MechanicalEquipment']
PARAMETERS = [
    ('WALL_BASE_CONSTRAINT', u'牆底樓層', 'ref'), ('WALL_HEIGHT_TYPE', u'牆頂樓層', 'ref'),
    ('WALL_BASE_OFFSET', u'牆底偏移', 'mm'), ('WALL_TOP_OFFSET', u'牆頂偏移', 'mm'),
    ('WALL_USER_HEIGHT_PARAM', u'牆高度', 'mm'), ('WALL_ATTR_ROOM_BOUNDING', u'房間邊界', 'int'),
    ('FAMILY_BASE_LEVEL_PARAM', u'基底樓層', 'ref'), ('FAMILY_TOP_LEVEL_PARAM', u'頂部樓層', 'ref'),
    ('FAMILY_BASE_LEVEL_OFFSET_PARAM', u'基底偏移', 'mm'), ('FAMILY_TOP_LEVEL_OFFSET_PARAM', u'頂部偏移', 'mm'),
    ('INSTANCE_LENGTH_PARAM', u'實例長度', 'mm'), ('INSTANCE_SILL_HEIGHT_PARAM', u'窗台高度', 'mm'),
    ('INSTANCE_HEAD_HEIGHT_PARAM', u'開口頂標高', 'mm'), ('FLOOR_HEIGHTABOVELEVEL_PARAM', u'樓板偏移', 'mm'),
    ('ALL_MODEL_MARK', u'標記', 'text'), ('ALL_MODEL_INSTANCE_COMMENTS', u'註解', 'text')]


def write(path, value):
    with codecs.open(path, 'w', 'utf-8') as stream:
        stream.write(dumps(value))


data = dict(state='opening', source_path=os.path.basename(MODEL), categories={}, elements=[], graphs={}, errors=[], warnings=[])
doc = None
try:
    write(STATUS, dict(state='opening'))
    if not MODEL:
        raise ValueError(u'請先設定環境變數 CASEGRAPH_MODEL 為要讀取的 .rvt 完整路徑。')
    doc = __revit__.Application.OpenDocumentFile(MODEL)
    modified = doc.IsModified
    data.update(document=document_id(doc), title=doc.Title, revit_version=doc.Application.VersionNumber,
                model_version=str(DB.Document.GetDocumentVersion(doc).VersionGUID), exported_at=datetime.now().isoformat())
    for warning in doc.GetWarnings():
        data['warnings'].append(dict(description=warning.GetDescriptionText(),
            element_ids=sorted(set(str(i.IntegerValue) for i in list(warning.GetFailingElements())+list(warning.GetAdditionalElements())))))
    for category in CATEGORIES:
        collector = DB.FilteredElementCollector(doc).OfCategory(getattr(DB.BuiltInCategory, category)).WhereElementIsNotElementType()
        try:
            elements = sorted(list(collector), key=lambda e: e.Id.IntegerValue)
        finally:
            collector.Dispose()
        selected = [e for e in elements if not isinstance(e, (DB.CurveElement, DB.FilledRegion))
                    and e.GetTypeId() != DB.ElementId.InvalidElementId]
        assert len(selected) <= 500, 'Category exceeds export bound; narrow selection explicitly'
        data['categories'][category] = dict(total=len(elements), exported=len(selected))
        for element in selected:
            try:
                record = detail_snapshot(doc, element)
                context = Context(doc, element, 40)
                explicit(context, element)
                values = {}
                for code, label, kind in PARAMETERS:
                    builtin = getattr(DB.BuiltInParameter, code, None)
                    parameter = element.get_Parameter(builtin) if builtin is not None else None
                    if parameter is None or not parameter.HasValue:
                        continue
                    value = dict(label=label, display=parameter.AsValueString() or parameter.AsString() or '')
                    if kind == 'mm' and parameter.StorageType == DB.StorageType.Double:
                        value['mm'] = round(parameter.AsDouble()*304.8, 3)
                    elif kind == 'ref' and parameter.StorageType == DB.StorageType.ElementId:
                        target = doc.GetElement(parameter.AsElementId())
                        if target:
                            value.update(element_id=str(target.Id.IntegerValue), display=name_of(target))
                            if isinstance(target, DB.Level):
                                context.add(element, target, 'LOCATED_ON_LEVEL', 'Revit '+code)
                    elif kind == 'int' and parameter.StorageType == DB.StorageType.Integer:
                        value['value'] = parameter.AsInteger()
                    values[code] = value
                record['properties'].update(native_class=element.GetType().Name,
                    view_specific=bool(element.ViewSpecific), catalog_category=category, constraints=values,
                    revit_warnings=[w['description'] for w in data['warnings'] if str(element.Id.IntegerValue) in w['element_ids']])
                context.data['nodes'][0] = record
                # Native properties can report the same level through multiple BIPs.
                unique = {}
                for edge in context.data['edges']:
                    key = (edge['source'], edge['target'], edge['relationship_type'])
                    if key in unique:
                        unique[key]['evidence'] += '; '+edge['evidence']
                    else:
                        unique[key] = edge
                context.data['edges'] = list(unique.values())
                assert doc.GetElement(record['source_element_id']).Id == element.Id
                assert str(element.GetTypeId().IntegerValue) == record['properties']['type_id']
                assert not element.ViewSpecific or doc.GetElement(element.OwnerViewId) is not None
                data['elements'].append(record)
                data['graphs'][record['source_element_id']] = context.data
            except Exception:
                data['errors'].append(dict(element_id=str(element.Id.IntegerValue), error=traceback.format_exc()))
        write(STATUS, dict(state='reading', category=category, count=len(data['elements']), errors=len(data['errors'])))
    assert doc.IsModified == modified, 'Read-only export changed document state'
    data.update(state='complete', read_only=True, count=len(data['elements']))
    write(OUTPUT, data)
    write(STATUS, dict(state='complete', count=len(data['elements']), categories=data['categories'], errors=data['errors']))
except Exception:
    write(STATUS, dict(state='failed', error=traceback.format_exc()))
    raise
finally:
    if doc is not None:
        doc.Close(False)
