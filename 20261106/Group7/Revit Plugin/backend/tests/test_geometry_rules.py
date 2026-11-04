import copy
import json
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.geometry_rules import RuleStore, make_rule, with_rule_overlay
from casegraph.geometry_check import MM3_PER_FT3, check_rule, intersect_solids, RevitGeometryAdapter
from casegraph.hierarchy import build_hierarchy


def node(uid='chair', **props):
    properties = dict(category='Furniture', category_id='-2000080', type_id='100', type_name='椅子 m³', element_id=uid)
    properties.update(props)
    return dict(node_id='model:'+uid, node_type='ELEMENT', source_document='model', source_element_id=uid,
                name=uid, properties=properties)


class Solid:
    def __init__(self, cells):
        self.cells = set(cells)
        self.Volume = len(self.cells)/MM3_PER_FT3
        self.Faces = SimpleNamespace(Size=6)
        self.disposed = False

    def Dispose(self):
        self.disposed = True


class Adapter:
    view_id = '1'
    view_name = '測試 3D 視圖'

    def __init__(self, elements=None, candidates=None, geometry=None):
        self.elements = ['chair'] if elements is None else elements
        self.targets = ['wall'] if candidates is None else candidates
        self.shapes = geometry or {'chair': [Solid([1, 2])], 'wall': [Solid([2, 3])]}
        self.hosts, self.joins = set(), set()
        self.closed, self.fail = False, False
        self.created = []

    def sources(self, rule):
        yield from self.elements

    def candidates(self, source, rule):
        yield from self.targets

    def key(self, element):
        return element

    def record(self, element):
        return node(element)

    def geometry(self, element):
        return dict(solids=self.shapes[element], problems=[])

    def boolean(self, a, b, operation):
        if self.fail:
            raise ValueError('Boolean failed')
        result = Solid(a.cells & b.cells if operation == 'Intersect' else a.cells | b.cells)
        self.created.append(result)
        return result

    def is_host(self, a, b):
        return (a, b) in self.hosts

    def joined(self, a, b):
        return (a, b) in self.joins

    def close(self):
        self.closed = True


def rule(**options):
    return make_rule(node(), options.pop('scope', 'type'), 'OST_Walls', **options)


def test_real_overlap_is_violation_but_bounding_candidate_and_touch_are_not():
    adapter = Adapter()
    report = check_rule(adapter, rule())
    assert report['status'] == 'VIOLATION' and report['violations'] == 1
    assert report['results'][0]['volume_mm3'] == pytest.approx(1)
    assert adapter.closed and all(s.disposed for s in adapter.created)
    # A broad-phase hit is insufficient: solids sharing no volume, including a
    # touching face or void, must be evaluated as zero-volume intersection.
    adapter.shapes['wall'] = [Solid([3, 4])]
    report = check_rule(adapter, rule())
    assert report['status'] == 'PASS' and report['checked_pairs'] == 1


def test_small_disjoint_intersections_are_unioned_and_duplicates_not_double_counted():
    adapter = Adapter(geometry={'chair': [Solid([1, 2]), Solid([3, 4])], 'wall': [Solid([1, 2, 3, 4])]})
    assert check_rule(adapter, rule(tolerance_mm3=3))['status'] == 'VIOLATION'
    adapter.shapes['chair'] = [Solid([1, 2]), Solid([1, 2])]
    report = check_rule(adapter, rule(tolerance_mm3=3))
    assert report['status'] == 'PASS'  # Adding volumes would incorrectly report 4.
    assert all(s.disposed for s in adapter.created)


def test_no_solids_failed_boolean_partial_geometry_and_joined_results_stay_unknown():
    adapter = Adapter(geometry={'chair': [], 'wall': [Solid([1])]})
    assert check_rule(adapter, rule())['status'] == 'INCOMPLETE'
    adapter = Adapter()
    adapter.fail = True
    assert check_rule(adapter, rule())['unknown'] == 1
    adapter = Adapter(geometry={'chair': [Solid([1])], 'wall': [Solid([2])]})
    adapter.joins.add(('chair', 'wall'))
    assert check_rule(adapter, rule())['status'] == 'INCOMPLETE'
    checked = intersect_solids(dict(solids=[Solid([1])], problems=['mesh']),
                              dict(solids=[Solid([2])], problems=[]), Adapter(), 0)
    assert checked['status'] == 'UNKNOWN'
    # Partial geometry can still prove a violation; it cannot prove no overlap.
    checked = intersect_solids(dict(solids=[Solid([1])], problems=['mesh']),
                              dict(solids=[Solid([1])], problems=[]), Adapter(), 0)
    assert checked['status'] == 'VIOLATION'


def test_host_exception_is_explicit_and_empty_view_is_not_a_pass():
    adapter = Adapter()
    adapter.hosts.add(('chair', 'wall'))
    assert check_rule(adapter, rule())['violations'] == 1
    report = check_rule(adapter, rule(exclude_host=True))
    assert report['status'] == 'PASS' and report['excluded_hosts'] == 1 and report['checked_pairs'] == 0
    assert check_rule(Adapter(elements=[]), rule())['status'] == 'NO_MATCH'


def test_caps_and_timeout_do_not_report_complete_passes():
    adapter = Adapter(elements=['chair', 'second'], candidates=[], geometry={'chair': [Solid([1])], 'second': [Solid([2])]})
    report = check_rule(adapter, rule(), max_sources=1)
    assert report['truncated'] and report['status'] == 'INCOMPLETE'
    report = check_rule(Adapter(), rule(), max_pairs=0)
    assert report['truncated'] and report['unknown'] == 1
    ticks = iter([0., 6.])
    report = check_rule(Adapter(), rule(), seconds=5, clock=lambda: next(ticks))
    assert report['truncated'] and report['status'] == 'INCOMPLETE'


def test_symmetric_pairs_are_checked_once_without_marking_other_source_passed():
    adapter = Adapter(elements=['chair', 'wall'], candidates=['chair', 'wall'])
    report = check_rule(adapter, rule())
    assert report['sources'] == 2 and report['violations'] == 1
    assert report['checked_pairs'] == 1 and report['passed_sources'] == 0


def test_rule_store_unicode_scope_dedup_and_corrupt_file_preservation(tmp_path):
    store = RuleStore(str(tmp_path))
    first = rule()
    edited = rule(tolerance_mm3=1)
    assert first['id'] == edited['id']
    assert rule(scope='category')['id'] != first['id']
    store.save('model', [first])
    store.save('model', [edited])
    assert store.load('model') == [edited]
    with pytest.raises(ValueError):
        store.save('model', [first, edited])
    assert store.load('model') == [edited]
    assert store.load('another-model') == []
    path = Path(store.path('model'))
    assert '椅子' in json.loads(path.read_text(encoding='utf-8'))['rules'][0]['name']
    path.write_text('{broken', encoding='utf-8')
    with pytest.raises(ValueError):
        store.save('model', [first])
    assert path.read_text() == '{broken'
    with pytest.raises(ValueError):
        rule(tolerance_mm3=float('nan'))
    with pytest.raises(ValueError):
        make_rule(node(simulation=True), 'type', 'OST_Walls')


def test_violation_graph_has_own_hierarchy_and_never_changes_snapshot():
    graph = dict(root='model:chair', nodes=[node()], edges=[], warnings=[])
    before = copy.deepcopy(graph)
    report = check_rule(Adapter(), rule())
    report['graph_root'] = graph['root']
    overlay = with_rule_overlay(graph, report)
    assert graph == before
    assert overlay['nodes'][0]['properties']['_rule_violation']
    tree = build_hierarchy(overlay)
    assert tree['physical_count'] == 0
    assert any(entry['record']['name'] == '相交規則違規' for entry in tree['entries'].values())
    report['stale'] = True
    assert with_rule_overlay(graph, report) is graph


def test_geometry_adapter_walks_nested_instances_and_marks_mesh_incomplete():
    class Geometry(list):
        def Dispose(self):
            pass

    class Instance:
        def GetInstanceGeometry(self):
            return Geometry([Solid([9])])  # Already in model coordinates.

    class Mesh:
        pass

    class Options:
        def Dispose(self):
            pass

    adapter = object.__new__(RevitGeometryAdapter)
    adapter.DB = SimpleNamespace(Options=Options, ViewDetailLevel=SimpleNamespace(Fine=3), Solid=Solid, GeometryInstance=Instance, Mesh=Mesh)
    adapter.owners = []
    element = SimpleNamespace(get_Geometry=lambda options: Geometry([Instance(), Mesh()]))
    geometry = adapter.geometry(element)
    assert len(geometry['solids']) == 1 and geometry['solids'][0].cells == {9}
    assert geometry['problems'] and '網格' in geometry['problems'][0]
    adapter.close()


def test_revit_adapter_collects_only_current_view_and_requested_type_or_category():
    # Exercise the production collector path against a small model containing
    # other views, types, categories, and target walls outside the source bounds.
    def element(uid, category, typ, view, x=10):
        return SimpleNamespace(UniqueId=uid, category=category, typ=typ, view=view, x=x)

    model = [element('same', -2000080, 100, 7), element('other-type', -2000080, 101, 7),
             element('hidden', -2000080, 100, 8), element('other-category', -2000011, 100, 7),
             element('wall', -2000011, 200, 7), element('hidden-wall', -2000011, 200, 8),
             element('distant-wall', -2000011, 200, 7, x=50)]
    xyz = lambda x, y, z: SimpleNamespace(X=x, Y=y, Z=z)
    source_box = SimpleNamespace(Min=xyz(0, 0, 0), Max=xyz(2, 1, 1),
                                Transform=SimpleNamespace(OfPoint=lambda p: xyz(10-p.Y, p.X, p.Z)))
    model[0].get_BoundingBox = lambda view: source_box

    class Collector:
        def __init__(self, doc, view_id):
            assert doc is model and view_id == 7
            self.items = [e for e in doc if e.view == view_id]

        def WhereElementIsNotElementType(self):
            return self

        def OfCategoryId(self, category):
            self.items = [e for e in self.items if e.category == category]
            return self

        OfCategory = OfCategoryId

        def WherePasses(self, predicate):
            self.items = list(filter(predicate, self.items))
            return self

        def __iter__(self):
            return iter(self.items)

        def Dispose(self):
            pass

    outlines = []

    def outline(low, high):
        outlines.append((low, high))
        return low, high

    adapter = object.__new__(RevitGeometryAdapter)
    adapter.DB = SimpleNamespace(FilteredElementCollector=Collector, ElementId=int, XYZ=xyz, Outline=outline,
        BuiltInParameter=SimpleNamespace(ELEM_TYPE_PARAM=1), BuiltInCategory=SimpleNamespace(OST_Walls=-2000011),
        ParameterFilterRuleFactory=SimpleNamespace(CreateEqualsRule=lambda parameter, typ: lambda e: e.typ == typ),
        ElementParameterFilter=lambda predicate: predicate,
        BoundingBoxIntersectsFilter=lambda box: lambda e: box[0].X <= e.x <= box[1].X)
    adapter.doc, adapter.view, adapter.owners = model, SimpleNamespace(Id=7), []
    assert [e.UniqueId for e in adapter.sources(rule())] == ['same']
    assert [e.UniqueId for e in adapter.sources(rule(scope='category'))] == ['same', 'other-type']
    assert [e.UniqueId for e in adapter.candidates(model[0], rule())] == ['other-category', 'wall']
    low, high = outlines[0]
    assert low.X == pytest.approx(9, abs=1e-5) and high.X == pytest.approx(10, abs=1e-5)
    assert low.Y == pytest.approx(0, abs=1e-5) and high.Y == pytest.approx(2, abs=1e-5)
    adapter.close()
