# -*- coding: utf-8 -*-
"""Guided element search UI. Revit reads stay in ExternalEvent; HTTP runs off-thread."""
import json
import os
import threading
import traceback
from System import Action, Uri, TimeSpan, EventHandler, DateTime
from System.Windows import Visibility
from System.Windows.Threading import DispatcherTimer
from System.Windows.Media import ScaleTransform
from System.Windows.Controls import ItemsControl, ScrollBarVisibility
from System.Collections.Generic import List
from Autodesk.Revit import DB, UI
from Autodesk.Revit.UI.Selection import ObjectType
from Autodesk.Revit.Exceptions import OperationCanceledException
from pyrevit import forms, HOST_APP
from casegraph.panel import Panel as GraphPanel, Item
from casegraph.extractor import document_id, extract
from casegraph.element_index import snapshot, discover
from casegraph.client import request
from casegraph.refresh import RefreshQueue
from casegraph.case_graph import with_related_cases, navigation_record
from casegraph.case_scope import related_matches
from casegraph.graph_view import GraphView
from casegraph.comparison_view import ComparisonView
from casegraph.presentation import with_similar_elements, STATUS_NAMES, ISSUE_NAMES
from casegraph.navigation import FEATURES, BY_KEY, WORKFLOWS
from casegraph.palette import case_chip
from casegraph.geometry_view import GeometryView
from casegraph.geometry_rules import with_rule_overlay
from casegraph.check_view import CheckView
from casegraph.review_view import ReviewView
from casegraph.suggest_view import SuggestView
from casegraph.model_checks import with_check_overlay, LABELS as CHECK_LABELS, STATUSES as CHECK_STATUSES
from casegraph.check_issue import origin_caption
from System.IO import File, Directory
from System.Text import Encoding

BUILD = '2026.11.04.4'
PANEL_ID = 'db4a9c6a-d351-4621-8159-4ac39189e424'
INSTANCE = None
MAX_MEMBERS = 20  # Additional selected elements kept with the pinned target.
ISSUES = [('GEOMETRY_CLASH',u'幾何碰撞'),('SPATIAL_CLEARANCE',u'空間淨距'),('PARAMETER_ERROR',u'參數錯誤'),
          ('IFC_MAPPING_ERROR',u'IFC 分類錯誤'),('CONNECTION_ERROR',u'連接問題'),('DESIGN_CONFLICT',u'設計衝突'),
          ('CONSTRUCTION_ISSUE',u'施工問題'),('OTHER',u'其他')]


class MatchItem(object):
    def __init__(self, data):
        self.data = data
        n, p = data['element'], data['element']['properties']
        self.title = n['name']
        self.score_label = u'{:.0f}%'.format(data['score']*100)
        self.subtitle = u'{} | {} | ID {}'.format(p.get('family') or p.get('category') or u'未提供',p.get('type_name') or u'未提供類型',p.get('element_id') or '-')
        self.reason = u'；'.join(data['evidence'][:2])
        if data['completeness'] < .5:
            self.reason += u'（可比較資料較少）'


class CaseItem(object):
    def __init__(self, data, case_id, status, title, meta, solved=True):
        self.data, self.title, self.meta = data, title, meta
        # A case without any recorded solution is unresolved, whatever its lifecycle step.
        self.status_label = STATUS_NAMES.get(status, status) if solved else u'未解決'
        self.status_back, self.status_fore = case_chip(status if solved else 'UNSOLVED')
        self.label = u'RFI-{} | {} | {}'.format(case_id, self.status_label, title)


class APIHandler(UI.IExternalEventHandler):
    def __init__(self, panel):
        self.panel, self.action = panel, None

    def Execute(self, app):
        action, self.action = self.action, None
        try:
            if app.ActiveUIDocument is None:
                raise ValueError(u'請先開啟 Revit 專案或族群文件。')
            if action:
                action(app.ActiveUIDocument)
        except OperationCanceledException:
            self.panel.Status.Text = u'已取消選取。'
        except Exception as error:
            if self.panel.check_job is not None:
                self.panel.invalidate_checks(u'所有平面圖檢查已中斷：'+unicode(error))
            self.panel.show_error(error)
        finally:
            if not self.panel.network_pending:
                self.panel.finish()

    def GetName(self):
        return u'案例驅動 BIM 診斷與修正 · 元素搜尋與定位'


class WorkspacePanel(CheckView, ReviewView, SuggestView, GeometryView, GraphView, ComparisonView, GraphPanel):
    build = BUILD
    panel_id = PANEL_ID
    panel_title = u'案例驅動 BIM 診斷與修正'
    panel_source = os.path.join(os.path.dirname(__file__),'Workspace.xaml')

    def __init__(self):
        self.ready = False
        self.refresh_queue = RefreshQueue()
        self._fit_pending = False
        self._active_document = None
        self.pending_result, self.suppress_result_selection = None, False
        self.graph_tree, self._selected_occurrence = None, None
        self.knowledge_window = None
        self.shaft_window = None
        self._focus_occurrence = None
        self.collapsed_groups = set()
        self.seen_graph_groups = set()
        self.context_graph, self.graph_matches = None, []
        self.graph_cases, self.graph_case_state = [], 'pending'
        self.graph_case_message = u'正在讀取相關案例…'
        self._graph_live, self._graph_message = False, ''
        self._overview = False
        self.comparison_pair, self.comparison_rows = None, []
        self.busy, self.network_pending = False, False
        self.current, self.graph, self.selected_node, self.target = None, None, None, None
        self.issue_origin = None
        self.target_members = []
        self.scale, self.drag = 1., None
        self.recorded_document, self.suppress_records = None, False
        self.matches = []
        self.case_rows = []
        self.active_feature = 'checks'
        self.workflow_pages = dict(checks='checks', elements='similar', cases='library')
        forms.WPFPanel.__init__(self)
        self.initialize_rules()
        self.initialize_checks()
        self.initialize_review()
        self.initialize_suggest()
        self.handler = APIHandler(self)
        self.event = UI.ExternalEvent.Create(self.handler)
        self.CaseIssueType.Items.Add(Item(u'所有', 'ALL'))
        for code,label in ISSUES:
            self.IssueType.Items.Add(Item(label,code))
            self.CaseIssueType.Items.Add(Item(label,code))
        self.IssueType.SelectedIndex = 0
        self.CaseIssueType.SelectedIndex = 0  # 相關案例: every problem of the same element type.
        for code,label in [('Low',u'低'),('Medium',u'中'),('High',u'高'),('Critical',u'緊急')]:
            self.Priority.Items.Add(Item(label,code))
        self.Priority.SelectedIndex = 1
        for code,label in [('model',u'目前模型'),('view',u'目前視圖'),('recorded',u'已記錄元素（跨專案）')]:
            self.Scope.Items.Add(Item(label,code))
        self.Scope.SelectedIndex = 0
        for value,label in [(0,u'相似度：全部'),(.5,u'相似度 ≥ 50%'),(.7,u'相似度 ≥ 70%'),(.9,u'相似度 ≥ 90%')]:
            self.Threshold.Items.Add(Item(label,value))
        self.Threshold.SelectedIndex = 0
        self.RelationFilter.Items.Add(Item(u'全部關係', 'ALL'))
        self.RelationFilter.SelectedIndex = 0
        self.refresh_timer = DispatcherTimer()
        self.refresh_timer.Interval = TimeSpan.FromMilliseconds(500)
        self.refresh_timer.Tick += self.refresh_tick
        # Events only invalidate; bounded extraction remains in ExternalEvent.
        self._document_handler = EventHandler[DB.Events.DocumentChangedEventArgs](self.document_changed)
        self._view_handler = EventHandler[UI.Events.ViewActivatedEventArgs](self.view_activated)
        HOST_APP.app.DocumentChanged += self._document_handler
        HOST_APP.uiapp.ViewActivated += self._view_handler
        self.IsVisibleChanged += self.visibility_changed
        self.ready = True
        self.update_feature_header()

    def update_feature_header(self):
        row = next((r for r in FEATURES if getattr(self, r[1]) == self.Tabs.SelectedItem), BY_KEY['checks'])
        self.active_feature, workflow = row[0], row[3]
        self.workflow_pages[workflow] = row[0]
        for key, button, pages in WORKFLOWS:
            getattr(self, button).IsChecked = key == workflow
            if pages:
                getattr(self, pages).Visibility = Visibility.Visible if key == workflow else Visibility.Collapsed
        for feature, _, _, _, button in FEATURES:
            if button:
                getattr(self, button).IsChecked = feature == row[0]
        self.TargetCard.Visibility = Visibility.Visible if row[0] in ('similar', 'related', 'graph', 'issue', 'checks', 'suggest') else Visibility.Collapsed
        # 案例庫 lists every past case, so only the target-based pages use the case scope.
        self.CaseScopeCard.Visibility = Visibility.Visible if row[0] in ('related', 'graph') else Visibility.Collapsed
        # Case results and their details must remain reachable even when the
        # pinned target/scope cards occupy most of a short docked pane.
        self.WorkAreaScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto if row[0] in ('related', 'library') else ScrollBarVisibility.Disabled
        self.AutoHighlight.Visibility = Visibility.Visible if row[0] in ('similar', 'related', 'graph', 'checks', 'library') else Visibility.Collapsed

    def open_feature(self, feature):
        # 'home' (ribbon 功能總覽) reopens the current page; a workflow opens its last page.
        feature = self.active_feature if feature == 'home' else self.workflow_pages.get(feature, feature)
        row = BY_KEY.get(feature)
        if row is None:
            raise ValueError(u'找不到此功能：' + unicode(feature))
        page = getattr(self, row[1])
        changed = self.Tabs.SelectedItem != page
        self.Tabs.SelectedItem = page
        self.update_feature_header()
        # Re-clicking a ribbon command also refreshes its results.
        if not changed:
            self.refresh_feature()

    def feature_click(self, sender, args):
        self.open_feature(unicode(sender.Tag))

    def refresh_feature(self):
        if self.active_feature == 'related':
            self.schedule_refresh('related')
        elif self.active_feature == 'graph':
            self.schedule_refresh('graph')
            self.fit_graph()
        elif self.active_feature == 'library':
            self.schedule_refresh('cases')
        elif self.active_feature == 'suggest':
            self.show_suggest_context()
            if self.target and self.suggestion_mode() == 'cases':
                self.schedule_refresh('suggest')
        elif self.active_feature == 'review':
            # Compare the open case's recorded elements with the model automatically.
            if self.current and not self.change_report_current():
                self.queue_change_scan()
        elif self.active_feature == 'similar':
            # Opening a tool does not replace a pinned target or reset any form.
            if self.target:
                self.schedule_refresh('matches')

    def log_error(self, error):
        self.log_text(u'{}\n{}'.format(unicode(error),unicode(traceback.format_exc())))

    def log_text(self, text):
        try:
            folder = os.path.abspath(os.path.join(os.path.dirname(__file__),'..','..','..','backend','data'))
            Directory.CreateDirectory(folder)
            File.AppendAllText(os.path.join(folder,'panel-errors.log'), unicode(text)+u'\n', Encoding.UTF8)
        except Exception:
            pass

    def show_error(self, error):
        self.Status.Text = unicode(error)
        self.log_error(error)

    def finish(self):
        self.busy, self.network_pending = False, False
        self.WorkArea.IsEnabled = True
        self.Progress.Visibility = Visibility.Collapsed
        if (self.refresh_queue.pending or self.pending_result or self.rule_pending or self.check_pending
                or self.pending_check_result or self.change_pending):
            self.refresh_timer.Start()

    def run(self, action, message=u'處理中…'):
        if self.busy:
            return False
        self.busy = True
        self.WorkArea.IsEnabled = False
        self.Progress.Visibility = Visibility.Visible
        self.Status.Text = message
        self.handler.action = action
        if str(self.event.Raise()) != 'Accepted':
            self.handler.action = None
            self.finish()
            self.Status.Text = u'Revit 目前忙碌，請完成目前指令後重試。'
            return False
        return True

    def network(self, work, success, failure=None):
        # Capture the URL on the UI thread. Worker functions receive only JSON data.
        base = self.Backend.Text
        self.network_pending = True
        def worker():
            try:
                result = work(lambda path,payload=None,method=None: request(base,path,payload,method))
                def completed():
                    try:
                        success(result)
                    except Exception as error:
                        self.show_error(error)
                    finally:
                        self.finish()
                self.Dispatcher.BeginInvoke(Action(completed))
            except Exception as error:
                self.log_error(error)
                message = unicode(error)
                def failed():
                    try:
                        if failure:
                            failure(message)
                        else:
                            self.Status.Text = message
                    finally:
                        self.finish()
                self.Dispatcher.BeginInvoke(Action(failed))
        thread = threading.Thread(target=worker)
        thread.daemon = True
        thread.start()

    def queue_result(self, record, inspect=False):
        if not self.ready or self.suppress_result_selection:
            return
        if record['node_type'] == 'GROUP':
            return
        # Later clicks replace pending selection, including while busy.
        self.pending_result = (record, inspect)
        self.refresh_timer.Stop()
        self.refresh_timer.Start()

    def result_clicked(self, sender, args):
        # Clicking the already-selected first row does not raise SelectionChanged.
        if args.ClickCount > 1 or self.suppress_result_selection:
            return
        container = ItemsControl.ContainerFromElement(sender, args.OriginalSource)
        if container is not None and sender.SelectedItem:
            self.queue_result(sender.SelectedItem.data['element'])

    def process_result(self, uidoc, record, inspect):
        navigation = navigation_record(record, self.graph)
        current_doc = (navigation or record)['source_document'] == document_id(uidoc.Document)
        navigable = navigation is not None
        element = uidoc.Document.GetElement(navigation['source_element_id']) if current_doc and navigable else None
        if inspect and record['node_type'] == 'ELEMENT':
            if element or not current_doc:
                record = self.read_detail(uidoc, record)
        self.focus_graph_result(record)
        if inspect:
            self.selected_node = record
            self.inspect_node(record)
        if not self.AutoHighlight.IsChecked or not navigable:
            self.Status.Text = u'已更新選取結果資訊。'
            return
        if not current_doc or element is None:
            uidoc.Selection.SetElementIds(List[DB.ElementId]())
            self.Status.Text = u'此結果來自其他文件，請開啟來源文件後再選取；已清除先前高亮。' if not current_doc else u'此元素已刪除，請重新搜尋；已清除先前高亮。'
            return
        self.show_node(uidoc, navigation)

    def set_target(self, target, refresh=True, keep_checks=False, members=None):
        """members: other selected elements; omitted, they are kept only for the same target."""
        changed = self.target is None or self.target['node_id'] != target['node_id']
        members_changed = members is not None and set(r['node_id'] for r in members if r['node_id'] != target['node_id']) != set(
            r['node_id'] for r in self.target_members)
        self.target = target
        if members is not None:
            self.target_members = [m for m in members if m['node_id'] != target['node_id']][:MAX_MEMBERS]
        elif changed:
            self.target_members = []
        if members_changed and not changed:
            self.suggest_context = None
            self.invalidate_suggestion(u'選取物已變更，請為新的目標組合重新產生建議。')
        p = target['properties']
        self.CaseTypeLabel.Text = u'{} · {} · {}'.format(p.get('category') or u'缺少類別', p.get('family') or u'缺少族群', p.get('type_name') or p.get('type_id') or u'缺少類型')
        count = len(self.target_members)
        self.TargetName.Text = target['name'] + (u'（另 {} 個）'.format(count) if count else u'')
        self.TargetCard.ToolTip = (u'基準：{}\n另選取：\n'.format(target['name']) + u'\n'.join(
            u'・{} · ID {}'.format(m['name'], m['properties'].get('element_id') or '-') for m in self.target_members)) if count else None
        self.TargetDetail.Text = u' · '.join(part for part in (p.get('category'), p.get('family'), p.get('type_name'),
                                                          u'ID {}'.format(p.get('element_id') or '-'), p.get('owner_view_name') or p.get('level')) if part)
        if changed:
            self.suggest_context = None
            self.SuggestContext.Text = ''
            self.SuggestSources.Items.Clear()
            if not keep_checks:
                self.invalidate_checks(u'基準元素已變更，請重新檢查目前視圖。')
            if self.issue_origin and not self.origin_matches_target():
                self.issue_origin = None
                self.show_issue_origin()
                self.Status.Text = u'基準元素已變更，已移除問題表單的模型檢查來源。'
            self.invalidate_rule_report(u'基準元素已變更，請重新檢查目前視圖。')
            self.pending_result = None
            self.collapsed_groups.clear()
            self.seen_graph_groups.clear()
            self.matches = []
            self.Matches.Items.Clear()
            self.SelectMatches.IsEnabled = False
            self.RelatedCases.Items.Clear()
            self.CaseDetails.Text = ''
            self.clear_graph(u'基準元素已變更，正在更新關係圖…')
            self.invalidate_suggestion(u'基準元素已變更，請重新產生建議。')
            if self.active_feature == 'suggest' and self.suggestion_mode() == 'cases':
                self.schedule_refresh('suggest')
            self.SnapshotOnly.IsChecked = False
            self.MatchSummary.Text = u'基準元素已更新，按「搜尋」開始比對。'
        self.SnapshotOnly.IsEnabled = self.case_matches_target()
        self.load_target_rules()
        self.filter_cases(None, None)
        if refresh:
            self.schedule_refresh('related', 'graph')

    def origin_matches_target(self):
        origin, target = self.issue_origin, self.target
        root = next((e for e in origin['elements'] if e['role'] == 'root'), None) if origin else None
        return bool(root and target and origin['document'] == target['source_document']
                    and root['unique_id'] == target['source_element_id'])

    def show_issue_origin(self):
        self.IssueOrigin.Text = origin_caption(self.issue_origin)
        self.IssueOrigin.Visibility = Visibility.Visible if self.issue_origin else Visibility.Collapsed

    def issue_form_unsaved(self):
        fields = (self.IssueTitle.Text.strip(), self.Description.Text.strip(), self.Notes.Text.strip())
        if not any(fields):
            return False
        if not self.current:
            return True
        draft = self.current['draft']
        return fields != (draft['title'].strip(), draft['description'].strip(), draft['notes'].strip())

    def case_matches_target(self):
        return bool(self.current and self.target and self.current['draft']['graph']['root'] == self.target['node_id'])

    def schedule_refresh(self, *parts):
        if not self.ready:
            return
        parts = set(parts)
        if 'graph' in parts and not self.SnapshotOnly.IsChecked:
            parts.add('related')
        if 'graph' in parts and self.ShowSimilar.IsChecked and not self.SnapshotOnly.IsChecked:
            parts.add('matches')
        self.refresh_queue.mark(*parts)
        if 'related' in parts and self.target:
            self.RelatedCases.Items.Clear()
            self.CaseDetails.Text = ''
            self.CaseSummary.Text = u'正在更新此元素的相關案例…'
            self.graph_cases, self.graph_case_state = [], 'pending'
            self.graph_case_message = u'正在讀取相關案例…'
            if self._graph_live:
                self.render_graph_preview()
        if 'graph' in parts and self.target:
            self.GraphSummary.Text = u'正在更新此元素的關係圖…'
        if 'suggest' in parts and self.target:
            self.SuggestSourceSummary.Text = u'正在讀取同類型的過去解法…'
        if 'matches' in parts and self.target:
            self.MatchSummary.Text = u'正在尋找其他同類別元素…'
        self.refresh_timer.Stop()
        self.refresh_timer.Start()

    def tabs_changed(self, sender, args):
        # SelectionChanged also bubbles from every ListBox/ComboBox in a tab.
        if not self.ready or args.OriginalSource != self.Tabs:
            return
        self.update_feature_header()
        self.refresh_feature()

    def related_options_changed(self, sender, args):
        if not self.ready:
            return
        self.pending_result = None
        self.filter_cases(None, None)
        self.schedule_refresh('related')

    def case_issue_type(self):
        return getattr(self.CaseIssueType.SelectedItem, 'data', None)

    def case_scope_label(self):
        return u'同元件類型 · 所有問題類型' if self.case_issue_type() == 'ALL' else u'同元件類型＋同問題類型'

    def set_case_issue_type(self, code):
        self.CaseIssueType.SelectedItem = next((i for i in self.CaseIssueType.Items if i.data == code), None)

    def issue_options_changed(self, sender, args):
        if self.ready and self.IssueType.SelectedItem:
            self.set_case_issue_type(self.IssueType.SelectedItem.data)
            self.schedule_refresh('graph')

    def graph_options_changed(self, sender, args):
        self.schedule_refresh('graph')

    def similarity_options_changed(self, sender, args):
        self.schedule_refresh('matches', 'graph')

    def graph_refresh_click(self, sender, args):
        self.schedule_refresh('graph')

    def visibility_changed(self, sender, args):
        if self.ready and self.IsVisible:
            self.schedule_refresh('related', 'graph', 'cases')

    def document_changed(self, sender, args):
        try:
            if self.shaft_window is not None:
                self.shaft_window.model_changed(sender, args)
            identity = document_id(args.GetDocument())
            self.checks_model_changed(identity)
            self.rule_model_changed(identity)
            if self.comparison_pair and any(r['source_document'] == identity for r in self.comparison_pair):
                self.ComparisonNotice.Text = u'來源模型已修改；請按「重新讀取比較」取得最新資料。'
            self.changes_model_changed(identity)
            if self.target and identity == self.target['source_document']:
                self.invalidate_suggestion(u'模型已變更，請重新讀取元素並產生建議。')
                self.schedule_refresh('related', 'graph')
        except Exception as error:
            self.log_error(error)

    def view_activated(self, sender, args):
        try:
            identity = document_id(args.CurrentActiveView.Document)
            self.checks_model_changed(identity, view_change=True)
            self.rule_view_changed()
            if identity != self._active_document:
                self._active_document = identity
                if self.target:
                    self.invalidate_suggestion(u'目前文件已變更，請重新產生建議。')
                    self.schedule_refresh('related', 'graph')
        except Exception as error:
            self.log_error(error)

    def extract_graph(self, uidoc, element):
        related = []
        # Other selected elements are recorded with the issue as related elements.
        for member in self.target_members:
            if member['source_document'] == self.target['source_document']:
                other = uidoc.Document.GetElement(member['source_element_id'])
                if other and other.Id != element.Id:
                    related.append(other)
        if self.IncludeRelated.IsChecked:
            for item in self.Elements.Items:
                if item.data['source_document'] == self.target['source_document']:
                    other = uidoc.Document.GetElement(item.data['source_element_id'])
                    if other and other.Id != element.Id:
                        related.append(other)
        findings = []
        origin = self.issue_origin
        if origin and self.origin_matches_target():
            if origin.get('kind') == 'SHAFT_CHECK':
                evidence = origin_caption(origin)
            else:
                evidence = u'模型檢查「{}」#{}：{}（{}）'.format(CHECK_LABELS[origin['check']], origin['result_id'],
                                                        CHECK_STATUSES[origin['status']], origin['checked_at'])
            properties = dict(check=origin['check'], result_id=origin['result_id'], status=origin['status'],
                              checked_at=origin['checked_at'])
            for item in origin['elements']:
                other = uidoc.Document.GetElement(item['unique_id']) if item['role'] == 'related' else None
                if other and other.Id != element.Id:
                    # Only a solid boolean overlap is verified evidence; other findings are candidates.
                    findings.append((other, evidence, origin['status'] == 'OVERLAP', properties))
        return extract(uidoc.Document, element, self.IssueType.SelectedItem.data, related,
                       depth=2 if self.Deep.IsChecked else 1, limit=160 if self.Deep.IsChecked else 80,
                       findings=findings)

    def refresh_tick(self, sender, args):
        self.refresh_timer.Stop()
        if self.busy or not (self.IsVisible or self.check_job is not None
                             or (self.knowledge_window is not None and self.knowledge_window.IsVisible)):
            return  # finish/visibility_changed resumes the pending work.
        if self.pending_result:
            record, inspect = self.pending_result
            self.pending_result = None
            if not self.run(lambda uidoc:self.process_result(uidoc, record, inspect), u'選取結果並更新高亮…'):
                self.pending_result = (record, inspect)
                self.refresh_timer.Start()
            return
        if self.rule_pending:
            self.rule_pending = False
            if not self.run(self.perform_rule_check, u'檢查目前視圖的相交規則…'):
                self.rule_pending = True
                self.refresh_timer.Start()
            return
        if self.pending_check_result:
            ticket = self.pending_check_result
            self.pending_check_result = None
            if not self.run(lambda uidoc:self.locate_check(uidoc, ticket), u'定位檢查結果…'):
                self.pending_check_result = ticket
                self.refresh_timer.Start()
            return
        if self.check_pending:
            self.check_pending = False
            if not self.run(self.perform_checks, u'檢查所選範圍的常見問題…'):
                self.check_pending = True
                self.refresh_timer.Start()
            return
        if self.change_pending:
            self.change_pending = False
            if not self.run(self.detect_changes, u'比對案例元件的模型變更…'):
                self.change_pending = True
                self.refresh_timer.Start()
            return
        ticket = self.refresh_queue.take()
        if not ticket:
            return
        def action(uidoc):
            self._active_document = document_id(uidoc.Document)
            element = None
            needs_target = any(p in ticket for p in ('related', 'graph', 'matches', 'cases', 'suggest'))
            if self.target is None and needs_target:
                ids = list(uidoc.Selection.GetElementIds())
                selected = uidoc.Document.GetElement(ids[0]) if len(ids) == 1 else None
                if selected and not isinstance(selected, DB.ElementType):
                    self.set_target(snapshot(uidoc.Document, selected), refresh=False)
                    missing_parts = [p for p in ('related', 'graph') if p not in ticket]
                    if missing_parts:
                        self.schedule_refresh(*missing_parts)
                else:
                    self.CaseSummary.Text = u'請先選取／讀取一個基準元素。'
                    self.GraphSummary.Text = u'請先選取／讀取一個基準元素。'
            if self.target and needs_target:
                if self.target['source_document'] == self._active_document:
                    element = uidoc.Document.GetElement(self.target['source_element_id'])
                    if element is None:
                        self.Status.Text = u'模型中已找不到此基準元素，案例比對使用先前讀取的資料。'
                    else:
                        self.set_target(snapshot(uidoc.Document, element), refresh=False)
            match_payload = None
            if 'matches' in ticket and self.target:
                self.graph_matches = []
                self.matches = []
                self.Matches.Items.Clear()
                self.SelectMatches.IsEnabled = False
                scope = self.Scope.SelectedItem.data
                if scope == 'recorded' or element:
                    try:
                        found = discover(uidoc, element, scope) if scope != 'recorded' else dict(target=self.target, candidates=[], truncated=False, skipped=0)
                        match_payload = dict(target=found['target'], candidates=found['candidates'], scope=scope,
                            limit=20, minimum_score=self.Threshold.SelectedItem.data, truncated=found['truncated'])
                        if found['skipped']:
                            self.MatchSummary.Text = u'有 {} 個元素無法讀取；正在比對其餘元素。'.format(found['skipped'])
                    except Exception as error:
                        self.MatchSummary.Text = u'同類元素搜尋失敗：'+unicode(error)
                        self.log_error(error)
                else:
                    self.MatchSummary.Text = u'請開啟來源模型，或將搜尋範圍改為「已記錄元素（跨專案）」。'
            if 'graph' in ticket and self.target:
                try:
                    if self.SnapshotOnly.IsChecked and self.case_matches_target():
                        self.display_graph(self.current['draft']['graph'], u'已儲存快照 · RFI-{} 第 {} 版'.format(self.current['id'], self.current['revision']))
                    elif element:
                        graph = self.extract_graph(uidoc, element)
                        self.display_graph(graph, u'目前模型預覽 · '+DateTime.Now.ToString('HH:mm:ss'), live=True)
                    else:
                        self.clear_graph(u'模型中已找不到此元素，請重新選取；或切換「已儲存快照」。' if self.target['source_document'] == self._active_document else u'請開啟此元素的來源文件以更新關係圖；已開啟案例可切換「已儲存快照」。')
                except Exception as error:
                    self.clear_graph(u'關係圖更新失敗：'+unicode(error))
                    self.log_error(error)
            payload = dict(target=self.target, issue_type=self.case_issue_type(), verified_only=bool(self.VerifiedOnly.IsChecked)) if self.target and 'related' in ticket else None
            # 修改建議 sources: past solutions for the same element type, verified or not.
            p = self.target['properties'] if self.target else {}
            suggest_payload = dict(query='', category=p.get('category'), family=p.get('family'), type_name=p.get('type_name'),
                                   verified_only=False, limit=10) if self.target and 'suggest' in ticket else None
            if not payload and not match_payload and not suggest_payload and 'cases' not in ticket:
                self.Status.Text = self.GraphSummary.Text
                return
            def work(api):
                result = {}
                if match_payload:
                    result['matches'] = api('/elements/search', match_payload)
                if payload:
                    result['related'] = api('/cases/related', payload)
                if 'cases' in ticket:
                    result['cases'] = api('/cases')
                if suggest_payload:
                    result['suggest'] = api('/solutions/search', suggest_payload)
                return result
            def done(result):
                if 'matches' in result and self.refresh_queue.valid(ticket, 'matches'):
                    self.display_matches(result['matches'])
                if 'related' in result and self.refresh_queue.valid(ticket, 'related'):
                    self.display_related(result['related'])
                    if element is None:
                        self.CaseSummary.Text += u'\n比對基準為歷史資料；來源元素目前不在作用中的模型。'
                if 'cases' in result and self.refresh_queue.valid(ticket, 'cases'):
                    self.display_cases(result['cases'])
                if 'suggest' in result and self.refresh_queue.valid(ticket, 'suggest'):
                    self.show_suggest_sources(result['suggest']['results'])
                self.Status.Text = u'相關資料已更新。'
            def failed(message):
                if match_payload and self.refresh_queue.valid(ticket, 'matches'):
                    self.MatchSummary.Text = u'同類元素更新失敗：'+message
                    if self._graph_live:
                        self.GraphSummary.Text += u'\n同類元素尚未更新，請稍後按「立即更新」。'
                if suggest_payload and self.refresh_queue.valid(ticket, 'suggest'):
                    self.SuggestSourceSummary.Text = u'過去解法讀取失敗：'+message
                if payload and self.refresh_queue.valid(ticket, 'related'):
                    self.RelatedCases.Items.Clear()
                    self.CaseSummary.Text = u'案例更新失敗：'+message
                    self.graph_cases, self.graph_case_state = [], 'failed'
                    self.graph_case_message = u'案例庫未能更新，請按「立即更新」重試。'
                    if self._graph_live:
                        self.render_graph_preview()
                self.Status.Text = message
            self.network(work, done, failed)
        if not self.run(action, u'自動更新相關資料…'):
            self.refresh_queue.pending.update(ticket)
            self.refresh_timer.Start()

    def display_related(self, result):
        # The same contract protects both lists and the graph, including old service responses.
        result = dict(result)
        original_count = len(result['results'])
        result['results'] = [r for r in result['results'] if related_matches(self.target, self.case_issue_type(), r)
                             and (not self.VerifiedOnly.IsChecked or r['status'] == 'VERIFIED')]
        if len(result['results']) != original_count:
            result['message'] = u'沒有符合「'+self.case_scope_label()+u'」的案例。'
            if result['results']:
                result['message'] = self.case_scope_label()+u'：顯示 {} 個案例。'.format(len(result['results']))
        selected = self.RelatedCases.SelectedItem
        selected_id = selected.data['case_id'] if selected else None
        self.suppress_result_selection = True
        try:
            self.RelatedCases.Items.Clear()
            for case in result['results']:
                item = CaseItem(case, case['case_id'], case['status'], case['title'], u'RFI-{} · {} · {:.0f}% 相似'.format(
                    case['case_id'], ISSUE_NAMES.get(case['issue_type'], case['issue_type']), case['score']*100),
                    bool((case.get('solution') or case.get('resolution_note') or u'').strip()))
                self.RelatedCases.Items.Add(item)
                if case['case_id'] == selected_id:
                    self.RelatedCases.SelectedItem = item
            self.CaseSummary.Text = result['message'] + u' · '+DateTime.Now.ToString('HH:mm:ss')
            if self.RelatedCases.SelectedItem is None and result['results']:
                self.RelatedCases.SelectedIndex = 0
        finally:
            self.suppress_result_selection = False

        self.graph_cases, self.graph_case_state = result['results'], 'ready'
        self.graph_case_message = result['message']
        if result.get('truncated'):
            self.graph_case_message += u' 已達搜尋上限。'
        if self.VerifiedOnly.IsChecked:
            self.graph_case_message += u' 篩選：只看已驗證案例。'
        if self.context_graph and self._graph_live:
            self.render_graph_preview()

    def display_cases(self, rows):
        self.case_rows = rows
        self.filter_cases(None, None)

    def filter_cases(self, sender, args):
        if not self.ready:
            return
        selected = self.Cases.SelectedItem
        selected_id = selected.data['id'] if selected else None
        query = self.CaseQuery.Text.strip().lower()
        previous_suppression = self.suppress_result_selection
        self.suppress_result_selection = True
        try:
            self.Cases.Items.Clear()
            for row in self.case_rows:
                # 案例庫 is every past case and solution, independent of the pinned element.
                draft = row['draft']
                solved = bool((row.get('solution') or u'').strip())
                text = u'RFI-{} {} {} {} {} {}'.format(row['id'], draft['title'], draft['description'], row.get('solution', ''),
                                                      STATUS_NAMES.get(row['status'], row['status']), u'' if solved else u'未解決')
                if query and query not in text.lower():
                    continue
                root = next((n for n in draft['graph']['nodes'] if n['node_id'] == draft['graph']['root']), {})
                item = CaseItem(row, row['id'], row['status'], draft['title'], u'RFI-{} · {} · {}'.format(
                    row['id'], ISSUE_NAMES.get(draft['issue_type'], draft['issue_type']),
                    (root.get('properties') or {}).get('category') or u'未提供類別'), solved)
                self.Cases.Items.Add(item)
                if row['id'] == selected_id:
                    self.Cases.SelectedItem = item
        finally:
            self.suppress_result_selection = previous_suppression
        if selected and self.Cases.SelectedItem is None and self.pending_result:
            if self.pending_result[0]['node_id'] == selected.data['draft']['graph']['root']:
                self.pending_result = None
        unsolved = sum(1 for item in self.Cases.Items if item.status_label == u'未解決')
        self.LibrarySummary.Text = u'{} 個案例'.format(self.Cases.Items.Count) + (u' · {} 個未解決'.format(unsolved) if unsolved else u'')

    def library_changed(self, sender, args):
        item = self.Cases.SelectedItem
        if item:
            row = item.data
            self.LibraryDetails.Text = u'{}\n\n{}\n\n{}'.format(row['draft']['title'], row['draft']['description'],
                u'解決方案：\n'+row['solution'] if (row.get('solution') or u'').strip() else u'未解決：尚未記錄解法。')
            graph = row['draft']['graph']
            root = next(n for n in graph['nodes'] if n['node_id'] == graph['root'])
            self.queue_result(root)
        else:
            self.LibraryDetails.Text = ''
        self.OpenLibraryCase.IsEnabled = self.OpenLibrarySolution.IsEnabled = bool(item)
        self.LocateLibraryCase.IsEnabled = self.DeleteLibraryCase.IsEnabled = bool(item)

    def library_clicked(self, sender, args):
        if args.ClickCount > 1 or self.suppress_result_selection:
            return
        if ItemsControl.ContainerFromElement(sender, args.OriginalSource) is not None and self.Cases.SelectedItem:
            graph = self.Cases.SelectedItem.data['draft']['graph']
            self.queue_result(next(n for n in graph['nodes'] if n['node_id'] == graph['root']))

    def locate_library_case(self, sender, args):
        item = self.Cases.SelectedItem
        if item:
            graph = item.data['draft']['graph']
            root = next(n for n in graph['nodes'] if n['node_id'] == graph['root'])
            self.pending_result = None
            self.run(lambda uidoc:self.show_node(uidoc, root), u'定位案例元件…')

    def open_library_solution(self, sender, args):
        if self.Cases.SelectedItem:
            self.open_case(self.Cases.SelectedItem.data['id'], destination='review')

    def delete_library_case(self, sender, args):
        item = self.Cases.SelectedItem
        if item:
            self.delete_case(item.data['id'], item.title)

    def delete_current_case(self, sender, args):
        if self.current:
            self.delete_case(self.current['id'], self.current['draft']['title'])

    def delete_case(self, case_id, title):
        if not forms.alert(u'要永久刪除 RFI-{}「{}」嗎？\n問題、解法、審查歷程、分析與套用紀錄會一併刪除，無法復原。\nRevit 模型不受影響。'.format(case_id, title),
                           title=u'案例驅動 BIM 診斷與修正', ok=False, yes=True, no=True):
            self.Status.Text = u'已取消刪除。'
            return
        def action(uidoc):
            def done(result):
                opened = bool(self.current and self.current['id'] == case_id)
                if opened:
                    self.reset_issue_form()
                self.case_rows = [r for r in self.case_rows if r['id'] != case_id]
                self.filter_cases(None, None)
                self.Status.Text = u'已刪除 RFI-{}。'.format(case_id)
                # Related lists, graph case markers and 修改建議 sources may still show the case.
                self.schedule_refresh(*(['cases', 'related'] + (['graph'] if opened else []) + (['suggest'] if self.target else [])))
            self.network(lambda api: api('/cases/{}'.format(case_id), method='DELETE'), done)
        self.run(action, u'刪除 RFI-{}…'.format(case_id))

    def display_matches(self, result):
        previous = self.Matches.SelectedItem
        previous_id = previous.data['element']['node_id'] if previous else None
        self.matches = result['results']
        self.graph_matches = result['results']
        self.suppress_result_selection = True
        try:
            self.Matches.Items.Clear()
            for match in self.matches:
                item = MatchItem(match)
                self.Matches.Items.Add(item)
                if match['element']['node_id'] == previous_id:
                    self.Matches.SelectedItem = item
            self.MatchSummary.Text = result['message']
            self.SelectMatches.IsEnabled = any(m['element']['source_document'] == self.target['source_document'] for m in self.matches)
            if self.matches and self.Matches.SelectedItem is None:
                self.Matches.SelectedIndex = 0
        finally:
            self.suppress_result_selection = False
        if self.context_graph and self._graph_live and self.context_graph['root'] == self.target['node_id']:
            self.render_graph_preview()

    def clear_graph(self, message):
        self.graph, self.selected_node = None, None
        self.graph_tree, self._selected_occurrence = None, None
        self._focus_occurrence = None
        self.context_graph, self.graph_matches = None, []
        self.graph_cases, self.graph_case_state = [], 'pending'
        self.graph_case_message = u'正在讀取相關案例…'
        self.GraphCanvas.Children.Clear()
        self.clear_inspector()
        self.GraphSummary.Text = message
        self.sync_knowledge_window()
        self._updating_filter = True
        try:
            self.RelationFilter.Items.Clear()
            self.RelationFilter.Items.Add(Item(u'全部關係', 'ALL'))
            self.RelationFilter.SelectedIndex = 0
        finally:
            self._updating_filter = False

    def display_graph(self, graph, message, live=False):
        self.context_graph = graph
        self._graph_live, self._graph_message = live, message
        self.render_graph_preview()

    def render_graph_preview(self):
        if not self.context_graph:
            return
        previous = self.selected_node['node_id'] if self.selected_node else None
        self.graph = with_similar_elements(self.context_graph, self.target, self.graph_matches) if self._graph_live and self.ShowSimilar.IsChecked else self.context_graph
        if self._graph_live:
            current = self.current if self.graph_case_state != 'failed' else None
            if current and self.VerifiedOnly.IsChecked and current['status'] != 'VERIFIED':
                current = None
            self.graph = with_related_cases(self.graph, self.graph_cases, current, self.case_issue_type())
            self.graph = with_rule_overlay(self.graph, self.rule_report)
            self.graph = with_check_overlay(self.graph, self.check_report)
        self.selected_node = next((n for n in self.graph['nodes'] if n['node_id'] == previous), None) or next(n for n in self.graph['nodes'] if n['node_id'] == self.graph['root'])
        lines = [u'{} · {} 節點／{} 關係'.format(self._graph_message, len(self.graph['nodes']), len(self.graph['edges']))]
        if self._graph_live and self.graph_case_state != 'ready':
            lines.append(self.graph_case_message)
        if self._graph_live and self.rule_report:
            lines.append(u'相交規則：{} 違規／{} 未確認'.format(self.rule_report['violations'], self.rule_report['unknown']))
        if self._graph_live and self.check_report:
            lines.append(u'模型檢查：{} 待處理／{} 未確認'.format(self.check_report['findings'], self.check_report['unknown']))
        lines.extend(self.graph.get('warnings') or [])
        self.GraphSummary.Text = u'\n'.join(lines)
        self.draw_graph(self.selected_node['node_id'])
        self.inspect_node(self.selected_node)
        self.sync_knowledge_window()
        self._fit_pending = True
        self._overview = False
        self.Dispatcher.BeginInvoke(Action(self.fit_graph))

    def fit_graph(self):
        if not self.graph or not self._fit_pending or self.Tabs.SelectedItem != self.GraphTab:
            return
        width, height = self.GraphScroll.ViewportWidth, self.GraphScroll.ViewportHeight
        if width < 50 or height < 50:
            return
        self.scale = max(.01, min(1., (width-16)/self.GraphCanvas.Width, (height-16)/self.GraphCanvas.Height)) if self._overview else max(.75, min(1., (width-16)/self.GraphCanvas.Width))
        self.GraphCanvas.LayoutTransform = ScaleTransform(self.scale, self.scale)
        root_x = getattr(self, '_root_position', (0, 0))[0]
        self.GraphScroll.ScrollToHorizontalOffset(0 if self._overview else max(0, root_x*self.scale-width/2.))
        self.GraphScroll.ScrollToVerticalOffset(0)
        self._fit_pending = False
        if not self._overview:
            self.Dispatcher.BeginInvoke(Action(self.scroll_to_graph_selection))

    def graph_size_changed(self, sender, args):
        self.fit_graph()

    def filter_changed(self, sender, args):
        if self.graph and not getattr(self, '_updating_filter', False):
            self.selected_node = None
            self.clear_inspector()
            self.draw_graph()
            self._fit_pending = True
            self.Dispatcher.BeginInvoke(Action(self.fit_graph))

    def fit_click(self, sender, args):
        self._fit_pending = True
        self._overview = True
        self._focus_occurrence = None
        self.fit_graph()

    def resolve_target(self, uidoc, read_selection=False, pick_if_empty=False):
        doc = uidoc.Document
        if read_selection or self.target is None:
            ids = list(uidoc.Selection.GetElementIds())
            if ids:
                elements = [doc.GetElement(i) for i in ids]
            elif pick_if_empty:
                # Pick order is kept: the first element becomes the pinned target.
                references = uidoc.Selection.PickObjects(ObjectType.Element, u'依序點選一個或多個元素（第一個為基準），完成後按「完成」')
                elements = [doc.GetElement(r.ElementId) for r in references]
            else:
                raise ValueError(u'請先在 Revit 選取元素，或按「選取」。')
            elements = [e for e in elements if e is not None and not isinstance(e, DB.ElementType)]
            if not elements:
                raise ValueError(u'請選取模型中的實例，不是族群類型。')
            if len(elements) > MAX_MEMBERS+1:
                raise ValueError(u'最多選取 {} 個元素，請縮小選取範圍。'.format(MAX_MEMBERS+1))
            self.set_target(snapshot(doc,elements[0]), members=[snapshot(doc,e) for e in elements[1:]])
            return elements[0]
        if self.target['source_document'] != document_id(doc):
            raise ValueError(u'搜尋基準屬於其他文件。請在目前文件重新按「選取／讀取元素」。')
        element = doc.GetElement(self.target['source_element_id'])
        if element is None:
            raise ValueError(u'基準元素已被刪除，請重新選取。')
        self.set_target(snapshot(doc,element))
        return element

    def read_click(self, sender, args):
        def action(uidoc):
            self.resolve_target(uidoc,True,True)
            self.Status.Text = u'已讀取基準元素' + (u'及另 {} 個元素。'.format(len(self.target_members)) if self.target_members else u'。')
        self.run(action,u'讀取目前選取的元素…')

    def similar_click(self, sender, args):
        def action(uidoc):
            self.resolve_target(uidoc)
            self.schedule_refresh('matches', 'graph')
        if self.target:
            self.schedule_refresh('matches', 'graph')
        else:
            self.run(action, u'讀取搜尋基準…')

    def match_changed(self, sender, args):
        item = self.Matches.SelectedItem
        self.LocateMatch.IsEnabled = item is not None
        self.UseMatch.IsEnabled = item is not None
        self.CompareMatch.IsEnabled = item is not None
        if item is None:
            self.MatchDetails.Text = ''
            return
        data,p = item.data,item.data['element']['properties']
        lines = [u'相似度 {:.0f}% · 可比較資料 {:.0f}%'.format(data['score']*100,data['completeness']*100),
                 u'文件：'+(p.get('document_title') or item.data['element']['source_document']),
                 u'位置：'+(p.get('owner_view_name') or p.get('level') or u'未提供'),u'',u'相同／接近的特徵']
        lines.extend(data['evidence'] or [u'沒有可確認的相同特徵。'])
        if data['differences']:
            lines.extend([u'',u'差異（基準 → 結果）'])
            lines.extend(u'{}：{} → {}'.format(d['field'],d['target'],d['candidate']) for d in data['differences'])
        if data['source'] == 'recorded':
            lines.extend([u'',u'此為已記錄的歷史快照，模型可能已變更。'])
        self.MatchDetails.Text = u'\n'.join(lines)
        self.queue_result(item.data['element'])

    def show_node(self, uidoc, record):
        if record['node_type'] in ('ISSUE', 'SOLUTION', 'GROUP', 'PROJECT'):
            raise ValueError(u'此節點是案例資料；請選取模型元素節點以定位。')
        if record['source_document'] != document_id(uidoc.Document):
            raise ValueError(u'此元素來自其他文件，請先開啟來源文件：'+(record['properties'].get('document_title') or record['source_document']))
        element = uidoc.Document.GetElement(record['source_element_id'])
        if element is None:
            raise ValueError(u'模型中已找不到此元素，請重新搜尋。')
        if element.ViewSpecific:
            view = uidoc.Document.GetElement(element.OwnerViewId)
            if view and not view.IsTemplate and view.Id != uidoc.ActiveView.Id:
                uidoc.ActiveView = view
        ids = List[DB.ElementId]()
        ids.Add(element.Id)
        uidoc.Selection.SetElementIds(ids)
        uidoc.ShowElements(ids)
        self.Status.Text = u'已在 Revit 定位並選取元素。'

    def navigate_click(self, sender, args):
        self.pending_result = None
        def action(uidoc):
            self.resolve_target(uidoc)
            self.show_node(uidoc,self.target)
        self.run(action)

    def locate_match(self, sender, args):
        self.pending_result = None
        item = self.Matches.SelectedItem
        if item:
            self.run(lambda uidoc:self.show_node(uidoc,item.data['element']))

    def select_matches(self, sender, args):
        self.pending_result = None
        def action(uidoc):
            ids = List[DB.ElementId]()
            views, skipped = set(), 0
            for result in self.matches:
                n = result['element']
                element = uidoc.Document.GetElement(n['source_element_id']) if n['source_document'] == document_id(uidoc.Document) else None
                if element:
                    ids.Add(element.Id)
                    if element.ViewSpecific:
                        views.add(element.OwnerViewId.IntegerValue)
                else:
                    skipped += 1
            if not ids.Count:
                raise ValueError(u'結果不屬於目前文件，或元素已被刪除。')
            if len(views) == 1:
                view = uidoc.Document.GetElement(DB.ElementId(next(iter(views))))
                if view and not view.IsTemplate:
                    uidoc.ActiveView = view
            uidoc.Selection.SetElementIds(ids)
            if len(views) <= 1:
                uidoc.ShowElements(ids)
            self.Status.Text = u'已選取清單中的 {} 個相似元素。'.format(ids.Count)
            if len(views) > 1:
                self.Status.Text += u' 結果分布於不同詳圖視圖；逐筆定位可切換來源視圖。'
            if skipped:
                self.Status.Text += u' {} 個跨文件或已刪除結果未選取。'.format(skipped)
        self.run(action)

    def use_match(self, sender, args):
        item = self.Matches.SelectedItem
        if item:
            self.set_target(item.data['element'])
            self.Status.Text = u'已更換搜尋基準。'

    def record_target(self, sender, args):
        def action(uidoc):
            self.resolve_target(uidoc)
            target = self.target
            def done(result):
                self.Status.Text = u'元素已記錄，可在跨專案搜尋中找到。'
                self.schedule_refresh('related', 'graph')
            self.network(lambda api:api('/elements',target),done)
        self.run(action)

    def refresh_elements(self, sender, args):
        def action(uidoc):
            identity = document_id(uidoc.Document)
            def done(rows):
                self.suppress_records = True
                self.Elements.Items.Clear()
                for n in rows:
                    self.Elements.Items.Add(Item(u'{} | ID {}'.format(n['name'],n['properties'].get('element_id','-')),n))
                self.suppress_records = False
                self.recorded_document = identity
                self.Status.Text = u'已載入 {} 個記錄。'.format(len(rows))
                if self.IncludeRelated.IsChecked:
                    self.schedule_refresh('graph')
            self.network(lambda api:api('/elements?document='+Uri.EscapeDataString(identity)),done)
        self.run(action)

    def recorded_changed(self, sender, args):
        if not self.suppress_records and self.Elements.SelectedItem:
            self.set_target(self.Elements.SelectedItem.data)

    def remove_click(self, sender, args):
        item = self.Elements.SelectedItem
        if item:
            record = item.data
            def action(uidoc):
                path = '/elements?document={}&unique_id={}'.format(Uri.EscapeDataString(record['source_document']),Uri.EscapeDataString(record['source_element_id']))
                def done(result):
                    self.Elements.Items.Remove(item)
                    self.Status.Text = u'記錄已移除。'
                    if self.IncludeRelated.IsChecked:
                        self.schedule_refresh('graph')
                self.network(lambda api:api(path,method='DELETE'),done)
            self.run(action)

    def search_click(self, sender, args):
        def action(uidoc):
            self.resolve_target(uidoc)
        if self.target:
            self.schedule_refresh('related')
        else:
            self.run(action,u'讀取搜尋基準…')

    def case_changed(self, sender, args):
        item = self.RelatedCases.SelectedItem
        if not item:
            self.CaseDetails.Text = ''
            return
        data = item.data
        self.CaseDetails.Text = u'{}\n\n{}\n\n{}\n{}'.format(data['description'],u'；'.join(data['evidence']),
            u'已驗證解法' if data['reliable_solution'] else u'未驗證：僅供比較，不代表有效解法',data['solution'] or data['resolution_note'] or u'尚未記錄解法。')
        self.queue_result(data['element'])

    def locate_case(self, sender, args):
        self.pending_result = None
        item = self.RelatedCases.SelectedItem
        if item:
            self.run(lambda uidoc:self.show_node(uidoc,item.data['element']))

    def open_related(self, sender, args):
        if self.RelatedCases.SelectedItem:
            self.open_case(self.RelatedCases.SelectedItem.data['case_id'])

    def sync_case(self, current):
        self.current = current
        self.issue_origin = current['draft'].get('origin')
        self.show_issue_origin()
        if not self.change_report_current():
            self.invalidate_changes(u'正在比對已記錄元件…' if self.active_feature == 'review' else u'開啟審查頁時自動比對。')
            if self.active_feature == 'review':
                self.queue_change_scan()
        self.set_case_issue_type(current['draft']['issue_type'])
        self.CaseState.Text = u'RFI-{} · {} · 第 {} 版'.format(current['id'],STATUS_NAMES.get(current['status'],current['status']),current['revision'])
        self.Analyze.IsEnabled = current['status'] in ('REPORTED','ANALYZED','UNDER_REVIEW')
        self.Submit.IsEnabled = current['status'] == 'DRAFT'
        self.SnapshotOnly.IsEnabled = self.case_matches_target()
        self.ReviewCaseState.Text = self.CaseState.Text
        self.ReviewCaseTitle.Text = current['draft']['title']
        self.ReviewDescription.Text = current['draft']['description']
        status = current['status']
        self.ReviewStart.IsEnabled = status in ('REPORTED', 'ANALYZED', 'RESOLVED')
        self.Resolve.IsEnabled = status in ('REPORTED', 'ANALYZED', 'UNDER_REVIEW')
        self.Verify.IsEnabled = status == 'RESOLVED'
        self.Archive.IsEnabled = status == 'VERIFIED'
        self.DeleteIssue.IsEnabled = self.DeleteReviewCase.IsEnabled = True
        self.ReviewHint.Text = (u'要修改解法，先按「開始審查」。' if status == 'RESOLVED'
                                else u'草稿須先在「問題紀錄」提交。' if status == 'DRAFT'
                                else u'已封存。' if status == 'ARCHIVED' else '')
        self.schedule_refresh('related', 'cases')

    def draft(self):
        return dict(title=self.IssueTitle.Text.strip(),description=self.Description.Text.strip(),
                    issue_type=self.IssueType.SelectedItem.data,priority=self.Priority.SelectedItem.data,notes=self.Notes.Text,graph=self.graph,
                    origin=self.issue_origin if self.origin_matches_target() else None)

    def new_click(self, sender, args):
        self.reset_issue_form()
        self.Status.Text = u'已開始新問題。填寫標題與描述後可儲存。'
        self.schedule_refresh('related', 'graph')

    def reset_issue_form(self):
        self.current = None
        self.invalidate_changes(u'開啟案例後自動比對已記錄元件。')
        self.issue_origin = None
        self.show_issue_origin()
        for control in (self.IssueTitle,self.Description,self.Notes,self.Solution,self.Reviewer,self.Results):
            control.Text = ''
        self.clear_graph(u'正在更新基準元素的關係圖…')
        self.SnapshotOnly.IsChecked = False
        self.SnapshotOnly.IsEnabled = False
        self.CaseState.Text = u'新問題 · 尚未儲存'
        self.ReviewCaseState.Text = ''
        self.ReviewCaseTitle.Text = u'尚未開啟案例'
        self.ReviewDescription.Text = ''
        self.ReviewHint.Text = u'從「案例庫」開啟案例後，可在此審查解法。'
        for control in (self.ReviewStart, self.Resolve, self.Verify, self.Archive, self.DeleteIssue, self.DeleteReviewCase):
            control.IsEnabled = False
        self.Analyze.IsEnabled,self.Submit.IsEnabled = False,True

    def save_report(self, submit=False):
        def action(uidoc):
            if not self.IssueTitle.Text.strip() or not self.Description.Text.strip():
                raise ValueError(u'請填寫問題標題與描述。')
            if self.current and self.current['status'] != 'DRAFT':
                raise ValueError(u'此問題已提交。請按「建立另一個問題」以新增紀錄。')
            element = self.resolve_target(uidoc)
            if self.current and not self.case_matches_target():
                raise ValueError(u'目前草稿屬於另一個基準元素。請返回原元素，或按「建立另一個問題」。')
            self.graph = self.extract_graph(uidoc, element)
            payload, target = self.draft(), self.target
            case_id, revision = (self.current['id'],self.current['revision']) if self.current else (None,None)
            def work(api):
                api('/elements',target)
                current = api('/cases/{}?revision={}'.format(case_id,revision),payload,'PUT') if case_id else api('/cases',payload)
                if submit:
                    current = api('/cases/{}/status'.format(current['id']),dict(status='REPORTED'))
                return current
            def done(current):
                self.sync_case(current)
                self.display_graph(current['draft']['graph'], u'已儲存快照 · RFI-{} 第 {} 版'.format(current['id'], current['revision']))
                self.Status.Text = u'問題已{}。'.format(u'提交' if submit else u'儲存為草稿')
                notes = self.graph.get('warnings') or []
                self.Results.Text = (u'擷取說明：\n' + u'\n'.join(notes)) if notes else u''
            self.network(work,done)
        self.run(action,u'擷取局部資訊並儲存問題…')

    def save_click(self, sender, args):
        self.save_report()

    def submit_click(self, sender, args):
        self.save_report(True)

    def analyze_click(self, sender, args):
        def action(uidoc):
            case_id = self.require_case()
            def work(api):
                return api('/cases/{}/analyze'.format(case_id),{}),api('/cases/{}'.format(case_id))
            def done(result):
                analysis,current = result
                self.sync_case(current)
                self.Results.Text = analysis['diagnosis']
                self.Status.Text = u'分析已完成，建議仍需人工審查。'
                self.schedule_refresh('graph')
            self.network(work,done)
        self.run(action,u'分析中；你可以繼續操作 Revit…')

    def change_status(self, status):
        def action(uidoc):
            case_id = self.require_case()
            payload = dict(status=status,solution=self.Solution.Text,reviewer=self.Reviewer.Text,
                           changes=self.change_report if self.change_report_current() else None)
            def done(current):
                self.sync_case(current)
                self.Status.Text = u'問題狀態已更新：'+STATUS_NAMES[status]
                self.schedule_refresh('graph')
            self.network(lambda api:api('/cases/{}/status'.format(case_id),payload),done)
        self.run(action)

    def refresh_suggest_click(self, sender, args):
        self.schedule_refresh('suggest')

    def refresh_cases(self, sender, args):
        self.schedule_refresh('cases')

    def open_case(self, case_id, destination='issue'):
        def action(uidoc):
            def done(current):
                self.sync_case(current)
                draft = current['draft']
                saved = draft['graph']
                self.set_target(next(n for n in saved['nodes'] if n['node_id']==saved['root']))
                self.IssueTitle.Text,self.Description.Text,self.Notes.Text = draft['title'],draft['description'],draft['notes']
                for box,key in [(self.IssueType,'issue_type'),(self.Priority,'priority')]:
                    box.SelectedItem = next(i for i in box.Items if i.data == draft[key])
                self.Solution.Text,self.Reviewer.Text = current['solution'],current['reviewer']
                self.Results.Text = json.loads(current['analyses'][-1]['result'])['diagnosis'] if current['analyses'] else ''
                self.display_graph(saved, u'已儲存快照 · RFI-{} 第 {} 版'.format(current['id'], current['revision']))
                self.open_feature(destination)
                if not self.IsVisible:
                    forms.open_dockable_panel(PANEL_ID)
                self.Status.Text = u'已開啟問題 RFI-{}。'.format(case_id)
            self.network(lambda api:api('/cases/{}'.format(case_id)),done)
        self.run(action)

    def open_click(self, sender, args):
        if self.Cases.SelectedItem:
            self.open_case(self.Cases.SelectedItem.data['id'])
