# -*- coding: utf-8 -*-
"""Excel-readable problem records, shared by CPython and pyRevit IronPython."""
from casegraph.shaft_issue import GROUPS, STATUSES

try:
    text_type = unicode
    string_types = (basestring,)
except NameError:
    text_type = str
    string_types = (str,)


HEADERS = (
    u'檢查時間', u'主模型', u'主模型識別碼', u'檢查視圖', u'問題編號',
    u'管道間', u'管道間來源', u'樓層', u'房間編號', u'房間名稱',
    u'房間元素 ID', u'房間 UniqueId', u'管道間識別碼',
    u'規則 ID', u'規則名稱', u'檢查類別', u'狀態', u'規則性質', u'規則依據',
    u'問題說明', u'實測值', u'要求值', u'單位', u'建議處理方法',
    u'相關元素 ID（含來源）', u'相關元素 UniqueId（含來源）',
    u'來源對應狀態', u'本次啟用規則', u'檢查範圍備註',
)


def _text(value):
    return u'' if value is None else text_type(value)


def _cell(value):
    text = _text(value)
    # Model names and notes are text, even if they resemble Excel formulas.
    # Numeric measurements (including negative values) remain numeric.
    if isinstance(value, string_types) and text.lstrip(u' \t\r\n')[:1] in ('=', '+', '-', '@'):
        text = u"'" + text
    return u'"' + text.replace(u'"', u'""') + u'"'


def _references(row, field):
    return u'\n'.join(u'{} | {}'.format(ref.get('source') or u'主模型', _text(ref.get(field)))
                      for ref in row.get('source_refs', []))


def problem_rows(report):
    """Keep every actionable finding, including unresolved source references."""
    return [row for row in report.get('results', []) if row.get('status') in STATUSES]


def write_problem_csv(path, report):
    """Write one row per finding with UTF-8 BOM; return the exported row count."""
    if report.get('stale'):
        raise ValueError(u'模型已變更，請重新執行管道間檢查後再匯出。')
    rows = problem_rows(report)
    if not rows:
        raise ValueError(u'本次沒有可匯出的管道間問題，請先執行檢查。')
    lines = [u','.join(_cell(value) for value in HEADERS)]
    for row in rows:
        values = (
            report.get('checked_at'), report.get('document_name') or report.get('document'),
            report.get('document'), report.get('view_name'), row.get('id'),
            row.get('shaft_label'), row.get('source') or u'主模型', row.get('level_name'),
            row.get('room_number'), row.get('room_name'), row.get('room_id'),
            row.get('room_unique_id'), row.get('shaft_key'),
            row.get('rule_id'), row.get('rule_name'), GROUPS.get(row.get('group'), row.get('group')),
            STATUSES[row['status']], row.get('nature'), row.get('reference'),
            row.get('message'), row.get('actual'), row.get('required'), row.get('unit'), row.get('advice'),
            _references(row, 'element_id'), _references(row, 'unique_id'),
            u'部分來源無法對應' if row.get('unresolved') else u'已對應',
            u'、'.join(report.get('rules_run', [])), u'\n'.join(report.get('warnings', [])),
        )
        lines.append(u','.join(_cell(value) for value in values))
    # Python 2's csv.writer cannot write Unicode directly. Quote all fields and
    # encode once, preserving embedded newlines, quotes and commas in evidence.
    payload = (u'\r\n'.join(lines) + u'\r\n').encode('utf-8-sig')
    with open(path, 'wb') as stream:
        stream.write(payload)
    return len(rows)
