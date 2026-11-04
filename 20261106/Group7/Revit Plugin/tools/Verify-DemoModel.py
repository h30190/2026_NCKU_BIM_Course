# -*- coding: utf-8 -*-
"""Native read-only verification of case identities and selected-range filtering."""
import os
import sys
import json
import codecs
import traceback

ROOT = r'C:\Users\izumi\Desktop\Revit Plugin Test'
sys.path.insert(0, os.path.join(ROOT, 'CaseBase.extension', 'lib'))
from casegraph.extractor import document_id
from casegraph.serialization import dumps
from casegraph.element_index import detail_snapshot
from casegraph.model_checks import RevitCheckAdapter, run_checks

doc = None
result = dict(state='starting')
try:
    with codecs.open(os.path.join(ROOT, 'samples', 'demo-model-export.json'), 'r', 'utf-8') as stream:
        export = json.load(stream)
    with codecs.open(os.path.join(ROOT, 'samples', 'model-case-topics.json'), 'r', 'utf-8') as stream:
        topics = json.load(stream)
    doc = __revit__.Application.OpenDocumentFile(export['source_path'])
    assert document_id(doc) == export['document']
    records = dict((n['properties']['element_id'], n) for n in export['elements'])
    validated = []
    for topic in topics['cases']:
        record = records[topic['element_id']]
        element = doc.GetElement(record['source_element_id'])
        assert element is not None and str(element.Id.IntegerValue) == topic['element_id']
        live = detail_snapshot(doc, element)
        assert live['source_document'] == record['source_document']
        assert live['properties']['dimensions'] == record['properties']['dimensions']
        assert live['properties']['type_id'] == record['properties']['type_id']
        assert doc.GetElement(element.OwnerViewId) is not None if element.ViewSpecific else True
        validated.append(topic['element_id'])
    detail = doc.GetElement(records['489868']['source_element_id'])
    view = doc.GetElement(detail.OwnerViewId)
    door = doc.GetElement(records['334206']['source_element_id'])
    wall = door.Host
    keys = [door.UniqueId, wall.UniqueId]
    scope = dict(document=document_id(doc), scope='all', source_key='*', source_category_id=None)
    report = run_checks(RevitCheckAdapter(doc, view), scope, ['clash'], element_keys=keys)
    assert report['sources'] == 2 and report['range_count'] == 2
    assert all(n['source_element_id'] in keys for row in report['results'] for n in row['records'])
    one = run_checks(RevitCheckAdapter(doc, view), scope, ['clash'], element_keys=[door.UniqueId])
    assert one['sources'] == 1 and one['pairs'] == 0 and one['findings'] == 0
    result = dict(state='complete', validated_case_elements=validated, document=export['document'],
        view=view.Name, range_report=dict((k, report[k]) for k in ['sources','pairs','status','unknown','range_count']),
        one_element_report=dict((k, one[k]) for k in ['sources','pairs','status','unknown','range_count']))
except Exception:
    result = dict(state='failed', error=traceback.format_exc())
    raise
finally:
    if doc is not None:
        doc.Close(False)
    with codecs.open(os.path.join(ROOT, 'backend', 'data', 'demo-model-verification.json'), 'w', 'utf-8') as stream:
        stream.write(dumps(result))
