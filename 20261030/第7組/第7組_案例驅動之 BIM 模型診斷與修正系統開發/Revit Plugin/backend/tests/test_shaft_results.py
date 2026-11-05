import copy
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension/lib'))
from casegraph.shaft_results import group_label, grouped_rows


def test_floor_groups_follow_elevation_and_keep_missing_floor():
    rows = [dict(id='1', level_name='10F', level_elevation_mm=30000),
            dict(id='2', level_name='B2', level_elevation_mm=-6000),
            dict(id='3', level_name='2F', level_elevation_mm=6000),
            dict(id='4', level_name='', level_elevation_mm=0),
            dict(id='5', level_name='2F', level_elevation_mm=6000)]
    before = copy.deepcopy(rows)
    assert [r['id'] for r in grouped_rows(rows, 'level')] == ['2', '3', '5', '1', '4']
    assert rows == before and grouped_rows(rows, 'none') == rows
    assert group_label(rows[3], 'level') == '未指定樓層'
    assert group_label({}, 'level') == '未指定樓層'


def test_category_and_judgment_preserve_all_individual_findings():
    rows = [dict(id='1', group='mep', status='DataMissing'),
            dict(id='2', group='fire', status='Warning'),
            dict(id='3', group='maint', status='Violation'),
            dict(id='4', group='fire', status='Violation')]
    assert [r['id'] for r in grouped_rows(rows, 'category')] == ['2', '4', '3', '1']
    assert [r['id'] for r in grouped_rows(rows, 'status')] == ['3', '4', '2', '1']
    assert group_label(rows[0], 'category') == '管線分隔與間距'
    assert group_label(rows[0], 'status') == '資料不足'
    assert grouped_rows([], 'level') == []
