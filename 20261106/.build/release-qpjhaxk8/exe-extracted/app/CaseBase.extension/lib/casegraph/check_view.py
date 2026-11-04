# -*- coding: utf-8 -*-
"""Focused model-check page and queued Revit navigation."""
from copy import deepcopy
from System.Collections.Generic import List
from System.Windows import Visibility
from System.Windows.Controls import ItemsControl
from Autodesk.Revit import DB
from pyrevit import forms
from casegraph.panel import Item
from casegraph.check_issue import check_issue
from casegraph.palette import CHECK_ACCENTS, brush
from casegraph.extractor import document_id, id_value
from casegraph.element_index import snapshot
from casegraph.model_checks import CHECKS, LABELS, STATUSES, CLASH_TARGETS, RevitCheckAdapter, run_checks
from casegraph.plan_checks import PLAN_KINDS, PlanCheckJob, list_plan_views, view_caption


class CheckItem(object):
    """One result row: check kind, involved elements and a one-line note."""
    def __init__(self, row):
        self.data = row
        records = row['records']
        self.kind = LABELS[row['check']]
        self.status_label = STATUSES[row['status']]
        self.accent = brush(CHECK_ACCENTS[row['status']])
        # Clashes span categories, so name the category of each side.
        names = [u'{}{} · ID {}'.format(r['name'], u'（{}）'.format(r['properties'].get('category') or u'類別未提供')
                                         if row['check'] == 'clash' else u'', r['properties'].get('element_id', u'—'))
                 for r in records[:3]]
        separator = u' ↔ ' if len(records) == 2 else u'、'
        self.headline = separator.join(names) + (u' 等 {} 個'.format(len(records)) if len(records) > 3 else u'') or u'未提供元件'
        note = row['message'].split(u'\n')[0]
        if row.get('views'):
            note = u'、'.join(view_caption(v) for v in row['views']) + u' · ' + note
        self.note = note
        self.label = u'{} · {}\n{}\n{}'.format(self.kind, self.status_label, self.headline, note)


class CheckView(object):
    def initialize_checks(self):
        self.check_report, self.check_pending, self.pending_check_result = None, False, None
        self.check_job = None
        self.check_selection = None
        self.CheckRangeMode.Items.Add(Item(u'範圍：目前視圖', 'view'))
        self.CheckRangeMode.Items.Add(Item(u'範圍：自訂（框選／選取）', 'selected'))
        self.CheckRangeMode.Items.Add(Item(u'範圍：所有平面圖', 'plans'))
        self.CheckRangeMode.SelectedIndex = 0
        self.check_source_keys, self.check_source_refresh = None, False
        self.CheckScope.Items.Add(Item(u'選取中的元件', 'selection'))
        self.CheckScope.Items.Add(Item(u'基準的同類型元素', 'type'))
        self.CheckScope.Items.Add(Item(u'基準的同類別元素', 'category'))
        self.CheckScope.Items.Add(Item(u'全部模型元件', 'all'))
        self.CheckScope.SelectedIndex = 0
        for code, label in CLASH_TARGETS:
            self.ClashTarget.Items.Add(Item(label, code))
        self.ClashTarget.SelectedIndex = 0
        self.CheckFilter.Items.Add(Item(u'全部結果', 'all'))
        for code, label in CHECKS:
            self.CheckFilter.Items.Add(Item(label, code))
        self.CheckFilter.SelectedIndex = 0

    def check_options_changed(self, sender, args):
        if self.ready:
            if sender is self.CheckRangeMode:
                mode = self.CheckRangeMode.SelectedItem.data
                self.CheckRangePanel.Visibility = Visibility.Visible if mode == 'selected' else Visibility.Collapsed
                if mode == 'view':
                    self.CheckRangeSummary.Text = ''
                elif mode == 'plans':
                    self.set_check_scope('all')
                    self.CheckRangeSummary.Text = u'逐張檢查全部平面圖（含天花、結構、面積平面），相同結果合併。'
                elif self.check_selection:
                    self.CheckRangeSummary.Text = u'已指定 {} 個元素。'.format(len(self.check_selection['keys']))
                else:
                    self.CheckRangeSummary.Text = u'先框選範圍，或使用目前選取。'
            self.invalidate_checks(u'檢查設定已變更，請重新執行檢查。')

    def set_check_selection(self, uidoc, elements):
        elements = [e for e in elements if e is not None and e.Category is not None and not isinstance(e, DB.ElementType)]
        if not elements:
            raise ValueError(u'沒有選到可檢查的元素；原範圍已保留。')
        if len(elements) > 1000:
            raise ValueError(u'自訂範圍最多選取 1000 個元素，請縮小範圍。')
        if not self.target or self.target['source_document'] != document_id(uidoc.Document):
            self.set_target(snapshot(uidoc.Document, elements[0]), refresh=False)
        keys = sorted(set(e.UniqueId for e in elements))
        self.check_selection = dict(document=document_id(uidoc.Document), view_id=id_value(uidoc.ActiveView.Id), keys=keys)
        self.CheckRangeMode.SelectedIndex = 1
        self.set_check_scope('all')
        self.CheckRangeSummary.Text = u'已指定 {} 個元素 · 視圖「{}」。'.format(len(keys), uidoc.ActiveView.Name)
        self.invalidate_checks(u'自訂範圍已更新，按「檢查」執行。')

    def pick_check_range(self, sender, args):
        def action(uidoc):
            elements = uidoc.Selection.PickElementsByRectangle(u'框選本次要檢查的元件；Esc 取消並保留原範圍。')
            self.set_check_selection(uidoc, list(elements))
        self.run(action, u'請在 Revit 框選檢查範圍…')

    def use_selected_check_range(self, sender, args):
        def action(uidoc):
            self.set_check_selection(uidoc, [uidoc.Document.GetElement(i) for i in uidoc.Selection.GetElementIds()])
        self.run(action, u'讀取目前選取的檢查範圍…')

    def invalidate_checks(self, message):
        previous = self.check_report
        self.check_report, self.check_pending, self.pending_check_result = None, False, None
        self.check_job = None
        self.CancelPlanChecks.IsEnabled = False
        self.PlanCoverage.Text = ''
        self.CheckResults.Items.Clear()
        self.CheckDetails.Text = ''
        self.LocateCheck.IsEnabled = self.ShowCheckGraph.IsEnabled = self.RecordCheckIssue.IsEnabled = False
        self.CheckSummary.Text = message
        self.CheckNotes.Text = ''
        self.CheckNotesPanel.Visibility = Visibility.Collapsed
        if previous and self.context_graph:
            self.render_graph_preview()

    def set_check_scope(self, code):
        self.CheckScope.SelectedItem = next(i for i in self.CheckScope.Items if i.data == code)

    def run_checks_click(self, sender, args):
        if self.check_job is not None:
            return
        # A manual run re-reads the Revit selection; automatic rechecks keep the same sources,
        # because highlighting a result changes the selection.
        if sender is not None:
            self.check_source_refresh = True
        self.check_pending = True
        self.CheckSummary.Text = u'等待檢查所選範圍…'
        self.refresh_timer.Start()

    def run_all_plans_click(self, sender, args):
        self.CheckRangeMode.SelectedIndex = 2
        self.run_checks_click(sender, args)

    def cancel_plan_checks(self, sender, args):
        if self.check_job is not None:
            report = self.check_job.finish(cancelled=True)
            self.check_job, self.check_pending = None, False
            self.CancelPlanChecks.IsEnabled = False
            self.apply_check_report(report)

    def selected_sources(self, uidoc):
        """UniqueIds of the selected model instances; else the pinned element and its group."""
        doc = uidoc.Document
        picked = [doc.GetElement(i) for i in uidoc.Selection.GetElementIds()]
        keys = [e.UniqueId for e in picked if e is not None and e.Category is not None and not isinstance(e, DB.ElementType)]
        if not keys and self.target and self.target['source_document'] == document_id(doc):
            keys = [r['source_element_id'] for r in [self.target] + self.target_members if r['source_document'] == self.target['source_document']]
        if not keys:
            raise ValueError(u'請先在 Revit 選取要檢查的元件。')
        if len(keys) > 1000:
            raise ValueError(u'來源最多 1000 個元件，請縮小選取範圍。')
        return sorted(set(keys))

    def perform_checks(self, uidoc):
        if self.check_job is not None:
            self.perform_plan_step(uidoc)
            return
        self.invalidate_checks(u'正在檢查所選範圍…')
        try:
            scope_name = self.CheckScope.SelectedItem.data
            identity = document_id(uidoc.Document)
            if scope_name == 'selection':
                if self.check_source_refresh or not self.check_source_keys or self.check_source_keys[0] != identity:
                    self.check_source_keys = (identity, self.selected_sources(uidoc))
                self.check_source_refresh = False
                if self.target is None or self.target['source_document'] != identity:
                    # Results and graph overlays hang off a pinned element in this document.
                    self.set_target(snapshot(uidoc.Document, uidoc.Document.GetElement(self.check_source_keys[1][0])), refresh=False)
            self.resolve_target(uidoc)
            if self.target['source_document'] != identity:
                raise ValueError(u'請先讀取目前文件的基準元素。')
            p = self.target['properties']
            source_key = '*' if scope_name in ('all', 'selection') else p.get('type_id') if scope_name == 'type' else p.get('category_id')
            if scope_name not in ('all', 'selection') and (source_key in (None, '', '-1') or not p.get('category_id')):
                raise ValueError(u'基準元素沒有可用的類型／類別，請改用同類別或重新讀取元素。')
            scope = dict(scope=scope_name, source_key=source_key, source_category_id=p.get('category_id'),
                         document=self.target['source_document'])
            if scope_name == 'selection':
                scope['keys'] = self.check_source_keys[1]
            keys = None
            if self.CheckRangeMode.SelectedItem.data == 'selected':
                selection = self.check_selection
                if not selection or selection['document'] != scope['document'] or selection['view_id'] != id_value(uidoc.ActiveView.Id):
                    raise ValueError(u'請在目前文件及視圖重新框選範圍，或切換為「目前視圖」。')
                keys = selection['keys']
                if any(uidoc.Document.GetElement(key) is None for key in keys):
                    raise ValueError(u'範圍內有元素已刪除，請重新框選後檢查。')
            checks = [code for code, control in [('duplicate', self.CheckDuplicates), ('overlap', self.CheckOverlaps),
                                                ('clash', self.CheckClashes),
                                                ('mark', self.CheckMarks), ('warning', self.CheckWarnings)] if control.IsChecked]
            if not checks:
                raise ValueError(u'請至少選擇一項檢查。')
            try:
                position, volume = float(self.CheckPositionTolerance.Text), float(self.CheckVolumeTolerance.Text)
                line_mm = float(self.ClashLineTolerance.Text) if self.CheckClashes.IsChecked and self.ClashSkipCollinear.IsChecked else 1.
            except Exception:
                raise ValueError(u'請輸入有效的數值容差。')
            # Validate before allocating native geometry resources.
            import math
            if any(math.isnan(v) or math.isinf(v) or v < 0 for v in (position, volume, line_mm)):
                raise ValueError(u'容差必須是大於或等於 0 的有限數值。')
            options = dict(position_mm=position, volume_mm3=volume, clash_target=self.ClashTarget.SelectedItem.data,
                           clash_skip_joined=bool(self.ClashSkipJoined.IsChecked),
                           clash_skip_collinear=bool(self.ClashSkipCollinear.IsChecked), clash_line_mm=line_mm,
                           peers=scope_name == 'selection')
            if self.CheckRangeMode.SelectedItem.data == 'plans':
                views = list_plan_views(uidoc.Document)
                if not views:
                    raise ValueError(u'目前文件沒有可檢查的平面圖（視圖樣板不納入）。')
                self.check_job = PlanCheckJob(views, scope, checks, options, self.target['node_id'])
                self.CancelPlanChecks.IsEnabled = True
                self.perform_plan_step(uidoc)
                return
            report = run_checks(RevitCheckAdapter(uidoc.Document, uidoc.ActiveView), scope, checks,
                                element_keys=keys, **options)
            report['graph_root'] = self.target['node_id']
            self.apply_check_report(report)
        except Exception as error:
            self.CheckSummary.Text = u'檢查未完成：'+unicode(error)
            raise

    def perform_plan_step(self, uidoc):
        job = self.check_job
        if document_id(uidoc.Document) != job.scope['document']:
            self.invalidate_checks(u'已切換文件，所有平面圖檢查已取消，請重新執行。')
            return
        view_info = job.views[job.index]
        try:
            view = uidoc.Document.GetElement(DB.ElementId(int(view_info['id'])))
            if view is None or view.IsTemplate:
                raise ValueError(u'視圖已刪除或已改為樣板。')
            result = run_checks(RevitCheckAdapter(uidoc.Document, view), job.scope, job.checks, **job.options)
            if result.get('error_trace'):
                self.log_text(u'模型檢查中斷（{}）\n{}'.format(view_info['name'], result['error_trace']))
            job.add(view_info, result)
        except Exception as error:
            job.add(view_info, error=unicode(error))
        self.update_plan_coverage(job.report)
        if job.done:
            self.check_job, self.check_pending = None, False
            self.CancelPlanChecks.IsEnabled = False
            self.apply_check_report(job.finish())
        else:
            self.CheckSummary.Text = u'所有平面圖：{}／{} 張 · 下一張「{}」'.format(
                job.index, len(job.views), view_caption(job.views[job.index]))
            self.Status.Text = self.CheckSummary.Text
            self.check_pending = True

    def update_plan_coverage(self, report):
        states = dict(CLEAR=u'已檢查，未發現問題', REVIEW=u'有待處理結果', INCOMPLETE=u'未完成／未能確認',
                      NO_MATCH=u'沒有符合來源條件的元素', NOT_APPLICABLE=u'沒有適用的實體來源')
        self.PlanCoverage.Text = u'\n'.join(u'{} · {} · {} · 來源 {}／候選 {}'.format(
            row['name'], PLAN_KINDS.get(row['kind'], row['kind']), states.get(row['status'], row['status']),
            row['sources'], row['pairs']) for row in report.get('plan_reports', []))

    def apply_check_report(self, report):
        if report.get('error_trace'):
            self.log_text(u'模型檢查中斷\n'+report['error_trace'])
        self.check_report = report
        self.SnapshotOnly.IsChecked = False
        if not self.context_graph or not self._graph_live or self.context_graph['root'] != report['graph_root']:
            self.display_graph(dict(root=report['graph_root'], nodes=[self.target], edges=[], warnings=[]), u'目前模型檢查', live=True)
        else:
            self.render_graph_preview()
        self.show_check_report()
        # On another page (e.g. an automatic recheck) the status bar is the only place the result shows.
        self.Status.Text = u'模型檢查完成。' if self.active_feature == 'checks' else u'模型檢查：'+self.CheckSummary.Text

    def show_check_report(self):
        report = self.check_report
        self.filter_checks(None, None)
        if not report:
            return
        # One line answers "what did I find"; coverage and exclusions live in 檢查說明.
        headline = dict(CLEAR=u'未發現問題', NOT_APPLICABLE=u'沒有適用的碰撞來源',
                        NO_MATCH=u'範圍內沒有符合條件的元素').get(report['status'])
        headline = headline or u'{} 項待處理 · {} 項未能確認'.format(report['findings'], report['unknown'])
        if report['status'] == 'INCOMPLETE':
            headline = u'⚠ 未完整 · ' + headline
        self.CheckSummary.Text = u'{}（{} · {}）'.format(headline, report['view_name'], report['checked_at'][11:16])
        plans = report['range_mode'] == 'plans'
        unit, count_unit = (u'組次', u'個次') if plans else (u'組', u'個')
        notes = []
        if report['scope'].get('scope') == 'selection':
            selected = len(report['scope']['keys'])
            notes.append(u'來源：選取中的 {} 個元件；重複與重疊也比對其旁邊的同類別元件。'.format(selected))
            if not plans and report['sources'] < selected:
                notes.append(u'其中 {} 個不在此視圖，未檢查。'.format(selected-report['sources']))
        notes += [u'；'.join(u'{} {} 項'.format(LABELS[code], report['counts'][code]) for code in report['checks']),
                 u'來源 {} {}／候選 {} {}'.format(report['sources'], u'次' if plans else u'個', report['pairs'], unit)]
        if plans:
            self.update_plan_coverage(report)
            failed = sum(row['status'] == 'INCOMPLETE' for row in report['plan_reports'])
            notes.append(u'{} 張未完整；{} 張尚未處理。各圖情況見下方。'.format(failed, report['total_plans']-report['completed_plans']))
        if report['range_mode'] == 'selected':
            notes.append(u'自訂範圍：{} 個指定元素；兩邊均限範圍內及目前視圖。'.format(report['range_count']))
        if 'clash' in report['checks']:
            notes.append(u'碰撞對象：'+dict(CLASH_TARGETS)[report['clash_target']])
            joined_count = u'{} {}'.format(report['skipped_joined'], unit) if report['clash_skip_joined'] else u'不略過'
            line_count = u'{} {}（容差 {:g} mm）'.format(report['skipped_collinear'], unit, report['clash_line_mm']) if report['clash_skip_collinear'] else u'不略過'
            notes.append(u'略過：已接合 {}／同一直線 {}'.format(joined_count, line_count))
            if report['excluded_curve_elements']:
                notes.append(u'已排除 {} 個線元素（分隔線、模型線等）。'.format(report['excluded_curve_elements']))
        if report.get('excluded_room_elements'):
            notes.append(u'已略過 {} {} Room（房間）。'.format(report['excluded_room_elements'], count_unit))
        if report.get('excluded_nonsolid_elements'):
            notes.append(u'已略過 {} {}無實體幾何的元件。'.format(report['excluded_nonsolid_elements'], count_unit))
        if report.get('geometry_failures'):
            notes.append(u'{} 筆實體擷取未完成，相關範圍未能確認。'.format(report['geometry_failures']))
        omitted = (report.get('skipped_joined', 0) or report.get('skipped_collinear', 0) or report.get('excluded_curve_elements', 0)
                   or report.get('excluded_nonsolid_elements', 0) or report.get('excluded_room_elements', 0))
        if report['status'] in ('CLEAR', 'NOT_APPLICABLE') and omitted:
            notes.append(u'略過或排除的元件未作碰撞判定。')
        notes.extend(report['warnings'])
        self.CheckNotes.Text = u'\n'.join(notes)
        self.CheckNotesPanel.Visibility = Visibility.Visible

    def filter_checks(self, sender, args):
        if not self.ready:
            return
        self.pending_check_result = None
        self.CheckResults.Items.Clear()
        self.CheckDetails.Text = ''
        self.LocateCheck.IsEnabled = self.ShowCheckGraph.IsEnabled = self.RecordCheckIssue.IsEnabled = False
        if self.check_report:
            code = self.CheckFilter.SelectedItem.data if self.CheckFilter.SelectedItem else 'all'
            for row in self.check_report['results']:
                if code == 'all' or row['check'] == code:
                    self.CheckResults.Items.Add(CheckItem(row))

    def check_result_changed(self, sender, args):
        item = self.CheckResults.SelectedItem
        available = bool(item and self.check_report and item.data['records'])
        self.LocateCheck.IsEnabled = self.RecordCheckIssue.IsEnabled = available
        self.ShowCheckGraph.IsEnabled = available and item.data['status'] != 'UNKNOWN'
        if not item:
            return
        row = item.data
        names = u'\n'.join(u'{} · {} · ID {}'.format(r['name'], r['properties'].get('category') or u'類別未提供',
                         r['properties'].get('element_id', '—')) for r in row['records'])
        self.CheckDetails.Text = u'{}\n{}\n\n建議處理\n{}'.format(names, row['message'], row['advice'])
        if row.get('views'):
            self.CheckDetails.Text += u'\n\n來源圖紙\n'+u'\n'.join(v['name']+u' · '+PLAN_KINDS.get(v['kind'], v['kind']) for v in row['views'])
        self.pending_result = None
        self.pending_check_result = None
        if available and self.AutoHighlight.IsChecked:
            self.pending_check_result = (self.check_report, row)
            self.refresh_timer.Start()

    def locate_check_click(self, sender, args):
        item = self.CheckResults.SelectedItem
        if item and self.check_report and item.data['records']:
            self.pending_result = None
            self.pending_check_result = (self.check_report, item.data)
            self.refresh_timer.Start()

    def locate_check(self, uidoc, ticket):
        report, row = ticket
        if self.check_report is not report:
            return
        if document_id(uidoc.Document) != report['document']:
            uidoc.Selection.SetElementIds(List[DB.ElementId]())
            self.invalidate_checks(u'已切換文件，請重新檢查。')
            raise ValueError(u'結果屬於其他文件，請開啟來源模型。')
        elements = [uidoc.Document.GetElement(r['source_element_id']) for r in row['records']]
        if any(e is None for e in elements):
            uidoc.Selection.SetElementIds(List[DB.ElementId]())
            self.invalidate_checks(u'結果中的元素已刪除，請重新檢查。')
            return
        if row.get('views') and id_value(uidoc.ActiveView.Id) not in [v['id'] for v in row['views']]:
            view = next((uidoc.Document.GetElement(DB.ElementId(int(v['id']))) for v in row['views']
                         if uidoc.Document.GetElement(DB.ElementId(int(v['id']))) is not None), None)
            if view is None:
                self.invalidate_checks(u'結果的來源平面圖已刪除，請重新檢查。')
                return
            uidoc.ActiveView = view
        # Pair rows display source first and counterpart second; focus the latter.
        if len(elements) == 2:
            elements = elements[1:]
        # Switch only when all navigation targets share the same owner view.
        owner_ids = set(e.OwnerViewId.IntegerValue for e in elements if e.ViewSpecific)
        if len(owner_ids) == 1 and all(e.ViewSpecific for e in elements):
            owner = uidoc.Document.GetElement(DB.ElementId(next(iter(owner_ids))))
            if owner:
                uidoc.ActiveView = owner
        ids = List[DB.ElementId]([e.Id for e in elements])
        uidoc.Selection.SetElementIds(ids)
        uidoc.ShowElements(ids)
        self.Status.Text = u'已選取並定位 {} 個檢查元素。'.format(len(elements))

    def record_check_issue(self, sender, args):
        # Double-clicking the scrollbar or empty list space must not create a record.
        if sender is self.CheckResults and ItemsControl.ContainerFromElement(sender, args.OriginalSource) is None:
            return
        item = self.CheckResults.SelectedItem
        if not item or not self.check_report:
            return
        try:
            record = check_issue(self.check_report, item.data, self.target)
        except ValueError as error:
            self.Status.Text = unicode(error)
            return
        if self.issue_form_unsaved() and not forms.alert(
                u'問題紀錄有尚未儲存的內容。要以此檢查結果建立新問題，並取代目前表單嗎？',
                title=u'案例驅動 BIM 診斷與修正', ok=False, yes=True, no=True):
            self.Status.Text = u'已保留目前的問題紀錄表單。'
            return
        moved = self.target is None or self.target['node_id'] != record['root']['node_id']
        # Keep the exact result pair even when several results share the same root.
        self.set_target(deepcopy(record['root']), refresh=False, keep_checks=True, members=deepcopy(record['related']))
        self.reset_issue_form()
        self.issue_origin = record['origin']
        self.IssueTitle.Text, self.Description.Text, self.Notes.Text = record['title'], record['description'], record['notes']
        for box, code in [(self.IssueType, record['issue_type']), (self.Priority, record['priority'])]:
            box.SelectedItem = next(i for i in box.Items if i.data == code)
        self.show_issue_origin()
        self.open_feature('issue')
        self.schedule_refresh('related', 'graph')
        self.Status.Text = u'已填入檢查結果 #{}；核對後按「儲存草稿」。'.format(item.data['id'])
        if moved:
            self.Status.Text += u' 基準元素已改為「{}」。'.format(record['root']['name'])

    def show_check_graph(self, sender, args):
        item = self.CheckResults.SelectedItem
        if not item or not self.check_report or not item.data['records']:
            return
        row = item.data
        self.SnapshotOnly.IsChecked = False
        self.open_feature('graph')
        self.RelationFilter.SelectedIndex = 0
        self.render_graph_preview()
        self.focus_graph_result(row['records'][0], 'check-element:'+row['id']+':'+row['records'][0]['node_id'])

    def checks_model_changed(self, identity=None, view_change=False):
        report = self.check_report
        job = self.check_job
        affected = report or (job.report if job else None)
        if affected and view_change and affected['range_mode'] == 'plans' and affected['document'] == identity:
            return  # Navigating among this document's views does not change the batch scope.
        if affected and (view_change or identity is None or affected['document'] == identity):
            self.invalidate_checks(u'模型或視圖已變更，先前結果已失效。')
            if self.AutoChecks.IsChecked and (not view_change or affected['document'] == identity):
                self.run_checks_click(None, None)
