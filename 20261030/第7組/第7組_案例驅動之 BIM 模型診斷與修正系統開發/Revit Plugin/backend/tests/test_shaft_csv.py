import copy
import csv
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.shaft_csv import write_problem_csv


def report():
    row = dict(id='1', shaft_label='1F PS-1 管道間', shaft_key='room-1', room_id='10',
               room_number='PS-1', room_name='管道間', room_unique_id='room-uid', level_name='1F',
               source=None, rule_id='A1', rule_name='牆防火時效', group='fire', status='Violation',
               nature='法規', reference='原規則依據', message='牆型 "A,B"\n防火時效不足',
               actual=0, required=1, unit='h', advice='調整牆型', unresolved=False,
               source_refs=[dict(source=None, element_id='100', unique_id='host-uid'),
                            dict(source='機電,連結', element_id='100', unique_id='link-uid')])
    return dict(document='model-id', document_name='測試模型', checked_at='2026-11-04 15:00:00',
                view_name='1F 平面圖', rules_run=['A1', 'P1'], stale=False,
                warnings=['1 個連結未載入', '部分管道間未納入'], results=[row])


def read_csv(path):
    with path.open(encoding='utf-8-sig', newline='') as stream:
        return list(csv.DictReader(stream))


def test_all_problems_keep_separate_rows_and_excel_readable_evidence(tmp_path):
    data = report()
    first = data['results'][0]
    data['results'].extend([
        dict(first, id='2', rule_id='A7', group='maint', status='Warning', actual=-0.5),
        dict(first, id='3', source='未載入連結', rule_id='P1', group='mep', status='DataMissing',
             actual=None, required=None, unresolved=True, room_unique_id='',
             source_refs=[dict(source='未載入連結', element_id='200', unique_id=None)]),
        dict(first, id='4', status='Pass'),
    ])
    before = copy.deepcopy(data)
    path = tmp_path / '管道間問題.csv'
    assert write_problem_csv(str(path), data) == 3
    assert data == before
    assert path.read_bytes().startswith(b'\xef\xbb\xbf')
    rows = read_csv(path)
    assert len(rows) == 3 and all(None not in row for row in rows)
    assert [row['狀態'] for row in rows] == ['違規', '警告', '資料不足']
    assert [row['規則 ID'] for row in rows] == ['A1', 'A7', 'P1']
    assert {row['管道間識別碼'] for row in rows} == {'room-1'}
    assert rows[0]['問題說明'] == first['message']
    assert rows[0]['建議處理方法'] == '調整牆型'
    assert rows[0]['實測值'] == '0' and rows[1]['實測值'] == '-0.5'
    assert rows[2]['實測值'] == rows[2]['要求值'] == ''
    assert rows[0]['主模型'] == '測試模型' and rows[0]['房間元素 ID'] == '10'
    assert rows[0]['相關元素 ID（含來源）'] == '主模型 | 100\n機電,連結 | 100'
    assert rows[0]['相關元素 UniqueId（含來源）'] == '主模型 | host-uid\n機電,連結 | link-uid'
    assert rows[2]['來源對應狀態'] == '部分來源無法對應'
    assert rows[2]['相關元素 ID（含來源）'] == '未載入連結 | 200'
    assert rows[2]['房間元素 ID'] == '10' and rows[2]['房間 UniqueId'] == ''
    assert rows[0]['檢查範圍備註'] == '\n'.join(data['warnings'])
    assert rows[0]['本次啟用規則'] == 'A1、P1'


@pytest.mark.parametrize('state', ['stale', 'empty', 'pass_only'])
def test_invalid_or_empty_export_does_not_overwrite_file(tmp_path, state):
    data = report()
    if state == 'stale':
        data['stale'] = True
    elif state == 'empty':
        data['results'] = []
    else:
        data['results'][0]['status'] = 'Pass'
    path = tmp_path / 'existing.csv'
    path.write_bytes(b'keep existing file')
    with pytest.raises(ValueError):
        write_problem_csv(str(path), data)
    assert path.read_bytes() == b'keep existing file'


def test_model_text_remains_text_in_excel(tmp_path):
    data = report()
    data['results'][0].update(room_name='=1+1', message='\t@SUM(1,2)', actual=-1.25)
    path = tmp_path / 'formula.csv'
    write_problem_csv(str(path), data)
    row = read_csv(path)[0]
    assert row['房間名稱'] == "'=1+1"
    assert row['問題說明'] == "'\t@SUM(1,2)"
    assert row['實測值'] == '-1.25'
