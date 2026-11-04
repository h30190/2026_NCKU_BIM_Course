import copy
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.check_issue import check_issue, origin_caption, MAX_LISTED
from test_element_search import client, element


def report(rows, **values):
    base = dict(document='model-a', view_id='900', view_name='2FL', range_mode='view',
                checked_at='2026-10-30 10:00:00', position_mm=1., volume_mm3=0., clash_target='ALL',
                clash_skip_joined=True, clash_skip_collinear=True, clash_line_mm=1., results=rows)
    base.update(values)
    return base


def row(records, check='overlap', status='OVERLAP', **values):
    base = dict(id='3', check=check, status=status, records=records,
                message='已確認實體重疊至少 12.000 mm³。', advice='調整位置後重新檢查。')
    base.update(values)
    return base


def test_record_follows_catalog_sections_and_keeps_a_structured_reference():
    a, b = element('a'), element('b')
    original = copy.deepcopy((a, b))
    record = check_issue(report([]), row([a, b]))
    assert (a, b) == original
    assert record['root'] == a and record['related'] == [b]
    assert record['issue_type'] == 'GEOMETRY_CLASH' and record['priority'] == 'High'
    assert record['title'].startswith('模型檢查｜同類元件實體重疊｜Door a')
    text = record['description']
    for heading in ('模型讀取資料', '檢查結果', '建議處理', '資料狀態'):
        assert '\n'+heading in '\n'+text
    assert '元件 ID：a' in text and 'Door b · Doors · ID b' in text and '判定：確認有重疊' in text
    assert 'Height 2100 mm；Width 900 mm' in text
    assert record['notes'].splitlines()[0] == 'model-check:overlap/a#3'
    origin = record['origin']
    assert origin['check'] == 'overlap' and origin['status'] == 'OVERLAP' and origin['result_id'] == '3'
    assert [(e['unique_id'], e['role']) for e in origin['elements']] == [('a', 'root'), ('b', 'related')]
    assert origin['views'] == [dict(id='900', name='2FL', kind='')]
    assert origin['settings']['volume_mm3'] == 0.
    assert '#3' in origin_caption(origin)


def test_pinned_target_becomes_root_and_issue_types_follow_the_check():
    a, b = element('a'), element('b')
    assert check_issue(report([]), row([a, b]), target=b)['root'] == b
    assert check_issue(report([]), row([a, b]), target=element('other'))['root'] == a
    expected = dict(duplicate='DESIGN_CONFLICT', clash='GEOMETRY_CLASH', mark='PARAMETER_ERROR', warning='OTHER')
    for check, issue_type in expected.items():
        record = check_issue(report([]), row([a, b], check=check, status='SUSPECT'))
        assert record['issue_type'] == issue_type and record['priority'] == 'Medium'
    unknown = check_issue(report([]), row([a], status='UNKNOWN'))
    assert '未能確認' in unknown['description']


def test_plan_results_keep_every_source_view_and_large_groups_are_bounded():
    views = [dict(id='1', name='1FL', kind='FloorPlan'), dict(id='2', name='1FL', kind='CeilingPlan')]
    records = [element(str(i)) for i in range(60)]
    record = check_issue(report([], range_mode='plans', view_id='', view_name='所有平面圖'),
                         row(records, check='mark', status='SUSPECT', views=views))
    assert record['origin']['views'] == views and record['origin']['range_mode'] == 'plans'
    assert '1FL（樓層平面圖）、1FL（天花平面圖）' in record['description']
    assert record['origin']['element_count'] == 60 and len(record['origin']['elements']) == 50
    assert '另有 {} 個未列出'.format(59-MAX_LISTED) in record['description']


def test_results_without_elements_or_from_another_document_are_refused():
    with pytest.raises(ValueError):
        check_issue(report([]), row([], check='warning', status='UNKNOWN'))
    with pytest.raises(ValueError):
        check_issue(report([]), row([element('a', document='model-b')]))


def test_backend_stores_origin_and_rejects_a_reference_to_another_element(client):
    a, b = element('a'), element('b')
    record = check_issue(report([]), row([a, b]))
    draft = dict(title=record['title'], description=record['description'], notes=record['notes'],
                 issue_type=record['issue_type'], priority=record['priority'], origin=record['origin'],
                 graph=dict(root=a['node_id'], nodes=[a, b], edges=[dict(
                     source=a['node_id'], target=b['node_id'], relationship_type='RELATED_TO', validation_status='verified',
                     evidence='模型檢查「同類元件實體重疊」#3', properties=dict(check='overlap', result_id='3'))]))
    created = client.post('/graph/cases', json=draft)
    assert created.status_code == 201, created.text
    case = created.json()
    assert case['draft']['origin']['elements'][0]['unique_id'] == 'a'
    edited = client.put('/graph/cases/{}?revision=1'.format(case['id']), json=dict(draft, title='Edited'))
    assert edited.json()['draft']['origin'] == case['draft']['origin']
    assert client.get('/graph/cases/{}/revisions/1'.format(case['id'])).json()['origin']['result_id'] == '3'
    moved = copy.deepcopy(draft)
    moved['graph']['root'] = b['node_id']
    assert client.post('/graph/cases', json=moved).status_code == 422
    plain = dict(draft)
    plain.pop('origin')
    assert client.post('/graph/cases', json=plain).json()['draft']['origin'] is None
