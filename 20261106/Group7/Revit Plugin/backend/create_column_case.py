"""Create one model-backed column case through the local API, preserving other cases."""
import copy
import ntpath
import json
import re
from pathlib import Path
from urllib.request import Request, urlopen
from app.graph_models import Draft
from app.case_scope import type_match

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'backend' / 'data'


def api(path, payload=None):
    data = json.dumps(payload, ensure_ascii=False).encode('utf-8') if payload is not None else None
    request = Request('http://127.0.0.1:8000/graph'+path, data=data, headers={'Content-Type': 'application/json'})
    with urlopen(request, timeout=15) as response:
        return json.load(response)


def prepare(export, number):
    assert export['state'] == 'complete' and export['read_only'] and export['revit_version'] == '2022'
    assert not export['errors'] and export['columns'], 'A complete native column export is required'
    chosen = next(c for c in export['columns'] if c['element']['properties']['element_id'] == '334197')
    root, constraints = chosen['element'], chosen['constraints']
    assert chosen['category'] in ('OST_StructuralColumns', 'OST_Columns')
    p = root['properties']
    peers = [c['element'] for c in export['columns'] if c['element']['node_id'] != root['node_id']
             and type_match(root, c['element'])]
    comparison = peers[0] if peers else None
    graph = copy.deepcopy(chosen['graph'])
    # Retain all native evidence while showing one edge per level relationship.
    edges = {}
    for edge in graph['edges']:
        key = (edge['source'], edge['target'], edge['relationship_type'])
        if key in edges:
            edges[key]['evidence'] += '；'+edge['evidence']
        else:
            edges[key] = edge
    graph['edges'] = list(edges.values())
    if comparison:
        graph['nodes'].append(copy.deepcopy(comparison))
        graph['edges'].append(dict(source=root['node_id'], target=comparison['node_id'],
            relationship_type='RELATED_TO', validation_status='unverified', properties={},
            evidence='同型柱的參數核對對象；此連線只供比較，不表示實體連接或碰撞。'))
    facts = [
        '來源模型：'+export['title'],
        '元件：{}／{} · ID {}'.format(p['family'], p['type_name'], p['element_id']),
        '類別：{}；Revit TypeId：{}'.format(p['category'], p['type_id']),
    ]
    for code in ['FAMILY_BASE_LEVEL_PARAM', 'FAMILY_TOP_LEVEL_PARAM', 'FAMILY_BASE_LEVEL_OFFSET_PARAM',
                 'FAMILY_TOP_LEVEL_OFFSET_PARAM', 'INSTANCE_LENGTH_PARAM']:
        value = constraints.get(code)
        if value:
            display = '{:g} mm'.format(value['mm']) if 'mm' in value else value['display'] or '未提供'
            facts.append(value['label']+'：'+display)
    if comparison:
        facts.append('同型比較柱：{} · ID {}'.format(comparison['name'], comparison['properties']['element_id']))
    review = ('核對柱基底、頂部樓層及上下偏移是否與本柱位置的核定圖面一致，並在立面／剖面檢查柱長、'
              '柱端及樓層連續性。同型柱可以比較設定，但應逐一確認其所在位置及設計要求。')
    evidence_note = '以上為模型實際讀取值；本案例記錄核對項目及處理方式，尚未認定參數有誤或處理已完成。'
    marker = 'model-column-case:'+root['node_id']+':PARAMETER_ERROR:constraints-v1'
    draft = Draft.model_validate(dict(title='CASE-{:03}｜RC柱基底／頂部樓層與柱長核對'.format(number),
        description='模型資料\n'+'\n'.join(facts)+'\n\n核對項目\n'+review+'\n\n'+evidence_note,
        issue_type='PARAMETER_ERROR', priority='Medium',
        notes=marker+'\n來源路徑：'+ntpath.basename(export['source_path'])+'\nUniqueId：'+root['source_element_id']
              +'\n擷取時間：'+export['exported_at'], graph=graph)).model_dump(mode='json')
    solution = '\n'.join([
        '處理方式',
        '1. 在 Revit 定位柱 ID '+p['element_id']+'，記錄目前基底／頂部樓層、上下偏移及柱長。',
        '2. 以該柱位置的核定圖面確認應有的起訖樓層與偏移；在立面或剖面核對柱端位置。',
        '3. 若約束與核定圖面不符，調整該柱實例的樓層及偏移；如有附接關係，先核對附接對象及柱端位置。',
        '4. 重新檢查柱長及上下樓層的銜接；與同型柱比較時，逐一確認位置條件，不直接套用所有柱。',
        '5. 如核對涉及柱截面、位置或結構配置變更，記錄需確認項目，待核定後再更新模型。',
        '', '完成後檢查',
        '□ 基底／頂部樓層及偏移與核定圖面一致。',
        '□ 剖面中的柱端、柱長與樓層銜接已覆核。',
        '□ 平面、天花平面及相關剖面顯示一致。',
        '□ 記錄調整前後資料，交由審核者確認後更新驗證狀態。',
    ])
    return dict(marker=marker, draft=draft, solution=solution, root=root, comparison=comparison, peers=peers)


def main():
    export = json.loads((DATA / 'column-case-export.json').read_text(encoding='utf-8-sig'))
    before = api('/cases')
    numbers = [int(m.group(1)) for r in before for m in [re.search(r'CASE-(\d+)', r['draft']['title'])] if m]
    item = prepare(export, max(numbers, default=0)+1)
    existing = [r for r in before if r['draft'].get('notes', '').split('\n')[0] == item['marker']]
    assert len(existing) <= 1, 'Duplicate case marker; nothing changed'
    if existing:
        current = api('/cases/'+str(existing[0]['id']))
        action = 'preserved'
    else:
        current = api('/cases', item['draft'])
        case_id = current['id']
        (DATA / 'column-case-created.json').write_text(json.dumps(dict(case_id=case_id, marker=item['marker'])), encoding='utf-8')
        current = api('/cases/{}/status'.format(case_id), dict(status='REPORTED'))
        current = api('/cases/{}/status'.format(case_id), dict(status='RESOLVED', solution=item['solution']))
        action = 'created'
    after = api('/cases')
    by_id = {r['id']: r for r in after}
    assert all(by_id[r['id']] == r for r in before), 'An existing case changed during creation'
    assert len(after) == len(before)+(action == 'created')
    case_id = current['id']
    assert current['draft']['classification']['element_type']['type_id'] == item['root']['properties']['type_id']
    assert current['draft']['graph']['root'] == item['root']['node_id']
    assert current['solution'] and current['status'] == 'RESOLVED' and not current['reviewer']
    queries = []
    for target in [item['root']]+item['peers']:
        result = api('/cases/related', dict(target=target, issue_type='PARAMETER_ERROR'))
        assert case_id in [r['case_id'] for r in result['results']]
        queries.append(dict(element_id=target['properties']['element_id'], case_ids=[r['case_id'] for r in result['results']]))
    wrong_issue = api('/cases/related', dict(target=item['root'], issue_type='GEOMETRY_CLASH'))
    assert case_id not in [r['case_id'] for r in wrong_issue['results']]
    preview = api('/cases/related', dict(target=item['root'], issue_type='PARAMETER_ERROR'))
    (DATA / 'column-case-preview.json').write_text(json.dumps(dict(graph=item['draft']['graph'], related=preview), ensure_ascii=False, indent=2), encoding='utf-8')
    receipt = dict(action=action, case_id=case_id, title=current['draft']['title'], status=current['status'],
                   element_id=item['root']['properties']['element_id'], unique_id=item['root']['source_element_id'],
                   document=item['root']['source_document'], classification=current['draft']['classification'],
                   queries=queries, previous_cases_preserved=len(before), model_modified=False)
    (DATA / 'column-case-import.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(receipt, ensure_ascii=True))


if __name__ == '__main__':
    main()
