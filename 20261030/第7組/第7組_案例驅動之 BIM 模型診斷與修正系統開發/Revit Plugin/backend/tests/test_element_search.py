import copy
import pytest
from fastapi.testclient import TestClient
from app.main import app
from app import database


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(database,'DATA_DIR',tmp_path)
    monkeypatch.setattr(database,'DATABASE_PATH',tmp_path/'test.db')
    with TestClient(app) as c:
        yield c


def element(uid='target', document='model-a', type_name='900 x 2100', width=900):
    return dict(node_id=document+':'+uid,node_type='ELEMENT',name='Door '+uid,source_document=document,source_element_id=uid,
        properties=dict(category='Doors',category_id='-2000014',family='Single swing',type_name=type_name,
                        element_id=uid,dimensions={'Width':width,'Height':2100},units='mm'))


def issue(target):
    return dict(title='Clearance issue',description='Door swing conflict',issue_type='SPATIAL_CLEARANCE',graph=dict(root=target['node_id'],nodes=[target]))


def test_finds_live_elements_without_any_cases_or_records(client):
    target, exact, different = element(),element('exact'),element('different',type_name='1200 x 2100',width=1200)
    r=client.post('/graph/elements/search',json=dict(target=target,candidates=[different,target,exact]))
    assert r.status_code==200
    body=r.json()
    assert [x['element']['source_element_id'] for x in body['results']]==['exact','different']
    assert body['results'][0]['score']==1
    assert body['results'][1]['score']<1
    assert body['results'][1]['differences']
    assert body['compared_count']==2
    assert client.get('/graph/cases').json()==[]


def test_detail_items_and_localized_categories_are_supported(client):
    target,other=element(),element('detail')
    target['properties'].update(category='Detail Items',category_id='-2002000')
    other['properties'].update(category='詳圖項目',category_id='-2002000')
    r=client.post('/graph/elements/search',json=dict(target=target,candidates=[other,element('door')])).json()
    assert len(r['results'])==1
    assert r['results'][0]['score']==1


def test_document_scoped_self_exclusion_and_live_overrides_snapshot(client):
    target=element()
    stale=element('other',type_name='old',width=1000)
    client.post('/graph/elements',json=stale)
    cross=element('target',document='model-b')
    client.post('/graph/elements',json=cross)
    fresh=element('other')
    body=client.post('/graph/elements/search',json=dict(target=target,candidates=[target,fresh,fresh],include_recorded=True)).json()
    assert len(body['results'])==2
    assert all(r['score']==1 for r in body['results'])
    assert next(r for r in body['results'] if r['element']['source_element_id']=='other')['source']=='model'


def test_units_missing_features_and_threshold_explanations(client):
    target=element()
    metric=element('metric')
    metric['properties'].update(dimensions={'Width':.9,'Height':2.1},units='m')
    r=client.post('/graph/elements/search',json=dict(target=target,candidates=[metric])).json()['results'][0]
    assert r['score']==1
    assert r['components']['parameters'] is None
    assert r['completeness']==.9
    other=element('different',type_name='different',width=1200)
    body=client.post('/graph/elements/search',json=dict(target=target,candidates=[other],minimum_score=.99)).json()
    assert not body['results']
    assert '門檻' in body['message']
    empty=client.post('/graph/elements/search',json=dict(target=target,scope='recorded')).json()
    assert '尚未記錄' in empty['message']


def test_candidate_limits_are_visible(client):
    body=client.post('/graph/elements/search',json=dict(target=element(),candidates=[element('a'),element('b')],limit=1,truncated=True)).json()
    assert body['matched_count']==2
    assert len(body['results'])==1
    assert body['truncated']
    assert '上限' in body['message']
    assert client.post('/graph/elements/search',json=dict(target=element(),candidates=[element('a')]*501)).status_code==422


def test_related_cases_can_be_browsed_before_verification(client):
    target=element()
    case_id=client.post('/graph/cases',json=issue(element('historical'))).json()['id']
    result=client.post('/graph/cases/related',json=dict(target=target,issue_type='SPATIAL_CLEARANCE')).json()['results'][0]
    assert result['case_id']==case_id
    assert result['status']=='DRAFT'
    assert not result['reliable_solution']
    assert result['solution']==''
    assert client.post('/graph/cases/related',json=dict(target=target,issue_type='SPATIAL_CLEARANCE',verified_only=True)).json()['results']==[]
    assert client.post('/graph/cases/related',json=dict(target=target,issue_type='SPATIAL_CLEARANCE',exclude_case_id=case_id)).json()['results']==[]
    # Browsing unverified cases must never promote them into the trusted LLM case base.
    assert client.post('/graph/search',json=dict(draft=issue(target))).json()==[]
    for status,extras in [('REPORTED',{}),('RESOLVED',{'solution':'Unreviewed suggestion'})]:
        assert client.post('/graph/cases/{}/status'.format(case_id),json=dict(status=status,**extras)).status_code==200
    result=client.post('/graph/cases/related',json=dict(target=target,issue_type='SPATIAL_CLEARANCE')).json()['results'][0]
    assert result['solution']==''
    assert result['resolution_note']=='Unreviewed suggestion'
    assert client.post('/graph/cases/{}/status'.format(case_id),json=dict(status='VERIFIED',reviewer='Coordinator')).status_code==200
    result=client.post('/graph/cases/related',json=dict(target=target,issue_type='SPATIAL_CLEARANCE',verified_only=True)).json()['results'][0]
    assert result['reliable_solution']
    assert result['solution']=='Unreviewed suggestion'
    client.post('/graph/cases/{}/status'.format(case_id),json=dict(status='ARCHIVED'))
    assert client.post('/graph/cases/related',json=dict(target=target,issue_type='SPATIAL_CLEARANCE')).json()['results']==[]
