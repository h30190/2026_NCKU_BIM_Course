# -*- coding: utf-8 -*-
"""Bounded, read-only common model checks. Revit access lives in the adapter."""
from copy import deepcopy
from datetime import datetime
import math
import time
import traceback
from casegraph.geometry_check import RevitGeometryAdapter, intersect_solids, dispose
from casegraph.geometry_rules import TARGETS

CHECKS = [('duplicate', u'疑似重複元件'), ('overlap', u'同類元件實體重疊'),
          ('clash', u'不同類型／類別碰撞'),
          ('mark', u'重複 Mark 編號'), ('warning', u'Revit 現有警告')]
CLASH_TARGETS = [('ALL', u'全部其他類型／類別（所選範圍）')] + TARGETS + [
    ('OST_Doors', u'門'), ('OST_Windows', u'窗'), ('OST_PipeCurves', u'管道'),
    ('OST_DuctCurves', u'風管'), ('OST_PipeFitting', u'管道配件'),
    ('OST_DuctFitting', u'風管配件'), ('OST_CableTray', u'電纜架'),
    ('OST_Conduit', u'電線管'), ('OST_ElectricalFixtures', u'電氣裝置')]
LABELS = dict(CHECKS)
STATUSES = dict(SUSPECT=u'待確認', OVERLAP=u'確認有重疊', WARNING=u'Revit 警告', UNKNOWN=u'未能確認')
ADVICE = dict(duplicate=u'比較尺寸、方向、階段及設計選項；確認是多放的副本後，再由你決定如何處理。',
              clash=u'核對兩邊元件的用途、宿主與接合關係；若非設計所需，協調改位、改尺寸、改路徑或開孔後重新檢查。',
              overlap=u'檢查是否刻意接合或嵌入；若屬錯誤，調整位置、尺寸或開孔後重新檢查。',
              mark=u'確認此類別是否要求編號唯一；需要時修改個別元件的 Mark，再重新檢查。',
              warning=u'依 Revit 警告內容檢視相關元件；可在「管理 → 檢閱警告」核對完整警告。')


def close_values(left, right, tolerance):
    return (left is not None and right is not None and len(left) == len(right)
            and all(abs(a-b) <= tolerance for a, b in zip(left, right)))


def near_bounds(a, b, pad=0.):
    return all(a[i] <= b[i+3]+pad and b[i] <= a[i+3]+pad for i in range(3))


def duplicate_position(a, b, mm):
    if a['type'] in ('', '-1') or a['type'] != b['type'] or a['category'] != b['category']:
        return False
    if not close_values(a['bounds'], b['bounds'], mm):
        return False
    if a['placement'] is None or b['placement'] is None:
        return None
    kind_a, points_a, direction_a = a['placement']
    kind_b, points_b, direction_b = b['placement']
    if kind_a != kind_b or not close_values(direction_a, direction_b, 1.e-6):
        return False
    return close_values(points_a, points_b, mm) or (kind_a.startswith('curve:') and
        close_values(points_a, tuple(v for i in range(len(points_b)-3, -1, -3) for v in points_b[i:i+3]), mm))


def collinear_lines(a, b, mm):
    """Only straight LocationCurves qualify; distance is measured in 3D mm."""
    try:
        placements = [a.get('placement'), b.get('placement')]
        if any(p is None or p[0] != 'curve:Line' for p in placements):
            return False
        points = [p[1] for p in placements]
        if any(len(p) < 6 or len(p) % 3 or any(math.isnan(v) or math.isinf(v) for v in p) for p in points):
            return False
        subtract = lambda p, q: tuple(x-y for x, y in zip(p, q))
        cross = lambda p, q: (p[1]*q[2]-p[2]*q[1], p[2]*q[0]-p[0]*q[2], p[0]*q[1]-p[1]*q[0])
        length = lambda p: math.sqrt(sum(v*v for v in p))
        vectors = [subtract(p[-3:], p[:3]) for p in points]
        lengths = [length(v) for v in vectors]
        if any(v <= 1.e-9 or math.isinf(v) for v in lengths):
            return False
        directions = [tuple(v/n for v in vector) for vector, n in zip(vectors, lengths)]
        # Opposite directions are equivalent, but merely intersecting lines are not.
        if length(cross(*directions)) > 1.e-6:
            return False
        return all(length(cross(subtract(endpoint, points[1-i][:3]), directions[1-i])) <= mm
                   for i, p in enumerate(points) for endpoint in (p[:3], p[-3:]))
    except (TypeError, ValueError, IndexError, OverflowError):
        return False


def run_checks(adapter, scope, checks, position_mm=1., volume_mm3=0., max_sources=150,
               max_pairs=500, max_results=300, seconds=5., clock=None, clash_target='ALL',
               clash_skip_joined=True, clash_skip_collinear=True, clash_line_mm=1., element_keys=None, peers=False):
    """peers: compare sources for duplicates/overlap with nearby same-category elements too.

    Used when the sources are a selection: a selected element can duplicate or overlap
    an unselected one, which a sources-only comparison would never see.
    """
    selected = [code for code, _ in CHECKS if code in checks]
    if not selected:
        raise ValueError(u'請至少選擇一項檢查。')
    allowed_keys = set(element_keys) if element_keys is not None else None
    if allowed_keys is not None and not allowed_keys:
        raise ValueError(u'自訂範圍沒有元素，請重新框選。')
    if 'clash' in selected and clash_target not in dict(CLASH_TARGETS):
        raise ValueError(u'碰撞目標類別無效。')
    if any(math.isnan(v) or math.isinf(v) or v < 0 for v in (position_mm, volume_mm3, clash_line_mm)):
        raise ValueError(u'容差必須是大於或等於 0 的有限數值。')
    now = clock or getattr(time, 'monotonic', time.time)
    deadline = now()+seconds
    expired = lambda: now() >= deadline
    report = dict(document=scope['document'], scope=scope, checks=selected, position_mm=position_mm,
        volume_mm3=volume_mm3, view_id=adapter.view_id, view_name=adapter.view_name,
        checked_at=datetime.now().strftime('%Y-%m-%d %H:%M:%S'), results=[], warnings=[], sources=0,
        pairs=0, truncated=False, stale=False, clash_target=clash_target,
        clash_skip_joined=clash_skip_joined, clash_skip_collinear=clash_skip_collinear,
        clash_line_mm=clash_line_mm, skipped_joined=0, skipped_collinear=0,
        range_mode='selected' if allowed_keys is not None else 'view',
        range_count=len(allowed_keys) if allowed_keys is not None else None)
    records, metadata, geometry, joins = {}, {}, {}, {}
    geometry_errors, geometry_failures, nonsolid = {}, set(), set()
    curve_exclusions, excluded_curve_sources = {}, set()
    room_exclusions = {}
    unsupported = set()
    counted_pairs, clash_pairs = set(), set()
    elements = []

    def add(code, status, items, message):
        # Collision rows must contain actual solids on every displayed element.
        # Failed extraction remains a coverage warning, not a fictitious clash.
        if code in ('clash', 'overlap') and any(collision_geometry(e) is None for e in items):
            return
        if len(report['results']) >= max_results:
            report['truncated'] = True
            return
        nodes = []
        for element in items:
            key = adapter.key(element)
            if key not in records:
                records[key] = adapter.record(element)
            nodes.append(records[key])
        report['results'].append(dict(id=str(len(report['results'])), check=code, status=status,
                                     records=nodes, message=message, advice=ADVICE[code]))

    def shape(element):
        key = adapter.key(element)
        if key in geometry_errors:
            raise geometry_errors[key]
        if key not in geometry:
            try:
                geometry[key] = adapter.geometry(element)
            except Exception as error:
                geometry_errors[key] = error
                raise
        return geometry[key]

    def geometry_warning(element, message):
        key = adapter.key(element)
        if key not in geometry_failures:
            geometry_failures.add(key)
            if len(geometry_failures) <= 5:
                report['warnings'].append(u'元素 {} 的實體幾何未能完整讀取，未列入碰撞結果：{}'.format(
                    getattr(element, 'Id', key), message[:300]))

    def collision_geometry(element):
        key = adapter.key(element)
        if exclude_room(element):
            unsupported.add(key)
            return None
        try:
            data = shape(element)
        except Exception as error:
            unsupported.add(key)
            geometry_warning(element, u'{}'.format(error))
            return None
        if not data['solids']:
            unsupported.add(key)
            if not curve_exclusions.get(key):
                nonsolid.add(key)
            if data['problems']:
                geometry_warning(element, u'；'.join(data['problems']))
            return None
        return data

    def exclude_curve(element):
        key = adapter.key(element)
        if key not in curve_exclusions:
            curve_exclusions[key] = adapter.is_clash_curve(element)
        return curve_exclusions[key]

    def exclude_room(element):
        key = adapter.key(element)
        if key not in room_exclusions:
            room_exclusions[key] = adapter.is_clash_room(element)
        return room_exclusions[key]

    def reserve_pair(a, b):
        pair = tuple(sorted((adapter.key(a), adapter.key(b))))
        if pair not in counted_pairs:
            if report['pairs'] >= max_pairs:
                report['truncated'] = True
                return False
            counted_pairs.add(pair)
            report['pairs'] += 1
        return True

    def joined(a, b):
        pair = tuple(sorted((adapter.key(a), adapter.key(b))))
        if pair not in joins:
            try:
                joins[pair] = (adapter.joined(a, b), None)
            except Exception as error:
                joins[pair] = (False, error)
        value, error = joins[pair]
        if error is not None:
            raise error
        return value

    def check_source_geometry(element, code):
        data_geometry = collision_geometry(element)
        if data_geometry is not None and data_geometry['problems']:
            add(code, 'UNKNOWN', [element], u'幾何不完整：'+u'；'.join(data_geometry['problems']))

    def check_overlap(a, b, code):
        try:
            left = collision_geometry(a)
            if left is None:
                return
            right = collision_geometry(b)
            if right is None:
                return
            checked = intersect_solids(left, right, adapter, volume_mm3, expired)
            if checked['status'] == 'VIOLATION':
                add(code, 'OVERLAP', [a, b], checked['message']+u'\n有重疊不一定是設計錯誤，請核對宿主、接合、階段及設計選項。')
            elif checked['status'] == 'UNKNOWN':
                add(code, 'UNKNOWN', [a, b], checked['message'])
            elif joined(a, b):
                add(code, 'UNKNOWN', [a, b], u'元件已接合，接合後幾何可能移除原有重疊，須人工確認。')
        except Exception as error:
            add(code, 'UNKNOWN', [a, b], u'實體檢查失敗：'+u'{}'.format(error))

    try:
        for element in adapter.sources(scope):
            if expired():
                report['truncated'] = True
                break
            if allowed_keys is not None and adapter.key(element) not in allowed_keys:
                continue
            if len(elements) >= max_sources:
                report['truncated'] = True
                break
            elements.append(element)
        report['sources'] = len(elements)
        members = dict((adapter.key(e), e) for e in elements)
        # Fast warning/parameter checks precede expensive geometry operations.
        if 'warning' in selected and elements:
            try:
                for i, warning in enumerate(adapter.warnings()):
                    if i >= 1000 or expired() or len(report['results']) >= max_results:
                        report['truncated'] = True
                        break
                    ids = list(dict.fromkeys(warning['keys']))
                    involved = [members[key] for key in ids if key in members]
                    if involved:
                        outside = len(ids)-len(involved)
                        message = warning['message']
                        if outside:
                            message += u'\n另有 {} 個關聯元素不在本次範圍；定位只選取範圍內元素。'.format(outside)
                        add('warning', 'WARNING', involved, message)
            except Exception as error:
                add('warning', 'UNKNOWN', [], u'無法完整讀取 Revit 警告：'+u'{}'.format(error))
        if 'mark' in selected:
            marks = {}
            for element in elements:
                if expired():
                    report['truncated'] = True
                    break
                try:
                    category, mark = adapter.mark(element)
                    if mark and mark.strip():
                        marks.setdefault((category, mark.strip()), []).append(element)
                except Exception as error:
                    add('mark', 'UNKNOWN', [element], u'編號讀取失敗：'+u'{}'.format(error))
            for (_, mark), group in sorted(marks.items()):
                if len(group) > 1:
                    add('mark', 'SUSPECT', group, u'同類別內 {} 個元件使用相同 Mark「{}」。是否需唯一編號須按專案要求確認。'.format(len(group), mark))
        if any(code in selected for code in ('duplicate', 'overlap', 'clash')):
            geometry_code = 'clash' if 'clash' in selected else 'overlap'
            for element in elements:
                if expired() or len(report['results']) >= max_results:
                    report['truncated'] = True
                    break
                try:
                    if ('clash' in selected or 'overlap' in selected) and exclude_room(element):
                        unsupported.add(adapter.key(element))
                        if 'duplicate' not in selected:
                            continue
                    if 'clash' in selected and exclude_curve(element):
                        excluded_curve_sources.add(adapter.key(element))
                        if selected == ['clash']:
                            continue
                    data = adapter.metadata(element)
                    if data['bounds'] is None:
                        raise ValueError(u'沒有可用的幾何外框')
                    metadata[adapter.key(element)] = data
                except Exception as error:
                    add('duplicate' if 'duplicate' in selected else geometry_code, 'UNKNOWN', [element],
                        u'位置／外框未能讀取，重複與重疊檢查無法涵蓋此元素：'+u'{}'.format(error))
                    continue
                if 'overlap' in selected:
                    check_source_geometry(element, 'overlap')
            # Candidate collection is view-scoped but deliberately not restricted
            # to the source type/category. Bounds only reduce the search space.
            if 'clash' in selected:
                stop_clashes = False
                for a in elements:
                    if stop_clashes:
                        break
                    if (room_exclusions.get(adapter.key(a)) or adapter.key(a) in excluded_curve_sources
                            or adapter.key(a) in nonsolid):
                        continue
                    ma = metadata.get(adapter.key(a))
                    if ma is None:
                        continue
                    if expired() or len(report['results']) >= max_results:
                        report['truncated'] = True
                        break
                    considered = False
                    try:
                        for b in adapter.clash_candidates(a, ma['bounds'], clash_target):
                            if expired() or len(report['results']) >= max_results:
                                report['truncated'] = stop_clashes = True
                                break
                            if allowed_keys is not None and adapter.key(b) not in allowed_keys:
                                continue
                            pair = tuple(sorted((adapter.key(a), adapter.key(b))))
                            if pair[0] == pair[1]:
                                continue
                            if pair in clash_pairs:
                                considered = True
                                continue
                            try:
                                # CurveElement is a drawn/boundary curve, not a
                                # physical object with a LocationCurve (pipe, wall,
                                # beam). Exclude before metadata, pair budget or solids.
                                if exclude_room(b) or exclude_curve(b) or adapter.key(b) in nonsolid:
                                    continue
                                if adapter.key(b) not in metadata:
                                    metadata[adapter.key(b)] = adapter.metadata(b)
                                mb = metadata[adapter.key(b)]
                                same_type = (ma['category'] == mb['category'] and ma['type'] not in ('', '-1') and ma['type'] == mb['type'])
                                if same_type:
                                    continue
                                if mb['bounds'] is None:
                                    raise ValueError(u'候選元素沒有可用外框')
                                if not near_bounds(ma['bounds'], mb['bounds'], .001):
                                    continue
                            except Exception as error:
                                considered = True
                                if not reserve_pair(a, b):
                                    stop_clashes = True
                                    break
                                clash_pairs.add(pair)
                                add('clash', 'UNKNOWN', [a, b], u'候選讀取失敗：'+u'{}'.format(error))
                                continue
                            considered = True
                            if not reserve_pair(a, b):
                                stop_clashes = True
                                break
                            clash_pairs.add(pair)
                            # Explicit exemptions precede solid extraction. Keep
                            # skipped pairs in the set so reverse/source overlap
                            # passes cannot report them again as a clash.
                            if clash_skip_joined:
                                try:
                                    if joined(a, b):
                                        report['skipped_joined'] += 1
                                        continue
                                except Exception:
                                    message = u'部分元件接合狀態未能讀取，未套用「略過已接合」條件。'
                                    if message not in report['warnings']:
                                        report['warnings'].append(message)
                            if clash_skip_collinear and collinear_lines(ma, mb, clash_line_mm):
                                report['skipped_collinear'] += 1
                                continue
                            check_overlap(a, b, 'clash')
                            if adapter.key(a) in unsupported:
                                break  # No solid source means no applicable collision pair.
                    except Exception as error:
                        considered = True
                        add('clash', 'UNKNOWN', [a], u'未能完整取得碰撞候選：'+u'{}'.format(error))
                    # Identify empty sources as not applicable, not as a geometry
                    # pass. Fully exempt pairs need no solid extraction.
                    if not considered and not stop_clashes and 'overlap' not in selected:
                        check_source_geometry(a, 'clash')
            stop = False
            compared = set()

            def partners(index, a, ma):
                if not peers:
                    return elements[index+1:]
                found = []
                for b in adapter.clash_candidates(a, ma['bounds'], 'ALL'):
                    if adapter.key(b) == adapter.key(a) or (allowed_keys is not None and adapter.key(b) not in allowed_keys):
                        continue
                    if adapter.key(b) not in metadata:
                        try:
                            metadata[adapter.key(b)] = adapter.metadata(b)
                        except Exception:
                            continue  # An unreadable neighbour is not part of the selection's coverage.
                    found.append(b)
                return found
            for index, a in enumerate(elements):
                if stop or not ('duplicate' in selected or 'overlap' in selected):
                    break
                ma = metadata.get(adapter.key(a))
                if ma is None:
                    continue
                for b in partners(index, a, ma):
                    if expired() or len(report['results']) >= max_results:
                        report['truncated'] = stop = True
                        break
                    pair = tuple(sorted((adapter.key(a), adapter.key(b))))
                    if pair in compared:
                        continue  # Two selected neighbours meet each other twice.
                    compared.add(pair)
                    mb = metadata.get(adapter.key(b))
                    if (mb is None or mb['bounds'] is None or ma['category'] != mb['category']
                            or not near_bounds(ma['bounds'], mb['bounds'], position_mm)):
                        continue
                    if not reserve_pair(a, b):
                        stop = True
                        break
                    if 'duplicate' in selected:
                        same = duplicate_position(ma, mb, position_mm)
                        if same is True:
                            add('duplicate', 'SUSPECT', [a, b], u'同類型、位置／路徑及外框在 {:.3f} mm 容差內一致。這是疑似副本，仍需核對實際形狀及實例參數。'.format(position_mm))
                        elif same is None:
                            add('duplicate', 'UNKNOWN', [a, b], u'同類型外框一致，但缺少可比較的位置／方向資料，不能確認為重複元件。')
                    if ('overlap' in selected and adapter.key(a) not in unsupported and adapter.key(b) not in unsupported
                            and near_bounds(ma['bounds'], mb['bounds'])
                            and tuple(sorted((adapter.key(a), adapter.key(b)))) not in clash_pairs):
                        check_overlap(a, b, 'overlap')
        if expired():
            report['truncated'] = True
    except Exception as error:
        report['truncated'] = True
        report['warnings'].append(u'檢查未完成：'+u'{}'.format(error))
        report['error_trace'] = traceback.format_exc()  # Logged by the panel; never shown as a result.
    finally:
        adapter.close()
    report['unknown'] = sum(row['status'] == 'UNKNOWN' for row in report['results'])
    report['excluded_curve_elements'] = sum(bool(value) for value in curve_exclusions.values())
    report['excluded_curve_sources'] = len(excluded_curve_sources)
    source_keys = set(adapter.key(e) for e in elements)
    rooms = set(key for key, excluded in room_exclusions.items() if excluded)
    report['excluded_room_elements'] = len(rooms)
    report['excluded_room_sources'] = len(rooms & source_keys)
    report['excluded_nonsolid_elements'] = len(nonsolid)
    report['excluded_nonsolid_sources'] = len(nonsolid & source_keys)
    report['excluded_geometry_sources'] = len((nonsolid | excluded_curve_sources | rooms) & source_keys)
    report['geometry_failures'] = len(geometry_failures)
    if len(geometry_failures) > 5:
        report['warnings'].append(u'另有 {} 個元件的實體幾何未能完整讀取，未列入碰撞結果。'.format(len(geometry_failures)-5))
    report['findings'] = len(report['results'])-report['unknown']
    report['counts'] = dict((code, sum(row['check'] == code and row['status'] != 'UNKNOWN' for row in report['results'])) for code in selected)
    if report['truncated']:
        report['warnings'].append(u'達到時間／數量上限或讀取中斷，結果只涵蓋部分範圍；請縮小視圖後重新檢查。')
    report['status'] = ('INCOMPLETE' if report['truncated'] or report['unknown'] or report['geometry_failures'] else
                        'REVIEW' if report['findings'] else 'NO_MATCH' if not elements else
                        'NOT_APPLICABLE' if set(selected).issubset(('clash', 'overlap')) and report['excluded_geometry_sources'] == len(elements) else 'CLEAR')
    return report


class RevitCheckAdapter(RevitGeometryAdapter):
    def sources(self, scope):
        if scope['scope'] not in ('all', 'selection'):
            for element in RevitGeometryAdapter.sources(self, scope):
                yield element
            return
        DB = self.DB
        collector = DB.FilteredElementCollector(self.doc, self.view.Id).WhereElementIsNotElementType()
        if scope['scope'] == 'selection':
            # Selected elements visible in this view; deleted ones simply drop out.
            ids = self.selection_ids(scope['keys'])
            if ids is None:
                dispose(collector)
                return
            collector.WherePasses(DB.ElementIdSetFilter(ids))
        self.owners.append(collector)
        try:
            for element in collector:
                if (element.Category is not None and element.Category.CategoryType == DB.CategoryType.Model
                        and not isinstance(element, DB.RevitLinkInstance)):
                    yield element
        finally:
            dispose(collector)

    def selection_ids(self, keys):
        """ElementIds of the selected elements that still exist, or None.

        A plain function on purpose: pyRevit's IronPython (Tracing + FullFrames) cannot
        run list comprehensions inside generator functions such as sources().
        """
        from System.Collections.Generic import List
        ids = [e.Id for e in (self.doc.GetElement(key) for key in keys) if e is not None]
        return List[self.DB.ElementId](ids) if ids else None

    def warning_record(self, message):
        ids = list(message.GetFailingElements()) + list(message.GetAdditionalElements())
        elements = [self.doc.GetElement(identity) for identity in ids]
        return dict(keys=[e.UniqueId for e in elements if e is not None], message=message.GetDescriptionText())

    def is_clash_curve(self, element):
        # Includes room/space/area separators and model/detail/reference curves;
        # independent of translated category names and line styles.
        return isinstance(element, self.DB.CurveElement)

    def is_clash_room(self, element):
        # Category identity is language-independent and excludes Room Tags and
        # unrelated families merely named Room. Room volumes are spatial data.
        category = element.Category
        return category is not None and category.Id.IntegerValue == int(self.DB.BuiltInCategory.OST_Rooms)

    def clash_candidates(self, source, bounds, target_category):
        DB = self.DB
        pad = 1.e-6  # Feet, broad phase only.
        outline = DB.Outline(DB.XYZ(*(v/304.8-pad for v in bounds[:3])),
                             DB.XYZ(*(v/304.8+pad for v in bounds[3:])))
        collector = DB.FilteredElementCollector(self.doc, self.view.Id).WhereElementIsNotElementType()
        self.owners.extend([outline, collector])
        try:
            if target_category != 'ALL':
                collector.OfCategory(getattr(DB.BuiltInCategory, target_category))
            collector.WherePasses(DB.BoundingBoxIntersectsFilter(outline))
            for element in collector:
                if (element.Id != source.Id and element.Category is not None
                        and element.Category.CategoryType == DB.CategoryType.Model
                        and not isinstance(element, DB.RevitLinkInstance)):
                    yield element
        finally:
            dispose(collector)
            dispose(outline)

    def metadata(self, element):
        from casegraph.extractor import id_value
        DB = self.DB
        box = element.get_BoundingBox(None) or element.get_BoundingBox(self.view)
        bounds = None
        if box:
            points = [box.Transform.OfPoint(DB.XYZ(x, y, z)) for x in (box.Min.X, box.Max.X)
                      for y in (box.Min.Y, box.Max.Y) for z in (box.Min.Z, box.Max.Z)]
            bounds = tuple(f(getattr(p, axis) for p in points)*304.8 for f in (min, max) for axis in ('X', 'Y', 'Z'))
            dispose(box)
        placement = None
        point = lambda p: (p.X*304.8, p.Y*304.8, p.Z*304.8)
        try:
            location = element.Location
            if isinstance(location, DB.LocationPoint):
                rotation = float(location.Rotation)
                direction = [math.sin(rotation), math.cos(rotation)]
                if isinstance(element, DB.FamilyInstance):
                    for vector in (element.FacingOrientation, element.HandOrientation):
                        direction.extend((vector.X, vector.Y, vector.Z))
                    direction.extend((float(element.Mirrored), float(element.FacingFlipped), float(element.HandFlipped)))
                placement = ('point', point(location.Point), tuple(direction))
            elif isinstance(location, DB.LocationCurve):
                curve = location.Curve
                if curve and curve.IsBound:
                    samples = tuple(v for t in (0., .25, .5, .75, 1.) for v in point(curve.Evaluate(t, True)))
                    placement = ('curve:'+curve.GetType().Name, samples, ())
        except Exception:
            # Missing placement data prevents a duplicate claim, but must not
            # prevent the independent solid-overlap check.
            placement = None
        return dict(category=id_value(element.Category.Id) if element.Category else '',
                    type=id_value(element.GetTypeId()), bounds=bounds, placement=placement)

    def mark(self, element):
        from casegraph.extractor import id_value
        parameter = element.get_Parameter(self.DB.BuiltInParameter.ALL_MODEL_MARK)
        return id_value(element.Category.Id) if element.Category else '', parameter.AsString() if parameter else None

    def warnings(self):
        messages = self.doc.GetWarnings()
        self.owners.extend(messages)
        try:
            for message in messages:
                yield self.warning_record(message)
        finally:
            for message in messages:
                dispose(message)


def with_check_overlay(graph, report, limit=240):
    from casegraph.plan_checks import view_caption
    if not report or report.get('stale') or report.get('graph_root') != graph['root']:
        return graph
    result = deepcopy(graph)
    nodes = dict((n['node_id'], n) for n in result['nodes'])
    omitted, occurrences = 0, 0
    for row in report['results']:
        if not row['records'] or row['status'] == 'UNKNOWN':
            continue
        missing = [n for n in row['records'] if n['node_id'] not in nodes]
        if len(nodes)+len(missing) > limit or occurrences+len(row['records'])+2 > 360:
            omitted += 1
            continue
        occurrences += len(row['records'])+2
        for record in row['records']:
            key = record['node_id']
            if key not in nodes:
                nodes[key] = deepcopy(record)
                result['nodes'].append(nodes[key])
            if nodes[key]['properties'].get('_model_check') != 'OVERLAP':
                nodes[key]['properties']['_model_check'] = row['status']
            result['edges'].append(dict(source=row['records'][0]['node_id'], target=key,
                relationship_type='MODEL_CHECK', validation_status='candidate', evidence=row['message'],
                properties=dict(check=row['check'], label=LABELS[row['check']], result_id=row['id'], status=row['status'],
                    status_label=STATUSES[row['status']], advice=row['advice'],
                    view_name=u'、'.join(view_caption(v) for v in row['views']) if row.get('views') else report['view_name'],
                    checked_at=report['checked_at'])))
    if omitted:
        result.setdefault('warnings', []).append(u'圖上省略 {} 項檢查結果；完整清單請看「模型檢查」。'.format(omitted))
    return result
