# -*- coding: utf-8 -*-
"""Turn one model-check result into an unsaved, structured issue record.

The layout follows the existing case catalog: 模型讀取資料 / 檢查結果 / 建議處理 /
資料狀態, with a provenance line in the notes. `origin` keeps the same facts as
data, so a saved case can always be traced back to its check result.
"""
from casegraph.model_checks import LABELS, STATUSES, CLASH_TARGETS
from casegraph.plan_checks import view_caption

try:
    text_type = unicode
except NameError:
    text_type = str

ISSUE_TYPES = dict(duplicate='DESIGN_CONFLICT', overlap='GEOMETRY_CLASH', clash='GEOMETRY_CLASH',
                   mark='PARAMETER_ERROR', warning='OTHER')
RANGES = dict(view=u'目前視圖', selected=u'自訂範圍', plans=u'所有平面圖')
SETTINGS = ('position_mm', 'volume_mm3', 'clash_target', 'clash_skip_joined', 'clash_skip_collinear', 'clash_line_mm')
MAX_LISTED, MAX_ELEMENTS = 20, 50


def clip(text, limit):
    text = text_type(text or u'')
    return text if len(text) <= limit else text[:limit-1]+u'…'


def element_line(record):
    p = record['properties']
    return u'{} · {} · ID {}'.format(record['name'], p.get('category') or u'類別未提供', p.get('element_id') or u'—')


def check_issue(report, row, target=None):
    """Prefer the pinned target as root when it is part of the result."""
    records = row['records']
    if not records:
        raise ValueError(u'此結果沒有對應的模型元件，無法記錄為問題。')
    if any(r['source_document'] != report['document'] or not r.get('source_element_id') for r in records):
        raise ValueError(u'結果元件不屬於檢查文件，請重新檢查後再記錄。')
    root = next((r for r in records if target and r['node_id'] == target['node_id']), records[0])
    others = [r for r in records if r['node_id'] != root['node_id']]
    p = root['properties']
    label, status = LABELS[row['check']], STATUSES[row['status']]
    views = row.get('views') or [dict(id=report['view_id'], name=report['view_name'], kind='')]
    view_text = u'、'.join(view_caption(v) for v in row['views']) if row.get('views') else report['view_name']
    dims = p.get('dimensions') or {}
    size = u'；'.join(u'{} {:g} mm'.format(k, round(dims[k], 1)) for k in sorted(dims)
                     if isinstance(dims[k], (int, float)) and not isinstance(dims[k], bool))

    lines = [u'模型讀取資料', u'元件 ID：'+text_type(p.get('element_id') or u'—'),
             u'族群／類型：{}／{}'.format(p.get('family') or u'未提供', p.get('type_name') or u'未提供'),
             u'類別：'+(p.get('category') or u'未提供'), u'樓層：'+(p.get('level') or u'未提供'),
             u'所屬視圖：'+(p.get('owner_view_name') or u'模型元件')]
    if size:
        lines.append(u'尺寸：'+size)
    if others:
        lines.append(u'相關元件（{} 個）'.format(len(others)))
        lines.extend(u'・'+element_line(r) for r in others[:MAX_LISTED])
        if len(others) > MAX_LISTED:
            lines.append(u'・另有 {} 個未列出，完整清單見備註。'.format(len(others)-MAX_LISTED))
    lines += [u'', u'檢查結果', u'檢查項目：'+label, u'判定：'+status,
              u'檢查範圍：{} · {}'.format(RANGES.get(report['range_mode'], report['range_mode']), view_text),
              u'檢查時間：'+report['checked_at']]
    if row['check'] == 'clash':
        lines.append(u'碰撞對象：'+dict(CLASH_TARGETS).get(report.get('clash_target'), u'未提供'))
    lines += [u'說明：'+clip(row['message'], 3000), u'', u'建議處理', row['advice'], u'', u'資料狀態',
              u'此結果為「未能確認」，檢查未取得完整幾何證據；請先在 Revit 核對再提交。' if row['status'] == 'UNKNOWN'
              else u'元件與上列資料來自 Revit 模型檢查；結果供審查，未確認為設計錯誤，解法待記錄及驗證。']

    notes = [u'model-check:{}/{}#{}'.format(row['check'], root['source_element_id'], row['id']),
             u'文件：'+(p.get('document_title') or report['document']),
             u'UniqueId：'+root['source_element_id']]
    if others:
        notes.append(u'相關 UniqueId：'+u'、'.join(r['source_element_id'] for r in others))
    notes.append(u'檢查時間：'+report['checked_at'])

    elements = [dict(unique_id=r['source_element_id'], element_id=text_type(r['properties'].get('element_id') or u''),
                     name=r['name'], category=r['properties'].get('category'),
                     role='root' if r is root else 'related') for r in [root]+others]
    origin = dict(schema_version=1, kind='MODEL_CHECK', check=row['check'], status=row['status'],
                  result_id=text_type(row['id']), message=clip(row['message'], 5000), advice=row['advice'],
                  document=report['document'], range_mode=report['range_mode'], view_name=view_text,
                  views=[dict(id=text_type(v['id']), name=v['name'], kind=v.get('kind') or u'') for v in views],
                  checked_at=report['checked_at'], settings=dict((k, report[k]) for k in SETTINGS if k in report),
                  element_count=len(elements), elements=elements[:MAX_ELEMENTS])
    return dict(title=clip(u'模型檢查｜{}｜{} · ID {}'.format(label, root['name'], p.get('element_id') or u'—'), 200),
                description=clip(u'\n'.join(lines), 10000), notes=clip(u'\n'.join(notes), 5000),
                issue_type=ISSUE_TYPES[row['check']], priority='High' if row['status'] == 'OVERLAP' else 'Medium',
                origin=origin, root=root, related=others)


def origin_caption(origin):
    if not origin:
        return u''
    if origin.get('kind') == 'SHAFT_CHECK':
        from casegraph.shaft_issue import shaft_origin_caption
        return shaft_origin_caption(origin)
    return u'來源：模型檢查 #{} · {} · {} · {}'.format(origin['result_id'], LABELS.get(origin['check'], origin['check']),
                                                 STATUSES.get(origin['status'], origin['status']), origin['checked_at'])
