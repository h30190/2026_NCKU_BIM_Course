# -*- coding: utf-8 -*-
"""Native Revit 2022 plan coverage check; opens and closes the model without saving."""
import os
import sys
import json
import codecs
import traceback
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
from casegraph.extractor import document_id
from casegraph.serialization import dumps
from casegraph.model_checks import RevitCheckAdapter, run_checks
from casegraph.plan_checks import list_plan_views, PlanCheckJob

doc = None
receipt = os.path.join(ROOT, 'backend', 'data', 'plan-check-verification.json')
result = dict(state='starting')


def write_result():
    with codecs.open(receipt, 'w', 'utf-8') as stream:
        stream.write(dumps(result))


try:
    with codecs.open(os.path.join(ROOT, 'samples', 'demo-model-export.json'), 'r', 'utf-8') as stream:
        export = json.load(stream)
    # Run on a saved-file copy so the user's open Revit model is not involved.
    model_path = os.path.join(ROOT, 'backend', 'data', 'plan-check-model.rvt')
    doc = __revit__.Application.OpenDocumentFile(model_path)
    modified_on_open = doc.IsModified
    views = list_plan_views(doc)
    assert views, 'No non-template plans found'
    scope = dict(document=document_id(doc), scope='all', source_key='*', source_category_id=None)
    options = dict(position_mm=1., volume_mm3=0., clash_target='ALL', clash_skip_joined=True,
                   clash_skip_collinear=True, clash_line_mm=1.)
    job = PlanCheckJob(views, scope, ['clash'], options, export['elements'][0]['node_id'])
    result.update(state='running', model_path=model_path, views=views, completed=[])
    write_result()
    for view in views:
        current = doc.GetElement(DB.ElementId(int(view['id'])))
        report = run_checks(RevitCheckAdapter(doc, current), scope, ['clash'], **options)
        assert report['view_id'] == view['id']
        assert report['clash_skip_joined'] and report['clash_skip_collinear']
        verify = RevitCheckAdapter(doc, current)
        try:
            checked = set()
            for row in report['results']:
                for record in row['records']:
                    key = record['source_element_id']
                    if key not in checked:
                        assert not verify.is_clash_room(doc.GetElement(key)), 'Room collision row displayed'
                        assert verify.geometry(doc.GetElement(key))['solids'], 'Non-solid collision row displayed'
                        checked.add(key)
        finally:
            verify.close()
        job.add(view, report)
        result['completed'] = job.report['plan_reports']
        write_result()
    report = job.finish()
    assert report['completed_plans'] == len(views)
    assert len(set(v['id'] for v in views)) == len(views)
    assert all(row.get('views') for row in report['results'])
    assert doc.IsModified == modified_on_open, 'Read-only scan unexpectedly modified the document'
    result.update(state='complete', summary=dict((k, report[k]) for k in
        ('total_plans', 'completed_plans', 'status', 'sources', 'pairs', 'findings', 'unknown',
         'skipped_joined', 'skipped_collinear', 'excluded_room_elements', 'excluded_room_sources',
         'excluded_nonsolid_elements', 'geometry_failures', 'truncated', 'warnings')))
except Exception:
    result.update(state='failed', error=traceback.format_exc())
    raise
finally:
    if doc is not None:
        doc.Close(False)
    write_result()
