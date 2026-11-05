"""Read-only checks of the installed service and model-backed CASE classification."""
import json
from pathlib import Path
import sys
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'backend'))
from app.case_scope import classify, related_matches


def api(path, payload=None):
    data = json.dumps(payload).encode('utf-8') if payload is not None else None
    request = Request('http://127.0.0.1:8000/graph'+path, data=data, headers={'Content-Type': 'application/json'})
    with urlopen(request, timeout=15) as response:
        return json.load(response)


health = api('/health')
assert health.get('case_policy') == 'same_type_and_issue', health
cases = api('/cases')
checks = []
for row in cases:
    draft = row['draft']
    assert draft['classification'] == classify(draft)
    graph = draft['graph']
    target = next(n for n in graph['nodes'] if n['node_id'] == graph['root'])
    query = dict(target=target, issue_type=draft['issue_type'], limit=50)
    result = api('/cases/related', query)
    assert all(related_matches(target, draft['issue_type'], r) for r in result['results'])
    ids = [r['case_id'] for r in result['results']]
    if row['status'] != 'ARCHIVED' and draft['classification']['element_type']['complete']:
        assert row['id'] in ids, (row['id'], ids)
    checks.append(dict(case_id=row['id'], issue_type=draft['issue_type'], matched_ids=ids,
                       classification=draft['classification']))

export = json.loads((ROOT / 'samples' / 'demo-model-export.json').read_text(encoding='utf-8-sig'))
graph = next(g for g in export['graphs'].values() if next(n for n in g['nodes'] if n['node_id'] == g['root'])['properties'].get('element_id') == '334206')
target = next(n for n in graph['nodes'] if n['node_id'] == graph['root'])
issue_type = next(row['draft']['issue_type'] for row in cases if row['draft']['graph']['root'] == graph['root'])
related = api('/cases/related', dict(target=target, issue_type=issue_type))
assert related['results'] and all(related_matches(target, issue_type, r) for r in related['results'])
data = ROOT / 'backend' / 'data'
(data / 'knowledge-case-preview.json').write_text(json.dumps(dict(graph=graph, related=related), ensure_ascii=False, indent=2), encoding='utf-8')
receipt = dict(build='2026.10.22.20', health=health, checked_cases=len(checks), checks=checks,
               preview_element_id='334206', preview_issue_type=issue_type,
               preview_case_ids=[r['case_id'] for r in related['results']], read_only=True)
(data / 'case-scope-verification.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(dict(checked_cases=len(checks), preview_case_ids=receipt['preview_case_ids'], policy=health['case_policy'])))
