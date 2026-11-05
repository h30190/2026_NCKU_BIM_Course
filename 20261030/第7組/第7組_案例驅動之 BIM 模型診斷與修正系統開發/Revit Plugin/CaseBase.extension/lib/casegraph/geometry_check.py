# -*- coding: utf-8 -*-
"""Read-only solid intersection checks. All adapter calls require Revit API context."""
from datetime import datetime
import math
import time
from casegraph.geometry_rules import validate_rule

MM3_PER_FT3 = 304.8 ** 3


def finite_volume(solid):
    value = abs(float(solid.Volume)) * MM3_PER_FT3
    if math.isnan(value) or math.isinf(value):
        raise ValueError(u'幾何體積不是有限數值')
    return value


def dispose(value):
    try:
        value.Dispose()
    except Exception:
        pass


def intersect_solids(left, right, adapter, tolerance, expired=lambda: False):
    """Return proven overlap or UNKNOWN; a failed boolean can never mean PASS.

    Small intersections are unioned before applying the tolerance, so overlapping
    family solids are not double-counted. A proven subset above tolerance suffices.
    """
    problems = list(left['problems']) + list(right['problems'])
    if not left['solids'] or not right['solids']:
        return dict(status='UNKNOWN', volume_mm3=0., message=u'沒有可檢查的實體幾何。'+u'；'.join(problems))
    aggregate, maximum = None, 0.
    try:
        for a in left['solids']:
            for b in right['solids']:
                if expired():
                    return dict(status='UNKNOWN', volume_mm3=maximum, message=u'已達本次時間上限，實體檢查尚未完成。')
                intersection = None
                try:
                    intersection = adapter.boolean(a, b, 'Intersect')
                    volume = finite_volume(intersection)
                    maximum = max(maximum, volume)
                    if volume > tolerance:
                        return dict(status='VIOLATION', volume_mm3=volume, message=u'已確認實體重疊至少 {:.3f} mm³，大於容差 {:.3f} mm³。'.format(volume, tolerance))
                    if volume > 0:
                        if aggregate is None:
                            aggregate, intersection = intersection, None
                        else:
                            united = adapter.boolean(aggregate, intersection, 'Union')
                            dispose(aggregate)
                            aggregate = united
                        volume = finite_volume(aggregate)
                        maximum = max(maximum, volume)
                        if volume > tolerance:
                            return dict(status='VIOLATION', volume_mm3=volume, message=u'已確認多個實體交集的聯集至少 {:.3f} mm³，大於容差 {:.3f} mm³。'.format(volume, tolerance))
                except Exception as error:
                    problems.append(u'實體布林運算失敗：'+u'{}'.format(error))
                finally:
                    if intersection is not None:
                        dispose(intersection)
        if problems:
            return dict(status='UNKNOWN', volume_mm3=maximum, message=u'未能完整確認：'+u'；'.join(sorted(set(problems))))
        return dict(status='PASS', volume_mm3=maximum, message=u'未找到超過容差的實體重疊。')
    finally:
        if aggregate is not None:
            dispose(aggregate)


def check_rule(adapter, rule, max_sources=150, max_pairs=500, seconds=5., clock=None):
    validate_rule(rule)
    now = clock or getattr(time, 'monotonic', time.time)
    deadline = now()+seconds
    expired = lambda: now() >= deadline
    report = dict(rule=rule, document=rule['document'], view_id=adapter.view_id, view_name=adapter.view_name,
        checked_at=datetime.now().strftime('%Y-%m-%d %H:%M:%S'), results=[], sources=0, completed_sources=0,
        passed_sources=0, checked_pairs=0, excluded_hosts=0, truncated=False, warnings=[], stale=False)
    cache, pair_states = {}, {}

    def geometry(element):
        key = adapter.key(element)
        if key not in cache:
            cache[key] = adapter.geometry(element)
        return cache[key]

    def result(source, target, status, message, volume=0.):
        report['results'].append(dict(source=adapter.record(source), target=adapter.record(target) if target is not None else None,
                                      status=status, message=message, volume_mm3=volume))

    try:
        for source in adapter.sources(rule):
            if report['sources'] >= max_sources or expired():
                report['truncated'] = True
                break
            report['sources'] += 1
            source_problem = False
            try:
                left = geometry(source)
                if not left['solids']:
                    result(source, None, 'UNKNOWN', u'來源元素沒有可檢查的立體實體；二維詳圖不能判定為通過。'+u'；'.join(left['problems']))
                    report['completed_sources'] += 1
                    continue
                for candidate in adapter.candidates(source, rule):
                    if expired():
                        report['truncated'] = source_problem = True
                        result(source, None, 'UNKNOWN', u'已達時間上限，此來源元素的候選尚未全部檢查。')
                        break
                    if adapter.key(candidate) == adapter.key(source):
                        continue
                    pair = tuple(sorted((adapter.key(source), adapter.key(candidate))))
                    if rule['exclude_host'] and adapter.is_host(source, candidate):
                        report['excluded_hosts'] += 1
                        continue
                    if pair in pair_states:
                        source_problem = source_problem or pair_states[pair] != 'PASS'
                        continue
                    if report['checked_pairs'] >= max_pairs or expired():
                        report['truncated'] = source_problem = True
                        result(source, None, 'UNKNOWN', u'已達檢查數量或時間上限，此來源元素的候選尚未全部檢查。')
                        break
                    report['checked_pairs'] += 1
                    try:
                        checked = intersect_solids(left, geometry(candidate), adapter, rule['tolerance_mm3'], expired)
                        if checked['status'] == 'PASS' and adapter.joined(source, candidate):
                            checked = dict(status='UNKNOWN', volume_mm3=checked['volume_mm3'],
                                           message=u'兩元素已接合，接合後幾何可能移除原始重疊，須人工確認。')
                        if checked['status'] != 'PASS':
                            source_problem = True
                            result(source, candidate, checked['status'], checked['message'], checked['volume_mm3'])
                        pair_states[pair] = checked['status']
                    except Exception as error:
                        source_problem = True
                        pair_states[pair] = 'UNKNOWN'
                        result(source, candidate, 'UNKNOWN', u'無法讀取或檢查候選：'+u'{}'.format(error))
                if left['problems'] and not source_problem:
                    source_problem = True
                    result(source, None, 'UNKNOWN', u'來源幾何不完整：'+u'；'.join(left['problems']))
                if not report['truncated']:
                    report['completed_sources'] += 1
                    if not source_problem:
                        report['passed_sources'] += 1
            except Exception as error:
                result(source, None, 'UNKNOWN', u'來源元素檢查失敗：'+u'{}'.format(error))
            if report['truncated']:
                break
    except Exception as error:
        report['truncated'] = True
        report['warnings'].append(u'候選讀取未完成：'+u'{}'.format(error))
    finally:
        adapter.close()
    report['violations'] = sum(r['status'] == 'VIOLATION' for r in report['results'])
    report['unknown'] = sum(r['status'] == 'UNKNOWN' for r in report['results'])
    report['status'] = ('VIOLATION' if report['violations'] else 'INCOMPLETE' if report['truncated'] or report['unknown']
                        else 'NO_MATCH' if not report['sources'] else 'PASS')
    if report['truncated']:
        report['warnings'].append(u'結果未涵蓋全部候選，請縮小視圖後重查；不能視為全部通過。')
    return report


class RevitGeometryAdapter(object):
    def __init__(self, doc, view):
        from Autodesk.Revit import DB
        from casegraph.extractor import id_value
        self.DB, self.doc, self.view = DB, doc, view
        if not DB.FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id):
            raise ValueError(u'此視圖不支援模型元素檢查，請使用樓層／天花平面圖、剖面或三維模型視圖。')
        self.view_id, self.view_name = id_value(view.Id), view.Name
        self.owners, self.records = [], {}

    def key(self, element):
        return element.UniqueId

    def record(self, element):
        from casegraph.element_index import snapshot
        from casegraph.extractor import document_id, id_value
        key = self.key(element)
        if key not in self.records:
            try:
                self.records[key] = snapshot(self.doc, element)
            except Exception:
                identity = document_id(self.doc)
                self.records[key] = dict(node_id=identity+':'+key, node_type='ELEMENT', name=u'元素 '+id_value(element.Id),
                    source_document=identity, source_element_id=key, properties=dict(element_id=id_value(element.Id)))
        return self.records[key]

    def sources(self, rule):
        DB = self.DB
        collector = DB.FilteredElementCollector(self.doc, self.view.Id).WhereElementIsNotElementType().OfCategoryId(DB.ElementId(int(rule['source_category_id'])))
        self.owners.append(collector)
        if rule['scope'] == 'type':
            equals = DB.ParameterFilterRuleFactory.CreateEqualsRule(DB.ElementId(DB.BuiltInParameter.ELEM_TYPE_PARAM), DB.ElementId(int(rule['source_key'])))
            collector.WherePasses(DB.ElementParameterFilter(equals))
        elif rule['scope'] == 'instance':
            from System.Collections.Generic import List
            element = self.doc.GetElement(rule['source_key'])
            if element is None:
                dispose(collector)
                return
            collector.WherePasses(DB.ElementIdSetFilter(List[DB.ElementId]([element.Id])))
        try:
            for element in collector:
                if rule['scope'] != 'instance' or element.UniqueId == rule['source_key']:
                    yield element
        finally:
            dispose(collector)

    def outline(self, box):
        # Kept out of the candidates() generator: pyRevit's IronPython (Tracing + FullFrames)
        # cannot run list comprehensions inside generator functions.
        DB = self.DB
        points = [box.Transform.OfPoint(DB.XYZ(x, y, z)) for x in (box.Min.X, box.Max.X)
                  for y in (box.Min.Y, box.Max.Y) for z in (box.Min.Z, box.Max.Z)]
        pad = 1.e-6  # Broad phase only; actual intersection always uses full solids.
        return DB.Outline(DB.XYZ(min(p.X for p in points)-pad, min(p.Y for p in points)-pad, min(p.Z for p in points)-pad),
                          DB.XYZ(max(p.X for p in points)+pad, max(p.Y for p in points)+pad, max(p.Z for p in points)+pad))

    def candidates(self, source, rule):
        DB = self.DB
        box = source.get_BoundingBox(None)
        if box is None:
            raise ValueError(u'來源元素沒有可用的外框，無法完整找出相交候選。')
        outline = self.outline(box)
        collector = DB.FilteredElementCollector(self.doc, self.view.Id).WhereElementIsNotElementType().OfCategory(getattr(DB.BuiltInCategory, rule['target_category']))
        self.owners.extend([outline, collector])
        try:
            collector.WherePasses(DB.BoundingBoxIntersectsFilter(outline))
            for candidate in collector:
                yield candidate
        finally:
            dispose(collector)
            dispose(outline)

    def geometry(self, element):
        DB = self.DB
        options = DB.Options()
        options.DetailLevel = DB.ViewDetailLevel.Fine
        options.IncludeNonVisibleObjects = False
        options.ComputeReferences = False
        solids, problems = [], []
        visited = [0]

        def walk(geometry, depth=0):
            if geometry is None:
                return
            self.owners.append(geometry)
            for part in geometry:
                visited[0] += 1
                if depth > 8 or visited[0] > 1000 or len(solids) >= 64:
                    problems.append(u'族群幾何超出擷取上限')
                    return
                if isinstance(part, DB.Solid):
                    try:
                        if part.Faces.Size and finite_volume(part) > 0:
                            solids.append(part)
                    except Exception as error:
                        problems.append(u'實體體積無法讀取：'+u'{}'.format(error))
                elif isinstance(part, DB.GeometryInstance):
                    try:
                        # GetInstanceGeometry already applies instance placement;
                        # applying its Transform again would move rotated/mirrored families twice.
                        walk(part.GetInstanceGeometry(), depth+1)
                    except Exception as error:
                        problems.append(u'巢狀族群幾何無法讀取：'+u'{}'.format(error))
                elif isinstance(part, DB.Mesh):
                    problems.append(u'含網格幾何，無法以實體布林運算完整確認')
        try:
            walk(element.get_Geometry(options))
        finally:
            options.Dispose()
        return dict(solids=solids, problems=problems)

    def boolean(self, a, b, operation):
        return self.DB.BooleanOperationsUtils.ExecuteBooleanOperation(a, b, getattr(self.DB.BooleanOperationsType, operation))

    def is_host(self, source, candidate):
        host = getattr(source, 'Host', None)
        return host is not None and host.Id == candidate.Id

    def joined(self, source, candidate):
        if self.doc.IsFamilyDocument:
            return False
        return self.DB.JoinGeometryUtils.AreElementsJoined(self.doc, source, candidate)

    def close(self):
        for owner in reversed(self.owners):
            dispose(owner)
        self.owners = []
