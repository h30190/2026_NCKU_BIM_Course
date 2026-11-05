import copy
import sys
from types import SimpleNamespace
from test_model_checks import ClashAdapter, run, SCOPE
from casegraph.plan_checks import list_plan_views, PlanCheckJob


VIEWS = [dict(id=str(i), name=name, kind=kind) for i, (name, kind) in enumerate([
    ('1F PLAN', 'FloorPlan'), ('1F CEILING', 'CeilingPlan'),
    ('1F STRUCTURE', 'EngineeringPlan'), ('1F AREA', 'AreaPlan')])]
OPTIONS = dict(position_mm=1., volume_mm3=0., clash_target='ALL', clash_skip_joined=True,
               clash_skip_collinear=True, clash_line_mm=1.)


def job(**kwargs):
    return PlanCheckJob(copy.deepcopy(VIEWS), SCOPE, ['clash'], OPTIONS, 'model:a', **kwargs)


def test_enumeration_includes_every_native_viewplan_type_and_dependent_views_but_not_templates(monkeypatch):
    class ViewPlan:
        def __init__(self, i, kind, template=False):
            self.Id, self.Name, self.ViewType, self.IsTemplate = i, str(i), kind, template
    views = [ViewPlan(i, v['kind']) for i, v in enumerate(VIEWS)]
    views += [ViewPlan(4, 'FloorPlan'), ViewPlan(5, 'CeilingPlan', template=True), object()]
    disposed = []
    class Collector:
        def __init__(self, doc):
            assert doc == 'doc'
        def OfClass(self, cls):
            assert cls is ViewPlan
            return self
        def __iter__(self):
            return iter(v for v in views if isinstance(v, ViewPlan))
        def Dispose(self):
            disposed.append(True)
    monkeypatch.setitem(sys.modules, 'Autodesk', SimpleNamespace())
    monkeypatch.setitem(sys.modules, 'Autodesk.Revit', SimpleNamespace(DB=SimpleNamespace(FilteredElementCollector=Collector, ViewPlan=ViewPlan)))
    monkeypatch.setitem(sys.modules, 'casegraph.extractor', SimpleNamespace(id_value=str))
    selected = list_plan_views('doc')
    assert {v['kind'] for v in selected} == {'FloorPlan', 'CeilingPlan', 'EngineeringPlan', 'AreaPlan'}
    assert len(selected) == 5 and all(v['id'] != '5' for v in selected) and disposed


def test_results_deduplicate_across_all_plans_and_keep_every_origin_without_mutating_reports():
    batch = job()
    result = run(ClashAdapter(), ('clash',))
    before = copy.deepcopy(result)
    for i, view in enumerate(VIEWS):
        assert batch.index == i and not batch.done
        batch.add(view, result)
    assert batch.done and result == before
    report = batch.finish()
    assert report['range_mode'] == 'plans' and report['completed_plans'] == 4
    assert report['counts'] == {'clash': 2} and report['findings'] == 2
    assert report['sources'] == 4*result['sources'] and report['pairs'] == 4*result['pairs']
    assert report['results'][0]['views'] == VIEWS
    assert report['status'] == 'REVIEW' and len(report['plan_reports']) == 4


def test_cancellation_failure_limits_and_unknown_geometry_never_report_clear():
    clear = run(ClashAdapter(), ('clash',), volume_mm3=999)
    batch = job()
    batch.add(VIEWS[0], clear)
    report = batch.finish(cancelled=True)
    assert report['status'] == 'INCOMPLETE' and report['completed_plans'] == 1 and report['cancelled']
    batch = job()
    for view in VIEWS:
        batch.add(view, clear) if view['id'] != '1' else batch.add(view, error='Unavailable ceiling view')
    assert batch.finish()['status'] == 'INCOMPLETE'
    assert batch.report['plan_reports'][1]['status'] == 'INCOMPLETE'
    limited = run(ClashAdapter(), ('clash',), max_pairs=0)
    batch = job()
    for view in VIEWS:
        batch.add(view, limited)
    assert batch.finish()['status'] == 'INCOMPLETE'
    batch = job(max_results=0)
    for view in VIEWS:
        batch.add(view, run(ClashAdapter(), ('clash',)))
    assert batch.finish()['truncated'] and batch.report['status'] == 'INCOMPLETE'


def test_skips_and_empty_plans_are_not_misreported_as_full_geometry_passes():
    batch = job()
    adapter = ClashAdapter()
    adapter.joins.add(('a', 'wall'))
    adapter.joins.add(('a', 'other'))
    skipped = run(adapter, ('clash',))
    for view in VIEWS:
        batch.add(view, skipped)
    report = batch.finish()
    assert report['skipped_joined'] == 4*skipped['skipped_joined'] and report['findings'] == 0
    batch = job()
    adapter = ClashAdapter()
    adapter.elements = []
    for view in VIEWS:
        batch.add(view, run(adapter, ('clash',)))
    assert batch.finish()['status'] == 'NO_MATCH'


def test_all_plan_solid_only_exclusions_and_extraction_failures_keep_coverage():
    adapter = ClashAdapter()
    adapter.shapes['a'] = []
    empty = run(adapter, ('clash',))
    batch = job()
    for view in VIEWS:
        batch.add(view, empty)
    report = batch.finish()
    assert report['status'] == 'NOT_APPLICABLE' and report['excluded_nonsolid_sources'] == 4
    assert not report['results'] and report['unknown'] == 0
    adapter = ClashAdapter()
    def fail(element):
        raise ValueError('Geometry extraction failed')
    adapter.geometry = fail
    failed = run(adapter, ('clash',))
    batch = job()
    for view in VIEWS:
        batch.add(view, failed)
    report = batch.finish()
    assert report['status'] == 'INCOMPLETE' and report['geometry_failures'] > 0
    assert not report['results'] and report['warnings']


def test_room_exclusions_are_aggregated_across_plans_without_false_passes():
    adapter = ClashAdapter()
    adapter.elements, adapter.rooms = ['room'], {'room'}
    room_only = run(adapter, ('clash',))
    batch = job()
    for view in VIEWS:
        batch.add(view, room_only)
    report = batch.finish()
    assert report['excluded_room_elements'] == report['excluded_room_sources'] == 4
    assert report['status'] == 'NOT_APPLICABLE' and not report['results']
