# -*- coding: utf-8 -*-
"""解法審查: what changed on the case's recorded elements since its snapshot."""
from datetime import datetime
from casegraph.model_changes import RevitLocator, change_report, summary, solution_text, STATES
from casegraph.palette import brush

ACCENTS = dict(CHANGED='#176A7A', UNCHANGED='#7A8A99', DELETED='#C0392B', UNAVAILABLE='#B76E00')


class ChangeItem(object):
    def __init__(self, element):
        self.data = element
        self.title = u'{}{} · ID {}'.format(u'◎ ' if element['root'] else u'', element['name'], element['element_id'] or u'—')
        count = len(element['changes'])
        self.status_label = u'已修改 {} 項'.format(count) if element['state'] == 'CHANGED' else STATES[element['state']]
        self.accent = brush(ACCENTS[element['state']])
        lines = [u'{}：{} → {}'.format(c['field'], c['before'], c['after']) for c in element['changes'][:4]]
        if count > 4:
            lines.append(u'…另 {} 項'.format(count-4))
        if element['match'] and element['match'] != u'Revit UniqueId':
            lines.append(u'以 {} 對應'.format(element['match']))
        self.detail = u'\n'.join(lines)
        self.label = u'{} · {}\n{}'.format(self.title, self.status_label, self.detail)


class ReviewView(object):
    locator = RevitLocator

    def initialize_review(self):
        self.change_report, self.change_pending = None, False

    def change_report_current(self):
        report, case = self.change_report, self.current
        return bool(report and case and report['case_id'] == case['id'] and report['revision'] == case['revision'])

    def invalidate_changes(self, message):
        self.change_report, self.change_pending = None, False
        self.ChangeResults.Items.Clear()
        self.ChangeSummary.Text = message
        self.UseChanges.IsEnabled = False

    def queue_change_scan(self):
        if self.current:
            self.change_pending = True
            self.refresh_timer.Start()

    def detect_changes_click(self, sender, args):
        if not self.current:
            self.Status.Text = u'請先從「案例庫」開啟案例。'
            return
        self.ChangeSummary.Text = u'正在比對已記錄元件…'
        self.queue_change_scan()

    def detect_changes(self, uidoc):
        case = self.current
        if not case:
            return
        locator = self.locator(uidoc.Document)
        self.show_changes(change_report(case, locator.document, datetime.now().strftime('%Y-%m-%d %H:%M'),
                                        locator.find, locator.read))

    def show_changes(self, report):
        self.change_report = report
        self.ChangeResults.Items.Clear()
        for element in report['elements']:
            self.ChangeResults.Items.Add(ChangeItem(element))
        self.ChangeSummary.Text = u'{} · {}'.format(summary(report), report['checked_at'][-5:])
        replayable = sum(1 for e in report['elements'] for c in e['changes']
                         if c.get('action') and c['action']['op'] != 'rotate') + sum(e['state'] == 'DELETED' for e in report['elements'])
        if replayable:
            self.ChangeSummary.Text += u'\n其中 {} 項可透過 Revit MCP 自動重播（需先驗證解法）。'.format(replayable)
        if any(e['state'] == 'UNAVAILABLE' for e in report['elements']):
            self.ChangeSummary.Text += u'\n部分元件不在目前模型；請開啟來源模型，或確認已匯出 IFC GlobalId。'
        self.UseChanges.IsEnabled = any(e['state'] in ('CHANGED', 'DELETED') for e in report['elements'])
        self.Status.Text = u'已比對案例快照：' + summary(report)

    def append_changes_click(self, sender, args):
        if not self.change_report_current():
            return
        text = solution_text(self.change_report)
        current = self.Solution.Text.rstrip()
        if text not in current:
            self.Solution.Text = (current + u'\n\n' if current else u'') + text
        self.Status.Text = u'已加入模型變更紀錄；按「儲存解法」等審查操作時會一併保存比對結果。'

    def change_selected(self, sender, args):
        item = self.ChangeResults.SelectedItem
        if item and self.current and item.data['state'] != 'UNAVAILABLE':
            record = next((n for n in self.current['draft']['graph']['nodes'] if n['node_id'] == item.data['node_id']), None)
            if record:
                self.queue_result(record)

    def changes_model_changed(self, identity):
        if self.change_report and self.change_report['document'] == identity:
            self.invalidate_changes(u'模型已修改，正在重新比對…')
            if self.active_feature == 'review':
                self.queue_change_scan()
