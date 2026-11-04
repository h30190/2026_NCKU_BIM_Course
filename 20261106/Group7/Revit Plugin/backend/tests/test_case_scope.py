import copy
import json
import pytest
from app.case_scope import type_match
from app import database
from test_element_search import client, element, issue


def typed(uid='a', document='model-a', type_id='100', **props):
    n = element(uid, document=document)
    n['properties'].update(type_id=type_id, **props)
    return n


@pytest.mark.parametrize('changes,expected', [
    ({'type_id': '200'}, None),  # Same name is insufficient when native types differ.
    ({'type_name': 'Renamed door'}, 'document_type_id'),
    ({'family': 'Different family', 'type_id': '200'}, None),
    ({'category_id': '-2000011'}, None),
])
def test_document_type_identity_is_not_a_similarity_score(changes, expected):
    a, b = typed(), typed('b')
    b['properties'].update(changes)
    assert type_match(a, b) == expected


def test_cross_document_ids_are_not_shared_and_unknown_types_do_not_match():
    a, b = typed(), typed('b', document='model-b', type_id='900')
    assert type_match(a, b) == 'family_and_type_name'
    b['properties'].update(type_id='100', type_name='Different door')
    assert type_match(a, b) is None
    b['properties'].update(type_id=None, family=None, type_name=None)
    assert type_match(a, b) is None


def test_both_case_dimensions_are_required_before_limits_and_verified_analysis(client):
    target = typed('target')
    accepted = client.post('/graph/cases', json=issue(typed('accepted'))).json()['id']
    wrong_type = issue(typed('other', type_id='200'))
    wrong_issue = issue(typed('other-issue'))
    wrong_issue['issue_type'] = 'PARAMETER_ERROR'
    foreign_wrong = issue(typed('foreign', document='different', type_name='Different type'))
    case_ids = [accepted]
    for payload in [wrong_type, wrong_issue, foreign_wrong]:
        case_ids.append(client.post('/graph/cases', json=payload).json()['id'])
    query = dict(target=target, issue_type='SPATIAL_CLEARANCE', candidate_limit=1)
    result = client.post('/graph/cases/related', json=query).json()
    assert [r['case_id'] for r in result['results']] == [accepted]
    assert not result['truncated']  # Rejected types do not consume the candidate budget.
    assert result['results'][0]['match_basis'] == 'document_type_id'
    assert result['classification']['issue_type'] == 'SPATIAL_CLEARANCE'
    for case_id in case_ids:
        for change in [dict(status='REPORTED'), dict(status='RESOLVED', solution='Review this change'),
                       dict(status='VERIFIED', reviewer='Reviewer')]:
            assert client.post('/graph/cases/{}/status'.format(case_id), json=change).status_code == 200
    matches = client.post('/graph/search', json=dict(draft=issue(target), candidate_limit=1)).json()
    assert [r['case_id'] for r in matches] == [accepted]
    all_query = dict(target=target, issue_type='ALL', verified_only=True)
    all_results = client.post('/graph/cases/related', json=all_query).json()
    assert {r['case_id'] for r in all_results['results']} == {accepted, case_ids[2]}
    assert {r['issue_type'] for r in all_results['results']} == {'SPATIAL_CLEARANCE', 'PARAMETER_ERROR'}
    assert all_results['classification']['issue_type'] == 'ALL' and '所有問題類型' in all_results['message']
    assert [r['case_id'] for r in client.post('/graph/cases/related', json=dict(all_query, exclude_case_id=case_ids[2])).json()['results']] == [accepted]
    client.post('/graph/cases/{}/status'.format(case_ids[2]), json=dict(status='ARCHIVED'))
    assert [r['case_id'] for r in client.post('/graph/cases/related', json=all_query).json()['results']] == [accepted]
    invalid_draft = issue(target)
    invalid_draft['issue_type'] = 'ALL'
    assert client.post('/graph/cases', json=invalid_draft).status_code == 422
    assert client.post('/graph/search', json=dict(draft=invalid_draft)).status_code == 422
    assert client.post('/graph/cases/related', json=dict(target=target)).status_code == 422
    target['properties'].update(type_id=None, family=None, type_name=None)
    result = client.post('/graph/cases/related', json=dict(target=target, issue_type='SPATIAL_CLEARANCE')).json()
    assert not result['results'] and '缺少類型' in result['message']
    assert not client.post('/graph/cases/related', json=dict(target=target, issue_type='ALL')).json()['results']


def test_classification_is_derived_from_case_evidence_and_preserves_legacy_revision(client):
    payload = issue(typed())
    payload['classification'] = dict(issue_type='PARAMETER_ERROR', element_type=dict(type_id='999'))
    created = client.post('/graph/cases', json=payload).json()
    case_id = created['id']
    assert created['draft']['classification']['element_type']['type_id'] == '100'
    assert created['draft']['classification']['issue_type'] == 'SPATIAL_CLEARANCE'
    # Emulate an existing stored revision with no derived classification.
    with database.connection() as db:
        raw = json.loads(db.execute('SELECT payload FROM graph_revisions WHERE case_id=?', (case_id,)).fetchone()[0])
        raw.pop('classification')
        encoded = json.dumps(raw)
        db.execute('UPDATE graph_revisions SET payload=? WHERE case_id=?', (encoded, case_id))
    before = client.get('/graph/cases/{}/revisions/1'.format(case_id)).json()
    read = client.get('/graph/cases/{}'.format(case_id)).json()
    assert read['draft']['classification']['element_type']['type_id'] == '100'
    assert client.get('/graph/cases/{}/revisions/1'.format(case_id)).json() == before
    with database.connection() as db:
        assert db.execute('SELECT payload FROM graph_revisions WHERE case_id=?', (case_id,)).fetchone()[0] == encoded
    updated = copy.deepcopy(payload)
    updated['issue_type'] = 'PARAMETER_ERROR'
    updated['graph']['nodes'][0]['properties']['type_id'] = '200'
    changed = client.put('/graph/cases/{}?revision=1'.format(case_id), json=updated).json()
    assert changed['draft']['classification']['element_type']['type_id'] == '200'
    assert changed['draft']['classification']['issue_type'] == 'PARAMETER_ERROR'
