# -*- coding: utf-8 -*-
"""Read actual column instances and constraints in Revit 2022; never save the model."""
import os
import sys
import codecs
import traceback
from datetime import datetime
from Autodesk.Revit import DB

ROOT = r'C:\Users\izumi\Desktop\Revit Plugin Test'
MODEL = u'C:\\Users\\izumi\\Desktop\\(U1-U21)個別元件測試用平面圖.rvt'
OUTPUT = os.path.join(ROOT, 'backend', 'data', 'column-case-export.json')
sys.path.insert(0, os.path.join(ROOT, 'CaseBase.extension', 'lib'))
from casegraph.element_index import detail_snapshot
from casegraph.extractor import document_id, Context, explicit
from casegraph.serialization import dumps

result = dict(state='opening', source_path=MODEL, categories={}, columns=[], errors=[])
doc = None


def write():
    with codecs.open(OUTPUT, 'w', 'utf-8') as stream:
        stream.write(dumps(result))


try:
    write()
    doc = __revit__.Application.OpenDocumentFile(MODEL)
    original_modified = doc.IsModified
    result.update(state='reading', document=document_id(doc), title=doc.Title,
                  revit_version=doc.Application.VersionNumber, exported_at=datetime.now().isoformat(),
                  model_version=str(DB.Document.GetDocumentVersion(doc).VersionGUID))
    for category in ('OST_StructuralColumns', 'OST_Columns'):
        collector = DB.FilteredElementCollector(doc).OfCategory(getattr(DB.BuiltInCategory, category)).WhereElementIsNotElementType()
        try:
            elements = sorted(list(collector), key=lambda e: e.Id.IntegerValue)
        finally:
            collector.Dispose()
        result['categories'][category] = len(elements)
        for element in elements[:30]:
            try:
                record = detail_snapshot(doc, element)
                context = Context(doc, element, 30)
                explicit(context, element)
                values = {}
                for code, label in [('FAMILY_BASE_LEVEL_PARAM', u'基底樓層'), ('FAMILY_TOP_LEVEL_PARAM', u'頂部樓層'),
                                    ('FAMILY_BASE_LEVEL_OFFSET_PARAM', u'基底偏移'), ('FAMILY_TOP_LEVEL_OFFSET_PARAM', u'頂部偏移'),
                                    ('INSTANCE_LENGTH_PARAM', u'柱長'), ('ALL_MODEL_MARK', u'標記')]:
                    parameter = element.get_Parameter(getattr(DB.BuiltInParameter, code))
                    if parameter is None or not parameter.HasValue:
                        continue
                    value = dict(label=label, display=parameter.AsValueString() or parameter.AsString() or '')
                    if parameter.StorageType == DB.StorageType.ElementId:
                        linked = doc.GetElement(parameter.AsElementId())
                        if linked:
                            value.update(element_id=str(linked.Id.IntegerValue), name=DB.Element.Name.GetValue(linked))
                            value['display'] = value['name']
                            if isinstance(linked, DB.Level):
                                context.add(element, linked, 'LOCATED_ON_LEVEL', 'Revit '+code)
                    elif parameter.StorageType == DB.StorageType.Double:
                        value['mm'] = round(parameter.AsDouble()*304.8, 3)
                    values[code] = value
                record['properties']['column_constraints'] = values
                context.data['nodes'][0] = record
                assert doc.GetElement(record['source_element_id']).Id == element.Id
                assert doc.GetElement(DB.ElementId(int(record['properties']['type_id']))).Id == element.GetTypeId()
                result['columns'].append(dict(category=category, element=record, graph=context.data, constraints=values))
            except Exception:
                result['errors'].append(dict(element_id=str(element.Id.IntegerValue), error=traceback.format_exc()))
        write()
    assert doc.IsModified == original_modified, 'Export unexpectedly changed document state'
    result.update(state='complete', read_only=True, count=len(result['columns']))
except Exception:
    result.update(state='failed', error=traceback.format_exc())
    raise
finally:
    if doc is not None:
        doc.Close(False)
    write()
