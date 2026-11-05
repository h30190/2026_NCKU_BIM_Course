import copy
import json
import sqlite3

import pytest

from app import database
from import_simulated_cases import import_cases, prepare_catalog
from prepare_model_cases import prepare, migrate, write_report
from test_simulated_cases import client, caller


def test_real_model_cases_preserve_native_properties_and_do_not_claim_verified_fixes(tmp_path):
    export, prepared = prepare()
    exported = {n['source_element_id']: n for n in export['elements']}
    assert len(prepared) == len({i['root']['source_element_id'] for i in prepared}) == 25
    assert {i['root']['properties']['category'] for i in prepared} == {'Doors', 'Windows', 'Walls', 'Floors', 'Detail Items'}
    for item in prepared:
        assert item['root'] == exported[item['root']['source_element_id']]
        assert '模擬' not in json.dumps(item, ensure_ascii=False)
        assert '待確認' in item['solution']
        assert all(n['source_document'] == export['document'] for n in item['draft']['graph']['nodes'])
        assert all(n['source_element_id'] and n['properties']['element_id'].isdigit() for n in item['draft']['graph']['nodes'])
        assert not any(e['relationship_type'] == 'INTERSECTS' for e in item['draft']['graph']['edges'])
    report = tmp_path / 'cases.html'
    write_report(export, prepared, path=report)
    assert report.read_text(encoding='utf-8').count('<details open>') == 25
    assert '模擬' not in report.read_text(encoding='utf-8')


def test_atomic_replacement_preserves_user_case_ids_history_and_native_navigation(client, tmp_path):
    call = caller(client)
    _, legacy = prepare_catalog()
    original = copy.deepcopy(legacy[0]['draft'])
    original.update(title='User case', notes='User notes')
    user = call('POST', '/cases', original)
    old_receipt = import_cases(call, legacy)
    export, prepared = prepare()
    receipt, backup = migrate(database.DATABASE_PATH, prepared, legacy, tmp_path / 'backups')
    assert len(receipt) == 25 and all(r['action'] == 'updated' for r in receipt)
    assert [r['case_id'] for r in receipt] == [r['case_id'] for r in old_receipt]
    assert call('GET', '/cases/' + str(user['id'])) == user
    assert len(call('GET', '/cases')) == 26
    with sqlite3.connect(backup) as db:
        assert db.execute('SELECT revision FROM graph_cases WHERE id=2').fetchone()[0] == 1
    for row, item, old in zip(receipt, prepared, legacy):
        current = call('GET', '/cases/' + str(row['case_id']))
        assert current['revision'] == 2 and current['status'] == 'RESOLVED' and not current['reviewer']
        assert current['draft']['graph']['root'] == item['root']['node_id']
        assert '模擬' not in json.dumps(current['draft'], ensure_ascii=False)
        assert call('GET', '/cases/{}/revisions/1'.format(row['case_id']))['title'] == old['draft']['title']
    assert call('GET', '/elements?document=' + legacy[0]['root']['source_document']) == []
    assert len(call('GET', '/elements?document=' + export['document'])) == 25
    related = call('POST', '/cases/related', dict(target=prepared[0]['root'], issue_type=prepared[0]['draft']['issue_type'], limit=50))['results']
    assert any(r['case_id'] == receipt[0]['case_id'] for r in related)
    assert not any(r['reliable_solution'] for r in related)
    assert call('POST', '/cases/related', dict(target=prepared[0]['root'], issue_type=prepared[0]['draft']['issue_type'], verified_only=True))['results'] == []
    before = call('GET', '/cases')
    repeated, _ = migrate(database.DATABASE_PATH, prepared, legacy, tmp_path / 'backups')
    assert all(r['action'] == 'preserved' for r in repeated)
    assert call('GET', '/cases') == before


def test_preflight_refuses_edited_catalog_without_partial_replacement(client, tmp_path):
    call = caller(client)
    _, legacy = prepare_catalog()
    receipt = import_cases(call, legacy)
    _, prepared = prepare()
    call('POST', '/cases/{}/status'.format(receipt[-1]['case_id']), dict(status='UNDER_REVIEW'))
    before = call('GET', '/cases')
    with pytest.raises(ValueError, match='unchanged generated'):
        migrate(database.DATABASE_PATH, prepared, legacy, tmp_path / 'backups')
    assert call('GET', '/cases') == before
