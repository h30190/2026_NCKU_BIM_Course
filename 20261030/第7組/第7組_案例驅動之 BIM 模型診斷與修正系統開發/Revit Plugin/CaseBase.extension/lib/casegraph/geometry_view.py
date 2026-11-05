# -*- coding: utf-8 -*-
"""Rule editor and view-wide checking inside the graph inspector."""
from System.Collections.Generic import List
from Autodesk.Revit import DB
from casegraph.panel import Item
from casegraph.extractor import document_id
from casegraph.geometry_rules import RuleStore, SCOPES, TARGETS, make_rule
from casegraph.geometry_check import RevitGeometryAdapter, check_rule


class GeometryView(object):
    def initialize_rules(self):
        self.rule_store = RuleStore()
        self.rules, self.rules_document = [], None
        self.rule_report, self.rule_pending = None, False
        self.rule_store_error = False
        self.suppress_rules = False
        for code, label in SCOPES:
            self.RuleSourceScope.Items.Add(Item(label, code))
        self.RuleSourceScope.SelectedIndex = 0
        for code, label in TARGETS:
            self.RuleTargetCategory.Items.Add(Item(label, code))
        self.RuleTargetCategory.SelectedIndex = 0

    def rules_click(self, sender, args):
        self.GraphInspector.SelectedItem = self.RulesTab

    def load_target_rules(self):
        if not self.target:
            return
        identity = self.target['source_document']
        if self.rules_document == identity:
            return
        self.rules_document = identity
        self.rule_store_error = False
        self.invalidate_rule_report(u'基準文件已變更，請選擇規則並重新檢查。')
        try:
            self.rules = self.rule_store.load(identity)
        except Exception as error:
            self.rules, self.rule_store_error = [], True
            self.RuleSummary.Text = u'無法讀取規則檔，已保留原檔：'+unicode(error)
            self.log_error(error)
        self.display_rules()

    def display_rules(self, selected=None):
        old = selected or (self.GeometryRules.SelectedItem.data['id'] if self.GeometryRules.SelectedItem else None)
        self.suppress_rules = True
        try:
            self.GeometryRules.Items.Clear()
            for rule in self.rules:
                item = Item(rule['name'], rule)
                self.GeometryRules.Items.Add(item)
                if rule['id'] == old:
                    self.GeometryRules.SelectedItem = item
            if self.GeometryRules.SelectedItem is None and self.rules:
                self.GeometryRules.SelectedIndex = 0
        finally:
            self.suppress_rules = False
        self.SaveGeometryRule.IsEnabled = not self.rule_store_error
        self.RunGeometryRules.IsEnabled = bool(self.GeometryRules.SelectedItem) and not self.rule_store_error
        self.RemoveGeometryRule.IsEnabled = bool(self.GeometryRules.SelectedItem) and not self.rule_store_error
        self.populate_rule_editor()

    def save_geometry_rule(self, sender, args):
        def action(uidoc):
            self.resolve_target(uidoc)
            self.load_target_rules()
            if self.rule_store_error:
                raise ValueError(u'規則檔無法讀取，已保留原檔；請先修復規則檔。')
            try:
                tolerance = float(self.RuleTolerance.Text)
            except Exception:
                raise ValueError(u'請輸入有效的交集體積容差（mm³），例如 0 或 1。')
            rule = make_rule(self.target, self.RuleSourceScope.SelectedItem.data,
                             self.RuleTargetCategory.SelectedItem.data, tolerance, bool(self.RuleExcludeHost.IsChecked))
            updated = [r for r in self.rules if r['id'] != rule['id']] + [rule]
            self.rule_store.save(rule['document'], updated)
            self.rules = updated
            self.display_rules(rule['id'])
            self.RuleEditor.IsExpanded = False
            self.invalidate_rule_report(u'規則已儲存。按「檢查目前視圖」批次檢查。')
            self.Status.Text = self.RuleSummary.Text
        self.run(action, u'儲存相交規則…')

    def remove_geometry_rule(self, sender, args):
        item = self.GeometryRules.SelectedItem
        if not item or self.rule_store_error:
            return
        try:
            updated = [r for r in self.rules if r['id'] != item.data['id']]
            self.rule_store.save(self.rules_document, updated)
            self.rules = updated
            self.display_rules()
            self.invalidate_rule_report(u'已移除規則；先前檢查結果已清除。')
        except Exception as error:
            self.show_error(error)

    def geometry_rule_changed(self, sender, args):
        if not self.ready or self.suppress_rules:
            return
        self.RunGeometryRules.IsEnabled = bool(self.GeometryRules.SelectedItem)
        self.RemoveGeometryRule.IsEnabled = bool(self.GeometryRules.SelectedItem)
        self.populate_rule_editor()
        self.invalidate_rule_report(u'已切換規則，請重新檢查。')

    def populate_rule_editor(self):
        if self.GeometryRules.SelectedItem:
            rule = self.GeometryRules.SelectedItem.data
            self.RuleSourceScope.SelectedItem = next(i for i in self.RuleSourceScope.Items if i.data == rule['scope'])
            self.RuleTargetCategory.SelectedItem = next(i for i in self.RuleTargetCategory.Items if i.data == rule['target_category'])
            self.RuleTolerance.Text = unicode(rule['tolerance_mm3'])
            self.RuleExcludeHost.IsChecked = rule['exclude_host']

    def invalidate_rule_report(self, message):
        previous = self.rule_report
        self.rule_report, self.rule_pending = None, False
        self.RuleResults.Items.Clear()
        self.RuleResultDetails.Text = ''
        self.LocateRulePair.IsEnabled = False
        self.RuleSummary.Text = message
        if previous and self.context_graph:
            self.render_graph_preview()

    def check_geometry_click(self, sender, args):
        if not self.GeometryRules.SelectedItem:
            self.RuleSummary.Text = u'先加入或選擇一條規則。'
            return
        self.rule_pending = True
        self.RuleSummary.Text = u'等待檢查目前視圖…'
        self.refresh_timer.Start()

    def perform_rule_check(self, uidoc):
        self.rule_pending = False
        item = self.GeometryRules.SelectedItem
        if not item:
            return
        rule = item.data
        self.invalidate_rule_report(u'正在檢查目前視圖的實體重疊…')
        try:
            if rule['document'] != document_id(uidoc.Document):
                raise ValueError(u'規則屬於其他文件。請先讀取目前文件的基準元素。')
            if not self.target or self.target['source_document'] != rule['document']:
                raise ValueError(u'請先讀取此文件的基準元素。')
            adapter = RevitGeometryAdapter(uidoc.Document, uidoc.ActiveView)
            report = check_rule(adapter, rule)
            report['graph_root'] = self.target['node_id']
            self.rule_report = report
            self.SnapshotOnly.IsChecked = False
            # A previous relationship filter must not hide the new violations.
            if report['violations']:
                self.RelationFilter.SelectedIndex = 0
            self.show_rule_report(report)
            if self.context_graph and self._graph_live:
                self.render_graph_preview()
            else:
                self.schedule_refresh('graph')
            self.GraphInspector.SelectedItem = self.RulesTab
            self.Status.Text = self.RuleSummary.Text
        except Exception as error:
            self.RuleSummary.Text = u'檢查未完成：'+unicode(error)
            raise

    def show_rule_report(self, report):
        self.RuleResults.Items.Clear()
        labels = dict(VIOLATION=u'違規', UNKNOWN=u'未能確認')
        for row in report['results']:
            source = row['source']['properties'].get('element_id', '—')
            target = row['target']['properties'].get('element_id', '—') if row['target'] else u'未完成'
            title = u'{} · {} → {}\n{}'.format(labels[row['status']], source, target, row['message'])
            self.RuleResults.Items.Add(Item(title, row))
        summary = u'視圖「{}」· {} 個來源／{} 組檢查 · {} 組違規／{} 組未能確認'.format(
            report['view_name'], report['sources'], report['checked_pairs'], report['violations'], report['unknown'])
        if report['status'] == 'PASS':
            summary += u'\n本次範圍內未找到超過容差的實體重疊。'
        elif report['status'] == 'NO_MATCH':
            summary += u'\n目前視圖沒有符合規則的來源元素；沒有執行幾何判定。'
        if report['excluded_hosts']:
            summary += u'\n依規則排除 {} 組宿主。'.format(report['excluded_hosts'])
        if report['warnings']:
            summary += u'\n'+u'；'.join(report['warnings'])
        self.RuleSummary.Text = summary

    def rule_result_changed(self, sender, args):
        item = self.RuleResults.SelectedItem
        self.LocateRulePair.IsEnabled = bool(item) and self.rule_report is not None
        if item:
            row = item.data
            self.RuleResultDetails.Text = u'{}\n{}\n{}'.format(row['source']['name'], row['target']['name'] if row['target'] else u'候選尚未確認', row['message'])
            occurrence = ('violation-source:'+self.rule_report['rule']['id']+':'+row['source']['node_id']) if self.rule_report else None
            self.focus_graph_result(row['source'], occurrence)
            self.queue_result(row['source'])

    def locate_rule_pair(self, sender, args):
        item = self.RuleResults.SelectedItem
        if not item or not self.rule_report:
            return
        records = [r for r in (item.data['source'], item.data['target']) if r]
        self.pending_result = None
        def action(uidoc):
            identity = document_id(uidoc.Document)
            if any(r['source_document'] != identity for r in records):
                raise ValueError(u'請先開啟檢查結果的來源文件。')
            elements = [uidoc.Document.GetElement(r['source_element_id']) for r in records]
            if any(e is None for e in elements):
                self.invalidate_rule_report(u'結果中的元素已刪除，請重新檢查。')
                uidoc.Selection.SetElementIds(List[DB.ElementId]())
                return
            ids = List[DB.ElementId]([e.Id for e in elements])
            uidoc.Selection.SetElementIds(ids)
            uidoc.ShowElements(ids)
            self.Status.Text = u'已選取並定位 {} 個檢查元素。'.format(len(elements))
        self.run(action, u'定位檢查結果…')

    def rule_model_changed(self, identity):
        if self.rule_report and self.rule_report['document'] == identity:
            self.invalidate_rule_report(u'模型已修改，先前結果已失效，請重新檢查。')
            if self.AutoRuleCheck.IsChecked:
                self.check_geometry_click(None, None)

    def rule_view_changed(self):
        if self.rule_report:
            self.invalidate_rule_report(u'目前視圖已變更，請重新檢查。')
            if self.AutoRuleCheck.IsChecked:
                self.check_geometry_click(None, None)
