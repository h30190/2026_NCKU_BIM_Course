"""Validate, preview and explicitly import the local simulation catalog via CaseGraph API."""
from __future__ import annotations

import argparse
import copy
import hashlib
import html
import json
import sqlite3
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from app import database
from app.graph_models import Draft, Transition


ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / 'samples' / 'simulated_cases.json'
REPORT = ROOT / 'samples' / 'simulated-cases.html'
BASE_URL = 'http://127.0.0.1:8000/graph'


def prepare_catalog(path=CATALOG):
    catalog = json.loads(Path(path).read_text(encoding='utf-8'))
    document = 'casegraph:simulation:' + catalog['batch']
    prepared, ids = [], set()
    for case in catalog['cases']:
        sample_id = case['id']
        if sample_id in ids:
            raise ValueError('Duplicate simulation ID: ' + sample_id)
        ids.add(sample_id)
        if not sample_id.startswith('SIM-') or len(case['steps']) < 3 or len(case['checks']) < 2:
            raise ValueError('Each simulation needs an ID, at least three steps and two checks')
        profile = copy.deepcopy(catalog['profiles'][case['profile']])
        profile.update(copy.deepcopy(case.get('overrides', {})))
        category = catalog['categories'][profile['category']]
        marker = 'simulation:{}/{}'.format(catalog['batch'], sample_id)
        shared = dict(simulation=True, simulation_batch=catalog['batch'], simulation_id=sample_id,
                      document_title=catalog['label'] + '（虛構資料）', data_source='虛構教學情境，非 Revit 擷取')
        properties = dict(profile, **shared, category_id=category['id'], units='mm',
                          element_id=sample_id, level=case['level'],
                          search_parameters=case['parameters'],
                          parameters={k: {'value': v, 'source_id': 'simulation', 'unit': None}
                                      for k, v in case['parameters'].items()})
        root = dict(node_id=document + ':' + sample_id, node_type='ELEMENT',
                    name='【模擬 {}】{}'.format(sample_id, profile['type_name']),
                    source_document=document, source_element_id=sample_id, properties=properties)
        level_id = document + ':level:' + case['level']
        nodes = [root, dict(node_id=level_id, node_type='LEVEL', name='模擬樓層 ' + case['level'],
                            source_document=document, properties=dict(shared))]
        edges = [dict(source=root['node_id'], target=level_id, relationship_type='LOCATED_ON_LEVEL',
                      evidence='模擬情境設定的樓層，未由實際模型擷取。', validation_status='unverified')]
        for index, context in enumerate(case['context'], 1):
            uid = '{}-context-{}'.format(sample_id, index)
            context_properties = dict(shared)
            if context.get('category'):
                context_properties['category'] = context['category']
                if context['category'] in catalog['categories']:
                    context_properties['category_id'] = catalog['categories'][context['category']]['id']
            nodes.append(dict(node_id=document + ':' + uid, node_type=context['kind'],
                              name='【模擬】' + context['name'], source_document=document,
                              source_element_id=uid if context['kind'] == 'ELEMENT' else None,
                              properties=context_properties))
            edges.append(dict(source=root['node_id'], target=nodes[-1]['node_id'],
                              relationship_type=context['relation'],
                              evidence='模擬情境假設：' + case['problem'] + ' 此連結並非實際幾何或連接驗證。',
                              validation_status='candidate' if context['relation'] == 'NEARBY' else 'unverified'))
        solution = '\n'.join([
            '【模擬解法／未實際驗證】', '假設原因：' + case['cause'], '', '建議處理步驟：',
            *['{}. {}'.format(i, s) for i, s in enumerate(case['steps'], 1)], '', '完成後檢查：',
            *['□ ' + check for check in case['checks']], '', '預期結果（待驗證）：' + case['expected'],
            '', catalog['notice']])
        draft = Draft.model_validate(dict(
            title='【模擬 {}】{}'.format(sample_id, case['title']),
            description='【模擬情境】\n{}\n\n假設原因：{}\n\n{}'.format(case['problem'], case['cause'], catalog['notice']),
            issue_type=case['issue_type'], priority=case['priority'],
            notes=marker + '\n模擬元素沒有對應的實體 Revit ID。\n' + catalog['notice'],
            graph=dict(root=root['node_id'], nodes=nodes, edges=edges,
                       model_version='simulation/' + catalog['batch'], warnings=[catalog['notice']]))).model_dump(mode='json')
        Transition(status='RESOLVED', solution=solution)  # Validate every payload before any writes.
        prepared.append(dict(sample_id=sample_id, marker=marker, draft=draft,
                             root=draft['graph']['nodes'][0], solution=solution))
    if not prepared:
        raise ValueError('The catalog is empty')
    return catalog, prepared


def api_call(method, path, payload=None):
    body = json.dumps(payload, ensure_ascii=False).encode('utf-8') if payload is not None else None
    request = Request(BASE_URL + path, data=body, method=method,
                      headers={'Content-Type': 'application/json; charset=utf-8'})
    try:
        with urlopen(request, timeout=20) as response:
            return json.load(response)
    except HTTPError as error:
        raise RuntimeError('{} {}: {} {}'.format(method, path, error.code,
                                                error.read().decode('utf-8', 'replace'))) from error


def import_cases(call, prepared):
    """Resume interrupted imports, leave user-edited/finished cases alone, never verify samples."""
    if call('GET', '/health').get('service') != 'casegraph':
        raise RuntimeError('Expected the CaseGraph service')
    existing = {}
    markers = {item['marker'] for item in prepared}
    for row in call('GET', '/cases'):
        marker = row['draft'].get('notes', '').split('\n')[0]
        if marker in markers:
            if marker in existing:
                raise RuntimeError('Multiple existing cases for ' + marker)
            existing[marker] = row
    # Do not complete an interrupted draft if a user has edited its content.
    for item in prepared:
        row = existing.get(item['marker'])
        if row and row['status'] in ('DRAFT', 'REPORTED'):
            draft = copy.deepcopy(row['draft'])
            issue_id = 'casegraph:issue:' + str(row['id'])
            draft['graph']['nodes'] = [n for n in draft['graph']['nodes'] if n['node_id'] != issue_id]
            draft['graph']['edges'] = [e for e in draft['graph']['edges']
                                       if issue_id not in (e['source'], e['target'])]
            if draft != item['draft'] or row['solution'] not in ('', item['solution']):
                raise RuntimeError('Existing unfinished simulation was edited; preserved: ' + item['sample_id'])
    documents = {item['root']['source_document'] for item in prepared}
    recorded = {(node['source_document'], node['source_element_id']) for document in documents
                for node in call('GET', '/elements?' + urlencode({'document': document}))}
    receipt = []
    for item in prepared:
        row = existing.get(item['marker'])
        action = 'preserved' if row else 'created'
        if row is None:
            row = call('POST', '/cases', item['draft'])
        elif row['status'] in ('DRAFT', 'REPORTED'):
            action = 'resumed'
        path = '/cases/{}/status'.format(row['id'])
        if row['status'] == 'DRAFT':
            row = call('POST', path, dict(status='REPORTED'))
        if row['status'] == 'REPORTED':
            row = call('POST', path, dict(status='RESOLVED', solution=item['solution']))
        # Use the stored snapshot, preserving any later user revisions.
        root = next(n for n in row['draft']['graph']['nodes'] if n['node_id'] == row['draft']['graph']['root'])
        identity = (root['source_document'], root['source_element_id'])
        if identity not in recorded and root['properties'].get('simulation') is True:
            call('POST', '/elements', root)
            recorded.add(identity)
        receipt.append(dict(sample_id=item['sample_id'], case_id=row['id'], status=row['status'], action=action))
    return receipt


def backup_database():
    if not database.DATABASE_PATH.is_file():
        raise RuntimeError('Start the local CaseGraph service before importing')
    destination = database.DATA_DIR / 'backups' / ('before-simulations-' + datetime.now().strftime('%Y%m%d-%H%M%S-%f') + '.db')
    destination.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(database.DATABASE_PATH) as source, sqlite3.connect(destination) as target:
        source.backup(target)
    return destination


def write_report(catalog, prepared, receipt=(), path=REPORT):
    esc = lambda value: html.escape(str(value), quote=True)
    ids = {row['sample_id']: row['case_id'] for row in receipt}
    cards = []
    for case, item in zip(catalog['cases'], prepared):
        props = item['root']['properties']
        label = catalog['categories'][props['category']]['label']
        dimensions = ' · '.join('{} {} mm'.format(k, v) for k, v in props['dimensions'].items())
        steps = ''.join('<li>' + esc(step) + '</li>' for step in case['steps'])
        checks = ''.join('<li>' + esc(check) + '</li>' for check in case['checks'])
        parameters = ''.join('<tr><th>' + esc(k) + '</th><td>' + esc(v) + '</td></tr>' for k, v in case['parameters'].items())
        related = '；'.join(context['name'] for context in case['context'])
        case_id = 'RFI-' + str(ids[case['id']]) if case['id'] in ids else '尚未匯入'
        cards.append('''<details class="case" data-category="{category}">
<summary><span class="tag">{sample} · {case_id}</span><strong>{title}</strong><span class="category">{label}</span></summary>
<div class="body"><p class="meta">{family} ／ {type_name} · {level}<br>{dimensions}</p>
<h3>問題情境</h3><p>{problem}</p><h3>假設原因</h3><p>{cause}</p>
<h3>建議處理步驟</h3><ol>{steps}</ol><h3>完成後檢查</h3><ul>{checks}</ul>
<p class="expected">{expected}</p><h3>比較資料（模擬值）</h3><table>{parameters}</table>
<p class="meta">關係快照：{level}；{related}。以上關係均未在實際模型驗證。</p></div></details>'''.format(
            category=esc(props['category']), sample=esc(case['id']), case_id=esc(case_id), title=esc(case['title']),
            label=esc(label), family=esc(props['family']), type_name=esc(props['type_name']), level=esc(case['level']),
            dimensions=esc(dimensions), problem=esc(case['problem']), cause=esc(case['cause']), steps=steps,
            checks=checks, expected=esc(case['expected']), parameters=parameters, related=esc(related)))
    options = ''.join('<option value="{}">{}</option>'.format(esc(key), esc(value['label']))
                      for key, value in catalog['categories'].items())
    document = '''<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>25 個 BIM 模擬案例與解法</title>
<style>body{margin:0;background:#f4f7f8;color:#20323b;font:16px/1.7 "Microsoft JhengHei",sans-serif}
main{max-width:1050px;margin:auto;padding:32px 20px}h1{font-size:30px;margin:0}h3{font-size:17px;margin:20px 0 6px}
p{margin:8px 0}.intro{margin-bottom:24px}.notice{border-left:4px solid #cf972b;background:#fff7e7;padding:12px 16px}
.controls{display:flex;gap:10px;flex-wrap:wrap;margin:22px 0 10px}input,select,button{font:inherit;padding:9px 12px;border:1px solid #b9cbd1;border-radius:6px;background:white}
input{flex:1;min-width:230px}button{cursor:pointer;color:#17697a}.case{border:1px solid #d4e1e5;border-radius:8px;background:white;margin:12px 0;overflow:hidden}
summary{cursor:pointer;padding:16px}summary strong{margin-left:12px}.tag,.category{font-size:13px;color:#347080}.category{float:right;padding:2px 8px;background:#ecf5f7;border-radius:4px}
.body{padding:0 24px 20px;border-top:1px solid #e3ecee}.meta{color:#58717d;font-size:14px}.expected{padding:12px;background:#eef7f5;border-radius:4px}
li{margin:7px 0}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:7px 12px;border-bottom:1px solid #e1e8eb}th{width:40%;font-weight:500}
[hidden]{display:none!important}@media(max-width:650px){summary strong{display:block;margin:6px 0}.category{float:none}}
@media print{.controls{display:none}body{background:white}.case{break-inside:avoid}main{padding:0}}</style></head><body><main>
<div class="intro"><p class="meta">REVIT CASEGRAPH · 模擬案例庫</p><h1>25 個協調案例與處理方法</h1>
<p>門 6 · 插座／電氣裝置 5 · 牆 4 · 窗 2 · 樓板 2 · 詳圖元件 4 · 家具 2</p>
<p class="notice">__NOTICE__</p><p>在 Revit 的 CaseBase 工具列按「案例庫」，搜尋標題含「模擬」的案例；選取後按「查看／審查解法」即可閱讀處理方法。</p>
<p>選取模型元素後可在「相關案例」比較同類案例，取消「只看已驗證的解法」。選用「已記錄元素（跨專案）」可比較模擬元素；它們沒有實體模型可高亮。關係圖可勾選「檢視此案例的已儲存快照」。</p></div>
<div class="controls"><input id="search" aria-label="搜尋案例" placeholder="搜尋問題、族群或處理方法…"><select id="category" aria-label="篩選類別"><option value="">全部類別</option>__OPTIONS__</select><button id="expand">展開符合案例</button><button id="collapse">全部收合</button></div>
<p class="meta" id="count" aria-live="polite"></p>__CARDS__
</main><script>const cards=Array.from(document.querySelectorAll('.case')),search=document.getElementById('search'),category=document.getElementById('category');
function filter(){const q=search.value.trim().toLocaleLowerCase();let n=0;for(const card of cards){card.hidden=!!((category.value&&card.dataset.category!==category.value)||(q&&!card.textContent.toLocaleLowerCase().includes(q)));if(!card.hidden)n++;}document.getElementById('count').textContent='顯示 '+n+' / '+cards.length+' 個模擬案例';}
search.addEventListener('input',filter);category.addEventListener('change',filter);document.getElementById('expand').onclick=()=>cards.filter(c=>!c.hidden).forEach(c=>c.open=true);document.getElementById('collapse').onclick=()=>cards.forEach(c=>c.open=false);
let wasOpen=[];window.addEventListener('beforeprint',()=>{wasOpen=cards.map(c=>c.open);cards.filter(c=>!c.hidden).forEach(c=>c.open=true);});window.addEventListener('afterprint',()=>cards.forEach((c,i)=>c.open=wasOpen[i]));filter();</script></body></html>'''
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(document.replace('__NOTICE__', esc(catalog['notice'])).replace('__OPTIONS__', options)
                          .replace('__CARDS__', '\n'.join(cards)), encoding='utf-8')


def main():
    # Preserve old command bookmarks after the real-model catalog migration.
    # Do not accidentally recreate the retired synthetic library.
    if (database.DATA_DIR / 'model-case-import.json').is_file():
        from prepare_model_cases import main as prepare_model_main
        return prepare_model_main()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true', help='Import into the running localhost service; default validates/previews only')
    args = parser.parse_args()
    catalog, prepared = prepare_catalog()
    receipt, backup = [], None
    if args.apply:
        backup = backup_database()
        receipt = import_cases(api_call, prepared)
        stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
        receipt_path = database.DATA_DIR / ('simulation-import-' + stamp + '.json')
        receipt_path.write_text(json.dumps(dict(batch=catalog['batch'], backup=str(backup),
            catalog_sha256=hashlib.sha256(CATALOG.read_bytes()).hexdigest(), cases=receipt), ensure_ascii=False, indent=2), encoding='utf-8')
    write_report(catalog, prepared, receipt)
    print(json.dumps(dict(mode='imported' if args.apply else 'preview', count=len(prepared),
        categories=dict(Counter(item['root']['properties']['category'] for item in prepared)),
        actions=dict(Counter(row['action'] for row in receipt)), report=str(REPORT),
        backup=str(backup) if backup else None), ensure_ascii=True))


if __name__ == '__main__':
    main()
