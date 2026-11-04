"""Real extractor -> shaft issue -> API submission; only Revit objects are simulated."""
import importlib.util
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace

import pytest

from test_element_search import client
from test_shaft_issue import sample
from casegraph.shaft_issue import shaft_issue


def eid(value):
    return SimpleNamespace(IntegerValue=value)


class Element:
    def __init__(self, number, name, category='Walls'):
        self.Id, self.UniqueId, self.Name = eid(number), 'element-uid-{}'.format(number), name
        self.Category = SimpleNamespace(Name=category, Id=eid(-2000160 if category == 'Rooms' else -2000011))
        self.Parameters = []
        self.LevelId = eid(10)

    def GetTypeId(self):
        return eid(-1)

    def LookupParameter(self, name):
        return None

    def get_BoundingBox(self, view):
        return None


class SpatialElement(Element):
    pass


class Room(SpatialElement):
    pass


class Space(SpatialElement):
    pass


class Level(Element):
    pass


@pytest.fixture
def extractor(monkeypatch):
    db = SimpleNamespace(Level=Level, SpatialElement=SpatialElement,
                         Material=type('Material', (), {}), MEPSystem=type('MEPSystem', (), {}),
                         FamilyInstance=type('FamilyInstance', (), {}),
                         Element=SimpleNamespace(Name=SimpleNamespace(GetValue=lambda e: e.Name)),
                         Document=SimpleNamespace(GetDocumentVersion=lambda doc: SimpleNamespace(VersionGUID='version')))
    monkeypatch.setitem(sys.modules, 'Autodesk', ModuleType('Autodesk'))
    monkeypatch.setitem(sys.modules, 'Autodesk.Revit', SimpleNamespace(DB=db))
    # Geometry is outside this regression; run the actual node/context/extract code.
    monkeypatch.setitem(sys.modules, 'casegraph.strategies', SimpleNamespace(
        strategy_for=lambda kind: SimpleNamespace(extract=lambda context: None)))
    source = Path(__file__).resolve().parents[2] / 'CaseBase.extension/lib/casegraph/extractor.py'
    spec = importlib.util.spec_from_file_location('spatial_case_extractor', source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def scene(kind):
    primary = kind(1, 'PS-102', 'Rooms' if kind is Room else 'MEP Spaces')
    related = Room(2, 'Adjacent room', 'Rooms')
    wall = Element(3, 'Fire wall')
    level = Level(10, 'Level 1', 'Levels')
    items = {e.Id.IntegerValue: e for e in (primary, related, wall, level)}
    doc = SimpleNamespace(Title='Test model', PathName='test-model.rvt',
                          GetFingerprintGUID=lambda: 'test-model', GetElement=lambda number: items.get(number.IntegerValue))
    return doc, primary, related, wall


@pytest.mark.parametrize('kind', [Room, Space])
def test_spatial_target_saves_submits_and_reopens_with_original_identity(extractor, client, kind):
    doc, primary, related, wall = scene(kind)
    graph = extractor.extract(doc, primary, 'DESIGN_CONFLICT', related=[related],
                              findings=[(wall, 'Shaft check A1', False, {'result_id': '1'})])
    by_id = {n['source_element_id']: n for n in graph['nodes']}
    root = by_id[primary.UniqueId]
    assert root['node_type'] == 'ELEMENT' and root['properties']['revit_node_type'] == 'SPACE'
    assert root['source_element_id'] == primary.UniqueId and root['properties']['element_id'] == '1'
    assert root['properties']['category'] == primary.Category.Name
    assert by_id[related.UniqueId]['node_type'] == 'SPACE'
    assert by_id['element-uid-10']['node_type'] == 'LEVEL'
    assert extractor.node(doc, primary)['node_type'] == 'SPACE'  # Context semantics are preserved.
    report, row = sample()
    report['document'] = root['source_document']
    row.update(records=[root, by_id[wall.UniqueId]], room_unique_id=primary.UniqueId,
               shaft_key=primary.UniqueId, source_refs=[], unresolved=False)
    record = shaft_issue(report, row)
    draft = {key: record[key] for key in ('title', 'description', 'notes', 'priority', 'issue_type', 'origin')}
    draft['graph'] = graph
    response = client.post('/graph/elements', json=root)
    assert response.status_code == 201, response.text
    response = client.post('/graph/cases', json=draft)
    assert response.status_code == 201, response.text
    case_id = response.json()['id']
    response = client.post('/graph/cases/{}/status'.format(case_id), json={'status': 'REPORTED'})
    assert response.status_code == 200 and response.json()['status'] == 'REPORTED', response.text
    saved = client.get('/graph/cases/{}'.format(case_id)).json()
    saved_root = next(n for n in saved['draft']['graph']['nodes'] if n['node_id'] == graph['root'])
    assert saved_root == root
    assert saved['draft']['origin']['elements'][0]['unique_id'] == primary.UniqueId
    assert any(e['target'] == by_id[wall.UniqueId]['node_id'] for e in saved['draft']['graph']['edges'])
    response = client.post('/graph/cases/{}/status'.format(case_id), json={'status': 'UNDER_REVIEW'})
    assert response.status_code == 200, response.text


def test_missing_persistent_identity_is_still_rejected(client):
    # The API still guards persistent identity; do not weaken it to accept arbitrary roots.
    root = dict(node_id='model:missing', node_type='ELEMENT', name='Missing identity',
                source_document='model', source_element_id=None, properties={'revit_node_type': 'SPACE'})
    draft = dict(title='Invalid room', description='No identity', issue_type='DESIGN_CONFLICT',
                 graph=dict(root=root['node_id'], nodes=[root], edges=[]))
    assert client.post('/graph/cases', json=draft).status_code == 422
