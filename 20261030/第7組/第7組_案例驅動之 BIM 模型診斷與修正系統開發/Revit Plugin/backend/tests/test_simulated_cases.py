import copy
from collections import Counter

import pytest
from fastapi.testclient import TestClient

from app import database
from app.main import app
from import_simulated_cases import import_cases, prepare_catalog, write_report


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(database, 'DATA_DIR', tmp_path)
    monkeypatch.setattr(database, 'DATABASE_PATH', tmp_path / 'test.db')
    with TestClient(app) as client:
        yield client


def caller(client):
    def call(method, path, payload=None):
        response = client.request(method, '/graph' + path, json=payload)
        response.raise_for_status()
        return response.json()
    return call


def test_import_is_searchable_unverified_idempotent_and_preserves_existing(client, tmp_path):
    catalog, prepared = prepare_catalog()
    assert len(prepared) == 25
    assert Counter(p['root']['properties']['category'] for p in prepared) == {
        'Doors': 6, 'Electrical Fixtures': 5, 'Walls': 4, 'Windows': 2,
        'Floors': 2, 'Detail Items': 4, 'Furniture': 2}
    call = caller(client)
    original = copy.deepcopy(prepared[0]['draft'])
    original.update(title='Existing user draft', notes='User notes')
    original = call('POST', '/cases', original)
    receipt = import_cases(call, prepared)
    assert len(receipt) == 25 and {r['action'] for r in receipt} == {'created'}
    assert call('GET', '/cases/{}'.format(original['id'])) == original
    for item, row in zip(prepared, receipt):
        stored = call('GET', '/cases/' + str(row['case_id']))
        assert stored['status'] == 'RESOLVED'
        assert stored['reviewer'] == ''
        assert stored['solution'] == item['solution']
        assert '【模擬解法／未實際驗證】' in stored['solution']
        assert [h['status'] for h in stored['history']] == ['REPORTED', 'RESOLVED']
        assert stored['draft']['graph']['warnings']
        assert all(e['validation_status'] != 'verified' for e in stored['draft']['graph']['edges']
                   if e['relationship_type'] != 'HAS_ISSUE')
    # Re-import does not add cases, overwrite records or append history.
    before = call('GET', '/cases')
    mutations = []
    def tracking_call(method, path, payload=None):
        if method != 'GET':
            mutations.append((method, path))
        return call(method, path, payload)
    repeated = import_cases(tracking_call, prepared)
    assert {r['action'] for r in repeated} == {'preserved'}
    assert mutations == []
    assert call('GET', '/cases') == before
    # All seven categories return suggested solutions without entering trusted retrieval.
    for category in catalog['categories']:
        item = next(p for p in prepared if p['root']['properties']['category'] == category)
        related = call('POST', '/cases/related', dict(target=item['root'], issue_type=item['draft']['issue_type'], limit=50))['results']
        simulations = [r for r in related if r['title'].startswith('【模擬')]
        assert simulations and all(r['resolution_note'] and not r['reliable_solution'] and not r['solution'] for r in simulations)
        assert call('POST', '/cases/related', dict(target=item['root'], issue_type=item['draft']['issue_type'], verified_only=True))['results'] == []
        assert call('POST', '/search', dict(draft=item['draft'])) == []
        matches = call('POST', '/elements/search', dict(target=item['root'], scope='recorded'))['results']
        assert matches and all(r['element']['properties']['simulation'] for r in matches)
    # Nearby candidates of one family have meaningful instance/type differences for comparison.
    doors = call('POST', '/elements/search', dict(target=prepared[0]['root'], scope='recorded'))['results']
    assert len(doors) == 5
    assert any(r['differences'] for r in doors)
    report = tmp_path / 'catalog.html'
    write_report(catalog, prepared, receipt, report)
    rendered = report.read_text(encoding='utf-8')
    assert rendered.count('<details class="case"') == 25
    assert 'SIM-025' in rendered and 'RFI-26' in rendered


@pytest.mark.parametrize('interrupted_status', ['DRAFT', 'REPORTED', 'RESOLVED'])
def test_resume_after_interruption(client, interrupted_status):
    _, prepared = prepare_catalog()
    call = caller(client)
    item = prepared[0]
    row = call('POST', '/cases', item['draft'])
    if interrupted_status in ('REPORTED', 'RESOLVED'):
        row = call('POST', '/cases/{}/status'.format(row['id']), dict(status='REPORTED'))
    if interrupted_status == 'RESOLVED':
        row = call('POST', '/cases/{}/status'.format(row['id']), dict(status='RESOLVED', solution=item['solution']))
    receipt = import_cases(call, prepared)
    assert receipt[0]['case_id'] == row['id']
    assert len(call('GET', '/cases')) == 25
    stored = call('GET', '/cases/' + str(row['id']))
    assert stored['status'] == 'RESOLVED'
    assert len(stored['history']) == 2
    matches = call('POST', '/elements/search', dict(target=prepared[1]['root'], scope='recorded'))['results']
    assert any(r['element']['node_id'] == item['root']['node_id'] for r in matches)


def test_user_edits_to_unfinished_simulation_are_not_overwritten(client):
    _, prepared = prepare_catalog()
    call = caller(client)
    row = call('POST', '/cases', prepared[-1]['draft'])
    edited = copy.deepcopy(prepared[-1]['draft'])
    edited['description'] = 'User added real observations'
    call('PUT', '/cases/{}?revision=1'.format(row['id']), edited)
    before = call('GET', '/cases')
    with pytest.raises(RuntimeError, match='was edited; preserved'):
        import_cases(call, prepared)
    assert call('GET', '/cases') == before
