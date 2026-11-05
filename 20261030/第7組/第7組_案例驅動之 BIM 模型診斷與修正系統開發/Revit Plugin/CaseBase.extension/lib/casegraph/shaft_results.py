# -*- coding: utf-8 -*-
"""Presentation-only grouping of the existing shaft findings."""
from casegraph.shaft_issue import GROUPS, STATUSES


def group_label(row, mode):
    if mode == 'level':
        return (row.get('level_name') or '').strip() or u'未指定樓層'
    if mode == 'category':
        return GROUPS.get(row.get('group'), u'未分類')
    if mode == 'status':
        return STATUSES.get(row.get('status'), u'未判定')
    return u''


def grouped_rows(rows, mode):
    """Keep findings intact; order floor groups by elevation and judgments by urgency."""
    if mode == 'none':
        return list(rows)
    groups = {}
    for row in rows:
        label = group_label(row, mode)
        groups.setdefault(label, []).append(row)

    def order(label):
        members = groups[label]
        if mode == 'level':
            elevations = [r['level_elevation_mm'] for r in members if r.get('level_elevation_mm') is not None]
            return (label == u'未指定樓層' or not bool(elevations), min(elevations) if elevations else 0, label)
        codes = ('fire', 'maint', 'mep') if mode == 'category' else ('Violation', 'Warning', 'DataMissing')
        field = 'group' if mode == 'category' else 'status'
        code = members[0].get(field)
        return (codes.index(code) if code in codes else len(codes), label)

    result = []
    for label in sorted(groups, key=order):
        result.extend(groups[label])
    return result
