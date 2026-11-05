"""SpatialStrategy against a minimal fake Revit API: only model elements, intersections first."""
import importlib
import sys
from pathlib import Path
from types import ModuleType, SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))

MODEL, ANNOTATION = 'Model', 'Annotation'


class Box(object):
    def __init__(self, x):
        self.Min, self.Max = SimpleNamespace(X=x, Y=0., Z=0.), SimpleNamespace(X=x+1., Y=1., Z=1.)


class Element(object):
    def __init__(self, ident, x, kind=MODEL, view_specific=False, intersects=False):
        self.Id, self.box, self.ViewSpecific, self.intersects = ident, Box(x), view_specific, intersects
        self.Category = SimpleNamespace(CategoryType=kind) if kind else None

    def get_BoundingBox(self, view):
        return self.box


class Link(Element):
    pass


class Collector(object):
    def __init__(self, doc):
        self.doc, self.precise = doc, False

    def WhereElementIsNotElementType(self):
        return self

    def WherePasses(self, value):
        self.precise = self.precise or isinstance(value, IntersectsFilter)
        return self

    def __iter__(self):
        return iter([e for e in self.doc.elements if e.intersects or not self.precise])


class IntersectsFilter(object):
    def __init__(self, primary):
        if getattr(primary, 'no_solid', False):
            raise ValueError('element has no solid')


@pytest.fixture
def strategies(monkeypatch):
    db = ModuleType('Autodesk.Revit.DB')
    db.__dict__.update(FilteredElementCollector=Collector, ElementIntersectsElementFilter=IntersectsFilter,
                       BoundingBoxIntersectsFilter=lambda outline: outline, Outline=lambda a, b: (a, b),
                       XYZ=lambda x, y, z: (x, y, z), RevitLinkInstance=Link, CategoryType=SimpleNamespace(Model=MODEL))
    monkeypatch.setitem(sys.modules, 'Autodesk', ModuleType('Autodesk'))
    monkeypatch.setitem(sys.modules, 'Autodesk.Revit', SimpleNamespace(DB=db))
    monkeypatch.setitem(sys.modules, 'Autodesk.Revit.DB', db)
    sys.modules.pop('casegraph.strategies', None)
    yield importlib.import_module('casegraph.strategies')
    sys.modules.pop('casegraph.strategies', None)


class Context(object):
    def __init__(self, elements, primary, limit=80):
        self.doc, self.primary, self.limit = SimpleNamespace(elements=elements), primary, limit
        self.data = dict(nodes=[dict(properties=dict(bounding_box=dict(min=dict(x=0, y=0, z=0), max=dict(x=1, y=1, z=1))))], warnings=[])
        self.added = []

    def add(self, source, target, relation, method, verified=True):
        self.added.append((target.Id, relation, verified))


def test_clash_keeps_intersections_and_only_the_nearest_model_neighbours(strategies):
    primary = Element('door', 0.)
    noise = [Element('tag%d' % i, 1., kind=ANNOTATION) for i in range(30)]
    noise += [Element('detail', 1., view_specific=True), Element('nocat', 1., kind=None), Link('link', 1.), primary]
    # Intersections listed last in iteration order still come first.
    neighbours = [Element('n%02d' % i, 50.-i) for i in range(50)]
    hits = [Element('wall', 0., intersects=True), Element('pipe', .5, intersects=True)]
    context = Context(noise + neighbours + hits, primary)
    strategies.SpatialStrategy(verify=True).extract(context)
    assert context.added[:2] == [('wall', 'INTERSECTS', True), ('pipe', 'INTERSECTS', True)]
    nearby = [a for a in context.added if a[1] == 'NEARBY']
    assert len(nearby) == strategies.NEARBY_LIMIT and all(not verified for _, _, verified in nearby)
    # The 20 nearest of the 50 neighbours: n49 (x=1) ... n30 (x=20).
    assert [a[0] for a in nearby] == ['n%02d' % i for i in range(49, 29, -1)]
    assert not any(a[0].startswith(('tag', 'detail', 'nocat', 'link', 'door')) for a in context.added)
    assert context.data['warnings'] == ['1000 mm 範圍內另有 30 個元件，只列入最近的 20 個。']


def test_small_neighbourhood_has_no_notes_and_clearance_keeps_its_review_note(strategies):
    primary = Element('door', 0.)
    context = Context([Element('wall', 2.), Element('tag', 1., kind=ANNOTATION)], primary)
    strategies.SpatialStrategy(verify=True).extract(context)
    assert context.added == [('wall', 'NEARBY', False)] and context.data['warnings'] == []
    clearance = Context([Element('wall', 2.)], primary)
    strategies.SpatialStrategy().extract(clearance)
    assert clearance.data['warnings'] == ['門扇開啟範圍與所需淨空需人工確認。']


def test_elements_without_solids_fall_back_to_nearby(strategies):
    primary = Element('line', 0.)
    primary.no_solid = True
    context = Context([Element('wall', 2., intersects=True)], primary)
    strategies.SpatialStrategy(verify=True).extract(context)
    assert context.added == [('wall', 'NEARBY', False)]
    assert context.data['warnings'] == ['部分元件無法判定是否相交，已改列為鄰近元件。']
