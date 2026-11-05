# -*- coding: utf-8 -*-
"""Keep .NET text as Unicode until the HTTP transport encodes it as UTF-8.

IronPython 2.7.12's accelerated ASCII JSON encoder interprets characters such as
U+00B3 (the exponent in m3) as UTF-8 bytes and raises UnicodeDecodeError. The
Unicode encoder avoids that path and also preserves Chinese parameter names.
"""
import json

try:
    text_type = unicode
except NameError:
    text_type = str


def dumps(value, **options):
    options['ensure_ascii'] = False
    return json.dumps(value, **options)


def error_text(detail, status=None):
    """Summarize validation errors without echoing submitted graphs or parameters."""
    if isinstance(detail, list):
        fields = {'graph': u'模型資料', 'title': u'標題', 'description': u'問題描述',
                  'notes': u'補充說明', 'origin': u'檢查來源', 'target': u'目標元素',
                  'candidates': u'比對元素', 'issue_type': u'問題類型', 'priority': u'優先程度'}
        lines = []
        for entry in detail[:5]:
            if not isinstance(entry, dict):
                continue
            location = [text_type(part) for part in entry.get('loc', []) if part != 'body']
            field = fields.get(location[0], location[0]) if location else u'資料'
            message = text_type(entry.get('msg') or u'格式不正確').replace('Value error, ', '', 1)
            if message == 'Root must be an element with a persistent UniqueId':
                message = u'案例目標必須是有有效 UniqueId 的 Revit 元素。'
            line = u'{}：{}'.format(field, message[:240])
            if line not in lines:
                lines.append(line)
        if len(detail) > 5:
            lines.append(u'另有 {} 項資料需檢查。'.format(len(detail)-5))
        heading = u'資料驗證失敗' if status == 422 else u'服務回傳錯誤'
        return heading + u'\n' + u'\n'.join(lines or [u'請檢查表單內容後重試。'])
    if isinstance(detail, dict):
        detail = detail.get('message') or detail.get('msg') or u'服務回傳錯誤，請稍後重試。'
    message = text_type(detail or u'服務回傳錯誤，請稍後重試。')
    return message if len(message) <= 1200 else message[:1199]+u'…'
