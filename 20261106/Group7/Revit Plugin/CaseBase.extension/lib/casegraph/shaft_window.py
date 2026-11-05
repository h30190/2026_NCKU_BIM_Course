# -*- coding: utf-8 -*-
"""Optional modeless ShaftChecker window, using CaseBase's existing event queue."""
import os
from pyrevit import forms
# A ribbon command has its own IronPython runtime. pyRevit's framework loads
# the engine-specific WPF assembly before importing the native _wpf module.
from pyrevit.framework import wpf
from System import Object
from System.Collections.Generic import List
from System.Collections.ObjectModel import ObservableCollection
from System.Windows import Window
from System.Windows.Controls import ComboBoxItem
from System.Windows.Data import ListCollectionView, PropertyGroupDescription
from Autodesk.Revit import DB
from pyrevit.coreutils import envvars
from casegraph.navigation import PANEL_INSTANCE_KEY
from casegraph.shaft_check import run_check, validate_report, validate_result, model_data_changed
from casegraph.shaft_csv import problem_rows, write_problem_csv
from casegraph.shaft_issue import GROUPS, STATUSES, shaft_issue, fill_issue
from casegraph.shaft_results import group_label, grouped_rows

WINDOW_KEY = 'CASEGRAPH_SHAFT_WINDOW'


class Finding(object):
    def __init__(self, row, group=u''):
        self.data = row
        self.group_label = group
        self.label = u'[{}] {} · {} · {}\n{}'.format(
            STATUSES[row['status']], GROUPS[row['group']], row['rule_id'], row['shaft_label'], row['message'])


class ShaftWindow(Window):
    def __init__(self, panel):
        self.panel, self.report, self._closed = panel, None, False
        self._ready, self._updating_view = False, False
        wpf.LoadComponent(self, os.path.join(os.path.dirname(__file__), 'ShaftChecker.xaml'))
        self._findings = ObservableCollection[Object]()
        self._view = ListCollectionView(self._findings)
        self.Findings.ItemsSource = self._view
        self._ready = True
        self.grouping_changed(None, None)
        # The registered panel already receives DocumentChanged in Revit's API
        # context. A modeless window must not add/remove Revit event handlers.
        if panel is not None:
            panel.shaft_window = self

    def closed(self, sender, args):
        if self._closed:
            return
        self._closed = True
        panel, self.panel = self.panel, None
        self.report = None
        try:
            if panel is not None and getattr(panel, 'shaft_window', None) is self:
                panel.shaft_window = None
            if envvars.get_pyrevit_env_var(WINDOW_KEY) is self:
                envvars.set_pyrevit_env_var(WINDOW_KEY, None)
        except Exception as error:
            # Exceptions must never escape a modeless WPF Closed callback.
            if panel is not None:
                panel.log_error(error)

    def model_changed(self, sender, args):
        # Navigation may update a view without changing the checked model data.
        # Actual model changes (including links) still invalidate the findings.
        if not self._closed and self.report and model_data_changed(args):
            self.report['stale'] = True
            self.Record.IsEnabled = self.Locate.IsEnabled = self.Suggest.IsEnabled = self.ExportCsv.IsEnabled = False
            self.Status.Text = u'模型已變更，請重新檢查後再記錄或匯出問題。'

    def execute(self, action, message):
        if self._closed or self.panel is None:
            return
        def guarded(uidoc):
            if self._closed or not self.IsVisible:
                return
            try:
                action(uidoc)
            except Exception as error:
                self.Status.Text = unicode(error)
                self.panel.log_error(error)
        if not self.panel.run(guarded, message):
            self.Status.Text = u'Revit 或診斷與修正系統正在處理其他工作，請稍後重試。'

    def check_click(self, sender, args):
        def action(uidoc):
            self.report = None
            self.grouping_changed(None, None)
            self.Record.IsEnabled = self.Locate.IsEnabled = self.Suggest.IsEnabled = self.ExportCsv.IsEnabled = False
            self.Details.Text = ''
            self.Summary.Text = u'正在讀取主模型及已載入連結…'
            self.report = run_check(uidoc)
            rows = self.report['results']
            self.ExportCsv.IsEnabled = bool(problem_rows(self.report))
            self.grouping_changed(None, None)
            self.Summary.Text = u'{} 個管道間 · {} 條啟用規則 · {} 筆需處理 · {} 筆規則通過'.format(
                self.report['shaft_count'], len(self.report['rules_run']), len(rows), self.report['pass_count'])
            if not self.report['shaft_count']:
                self.Summary.Text = u'未辨識到管道間，尚未完成檢查；請確認 Room 及辨識設定。'
            self.Status.Text = u'\n'.join(self.report['warnings']) or (
                u'尚未檢查任何管道間，請確認 Room 及辨識設定。' if not self.report['shaft_count'] else
                u'選取結果可查看證據並帶入問題紀錄。' if rows else u'本次檢查範圍內沒有需處理的結果。')
        self.execute(action, u'執行管道間檢查…')

    def grouping_changed(self, sender, args):
        if not self._ready or self._closed or self._updating_view:
            return
        mode = str(self.GroupBy.SelectedItem.Tag)
        rows = grouped_rows(self.report['results'] if self.report else [], mode)
        self._updating_view = True
        try:
            self.GroupFilter.Items.Clear()
            self.GroupFilter.Items.Add(ComboBoxItem(Content=u'全部', Tag=''))
            labels = set()
            for row in rows:
                label = group_label(row, mode)
                if label and label not in labels:
                    labels.add(label)
                    self.GroupFilter.Items.Add(ComboBoxItem(Content=label, Tag=label))
            self.GroupFilter.SelectedIndex = 0
            self.GroupFilter.IsEnabled = mode != 'none' and bool(rows)
        finally:
            self._updating_view = False
        self.filter_changed(None, None)

    def filter_changed(self, sender, args):
        if not self._ready or self._closed or self._updating_view:
            return
        selected = self.Findings.SelectedItem
        selected_row = selected.data if selected else None
        mode = str(self.GroupBy.SelectedItem.Tag)
        wanted = self.GroupFilter.SelectedItem.Tag if self.GroupFilter.SelectedItem else ''
        rows = self.report['results'] if self.report else []
        restore = None
        findings = ObservableCollection[Object]()
        for row in grouped_rows(rows, mode):
            label = group_label(row, mode)
            if wanted and label != wanted:
                continue
            finding = Finding(row, label)
            findings.Add(finding)
            if row is selected_row:
                restore = finding
        # Build off-screen before replacing the source: WPF selection callbacks
        # must never read an attached CollectionView during DeferRefresh.
        view = ListCollectionView(findings)
        if mode != 'none':
            view.GroupDescriptions.Add(PropertyGroupDescription('group_label'))
        self._findings, self._view = findings, view
        self.Findings.ItemsSource = view
        self.Findings.SelectedItem = restore
        self.ResultCount.Text = u'顯示 {} / {} 筆問題'.format(self._findings.Count, len(rows))
        self.selection_changed(None, None)

    def export_csv_click(self, sender, args):
        report = self.report
        if not report:
            return
        def action(uidoc):
            if report is not self.report:
                return
            validate_report(uidoc, report)
            if not problem_rows(report):
                self.Status.Text = u'本次沒有可匯出的管道間問題。'
                return
            stamp = report['checked_at'].replace(':', '-').replace(' ', '_')
            path = forms.save_file(file_ext='csv', files_filter=u'CSV 檔案 (*.csv)|*.csv',
                                   default_name=u'管道間問題_{}.csv'.format(stamp), title=u'匯出管道間問題 CSV')
            if not path:
                self.Status.Text = u'已取消匯出。'
                return
            if self._closed or report is not self.report:
                return
            validate_report(uidoc, report)
            count = write_problem_csv(path, report)
            self.Status.Text = u'已匯出 {} 筆管道間問題：{}'.format(count, path)
        self.execute(action, u'匯出管道間問題 CSV…')

    def selection_changed(self, sender, args):
        if self._closed:
            return
        item = self.Findings.SelectedItem
        available = bool(item and self.report and not self.report['stale'] and not item.data['unresolved'])
        self.Record.IsEnabled = self.Locate.IsEnabled = self.Suggest.IsEnabled = available
        if not item:
            self.Details.Text = ''
            return
        if item.data['unresolved']:
            self.Details.Text = item.data['message']+u'\n\n來源元件無法對應，請確認連結已載入並重新檢查。'
        elif self.report['stale']:
            self.Details.Text = u'此結果已失效，請重新檢查。'
        else:
            self.Details.Text = shaft_issue(self.report, item.data)['description']

    def locate_click(self, sender, args):
        item, report = self.Findings.SelectedItem, self.report
        if not item or not report:
            return
        def action(uidoc):
            validate_result(uidoc, report, item.data)
            ids = List[DB.ElementId]([uidoc.Document.GetElement(r['source_element_id']).Id for r in item.data['records']])
            uidoc.Selection.SetElementIds(ids)
            uidoc.ShowElements(ids)
            if report['stale']:
                return  # Do not overwrite a real model-change notice during navigation.
            self.Status.Text = u'已定位；返回此視窗可繼續按「帶入問題紀錄」。連結來源定位至其連結實例，詳細元件 ID 見檢查證據。'
        self.execute(action, u'定位管道間檢查元件…')

    def suggest_click(self, sender, args):
        item, report = self.Findings.SelectedItem, self.report
        if not item or not report:
            return
        def action(uidoc):
            validate_result(uidoc, report, item.data)
            if self.panel.suggest_kind == 'apply':
                self.Status.Text = u'正在套用修改，請完成後再帶入另一項問題。'
                return
            record = shaft_issue(report, item.data)
            # Keep the issue draft intact; only pin this finding for suggestion planning.
            self.panel.set_target(record['root'], refresh=False, keep_checks=True, members=record['related'][:20])
            self.panel.set_suggest_context(record['origin'])
            self.panel.open_feature('suggest')
            forms.open_dockable_panel(self.panel.panel_id)
            self.Status.Text = u'已帶入「修改建議」，可選擇過去案例或 AI 建議。'
        self.execute(action, u'帶入修改建議…')

    def record_click(self, sender, args):
        item, report = self.Findings.SelectedItem, self.report
        if not item or not report:
            return
        def action(uidoc):
            validate_result(uidoc, report, item.data)
            record = shaft_issue(report, item.data)
            if self.panel.issue_form_unsaved() and not forms.alert(
                    u'問題紀錄有尚未儲存的內容。要以此管道間結果取代目前表單嗎？',
                    title=u'案例驅動 BIM 診斷與修正', ok=False, yes=True, no=True):
                self.Status.Text = u'已保留原有問題紀錄。'
                return
            from casegraph.service import ensure_started
            ensure_started()
            fill_issue(self.panel, record)
            forms.open_dockable_panel(self.panel.panel_id)
            self.Status.Text = u'已帶入「問題紀錄」，核對後儲存草稿或提交。'
        self.execute(action, u'帶入管道間問題紀錄…')


def open_window():
    from casegraph import bootstrap
    # Register/retrieve the shared event dispatcher without showing the docked
    # panel or changing its current page. Only report transfer reveals it.
    bootstrap.register()
    panel = envvars.get_pyrevit_env_var(PANEL_INSTANCE_KEY)
    if panel is None:
        return
    window = envvars.get_pyrevit_env_var(WINDOW_KEY)
    if window is not None and not window._closed and window.IsVisible:
        window.Activate()
        return
    window = ShaftWindow(panel)
    envvars.set_pyrevit_env_var(WINDOW_KEY, window)
    window.Show()
