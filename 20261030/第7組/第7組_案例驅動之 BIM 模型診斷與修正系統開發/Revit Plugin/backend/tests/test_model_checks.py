import copy
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.model_checks import run_checks, duplicate_position, collinear_lines, with_check_overlay, RevitCheckAdapter
from casegraph.hierarchy import build_hierarchy
from casegraph.presentation import relationship_rows
from test_geometry_rules import Adapter as SolidAdapter, Solid, node

SCOPE = dict(document='model', scope='type', source_category_id='-2000080', source_key='100')


def metadata(**options):
    result = dict(type='100', category='furniture', bounds=(0, 0, 0, 10, 10, 10),
                  placement=('point', (0, 0, 0), (0, 1)))
    result.update(options)
    return result


class Adapter(SolidAdapter):
    def __init__(self):
        super().__init__(elements=['a', 'b'], geometry={'a': [Solid([1, 2])], 'b': [Solid([1, 2])]})
        self.data = dict(a=metadata(), b=metadata())
        self.curves = set()
        self.rooms = set()
        self.marks = dict(a=('furniture', 'A-01'), b=('furniture', 'A-01'))
        self.messages = [dict(keys=['a', 'b', 'outside'], message='Revit 測試警告')]

    def metadata(self, element):
        return self.data[element]

    def is_clash_curve(self, element):
        return element in self.curves

    def is_clash_room(self, element):
        return element in self.rooms

    def mark(self, element):
        return self.marks[element]

    def warnings(self):
        return self.messages


def run(adapter=None, checks=('duplicate', 'overlap', 'mark', 'warning'), **options):
    return run_checks(adapter or Adapter(), SCOPE, checks, **options)


def test_common_checks_are_separate_and_native_warnings_keep_only_scoped_elements():
    adapter = Adapter()
    report = run(adapter)
    assert report['counts'] == dict(duplicate=1, overlap=1, mark=1, warning=1)
    assert report['status'] == 'REVIEW' and adapter.closed
    warning = next(r for r in report['results'] if r['check'] == 'warning')
    assert len(warning['records']) == 2 and '1 個關聯元素不在本次範圍' in warning['message']
    assert report['pairs'] == 1  # A/B is not also checked as B/A.
    assert all(s.disposed for s in adapter.created)


def test_same_bounds_do_not_prove_duplicate_or_overlap_and_orientation_matters():
    adapter = Adapter()
    adapter.shapes['b'] = [Solid([3, 4])]
    adapter.data['b']['placement'] = ('point', (0, 0, 0), (1, 0))
    assert run(adapter, ('duplicate', 'overlap'))['status'] == 'CLEAR'
    adapter.data['b']['placement'] = ('point', (0, 0, 0), (0, 1))
    report = run(adapter, ('duplicate', 'overlap'))
    assert report['counts'] == dict(duplicate=1, overlap=0)
    assert report['results'][0]['status'] == 'SUSPECT'
    adapter.data['b']['type'] = '101'
    assert run(adapter, ('duplicate',))['findings'] == 0


def test_position_threshold_inclusive_and_reversed_curves():
    a, b = metadata(), metadata(placement=('point', (1, 0, 0), (0, 1)))
    assert duplicate_position(a, b, 1) is True
    assert duplicate_position(a, b, .99) is False
    a['placement'] = ('curve:Line', (0, 0, 0, 1, 0, 0, 2, 0, 0), ())
    b['placement'] = ('curve:Line', (2, 0, 0, 1, 0, 0, 0, 0, 0), ())
    assert duplicate_position(a, b, 0) is True
    b['placement'] = None
    assert duplicate_position(a, b, 0) is None


def test_marks_ignore_empty_values_and_separate_categories_and_only_run_selected_checks():
    adapter = Adapter()
    adapter.marks['b'] = ('walls', 'A-01')
    assert run(adapter, ('mark',))['status'] == 'CLEAR'
    adapter.marks = dict(a=('furniture', ' '), b=('furniture', None))
    assert run(adapter, ('mark',))['findings'] == 0
    adapter.messages = [dict(keys=['outside'], message='hidden')]
    assert run(adapter, ('warning',))['findings'] == 0


def test_empty_geometry_is_filtered_but_real_geometry_failures_remain_incomplete():
    adapter = Adapter()
    adapter.shapes['a'] = []
    report = run(adapter, ('overlap',))
    assert report['status'] == 'CLEAR' and report['unknown'] == 0
    assert report['excluded_nonsolid_elements'] == 1 and not report['results']
    adapter = Adapter()
    adapter.data['a']['bounds'] = None
    assert run(adapter, ('overlap',))['status'] == 'INCOMPLETE'
    adapter = Adapter()
    adapter.fail = True
    assert run(adapter, ('overlap',))['status'] == 'INCOMPLETE'
    adapter = Adapter()
    adapter.shapes['b'] = [Solid([3])]
    adapter.joins.add(('a', 'b'))
    assert run(adapter, ('overlap',))['status'] == 'INCOMPLETE'


def test_tolerances_caps_empty_scope_and_timeouts_never_report_complete_clear():
    assert run(checks=('overlap',), volume_mm3=2)['status'] == 'CLEAR'
    assert run(checks=('overlap',), max_pairs=0)['status'] == 'INCOMPLETE'
    assert run(max_sources=1)['status'] == 'INCOMPLETE'
    assert run(max_results=1)['status'] == 'INCOMPLETE'
    assert run(seconds=0)['status'] == 'INCOMPLETE'
    adapter = Adapter()
    adapter.elements = []
    assert run(adapter)['status'] == 'NO_MATCH'
    with pytest.raises(ValueError):
        run(position_mm=float('nan'))
    with pytest.raises(ValueError):
        run(checks=[])


def test_warning_read_errors_are_reported_and_geometry_checks_can_continue():
    adapter = Adapter()
    def broken():
        raise ValueError('warnings inaccessible')
    adapter.warnings = broken
    report = run(adapter)
    assert report['unknown'] == 1 and report['counts']['overlap'] == 1
    assert report['status'] == 'INCOMPLETE'


def test_graph_overlay_is_hierarchical_bounded_and_never_mutates_saved_graph():
    original = dict(root='model:a', nodes=[node('a')], edges=[], warnings=[])
    before = copy.deepcopy(original)
    report = run()
    report['graph_root'] = original['root']
    graph = with_check_overlay(original, report)
    assert original == before
    tree = build_hierarchy(graph)
    assert tree['physical_count'] == 0
    assert any(e['record']['name'] == '常見錯誤檢查' for e in tree['entries'].values())
    assert sum(k.startswith('check-element:') for k in tree['entries']) == 8
    assert all(n['properties']['_model_check'] == 'OVERLAP' for n in graph['nodes'])
    descriptions = relationship_rows(graph['nodes'][0], graph)
    assert len(descriptions) == 4 and all('建議：' in row.value for row in descriptions)
    assert with_check_overlay(original, report, limit=1)['warnings']
    many = copy.deepcopy(report)
    many['results'] *= 100
    assert len(with_check_overlay(original, many)['edges']) <= 360
    report['stale'] = True
    assert with_check_overlay(original, report) is original


class ClashAdapter(Adapter):
    def __init__(self):
        super().__init__()
        self.elements = ['a']
        self.data.update(other=metadata(type='101'), wall=metadata(type='200', category='walls'))
        self.shapes.update(other=[Solid([1])], wall=[Solid([2])])
        self.targets = ['a', 'b', 'other', 'wall']

    def clash_candidates(self, source, bounds, target):
        for key in self.targets:
            if target == 'ALL' or (target == 'OST_Walls' and self.data[key]['category'] == 'walls'):
                yield key


def test_cross_clashes_include_different_types_and_categories_outside_source_scope():
    report = run(ClashAdapter(), ('clash', 'overlap'))
    assert report['counts'] == dict(clash=2, overlap=0)
    assert report['sources'] == 1 and report['pairs'] == 2
    assert {r['records'][1]['source_element_id'] for r in report['results']} == {'other', 'wall'}
    report = run(ClashAdapter(), ('clash',), clash_target='OST_Walls')
    assert report['findings'] == 1 and report['results'][0]['records'][1]['source_element_id'] == 'wall'


def test_cross_clashes_use_solids_not_bounds_and_keep_incomplete_results():
    adapter = ClashAdapter()
    adapter.shapes['other'], adapter.shapes['wall'] = [Solid([3])], [Solid([4])]
    assert run(adapter, ('clash',))['status'] == 'CLEAR'
    adapter.shapes['wall'] = []
    report = run(adapter, ('clash',))
    assert report['unknown'] == 0 and report['excluded_nonsolid_elements'] == 1 and not report['results']
    adapter = ClashAdapter()
    adapter.fail = True
    assert run(adapter, ('clash',))['status'] == 'INCOMPLETE'
    adapter = ClashAdapter()
    adapter.shapes['wall'] = [Solid([3])]
    adapter.joins.add(('a', 'wall'))
    assert run(adapter, ('clash',), clash_target='OST_Walls', clash_skip_joined=False)['unknown'] == 1
    assert run(ClashAdapter(), ('clash',), volume_mm3=1)['status'] == 'CLEAR'


def test_cross_clashes_are_deduplicated_across_reversed_pairs_and_existing_overlap_check():
    adapter = ClashAdapter()
    adapter.elements, adapter.targets = ['a', 'other'], ['a', 'other']
    report = run(adapter, ('clash', 'overlap'))
    assert report['counts'] == dict(clash=1, overlap=0) and report['pairs'] == 1
    # A restricted target must not suppress other within-source overlaps.
    report = run(adapter, ('clash', 'overlap'), clash_target='OST_Walls')
    assert report['counts'] == dict(clash=0, overlap=1) and report['pairs'] == 1


def line(start=(0, 0, 0), end=(10, 0, 0), kind='curve:Line'):
    return (kind, tuple(a+(b-a)*t for t in (0, .25, .5, .75, 1) for a, b in zip(start, end)), ())


def test_collinear_lines_are_3d_bidirectional_and_respect_distance_tolerance():
    a = metadata(placement=line())
    b = metadata(placement=line((15, 0, 0), (5, 0, 0)))
    assert collinear_lines(a, b, 0)  # Different lengths, reverse direction, partial overlap.
    b['placement'] = line((15, 0, 1), (5, 0, 1))
    assert collinear_lines(a, b, 1)
    assert not collinear_lines(a, b, .999)
    b['placement'] = line((0, .8, .8), (10, .8, .8))
    assert not collinear_lines(a, b, 1)  # Euclidean distance, not axis-by-axis.
    a['placement'], b['placement'] = line((0, 0, 0), (3, 4, 5)), line((6, 8, 10), (0, 0, 0))
    assert collinear_lines(a, b, .001)


def test_crossing_offset_curved_unknown_or_degenerate_paths_are_not_skipped():
    a = metadata(placement=line())
    for placement in (line((5, -5, 0), (5, 5, 0)), line((0, 2, 0), (10, 2, 0)),
                      line((0, 0, 2), (10, 0, 2)), line(kind='curve:Arc'),
                      None, ('point', (0, 0, 0), ()), ('curve:Line', (), ()),
                      line((1, 1, 1), (1, 1, 1)), line(end=(float('nan'), 0, 0)),
                      line(end=(float('inf'), 0, 0)), line(end=(10, .001, 0))):
        assert not collinear_lines(a, metadata(placement=placement), 1), placement


def test_joined_clashes_default_to_skip_without_reading_solids_and_can_be_restored():
    adapter = ClashAdapter()
    adapter.joins.add(('a', 'wall'))
    original = adapter.geometry
    def broken(element):
        raise AssertionError('Exempt pair must not read solid geometry')
    adapter.geometry = broken
    report = run(adapter, ('clash',), clash_target='OST_Walls')
    assert report['status'] == 'CLEAR' and report['results'] == [] and adapter.closed
    assert report['skipped_joined'] == 1 and report['skipped_collinear'] == 0 and report['pairs'] == 1
    assert not adapter.created
    adapter.geometry = original
    report = run(adapter, ('clash',), clash_target='OST_Walls', clash_skip_joined=False)
    assert report['counts']['clash'] == 1 and report['skipped_joined'] == 0


def test_same_line_skip_applies_to_other_types_and_categories_and_is_optional():
    adapter = ClashAdapter()
    adapter.data['a']['placement'] = line()
    adapter.data['other']['placement'] = line((15, 0, 0), (5, 0, 0))
    adapter.data['wall']['placement'] = line((5, -5, 0), (5, 5, 0))
    report = run(adapter, ('clash',))
    assert report['skipped_collinear'] == 1 and report['counts']['clash'] == 1
    assert report['results'][0]['records'][1]['source_element_id'] == 'wall'
    report = run(adapter, ('clash',), clash_skip_collinear=False)
    assert report['skipped_collinear'] == 0 and report['counts']['clash'] == 2
    adapter.data['wall']['placement'] = line((0, 0, .75), (10, 0, .75))
    assert run(adapter, ('clash',), clash_line_mm=.5)['counts']['clash'] == 1
    report = run(adapter, ('clash',), clash_line_mm=1)
    assert report['skipped_collinear'] == 2 and report['findings'] == 0


def test_skips_count_each_pair_once_with_join_precedence_and_no_graph_findings():
    adapter = ClashAdapter()
    adapter.elements = adapter.targets = ['a', 'other']
    adapter.data['a']['placement'] = adapter.data['other']['placement'] = line()
    adapter.joins.add(('a', 'other'))
    report = run(adapter, ('clash', 'overlap'))
    assert report['status'] == 'CLEAR' and report['pairs'] == 1
    assert report['skipped_joined'] == 1 and report['skipped_collinear'] == 0
    report = run(adapter, ('clash', 'overlap'), clash_skip_joined=False)
    assert report['skipped_joined'] == 0 and report['skipped_collinear'] == 1
    assert report['counts'] == dict(clash=0, overlap=0)
    graph = dict(root='model:a', nodes=[node('a')], edges=[], warnings=[])
    report['graph_root'] = graph['root']
    assert with_check_overlay(graph, report) == graph
    assert run(adapter, ('clash',), max_pairs=0)['status'] == 'INCOMPLETE'


def test_skip_does_not_hide_same_type_duplicates_or_independent_overlap_checks():
    adapter = ClashAdapter()
    adapter.elements = adapter.targets = ['a', 'b']
    adapter.data['a']['placement'] = adapter.data['b']['placement'] = line()
    adapter.joins.add(('a', 'b'))
    report = run(adapter, ('clash', 'duplicate', 'overlap', 'mark'))
    assert report['counts'] == dict(clash=0, duplicate=1, overlap=1, mark=1)
    assert report['skipped_joined'] == report['skipped_collinear'] == 0
    adapter.data['b']['type'] = '101'
    assert run(adapter, ('overlap',))['counts']['overlap'] == 1


def test_failed_join_lookup_is_not_an_exemption_or_proof_of_clearance():
    adapter = ClashAdapter()
    calls = []
    def broken(a, b):
        calls.append((a, b))
        raise ValueError('Join state inaccessible')
    adapter.joined = broken
    report = run(adapter, ('clash',), clash_target='OST_Walls')
    assert report['counts']['clash'] == 1 and report['skipped_joined'] == 0
    assert len(report['warnings']) == 1 and calls == [('a', 'wall')]
    calls[:] = []
    adapter.shapes['wall'] = [Solid([3])]
    report = run(adapter, ('clash',), clash_target='OST_Walls')
    assert report['status'] == 'INCOMPLETE' and report['unknown'] == 1
    assert calls == [('a', 'wall')]  # Failure is cached even after a clean Boolean.


def test_empty_candidate_set_does_not_pass_unsupported_sources_but_collinear_pairs_need_no_solids():
    adapter = ClashAdapter()
    adapter.targets = []
    adapter.shapes['a'] = []
    assert run(adapter, ('clash',))['status'] == 'NOT_APPLICABLE'
    adapter.targets = ['wall']
    adapter.data['a']['placement'] = adapter.data['wall']['placement'] = line()
    adapter.shapes['wall'] = []
    report = run(adapter, ('clash',))
    assert report['status'] == 'CLEAR' and report['skipped_collinear'] == 1
    assert run(adapter, ('clash',), clash_skip_collinear=False)['status'] == 'NOT_APPLICABLE'
    for value in (-1, float('nan'), float('inf')):
        with pytest.raises(ValueError):
            run(adapter, ('clash',), clash_line_mm=value)


def test_cross_clash_limits_and_failed_candidate_reads_never_report_clear():
    assert run(ClashAdapter(), ('clash',), max_pairs=0)['status'] == 'INCOMPLETE'
    assert run(ClashAdapter(), ('clash',), max_results=1)['status'] == 'INCOMPLETE'
    assert run(ClashAdapter(), ('clash',), seconds=0)['status'] == 'INCOMPLETE'
    adapter = ClashAdapter()
    def broken(*args):
        raise ValueError('candidate collection failed')
    adapter.clash_candidates = broken
    assert run(adapter, ('clash',))['unknown'] == 1
    with pytest.raises(ValueError):
        run(ClashAdapter(), ('clash',), clash_target='INVALID')


def test_boundary_line_targets_are_excluded_before_reads_and_do_not_use_pair_budget():
    adapter = ClashAdapter()
    adapter.curves.add('room-separation')
    # No metadata, geometry or node exists for the separator: none should be read.
    adapter.targets = ['room-separation'] * 600 + ['wall']
    report = run(adapter, ('clash',), max_pairs=1)
    assert report['status'] == 'REVIEW' and report['counts']['clash'] == 1
    assert report['unknown'] == 0 and report['pairs'] == 1 and not report['truncated']
    assert report['excluded_curve_elements'] == 1 and report['excluded_curve_sources'] == 0
    assert report['results'][0]['records'][1]['source_element_id'] == 'wall'
    graph = dict(root='model:a', nodes=[node('a')], edges=[], warnings=[])
    report['graph_root'] = graph['root']
    assert all(n['source_element_id'] != 'room-separation' for n in with_check_overlay(graph, report)['nodes'])
    # Empty objects are omitted from collision rows as requested, even if named wall.
    adapter.shapes['wall'] = []
    report = run(adapter, ('clash',), max_pairs=1)
    assert report['status'] == 'CLEAR' and report['unknown'] == 0
    assert report['excluded_nonsolid_elements'] == 1 and not report['results']


def test_collision_rows_require_solids_on_both_sides_and_geometry_is_cached():
    adapter = ClashAdapter()
    adapter.elements, adapter.targets = ['a', 'other', 'wall'], ['a', 'other', 'wall']
    adapter.shapes['other'] = []
    original = adapter.geometry
    reads = []
    def geometry(element):
        reads.append(element)
        return original(element)
    adapter.geometry = geometry
    report = run(adapter, ('clash', 'overlap'))
    assert report['excluded_nonsolid_elements'] == report['excluded_nonsolid_sources'] == 1
    assert reads.count('other') == 1
    assert report['findings'] == 1 and report['unknown'] == 0
    assert all(adapter.shapes[n['source_element_id']] for r in report['results'] for n in r['records'])
    report['graph_root'] = 'model:a'
    graph = with_check_overlay(dict(root='model:a', nodes=[node('a')], edges=[]), report)
    assert all(n['source_element_id'] != 'other' for n in graph['nodes'])


def test_unreadable_or_mesh_only_geometry_is_incomplete_without_nonsolid_collision_rows():
    for failure in ('mesh', 'exception'):
        adapter = ClashAdapter()
        adapter.targets = ['wall', 'wall']
        original = adapter.geometry
        def geometry(element):
            if element != 'wall':
                return original(element)
            if failure == 'exception':
                raise ValueError('Cannot read geometry')
            return dict(solids=[], problems=['Mesh only'])
        adapter.geometry = geometry
        report = run(adapter, ('clash',))
        assert report['status'] == 'INCOMPLETE' and report['geometry_failures'] == 1
        assert report['warnings'] and report['unknown'] == 0 and not report['results']


def test_empty_geometry_metadata_failure_is_filtered_and_noncollision_checks_still_work():
    adapter = Adapter()
    adapter.shapes = dict(a=[], b=[])
    report = run(adapter)
    assert report['counts'] == dict(duplicate=1, overlap=0, mark=1, warning=1)
    assert report['excluded_nonsolid_elements'] == report['excluded_geometry_sources'] == 2
    assert run(adapter, ('overlap',))['status'] == 'NOT_APPLICABLE'
    adapter = ClashAdapter()
    adapter.shapes['a'] = []
    adapter.data['a']['bounds'] = None
    report = run(adapter, ('clash',))
    assert report['status'] == 'NOT_APPLICABLE' and not report['results']


def test_curve_sources_are_not_applicable_to_clashes_and_other_checks_still_run():
    adapter = ClashAdapter()
    adapter.elements = ['room-separation']
    adapter.curves.add('room-separation')
    report = run(adapter, ('clash',))
    assert report['status'] == 'NOT_APPLICABLE' and report['unknown'] == report['pairs'] == 0
    assert report['excluded_curve_elements'] == report['excluded_curve_sources'] == 1 and adapter.closed
    adapter = ClashAdapter()
    adapter.elements = ['a', 'b']
    adapter.curves.update(adapter.elements)
    report = run(adapter, ('duplicate', 'clash', 'mark', 'warning'))
    assert report['counts'] == dict(duplicate=1, clash=0, mark=1, warning=1)
    assert report['excluded_curve_elements'] == report['excluded_curve_sources'] == 2


def test_room_targets_are_skipped_before_metadata_geometry_and_pair_budget():
    adapter = ClashAdapter()
    adapter.rooms.add('room')
    # A Room intentionally has no metadata/geometry fixture: neither may be read.
    adapter.targets = ['room'] * 600 + ['wall']
    report = run(adapter, ('clash',), max_pairs=1)
    assert report['status'] == 'REVIEW' and report['counts']['clash'] == 1
    assert report['pairs'] == 1 and not report['truncated'] and report['unknown'] == 0
    assert report['excluded_room_elements'] == 1 and report['excluded_room_sources'] == 0
    assert report['excluded_nonsolid_elements'] == report['geometry_failures'] == 0
    assert all(n['source_element_id'] != 'room' for r in report['results'] for n in r['records'])
    report['graph_root'] = 'model:a'
    graph = with_check_overlay(dict(root='model:a', nodes=[node('a')], edges=[]), report)
    assert all(n['source_element_id'] != 'room' for n in graph['nodes'])


def test_room_sources_are_not_applicable_to_collision_or_overlap_and_other_checks_remain():
    adapter = ClashAdapter()
    adapter.elements, adapter.targets = ['room'], ['wall']
    adapter.rooms.add('room')
    for checks in [('clash',), ('overlap',), ('clash', 'overlap')]:
        report = run(adapter, checks)
        assert report['status'] == 'NOT_APPLICABLE' and not report['results']
        assert report['pairs'] == report['geometry_failures'] == 0
        assert report['excluded_room_elements'] == report['excluded_room_sources'] == report['excluded_geometry_sources'] == 1
    adapter = Adapter()
    adapter.rooms.update(adapter.elements)
    adapter.shapes.clear()  # Rooms are still skipped even if geometry would fail.
    report = run(adapter, ('duplicate', 'overlap', 'clash', 'mark', 'warning'))
    assert report['counts'] == dict(duplicate=1, overlap=0, clash=0, mark=1, warning=1)
    assert report['excluded_room_elements'] == report['excluded_room_sources'] == 2


def test_room_counts_deduplicate_both_sides_and_respect_custom_selection():
    adapter = ClashAdapter()
    adapter.elements = ['a', 'room']
    adapter.targets = ['room', 'wall']
    adapter.rooms.add('room')
    report = run(adapter, ('clash',), element_keys=['a', 'room', 'wall'])
    assert report['counts']['clash'] == 1
    assert report['excluded_room_elements'] == report['excluded_room_sources'] == 1
    report = run(adapter, ('clash',), element_keys=['a', 'wall'])
    assert report['excluded_room_elements'] == 0 and report['counts']['clash'] == 1


def test_native_room_exclusion_uses_category_identity_instead_of_display_name():
    adapter = object.__new__(RevitCheckAdapter)
    adapter.DB = SimpleNamespace(BuiltInCategory=SimpleNamespace(OST_Rooms=-2000160))
    def element(category_id, name):
        return SimpleNamespace(Category=SimpleNamespace(Id=SimpleNamespace(IntegerValue=category_id)), Name=name)
    assert adapter.is_clash_room(element(-2000160, '客廳 1'))
    assert adapter.is_clash_room(element(-2000160, 'Room 101'))
    assert not adapter.is_clash_room(element(-2000011, 'Room'))
    assert not adapter.is_clash_room(element(-2000480, '房間分隔線'))
    assert not adapter.is_clash_room(SimpleNamespace(Category=None, Name='Room'))


def test_curve_exclusion_count_deduplicates_sources_and_targets():
    adapter = ClashAdapter()
    adapter.elements = ['a', 'room-separation', 'other']
    adapter.curves.add('room-separation')
    adapter.targets = ['room-separation', 'wall']
    report = run(adapter, ('clash',))
    assert report['excluded_curve_elements'] == report['excluded_curve_sources'] == 1
    assert report['counts']['clash'] == 1 and report['pairs'] == 2 and report['unknown'] == 0


def test_native_curve_exclusion_uses_revit_element_class_not_names_or_location_curve():
    class CurveElement(SimpleNamespace):
        pass
    class ModelLine(CurveElement):
        pass
    class ModelArc(CurveElement):
        pass
    adapter = object.__new__(RevitCheckAdapter)
    adapter.DB = SimpleNamespace(CurveElement=CurveElement)
    assert adapter.is_clash_curve(ModelLine(Name='房間分隔線', Category='Room Separation'))
    assert adapter.is_clash_curve(ModelLine(Name='Space Separator'))
    assert adapter.is_clash_curve(ModelArc(Name='面積邊界'))
    for name in ('RC 牆 20cm', 'Pipe', 'Beam', 'Model Lines', 'Room Separation'):
        assert not adapter.is_clash_curve(SimpleNamespace(Name=name, Location='LocationCurve'))


def test_custom_range_limits_both_sources_and_targets_before_pair_budget():
    adapter = ClashAdapter()
    adapter.elements = ['a', 'other', 'wall']
    report = run(adapter, ('clash',), element_keys=['a', 'wall'], max_pairs=1)
    assert report['sources'] == 2 and report['pairs'] == 1 and report['counts']['clash'] == 1
    assert report['range_mode'] == 'selected' and report['range_count'] == 2
    assert not report['truncated']
    assert {n['source_element_id'] for r in report['results'] for n in r['records']} == {'a', 'wall'}
    report = run(adapter, ('clash',), element_keys=['a'])
    assert report['sources'] == 1 and report['pairs'] == report['findings'] == 0
    assert run(adapter, ('clash',), element_keys=['not-in-view'])['status'] == 'NO_MATCH'
    with pytest.raises(ValueError, match='自訂範圍'):
        run(adapter, ('clash',), element_keys=[])


def test_custom_range_keeps_marks_and_native_warning_navigation_inside_selection():
    report = run(Adapter(), element_keys=['a'])
    assert report['counts'] == dict(duplicate=0, overlap=0, mark=0, warning=1)
    assert [n['source_element_id'] for n in report['results'][0]['records']] == ['a']
    assert '2 個關聯元素不在本次範圍' in report['results'][0]['message']


def test_native_cross_candidate_filter_uses_active_view_category_and_model_bounds():
    class Link(SimpleNamespace):
        pass

    def element(uid, category=1, view=7, x=.5, model=True, linked=False):
        cls = Link if linked else SimpleNamespace
        return cls(Id=uid, view=view, x=x, Category=SimpleNamespace(Id=category, CategoryType=1 if model else 2))

    source = element('source')
    model = [source, element('same'), element('wall', -2000011), element('hidden', -2000011, view=8),
             element('far', -2000011, x=20), element('annotation', model=False), element('link', linked=True)]
    xyz = lambda x, y, z: SimpleNamespace(X=x, Y=y, Z=z)
    outlines = []

    def outline(low, high):
        outlines.append((low, high))
        return low, high

    class Collector:
        def __init__(self, doc, view):
            assert doc is model and view == 7
            self.items = [e for e in doc if e.view == view]
        def WhereElementIsNotElementType(self):
            return self
        def OfCategory(self, category):
            self.items = [e for e in self.items if e.Category.Id == category]
            return self
        def WherePasses(self, predicate):
            self.items = list(filter(predicate, self.items))
            return self
        def __iter__(self):
            return iter(self.items)
        def Dispose(self):
            pass

    adapter = object.__new__(RevitCheckAdapter)
    adapter.DB = SimpleNamespace(FilteredElementCollector=Collector, XYZ=xyz, Outline=outline,
        BuiltInCategory=SimpleNamespace(OST_Walls=-2000011), CategoryType=SimpleNamespace(Model=1), RevitLinkInstance=Link,
        BoundingBoxIntersectsFilter=lambda box: lambda e: box[0].X <= e.x <= box[1].X)
    adapter.doc, adapter.view, adapter.owners = model, SimpleNamespace(Id=7), []
    bounds = (0, 0, 0, 304.8, 304.8, 304.8)
    assert [e.Id for e in adapter.clash_candidates(source, bounds, 'ALL')] == ['same', 'wall']
    assert [e.Id for e in adapter.clash_candidates(source, bounds, 'OST_Walls')] == ['wall']
    assert [e.Id for e in adapter.sources(dict(scope='all'))] == ['source', 'same', 'wall', 'far']
    assert outlines[0][1].X == pytest.approx(1, abs=1e-5)  # mm back to Revit feet.
    adapter.close()


def test_selected_sources_compare_duplicates_and_overlap_with_unselected_neighbours():
    # Sources-only comparison cannot see a selected element's unselected copy.
    assert run(ClashAdapter(), ('duplicate', 'overlap'))['findings'] == 0
    report = run(ClashAdapter(), ('duplicate', 'overlap'), peers=True)
    pairs = sorted((r['check'], tuple(n['source_element_id'] for n in r['records'])) for r in report['results'])
    # b is the same type (duplicate + overlap); other is the same category; the wall is another category.
    assert pairs == [('duplicate', ('a', 'b')), ('overlap', ('a', 'b')), ('overlap', ('a', 'other'))]
    assert report['sources'] == 1
    both = ClashAdapter()
    both.elements = ['a', 'b']
    report = run(both, ('duplicate', 'overlap'), peers=True)
    ab = [r for r in report['results'] if {n['source_element_id'] for n in r['records']} == {'a', 'b'}]
    assert sorted(r['check'] for r in ab) == ['duplicate', 'overlap']  # Selected neighbours meet once.
    limited = run(ClashAdapter(), ('duplicate', 'overlap'), peers=True, element_keys=['a', 'b'])
    assert {n['source_element_id'] for r in limited['results'] for n in r['records']} == {'a', 'b'}


def test_selection_scope_reads_selected_model_elements_in_the_view(monkeypatch):
    class Link:
        pass

    def element(key, view=7, model=True):
        return SimpleNamespace(Id=key, UniqueId='uid-'+key, view=view,
                               Category=SimpleNamespace(CategoryType=1 if model else 2))
    items = [element('a'), element('b'), element('hidden', view=8), element('tag', model=False), element('c')]

    class Doc(list):
        def GetElement(self, key):
            return next((e for e in self if e.UniqueId == key), None)
    doc = Doc(items)

    class Collector:
        def __init__(self, document, view):
            self.items = [e for e in document if e.view == view]
        def WhereElementIsNotElementType(self):
            return self
        def WherePasses(self, predicate):
            self.items = list(filter(predicate, self.items))
            return self
        def __iter__(self):
            return iter(self.items)
        def Dispose(self):
            pass
    monkeypatch.setitem(sys.modules, 'System', SimpleNamespace())
    monkeypatch.setitem(sys.modules, 'System.Collections', SimpleNamespace())
    monkeypatch.setitem(sys.modules, 'System.Collections.Generic', SimpleNamespace(List={None: list}))
    adapter = object.__new__(RevitCheckAdapter)
    adapter.DB = SimpleNamespace(FilteredElementCollector=Collector, CategoryType=SimpleNamespace(Model=1), RevitLinkInstance=Link,
                                 ElementId=None, ElementIdSetFilter=lambda ids: lambda e: e.Id in ids)
    adapter.doc, adapter.view, adapter.owners = doc, SimpleNamespace(Id=7), []
    scope = dict(scope='selection', keys=['uid-a', 'uid-hidden', 'uid-tag', 'uid-gone'])
    # Deleted keys drop out; elements outside this view and annotations are not sources.
    assert [e.Id for e in adapter.sources(scope)] == ['a']
    assert list(adapter.sources(dict(scope='selection', keys=['uid-gone']))) == []


def test_interrupted_check_keeps_its_traceback_for_the_log():
    class Broken(Adapter):
        def sources(self, scope):
            raise TypeError('Unable to cast FieldExpression')
            yield
    report = run(Broken())
    assert report['truncated'] and report['status'] == 'INCOMPLETE'
    assert report['warnings'][0] == '檢查未完成：Unable to cast FieldExpression'
    assert 'Traceback' in report['error_trace'] and 'TypeError' in report['error_trace']
    assert 'error_trace' not in run()
