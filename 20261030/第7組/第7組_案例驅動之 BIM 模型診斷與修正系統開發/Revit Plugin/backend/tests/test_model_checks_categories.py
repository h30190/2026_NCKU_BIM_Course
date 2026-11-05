import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.model_checks import category_groups, category_lines, row_categories


def row(check, *categories):
    return dict(check=check, records=[dict(properties=dict(category=c) if c else {}) for c in categories])


def test_results_are_grouped_by_element_category():
    results = [row('clash', 'Pipes', 'Walls'), row('clash', 'Pipes', 'Floors'), row('duplicate', 'Walls', 'Walls'),
               row('mark', 'Doors'), row('warning', None)]
    assert row_categories(results[0]) == ['Pipes', 'Walls'] and row_categories(results[2]) == ['Walls']
    groups = category_groups(results)
    assert [(c, total) for c, _, total in groups] == [('Pipes', 2), ('Walls', 2), ('Doors', 1), ('Floors', 1), ('（類別未提供）', 1)]
    assert dict((c, counts) for c, counts, _ in groups)['Walls'] == [('duplicate', 1), ('clash', 1)]
    lines = category_lines(results)
    assert lines[0] == '・管 Pipes：不同類型／類別碰撞 2' and lines[1] == '・牆 Walls：疑似重複元件 1、不同類型／類別碰撞 1'
