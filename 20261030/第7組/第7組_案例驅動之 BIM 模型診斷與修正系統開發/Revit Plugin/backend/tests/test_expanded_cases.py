import copy
import json
import sqlite3

import pytest
from app import database
from expand_model_cases import EXPORT, prepare, apply_plan
from test_simulated_cases import client, caller


def plan():
    export = json.loads(EXPORT.read_text(encoding='utf-8'))
    return export, prepare(export, [])


def test_native_roots_scenarios_and_geometry_evidence():
    export, batch = plan()
    native = {n['node_id']: n for n in export['elements']}
    assert len(batch['cases']) == len({i['root']['node_id'] for i in batch['cases']}) == 100
    for item in batch['cases']:
        assert item['root'] == native[item['root']['node_id']]
        assert item['draft']['classification']['element_type']['complete']
        assert '尚待驗證' in item['solution']
        assert '問題情境' in item['draft']['description']
        assert '模擬' not in json.dumps(item,ensure_ascii=False)
        assert not any(e['relationship_type']=='INTERSECTS' for e in item['draft']['graph']['edges'])
        if item['root']['properties']['view_specific']:
            assert item['root']['properties']['owner_view_id']
            assert item['draft']['issue_type'] != 'GEOMETRY_CLASH'


def test_atomic_import_preserves_existing_and_rerun_does_not_overwrite_edits(client,tmp_path):
    call = caller(client)
    _, batch = plan()
    original = copy.deepcopy(batch['cases'][0]['draft'])
    original.update(title='Existing user case',notes='Do not replace')
    saved = call('POST','/cases',original)
    receipt = apply_plan(database.DATABASE_PATH,batch,tmp_path/'backups')
    assert len(receipt['cases'])==100
    assert call('GET','/cases/'+str(saved['id'])) == saved
    assert len(call('GET','/cases'))==101
    for item,row in zip(batch['cases'],receipt['cases']):
        case = call('GET','/cases/'+str(row['case_id']))
        assert case['status']=='RESOLVED' and case['solution']==item['solution'] and case['reviewer']==''
        assert [h['status'] for h in case['history']] == ['REPORTED','RESOLVED']
        result = call('POST','/cases/related',dict(target=item['root'],issue_type=item['draft']['issue_type'],limit=50))
        assert row['case_id'] in {r['case_id'] for r in result['results']}
    cid = receipt['cases'][0]['case_id']
    call('POST','/cases/{}/status'.format(cid),dict(status='UNDER_REVIEW',solution='User amended solution'))
    before = call('GET','/cases')
    repeated = apply_plan(database.DATABASE_PATH,batch,tmp_path/'backups')
    assert all(r['action']=='preserved' for r in repeated['cases'])
    assert call('GET','/cases') == before


def test_failed_batch_rolls_back_all_new_cases(client,tmp_path):
    _, batch = plan()
    with sqlite3.connect(database.DATABASE_PATH) as db:
        db.execute("CREATE TRIGGER reject_batch BEFORE INSERT ON graph_cases WHEN (SELECT count(*) FROM graph_cases)>=10 BEGIN SELECT RAISE(ABORT,'test interruption'); END")
    with pytest.raises(sqlite3.IntegrityError):
        apply_plan(database.DATABASE_PATH,batch,tmp_path/'backups')
    with sqlite3.connect(database.DATABASE_PATH) as db:
        for table in ['graph_cases','graph_revisions','graph_history','graph_elements']:
            assert db.execute('SELECT count(*) FROM '+table).fetchone()[0]==0
