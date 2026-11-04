# -*- coding: utf-8 -*-
"""Modeless WPF knowledge graph. All Revit actions use the panel's ExternalEvent."""
import math
import os
import wpf
from System import Action
from System.Windows import Window, Thickness, Point, Visibility, TextWrapping, FontWeights
from System.Windows.Controls import Button, Canvas, StackPanel, TextBlock, Border, CheckBox, ComboBox
from System.Windows.Data import Binding, BindingMode
from System.Windows.Media import (BrushConverter, Brushes, ScaleTransform, PathGeometry,
                                  PathFigure, BezierSegment, DoubleCollection)
from System.Windows.Shapes import Path, Polygon
from System.Windows import CornerRadius
from casegraph.panel import Item
from casegraph.presentation import RELATIONS, KINDS, STATUS_NAMES, Row, caption, detail_rows, relationship_rows
from casegraph.case_graph import navigation_record
from casegraph.knowledge_graph import build_network, CARD_WIDTH, CARD_HEIGHT


def brush(color):
    return BrushConverter().ConvertFromString(color)


class KnowledgeWindow(Window):
    def __init__(self, panel):
        self.panel, self.graph, self.selected = panel, None, None
        self.network = build_network(None)
        self.scale, self.drag, self._updating = 1., None, True
        self._root, self._fit_pending = None, True
        self.cards = {}
        wpf.LoadComponent(self, os.path.join(os.path.dirname(__file__), 'KnowledgeGraph.xaml'))
        toggle = Binding('IsChecked')
        toggle.Source, toggle.Mode = panel.AutoHighlight, BindingMode.TwoWay
        self.AutoHighlight.SetBinding(CheckBox.IsCheckedProperty, toggle)
        verified = Binding('IsChecked')
        verified.Source, verified.Mode = panel.VerifiedOnly, BindingMode.TwoWay
        self.VerifiedCases.SetBinding(CheckBox.IsCheckedProperty, verified)
        self.CaseIssueType.ItemsSource = panel.CaseIssueType.Items
        issue_type = Binding('SelectedItem')
        issue_type.Source, issue_type.Mode = panel.CaseIssueType, BindingMode.TwoWay
        self.CaseIssueType.SetBinding(ComboBox.SelectedItemProperty, issue_type)
        scope = Binding('Text')
        scope.Source = panel.CaseTypeLabel
        self.CaseTypeLabel.SetBinding(TextBlock.TextProperty, scope)
        status = Binding('Text')
        status.Source = panel.Status
        self.Status.SetBinding(TextBlock.TextProperty, status)
        self._updating = False
        self.set_graph(panel.graph, panel.GraphSummary.Text, panel.selected_node)

    def window_closed(self, sender, args):
        panel, self.panel = self.panel, None
        if panel and panel.knowledge_window is self:
            panel.knowledge_window = None
        self.graph, self.selected = None, None
        self.GraphCanvas.Children.Clear()
        self.cards.clear()
        # Drop bindings to the shared panel when a window is closed/reopened.
        from System.Windows.Data import BindingOperations
        BindingOperations.ClearAllBindings(self.Status)
        BindingOperations.ClearAllBindings(self.AutoHighlight)
        BindingOperations.ClearAllBindings(self.VerifiedCases)
        BindingOperations.ClearAllBindings(self.CaseIssueType)
        BindingOperations.ClearAllBindings(self.CaseTypeLabel)
        self.CaseIssueType.ItemsSource = None

    def window_loaded(self, sender, args):
        self.fit_pending()

    def read_target(self, sender, args):
        self.panel.read_click(sender, args)

    def refresh_click(self, sender, args):
        self.panel.graph_refresh_click(sender, args)
        self.panel.Status.Text = u'正在更新知識圖譜…'

    def set_graph(self, graph, summary, selected=None):
        root = graph['root'] if graph else None
        changed = root != self._root
        self.graph, self._root = graph, root
        self.SourceLabel.Text = summary.split(u'\n')[0]
        self.SourceLabel.ToolTip = summary
        self._updating = True
        current = getattr(self.RelationFilter.SelectedItem, 'data', 'CASES')
        self.RelationFilter.Items.Clear()
        codes = ['CASES', 'ALL']+sorted(set(e['relationship_type'] for e in graph['edges'])) if graph else ['CASES', 'ALL']
        for code in codes:
            item = Item(RELATIONS.get(code, code), code)
            self.RelationFilter.Items.Add(item)
            if code == current:
                self.RelationFilter.SelectedItem = item
        if self.RelationFilter.SelectedItem is None:
            self.RelationFilter.SelectedIndex = 0
        self._updating = False
        self.selected = selected
        self.rebuild()
        if changed or self._fit_pending:
            self._fit_pending = True
            self.Dispatcher.BeginInvoke(Action(self.fit_pending))

    def rebuild(self):
        code = getattr(self.RelationFilter.SelectedItem, 'data', 'ALL')
        self.network = build_network(self.graph, code)
        if not self.selected or self.selected['node_id'] not in self.network['nodes']:
            self.selected = self.network['nodes'].get(self.network['root'])
        actual = len([n for n in self.network['nodes'].values() if n['node_type'] != 'GROUP'])
        groups = len(self.network['nodes'])-actual
        links = len([e for e in self.network['edges'] if not e['grouping']])
        self.Summary.Text = u'{} 個資料節點 · {} 個分類 · {} 條原始關係 · {} 個比較候選'.format(actual, groups, links, len(self.network['matches']))
        cases = sum(n['node_type'] == 'ISSUE' for n in self.network['nodes'].values())
        solutions = sum(n['node_type'] == 'SOLUTION' for n in self.network['nodes'].values())
        if code == 'CASES':
            self.Summary.Text = u'{} 個案例 · {} 個解法 · 元素 → 問題案例 → 解法（依案例狀態標示）'.format(cases, solutions)
        if self.panel._graph_live:
            self.Summary.Text += u'\n'+self.panel.graph_case_message
        elif self.graph:
            self.Summary.Text += u'\n已儲存快照；按「立即更新」並取消快照選項可讀取案例庫最新關係。'
        if self.graph and self.graph.get('warnings'):
            self.Summary.Text += u'\n'+u'；'.join(self.graph['warnings'])
        no_cases = code == 'CASES' and self.graph and cases == 0
        self.EmptyMessage.Text = (u'此範圍尚無案例連線。可調整「相關案例」的篩選，或切換「全部關係」查看模型。' if no_cases
                                  else u'先在 Revit 選取一個元素，再按「讀取 Revit 選取元素」。')
        self.EmptyMessage.Visibility = Visibility.Visible if not self.graph or no_cases else Visibility.Collapsed
        self.draw()
        self.inspect(self.selected)

    def filter_changed(self, sender, args):
        if not self._updating:
            self.rebuild()
            self.fit_click(None, None)

    def inspect(self, record):
        self.NodeProperties.Items.Clear()
        self.NodeRelations.Items.Clear()
        self.LocateNode.IsEnabled = navigation_record(record, self.graph) is not None
        is_case = bool(record and record['node_type'] in ('ISSUE', 'SOLUTION') and record['properties'].get('case_id'))
        self.OpenCase.IsEnabled = is_case
        self.OpenSolution.IsEnabled = bool(is_case and record['properties'].get('solution'))
        self.LocateNode.Content = u'在 Revit 查看案例元件' if is_case else u'在 Revit 查看此元素'
        if not record:
            self.NodeTitle.Text, self.NodeSubtitle.Text = u'點選圖中節點', ''
            return
        self.NodeTitle.Text = record['name'] if record['node_type'] != 'ELEMENT' else caption(record)
        self.NodeSubtitle.Text = KINDS.get(record['node_type'], record['node_type'])
        if record.get('_recorded'):
            self.NodeSubtitle.Text += u' · 歷史記錄'
        if record['node_type'] == 'GROUP':
            p = record['properties']
            rows = [Row(u'分類層級', p['hierarchy_level']), Row(u'包含元素', unicode(p['element_count'])),
                    Row(u'來源文件', p.get('document_title') or u'依各元素的來源文件')]
            self.NodeRelations.Items.Add(Row(u'分類說明', u'虛線只表示比較分類，不表示模型中的實體連接。'))
        else:
            rows = detail_rows(record, self.graph)
            for row in relationship_rows(record, self.graph):
                self.NodeRelations.Items.Add(row)
        for row in rows:
            self.NodeProperties.Items.Add(row)

    def select_record(self, record):
        if record and record['node_id'] in self.network['nodes']:
            self.selected = record
            self.inspect(record)
            self.style_selection()

    def node_click(self, sender, args):
        record = sender.Tag
        self.select_record(record)
        if record['node_type'] == 'GROUP':
            self.panel.pending_result = None
            self.panel.Status.Text = u'已選取分類；點選個別元素可在 Revit 高亮。'
        else:
            self.panel.selected_node = record
            self.panel.queue_result(record, inspect=True)

    def locate_click(self, sender, args):
        record = navigation_record(self.selected, self.graph)
        if record and self.LocateNode.IsEnabled:
            panel = self.panel
            panel.pending_result = None
            panel.run(lambda uidoc:panel.show_node(uidoc, record), u'定位圖譜元素…')

    def open_case_click(self, sender, args):
        if self.selected and self.OpenCase.IsEnabled:
            self.panel.open_case(self.selected['properties']['case_id'], destination='issue')

    def open_solution_click(self, sender, args):
        if self.selected and self.OpenSolution.IsEnabled:
            self.panel.open_case(self.selected['properties']['case_id'], destination='review')

    def draw(self):
        self.GraphCanvas.Children.Clear()
        self.cards.clear()
        self.GraphCanvas.Width, self.GraphCanvas.Height = self.network['width'], self.network['height']
        positions = self.network['positions']
        pairs = {}
        for edge in self.network['edges']:
            pair = tuple(sorted((edge['source'], edge['target'])))
            index = pairs.get(pair, 0)
            pairs[pair] = index+1
            self.draw_edge(edge, positions[edge['source']], positions[edge['target']], index)
        for key in sorted(self.network['nodes']):
            record = self.network['nodes'][key]
            p = record['properties']
            group = record['node_type'] == 'GROUP'
            stack = StackPanel()
            kind = TextBlock()
            kind.Text = p['hierarchy_level'] if group else (u'基準元素' if key == self.network['root'] else KINDS.get(record['node_type'], u'元素'))
            kind.FontSize, kind.Foreground = 10., brush('#587084')
            stack.Children.Add(kind)
            title = TextBlock()
            title.Text, title.TextWrapping = record['name'], TextWrapping.Wrap
            title.FontSize, title.FontWeight, title.MaxHeight = 12., FontWeights.SemiBold, 32.
            stack.Children.Add(title)
            subtitle = TextBlock()
            subtitle.FontSize = 10.
            subtitle.Text = u'{} 個元素'.format(p['element_count']) if group else u'ID '+unicode(p.get('element_id') or u'—')
            match = self.network['matches'].get(key)
            if match:
                subtitle.Text += u' · '+RELATIONS.get(match['relationship_type'], '')
            if record['node_type'] in ('ISSUE', 'SOLUTION'):
                subtitle.Text = STATUS_NAMES.get(p.get('status'), u'查看案例內容') if record['node_type'] == 'ISSUE' else p.get('solution_state', u'查看解法內容')
            root_record = self.network['nodes'].get(self.network['root'])
            if not group and root_record and record['source_document'] != root_record['source_document']:
                subtitle.Text += u' · 其他文件'
            stack.Children.Add(subtitle)
            card = Button()
            card.Style = self.Resources['NodeCard']
            card.Width, card.Height = CARD_WIDTH, CARD_HEIGHT
            card.Content, card.Tag = stack, record
            color = '#EEF6FC'
            if key == self.network['root']:
                color = '#CAE7F6'
            elif group:
                color = '#F0ECFA'
            elif match:
                color = '#E7F4EC' if match['relationship_type'] == 'SAME_TYPE' else '#F0ECFA'
            elif record['node_type'] in ('ISSUE', 'SOLUTION'):
                color = '#FFF2D9'
            elif record['node_type'] in ('LEVEL', 'SPACE', 'MATERIAL', 'SYSTEM'):
                color = '#EAF4F1'
            if p.get('_rule_violation') or p.get('_model_check'):
                color = '#FCE8E6' if p.get('_rule_violation') or p.get('_model_check') == 'OVERLAP' else '#FFF2D9'
            card.Background, card.Foreground = brush(color), brush('#203D53')
            card.ToolTip = record['name']+u'\n'+subtitle.Text
            card.Click += self.node_click
            x, y = positions[key]
            Canvas.SetLeft(card, x-CARD_WIDTH/2.)
            Canvas.SetTop(card, y-CARD_HEIGHT/2.)
            self.GraphCanvas.Children.Add(card)
            self.cards[key] = card
        self.style_selection()

    def style_selection(self):
        selected = self.selected['node_id'] if self.selected else None
        for key, card in self.cards.items():
            card.BorderBrush = brush('#EA8A17' if key == selected else '#B4C7D5')
            card.BorderThickness = Thickness(3. if key == selected else 1.)

    def draw_edge(self, edge, a, b, index):
        # Endpoints stop at card borders; curves retain back-links and self-loops.
        ax, ay = a
        bx, by = b
        bend = index*24.
        if abs(ax-bx) < 1.:
            start, end = Point(ax+CARD_WIDTH/2., ay-12.), Point(bx+CARD_WIDTH/2., by+12.)
            reach = 80.+bend
            c1, c2 = Point(start.X+reach, start.Y-55.), Point(end.X+reach, end.Y+55.)
        else:
            direction = 1. if bx > ax else -1.
            start = Point(ax+direction*CARD_WIDTH/2., ay)
            end = Point(bx-direction*CARD_WIDTH/2., by)
            reach = max(50., abs(end.X-start.X)*.45)
            c1, c2 = Point(start.X+direction*reach, start.Y+bend), Point(end.X-direction*reach, end.Y+bend)
        figure = PathFigure()
        figure.StartPoint = start
        segment = BezierSegment(c1, c2, end, True)
        figure.Segments.Add(segment)
        geometry = PathGeometry()
        geometry.Figures.Add(figure)
        path = Path()
        path.Data, path.StrokeThickness = geometry, 1.5
        record = edge['record'] or {}
        reference = record.get('relationship_type') == 'CASE_REFERENCE'
        color = '#9985B6' if edge['grouping'] or reference else '#B74242' if record.get('relationship_type') in ('MODEL_CHECK', 'RULE_VIOLATION') else '#688BA1'
        path.Stroke = brush(color)
        if edge['grouping'] or reference:
            path.StrokeDashArray = DoubleCollection([4., 3.])
        validation = record.get('validation_status')
        uncertain = not edge['grouping'] and (validation != 'verified' or record.get('relationship_type') in ('NEARBY', 'SIMILAR_TO'))
        label_text = edge['label']+(u'（待確認）' if uncertain and not reference else '')
        if record.get('relationship_type') == 'RESOLVED_BY':
            label_text = u'已驗證解法' if validation == 'verified' else u'解法（未驗證）'
        tip = label_text
        if record.get('evidence'):
            tip += u'\n'+unicode(record['evidence'])
        if record.get('properties', {}).get('rule_name'):
            tip += u'\n'+record['properties']['rule_name']
        path.ToolTip = tip
        self.GraphCanvas.Children.Add(path)
        if not edge['grouping']:
            dx, dy = end.X-c2.X, end.Y-c2.Y
            length = math.sqrt(dx*dx+dy*dy) or 1.
            dx, dy = dx/length, dy/length
            arrow = Polygon()
            for point in (end, Point(end.X-dx*9-dy*4, end.Y-dy*9+dx*4), Point(end.X-dx*9+dy*4, end.Y-dy*9-dx*4)):
                arrow.Points.Add(point)
            arrow.Fill = path.Stroke
            self.GraphCanvas.Children.Add(arrow)
        # Cubic midpoint locates the label on the actual edge, not its chord.
        x, y = (start.X+3*c1.X+3*c2.X+end.X)/8., (start.Y+3*c1.Y+3*c2.Y+end.Y)/8.
        text = TextBlock()
        text.Text, text.FontSize, text.Foreground = label_text, 10., brush(color)
        text.TextWrapping = TextWrapping.Wrap
        badge = Border()
        badge.Child, badge.Background, badge.Padding = text, Brushes.White, Thickness(4, 2, 4, 2)
        badge.CornerRadius, badge.MaxWidth, badge.ToolTip = CornerRadius(4), 155., tip
        from System.Windows import Size
        badge.Measure(Size(155., float('inf')))
        Canvas.SetLeft(badge, x-badge.DesiredSize.Width/2.)
        Canvas.SetTop(badge, y-badge.DesiredSize.Height/2.)
        self.GraphCanvas.Children.Add(badge)

    def fit_pending(self):
        if self.panel and self._fit_pending:
            # Start at a readable size; full overview is an explicit action.
            self.GraphScroll.UpdateLayout()
            width = self.GraphScroll.ViewportWidth
            if width > 50:
                self.set_scale(max(.75, min(1., (width-24)/self.network['width'])))
                self.center_selected()
                self._fit_pending = False

    def fit_click(self, sender, args):
        self.GraphScroll.UpdateLayout()
        width, height = self.GraphScroll.ViewportWidth, self.GraphScroll.ViewportHeight
        if width > 50 and height > 50:
            self.set_scale(min(1., (width-24)/self.network['width'], (height-24)/self.network['height']))
            self.GraphScroll.ScrollToHorizontalOffset(0.)
            self.GraphScroll.ScrollToVerticalOffset(0.)
            self._fit_pending = False

    def set_scale(self, scale, anchor=None):
        old = self.scale
        self.scale = max(.01, min(2.5, scale))
        x = anchor.X if anchor else self.GraphScroll.ViewportWidth/2.
        y = anchor.Y if anchor else self.GraphScroll.ViewportHeight/2.
        ox, oy = self.GraphScroll.HorizontalOffset, self.GraphScroll.VerticalOffset
        self.GraphCanvas.LayoutTransform = ScaleTransform(self.scale, self.scale)
        self.GraphScroll.UpdateLayout()
        self.GraphScroll.ScrollToHorizontalOffset((ox+x)*self.scale/old-x)
        self.GraphScroll.ScrollToVerticalOffset((oy+y)*self.scale/old-y)
        self.ZoomLabel.Text = u'{:.0f}%'.format(self.scale*100.)

    def actual_size(self, sender, args):
        self.set_scale(1.)
        self.center_selected()

    def center_selected(self):
        key = self.selected['node_id'] if self.selected else self.network['root']
        if key in self.network['positions']:
            x, y = self.network['positions'][key]
            self.GraphScroll.ScrollToHorizontalOffset(x*self.scale-self.GraphScroll.ViewportWidth/2.)
            self.GraphScroll.ScrollToVerticalOffset(y*self.scale-self.GraphScroll.ViewportHeight/2.)

    def zoom_in(self, sender, args):
        self.set_scale(self.scale*1.2)

    def zoom_out(self, sender, args):
        self.set_scale(self.scale/1.2)

    def wheel(self, sender, args):
        self.set_scale(self.scale*(1.15 if args.Delta > 0 else 1./1.15), args.GetPosition(self.GraphScroll))
        args.Handled = True

    def pan_start(self, sender, args):
        if args.OriginalSource != self.GraphCanvas:
            return
        self.drag = (args.GetPosition(self.GraphScroll), self.GraphScroll.HorizontalOffset, self.GraphScroll.VerticalOffset)
        self.GraphScroll.CaptureMouse()
        args.Handled = True

    def pan_move(self, sender, args):
        if self.drag:
            point = args.GetPosition(self.GraphScroll)
            start, x, y = self.drag
            self.GraphScroll.ScrollToHorizontalOffset(x+start.X-point.X)
            self.GraphScroll.ScrollToVerticalOffset(y+start.Y-point.Y)
            args.Handled = True

    def pan_end(self, sender, args):
        if self.drag:
            self.drag = None
            self.GraphScroll.ReleaseMouseCapture()
            args.Handled = True

    def pan_lost(self, sender, args):
        self.drag = None
