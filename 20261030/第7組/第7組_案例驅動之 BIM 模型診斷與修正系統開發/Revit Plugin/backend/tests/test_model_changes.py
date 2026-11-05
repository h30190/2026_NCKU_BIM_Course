import copy
import sqlite3
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.model_changes import element_changes, change_report, solution_text, summary, recorded_elements
from app import database, graph_api
from test_element_search import client


def door(uid='door', document='model-a', **changes):
    p = dict(element_id=uid, category='Doors', family='Single', type_name='900 x 2100', type_id='100',
             dimensions={'Width': 900.0, 'Height': 2100.0}, location=dict(x=0., y=0., z=0.),
             FacingOrientation=dict(x=0., y=1., z=0.), HandOrientation=dict(x=1., y=0., z=0.),
             ifc_class='IfcDoor', ifc_global_id='2N1qr8Fx5Ah9$QK7r1V3Ao',
             parameters={'instance:Mark': dict(value='D-01'), 'instance:Family and Type': dict(value='Single: 900 x 2100'),
                         'type:Fire Rating': dict(value='60 min'), 'instance:Width': dict(value='900')})
    p.update(changes)
    return dict(node_id=document+':'+uid, node_type='ELEMENT', name='Door '+uid, source_document=document,
                source_element_id=uid, properties=p)


def fields(rows):
    return [r['field'] for r in rows]


def test_unchanged_and_missing_data_report_no_changes():
    assert element_changes(door(), door()) == []
    sparse = door()
    for key in ('location', 'FacingOrientation', 'parameters', 'ifc_global_id'):
        sparse['properties'].pop(key)
    # Absent data on either side is not a change.
    assert element_changes(door(), sparse) == []
    assert element_changes(door(), door(dimensions={'Width': 900.3, 'Height': 2100.})) == []


def test_type_dimension_move_orientation_ifc_and_parameter_changes():
    after = door(type_id='200', type_name='1000 x 2100', dimensions={'Width': 1000., 'Height': 2100.},
                 location=dict(x=120., y=0., z=0.), FacingOrientation=dict(x=0., y=-1., z=0.), ifc_class='IfcDoorStandardCase',
                 parameters={'instance:Mark': dict(value='D-02'), 'instance:Family and Type': dict(value='Single: 1000 x 2100'),
                             'type:Fire Rating': dict(value=''), 'instance:Width': dict(value='1000')})
    rows = element_changes(door(), after)
    assert fields(rows) == ['類型', '寬度', '位置', '朝向', 'IFC 類別', '參數 · Mark', '類型參數 · Fire Rating']
    assert {k: rows[0][k] for k in ('field', 'before', 'after')} == dict(field='類型', before='Single／900 x 2100', after='Single／1000 x 2100')
    assert rows[1]['before'] == '900 mm' and rows[1]['after'] == '1000 mm'
    assert rows[2]['before'] == '移動 120 mm' and rows[2]['after'].startswith('X +120')
    assert rows[2]['action']['position_basis'] == 'location'
    assert rows[2]['action']['before_position_mm'] == dict(x=0., y=0., z=0.)
    assert rows[2]['action']['after_position_mm'] == dict(x=120., y=0., z=0.)
    assert rows[-1]['after'] == '（空）'


def test_curve_elements_compare_bounding_box_centres():
    def wall(x):
        w = door('wall')
        w['properties'].pop('location')
        w['properties']['bounding_box'] = dict(min=dict(x=x, y=0., z=0.), max=dict(x=x+5000., y=200., z=3000.))
        return w
    assert element_changes(wall(0.), wall(0.)) == []
    assert fields(element_changes(wall(0.), wall(300.))) == ['位置（外框中心）']


def case(*elements):
    root = elements[0]
    return dict(id=4, revision=2, draft=dict(graph=dict(root=root['node_id'], nodes=[
        dict(node_id='level', node_type='LEVEL', name='1FL', source_document='model-a', source_element_id='lvl', properties={})]
        + list(reversed(elements)), edges=[])))


def test_report_matches_states_and_builds_solution_text():
    root, moved, gone, other = door('a'), door('b'), door('c'), door('d', document='model-b')
    live = {'a': door('a'), 'b': door('b', location=dict(x=0., y=50., z=0.))}

    def find(record):
        if record['source_element_id'] in live:
            return live[record['source_element_id']], 'Revit UniqueId'
        return None, None
    report = change_report(case(root, moved, gone, other), 'model-a', '2026-10-30 10:00', find, lambda e: e)
    assert [e['node_id'] for e in recorded_elements(case(root, moved))][0] == root['node_id']
    states = dict((e['unique_id'], e['state']) for e in report['elements'])
    assert states == dict(a='UNCHANGED', b='CHANGED', c='DELETED', d='UNAVAILABLE')
    assert report['elements'][0]['root'] and report['case_id'] == 4 and report['revision'] == 2
    assert summary(report) == '1 個已修改、1 個已刪除、1 個未變更、1 個未開啟來源模型'
    text = solution_text(report)
    assert text.splitlines()[0] == '模型變更紀錄（2026-10-30 10:00；對比 RFI-4 第 2 版快照）'
    assert '・Door b（ID b）：位置 移動 50 mm' in text and '・Door c（ID c）：已刪除' in text and 'Door a' not in text


def test_review_step_stores_change_evidence_for_the_current_revision(client):
    root = door('a')
    created = client.post('/graph/cases', json=dict(title='Door clash', description='Door overlaps wall',
                          issue_type='GEOMETRY_CLASH', graph=dict(root=root['node_id'], nodes=[root]))).json()
    client.post('/graph/cases/{}/status'.format(created['id']), json=dict(status='REPORTED'))
    live = door('a', dimensions={'Width': 800., 'Height': 2100.})
    report = change_report(dict(created, revision=1), 'model-a', '2026-10-30 10:00', lambda r: (live, 'IFC GlobalId'), lambda e: e)
    stale = dict(report, revision=2)
    assert client.post('/graph/cases/{}/status'.format(created['id']),
                       json=dict(status='RESOLVED', solution='Narrow the door', changes=stale)).status_code == 409
    resolved = client.post('/graph/cases/{}/status'.format(created['id']),
                           json=dict(status='RESOLVED', solution='Narrow the door', changes=report)).json()
    evidence = resolved['history'][-1]['evidence']
    row = evidence['elements'][0]['changes'][0]
    assert (row['field'], row['before'], row['after']) == ('寬度', '900 mm', '800 mm')
    assert evidence['elements'][0]['match'] == 'IFC GlobalId' and resolved['history'][0]['evidence'] is None


def test_existing_history_table_gains_the_evidence_column(tmp_path, monkeypatch):
    monkeypatch.setattr(database, 'DATA_DIR', tmp_path)
    monkeypatch.setattr(database, 'DATABASE_PATH', tmp_path / 'old.db')
    db = sqlite3.connect(tmp_path / 'old.db')
    db.execute('CREATE TABLE graph_history (id INTEGER PRIMARY KEY, case_id INTEGER NOT NULL, status TEXT NOT NULL, '
               'solution TEXT NOT NULL, reviewer TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP)')
    db.execute("INSERT INTO graph_history(case_id,status,solution,reviewer) VALUES (1,'RESOLVED','old','')")
    db.commit()
    db.close()
    graph_api.initialize_graph()
    graph_api.initialize_graph()  # Idempotent.
    with database.connection() as db:
        rows = [dict(r) for r in db.execute('SELECT * FROM graph_history')]
    assert rows == [dict(rows[0], evidence=None)] and rows[0]['solution'] == 'old'
