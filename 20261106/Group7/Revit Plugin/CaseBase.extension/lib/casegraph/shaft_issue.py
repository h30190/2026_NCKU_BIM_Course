# -*- coding: utf-8 -*-
"""Pure ShaftChecker-to-CaseBase mapping; no Revit or network dependencies."""
from copy import deepcopy

GROUPS = {'fire': u'防火時效及區劃', 'maint': u'維修門與維修空間', 'mep': u'管線分隔與間距'}
RULES = ('A1', 'A2', 'A4', 'A6', 'A7', 'A8', 'A9', 'P1', 'P2', 'P3', 'P4', 'P5')
STATUSES = {'Violation': u'違規', 'Warning': u'警告', 'DataMissing': u'資料不足'}
ISSUE_TYPES = {'fire': 'DESIGN_CONFLICT', 'maint': 'SPATIAL_CLEARANCE', 'mep': 'SPATIAL_CLEARANCE'}
try:
    text_type = unicode
except NameError:
    text_type = str


def clip(value, limit):
    value = text_type(value or u'')
    return value if len(value) <= limit else value[:limit-1]+u'…'


def shaft_issue(report, row):
    """One non-pass result becomes an unsaved issue, anchored in the host model.

    Linked rooms use their host link instance as the CaseBase root. Actual linked
    document/UniqueId references remain in source_refs, never as host element IDs.
    """
    if row['rule_id'] not in RULES or row['group'] not in GROUPS or row['status'] not in STATUSES:
        raise ValueError(u'此項目不是可記錄的管道間問題。')
    if report.get('stale'):
        raise ValueError(u'模型已變更，請重新執行管道間檢查。')
    records = row['records']
    if not records or row.get('unresolved'):
        raise ValueError(u'部分來源元件無法對應，請確認連結已載入並重新檢查。')
    if any(r['source_document'] != report['document'] or not r.get('source_element_id') for r in records):
        raise ValueError(u'結果與目前檢查文件不符，請重新檢查。')
    root, others = records[0], records[1:]
    label, status = GROUPS[row['group']], STATUSES[row['status']]
    advice = row.get('advice') or u'核對模型資料及檢查門檻，補齊資料或調整設計後重新檢查。'
    lines = [u'模型讀取資料', u'管道間：'+row['shaft_label'],
             u'來源模型：'+(row.get('source') or u'主模型'),
             u'房間 UniqueId：'+row['room_unique_id'], u'檢查時間：'+report['checked_at']]
    if row.get('source'):
        lines.append(u'案例基準為主模型中的連結實例；下列問題元件位於各自的來源模型。')
    lines += [u'', u'檢查結果', u'分類：'+label,
              u'規則：{} {}'.format(row['rule_id'], row['rule_name']), u'判定：'+status,
              u'規則性質：'+row['nature'], u'依據（原規則設定）：'+(row.get('reference') or u'未提供'),
              row['message']]
    if row.get('actual') is not None or row.get('required') is not None:
        lines.append(u'實測：{}；門檻：{} {}'.format(row.get('actual'), row.get('required'), row.get('unit') or u''))
    lines += [u'', u'相關元件']
    for ref in row['source_refs'][:30]:
        lines.append(u'・{} · {} · ID {} · UniqueId {}'.format(
            ref.get('source') or u'主模型', ref.get('name') or u'', ref['element_id'], ref.get('unique_id') or u'未取得'))
    if len(row['source_refs']) > 30:
        lines.append(u'其餘來源元件保存在結構化檢查記錄。')
    lines += [u'', u'資料狀態',
              u'資料不足，尚未完成判定；請補齊資料後重查。' if row['status'] == 'DataMissing'
              else u'此為規則引擎檢查結果，須經人工審查。']
    lines.extend(report.get('warnings') or [])
    elements = [dict(unique_id=r['source_element_id'], element_id=text_type(r['properties'].get('element_id') or ''),
                     name=clip(r['name'], 500), category=r['properties'].get('category'),
                     role='root' if index == 0 else 'related') for index, r in enumerate(records)]
    origin = dict(schema_version=1, kind='SHAFT_CHECK', check=row['group'], status=row['status'],
                  result_id=row['id'], message=clip(row['message'], 5000), advice=clip(advice, 2000),
                  document=report['document'], range_mode='model', view_name=report.get('view_name') or '', views=[],
                  checked_at=report['checked_at'], element_count=len(elements), elements=elements[:50],
                  settings=dict(rules_run=report['rules_run'], thresholds=deepcopy(report['thresholds']),
                                warnings=list(report.get('warnings') or [])),
                  rule_id=row['rule_id'], rule_name=clip(row['rule_name'], 200), nature=row['nature'],
                  reference=clip(row.get('reference'), 2000), actual=row.get('actual'), required=row.get('required'),
                  unit=row.get('unit'), shaft_key=row['shaft_key'], shaft_label=row['shaft_label'],
                  source_refs=deepcopy(row['source_refs']))
    return dict(title=clip(u'管道間檢查｜{}｜{} {}'.format(label, row['rule_id'], row['shaft_label']), 200),
                description=clip(u'\n'.join(lines), 10000),
                notes=clip(u'shaft-check:{}#{}\n管道間：{}\n規則：{}\n檢查時間：{}'.format(
                    row['shaft_key'], row['id'], row['shaft_label'], row['rule_id'], report['checked_at']), 5000),
                issue_type='PARAMETER_ERROR' if row['status'] == 'DataMissing' else ISSUE_TYPES[row['group']],
                priority='High' if row['status'] == 'Violation' else 'Medium',
                origin=origin, root=deepcopy(root), related=deepcopy(others))


def shaft_origin_caption(origin):
    return u'來源：管道間檢查 #{} · {} · {} · {} · {}'.format(
        origin['result_id'], GROUPS.get(origin['check'], origin['check']), origin['rule_id'],
        STATUSES.get(origin['status'], origin['status']), origin['checked_at'])


def fill_issue(panel, record):
    """Populate the existing form; saving/submitting stays with its existing workflow."""
    panel.set_target(record['root'], refresh=False, keep_checks=True, members=record['related'][:20])
    panel.reset_issue_form()
    panel.issue_origin = record['origin']
    panel.IssueTitle.Text, panel.Description.Text, panel.Notes.Text = record['title'], record['description'], record['notes']
    for box, code in [(panel.IssueType, record['issue_type']), (panel.Priority, record['priority'])]:
        box.SelectedItem = next(i for i in box.Items if i.data == code)
    panel.show_issue_origin()
    panel.set_suggest_context(record['origin'])
    panel.open_feature('issue')
    panel.schedule_refresh('related', 'graph')
    panel.Status.Text = u'已帶入管道間問題；核對後按「儲存草稿」或「提交」。'
