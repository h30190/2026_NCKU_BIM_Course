# -*- coding: utf-8 -*-
"""Plan enumeration and bounded, resumable aggregation of per-view checks."""
from copy import deepcopy
from datetime import datetime
from casegraph.geometry_check import dispose
from casegraph.model_checks import CHECKS

PLAN_KINDS = dict(FloorPlan=u'樓層平面圖', CeilingPlan=u'天花平面圖',
                  EngineeringPlan=u'結構平面圖', AreaPlan=u'面積平面圖')


def view_caption(view):
    return u'{}（{}）'.format(view['name'], PLAN_KINDS.get(view['kind'], view['kind']))


def list_plan_views(doc):
    from Autodesk.Revit import DB
    from casegraph.extractor import id_value
    collector = DB.FilteredElementCollector(doc).OfClass(DB.ViewPlan)
    try:
        views = [dict(id=id_value(v.Id), name=v.Name, kind=str(v.ViewType))
                 for v in collector if not v.IsTemplate]
        return sorted(views, key=lambda v: (v['kind'], v['name'], v['id']))
    finally:
        dispose(collector)


class PlanCheckJob(object):
    """One step per ExternalEvent; never switch the active Revit view to scan.

    Per-view source/pair/time limits remain in force. Results are deduplicated,
    while their originating views and incomplete coverage are retained.
    """
    def __init__(self, views, scope, checks, options, graph_root, max_results=300):
        self.views, self.scope, self.options = views, scope, options
        self.checks = [code for code, _ in CHECKS if code in checks]
        self.index, self.max_results, self.keys = 0, max_results, {}
        self.report = dict(document=scope['document'], scope=scope, checks=self.checks,
            view_id='', view_name=u'所有平面圖', range_mode='plans', range_count=None,
            graph_root=graph_root, checked_at=datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
            results=[], warnings=[], sources=0, pairs=0, truncated=False, stale=False,
            unknown=0, findings=0, counts=dict((code, 0) for code in self.checks),
            skipped_joined=0, skipped_collinear=0, excluded_curve_elements=0,
            excluded_room_elements=0, excluded_room_sources=0,
            excluded_nonsolid_elements=0, excluded_nonsolid_sources=0, excluded_geometry_sources=0, geometry_failures=0,
            excluded_curve_sources=0, status='RUNNING', plan_views=deepcopy(views),
            plan_reports=[], completed_plans=0, total_plans=len(views), cancelled=False,
            **options)

    @property
    def done(self):
        return self.index >= len(self.views)

    def add(self, view, result=None, error=None):
        report = self.report
        summary = dict(view)
        if error is not None:
            summary.update(status='INCOMPLETE', sources=0, pairs=0, findings=0, unknown=1, truncated=True)
            report['truncated'] = True
            report['warnings'].append(u'「{}」未完成：{}'.format(view_caption(view), error))
        else:
            for key in ('sources', 'pairs', 'skipped_joined', 'skipped_collinear',
                        'excluded_room_elements', 'excluded_room_sources',
                        'excluded_curve_elements', 'excluded_curve_sources', 'excluded_nonsolid_elements',
                        'excluded_nonsolid_sources', 'excluded_geometry_sources', 'geometry_failures'):
                report[key] += result.get(key, 0)
            summary.update((k, result[k]) for k in ('status', 'sources', 'pairs', 'findings', 'unknown', 'truncated'))
            report['truncated'] = report['truncated'] or result['truncated']
            for warning in result['warnings']:
                report['warnings'].append(u'「{}」：{}'.format(view_caption(view), warning))
            for row in result['results']:
                ids = tuple(sorted(n['node_id'] for n in row['records']))
                # Different native warnings/diagnostic failures remain distinct.
                detail = row['message'] if row['check'] == 'warning' or row['status'] == 'UNKNOWN' else ''
                key = (row['check'], row['status'], ids, detail)
                existing = self.keys.get(key)
                if existing is not None:
                    if view['id'] not in [v['id'] for v in existing['views']]:
                        existing['views'].append(dict(view))
                elif len(report['results']) < self.max_results:
                    merged = deepcopy(row)
                    merged.update(id=str(len(report['results'])), views=[dict(view)])
                    report['results'].append(merged)
                    self.keys[key] = merged
                else:
                    report['truncated'] = True
                    warning = u'跨圖合併結果超過 {} 項，清單只顯示部分結果；請縮小範圍重查。'.format(self.max_results)
                    if warning not in report['warnings']:
                        report['warnings'].append(warning)
        report['plan_reports'].append(summary)
        self.index += 1
        report['completed_plans'] = self.index

    def finish(self, cancelled=False):
        report = self.report
        report['cancelled'] = cancelled
        if cancelled or not self.done:
            report['truncated'] = True
            report['warnings'].append(u'已停止；{} 張平面圖尚未檢查。'.format(len(self.views)-self.index))
        report['unknown'] = sum(r['status'] == 'UNKNOWN' for r in report['results'])
        report['findings'] = len(report['results'])-report['unknown']
        report['counts'] = dict((code, sum(r['check'] == code and r['status'] != 'UNKNOWN'
                                         for r in report['results'])) for code in self.checks)
        report['status'] = ('INCOMPLETE' if report['truncated'] or report['unknown'] or report['geometry_failures'] else
                            'REVIEW' if report['findings'] else 'NO_MATCH' if not report['sources'] else
                            'NOT_APPLICABLE' if set(self.checks).issubset(('clash', 'overlap')) and report['sources'] == report['excluded_geometry_sources'] else 'CLEAR')
        report['view_name'] = u'所有平面圖（{}／{} 張已處理）'.format(self.index, len(self.views))
        return report
