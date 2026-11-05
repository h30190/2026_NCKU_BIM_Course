# -*- coding: utf-8 -*-
"""Read-only Revit 2022 export for demo cases; run with pyrevit run.

Opens the supplied model through Revit's API, creates no transactions, and closes
without saving. All exports stay in the CaseGraph workspace.
"""
import sys
import os
import codecs
import traceback
from collections import defaultdict
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
sys.path.insert(0, os.path.join(ROOT, 'CaseBase.extension', 'lib'))
from casegraph.extractor import document_id, Context, explicit
from casegraph.element_index import detail_snapshot
from casegraph.serialization import dumps

DESTINATION = os.path.join(ROOT, 'samples', 'demo-model-export.json')
STATUS = os.path.join(ROOT, 'backend', 'data', 'demo-export-status.json')
MODEL = os.environ.get('CASEGRAPH_MODEL') or u''


def write(path, value):
    with codecs.open(path, 'w', 'utf-8') as stream:
        stream.write(dumps(value))


def export(doc):
    identity = document_id(doc)
    data = dict(schema=1, source_path=os.path.basename(doc.PathName), document=identity, title=doc.Title,
                revit_version=doc.Application.VersionNumber,
                model_version=str(DB.Document.GetDocumentVersion(doc).VersionGUID),
                exported_at=datetime.now().isoformat(), categories={}, elements=[], graphs={}, errors=[])
    categories = ['OST_Doors', 'OST_Windows', 'OST_Walls', 'OST_Floors',
                  'OST_ElectricalFixtures', 'OST_DetailComponents', 'OST_Furniture',
                  'OST_GenericModel', 'OST_MechanicalEquipment', 'OST_PlumbingFixtures']
    cache = {}
    for category in categories:
        collector = DB.FilteredElementCollector(doc).OfCategory(getattr(DB.BuiltInCategory, category)).WhereElementIsNotElementType()
        try:
            elements = sorted(list(collector), key=lambda e: e.Id.IntegerValue)
            groups = defaultdict(list)
            for element in elements:
                if isinstance(element, DB.CurveElement):
                    continue
                groups[str(element.GetTypeId().IntegerValue)].append(element)
            # Spread representatives across types before taking more instances.
            chosen = []
            for index in range(30):
                for key in sorted(groups):
                    if len(groups[key]) > index and len(chosen) < 30:
                        chosen.append(groups[key][index])
            data['categories'][category] = dict(total=len(elements), exported=len(chosen), types=len(groups))
            for element in chosen:
                try:
                    record = detail_snapshot(doc, element)
                    context = Context(doc, element, 25)
                    explicit(context, element)
                    context.data['nodes'][0] = record
                    assert doc.GetElement(record['source_element_id']).Id == element.Id
                    data['elements'].append(record)
                    data['graphs'][record['source_element_id']] = context.data
                except Exception as error:
                    data['errors'].append(dict(element_id=str(element.Id.IntegerValue), error=unicode(error)))
            write(STATUS, dict(state='extracting', category=category, count=len(data['elements'])))
        finally:
            collector.Dispose()
    write(DESTINATION, data)
    return data


document = None
try:
    # The runner's ANSI journal can corrupt non-ASCII model arguments. Use the
    # exact Unicode source path instead of that command-line transport.
    if not MODEL:
        raise ValueError(u'請先設定環境變數 CASEGRAPH_MODEL 為要讀取的 .rvt 完整路徑。')
    write(STATUS, dict(state='opening', model=os.path.basename(MODEL)))
    document = __revit__.Application.OpenDocumentFile(MODEL)
    result = export(document)
    write(STATUS, dict(state='complete', count=len(result['elements']), document=result['document'],
                       categories=result['categories'], errors=result['errors'], output=DESTINATION))
except Exception:
    write(STATUS, dict(state='failed', error=traceback.format_exc()))
    raise
finally:
    if document is not None:
        document.Close(False)
