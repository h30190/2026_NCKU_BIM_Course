import copy
import sys
from pathlib import Path
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.presentation import compare_rows, detail_rows, relationship_rows, with_similar_elements


def element(uid, **props):
    p = dict(element_id=uid, category='Walls', category_id='-2000011', family='Basic Wall',
             type_name='RC 200', type_id='100', units='mm', dimensions={'Thickness': 200})
    p.update(props)
    return dict(node_id='model:'+uid, node_type='ELEMENT', name='RC Wall', source_document='model', source_element_id=uid, properties=p)


def match(record):
    return dict(element=record, score=.95, completeness=.8)


def test_inspector_is_readable_and_does_not_guess_ifc_class():
    record = element('1', parameters={'instance:Volume': {'value': '12 m³', 'source_id': '123', 'unit': 'Autodesk.Revit.DB.ForgeTypeId'}})
    rows = {r.label:r.value for r in detail_rows(record)}
    assert rows['厚度'] == '200 mm'
    assert rows['實例 · Volume'] == '12 m³'
    assert rows['IFC 類別'] == '未設定（依 IFC 匯出設定決定）'
    assert not any('source_id' in r or 'ForgeTypeId' in r for r in rows.values())


def test_comparison_normalizes_dimensions_and_keeps_missing_unknown():
    a, b = element('1'), element('2', dimensions={'Thickness':.2}, units='m')
    rows = {r.label:r for r in compare_rows(a,b)}
    assert rows['厚度'].status == '相同'
    assert rows['厚度'].left == rows['厚度'].right == '200 mm'
    assert rows['IFC 類別'].status == '資料不足'
    assert rows['元素 ID'].status == '識別資料'
    b['properties']['dimensions']['Thickness'] = .3
    assert next(r for r in compare_rows(a,b) if r.label=='厚度').status == '不同'


def test_unknown_units_zero_and_one_sided_parameters():
    a = element('1', dimensions={'Length':0}, parameters={'instance:Mark':{'value':'A'}})
    b = element('2', dimensions={'Length':0}, units='unknown')
    rows = {r.label:r for r in compare_rows(a,b)}
    assert rows['長度'].left == '0 mm' and rows['長度'].status == '資料不足'
    assert rows['實例 · Mark'].right == '未提供' and rows['實例 · Mark'].status == '資料不足'


def test_comparison_accepts_different_types_and_rejects_wrong_category_or_self():
    a, b = element('1'), element('2', type_name='RC 300', type_id='200')
    assert next(r for r in compare_rows(a,b) if r.label=='類型').status == '不同'
    with pytest.raises(ValueError, match='兩個不同'):
        compare_rows(a,a)
    with pytest.raises(ValueError, match='同類別'):
        compare_rows(a, element('3', category_id='-2000014'))


def test_similarity_overlay_is_bounded_deduplicated_and_does_not_change_snapshot():
    a, b = element('1'), element('2')
    graph = dict(root=a['node_id'], nodes=[a,b], edges=[dict(source=a['node_id'], target=b['node_id'], relationship_type='NEARBY', validation_status='candidate')])
    original = copy.deepcopy(graph)
    candidates = [match(a),match(b),match(b),match(element('3',type_id='200'))]
    candidates += [match(element(str(i))) for i in range(4,22)]
    result = with_similar_elements(graph, a, candidates)
    assert graph == original
    assert len([e for e in result['edges'] if e['relationship_type'] in ('SAME_TYPE','SIMILAR_TO')]) == 12
    assert len({n['node_id'] for n in result['nodes']}) == len(result['nodes'])
    assert any(e['target']=='model:2' and e['relationship_type']=='SAME_TYPE' for e in result['edges'])
    assert any(e['target']=='model:3' and e['relationship_type']=='SIMILAR_TO' for e in result['edges'])
    details = relationship_rows(b, result)
    assert any('實例尺寸仍可能不同' in r.value for r in details)
    assert all('model:1' not in r.value for r in details)


def test_matching_names_across_documents_are_not_claimed_as_same_revit_type():
    a,b = element('1'),element('2')
    b.update(source_document='other',node_id='other:2')
    result = with_similar_elements(dict(root=a['node_id'],nodes=[a],edges=[]), a, [match(b)])
    assert result['edges'][0]['relationship_type'] == 'SIMILAR_TO'
