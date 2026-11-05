# -*- coding: utf-8 -*-
"""Two-element comparison actions; model reads use the panel's ExternalEvent."""
from copy import deepcopy
from Autodesk.Revit import DB
from Autodesk.Revit.UI.Selection import ObjectType
from casegraph.extractor import document_id
from casegraph.element_index import detail_snapshot
from casegraph.presentation import caption, compare_rows


class ComparisonView(object):
    def read_detail(self, uidoc, record):
        if record['source_document'] != document_id(uidoc.Document):
            result = deepcopy(record)
            result['_recorded'] = True
            return result
        element = uidoc.Document.GetElement(record['source_element_id'])
        if element is None:
            raise ValueError(u'元素已被刪除，請重新選取：'+caption(record))
        return detail_snapshot(uidoc.Document, element)

    def compare_pair(self, left, right):
        # Validate before replacing an existing comparison.
        compare_rows(left, right)
        def action(uidoc):
            self.show_comparison(self.read_detail(uidoc, left), self.read_detail(uidoc, right))
        self.run(action, u'讀取兩個元素的規格與參數…')

    def show_comparison(self, left, right):
        rows = compare_rows(left, right)
        self.comparison_pair, self.comparison_rows = (left, right), rows
        self.CompareA.Text, self.CompareB.Text = caption(left), caption(right)
        self.CompareASource.Text = left['properties'].get('document_title') or u'文件名稱未提供'
        self.CompareBSource.Text = right['properties'].get('document_title') or u'文件名稱未提供'
        for record, control in ((left, self.CompareASource), (right, self.CompareBSource)):
            if record.get('_recorded'):
                control.Text += u' · 歷史記錄'
        self.ComparisonSummary.Text = u'{} 欄不同 · {} 欄相同 · {} 欄資料不足'.format(
            sum(r.status == u'不同' for r in rows), sum(r.status == u'相同' for r in rows), sum(r.status == u'資料不足' for r in rows))
        self.ComparisonNotice.Text = u'已讀取目前可用資料。識別資料不計入差異；未提供欄位不判定為相同。'
        if left.get('_recorded') or right.get('_recorded'):
            self.ComparisonNotice.Text = u'包含歷史記錄，可能不是模型最新狀態。開啟來源文件後可重新讀取。'
        self.comparison_filter_changed(None, None)
        self.Tabs.SelectedItem = self.CompareTab
        self.Status.Text = u'已完成元素比較。'

    def comparison_filter_changed(self, sender, args):
        if not self.ready:
            return
        self.ComparisonRows.Items.Clear()
        for row in self.comparison_rows:
            if not self.DifferencesOnly.IsChecked or row.status in (u'不同', u'資料不足'):
                self.ComparisonRows.Items.Add(row)

    def compare_node(self, sender, args):
        if self.target and self.selected_node:
            try:
                self.compare_pair(self.target, self.selected_node)
            except Exception as error:
                self.show_error(error)

    def compare_match(self, sender, args):
        if self.target and self.Matches.SelectedItem:
            try:
                self.compare_pair(self.target, self.Matches.SelectedItem.data['element'])
            except Exception as error:
                self.show_error(error)

    def pick_compare(self, sender, args):
        def action(uidoc):
            ids = list(uidoc.Selection.GetElementIds())
            if len(ids) != 2:
                refs = uidoc.Selection.PickObjects(ObjectType.Element, u'請選取兩個同類別元素，然後按完成（Esc 取消）')
                ids = [r.ElementId for r in refs]
            if len(ids) != 2:
                raise ValueError(u'請選取剛好兩個不同的同類別元素。')
            elements = [uidoc.Document.GetElement(i) for i in ids]
            if any(e is None or isinstance(e, DB.ElementType) for e in elements):
                raise ValueError(u'請選取兩個模型實例。')
            left, right = [detail_snapshot(uidoc.Document, e) for e in elements]
            self.show_comparison(left, right)
        self.run(action, u'選取兩個同類別元素…')

    def refresh_comparison(self, sender, args):
        if self.comparison_pair:
            self.compare_pair(*self.comparison_pair)
        else:
            self.Status.Text = u'請先在圖中選擇比較元素，或從 Revit 選取兩個元素。'

    def swap_comparison(self, sender, args):
        if self.comparison_pair:
            notice = self.ComparisonNotice.Text
            self.show_comparison(self.comparison_pair[1], self.comparison_pair[0])
            self.ComparisonNotice.Text = notice

    def locate_comparison(self, sender, args):
        if self.comparison_pair:
            record = self.comparison_pair[int(sender.Tag)]
            self.run(lambda uidoc: self.show_node(uidoc, record))
