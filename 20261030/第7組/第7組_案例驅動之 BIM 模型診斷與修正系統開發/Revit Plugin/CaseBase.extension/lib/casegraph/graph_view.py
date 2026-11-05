# -*- coding: utf-8 -*-
"""Collapsible hierarchical graph cards and a human-readable inspector."""
from System import Action
from System.Windows import Thickness, TextWrapping, FontWeights
from System.Windows.Controls import Button, Canvas, StackPanel, TextBlock
from System.Windows.Shapes import Line
from System.Windows.Media import Brushes
from casegraph.panel import Item
from casegraph.presentation import RELATIONS, KINDS, Row, caption, detail_rows, relationship_rows, same_category
from casegraph.hierarchy import build_hierarchy, visible_rows, path_to
from casegraph.case_graph import navigation_record


class GraphView(object):
    def open_knowledge_window(self, sender, args):
        from casegraph.knowledge_window import KnowledgeWindow
        from System.Windows import WindowState
        if self.knowledge_window is None:
            window = KnowledgeWindow(self)
            # Revit 2022 exposes its supported owner handle. Ownership keeps the
            # modeless window with Revit and closes it when Revit exits.
            from System.Windows.Interop import WindowInteropHelper
            from pyrevit import HOST_APP
            handle = getattr(HOST_APP.uiapp, 'MainWindowHandle', None)
            if handle:
                WindowInteropHelper(window).Owner = handle
            self.knowledge_window = window
            window.Show()
        else:
            window = self.knowledge_window
            if window.WindowState == WindowState.Minimized:
                window.WindowState = WindowState.Normal
            window.Activate()
        if not self.graph:
            self.schedule_refresh('graph')
        self.refresh_timer.Start()

    def sync_knowledge_window(self):
        if self.knowledge_window is not None:
            self.knowledge_window.set_graph(self.graph, self.GraphSummary.Text, self.selected_node)

    def clear_inspector(self):
        self.NodeTitle.Text = u'點選節點查看元素資訊'
        self.NodeSubtitle.Text = u'群組可展開／收合；元素可自動高亮與比較。'
        self.NodeProperties.Items.Clear()
        self.NodeRelations.Items.Clear()
        self.CompareNode.IsEnabled = False
        self.GraphNavigate.IsEnabled = False

    def inspect_node(self, record):
        if self.knowledge_window is not None and self.knowledge_window.graph is self.graph:
            self.knowledge_window.select_record(record)
        self.NodeTitle.Text = caption(record)
        self.NodeSubtitle.Text = KINDS.get(record['node_type'], u'元素')
        if record.get('_recorded'):
            self.NodeSubtitle.Text += u' · 歷史記錄（尚未從來源模型重新讀取）'
        self.NodeProperties.Items.Clear()
        self.NodeRelations.Items.Clear()
        self.GraphNavigate.IsEnabled = navigation_record(record, self.graph) is not None
        if record['node_type'] == 'GROUP':
            p = record['properties']
            for row in [Row(u'分組層級', p['hierarchy_level']), Row(u'包含項目', unicode(p['element_count'])),
                        Row(u'來源文件', p.get('document_title') or u'依下層元素的來源文件')]:
                self.NodeProperties.Items.Add(row)
            self.NodeRelations.Items.Add(Row(u'分類說明', u'此為檢視分組，不代表模型中的實體連接；展開後選取個別元素。'))
        else:
            for row in detail_rows(record, self.graph):
                self.NodeProperties.Items.Add(row)
            if self.graph:
                for row in relationship_rows(record, self.graph):
                    self.NodeRelations.Items.Add(row)
        self.CompareNode.IsEnabled = bool(self.target and record['node_type'] == 'ELEMENT'
            and record['node_id'] != self.target['node_id'] and same_category(self.target, record))

    def node_click(self, sender, args):
        record = sender.Tag
        self._selected_occurrence = sender.CommandParameter
        self.selected_node = record
        self.inspect_node(record)
        if record['node_type'] == 'GROUP':
            self.pending_result = None
            key = self._selected_occurrence
            if key in self.collapsed_groups:
                self.collapsed_groups.remove(key)
            else:
                self.collapsed_groups.add(key)
            self.draw_graph(record['node_id'])
            self.Status.Text = u'群組已收合。' if key in self.collapsed_groups else u'群組已展開；點選個別元素可高亮。'
        else:
            self.draw_graph(record['node_id'])
            self.queue_result(record, inspect=True)

    def graph_navigate(self, sender, args):
        self.pending_result = None
        record = navigation_record(self.selected_node, self.graph)
        if record:
            self.run(lambda uidoc:self.show_node(uidoc, record))

    def collapse_groups(self, sender, args):
        if self.graph_tree:
            self.collapsed_groups.update(key for key, entry in self.graph_tree['entries'].items()
                if entry['record']['node_type'] == 'GROUP' and entry['record']['properties']['hierarchy_level'] in (u'類型', u'模型關係'))
            self.draw_graph()

    def expand_groups(self, sender, args):
        self.collapsed_groups.clear()
        self.draw_graph()

    def focus_graph_result(self, record, occurrence=None):
        if not self.graph_tree:
            return
        entries = self.graph_tree['entries']
        keys = [occurrence, 'match:'+record['node_id'], 'context:'+record['node_id'], 'root']
        key = next((k for k in keys if k in entries and entries[k]['record']['node_id'] == record['node_id']), None)
        if key is None:
            key = next((k for k, entry in entries.items() if entry['record']['node_id'] == record['node_id']), None)
        if key is None:
            return
        self.collapsed_groups.difference_update(path_to(self.graph_tree, key))
        self._selected_occurrence = key
        self.selected_node = record
        self.draw_graph(record['node_id'])
        self.inspect_node(record)
        self._focus_occurrence = key
        self.Dispatcher.BeginInvoke(Action(self.scroll_to_graph_selection))

    def scroll_to_graph_selection(self):
        key = self._focus_occurrence
        if self.Tabs.SelectedItem == self.GraphTab and key in getattr(self, '_graph_positions', {}):
            y = self._graph_positions[key][1]
            self.GraphScroll.ScrollToVerticalOffset(max(0, y*self.scale-self.GraphScroll.ViewportHeight/2.))

    def draw_graph(self, selected=None):
        self.GraphCanvas.Children.Clear()
        if not self.graph:
            self.graph_tree = None
            return
        desired = ['ALL'] + sorted(set(e['relationship_type'] for e in self.graph['edges']))
        current = getattr(self.RelationFilter.SelectedItem, 'data', 'ALL')
        if [getattr(i, 'data', None) for i in self.RelationFilter.Items] != desired:
            self._updating_filter = True
            try:
                self.RelationFilter.Items.Clear()
                for code in desired:
                    item = Item(RELATIONS.get(code, code), code)
                    self.RelationFilter.Items.Add(item)
                    if code == current:
                        self.RelationFilter.SelectedItem = item
                if self.RelationFilter.SelectedItem is None:
                    self.RelationFilter.SelectedIndex = 0
            finally:
                self._updating_filter = False
        self.graph_tree = build_hierarchy(self.graph, self.RelationFilter.SelectedItem.data)
        entries = self.graph_tree['entries']
        for key, entry in entries.items():
            if entry['record']['node_type'] == 'GROUP' and key not in self.seen_graph_groups:
                p = entry['record']['properties']
                if p['hierarchy_level'] == u'類型' and p['element_count'] > 3:
                    self.collapsed_groups.add(key)
                self.seen_graph_groups.add(key)
        rows = visible_rows(self.graph_tree, self.collapsed_groups)
        positions = dict((key, (150.+depth*28., 48.+i*82.)) for i, (key, depth) in enumerate(rows))
        self._graph_positions = positions
        self._root_position = positions['root']
        self.GraphCanvas.Width = max(p[0] for p in positions.values())+150.
        self.GraphCanvas.Height = len(rows)*82.+18.
        selected_key = self._selected_occurrence
        if selected_key not in entries or (selected and entries[selected_key]['record']['node_id'] != selected):
            selected_key = next((key for key, depth in rows if entries[key]['record']['node_id'] == selected), None)
        selected_path = set(path_to(self.graph_tree, selected_key)) if selected_key else set()
        for key, depth in rows:
            entry = entries[key]
            record = entry['record']
            x, y = positions[key]
            parent = self.graph_tree['parents'].get(key)
            if parent in positions:
                px, py = positions[parent]
                elbow_x = px-130.+10.
                brush = Brushes.DarkOrange if key == selected_key or key in selected_path else Brushes.LightSlateGray
                for ax, ay, bx, by in [(elbow_x, py+32., elbow_x, y), (elbow_x, y, x-130., y)]:
                    line = Line()
                    line.X1, line.Y1, line.X2, line.Y2 = ax, ay, bx, by
                    line.Stroke, line.StrokeThickness = brush, 1.5
                    line.ToolTip = u'分層瀏覽；模型關係詳見下方「關係說明」。'
                    self.GraphCanvas.Children.Add(line)
            grouped = record['node_type'] == 'GROUP'
            p = record['properties']
            content = StackPanel()
            title = TextBlock()
            prefix = (u'＋ ' if key in self.collapsed_groups else u'− ') if grouped else ''
            if grouped:
                prefix += p['hierarchy_level']+u'：'
            title.Text = prefix+record['name']
            title.TextWrapping, title.FontWeight, title.MaxHeight = TextWrapping.Wrap, FontWeights.SemiBold, 34.
            content.Children.Add(title)
            subtitle = TextBlock()
            subtitle.FontSize = 11.
            if grouped:
                subtitle.Text = u'{} 項 · {}'.format(p['element_count'], p.get('document_title') or u'點選展開／收合')
                if p.get('same_type'):
                    subtitle.Text = u'{} 個元素 · 與基準相同 Revit 類型'.format(p['element_count'])
            else:
                subtitle.Text = u'ID {} · {}'.format(p.get('element_id') or u'—', KINDS.get(record['node_type'], u'元素'))
                if p.get('_rule_violation'):
                    subtitle.Text += u' · 相交規則違規'
                elif p.get('_model_check'):
                    subtitle.Text += u' · '+(u'實體重疊' if p['_model_check'] == 'OVERLAP' else u'檢查待確認')
                if entry['match']:
                    subtitle.Text += u' · {:.0f}%'.format(entry['match']['properties']['score']*100)
            content.Children.Add(subtitle)
            button = Button()
            button.Content, button.Width, button.Height = content, 260., 64.
            button.FontSize, button.Padding, button.Margin = 13., Thickness(8, 4, 8, 4), Thickness(0)
            same = p.get('same_type') or (entry['match'] and entry['match']['relationship_type'] == 'SAME_TYPE')
            button.Background = Brushes.LightSkyBlue if key == 'root' else Brushes.Honeydew if same else Brushes.Lavender if grouped or entry['match'] else Brushes.WhiteSmoke
            button.BorderBrush = Brushes.DarkOrange if key == selected_key else Brushes.LightSlateGray
            if p.get('_rule_violation'):
                button.Background = Brushes.MistyRose
                button.BorderBrush = Brushes.DarkOrange if key == selected_key else Brushes.Firebrick
            elif p.get('_model_check'):
                button.Background = Brushes.MistyRose if p['_model_check'] == 'OVERLAP' else Brushes.LemonChiffon
                button.BorderBrush = Brushes.DarkOrange if key == selected_key else Brushes.Firebrick if p['_model_check'] == 'OVERLAP' else Brushes.Goldenrod
            button.Tag, button.CommandParameter, button.ToolTip = record, key, title.Text+u'\n'+subtitle.Text
            button.Click += self.node_click
            Canvas.SetLeft(button, x-130.)
            Canvas.SetTop(button, y-32.)
            self.GraphCanvas.Children.Add(button)
