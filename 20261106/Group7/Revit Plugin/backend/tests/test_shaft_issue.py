import copy
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.shaft_issue import shaft_issue, RULES
from casegraph.check_issue import origin_caption
from test_element_search import client, element


def sample(rule='A1', group='fire', status='Violation'):
    room, wall = element('room'), element('wall')
    report = dict(document='model-a', checked_at='2026-11-03 14:00:00', view_name='1F',
                  rules_run=list(RULES), thresholds={'minFireRatingHours': 1}, warnings=[])
    row = dict(id='1', rule_id=rule, rule_name='檢查規則', group=group, status=status,
               shaft_label='1F PS-1', shaft_key='room', room_unique_id='room', source=None,
               nature='法規', reference='原規則依據', message='牆防火時效不足', advice='調整牆型',
               actual=0.5, required=1, unit='h', records=[room, wall],
               source_refs=[dict(source=None, document='model-a', unique_id='wall', element_id='wall', name='Wall')])
    return report, row


@pytest.mark.parametrize('rule,group,issue_type', [
    ('A1', 'fire', 'DESIGN_CONFLICT'), ('A7', 'maint', 'SPATIAL_CLEARANCE'), ('P2', 'mep', 'SPATIAL_CLEARANCE')])
def test_groups_keep_measurements_evidence_and_case_link(rule, group, issue_type):
    report, row = sample(rule, group)
    original = copy.deepcopy((report, row))
    record = shaft_issue(report, row)
    assert (report, row) == original
    assert record['root']['source_element_id'] == 'room'
    assert record['related'][0]['source_element_id'] == 'wall'
    assert record['issue_type'] == issue_type and record['priority'] == 'High'
    assert '實測：0.5；門檻：1 h' in record['description']
    assert '建議處理' not in record['description'] and row['advice'] not in record['description']
    assert record['origin']['advice'] == row['advice']  # Available only to 修改建議.
    assert record['origin']['actual'] == .5
    assert record['origin']['settings']['thresholds'] == report['thresholds']
    assert '管道間檢查' in origin_caption(record['origin'])
    assert rule in origin_caption(record['origin'])


@pytest.mark.parametrize('status', ['Warning', 'DataMissing'])
def test_uncertain_results_are_reportable_without_calling_them_violations(status):
    report, row = sample(status=status)
    record = shaft_issue(report, row)
    assert record['origin']['status'] == status
    assert record['priority'] == 'Medium'
    if status == 'DataMissing':
        assert record['issue_type'] == 'PARAMETER_ERROR'
        assert '尚未完成判定' in record['description']


@pytest.mark.parametrize('changes', [dict(status='Pass'), dict(rule_id='S1'), dict(group='struct'),
                                   dict(records=[]), dict(unresolved=True)])
def test_pass_out_of_scope_or_unresolvable_results_cannot_be_reported(changes):
    report, row = sample()
    row.update(changes)
    with pytest.raises(ValueError):
        shaft_issue(report, row)


def test_stale_and_foreign_document_results_are_refused():
    report, row = sample()
    report['stale'] = True
    with pytest.raises(ValueError):
        shaft_issue(report, row)
    report['stale'] = False
    row['records'][0]['source_document'] = 'another-model'
    with pytest.raises(ValueError):
        shaft_issue(report, row)


def test_linked_elements_preserve_source_identities_in_saved_and_reopened_case(client):
    report, row = sample('P2', 'mep')
    row.update(source='MEP link', room_unique_id='linked-room', shaft_key='MEP link:linked-room')
    row['records'] = [element('link-instance')]
    row['source_refs'] = [dict(source='MEP link', document='linked-model', unique_id='linked-pipe',
                               element_id='42', name='Pipe', link_unique_id='link-instance')]
    record = shaft_issue(report, row)
    root = record['root']
    draft = {key: record[key] for key in ('title', 'description', 'notes', 'issue_type', 'priority', 'origin')}
    draft['graph'] = dict(root=root['node_id'], nodes=[root], edges=[])
    response = client.post('/graph/cases', json=draft)
    assert response.status_code == 201, response.text
    case = response.json()
    saved = client.get('/graph/cases/{}'.format(case['id'])).json()
    assert saved['draft']['origin'] == case['draft']['origin']
    assert saved['draft']['origin']['source_refs'] == row['source_refs']
    assert saved['draft']['origin']['elements'][0]['unique_id'] == 'link-instance'
    assert '連結實例' in saved['draft']['description']
    edited = client.put('/graph/cases/{}?revision=1'.format(case['id']), json=dict(draft, title='Reviewed'))
    assert edited.status_code == 200, edited.text
    assert client.get('/graph/cases/{}/revisions/1'.format(case['id'])).json()['origin']['rule_id'] == 'P2'
    draft['origin']['elements'][0]['unique_id'] = 'wrong-root'
    assert client.post('/graph/cases', json=draft).status_code == 422
