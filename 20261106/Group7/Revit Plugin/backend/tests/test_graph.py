"""Exercise evidence integrity, revision history and human review boundaries."""
import copy
from fastapi.testclient import TestClient
from app.main import app


def draft():
    return dict(title='Door clearance', description='Door swing conflicts with equipment',
        issue_type='SPATIAL_CLEARANCE', graph=dict(root='doc:door', nodes=[
            dict(node_id='doc:door', name='Door', source_document='doc', source_element_id='door',
                 properties=dict(category='Doors', family='Single swing', type_name='900 x 2100', type_id='100', dimensions={'Width': 900}, units='mm')),
            dict(node_id='doc:level', node_type='LEVEL', name='Level 2', source_document='doc')],
            edges=[dict(source='doc:door', target='doc:level', relationship_type='LOCATED_ON_LEVEL',
                        evidence='Revit LevelId', validation_status='verified')]))


def test_graph_draft_revisions_and_lifecycle():
    with TestClient(app) as client:
        original = draft()
        created = client.post('/graph/cases', json=original)
        assert created.status_code == 201
        case_id = created.json()['id']
        assert created.json()['status'] == 'DRAFT'
        assert client.post(f'/graph/cases/{case_id}/analyze').status_code == 409
        updated = copy.deepcopy(original)
        updated['description'] = 'Updated issue description'
        assert client.put(f'/graph/cases/{case_id}?revision=1', json=updated).json()['revision'] == 2
        assert client.put(f'/graph/cases/{case_id}?revision=1', json=updated).status_code == 409
        assert client.get(f'/graph/cases/{case_id}/revisions/1').json()['description'] == original['description']
        assert client.post(f'/graph/cases/{case_id}/status', json={'status':'VERIFIED'}).status_code == 409
        assert client.post(f'/graph/cases/{case_id}/status', json={'status':'REPORTED'}).status_code == 200
        assert client.put(f'/graph/cases/{case_id}?revision=2', json=updated).status_code == 409
        result = client.post(f'/graph/cases/{case_id}/analyze')
        assert result.status_code == 200
        assert result.json()['requires_review']
        assert client.get(f'/graph/cases/{case_id}').json()['analyses']
        assert client.post(f'/graph/cases/{case_id}/status', json={'status':'RESOLVED'}).status_code == 422
        assert client.post(f'/graph/cases/{case_id}/status', json={'status':'RESOLVED', 'solution':'Adjust handing'}).status_code == 200
        assert client.post(f'/graph/cases/{case_id}/status', json={'status':'VERIFIED'}).status_code == 422
        assert client.post(f'/graph/cases/{case_id}/status', json={'status':'VERIFIED', 'reviewer':'Coordinator'}).status_code == 200
        assert len(client.get(f'/graph/cases/{case_id}').json()['history']) == 3


def test_verified_retrieval_and_missing_features():
    with TestClient(app) as client:
        data = draft()
        # Use a distinct category to avoid order dependencies with other tests.
        data['graph']['nodes'][0]['properties']['category'] = 'Test doors'
        case_id = client.post('/graph/cases', json=data).json()['id']
        search = lambda: client.post('/graph/search', json={'draft':data})
        assert search().json() == []
        for status, extra in [('REPORTED', {}), ('RESOLVED', {'solution':'Rehand door'}), ('VERIFIED', {'reviewer':'Reviewer'})]:
            assert client.post(f'/graph/cases/{case_id}/status', json=dict(status=status, **extra)).status_code == 200
        match = search().json()[0]
        assert match['score'] == 1
        assert match['components']['relationship'] is None
        assert 0 < match['completeness'] < 1
        assert match['evidence']
        assert client.get(f'/graph/cases/{case_id}/graph?depth=0').json()['nodes'][0]['node_id'] == 'doc:door'
        assert len(client.get(f'/graph/cases/{case_id}/graph?depth=0').json()['nodes']) == 1
        assert client.post(f'/graph/cases/{case_id}/status', json={'status':'ARCHIVED'}).status_code == 200
        assert search().json() == []


def test_invalid_evidence_and_weights_are_rejected():
    with TestClient(app) as client:
        data = draft()
        data['graph']['edges'][0].update(relationship_type='INTERSECTS', validation_status='candidate')
        assert client.post('/graph/cases', json=data).status_code == 422
        data = draft()
        data['graph']['edges'][0]['target'] = 'missing-node'
        assert client.post('/graph/cases', json=data).status_code == 422
        assert client.post('/graph/search', json={'draft':draft(), 'weights':{'element':1}}).status_code == 422


def test_record_identity_includes_document():
    with TestClient(app) as client:
        element = draft()['graph']['nodes'][0]
        for document in ['project-a', 'project-b']:
            element['source_document'] = document
            assert client.post('/graph/elements', json=element).status_code == 201
        assert len(client.get('/graph/elements?document=project-a').json()) == 1
        client.delete('/graph/elements?document=project-a&unique_id=door')
        assert client.get('/graph/elements?document=project-a').json() == []
        assert len(client.get('/graph/elements?document=project-b').json()) == 1


def test_llm_failure_preserves_case(monkeypatch):
    from app import graph_api
    monkeypatch.setenv('CASEGRAPH_LLM_URL', 'http://127.0.0.1:11434/v1/chat/completions')
    def fail(*args, **kwargs):
        raise OSError('offline')
    monkeypatch.setattr(graph_api, 'urlopen', fail)
    with TestClient(app) as client:
        case_id = client.post('/graph/cases', json=draft()).json()['id']
        client.post(f'/graph/cases/{case_id}/status', json={'status':'REPORTED'})
        assert client.post(f'/graph/cases/{case_id}/analyze').status_code == 502
        case = client.get(f'/graph/cases/{case_id}').json()
        assert case['status'] == 'REPORTED'
        assert case['revision'] == 1
        assert case['analyses'] == []
