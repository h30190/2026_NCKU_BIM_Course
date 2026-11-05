"""Prepare and atomically replace the generated catalog with real-model cases."""
from __future__ import annotations

import argparse
import ntpath
import copy
import hashlib
import html
import json
import sqlite3
from datetime import datetime
from pathlib import Path

from app import database
from app.graph_api import attach_issue
from app.graph_models import Draft, Node
from import_simulated_cases import prepare_catalog

ROOT = Path(__file__).resolve().parents[1]
EXPORT = ROOT / 'samples' / 'demo-model-export.json'
TOPICS = ROOT / 'samples' / 'model-case-topics.json'
REPORT = ROOT / 'samples' / 'model-cases.html'


def prepare(export_path=EXPORT, topics_path=TOPICS):
    export = json.loads(Path(export_path).read_text(encoding='utf-8'))
    topics = json.loads(Path(topics_path).read_text(encoding='utf-8'))
    records = {n['properties']['element_id']: n for n in export['elements']}
    if export['revit_version'] != '2022' or export['errors']:
        raise ValueError('A complete Revit 2022 export is required')
    prepared = []
    for index, topic in enumerate(topics['cases'], 1):
        root = records[topic['element_id']]
        graph = copy.deepcopy(export['graphs'][root['source_element_id']])
        if graph['root'] != root['node_id'] or next(n for n in graph['nodes'] if n['node_id'] == graph['root']) != root:
            raise ValueError('Root differs from the exported graph')
        comparison = records.get(topic.get('compare_id'))
        if topic.get('compare_id') and comparison is None:
            raise ValueError('Missing comparison element')
        if comparison:
            if comparison['properties']['category_id'] != root['properties']['category_id']:
                raise ValueError('Comparison must use the same category')
            if comparison['node_id'] not in {n['node_id'] for n in graph['nodes']}:
                graph['nodes'].append(copy.deepcopy(comparison))
            graph['edges'].append(dict(source=root['node_id'], target=comparison['node_id'],
                relationship_type='RELATED_TO', validation_status='unverified',
                evidence='案例比較對象（同類別）；此關係不表示幾何相鄰或碰撞。'))
        for node in graph['nodes']:
            Node.model_validate(node)
            if (node['source_document'] != export['document'] or not node.get('source_element_id')
                    or node['node_id'] != export['document'] + ':' + node['source_element_id']
                    or node['properties'].get('simulation')):
                raise ValueError('Every model node must use an actual exported identity')
        p = root['properties']
        facts = '\n'.join([
            '來源模型：' + export['title'], '元素 ID：' + p['element_id'],
            '類別：' + p['category'], '族群／類型：{}／{}'.format(p.get('family') or '未提供', p.get('type_name') or root['name']),
            '樓層：' + (p.get('level') or '未提供'),
            '所屬視圖：' + (p.get('owner_view_name') or '模型元件'),
            '模型尺寸：' + ('；'.join('{} {:g} mm'.format(k, v) for k, v in sorted(p.get('dimensions', {}).items())) or '標準尺寸欄位未提供')])
        if comparison:
            facts += '\n比較對象：{} · ID {}'.format(comparison['name'], comparison['properties']['element_id'])
        case_key = 'CASE-{:03}'.format(index)
        marker = 'model-case:{}/{}'.format(topics['batch'], case_key)
        note = '元素與關係為 Revit 擷取資料；檢查項目及處理方式待確認，未宣告模型已有錯誤或解法已驗證。'
        graph['warnings'].append(note)
        draft = Draft.model_validate(dict(title='{}｜{}'.format(case_key, topic['title']),
            description='模型資料\n' + facts + '\n\n檢查項目\n' + topic['review'] + '\n\n' + note,
            issue_type=topic['issue_type'], priority=topic['priority'],
            notes=marker + '\n來源路徑：' + ntpath.basename(export['source_path']) + '\nUniqueId：' + root['source_element_id']
                + '\n擷取時間：' + export['exported_at'], graph=graph)).model_dump(mode='json')
        solution = '\n'.join(['建議處理方式（待確認）',
            *['{}. {}'.format(i, step) for i, step in enumerate(topic['steps'], 1)], '', '完成後檢查',
            *['□ ' + check for check in topic['checks']], '', '完成後請記錄實際檢查結果，再由審核者確認。'])
        if '模擬' in json.dumps(dict(draft=draft, solution=solution), ensure_ascii=False):
            raise ValueError('Unexpected old catalog wording')
        prepared.append(dict(key=case_key, marker=marker, draft=draft, root=copy.deepcopy(root),
                             solution=solution, topic=topic, facts=facts))
    if len(prepared) != 25 or len({p['root']['source_element_id'] for p in prepared}) != 25:
        raise ValueError('Exactly 25 distinct real root elements are required')
    return export, prepared


def bare_draft(payload, case_id):
    draft = copy.deepcopy(payload)
    issue_id = 'casegraph:issue:' + str(case_id)
    graph = draft['graph']
    graph['nodes'] = [n for n in graph['nodes'] if n['node_id'] != issue_id]
    graph['edges'] = [e for e in graph['edges'] if issue_id not in (e['source'], e['target'])]
    return draft


def migrate(path, prepared, legacy, backup_dir):
    """Only replace unchanged generated cases; preserve IDs and previous revisions.

    Preflight and all writes share one SQLite write transaction, so a concurrent
    edit cannot be overwritten and a failed import cannot leave a partial batch.
    """
    path = Path(path)
    backup = Path(backup_dir) / ('before-model-cases-' + datetime.now().strftime('%Y%m%d-%H%M%S-%f') + '.db')
    backup.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(path) as source, sqlite3.connect(backup) as target:
        source.backup(target)
    db = sqlite3.connect(path)
    db.row_factory = sqlite3.Row
    receipt = []
    try:
        db.execute('PRAGMA foreign_keys=ON')
        db.execute('BEGIN IMMEDIATE')
        rows = list(db.execute('SELECT c.*,r.payload FROM graph_cases c JOIN graph_revisions r ON c.id=r.case_id AND c.revision=r.version'))
        marked = {}
        for row in rows:
            marker = json.loads(row['payload']).get('notes', '').split('\n')[0]
            if marker in marked and marker:
                raise ValueError('Duplicate import marker')
            marked[marker] = row
        planned = []
        for old, item in zip(legacy, prepared):
            existing = marked.get(item['marker'])
            if existing:
                if (bare_draft(json.loads(existing['payload']), existing['id']) != item['draft']
                        or existing['solution'] != item['solution'] or existing['status'] != 'RESOLVED'):
                    raise ValueError('Real-model case was edited; preserved: ' + item['key'])
                planned.append((old, item, existing, False))
                continue
            row = marked.get(old['marker'])
            if row is None or row['status'] != 'RESOLVED' or row['reviewer'] or row['solution'] != old['solution']:
                raise ValueError('Expected unchanged generated case: ' + old['sample_id'])
            if bare_draft(json.loads(row['payload']), row['id']) != old['draft']:
                raise ValueError('Generated case was edited; preserved: ' + old['sample_id'])
            planned.append((old, item, row, True))
        for old, item, row, update in planned:
            if update:
                version = row['revision'] + 1
                draft = attach_issue(Draft.model_validate(item['draft']), row['id'])
                db.execute('INSERT INTO graph_revisions(case_id,version,payload) VALUES (?,?,?)', (row['id'], version, draft.model_dump_json()))
                db.execute('UPDATE graph_cases SET revision=?,solution=? WHERE id=?', (version, item['solution'], row['id']))
                db.execute('INSERT INTO graph_history(case_id,status,solution,reviewer) VALUES (?,?,?,?)',
                           (row['id'], 'RESOLVED', item['solution'], ''))
            root = item['root']
            db.execute('INSERT OR IGNORE INTO graph_elements VALUES (?,?,?)',
                       (root['source_document'], root['source_element_id'], json.dumps(root, ensure_ascii=False)))
            old_root = old['root']
            stored = db.execute('SELECT payload FROM graph_elements WHERE document=? AND unique_id=?',
                                (old_root['source_document'], old_root['source_element_id'])).fetchone()
            if stored and json.loads(stored['payload']) == old_root:
                db.execute('DELETE FROM graph_elements WHERE document=? AND unique_id=?',
                           (old_root['source_document'], old_root['source_element_id']))
            receipt.append(dict(key=item['key'], case_id=row['id'], element_id=root['properties']['element_id'],
                unique_id=root['source_element_id'], action='updated' if update else 'preserved'))
        db.commit()
    except Exception:
        db.rollback()
        raise
    finally:
        db.close()
    return receipt, str(backup)


def write_report(export, prepared, receipt=(), path=REPORT):
    esc = lambda value: html.escape(str(value), quote=True)
    ids = {r['key']: r['case_id'] for r in receipt}
    cards = []
    for item in prepared:
        cards.append('<details open><summary>{}</summary><pre>{}</pre><p>{}</p><pre>{}</pre></details>'.format(
            esc(item['draft']['title'] + (' · RFI-' + str(ids[item['key']]) if item['key'] in ids else '')),
            esc(item['facts']), esc(item['topic']['review']), esc(item['solution'])))
    document = '''<!doctype html><html lang="zh-Hant"><meta charset="utf-8"><title>U1–U21 元件案例庫</title>
<style>body{font:16px/1.7 "Microsoft JhengHei",sans-serif;background:#f4f7f8;color:#20323b;max-width:1000px;margin:30px auto;padding:16px}details{background:white;border:1px solid #d4e1e5;border-radius:8px;margin:14px 0;padding:18px}summary{font-weight:bold;cursor:pointer}pre{white-space:pre-wrap;font:inherit}input{font:inherit;width:95%;padding:12px}p{color:#52677a}</style>
<h1>U1–U21 元件案例庫 · 25 個案例</h1><p>來源：__MODEL__</p>
<p>在 Revit 開啟來源模型，在「案例庫」選取案例即可高亮對應元件。處理方式待檢查及審核確認。</p>
<input aria-label="搜尋案例" placeholder="搜尋名稱、元件 ID 或處理方式" oninput="for(const c of document.querySelectorAll('details'))c.hidden=!c.textContent.toLowerCase().includes(this.value.toLowerCase())">
__CARDS__</html>'''
    Path(path).write_text(document.replace('__MODEL__', esc(ntpath.basename(export['source_path']))).replace('__CARDS__', '\n'.join(cards)), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    export, prepared = prepare()
    receipt, backup = [], None
    if args.apply:
        _, legacy = prepare_catalog()
        receipt, backup = migrate(database.DATABASE_PATH, prepared, legacy, database.DATA_DIR / 'backups')
        output = dict(document=export['document'], source_path=ntpath.basename(export['source_path']), cases=receipt,
                      backup=backup, export_sha256=hashlib.sha256(EXPORT.read_bytes()).hexdigest())
        (database.DATA_DIR / 'model-case-import.json').write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding='utf-8')
    else:
        receipt_path = database.DATA_DIR / 'model-case-import.json'
        if receipt_path.is_file():
            previous = json.loads(receipt_path.read_text(encoding='utf-8'))
            if (previous['document'] == export['document'] and
                    {r['unique_id'] for r in previous['cases']} == {p['root']['source_element_id'] for p in prepared}):
                receipt = previous['cases']
    write_report(export, prepared, receipt)
    # Keep existing report bookmarks useful after replacement.
    write_report(export, prepared, receipt, ROOT / 'samples' / 'simulated-cases.html')
    print(json.dumps(dict(count=len(prepared), applied=args.apply, backup=backup, report=str(REPORT)), ensure_ascii=True))


if __name__ == '__main__':
    main()
