# -*- coding: utf-8 -*-
"""Real IronPython/WPF UI regression tests with fake Revit documents and HTTP.

Loads the production XAML, event bindings, renderer and refresh handlers. No real
model or database is modified. Revit's API dispatcher is advanced explicitly.
"""
import sys
import types
import copy
import traceback
import os
import tempfile
import clr
clr.AddReference('PresentationFramework')
clr.AddReference('PresentationCore')
clr.AddReference('WindowsBase')
import wpf
from System import EventArgs
from System.Windows import Size, Rect, RoutedEventArgs, Visibility
from System.Windows.Controls import Page, Button


def module(name, **values):
    value = types.ModuleType(name)
    value.__dict__.update(values)
    sys.modules[name] = value
    return value


class Object(object):
    def __init__(self, **values):
        self.__dict__.update(values)


class Signal(object):
    def __iadd__(self, handler):
        return self

    def __isub__(self, handler):
        return self


class WPFPanel(Page):
    def __init__(self):
        wpf.LoadComponent(self, self.panel_source)


class ExternalEvent(object):
    accepted = True

    @staticmethod
    def Create(handler):
        return ExternalEvent()

    def Raise(self):
        return 'Accepted' if self.accepted else 'Denied'


module('Autodesk')
db = module('Autodesk.Revit.DB', ElementId=int, ElementType=type('ElementType', (object,), {}),
            Events=Object(DocumentChangedEventArgs=EventArgs))
ui = module('Autodesk.Revit.UI', IExternalEventHandler=object, ExternalEvent=ExternalEvent,
            Events=Object(ViewActivatedEventArgs=EventArgs))
module('Autodesk.Revit', DB=db, UI=ui)
module('Autodesk.Revit.UI.Selection', ObjectType=Object(Element=1))
module('Autodesk.Revit.Exceptions', OperationCanceledException=type('OperationCanceledException', (Exception,), {}))
module('pyrevit', forms=Object(WPFPanel=WPFPanel),
       HOST_APP=Object(app=Object(DocumentChanged=Signal()), uiapp=Object(ViewActivated=Signal())))
module('pyrevit.framework', wpf=wpf)


def node(uid, name=None):
    return dict(node_id='model:'+uid, name=name or uid, node_type='ELEMENT',
                source_document='model', source_element_id=uid,
                properties=dict(element_id=uid, category='Doors', category_id='-2000014', family='Door', type_name='900 x 2100', type_id='100', dimensions={'Width':900}, units='mm'))


elements = dict((uid, Object(Id=101+i, UniqueId=uid, Category=Object(Id=-2000014), record=node(uid), ViewSpecific=False)) for i, uid in enumerate(['A', 'B']))
selected_ids, shown_ids = [], []
extractions, extracted_findings, extracted_related = [], [], []


def extract(doc, primary, issue_type, related=None, depth=1, limit=80, findings=None):
    extractions.append((primary.Id, issue_type, depth, limit))
    extracted_related.append([e.Id for e in related or []])
    extracted_findings.append([(e.Id, verified, properties['result_id']) for e, _, verified, properties in findings or []])
    record = copy.deepcopy(primary.record)
    return dict(root=record['node_id'], nodes=[record], edges=[], warnings=[])


module('casegraph.extractor', document_id=lambda doc: doc.identity, id_value=lambda value: str(value), node=lambda doc, e: e.record, extract=extract)
module('casegraph.element_index', snapshot=lambda doc, e: copy.deepcopy(e.record), detail_snapshot=lambda doc, e: copy.deepcopy(e.record),
       discover=lambda uidoc, e, scope: dict(target=copy.deepcopy(e.record), candidates=[copy.deepcopy(x.record) for x in elements.values() if x.Id != e.Id], skipped=0, truncated=False))
module('casegraph.client', request=None)

from casegraph.workspace_panel import WorkspacePanel

os.environ['CASEGRAPH_RULES_DIR'] = tempfile.mkdtemp(prefix='casegraph-ui-rules-', dir=os.path.join(os.path.dirname(__file__), '..', 'data'))


class TestPanel(WorkspacePanel):
    # Layout/event binding is real WPF; visibility is controlled without a window.
    IsVisible = True


uidoc = Object(Document=Object(identity='model', GetElement=lambda uid: elements.get(uid)),
               Selection=Object(GetElementIds=lambda: ['A'], SetElementIds=lambda ids:selected_ids.append(list(ids))),
               ShowElements=lambda ids:shown_ids.append(list(ids)))
app = Object(ActiveUIDocument=uidoc)


def new_panel():
    del selected_ids[:]
    del shown_ids[:]
    panel = TestPanel()
    panel.open_feature('similar')
    panel.refresh_timer.Stop()
    panel.Measure(Size(520, 1200))
    panel.Arrange(Rect(0, 0, 520, 1200))
    panel.UpdateLayout()
    panel.refresh_queue.take()
    panel.calls = []
    panel.responses = {'/elements/search': dict(results=[], message=u'沒有其他同類元素', truncated=False)}
    panel.waiting = []
    panel.log_error = lambda error: None

    def network(work, done, failed=None):
        panel.network_pending = True
        panel.waiting.append((work, done, failed))
    panel.network = network
    return panel


def start(panel):
    panel.refresh_tick(None, None)
    if panel.handler.action:
        panel.handler.Execute(app)


def complete(panel, failure=None):
    work, done, failed = panel.waiting.pop(0)

    def api(path, payload=None, method=None):
        panel.calls.append((path, payload, method))
        value = panel.responses[path]
        return value(payload) if callable(value) else copy.deepcopy(value)
    if failure:
        failed(failure)
    else:
        done(work(api))
    panel.finish()
    panel.refresh_timer.Stop()


def related(uid, status='DRAFT'):
    return dict(message=u'找到案例', results=[dict(case_id=1, score=1., status=status, title=uid,
        description=uid, issue_type='GEOMETRY_CLASH', evidence=[], reliable_solution=status=='VERIFIED', solution='', resolution_note='', element=node(uid))])


def current(uid='A', status='DRAFT'):
    root = node(uid)
    issue = dict(node_id='issue:1', node_type='ISSUE', name='Issue', properties=dict(status='DRAFT'), source_document='model', source_element_id='')
    edge = dict(source=root['node_id'], target=issue['node_id'], relationship_type='HAS_ISSUE', evidence='Reported', properties={})
    graph = dict(root=root['node_id'], nodes=[root, issue], edges=[edge], warnings=[])
    return dict(id=1, revision=1, status=status, solution='', reviewer='', analyses=[],
                draft=dict(title='Issue', description='Description', notes='', priority='Medium', issue_type='GEOMETRY_CLASH', graph=graph))


def matches(record):
    return dict(message=u'找到 1 個同類元素，顯示 1 個。', truncated=False, results=[dict(
        element=copy.deepcopy(record), score=.94, completeness=.9, evidence=[u'類別與 Revit 類型相同'], differences=[], source='model')])


def preview(panel, name, width=900, height=1180):
    folder = os.environ.get('CASEGRAPH_PREVIEW_DIR')
    if not folder:
        return
    from System.Windows.Media import PixelFormats
    from System.Windows.Media.Imaging import RenderTargetBitmap, BitmapFrame, PngBitmapEncoder
    from System.IO import File, Directory
    Directory.CreateDirectory(folder)
    panel.Measure(Size(width, height))
    panel.Arrange(Rect(0, 0, width, height))
    panel.UpdateLayout()
    panel.fit_graph()
    panel.UpdateLayout()
    # WPF star-column sizing is deferred until the dispatcher processes layout.
    from System import Action
    from System.Windows.Threading import DispatcherPriority
    panel.refresh_timer.Stop()
    panel.Dispatcher.Invoke(Action(lambda: None), DispatcherPriority.Background)
    panel.UpdateLayout()
    bitmap = RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32)
    bitmap.Render(panel)
    encoder = PngBitmapEncoder()
    encoder.Frames.Add(BitmapFrame.Create(bitmap))
    stream = File.Create(os.path.join(folder, name+'.png'))
    try:
        encoder.Save(stream)
    finally:
        stream.Close()


def test_graph_matches_inspector_comparison_and_snapshot():
    p = new_panel()
    original_b = copy.deepcopy(elements['B'].record)
    elements['B'].record['properties'].update(dimensions={'Width':1200}, level='3FL', ifc_class='IfcDoor', parameters={'instance:Area':dict(value=u'2.52 m²', unit='ForgeTypeId')})
    p.responses.update({'/elements/search': matches(elements['B'].record), '/cases/related':related('A')})
    p.set_target(node('A'))
    p.Tabs.SelectedItem = p.GraphTab
    start(p)
    complete(p)
    assert any(e['relationship_type']=='SAME_TYPE' for e in p.graph['edges'])
    assert p.context_graph['edges'] == []
    assert p.GraphCanvas.Children.Count > 2
    button = next(c for c in p.GraphCanvas.Children if isinstance(c, Button) and c.Tag['node_id']=='model:B')
    button.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    start(p)
    assert selected_ids == [[102]] and shown_ids == [[102]]
    rows = dict((r.label, r.value) for r in p.NodeProperties.Items)
    assert rows[u'IFC 類別']=='IfcDoor' and rows[u'寬度']=='1200 mm'
    assert rows[u'實例 · Area']==u'2.52 m²'
    assert all('ForgeTypeId' not in r.value for r in p.NodeProperties.Items)
    assert p.CompareNode.IsEnabled
    assert any(u'相同類型'==r.label for r in p.NodeRelations.Items)
    preview(p, 'graph-inspector-preview')
    p.CompareNode.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    p.handler.Execute(app)
    assert p.Tabs.SelectedItem == p.CompareTab
    rows = dict((r.label,r) for r in p.ComparisonRows.Items)
    assert rows[u'寬度'].left=='900 mm' and rows[u'寬度'].right=='1200 mm'
    assert rows[u'寬度'].status==u'不同'
    assert rows[u'IFC 類別'].status==u'資料不足'
    preview(p, 'element-comparison-preview')
    p.DifferencesOnly.IsChecked = True
    assert all(r.status in (u'不同',u'資料不足') for r in p.ComparisonRows.Items)
    p.swap_comparison(None,None)
    assert p.comparison_pair[0]['source_element_id']=='B'
    p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
    assert u'來源模型已修改' in p.ComparisonNotice.Text
    # Saving a report must exclude the UI-only comparison links.
    p.IssueTitle.Text, p.Description.Text = 'Issue', 'Description'
    p.responses.update({'/elements':node('A'), '/cases':current()})
    p.save_click(None,None)
    p.handler.Execute(app)
    complete(p)
    payload = next(c[1] for c in p.calls if c[0]=='/cases')
    assert all(e['relationship_type'] not in ('SAME_TYPE','SIMILAR_TO') for e in payload['graph']['edges'])
    p.refresh_queue.take()
    p.SnapshotOnly.IsChecked = True
    start(p)
    assert not any(e['relationship_type'] in ('SAME_TYPE','SIMILAR_TO') for e in p.graph['edges'])
    elements['B'].record = original_b


def test_geometry_rule_editor_batch_check_graph_selection_and_invalidation():
    import casegraph.geometry_view as view_module
    from casegraph.geometry_check import MM3_PER_FT3
    from System.Windows.Media import Brushes
    p = new_panel()
    # Rules use the isolated test directory, never the installed rule files.
    p.set_target(node('A'))
    p.open_feature('graph')
    p.rules_click(None, None)
    p.RuleTolerance.Text = '0'
    p.RuleExcludeHost.IsChecked = False
    p.RuleSourceScope.SelectedIndex = 0
    p.SaveGeometryRule.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    p.handler.Execute(app)
    p.refresh_timer.Stop()
    p.refresh_queue.take()
    assert len(p.rule_store.load('model')) == 1, repr(p.Status.Text)
    assert p.GeometryRules.SelectedItem.data['target_category'] == 'OST_Walls'
    assert p.RunGeometryRules.IsEnabled and not p.RuleEditor.IsExpanded
    # Exercise replacement twice (update, then remove below), including the
    # existing backup path, under the installed IronPython runtime.
    p.RuleTolerance.Text = '1'
    p.SaveGeometryRule.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    p.handler.Execute(app)
    p.refresh_timer.Stop()
    assert len(p.rule_store.load('model')) == 1, repr(p.Status.Text)
    assert p.rule_store.load('model')[0]['tolerance_mm3'] == 1, repr(p.Status.Text)
    p.RuleTolerance.Text = '9'
    p.display_rules()
    assert float(p.RuleTolerance.Text) == 1  # Reload the selected saved settings.
    saved = current()
    p.sync_case(saved)
    p.refresh_queue.take()
    original = copy.deepcopy(saved)
    p.display_graph(saved['draft']['graph'], u'目前模型預覽', live=True)

    class Solid(object):
        def __init__(self, cells):
            self.cells = set(cells)
            self.Volume = len(cells)/MM3_PER_FT3
        def Dispose(self):
            pass

    class Adapter(object):
        view_id, view_name = '900', u'2FL 模型協調視圖'
        def sources(self, rule):
            return ['A']
        def candidates(self, source, rule):
            return ['B']
        def key(self, element):
            return element
        def record(self, element):
            record = node(element)
            record['name'] = u'檢查元件' if element == 'A' else u'RC 牆 20cm'
            if element == 'B':
                record['properties'].update(category='Walls', category_id='-2000011')
            return record
        def geometry(self, element):
            return dict(solids=[Solid([1, 2])], problems=[])
        def boolean(self, a, b, operation):
            return Solid(a.cells & b.cells if operation == 'Intersect' else a.cells | b.cells)
        def is_host(self, a, b):
            return False
        def joined(self, a, b):
            return False
        def close(self):
            pass

    previous_adapter = view_module.RevitGeometryAdapter
    uidoc.ActiveView = Object(Id=900, Name='Test view')
    view_module.RevitGeometryAdapter = lambda doc, view: Adapter()
    try:
        p.RelationFilter.SelectedIndex = p.RelationFilter.Items.Count-1
        p.RunGeometryRules.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        start(p)
        assert p.rule_report['status'] == 'VIOLATION' and p.RuleResults.Items.Count == 1
        assert p.GraphInspector.SelectedItem == p.RulesTab
        assert p.RelationFilter.SelectedIndex == 0
        assert p.current == original and p.context_graph == saved['draft']['graph']
        assert any(e['relationship_type']=='RULE_VIOLATION' for e in p.graph['edges'])
        assert any(isinstance(c, Button) and c.Tag['node_type']=='ELEMENT' and c.Background==Brushes.MistyRose for c in p.GraphCanvas.Children)
        p.RuleResults.SelectedIndex = 0
        assert p._selected_occurrence.startswith('violation-source:')
        p.LocateRulePair.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        p.handler.Execute(app)
        assert selected_ids[-1] == [101, 102] and shown_ids[-1] == [101, 102]
        preview(p, 'geometry-rules-preview')
        p.AutoRuleCheck.IsChecked = True
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        assert p.rule_report is None and p.rule_pending
        assert not p.LocateRulePair.IsEnabled and p.RuleResults.Items.Count == 0
        assert not any(e['relationship_type']=='RULE_VIOLATION' for e in p.graph['edges'])
        start(p)
        assert p.rule_report['violations'] == 1
        p.AutoRuleCheck.IsChecked = False
        p.view_activated(None, Object(CurrentActiveView=Object(Document=uidoc.Document)))
        assert p.rule_report is None and not p.rule_pending
        assert u'視圖已變更' in p.RuleSummary.Text
        p.check_geometry_click(None, None)
        start(p)
        assert p.rule_report is not None
        identity = uidoc.Document.identity
        uidoc.Document.identity = 'other'
        try:
            p.check_geometry_click(None, None)
            start(p)
            assert p.rule_report is None and p.RuleResults.Items.Count == 0
            assert u'檢查未完成' in p.RuleSummary.Text
        finally:
            uidoc.Document.identity = identity
        p.RemoveGeometryRule.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        assert p.rule_store.load('model') == [] and not p.RunGeometryRules.IsEnabled, repr(p.Status.Text)
    finally:
        view_module.RevitGeometryAdapter = previous_adapter
        p.refresh_timer.Stop()


def test_model_checks_page_engine_filters_navigation_graph_and_auto_refresh():
    import casegraph.check_view as view_module
    from casegraph.geometry_check import MM3_PER_FT3
    p = new_panel()
    p.set_target(node('A'))
    p.open_feature('checks')
    p.set_check_scope('type')  # These scenarios cover the type-based source scope.
    p.IssueTitle.Text = u'保留問題草稿'
    p.refresh_queue.take()
    saved = current()
    p.sync_case(saved)
    p.refresh_queue.take()
    original = copy.deepcopy(saved)

    class Solid(object):
        def __init__(self, cells):
            self.cells = set(cells)
            self.Volume = len(cells)/MM3_PER_FT3
        def Dispose(self):
            pass

    class Adapter(object):
        view_id, view_name = '900', u'2FL 模型檢查'
        def sources(self, scope):
            assert scope['scope'] == 'type' and scope['source_key'] == '100'
            return ['A', 'B']
        def key(self, element):
            return element
        def record(self, element):
            return node(element, u'測試門 '+element)
        def metadata(self, element):
            return dict(type='100', category='Doors', bounds=(0,0,0,900,100,2100),
                        placement=('point',(0,0,0),(0,1)))
        def mark(self, element):
            return 'Doors', u'D-01'
        def warnings(self):
            return [dict(keys=['A','B'], message=u'測試用 Revit 警告')]
        def clash_candidates(self, source, bounds, target):
            return []
        def is_clash_room(self, element):
            return False
        def is_clash_curve(self, element):
            return False
        def geometry(self, element):
            return dict(solids=[Solid([1,2])], problems=[])
        def boolean(self, a, b, operation):
            return Solid(a.cells & b.cells if operation == 'Intersect' else a.cells | b.cells)
        def joined(self, a, b):
            return False
        def close(self):
            pass

    previous = view_module.RevitCheckAdapter
    view_module.RevitCheckAdapter = lambda doc, view: Adapter()
    uidoc.ActiveView = Object(Id=900, Name='QA view')
    try:
        p.RunModelChecks.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        p.event.accepted = False
        start(p)
        assert p.check_pending and p.check_report is None
        p.event.accepted = True
        start(p)
        assert p.check_report['counts'] == dict(duplicate=1, overlap=1, clash=0, mark=1, warning=1), repr(p.Status.Text)
        assert p.CheckResults.Items.Count == 4 and p.active_feature == 'checks'
        assert p.current == original and not selected_ids
        p.CheckFilter.SelectedIndex = 1
        assert p.CheckResults.Items.Count == 1 and p.CheckResults.Items[0].data['check'] == 'duplicate'
        p.AutoHighlight.IsChecked = False
        p.CheckResults.SelectedIndex = 0
        assert p.pending_check_result is None
        p.LocateCheck.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        start(p)
        assert selected_ids[-1] == [102] and shown_ids[-1] == [102]
        assert u'建議處理' in p.CheckDetails.Text
        preview(p, 'model-checks-preview')
        p.ShowCheckGraph.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        assert p.active_feature == 'graph' and p._selected_occurrence.startswith('check-element:')
        assert any(e['relationship_type']=='MODEL_CHECK' for e in p.graph['edges'])
        assert p.current == original and not any(e['relationship_type']=='MODEL_CHECK' for e in p.context_graph['edges'])
        preview(p, 'model-checks-graph-preview')
        p.open_feature('checks')
        p.AutoHighlight.IsChecked = True
        p.CheckResults.SelectedIndex = -1
        p.CheckResults.SelectedIndex = 0
        start(p)
        assert selected_ids[-1] == [102] and shown_ids[-1] == [102]
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        assert p.check_report is None and p.check_pending and p.CheckResults.Items.Count == 0
        assert not any(e['relationship_type']=='MODEL_CHECK' for e in p.graph['edges'])
        start(p)
        assert p.check_report['findings'] == 4
        p.AutoChecks.IsChecked = False
        p.view_activated(None, Object(CurrentActiveView=Object(Document=uidoc.Document)))
        assert p.check_report is None and not p.check_pending and not p.LocateCheck.IsEnabled
        p.run_checks_click(None, None)
        start(p)
        p.CheckResults.SelectedIndex = 0
        stale_ticket = p.pending_check_result
        p.CheckPositionTolerance.Text = '2'
        assert p.check_report is None and p.pending_check_result is None
        count = len(selected_ids)
        p.locate_check(uidoc, stale_ticket)
        assert len(selected_ids) == count
        p.run_checks_click(None, None)
        start(p)
        p.CheckResults.SelectedIndex = 0
        ticket = p.pending_check_result
        identity = uidoc.Document.identity
        uidoc.Document.identity = 'foreign'
        try:
            p.locate_check(uidoc, ticket)
            raise AssertionError('Foreign document navigation accepted')
        except ValueError:
            assert p.check_report is None and selected_ids[-1] == []
        finally:
            uidoc.Document.identity = identity
        p.run_checks_click(None, None)
        start(p)
        p.CheckResults.SelectedIndex = 0
        deleted = elements.pop('B')
        try:
            p.locate_check(uidoc, p.pending_check_result)
            assert p.check_report is None and selected_ids[-1] == []
        finally:
            elements['B'] = deleted
        assert p.IssueTitle.Text == u'保留問題草稿'
    finally:
        view_module.RevitCheckAdapter = previous
        p.refresh_timer.Stop()


def test_cross_clash_target_ui_locates_second_element_and_clears_stale_results():
    import casegraph.check_view as view_module
    from casegraph.geometry_check import MM3_PER_FT3
    p = new_panel()
    p.set_target(node('A'))
    p.open_feature('checks')
    p.set_check_scope('type')  # These scenarios cover the type-based source scope.
    p.refresh_queue.take()
    p.CheckDuplicates.IsChecked = p.CheckOverlaps.IsChecked = False
    p.CheckMarks.IsChecked = p.CheckWarnings.IsChecked = False
    assert p.CheckClashes.IsChecked and p.ClashTarget.SelectedItem.data == 'ALL'
    assert p.ClashSkipJoined.IsChecked and p.ClashSkipCollinear.IsChecked
    assert p.ClashLineTolerance.Text == '1'
    state = dict(joined=False, line=False, curve=False, geometry_reads=0, allocated=0)

    class Solid(object):
        def __init__(self, cells):
            self.cells = set(cells)
            self.Volume = len(cells)/MM3_PER_FT3
        def Dispose(self):
            pass

    class Adapter(object):
        view_id, view_name = '900', u'2FL 不同元件碰撞檢查'
        def sources(self, scope):
            assert scope['scope'] in ('type', 'all')
            return ['A', 'B'] if scope['scope'] == 'all' else ['A']
        def key(self, element):
            return element
        def record(self, element):
            record = node(element, u'門 D-01' if element == 'A' else u'RC 牆 20cm')
            if element == 'B':
                record['properties'].update(category='Walls', category_id='-2000011', type_id='200')
            return record
        def metadata(self, element):
            assert not (state['curve'] and element == 'B'), 'Excluded curve metadata was read'
            assert not (state.get('room') and element == 'B'), 'Excluded Room metadata was read'
            placement = None
            if state['line']:
                points = (0,0,0,450,0,0,900,0,0) if element == 'A' else (900,0,.75,450,0,.75,0,0,.75)
                placement = ('curve:Line', points, ())
            return dict(category='Doors' if element == 'A' else 'Walls', type='100' if element == 'A' else '200',
                        bounds=(0,0,0,900,200,2100), placement=placement)
        def geometry(self, element):
            assert not (state['curve'] and element == 'B'), 'Excluded curve geometry was read'
            assert not (state.get('room') and element == 'B'), 'Excluded Room geometry was read'
            state['geometry_reads'] += 1
            if element == 'B' and state.get('geometry_error'):
                raise ValueError('Solid extraction failed')
            if element == 'B' and state.get('no_solid'):
                return dict(solids=[], problems=[])
            return dict(solids=[Solid([1,2])], problems=[])
        def clash_candidates(self, source, bounds, target):
            return ['B'] if target in ('ALL', 'OST_Walls') else []
        def is_clash_curve(self, element):
            return state['curve'] and element == 'B'
        def is_clash_room(self, element):
            return state.get('room', False) and element == 'B'
        def boolean(self, a, b, operation):
            return Solid(a.cells & b.cells if operation == 'Intersect' else a.cells | b.cells)
        def joined(self, a, b):
            return state['joined']
        def close(self):
            pass

    previous = view_module.RevitCheckAdapter
    def make_adapter(doc, view):
        state['allocated'] += 1
        return Adapter()
    view_module.RevitCheckAdapter = make_adapter
    uidoc.ActiveView = Object(Id=900, Name='Clash view')
    try:
        p.ClashTarget.SelectedItem = next(i for i in p.ClashTarget.Items if i.data == 'OST_Walls')
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report['counts'] == dict(clash=1), repr(p.Status.Text)
        assert p.check_report['sources'] == 1 and p.check_report['pairs'] == 1
        assert p.check_report['clash_target'] == 'OST_Walls'
        assert u'Walls' in p.CheckResults.Items[0].label and u'Doors' in p.CheckResults.Items[0].label
        p.CheckFilter.SelectedItem = next(i for i in p.CheckFilter.Items if i.data == 'clash')
        p.CheckResults.SelectedIndex = 0
        start(p)
        assert selected_ids[-1] == [102] and shown_ids[-1] == [102]
        assert u'RC 牆 20cm' in p.CheckDetails.Text and u'門 D-01' in p.CheckDetails.Text
        preview(p, 'cross-clash-preview')
        p.ShowCheckGraph.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        assert p._selected_occurrence.startswith('check-element:')
        assert any(e.get('properties',{}).get('check') == 'clash' for e in p.graph['edges'])
        preview(p, 'cross-clash-graph-preview')
        p.open_feature('checks')
        state['curve'] = True
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        start(p)
        assert p.check_report['excluded_curve_elements'] == 1 and p.check_report['unknown'] == 0
        assert p.check_report['pairs'] == 0 and p.CheckResults.Items.Count == 0
        assert u'已排除 1 個線元素' in p.CheckNotes.Text
        assert not any(e.get('properties',{}).get('check') == 'clash' for e in p.graph['edges'])
        assert p.pending_check_result is None and not p.ShowCheckGraph.IsEnabled
        preview(p, 'curve-exclusion-preview')
        state['curve'] = False
        state.update(room=True, geometry_reads=0)
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        start(p)
        assert p.CheckResults.Items.Count == 0 and p.check_report['excluded_room_elements'] == 1
        assert p.check_report['pairs'] == p.check_report['unknown'] == 0
        assert u'Room（房間）' in p.CheckNotes.Text
        assert not any(e.get('properties',{}).get('check') == 'clash' for e in p.graph['edges'])
        state['room'] = False
        state['no_solid'] = True
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        start(p)
        assert p.CheckResults.Items.Count == 0 and p.check_report['excluded_nonsolid_elements'] == 1
        assert p.check_report['unknown'] == 0 and u'無實體幾何' in p.CheckNotes.Text
        assert not any(e.get('properties',{}).get('check') == 'clash' for e in p.graph['edges'])
        preview(p, 'solid-only-clash-preview')
        state.update(no_solid=False, geometry_error=True)
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        start(p)
        assert p.check_report['geometry_failures'] == 1 and p.check_report['status'] == 'INCOMPLETE'
        assert p.CheckResults.Items.Count == 0 and u'實體擷取未完成' in p.CheckNotes.Text
        state['geometry_error'] = False
        state.update(joined=True, geometry_reads=0)
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        start(p)
        assert p.check_report['skipped_joined'] == 1 and p.check_report['findings'] == 0
        assert p.CheckResults.Items.Count == 0 and state['geometry_reads'] == 0
        assert u'已接合 1 組' in p.CheckNotes.Text and u'未作碰撞判定' in p.CheckNotes.Text
        assert not any(e.get('properties',{}).get('check') == 'clash' for e in p.graph['edges'])
        p.ClashSkipJoined.IsChecked = False
        assert p.check_report is None
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report['counts']['clash'] == 1 and not p.check_report['clash_skip_joined']
        p.CheckResults.SelectedIndex = 0
        assert p.pending_check_result is not None
        p.ClashSkipJoined.IsChecked = True
        assert p.check_report is None and p.pending_check_result is None
        state.update(joined=False, line=True)
        p.ClashLineTolerance.Text = '.5'
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report['counts']['clash'] == 1 and p.check_report['clash_line_mm'] == .5
        p.ClashLineTolerance.Text = '1'
        assert p.check_report is None and p.CheckResults.Items.Count == 0
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report['skipped_collinear'] == 1 and p.check_report['status'] == 'CLEAR'
        assert u'同一直線 1 組' in p.CheckNotes.Text
        preview(p, 'clash-exclusions-preview')
        p.ClashSkipCollinear.IsChecked = False
        assert not p.ClashLineTolerance.IsEnabled
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report['counts']['clash'] == 1 and p.check_report['skipped_collinear'] == 0
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        start(p)
        assert not p.check_report['clash_skip_collinear'] and p.check_report['counts']['clash'] == 1
        p.ClashSkipCollinear.IsChecked = True
        p.ClashLineTolerance.Text = 'NaN'
        allocations = state['allocated']
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report is None and state['allocated'] == allocations
        assert u'有限數值' in p.CheckSummary.Text
        p.ClashLineTolerance.Text = '1'
        state.update(joined=False, line=False)
        p.use_selected_check_range(None, None)
        p.handler.Execute(app)
        assert p.CheckRangeMode.SelectedItem.data == 'selected' and p.CheckScope.SelectedItem.data == 'all'
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report['range_count'] == 1 and p.check_report['findings'] == 0
        uidoc.Selection.PickElementsByRectangle = lambda prompt: [elements['A'], elements['B']]
        p.pick_check_range(None, None)
        p.handler.Execute(app)
        del uidoc.Selection.PickElementsByRectangle
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report['counts']['clash'] == 1 and p.check_report['range_count'] == 2
        assert u'兩邊均限範圍內' in p.CheckNotes.Text
        preview(p, 'custom-range-preview')
        saved_selection = p.check_selection
        try:
            p.set_check_selection(uidoc, [])
            raise AssertionError('Empty range accepted')
        except ValueError:
            pass
        assert p.check_selection is saved_selection
        from Autodesk.Revit.Exceptions import OperationCanceledException
        def cancel_pick(prompt):
            raise OperationCanceledException()
        uidoc.Selection.PickElementsByRectangle = cancel_pick
        p.pick_check_range(None, None)
        p.handler.Execute(app)
        del uidoc.Selection.PickElementsByRectangle
        assert p.check_selection is saved_selection and u'已取消' in p.Status.Text
        uidoc.ActiveView = Object(Id=901, Name='Other view')
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report is None and u'重新框選' in p.CheckSummary.Text
        uidoc.ActiveView = Object(Id=900, Name='Clash view')
        p.run_checks_click(None, None)
        start(p)
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        start(p)
        assert p.check_report['range_count'] == 2 and p.check_report['counts']['clash'] == 1
        removed = elements.pop('B')
        try:
            p.run_checks_click(None, None)
            start(p)
            assert p.check_report is None and u'已刪除' in p.CheckSummary.Text
        finally:
            elements['B'] = removed
        p.CheckRangeMode.SelectedIndex = 0
        p.set_check_scope('type')
        p.ClashTarget.SelectedItem = next(i for i in p.ClashTarget.Items if i.data == 'OST_Floors')
        assert p.check_report is None and p.CheckResults.Items.Count == 0
        assert not p.LocateCheck.IsEnabled and p.pending_check_result is None
        p.run_checks_click(None, None)
        start(p)
        assert p.check_report['status'] == 'CLEAR' and p.check_report['clash_target'] == 'OST_Floors'
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        assert p.check_pending
        start(p)
        assert p.check_report['clash_target'] == 'OST_Floors' and p.check_report['findings'] == 0
        p.CheckClashes.IsChecked = False
        assert p.check_report is None and not p.ClashTarget.IsEnabled
        assert not p.ClashSkipJoined.IsEnabled and not p.ClashSkipCollinear.IsEnabled and not p.ClashLineTolerance.IsEnabled
    finally:
        view_module.RevitCheckAdapter = previous
        p.refresh_timer.Stop()


def test_two_selected_elements_and_invalid_comparison_preserve_previous_pair():
    p = new_panel()
    original_selection = uidoc.Selection.GetElementIds
    uidoc.Selection.GetElementIds = lambda:['A','B']
    p.pick_compare(None,None)
    p.handler.Execute(app)
    assert [r['source_element_id'] for r in p.comparison_pair] == ['A','B']
    pair = p.comparison_pair
    wrong = node('wrong')
    wrong['properties']['category_id'] = '-2000011'
    try:
        p.compare_pair(node('A'),wrong)
        raise AssertionError('Wrong-category pair was accepted')
    except ValueError:
        pass
    assert p.comparison_pair == pair
    old_description = p.Description.Text
    p.refresh_comparison(None,None)
    p.handler.Execute(app)
    assert p.Description.Text == old_description
    uidoc.Selection.GetElementIds = original_selection


def test_obsolete_similar_results_do_not_reappear_in_graph():
    p = new_panel()
    p.responses.update({'/elements/search':matches(node('B')), '/cases/related':related('A')})
    p.set_target(node('A'))
    start(p)
    p.set_target(node('B'))
    complete(p)
    assert p.graph is None and p.Matches.Items.Count==0
    p.responses.update({'/elements/search':matches(node('A')), '/cases/related':related('B')})
    start(p)
    complete(p)
    assert p.graph['root']=='model:B'
    peers = [e['target'] for e in p.graph['edges'] if e['relationship_type']=='SAME_TYPE']
    assert peers==['model:A']


def test_hierarchy_groups_collapse_without_selecting_revit_objects():
    p = new_panel()
    p.set_target(node('A'))
    p.responses.update({'/elements/search':matches(node('B')), '/cases/related':related('A')})
    start(p)
    complete(p)
    assert selected_ids == [] and p.pending_result is None
    group = next(c for c in p.GraphCanvas.Children if isinstance(c,Button) and c.Tag['node_type']=='GROUP' and c.Tag['properties']['hierarchy_level']==u'類型')
    key = group.CommandParameter
    group.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    assert key in p.collapsed_groups and p.pending_result is None
    assert not p.GraphNavigate.IsEnabled and not p.CompareNode.IsEnabled
    assert not any(isinstance(c,Button) and c.Tag['node_id']=='model:B' for c in p.GraphCanvas.Children)
    assert selected_ids == []
    # Selecting an instance reveals its path without changing saved graph data.
    p.queue_result(node('B'))
    start(p)
    assert key not in p.collapsed_groups and selected_ids == [[102]]
    assert p.context_graph['edges'] == []
    assert any(isinstance(c,Button) and c.Tag['node_id']=='model:B' for c in p.GraphCanvas.Children)
    p.Tabs.SelectedItem = p.GraphTab
    preview(p, 'hierarchical-graph-preview')


def test_first_result_click_highlights_but_background_repopulation_does_not():
    p = new_panel()
    p.set_target(node('A'))
    p.refresh_queue.take()
    p.display_matches(matches(node('B')))
    assert p.Matches.SelectedIndex==0 and p.pending_result is None
    p.UpdateLayout()
    container = p.Matches.ItemContainerGenerator.ContainerFromIndex(0)
    assert container is not None
    p.result_clicked(p.Matches,Object(ClickCount=1,OriginalSource=container))
    start(p)
    assert selected_ids == [[102]] and shown_ids == [[102]]
    p.display_matches(matches(node('B')))
    p.display_related(related('A'))
    assert p.pending_result is None and selected_ids==[[102]]
    # Keyboard-driven selection changes use the same path.
    p.Matches.SelectedIndex = -1
    p.Matches.SelectedIndex = 0
    start(p)
    assert selected_ids == [[102],[102]]


def test_focused_features_keep_drafts_and_comparison_while_switching():
    p = new_panel()
    p.set_target(node('A'))
    p.IssueTitle.Text = u'尚未儲存的問題'
    p.Description.Text = u'保留輸入內容'
    p.Solution.Text = u'尚未儲存的解法'
    p.show_comparison(node('A'), node('B'))
    pair = p.comparison_pair
    workflows = dict(checks=p.NavChecks, elements=p.NavElements, cases=p.NavCases)
    for feature, workflow, sub in [('issue', 'cases', p.SubIssue), ('review', 'cases', p.SubReview),
                                   ('library', 'cases', p.SubLibrary), ('related', 'cases', p.SubRelated),
                                   ('graph', 'elements', p.SubGraph), ('compare', 'elements', p.SubCompare),
                                   ('similar', 'elements', p.SubSimilar), ('suggest', 'cases', p.SubSuggest),
                                   ('checks', 'checks', None)]:
        button = Button(Tag=feature)
        p.feature_click(button, None)
        assert p.active_feature == feature
        assert all(nav.IsChecked == (key == workflow) for key, nav in workflows.items())
        assert sub is None or sub.IsChecked
        assert p.ElementNav.Visibility == (Visibility.Visible if workflow == 'elements' else Visibility.Collapsed)
        assert p.CaseNav.Visibility == (Visibility.Visible if workflow == 'cases' else Visibility.Collapsed)
        assert p.TargetCard.Visibility == (Visibility.Visible if feature in ('issue','related','graph','similar','checks','suggest') else Visibility.Collapsed)
        assert p.CaseScopeCard.Visibility == (Visibility.Visible if feature in ('related','graph') else Visibility.Collapsed)
        assert p.AutoHighlight.Visibility == (Visibility.Visible if feature in ('related','graph','similar','checks','library') else Visibility.Collapsed)
        assert p.IssueTitle.Text == u'尚未儲存的問題' and p.Solution.Text == u'尚未儲存的解法'
        assert p.target['node_id'] == 'model:A' and p.comparison_pair == pair
    # A workflow tab reopens its last page; the ribbon's 'home' keeps the current page.
    p.open_feature('related')
    p.feature_click(Button(Tag='elements'), None)
    assert p.active_feature == 'similar'
    p.feature_click(Button(Tag='cases'), None)
    assert p.active_feature == 'related'
    p.open_feature('home')
    assert p.active_feature == 'related'
    p.open_feature('checks')
    preview(p, 'feature-overview-preview')
    p.open_feature('issue')
    preview(p, 'focused-issue-preview')
    p.refresh_timer.Stop()


def test_case_lists_remain_reachable_in_short_and_narrow_panels():
    from System.Windows import Window, WindowStyle
    p = new_panel()
    # A native host is needed for ListBox virtualization/BringIntoView. Keep it
    # offscreen and inactive so this regression cannot cover the user's model.
    host = Window()
    host.WindowStyle = getattr(WindowStyle, 'None')
    host.ShowInTaskbar = host.ShowActivated = False
    host.Left = host.Top = -10000
    host.Content = p
    try:
        host.Show()
        exercise_case_list_layout(p, host)
    finally:
        host.Close()
        p.refresh_timer.Stop()


def exercise_case_list_layout(p, host):
    from System import Action
    from System.Windows import Point
    from System.Windows.Media import VisualTreeHelper
    from System.Windows.Threading import DispatcherPriority

    def layout(p, width, height):
        p.refresh_timer.Stop()
        host.Width, host.Height = width, height
        host.UpdateLayout()
        p.Measure(Size(width, height))
        p.Arrange(Rect(0, 0, width, height))
        p.UpdateLayout()
        p.Dispatcher.Invoke(Action(lambda: None), DispatcherPriority.Background)
        p.UpdateLayout()

    def bounds(control, ancestor):
        return control.TransformToAncestor(ancestor).TransformBounds(Rect(control.RenderSize))

    def reveal_row(p, results, details, index, width, height):
        layout(p, width, height)
        results.ScrollIntoView(results.Items[index])
        layout(p, width, height)
        container = results.ItemContainerGenerator.ContainerFromIndex(index)
        assert container is not None
        container.BringIntoView()
        layout(p, width, height)
        row_bounds = bounds(container, p)
        details_bounds = bounds(details, p)
        assert row_bounds.Bottom <= details_bounds.Top, 'Case rows overlap the details panel: {} {}x{} row {} {} details {} list {}'.format(p.active_feature, width, height, index, row_bounds, details_bounds, bounds(results, p))
        assert row_bounds.Top >= 0 and row_bounds.Bottom <= height, 'Case row cannot scroll into the viewport'
        hit = VisualTreeHelper.HitTest(p, Point(row_bounds.Left+row_bounds.Width/2, row_bounds.Top+row_bounds.Height/2))
        if not (hit is not None and container.IsAncestorOf(hit.VisualHit)):
            chain, node_ = [], hit and hit.VisualHit
            while node_ is not None and len(chain) < 12:
                chain.append(u'{}:{}'.format(node_.GetType().Name, getattr(node_, 'Name', '')))
                node_ = VisualTreeHelper.GetParent(node_)
            border = VisualTreeHelper.GetChild(results, 0)
            viewer = VisualTreeHelper.GetChild(border, 0)
            raise AssertionError(u'Case row covered: {} {}x{} row {} row={} list={} desired={} viewer={} {} extent={} view={} hscroll={}'.format(
                p.active_feature, width, height, index, row_bounds, bounds(results, p), container.DesiredSize,
                viewer.GetType().Name, viewer.CanContentScroll, getattr(viewer, 'ExtentWidth', None), getattr(viewer, 'ViewportWidth', None),
                getattr(viewer, 'HorizontalScrollBarVisibility', None)))
        return container

    p.set_target(node('A'), refresh=False)
    p.set_case_issue_type('ALL')
    cases = [current('A', 'RESOLVED'), current('B', 'RESOLVED')]
    cases[0]['draft']['title'] = u'CASE-001 | 大門開向與使用淨空檢查'
    cases[1]['id'] = 2
    cases[1]['draft']['title'] = u'CASE-002 | 同型門的連接及尺寸檢查'
    cases[1]['draft']['issue_type'] = 'CONNECTION_ERROR'
    for row in cases:
        row['solution'] = u'核對模型元件及圖面，再按核定方案調整。'
    p.display_cases(cases)
    p.open_feature('library')
    assert p.Cases.Items.Count == 2 and u'2' in p.LibrarySummary.Text
    for width, height in [(900, 800), (520, 800), (380, 600)]:
        for index in (0, 1):
            reveal_row(p, p.Cases, p.LibraryDetails, index, width, height)
    p.WorkAreaScroll.ScrollToTop()
    layout(p, 520, 800)
    preview(p, 'case-library-compact', 520, 800)

    # Long lists must retain a bounded inner viewport, while both the last row
    # and the actions below it remain reachable through the outer page scroll.
    many = []
    for index in range(30):
        row = copy.deepcopy(cases[index % 2])
        row['id'] = index+1
        row['draft']['title'] += u'：檢查同一樓層的元件位置、標高、尺寸與接合條件'
        many.append(row)
    p.display_cases(many)
    p.TargetName.Text = u'目前樓層的基準元件（長名稱）：大門與相鄰牆體接合及尺寸檢查'
    rows = []
    for index in range(30):
        row = related('A' if index % 2 == 0 else 'B', 'RESOLVED')['results'][0]
        row.update(case_id=index+1, title=many[index]['draft']['title'])
        rows.append(row)
    for feature, results, details, action in [('library', p.Cases, p.LibraryDetails, p.OpenLibrarySolution),
                                             ('related', p.RelatedCases, p.CaseDetails, None)]:
        p.open_feature(feature)
        if feature == 'related':
            p.display_related(dict(results=rows, message=u'找到 30 個相關案例'))
        assert results.Items.Count == 30
        for width, height in [(900, 800), (520, 800), (380, 600)]:
            for index in (0, 29):
                container = reveal_row(p, results, details, index, width, height)
            assert results.ActualHeight <= 360, 'Long list needs its own scroll viewport'
        results.SelectedIndex = 29
        start(p)
        assert selected_ids[-1] == [102] and shown_ids[-1] == [102]
        assert details.Text
        target = action or details
        target.BringIntoView()
        layout(p, 380, 600)
        action_bounds = bounds(target, p)
        assert action_bounds.Top >= 0 and action_bounds.Bottom <= 600
    p.open_feature('issue')
    layout(p, 520, 800)
    assert p.WorkAreaScroll.VerticalOffset == 0
    p.refresh_timer.Stop()


def test_library_search_opens_solutions_directly_and_review_actions_follow_status():
    p = new_panel()
    p.set_target(node('A'), refresh=False)
    saved = current(status='RESOLVED')
    saved['draft']['title'] = u'CASE-001｜大門開向與使用淨空檢查'
    saved['draft']['description'] = u'門扇開啟後遮擋牆上的插座。'
    saved['solution'] = u'建議處理方式（待確認）\n1. 核對門扇開向及插座位置。\n2. 按核定方案重新定位插座。\n3. 更新圖面並檢查同型門。'
    other = current('B')
    other['id'] = 2
    p.display_cases([saved, other])
    p.open_feature('library')
    assert p.AutoHighlight.Visibility == Visibility.Visible
    p.CaseQuery.Text = u'重新定位'
    assert p.Cases.Items.Count == 1
    p.Cases.SelectedIndex = 0
    assert u'插座' in p.LibraryDetails.Text and p.OpenLibrarySolution.IsEnabled
    start(p)
    assert selected_ids == [[101]] and shown_ids == [[101]]
    p.display_cases([saved, other])
    assert p.pending_result is None  # Refresh must not jump the model view.
    p.UpdateLayout()
    container = p.Cases.ItemContainerGenerator.ContainerFromIndex(0)
    p.library_clicked(p.Cases, Object(ClickCount=1, OriginalSource=container))
    start(p)
    assert selected_ids == [[101], [101]]
    p.AutoHighlight.IsChecked = False
    p.LocateLibraryCase.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    p.handler.Execute(app)
    assert selected_ids[-1] == [101]
    preview(p, 'case-library-preview')
    p.responses['/cases/1'] = saved
    p.OpenLibrarySolution.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    p.handler.Execute(app)
    complete(p)
    assert p.active_feature == 'review' and p.Solution.Text == saved['solution']
    assert p.ReviewCaseTitle.Text == saved['draft']['title']
    assert p.ReviewStart.IsEnabled and p.Verify.IsEnabled and not p.Resolve.IsEnabled
    preview(p, 'solution-review-preview')
    p.Solution.Text += u'\n尚未儲存的補充'
    p.open_feature('library')
    p.open_feature('review')
    assert p.Solution.Text.endswith(u'尚未儲存的補充')
    p.sync_case(current(status='UNDER_REVIEW'))
    assert p.Resolve.IsEnabled and not p.Verify.IsEnabled and not p.Archive.IsEnabled
    p.sync_case(current(status='VERIFIED'))
    assert p.Archive.IsEnabled and not p.Resolve.IsEnabled
    p.new_click(None, None)
    assert not any(c.IsEnabled for c in (p.ReviewStart,p.Resolve,p.Verify,p.Archive))
    p.refresh_timer.Stop()


def test_delete_buttons_remove_library_and_open_cases_only_after_confirmation():
    forms = sys.modules['pyrevit'].forms
    previous, answers = getattr(forms, 'alert', None), []
    forms.alert = lambda *args, **kwargs: answers.pop(0)
    click = RoutedEventArgs(Button.ClickEvent)
    try:
        p = new_panel()
        p.set_target(node('A'), refresh=False)
        other = current('B')
        other['id'], other['draft']['title'] = 2, u'舊解法'
        p.display_cases([current(status='VERIFIED'), other])
        p.open_feature('library')
        assert not any(b.IsEnabled for b in (p.DeleteLibraryCase, p.DeleteIssue, p.DeleteReviewCase))
        p.Cases.SelectedIndex = 1
        assert p.DeleteLibraryCase.IsEnabled
        answers.append(False)
        p.DeleteLibraryCase.RaiseEvent(click)
        assert not p.handler.action and not p.waiting and u'已取消' in p.Status.Text
        answers.append(True)
        p.DeleteLibraryCase.RaiseEvent(click)
        p.handler.Execute(app)
        p.responses['/cases/2'] = dict(removed=True, case_id=2)
        complete(p)
        assert p.calls[-1] == ('/cases/2', None, 'DELETE')
        assert [i.data['id'] for i in p.Cases.Items] == [1] and u'RFI-2' in p.Status.Text
        # Deleting the open case from 解法審查 also clears the issue and review forms.
        p.sync_case(current(status='RESOLVED'))
        assert p.DeleteIssue.IsEnabled and p.DeleteReviewCase.IsEnabled
        answers.append(True)
        p.DeleteReviewCase.RaiseEvent(click)
        p.handler.Execute(app)
        p.responses['/cases/1'] = dict(removed=True, case_id=1)
        complete(p)
        assert p.calls[-1] == ('/cases/1', None, 'DELETE') and p.current is None
        assert not p.Cases.Items.Count and not p.DeleteIssue.IsEnabled and not p.DeleteReviewCase.IsEnabled
        p.refresh_timer.Stop()
    finally:
        forms.alert = previous


def test_ribbon_entry_points_share_registered_panel_across_python_engines():
    import os
    from System import AppDomain, String
    from System.Collections.Generic import List
    clr.AddReference('pyRevitLabs.IronPython')
    from IronPython.Hosting import Python
    from casegraph.navigation import PANEL_INSTANCE_KEY
    state = dict(registered=False, registrations=0, opens=0)
    store_key = 'CaseGraph.Test.SharedEnvironment'
    AppDomain.CurrentDomain.SetData(store_key, {})

    def get_var(key):
        return AppDomain.CurrentDomain.GetData(store_key).get(key)

    def set_var(key, value):
        values = AppDomain.CurrentDomain.GetData(store_key)
        values[key] = value
        AppDomain.CurrentDomain.SetData(store_key, values)

    def register(panel_type, default_visible=False):
        state['registered'] = True
        state['registrations'] += 1
        return new_panel()

    def show(panel_id):
        state['opens'] += 1

    forms = sys.modules['pyrevit'].forms
    forms.register_dockable_panel = register
    forms.open_dockable_panel = show
    forms.alert = lambda *a, **k: (_ for _ in ()).throw(AssertionError('Unexpected restart message'))
    ui.DockablePane = Object(PaneIsRegistered=lambda pane_id: state['registered'])
    ui.DockablePaneId = lambda guid: guid
    coreutils = module('pyrevit.coreutils', envvars=Object(get_pyrevit_env_var=get_var, set_pyrevit_env_var=set_var))
    service = module('casegraph.service', ensure_started=lambda: None)
    from casegraph import bootstrap
    bootstrap.open_panel('issue')
    p = get_var(PANEL_INSTANCE_KEY)
    p.IssueTitle.Text = u'跨按鈕保留草稿'
    # A second real IronPython runtime imports its own bootstrap module, as ribbon
    # commands do, and must operate on the original WPF panel from the shared store.
    engine = Python.CreateEngine()
    engine.SetSearchPaths(List[String](sys.path))
    scope = engine.CreateScope()
    for key, value in dict(shared_ui=ui, shared_pyrevit=sys.modules['pyrevit'],
                           shared_coreutils=coreutils, shared_service=service).items():
        scope.SetVariable(key, value)
    engine.Execute('''import sys, types
sys.modules['Autodesk'] = types.ModuleType('Autodesk')
sys.modules['Autodesk.Revit'] = types.ModuleType('Autodesk.Revit')
sys.modules['Autodesk.Revit.UI'] = shared_ui
sys.modules['pyrevit'] = shared_pyrevit
sys.modules['pyrevit.coreutils'] = shared_coreutils
sys.modules['casegraph.service'] = shared_service
workspace = types.ModuleType('casegraph.workspace_panel')
workspace.PANEL_ID = 'db4a9c6a-d351-4621-8159-4ac39189e424'
workspace.INSTANCE = None
sys.modules['casegraph.workspace_panel'] = workspace
from casegraph.bootstrap import open_panel
open_panel('library')
open_panel('graph')
''', scope)
    assert p.active_feature == 'graph'
    assert p.IssueTitle.Text == u'跨按鈕保留草稿'
    assert state['registrations'] == 1 and state['opens'] == 3
    assert get_var(PANEL_INSTANCE_KEY) is p
    # Execute every real ribbon script and check the feature it opens.
    from casegraph.navigation import FEATURES
    extension = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', 'CaseBase.extension', u'案例驅動 BIM 診斷與修正.tab'))
    opened = set()
    # The optional ShaftChecker has its own window; exercise its command without
    # displaying a desktop window during this headless ribbon test.
    from casegraph import shaft_window
    open_shaft_window = shaft_window.open_window
    shaft_commands = []
    shaft_window.open_window = lambda: shaft_commands.append(True)
    for directory, _, files in os.walk(extension):
        if directory.endswith('.pushbutton') and 'script.py' in files:
            execfile(os.path.join(directory, 'script.py'), {})
            opened.add(p.active_feature)
    shaft_window.open_window = open_shaft_window
    assert shaft_commands == [True]
    assert opened == set(row[0] for row in FEATURES)
    assert state['registrations'] == 1 and p.IssueTitle.Text == u'跨按鈕保留草稿'
    p.refresh_timer.Stop()
    AppDomain.CurrentDomain.SetData(store_key, None)


def test_rapid_result_selection_and_busy_external_event_use_only_latest():
    p = new_panel()
    p.busy = True
    p.queue_result(node('A'))
    p.queue_result(node('B'))
    start(p)
    assert not selected_ids and p.pending_result[0]['node_id']=='model:B'
    p.finish()
    p.event.accepted = False
    start(p)
    assert p.pending_result[0]['node_id']=='model:B' and not selected_ids
    p.event.accepted = True
    start(p)
    assert selected_ids == [[102]] and p.pending_result is None
    p.AutoHighlight.IsChecked = False
    p.queue_result(node('A'))
    start(p)
    assert selected_ids == [[102]]


def test_foreign_deleted_and_detail_view_results_are_safe_to_highlight():
    p = new_panel()
    foreign = node('A')
    foreign.update(source_document='other',node_id='other:A')
    p.queue_result(foreign)
    start(p)
    assert selected_ids == [[]] and not shown_ids and u'其他文件' in p.Status.Text
    p.queue_result(node('deleted'))
    start(p)
    assert selected_ids == [[],[]] and not shown_ids and u'已刪除' in p.Status.Text
    elements['B'].ViewSpecific = True
    elements['B'].OwnerViewId = 'detail-view'
    elements['detail-view'] = Object(Id=901,IsTemplate=False)
    uidoc.ActiveView = Object(Id=900)
    p.queue_result(node('B'))
    start(p)
    assert uidoc.ActiveView.Id==901 and selected_ids[-1]==[102] and shown_ids==[[102]]
    elements['B'].ViewSpecific = False
    del elements['detail-view']


def test_target_refresh_and_real_xaml_events():
    p = new_panel()
    p.responses['/cases/related'] = related('A')
    p.set_target(node('A'))
    assert p.graph is None
    start(p)
    complete(p)
    assert p.graph['root'] == 'model:A'
    assert p.RelatedCases.Items.Count == 1
    assert p.RelatedCases.SelectedItem.data['title'] == 'A'
    assert p.CaseDetails.Text.startswith('A')
    assert not p.refresh_queue.pending  # Nested list selection must not loop.
    p.VerifiedOnly.IsChecked = True
    assert p.refresh_queue.pending == set(['related'])
    p.responses['/cases/related'] = dict(results=[], message=u'尚無已驗證案例')
    start(p)
    complete(p)
    assert p.calls[-1][1]['verified_only']
    assert p.RelatedCases.Items.Count == 0 and p.CaseDetails.Text == ''
    p.Deep.IsChecked = True
    assert p.refresh_queue.pending == set(['graph', 'matches', 'related'])
    start(p)
    complete(p)
    assert extractions[-1][2:] == (2, 160)
    p.Tabs.SelectedItem = p.GraphTab
    assert p.refresh_queue.pending == set(['graph', 'matches', 'related'])
    p.Measure(Size(520, 1200))
    p.Arrange(Rect(0, 0, 520, 1200))
    p.UpdateLayout()
    p.fit_click(None, None)
    assert 0 < p.scale <= 1
    assert p.GraphCanvas.Width*p.scale <= p.GraphScroll.ViewportWidth
    assert p.GraphCanvas.Height*p.scale <= p.GraphScroll.ViewportHeight
    p.refresh_timer.Stop()


def test_superseded_response_and_pending_refresh():
    p = new_panel()
    p.responses['/cases/related'] = related('A')
    p.set_target(node('A'))
    start(p)
    p.set_target(node('B'))  # New target while previous HTTP request is pending.
    p.refresh_tick(None, None)
    assert p.refresh_queue.pending == set(['related', 'graph', 'matches'])
    complete(p)
    assert p.RelatedCases.Items.Count == 0 and p.graph is None
    p.responses['/cases/related'] = related('B')
    start(p)
    complete(p)
    assert p.graph['root'] == 'model:B'
    assert p.RelatedCases.SelectedItem.data['title'] == 'B'
    assert len([c for c in p.calls if c[0] == '/cases/related']) == 2 and not p.refresh_queue.pending


def test_model_invalidation_and_immutable_snapshot():
    p = new_panel()
    saved = current()
    original = copy.deepcopy(saved)
    p.set_target(node('A'))
    p.sync_case(saved)
    p.responses.update({'/cases/related': related('A'), '/cases': [saved]})
    start(p)
    complete(p)
    elements['A'].record['name'] = u'修改後 m³'
    p.document_changed(None, Object(GetDocument=lambda: uidoc.Document))
    start(p)
    complete(p)
    assert p.graph['nodes'][0]['name'] == u'修改後 m³'
    assert p.current == original
    assert p.graph is not p.current['draft']['graph']
    count = len(extractions)
    p.SnapshotOnly.IsChecked = True
    start(p)
    assert len(extractions) == count
    assert p.graph == original['draft']['graph']
    assert u'已儲存快照' in p.GraphSummary.Text
    elements['A'].record['name'] = 'A'


def test_save_and_status_refresh_lists_without_excluding_current_case():
    p = new_panel()
    p.set_target(node('A'))
    p.refresh_queue.take()
    p.IssueTitle.Text, p.Description.Text = 'Issue', 'Description'
    saved = current()
    p.responses.update({'/elements': node('A'), '/cases': saved})
    p.save_click(None, None)
    p.handler.Execute(app)
    complete(p)
    assert p.current['id'] == 1
    assert set(['related', 'cases']).issubset(p.refresh_queue.pending)
    p.responses.update({'/cases/related': related('A'), '/cases': [saved]})
    start(p)
    complete(p)
    assert p.RelatedCases.Items.Count == 1 and p.Cases.Items.Count == 1
    payload = next(call[1] for call in p.calls if call[0] == '/cases/related')
    assert not payload.get('exclude_case_id')
    verified = current(status='VERIFIED')
    p.responses['/cases/1/status'] = verified
    p.change_status('VERIFIED')
    p.handler.Execute(app)
    complete(p)
    assert p.refresh_queue.pending == set(['related', 'graph', 'matches', 'cases'])
    p.responses.update({'/cases/related': related('A', 'VERIFIED'), '/cases': [verified]})
    start(p)
    complete(p)
    assert p.RelatedCases.SelectedItem.data['status'] == 'VERIFIED'
    assert p.graph['nodes'][-1]['properties']['status'] == 'VERIFIED'
    assert saved['draft']['graph']['nodes'][-1]['properties']['status'] == 'DRAFT'


def test_tab_revisit_bubbled_selection_and_offline_error():
    p = new_panel()
    p.set_target(node('A'))
    p.refresh_queue.take()
    p.Tabs.SelectedItem = p.HistoryTab
    assert p.refresh_queue.pending == set(['related'])
    start(p)
    complete(p, failure=u'本機服務離線')
    assert u'離線' in p.CaseSummary.Text
    assert not p.busy and p.WorkArea.IsEnabled
    assert not p.refresh_queue.pending  # No endless offline retry.
    p.Tabs.SelectedItem = p.SimilarTab
    p.Tabs.SelectedItem = p.HistoryTab
    p.responses['/cases/related'] = related('A')
    start(p)
    complete(p)
    assert p.RelatedCases.Items.Count == 1
    assert not p.refresh_queue.pending


def test_hidden_busy_and_rejected_external_event_are_not_dropped():
    p = new_panel()
    p.set_target(node('A'))
    p.IsVisible = False
    start(p)
    assert p.refresh_queue.pending == set(['graph', 'related', 'matches']) and not p.waiting
    p.IsVisible = True
    p.event.accepted = False
    start(p)
    assert p.refresh_queue.pending == set(['graph', 'related', 'matches'])
    p.event.accepted = True
    p.responses['/cases/related'] = related('A')
    start(p)
    complete(p)
    assert p.graph['root'] == 'model:A' and not p.refresh_queue.pending


def test_cross_document_and_deleted_target_allow_saved_snapshot():
    p = new_panel()
    p.set_target(node('A'))
    saved = current()
    p.sync_case(saved)
    p.refresh_queue.take()
    uidoc.Document.identity = 'other-model'
    p.schedule_refresh('graph')
    start(p)
    assert p.graph is None and u'來源文件' in p.GraphSummary.Text
    p.responses['/cases/related'] = related('A')
    complete(p)
    p.SnapshotOnly.IsChecked = True
    start(p)
    assert p.graph == saved['draft']['graph']
    uidoc.Document.identity = 'model'
    deleted = elements.pop('A')
    p.schedule_refresh('graph')
    start(p)
    assert p.graph == saved['draft']['graph']
    p.SnapshotOnly.IsChecked = False
    start(p)
    assert p.graph is None and u'找不到' in p.GraphSummary.Text
    complete(p)
    elements['A'] = deleted


def test_open_case_refresh_preserves_form_and_snapshot():
    p = new_panel()
    p.set_target(node('B'))
    saved = current()
    original = copy.deepcopy(saved)
    p.responses['/cases/1'] = saved
    p.open_case(1)
    p.handler.Execute(app)
    complete(p)
    assert p.target['node_id'] == 'model:A' and p.IssueTitle.Text == 'Issue'
    p.IssueTitle.Text = u'尚未儲存的編輯'
    p.responses.update({'/cases/related': related('A'), '/cases': [saved]})
    start(p)
    complete(p)
    assert p.IssueTitle.Text == u'尚未儲存的編輯' and p.current == original
    assert all(call[2] is None for call in p.calls)


def test_all_plan_scan_steps_origin_navigation_cancellation_and_invalidation():
    import casegraph.check_view as checks_module
    from casegraph.plan_checks import PLAN_KINDS
    p = new_panel()
    p.set_target(node('A'), refresh=False)
    p.open_feature('checks')
    p.refresh_queue.take()
    p.CheckDuplicates.IsChecked = p.CheckOverlaps.IsChecked = False
    p.CheckMarks.IsChecked = p.CheckWarnings.IsChecked = False
    views = [dict(id=str(901+i), name=name, kind=kind) for i, (kind, name) in enumerate(sorted(PLAN_KINDS.items()))]
    native_views = dict((int(v['id']), Object(Id=int(v['id']), Name=v['name'], ViewType=v['kind'], IsTemplate=False, Document=uidoc.Document)) for v in views)
    original_get, original_view = uidoc.Document.GetElement, getattr(uidoc, 'ActiveView', None)
    uidoc.ActiveView = Object(Id=900, Name='3D', Document=uidoc.Document)
    uidoc.Document.GetElement = lambda key: native_views.get(key) or elements.get(key)
    previous = (checks_module.RevitCheckAdapter, checks_module.run_checks, checks_module.list_plan_views)
    calls = []
    failed = set()
    def execute(adapter, scope, checks, **options):
        calls.append(adapter.view.Id)
        assert scope['scope'] == 'all' and checks == ['clash']
        assert options['clash_skip_joined'] and options['clash_skip_collinear']
        assert options['clash_line_mm'] == 1.
        if adapter.view.Id in failed:
            raise ValueError('Cannot read plan')
        return dict(status='REVIEW', sources=2, pairs=1, findings=1, unknown=0, truncated=False,
                    skipped_joined=0, skipped_collinear=0, excluded_curve_elements=0, excluded_curve_sources=0,
                    warnings=[], results=[dict(id='0', check='clash', status='OVERLAP', records=[node('A'), node('B')],
                                             message=u'確認實體重疊', advice=u'核對相交元件')])
    checks_module.RevitCheckAdapter = lambda doc, view: Object(view=view)
    checks_module.run_checks = execute
    checks_module.list_plan_views = lambda doc: copy.deepcopy(views)
    try:
        p.CheckRangeMode.SelectedIndex = 2
        assert p.CheckRangePanel.Visibility == Visibility.Collapsed
        p.RunModelChecks.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        assert p.CheckRangeMode.SelectedItem.data == 'plans' and p.CheckScope.SelectedItem.data == 'all'
        start(p)
        assert len(calls) == 1 and p.check_job.index == 1 and p.check_pending
        assert p.CancelPlanChecks.IsEnabled and uidoc.ActiveView.Id == 900
        # Switching views in the same document does not restart the batch.
        uidoc.ActiveView = native_views[902]
        p.view_activated(None, Object(CurrentActiveView=uidoc.ActiveView))
        assert p.check_job.index == 1
        p.IsVisible = False
        for _ in range(3):
            start(p)
        p.IsVisible = True
        assert len(calls) == 4 and p.check_job is None and not p.check_pending
        assert not p.CancelPlanChecks.IsEnabled and p.check_report['completed_plans'] == 4
        assert p.CheckResults.Items.Count == 1 and len(p.check_report['results'][0]['views']) == 4
        assert all(v['name'] in p.CheckResults.Items[0].label for v in views)
        assert all(v['name'] in p.PlanCoverage.Text for v in views)
        p.refresh_queue.take()
        uidoc.ActiveView = Object(Id=900, Name='3D', Document=uidoc.Document)
        p.CheckResults.SelectedIndex = 0
        start(p)
        assert selected_ids[-1] == [102] and shown_ids[-1] == [102] and uidoc.ActiveView.Id == 901
        saved = p.check_report
        p.view_activated(None, Object(CurrentActiveView=uidoc.ActiveView))
        assert p.check_report is saved
        preview(p, 'all-plans-check-preview')
        # Cancel between views retains findings and identifies unprocessed plans.
        p.run_checks_click(None, None)
        start(p)
        p.cancel_plan_checks(None, None)
        assert p.check_report['cancelled'] and p.check_report['status'] == 'INCOMPLETE'
        assert p.check_report['completed_plans'] == 1 and not p.check_pending
        # A failed plan does not prevent subsequent plans from being inspected.
        failed.add(902)
        p.run_checks_click(None, None)
        for _ in range(4):
            start(p)
        assert p.check_report['status'] == 'INCOMPLETE' and p.check_report['completed_plans'] == 4
        assert u'未完成' in p.PlanCoverage.Text
        # Model changes invalidate the old batch and restart using the same mode.
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        assert p.check_report is None and p.check_pending
        start(p)
        assert p.check_job.index == 1 and p.CheckRangeMode.SelectedItem.data == 'plans'
        uidoc.Document.identity = 'other'
        p.view_activated(None, Object(CurrentActiveView=uidoc.ActiveView))
        assert p.check_job is None and p.check_report is None and not p.check_pending
    finally:
        uidoc.Document.identity = 'model'
        uidoc.Document.GetElement, uidoc.ActiveView = original_get, original_view
        checks_module.RevitCheckAdapter, checks_module.run_checks, checks_module.list_plan_views = previous
        p.refresh_timer.Stop()


def preview_knowledge(window, name, overview=False):
    folder = os.environ.get('CASEGRAPH_PREVIEW_DIR')
    if not folder:
        return
    from System import Action
    from System.Windows.Threading import DispatcherPriority
    from System.Windows.Media import PixelFormats
    from System.Windows.Media.Imaging import RenderTargetBitmap, BitmapFrame, PngBitmapEncoder
    from System.IO import File, Directory
    window.panel.refresh_timer.Stop()
    window.Dispatcher.Invoke(Action(lambda: None), DispatcherPriority.Background)
    window.UpdateLayout()
    if overview:
        window.fit_click(None, None)
    window.UpdateLayout()
    width, height = int(window.ActualWidth), int(window.ActualHeight)
    bitmap = RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32)
    bitmap.Render(window)
    encoder = PngBitmapEncoder()
    encoder.Frames.Add(BitmapFrame.Create(bitmap))
    Directory.CreateDirectory(folder)
    stream = File.Create(os.path.join(folder, name+'.png'))
    try:
        encoder.Save(stream)
    finally:
        stream.Close()


def test_knowledge_window_is_modeless_singleton_and_highlights_with_panel_hidden():
    p = new_panel()
    p.set_target(node('A'), refresh=False)
    graph = current()['draft']['graph']
    graph['nodes'].append(node('B'))
    graph['edges'].extend([
        dict(source='model:A', target='model:B', relationship_type='CONNECTED_TO', properties={}, evidence=u'接頭', validation_status='candidate'),
        dict(source='model:B', target='model:A', relationship_type='HOSTED_BY', properties={}, evidence=u'主體', validation_status='verified')])
    p.display_graph(graph, u'目前模型')
    p.refresh_queue.take()
    p.OpenKnowledgeWindow.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    w = p.knowledge_window
    try:
        assert w.IsVisible and w.graph is p.graph
        w.RelationFilter.SelectedItem = next(i for i in w.RelationFilter.Items if i.data == 'ALL')
        p.open_knowledge_window(None, None)
        assert p.knowledge_window is w
        p.IsVisible = False
        w.node_click(w.cards['model:B'], None)
        start(p)
        assert selected_ids[-1] == [102] and shown_ids[-1] == [102]
        assert w.selected['node_id'] == 'model:B' and w.LocateNode.IsEnabled
        assert w.NodeProperties.Items.Count > 0 and w.NodeRelations.Items.Count == 2
        w.AutoHighlight.IsChecked = False
        assert not p.AutoHighlight.IsChecked
        count = len(selected_ids)
        w.node_click(w.cards['model:A'], None)
        start(p)
        assert len(selected_ids) == count
        p.AutoHighlight.IsChecked = True
        assert w.AutoHighlight.IsChecked
        p.IssueTitle.Text = u'尚未儲存的內容'
        w.Close()
        assert p.knowledge_window is None and p.IssueTitle.Text == u'尚未儲存的內容'
        p.open_knowledge_window(None, None)
        w = p.knowledge_window
        assert w.IsVisible and w.graph is graph
        w.RelationFilter.SelectedItem = next(i for i in w.RelationFilter.Items if i.data == 'ALL')
        # A scheduled navigation survives closing the presentation window.
        w.select_record(node('B'))
        w.locate_click(None, None)
        w.Close()
        p.handler.Execute(app)
        assert selected_ids[-1] == [102]
    finally:
        if p.knowledge_window is not None:
            w.Close()
        p.refresh_timer.Stop()


def test_knowledge_window_refresh_filter_classification_and_empty_state():
    p = new_panel()
    p.set_target(node('A'), refresh=False)
    p.graph_matches = matches(node('B'))['results']
    graph = current()['draft']['graph']
    p.display_graph(graph, u'目前模型', live=True)
    original = copy.deepcopy(graph)
    p.open_knowledge_window(None, None)
    w = p.knowledge_window
    try:
        w.RelationFilter.SelectedItem = next(i for i in w.RelationFilter.Items if i.data == 'ALL')
        groups = [b for b in w.cards.values() if b.Tag['node_type'] == 'GROUP']
        assert len(groups) == 3 and len(w.network['matches']) == 1
        w.node_click(groups[0], None)
        assert p.pending_result is None and not w.LocateNode.IsEnabled
        assert len(selected_ids) == 0
        w.RelationFilter.SelectedItem = next(i for i in w.RelationFilter.Items if i.data == 'HAS_ISSUE')
        assert len(w.network['edges']) == 1 and 'model:B' not in w.cards
        assert len(w.network['nodes']) == 2
        w.RelationFilter.SelectedIndex = 0
        preview_knowledge(w, 'knowledge-graph-window')
        p.refresh_queue.take()
        p.IsVisible = False
        p.responses['/cases/related'] = related('A')
        p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
        start(p)
        complete(p)
        assert w.graph is p.graph and w.graph['root'] == 'model:A'
        assert graph == original
        p.clear_graph(u'來源文件已切換')
        assert not w.cards and not w.selected and not w.LocateNode.IsEnabled
        assert w.EmptyMessage.Visibility == Visibility.Visible
        assert u'來源文件已切換' in w.SourceLabel.Text
    finally:
        w.Close()
        p.refresh_timer.Stop()


def test_knowledge_window_real_model_graph_preview():
    import json
    from System.IO import File
    from System.Text import Encoding
    path = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', 'samples', 'demo-model-export.json'))
    export = json.loads(File.ReadAllText(path, Encoding.UTF8))
    graph = next(g for g in export['graphs'].values() if next(n for n in g['nodes'] if n['node_id'] == g['root'])['properties'].get('element_id') == '334206')
    original = copy.deepcopy(graph)
    root = next(n for n in graph['nodes'] if n['node_id'] == graph['root'])
    p = new_panel()
    p.set_target(root, refresh=False)
    candidates = [n for n in export['elements'] if n['properties'].get('category') == 'Doors' and n['node_id'] != root['node_id']]
    p.graph_matches = [dict(element=n, score=.9, completeness=.9) for n in candidates[:6]]
    p.display_graph(graph, u'模型記錄 · '+export['title'], live=True)
    p.refresh_queue.take()
    p.open_knowledge_window(None, None)
    w = p.knowledge_window
    try:
        assert w.scale >= .75
        w.RelationFilter.SelectedItem = next(i for i in w.RelationFilter.Items if i.data == 'ALL')
        assert len(w.network['matches']) == 6
        assert len([e for e in w.network['edges'] if not e['grouping']]) == len(graph['edges'])
        preview_knowledge(w, 'knowledge-graph-real-model')
        preview_knowledge(w, 'knowledge-graph-real-model-overview', overview=True)
        w.actual_size(None, None)
        assert w.scale == 1.
        assert graph == original
    finally:
        w.Close()
        p.refresh_timer.Stop()


def test_case_knowledge_refresh_navigation_status_failure_and_snapshot():
    p = new_panel()
    p.set_target(node('A'), refresh=False)
    p.ShowSimilar.IsChecked = False
    response = related('B', 'RESOLVED')
    response['results'][0]['resolution_note'] = u'調整門扇開啟方向，覆核通道淨寬。'
    p.responses['/cases/related'] = response
    p.graph_refresh_click(None, None)
    start(p)
    complete(p)
    p.refresh_queue.take()
    p.open_knowledge_window(None, None)
    w = p.knowledge_window
    p.IssueTitle.Text = u'保留未儲存內容'
    try:
        assert w.RelationFilter.SelectedItem.data == 'CASES'
        assert set(w.cards) == set(['model:A', 'model:B', 'case:1', 'case:1:solution'])
        assert any(e['source'] == 'model:B' and e['target'] == 'case:1' for e in p.graph['edges'])
        assert not any(e['source'] == 'model:A' and e['target'] == 'case:1' for e in p.graph['edges'])
        p.IsVisible = False
        w.node_click(w.cards['case:1'], None)
        start(p)
        assert selected_ids[-1] == [102] and shown_ids[-1] == [102]
        assert p.target['node_id'] == 'model:A' and w.selected['node_id'] == 'case:1'
        assert p.IssueTitle.Text == u'保留未儲存內容' and p.current is None
        assert w.LocateNode.IsEnabled and w.OpenCase.IsEnabled and w.OpenSolution.IsEnabled
        saved_open_case = p.open_case
        rows = dict((r.label, r.value) for r in w.NodeProperties.Items)
        assert rows[u'解法驗證'] == u'未驗證' and rows[u'案例對應元素'] == 'B'
        w.node_click(w.cards['case:1:solution'], None)
        start(p)
        assert selected_ids[-1] == [102] and w.selected['node_type'] == 'SOLUTION'
        opened = []
        p.open_case = lambda case_id, destination='issue':opened.append((case_id, destination))
        w.OpenCase.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        w.OpenSolution.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        assert opened == [(1, 'issue'), (1, 'review')]
        preview_knowledge(w, 'knowledge-case-links')
        # Status changes appear without reopening or changing the pinned element.
        verified = related('B', 'VERIFIED')
        verified['results'][0]['solution'] = u'已覆核通道淨寬。'
        p.responses['/cases/related'] = verified
        w.refresh_click(None, None)
        start(p)
        complete(p)
        assert w.cards['case:1'].Tag['properties']['status'] == 'VERIFIED'
        assert w.cards['case:1:solution'].Tag['properties']['solution_state'] == u'已驗證'
        # A same-UID record in another document must never highlight the local element.
        foreign = copy.deepcopy(response)
        foreign['results'][0]['element'].update(node_id='other:B', source_document='other')
        p.display_related(foreign)
        w.node_click(w.cards['case:1'], None)
        start(p)
        assert selected_ids[-1] == [] and u'其他文件' in p.Status.Text
        p.display_related(response)
        deleted = elements.pop('B')
        try:
            w.node_click(w.cards['case:1'], None)
            start(p)
            assert selected_ids[-1] == [] and u'已刪除' in p.Status.Text
        finally:
            elements['B'] = deleted
        w.refresh_click(None, None)
        start(p)
        complete(p, failure=u'本機案例庫離線')
        assert 'case:1' not in w.cards and u'未能更新' in w.Summary.Text
        assert not p.refresh_queue.pending
        # Related-result updates never alter a saved evidence snapshot.
        snapshot = current()['draft']['graph']
        original = copy.deepcopy(snapshot)
        p.display_graph(snapshot, u'已儲存快照')
        p.display_related(response)
        assert p.graph == original and snapshot == original and 'case:1:solution' not in w.cards
        # Explicitly opening a case solution reveals its page even with the panel hidden.
        import casegraph.workspace_panel as workspace
        pane_ids = []
        old_open_pane = getattr(workspace.forms, 'open_dockable_panel', None)
        workspace.forms.open_dockable_panel = lambda pane_id:pane_ids.append(pane_id)
        try:
            p.open_case = saved_open_case
            p.responses['/cases/1'] = current('B', 'RESOLVED')
            p.open_case(1, destination='review')
            p.handler.Execute(app)
            complete(p)
            assert pane_ids == [workspace.PANEL_ID]
            assert p.active_feature == 'review' and p.target['node_id'] == 'model:B'
        finally:
            if old_open_pane is None:
                del workspace.forms.open_dockable_panel
            else:
                workspace.forms.open_dockable_panel = old_open_pane
    finally:
        w.Close()
        p.refresh_timer.Stop()


def test_case_knowledge_actual_library_preview():
    path = os.environ.get('CASEGRAPH_CASE_FIXTURE')
    if not path:
        return
    import json
    from System.IO import File
    from System.Text import Encoding
    fixture = json.loads(File.ReadAllText(path, Encoding.UTF8))
    graph = fixture['graph']
    p = new_panel()
    p.set_case_issue_type(fixture['related']['results'][0]['issue_type'])
    p.set_target(next(n for n in graph['nodes'] if n['node_id'] == graph['root']), refresh=False)
    p.display_graph(graph, u'目前模型 · 元件 334206', live=True)
    p.display_related(fixture['related'])
    p.open_knowledge_window(None, None)
    w = p.knowledge_window
    try:
        assert len([n for n in w.network['nodes'].values() if n['node_type'] == 'ISSUE']) == len(fixture['related']['results'])
        case_card = next(b for b in w.cards.values() if b.Tag['node_type'] == 'ISSUE'
                         and b.Tag['properties']['related_element'] == graph['root'])
        w.select_record(case_card.Tag)
        w.actual_size(None, None)
        preview_knowledge(w, 'knowledge-case-real-library')
        preview_knowledge(w, 'knowledge-case-real-library-overview', overview=True)
    finally:
        w.Close()
        p.refresh_timer.Stop()


def test_case_scope_is_shared_by_related_results_graph_and_does_not_change_draft():
    p = new_panel()
    # 相關案例 starts with every problem type of the pinned element's type.
    assert p.case_issue_type() == 'ALL'
    p.set_target(node('A'), refresh=False)
    p.set_case_issue_type('GEOMETRY_CLASH')
    saved, wrong_issue, wrong_type = current(), current('B'), current('B')
    wrong_issue['id'], wrong_type['id'] = 2, 3
    wrong_issue['draft']['issue_type'] = 'PARAMETER_ERROR'
    wrong_type['draft']['graph']['nodes'][0]['properties']['type_id'] = '200'
    p.display_cases([saved, wrong_issue, wrong_type])
    # 案例庫 is every past case, whatever the pinned element or problem filter.
    assert [i.data['id'] for i in p.Cases.Items] == [1, 2, 3]
    response = related('A')
    other_issue = copy.deepcopy(response['results'][0])
    other_issue.update(case_id=2, issue_type='PARAMETER_ERROR')
    other_type = copy.deepcopy(response['results'][0])
    other_type['case_id'] = 3
    other_type['element']['properties']['type_id'] = '200'
    response['results'].extend([other_issue, other_type])
    graph = extract(uidoc.Document, elements['A'], 'GEOMETRY_CLASH')
    p.display_graph(graph, u'目前模型', live=True)
    p.current = saved
    p.display_related(response)
    assert p.RelatedCases.Items.Count == 1
    assert len([n for n in p.graph['nodes'] if n['node_type'] == 'ISSUE']) == 1
    p.IssueTitle.Text = u'未儲存表單'
    p.open_knowledge_window(None, None)
    w = p.knowledge_window
    try:
        w.CaseIssueType.SelectedItem = next(i for i in w.CaseIssueType.Items if i.data == 'PARAMETER_ERROR')
        assert p.case_issue_type() == 'PARAMETER_ERROR'
        assert p.IssueType.SelectedItem.data == 'GEOMETRY_CLASH' and p.IssueTitle.Text == u'未儲存表單'
        assert p.RelatedCases.Items.Count == 0
        assert len([n for n in p.graph['nodes'] if n['node_type'] == 'ISSUE']) == 0  # Open case cannot bypass scope.
        assert p.Cases.Items.Count == 3
        p.responses['/cases/related'] = response
        start(p)
        complete(p)
        assert p.calls[-1][1]['issue_type'] == 'PARAMETER_ERROR'
        assert p.RelatedCases.Items.Count == 1 and p.RelatedCases.Items[0].data['case_id'] == 2
        assert all(n['properties']['issue_type'] == 'PARAMETER_ERROR' for n in p.graph['nodes'] if n['node_type'] == 'ISSUE')
        # Start an older filter request, switch filter, then deliver its response.
        p.schedule_refresh('related')
        start(p)
        p.set_case_issue_type('SPATIAL_CLEARANCE')
        complete(p)
        assert p.RelatedCases.Items.Count == 0
        start(p)
        complete(p)
        assert p.RelatedCases.Items.Count == 0 and p.Cases.Items.Count == 3
        assert not [n for n in p.graph['nodes'] if n['node_type'] == 'ISSUE']
        assert p.IssueTitle.Text == u'未儲存表單'
        w.CaseIssueType.SelectedItem = next(i for i in w.CaseIssueType.Items if i.data == 'ALL')
        assert p.case_issue_type() == 'ALL' and p.Cases.Items.Count == 3
        start(p)
        complete(p)
        assert p.calls[-1][1]['issue_type'] == 'ALL' and p.RelatedCases.Items.Count == 2
        assert {n['properties']['issue_type'] for n in p.graph['nodes'] if n['node_type'] == 'ISSUE'} == {'GEOMETRY_CLASH', 'PARAMETER_ERROR'}
        assert p.IssueTitle.Text == u'未儲存表單' and p.IssueType.SelectedItem.data == 'GEOMETRY_CLASH'
        assert not any(i.data == 'ALL' for i in p.IssueType.Items)
        # An ALL response arriving after a narrower choice cannot repopulate the graph.
        p.schedule_refresh('related')
        start(p)
        p.set_case_issue_type('PARAMETER_ERROR')
        complete(p)
        assert p.RelatedCases.Items.Count == 0
        start(p)
        complete(p)
        assert p.RelatedCases.Items.Count == 1 and p.RelatedCases.Items[0].data['case_id'] == 2
        assert all(n['properties']['issue_type'] == 'PARAMETER_ERROR' for n in p.graph['nodes'] if n['node_type'] == 'ISSUE')
    finally:
        w.Close()
        p.refresh_timer.Stop()


def test_double_clicked_check_result_becomes_a_structured_issue_record():
    import casegraph.check_view as view_module
    from casegraph.geometry_check import MM3_PER_FT3
    from System.Windows.Controls import Control
    from System.Windows.Input import Mouse, MouseButton, MouseButtonEventArgs
    elements['C'] = Object(Id=103, UniqueId='C', Category=Object(Id=-2000014), record=node('C'), ViewSpecific=False)
    answers = []
    forms = sys.modules['pyrevit'].forms
    forms.alert = lambda *args, **options: answers.pop(0)
    p = new_panel()
    p.set_target(node('C'))
    p.open_feature('checks')
    p.set_check_scope('type')  # These scenarios cover the type-based source scope.
    p.refresh_queue.take()

    class Solid(object):
        def __init__(self, cells):
            self.cells = set(cells)
            self.Volume = len(cells)/MM3_PER_FT3
        def Dispose(self):
            pass

    class Adapter(object):
        view_id, view_name = '900', u'2FL 模型檢查'
        def sources(self, scope):
            return ['A', 'B']
        def key(self, element):
            return element
        def record(self, element):
            return node(element, u'測試門 '+element)
        def metadata(self, element):
            return dict(type='100', category='Doors', bounds=(0,0,0,900,100,2100), placement=None)
        def mark(self, element):
            return 'Doors', u'D-01'
        def is_clash_room(self, element):
            return False
        def geometry(self, element):
            return dict(solids=[Solid([1,2])], problems=[])
        def boolean(self, a, b, operation):
            return Solid(a.cells & b.cells if operation == 'Intersect' else a.cells | b.cells)
        def joined(self, a, b):
            return False
        def close(self):
            pass

    def double_click(source):
        args = MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        args.RoutedEvent = Control.MouseDoubleClickEvent
        args.Source = source
        p.CheckResults.RaiseEvent(args)

    previous = view_module.RevitCheckAdapter
    view_module.RevitCheckAdapter = lambda doc, view: Adapter()
    uidoc.ActiveView = Object(Id=900, Name='QA view')
    try:
        for box in (p.CheckDuplicates, p.CheckClashes, p.CheckWarnings):
            box.IsChecked = False
        p.CheckOverlaps.IsChecked = p.CheckMarks.IsChecked = True
        p.AutoHighlight.IsChecked = False
        p.run_checks_click(None, None)
        start(p)
        assert [r['check'] for r in p.check_report['results']] == ['mark', 'overlap'], repr(p.CheckSummary.Text)
        p.Measure(Size(520, 1200))
        p.Arrange(Rect(0, 0, 520, 1200))
        p.UpdateLayout()
        p.CheckResults.SelectedIndex = 1
        assert p.RecordCheckIssue.IsEnabled
        preview(p, 'model-checks-docked', 400, 760)
        p.Measure(Size(520, 1200))
        p.Arrange(Rect(0, 0, 520, 1200))
        p.UpdateLayout()
        double_click(p.CheckResults)  # Empty list space or scrollbar.
        assert p.active_feature == 'checks' and p.issue_origin is None
        double_click(p.CheckResults.ItemContainerGenerator.ContainerFromIndex(1))
        assert p.active_feature == 'issue' and p.target['node_id'] == 'model:A'
        assert p.IssueTitle.Text.startswith(u'模型檢查｜同類元件實體重疊｜測試門 A')
        assert p.IssueType.SelectedItem.data == 'GEOMETRY_CLASH' and p.Priority.SelectedItem.data == 'High'
        assert u'\n檢查結果\n' in p.Description.Text and p.Notes.Text.startswith('model-check:overlap/A#1')
        assert p.IssueOrigin.Visibility == Visibility.Visible and u'#1' in p.IssueOrigin.Text
        # Moving the target to record the result keeps the list for further records.
        assert p.check_report is not None and p.CheckResults.Items.Count == 2
        assert u'基準元素已改為' in p.Status.Text
        assert not answers  # An empty form needs no confirmation.

        stored = {}
        def create(payload):
            stored['case'] = dict(id=7, revision=1, status='DRAFT', solution='', reviewer='', analyses=[], draft=copy.deepcopy(payload))
            return copy.deepcopy(stored['case'])
        p.responses.update({'/elements': node('A'), '/cases': create})
        p.save_click(None, None)
        p.handler.Execute(app)
        complete(p)
        payload = next(call[1] for call in p.calls if call[0] == '/cases')
        assert payload['origin']['result_id'] == '1' and payload['origin']['check'] == 'overlap'
        assert [(e['unique_id'], e['role']) for e in payload['origin']['elements']] == [('A', 'root'), ('B', 'related')]
        assert extracted_findings[-1] == [(102, True, '1')]  # Confirmed overlap is verified evidence.
        assert p.current['id'] == 7 and p.issue_origin == payload['origin'] and not p.issue_form_unsaved()

        # A second result: unsaved edits are only replaced after confirmation.
        p.open_feature('checks')
        p.UpdateLayout()
        p.IssueTitle.Text = u'尚未儲存的修改'
        p.CheckResults.SelectedIndex = 0
        answers.append(False)
        p.RecordCheckIssue.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        assert p.IssueTitle.Text == u'尚未儲存的修改' and p.current['id'] == 7 and p.active_feature == 'checks'
        answers.append(True)
        p.RecordCheckIssue.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        assert p.current is None and p.issue_origin['check'] == 'mark' and p.IssueType.SelectedItem.data == 'PARAMETER_ERROR'
        assert p.Submit.IsEnabled and u'重複 Mark' in p.IssueTitle.Text

        # Reopening the saved case restores its reference; another target drops a stale one.
        p.responses['/cases/7'] = stored['case']
        p.open_case(7)
        p.handler.Execute(app)
        complete(p)
        assert p.issue_origin == payload['origin'] and p.IssueOrigin.Visibility == Visibility.Visible
        p.new_click(None, None)
        assert p.issue_origin is None and p.IssueOrigin.Visibility == Visibility.Collapsed
        p.open_feature('checks')
        p.CheckResults.SelectedIndex = 1
        p.record_check_issue(p.RecordCheckIssue, None)
        p.set_target(node('B'))
        assert p.issue_origin is None and p.draft()['origin'] is None
    finally:
        view_module.RevitCheckAdapter = previous
        elements.pop('C')
        del forms.alert
        p.refresh_timer.Stop()


def test_multiple_selected_elements_pin_the_first_and_are_saved_as_related():
    p = new_panel()
    original_ids = uidoc.Selection.GetElementIds
    try:
        uidoc.Selection.GetElementIds = lambda: ['A', 'B']
        p.read_click(None, None)
        p.handler.Execute(app)
        assert p.target['node_id'] == 'model:A' and [m['node_id'] for m in p.target_members] == ['model:B']
        assert p.TargetName.Text.endswith(u'（另 1 個）') and u'ID B' in p.TargetCard.ToolTip
        # Re-reading the same target keeps the group; a different target drops it.
        p.set_target(node('A'), refresh=False)
        assert len(p.target_members) == 1
        p.IssueTitle.Text, p.Description.Text = 'Issue', 'Two doors'
        p.responses.update({'/elements': node('A'), '/cases': current()})
        p.save_click(None, None)
        p.handler.Execute(app)
        complete(p)
        assert extracted_related[-1] == [102]
        # Picking in the model keeps the click order: the first pick is the target.
        uidoc.Selection.GetElementIds = lambda: []
        uidoc.Selection.PickObjects = lambda kind, prompt: [Object(ElementId='B'), Object(ElementId='A')]
        p.read_click(None, None)
        p.handler.Execute(app)
        assert p.target['node_id'] == 'model:B' and [m['node_id'] for m in p.target_members] == ['model:A']
        p.set_target(node('A'), refresh=False)
        assert p.target_members == [] and p.TargetName.Text == 'A' and p.TargetCard.ToolTip is None
    finally:
        uidoc.Selection.GetElementIds = original_ids
        p.refresh_timer.Stop()


def test_review_detects_model_changes_appends_them_and_sends_evidence():
    p = new_panel()
    saved = current('A', 'RESOLVED')
    saved['draft']['graph']['nodes'].append(node('B'))
    saved['solution'] = u'調整門寬'
    live = dict(A=node('A'))
    live['A']['properties']['dimensions'] = {'Width': 1000}

    class Locator(object):
        def __init__(self, doc):
            self.document = doc.identity
        def find(self, record):
            uid = record['source_element_id']
            return (uid, 'Revit UniqueId') if uid in live else (None, None)
        def read(self, uid):
            return copy.deepcopy(live[uid])
    p.locator = Locator
    p.responses['/cases/1'] = saved
    try:
        p.open_case(1, 'review')
        p.handler.Execute(app)
        complete(p)
        assert p.active_feature == 'review' and p.change_pending
        start(p)
        states = [(i.data['unique_id'], i.data['state']) for i in p.ChangeResults.Items]
        assert states == [('A', 'CHANGED'), ('B', 'DELETED')]
        assert u'寬度：900 mm → 1000 mm' in p.ChangeResults.Items[0].detail
        assert p.UseChanges.IsEnabled and u'1 個已修改' in p.ChangeSummary.Text
        preview(p, 'solution-review-changes', 420, 900)
        p.UseChanges.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        p.UseChanges.RaiseEvent(RoutedEventArgs(Button.ClickEvent))  # Appending twice adds it once.
        assert p.Solution.Text.startswith(u'調整門寬\n\n模型變更紀錄') and p.Solution.Text.count(u'模型變更紀錄') == 1
        assert u'・B（ID B）：已刪除' in p.Solution.Text
        p.ChangeResults.SelectedIndex = 0
        assert p.pending_result[0]['node_id'] == 'model:A'
        p.pending_result = None
        p.Reviewer.Text = u'審核者'
        p.responses['/cases/1/status'] = current('A', 'VERIFIED')
        p.change_status('VERIFIED')
        p.handler.Execute(app)
        complete(p)
        payload = next(call[1] for call in p.calls if call[0] == '/cases/1/status')
        assert payload['changes']['case_id'] == 1 and payload['changes']['elements'][0]['changes']
        # A model edit re-runs the comparison while the page is open.
        live['A']['properties']['dimensions'] = {'Width': 900}
        p.document_changed(None, Object(GetDocument=lambda: uidoc.Document))
        assert p.change_report is None and p.change_pending
        start(p)
        # The verified case returned by the service records only A, now back to its snapshot.
        assert [i.data['state'] for i in p.ChangeResults.Items] == ['UNCHANGED'] and not p.UseChanges.IsEnabled
        p.new_click(None, None)
        assert p.ChangeResults.Items.Count == 0 and not p.UseChanges.IsEnabled
    finally:
        del p.locator
        p.refresh_timer.Stop()


def test_check_sources_are_the_revit_selection_captured_at_each_manual_run():
    import casegraph.check_view as view_module
    calls = []

    class Adapter(object):
        view_id, view_name = '900', u'2FL'
        def sources(self, scope):
            return list(scope['keys'])
        def key(self, element):
            return element
        def record(self, element):
            return node(element)
        def mark(self, element):
            return 'Doors', u'M-{}'.format(element)
        def close(self):
            pass
    real = view_module.run_checks

    def spy(adapter, scope, checks, **options):
        calls.append((dict(scope), options))
        return real(adapter, scope, checks, **options)
    previous = view_module.RevitCheckAdapter, view_module.run_checks
    view_module.RevitCheckAdapter, view_module.run_checks = (lambda doc, view: Adapter()), spy
    original_ids = uidoc.Selection.GetElementIds
    uidoc.ActiveView = Object(Id=900, Name='QA view')
    p = new_panel()
    try:
        p.open_feature('checks')
        assert p.CheckScope.SelectedItem.data == 'selection'
        for box in (p.CheckDuplicates, p.CheckOverlaps, p.CheckClashes, p.CheckWarnings):
            box.IsChecked = False
        uidoc.Selection.GetElementIds = lambda: ['A', 'B']
        p.RunModelChecks.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        start(p)
        scope, options = calls[-1]
        assert scope['scope'] == 'selection' and scope['keys'] == ['A', 'B'] and options['peers']
        assert p.target['node_id'] == 'model:A' and u'選取中的 2 個元件' in p.CheckNotes.Text
        # Highlighting a result selects its elements; an automatic recheck keeps the original sources.
        uidoc.Selection.GetElementIds = lambda: ['B']
        p.document_changed(None, Object(GetDocument=lambda: uidoc.Document))
        start(p)
        assert calls[-1][0]['keys'] == ['A', 'B']
        p.RunModelChecks.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        start(p)
        assert calls[-1][0]['keys'] == ['B']
        # Nothing selected: the pinned element (and its 選取 group) is the source.
        uidoc.Selection.GetElementIds = lambda: []
        p.RunModelChecks.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        start(p)
        assert calls[-1][0]['keys'] == ['A']
        p.CheckRangeMode.SelectedIndex = 2
        assert p.CheckScope.SelectedItem.data == 'all'
        empty = new_panel()
        empty.open_feature('checks')
        empty.RunModelChecks.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
        empty.refresh_tick(None, None)
        try:
            empty.handler.Execute(app)
        finally:
            empty.refresh_timer.Stop()
        assert u'請先在 Revit 選取要檢查的元件' in empty.CheckSummary.Text
    finally:
        uidoc.Selection.GetElementIds = original_ids
        view_module.RevitCheckAdapter, view_module.run_checks = previous
        p.refresh_timer.Stop()


def test_library_lists_every_case_and_marks_unsolved_ones():
    p = new_panel()
    p.set_target(node('A'), refresh=False)
    solved, open_case, other_type = current('A', 'RESOLVED'), current('B'), current('B', 'REPORTED')
    solved['solution'] = u'改用 1000 寬門型'
    open_case['id'], other_type['id'] = 2, 3
    other_type['draft']['graph']['nodes'][0]['properties'].update(type_id='999', category='Walls')
    p.display_cases([solved, open_case, other_type])
    p.open_feature('library')
    assert [i.data['id'] for i in p.Cases.Items] == [1, 2, 3]
    assert [i.status_label for i in p.Cases.Items] == [u'已提出解法', u'未解決', u'未解決']
    assert p.LibrarySummary.Text == u'3 個案例 · 2 個未解決' and u'Walls' in p.Cases.Items[2].meta
    p.CaseQuery.Text = u'未解決'
    assert [i.data['id'] for i in p.Cases.Items] == [2, 3]
    p.CaseQuery.Text = ''
    p.Cases.SelectedIndex = 1
    assert u'未解決：尚未記錄解法' in p.LibraryDetails.Text
    preview(p, 'case-library-all', 420, 760)
    # 相關案例: same element type, every problem type, unsolved ones marked.
    rows = related('A')
    rows['results'][0]['resolution_note'] = ''
    p.display_related(rows)
    assert p.case_issue_type() == 'ALL' and p.RelatedCases.Items[0].status_label == u'未解決'
    p.refresh_timer.Stop()


def suggest_click_and_flush(panel, control):
    control.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    flush_suggestion(panel)


def flush_suggestion(panel):
    for _ in range(10):
        if panel.handler.action:
            panel.handler.Execute(app)
        elif panel.suggest_callbacks:
            panel.suggest_callbacks.pop(0)()
        else:
            return
    raise AssertionError('Suggestion queue did not settle')


def test_suggestion_auto_repair_applies_complete_plan_and_stops_for_questions_or_target_change():
    forms = sys.modules['pyrevit'].forms
    original_selection = uidoc.Selection.GetElementIds
    def unexpected_confirmation(*args, **kwargs):
        raise AssertionError('Automatic repair was already authorized by its button')
    forms.alert = unexpected_confirmation
    calls, state = [], dict(questions=[], steps=True, opening=False)
    class Agent(object):
        def __init__(self, base, provider='claude'): pass
        def plan(self, target, sources, instruction, **options):
            calls.append('plan')
            planned = (dict(tool='create_wall_opening', arguments=dict(elementId=101, ductId=102,
                widthMm=400, heightMm=600, expectedGeometry='a'*64), reason=u'依 SOLVE 尺寸', source_case=None)
                if state['opening'] else dict(tool='move_element', arguments=dict(elementId=101, dx=100),
                reason=u'避開碰撞', source_case=None))
            return dict(summary=u'依現況修正', questions=state['questions'], problems=[], warnings=[],
                steps=[planned] if state['steps'] else [])
        def apply(self, steps, scope=None):
            calls.append(('apply', steps))
            return dict(summary=u'已移動', results=[dict(step=1, ok=True, message=u'MCP 已執行')])
        def cancel(self): calls.append('cancel')
    p = new_panel()
    p.agent_factory = Agent
    p.spawn = lambda worker: worker()
    p.suggest_callbacks = []
    p.dispatch = lambda callback: p.suggest_callbacks.append(callback)
    try:
        p.set_target(node('A'), refresh=False)
        p.SuggestAiMode.IsChecked = True
        p.suggest_mode_changed(None, None)
        p.SuggestPrompt.Text = u'參考記錄修正目前問題'
        suggest_click_and_flush(p, p.SuggestAutoFix)
        assert calls[0] == 'plan' and calls[1][0] == 'apply'
        assert calls[1][1][0]['arguments'] == dict(elementId=101, dx=100)
        assert u'已全部套用' in p.SuggestStatus.Text and u'MCP 已執行' in p.SuggestResults.Text
        assert p.SuggestAutoFix.IsEnabled and not p.SuggestApply.IsEnabled
        calls[:] = []
        state['opening'] = True
        uidoc.Selection.GetElementIds = lambda: ['A', 'B']
        suggest_click_and_flush(p, p.SuggestAutoFix)
        assert calls[1][1][0]['tool'] == 'create_wall_opening'
        assert calls[1][1][0]['arguments']['expectedGeometry'] == 'a'*64
        assert u'已全部套用' in p.SuggestStatus.Text and u'400' in p.SuggestResults.Text
        calls[:] = []
        state['questions'] = [u'目標沒有開口，請選擇修正方式。']
        suggest_click_and_flush(p, p.SuggestAutoFix)
        # Open questions withdraw only the automatic authorization; the steps stay for manual approval.
        assert calls == ['plan'] and u'未自動套用' in p.SuggestStatus.Text
        assert p.SuggestApply.IsEnabled and len(p.suggest_steps) == 1
        state.update(questions=[], steps=False)
        suggest_click_and_flush(p, p.SuggestAutoFix)
        assert calls == ['plan', 'plan'] and not p.SuggestApply.IsEnabled
        assert u'未產生有效步驟' in p.SuggestStatus.Text
        state['steps'] = True
        workers = []
        p.spawn = lambda worker: workers.append(worker)
        suggest_click_and_flush(p, p.SuggestAutoFix)
        assert not p.SuggestAutoFix.IsEnabled
        p.set_target(node('B'), refresh=False)
        assert calls[-1] == 'cancel'
        workers.pop()()
        assert not any(isinstance(c, tuple) and c[0] == 'apply' for c in calls)
        assert p.SuggestAutoFix.IsEnabled and not p.SuggestApply.IsEnabled
    finally:
        uidoc.Selection.GetElementIds = original_selection
        del forms.alert
        p.refresh_timer.Stop()


def test_suggestion_reads_sources_plans_confirms_applies_and_records():
    import casegraph.suggest_view as suggest_module
    calls, recorded, answers = [], [], []
    forms = sys.modules['pyrevit'].forms
    forms.alert = lambda *args, **options: answers.pop(0)
    previous_request = suggest_module.request
    suggest_module.request = lambda base, path, payload=None, method=None: recorded.append((path, payload))

    class Agent(object):
        def __init__(self, base, provider='claude'):
            self.base, self.provider = base, provider
            calls.append(('provider', provider))
        def plan(self, target, sources, instruction, **options):
            calls.append(('plan', target['node_id'], [s['case_id'] for s in sources], instruction))
            return dict(summary=u'依 RFI-3 加寬並反轉。', warnings=[u'RFI-4 未驗證'], questions=[], problems=[u'第 3 步缺少 typeId'],
                        steps=[dict(tool='modify_element_parameter', arguments=dict(elementId=101, parameterName='Width', value='3.2808'),
                                    reason=u'加寬至 1000 mm', source_case=3),
                               dict(tool='flip_element', arguments=dict(elementId=101, flipType='facing'), reason=u'反轉開向', source_case=3)])
        def apply(self, steps, scope=None):
            calls.append(('apply', [s['tool'] for s in steps]))
            return dict(summary=u'完成一步', results=[dict(step=1, ok=True), dict(step=2, ok=False, message=u'元素無法翻轉')])
        def cancel(self):
            calls.append(('cancel',))
    p = new_panel()
    p.agent_factory = Agent
    p.spawn = lambda worker: worker()
    p.suggest_callbacks = []
    p.dispatch = lambda callback: p.suggest_callbacks.append(callback)
    try:
        p.set_target(node('A'), refresh=False)
        p.refresh_queue.take()
        sources = [dict(case_id=3, title=u'門寬不足', verified=True, status='VERIFIED', solution=u'改用 1000 寬門型\n第二行', replayable_steps=2),
                   dict(case_id=4, title=u'門開向錯誤', verified=False, status='RESOLVED', solution=u'反轉開向', replayable_steps=0)]
        p.responses['/solutions/search'] = dict(results=sources, message='')
        p.open_feature('suggest')
        assert p.active_feature == 'suggest' and p.SubSuggest.IsChecked and p.TargetCard.Visibility == Visibility.Visible
        start(p)
        complete(p)
        search = next(call[1] for call in p.calls if call[0] == '/solutions/search')
        assert search['category'] == 'Doors' and search['type_name'] == '900 x 2100' and not search['verified_only']
        assert p.SuggestSources.Items.Count == 2 and p.SuggestSources.SelectedItems.Count == 2
        assert p.SuggestSources.Items[0].meta == u'RFI-3 · 已驗證 · 可重播 2 步' and p.SuggestSources.Items[0].excerpt == u'改用 1000 寬門型'
        p.SuggestSources.SelectedItems.Remove(p.SuggestSources.Items[1])
        p.SuggestPrompt.Text = u'門太窄，照之前的做法'
        assert p.SuggestProvider.Text == u'Claude'
        suggest_click_and_flush(p, p.SuggestRun)
        assert ('provider', 'claude') in calls
        assert calls[-1] == ('plan', 'model:A', [3], u'門太窄，照之前的做法')
        assert [i.caption for i in p.SuggestSteps.Items] == [u'1. 修改參數 · 元素 101 · Width = 3.2808（Revit 內部單位）', u'2. 翻轉 · 元素 101 · facing']
        assert u'⚠ RFI-4 未驗證' in p.SuggestSummary.Text and u'已略過：第 3 步' in p.SuggestSummary.Text
        assert p.SuggestApply.IsEnabled and p.SuggestRun.IsEnabled and not p.SuggestCancel.IsEnabled
        preview(p, 'suggestion-plan', 420, 900)
        answers.append(False)
        suggest_click_and_flush(p, p.SuggestApply)
        assert calls[-1][0] == 'plan' and u'沒有變更' in p.SuggestStatus.Text and p.SuggestApply.IsEnabled
        answers.append(True)
        suggest_click_and_flush(p, p.SuggestApply)
        assert calls[-1] == ('apply', ['modify_element_parameter', 'flip_element'])
        assert p.SuggestResults.Text.splitlines() == [u'✓ 1. 修改參數 · 元素 101 · Width = 3.2808（Revit 內部單位）', u'✗ 2. 翻轉 · 元素 101 · facing：元素無法翻轉']
        assert recorded[0][0] == '/cases/3/applications' and recorded[0][1]['outcome'] == 'PARTIAL'
        assert recorded[0][1]['element_ids'] == [101] and recorded[0][1]['steps'][1]['result']['ok'] is False
        assert p.SuggestStatus.Text.startswith(u'部分套用') and not p.SuggestApply.IsEnabled
        # A plan for another element is discarded when the target changes.
        suggest_click_and_flush(p, p.SuggestRun)
        assert p.SuggestApply.IsEnabled
        p.set_target(node('B'), refresh=False)
        assert p.SuggestSteps.Items.Count == 0 and not p.SuggestApply.IsEnabled and u'重新產生' in p.SuggestStatus.Text
        suggest_click_and_flush(p, p.SuggestRun)
        assert not p.SuggestApply.IsEnabled
        assert u'請選取至少一個過去案例' in p.SuggestStatus.Text
        assert ('provider', 'codex') not in calls
    finally:
        suggest_module.request = previous_request
        del forms.alert
        p.refresh_timer.Stop()


def test_suggestion_scope_uses_current_pair_instead_of_a_repaired_counterpart():
    from casegraph.check_view import CheckItem
    from casegraph.suggestion import repair_scope
    original_selection = uidoc.Selection.GetElementIds
    elements['C'] = Object(Id=103, UniqueId='C', Category=Object(Id=-2000014), record=node('C'), ViewSpecific=False)
    calls = []
    class Agent(object):
        def __init__(self, base, provider='claude'): pass
        def cancel(self): pass
        def plan(self, target, sources, instruction, **options):
            scope = repair_scope(target, options['members'])
            calls.append((scope, options['context']))
            fixed = scope['element_ids'] == [101, 102]
            return dict(summary=u'本次組合已修正' if fixed else u'修正第二組', already_resolved=fixed,
                steps=[] if fixed else [dict(tool='move_element', arguments=dict(elementId=103, dx=100),
                                             reason=u'第二組仍有碰撞', source_case=None)])
    p = new_panel()
    p.agent_factory = Agent
    p.spawn = lambda worker: worker()
    p.suggest_callbacks = []
    p.dispatch = lambda callback: p.suggest_callbacks.append(callback)
    try:
        p.set_target(node('A'), refresh=False, members=[node('B')])
        p.open_feature('suggest')
        p.SuggestAiMode.IsChecked = True
        p.suggest_mode_changed(None, None)
        p.SuggestPrompt.Text = u'只修正目前目標同選取物'
        p.refresh_queue.take()
        first_origin = dict(document='model', message=u'第一組', elements=[dict(unique_id='A'), dict(unique_id='B')])
        p.set_suggest_context(first_origin)
        uidoc.Selection.GetElementIds = lambda: ['B']
        suggest_click_and_flush(p, p.SuggestRun)
        assert calls[-1][0]['element_ids'] == [101, 102] and calls[-1][1]['message'] == u'第一組'
        assert u'本次目標與選取物' in p.SuggestStatus.Text and not p.SuggestApply.IsEnabled
        # Selecting another counterpart replaces the old pinned members and old issue context.
        uidoc.Selection.GetElementIds = lambda: ['C']
        suggest_click_and_flush(p, p.SuggestRun)
        assert calls[-1][0]['element_ids'] == [101, 103] and calls[-1][1] is None
        assert p.SuggestContext.Text == ''
        assert p.SuggestApply.IsEnabled and p.suggest_steps[0]['arguments']['elementId'] == 103
        assert 'ID 103' in p.SuggestScope.Text and 'ID 102' not in p.SuggestScope.Text
        # The matching selected model-check row supplies evidence for this exact pair.
        p.AutoHighlight.IsChecked = False
        p.check_report = dict(document='model', view_id='900', view_name='1F', range_mode='view', checked_at='2026-11-04 10:00:00')
        row = dict(id='2', check='clash', status='OVERLAP', records=[node('A'), node('C')], message=u'第二組仍有碰撞', advice=u'移動選取物')
        p.CheckResults.Items.Add(CheckItem(row))
        p.CheckResults.SelectedIndex = 0
        suggest_click_and_flush(p, p.SuggestRun)
        assert calls[-1][1]['result_id'] == '2' and calls[-1][1]['message'] == u'第二組仍有碰撞'
        p.show_suggest_context()
        assert u'第二組仍有碰撞' in p.SuggestContext.Text and u'第一組' not in p.SuggestContext.Text
        assert p.suggest_scope['element_ids'] == [101, 103]
        # Changing the explicit selected group invalidates the plan even with the same root.
        p.set_target(node('A'), refresh=False, members=[node('C')])
        assert not p.suggest_steps and not p.SuggestApply.IsEnabled
        assert u'選取物已變更' in p.SuggestStatus.Text
    finally:
        uidoc.Selection.GetElementIds = original_selection
        del elements['C']
        p.refresh_timer.Stop()


def test_suggestion_rows_apply_three_individual_changes_and_skip_completed_steps():
    import casegraph.suggest_view as suggest_module
    from System.Windows.Media import VisualTreeHelper
    forms = sys.modules['pyrevit'].forms
    old_alert = getattr(forms, 'alert', None)
    previous_request = suggest_module.request
    calls, recorded, confirmations = [], [], []
    forms.alert = lambda message, **options: confirmations.append(message) or True
    suggest_module.request = lambda base, path, payload=None, method=None: recorded.append((path, payload))
    state = dict(failure=False, exception=False)
    steps = [dict(tool='move_element', arguments=dict(elementId=ident, dx=dx, dy=dy, dz=0),
                  reason=u'移動開口至風管位置', source_case=3)
             for ident, dx, dy in [(884434, 100, 600), (884441, 600, 600.5), (884455, 100, 625)]]

    class Agent(object):
        def __init__(self, base, provider='claude'): pass
        def cancel(self): pass
        def apply(self, requested, scope=None):
            assert scope == dict(document='model', target_id=101, element_ids=[101, 884434, 884441, 884455])
            calls.append(copy.deepcopy(requested))
            assert not any(item.can_apply for item in p.SuggestSteps.Items)
            # The executor's own model changes must preserve pending rows.
            p.document_changed(None, Object(GetDocument=lambda: uidoc.Document))
            p.suggest_apply(None, None)  # A second click while executing cannot launch another run.
            if state['exception']:
                raise ValueError('MCP reply lost; outcome unknown')
            return dict(summary=u'已執行', results=[dict(step=i, ok=not state['failure'], message=u'成功移動元素')
                        for i, step in enumerate(requested, 1)])

    def find_button(visual):
        if isinstance(visual, Button) and visual.Name == 'ApplySuggestionStep':
            return visual
        for i in range(VisualTreeHelper.GetChildrenCount(visual)):
            found = find_button(VisualTreeHelper.GetChild(visual, i))
            if found is not None:
                return found

    def row_button(index):
        p.SuggestSteps.ScrollIntoView(p.SuggestSteps.Items[index-1])
        p.UpdateLayout()
        container = p.SuggestSteps.ItemContainerGenerator.ContainerFromIndex(index-1)
        assert container is not None
        button = find_button(container)
        assert button is not None and button.Tag.index == index
        return button

    def show_plan():
        p.show_suggestion(p.target, dict(summary=u'分別修正三個開口', steps=copy.deepcopy(steps),
            scope=dict(document='model', target_id=101, element_ids=[101, 884434, 884441, 884455])))
        p.UpdateLayout()

    p = new_panel()
    p.agent_factory = Agent
    p.spawn = lambda worker: worker()
    p.suggest_callbacks = []
    p.dispatch = lambda callback: p.suggest_callbacks.append(callback)
    try:
        p.set_target(node('A'), refresh=False)
        p.open_feature('suggest')
        p.refresh_queue.take()
        show_plan()
        stale_button = row_button(2)
        # Every row has its own real WPF button, even when chosen out of order.
        for position, index in enumerate([2, 1, 3], 1):
            button = row_button(index)
            assert button.IsEnabled
            suggest_click_and_flush(p, button)
            assert calls[-1] == [steps[index-1]]
            assert len(calls) == position and len(p.suggest_steps) == 3
            assert p.SuggestSteps.Items[index-1].state == u'已套用'
            assert not row_button(index).IsEnabled
            assert len(p.pending_suggestion_indices()) == 3-position
            assert step_caption_for_test(index, steps[index-1]) in confirmations[-1]
            assert recorded[-1][1]['steps'][0]['plan_step'] == index
            assert recorded[-1][1]['element_ids'] == [steps[index-1]['arguments']['elementId']]
        assert p.SuggestStatus.Text.startswith(u'已全部套用') and not p.SuggestApply.IsEnabled
        assert len(p.SuggestResults.Text.splitlines()) == 3
        p.suggest_apply_step(stale_button, None)
        p.suggest_apply(None, None)
        assert len(calls) == 3, 'Completed or stale rows must never repeat a move'
        preview(p, 'suggestion-individual-complete', 460, 1000)
        # Applying all after one individual change sends only the remaining steps.
        show_plan()
        suggest_click_and_flush(p, row_button(2))
        preview(p, 'suggestion-individual-pending', 460, 1000)
        suggest_click_and_flush(p, p.SuggestApply)
        assert calls[-1] == [steps[0], steps[2]]
        assert [s['plan_step'] for s in recorded[-1][1]['steps']] == [1, 3]
        assert len(p.suggest_results) == 3 and not p.pending_suggestion_indices()
        # Failure and lost replies block replay until the model is checked again.
        for failure, exception in [(True, False), (False, True)]:
            show_plan()
            state.update(failure=failure, exception=exception)
            suggest_click_and_flush(p, row_button(1))
            count = len(calls)
            assert p.suggest_blocked and not p.SuggestApply.IsEnabled
            assert not any(item.can_apply for item in p.SuggestSteps.Items)
            p.suggest_apply(None, None)
            assert len(calls) == count
        # External edits still invalidate the plan between individual applications.
        state.update(failure=False, exception=False)
        show_plan()
        stale_button = row_button(3)
        p.document_changed(None, Object(GetDocument=lambda: uidoc.Document))
        p.suggest_apply_step(stale_button, None)
        assert not p.suggest_steps and not p.SuggestApply.IsEnabled
        assert len(calls) == count
    finally:
        suggest_module.request = previous_request
        if old_alert is None:
            del forms.alert
        else:
            forms.alert = old_alert
        p.refresh_timer.Stop()


def step_caption_for_test(index, step):
    return u'{}. 移動 · 元素 {}'.format(index, step['arguments']['elementId'])


def test_suggestion_modes_separate_inputs_and_invalidate_previous_plans():
    calls = []
    original_selection = uidoc.Selection.GetElementIds
    uidoc.Selection.GetElementIds = lambda: ['A', 'B']
    class Agent(object):
        def __init__(self, base, provider='claude'): pass
        def cancel(self): pass
        def plan(self, target, sources, instruction, **options):
            calls.append((sources, instruction, options))
            return dict(summary=u'修正間距', warnings=[], questions=[], problems=[],
                        steps=[dict(tool='move_element', arguments=dict(elementId=101, dx=100),
                                    reason=u'依元素現況及指示', source_case=None)])
    p = new_panel()
    p.agent_factory = Agent
    p.spawn = lambda worker: worker()
    p.suggest_callbacks = []
    p.dispatch = lambda callback: p.suggest_callbacks.append(callback)
    try:
        p.set_target(node('A'), refresh=False, members=[node('B')])
        p.open_feature('suggest')
        p.refresh_timer.Stop()
        p.set_suggest_context(dict(message=u'間距不足', advice=u'調整管線位置'))
        p.SuggestPrompt.Text = u'增加間距 100 mm'
        suggest_click_and_flush(p, p.SuggestRun)
        assert not calls and u'過去案例' in p.SuggestStatus.Text
        p.show_suggest_sources([dict(case_id=3, title=u'舊案例', verified=True, solution=u'移動 300 mm')])
        p.SuggestAiMode.IsChecked = True
        p.suggest_mode_changed(None, None)
        assert p.SuggestCasesPanel.Visibility == Visibility.Collapsed
        p.SuggestPrompt.Text = ''
        suggest_click_and_flush(p, p.SuggestRun)
        assert not calls and u'請輸入' in p.SuggestStatus.Text
        p.SuggestPrompt.Text = u'增加間距 100 mm'
        suggest_click_and_flush(p, p.SuggestRun)
        assert calls[-1][0] == [] and calls[-1][2]['mode'] == 'ai'
        assert calls[-1][2]['context']['message'] == u'間距不足'
        assert calls[-1][2]['members'][0]['source_element_id'] == 'B'
        assert p.SuggestApply.IsEnabled
        preview(p, 'suggestion-ai-mode', 460, 1000)
        p.SuggestPrompt.Text = u'增加間距 150 mm'
        assert not p.SuggestApply.IsEnabled and not p.suggest_steps
        suggest_click_and_flush(p, p.SuggestRun)
        assert p.SuggestApply.IsEnabled
        p.SuggestCaseMode.IsChecked = True
        p.suggest_mode_changed(None, None)
        assert not p.SuggestApply.IsEnabled and p.SuggestCasesPanel.Visibility == Visibility.Visible
        suggest_click_and_flush(p, p.SuggestRun)
        assert calls[-1][0][0]['case_id'] == 3 and calls[-1][2]['mode'] == 'cases'
        preview(p, 'suggestion-case-mode', 460, 1000)
        p.SuggestSources.SelectedItems.Clear()
        assert not p.SuggestApply.IsEnabled
        p.SuggestAiMode.IsChecked = True
        p.suggest_mode_changed(None, None)
        suggest_click_and_flush(p, p.SuggestRun)
        p.document_changed(None, Object(GetDocument=lambda: uidoc.Document))
        assert not p.SuggestApply.IsEnabled and u'模型已變更' in p.SuggestStatus.Text
        before = len(calls)
        uidoc.Document.identity = 'other-model'
        suggest_click_and_flush(p, p.SuggestRun)
        assert len(calls) == before and u'目前文件已變更' in p.SuggestStatus.Text
    finally:
        uidoc.Selection.GetElementIds = original_selection
        uidoc.Document.identity = 'model'
        p.refresh_timer.Stop()


def test_shaft_adapter_uses_real_core_and_keeps_linked_ids_separate():
    from casegraph import shaft_check as sc
    from casegraph.shaft_issue import shaft_issue
    from casegraph.serialization import dumps
    from System import String
    from System.Collections.Generic import List
    core = os.path.join(sc.ROOT, 'ShaftChecker-main', 'src', 'ShaftChecker.Revit', 'bin', 'Release', '2022', 'ShaftChecker.Core.dll')
    clr.AddReferenceToFileAndPath(core)
    from ShaftChecker.Core.Models import ShaftInfo, WallInfo
    from ShaftChecker.Core.Config import RuleConfig
    from ShaftChecker.Core.Rules import RuleEngine
    cfg = RuleConfig.Load(os.path.join(sc.ROOT, 'ShaftChecker-main', 'src', 'ShaftChecker.Revit', 'config', 'shaft-rules.json'))
    cfg.EnabledRules = List[String](['A1', 'P1'])
    shafts = List[ShaftInfo]()
    for source in (None, 'MEP link'):
        shaft = ShaftInfo()
        shaft.RoomId, shaft.RoomKey, shaft.Name, shaft.SourceName = 1, (source or 'host')+':room', 'PS-1', source
        shaft.MepModelFound = False
        wall = WallInfo()
        wall.Id, wall.TypeName, wall.FireRatingRaw = 2, 'Wall', '30 min'
        shaft.Walls.Add(wall)
        shafts.Add(shaft)
    def make_element(identity, number):
        uid = identity+str(number)
        record = node(uid)
        record['source_document'], record['node_id'] = identity, identity+':'+uid
        return Object(Id=number, UniqueId=uid, record=record)
    def make_doc(identity, items):
        by_key = {}
        for e in items:
            by_key[e.Id] = by_key[e.UniqueId] = e
        return Object(identity=identity, GetElement=lambda key: by_key.get(key), Application=Object(VersionNumber='2022'))
    linked_doc = make_doc('linked', [make_element('linked', 1), make_element('linked', 2)])
    class Link(object):
        Id, UniqueId, Name = 9, 'host9', 'MEP link'
        record = make_element('host', 9).record
        def GetLinkDocument(self):
            return linked_doc
    link = Link()
    host_doc = make_doc('host', [make_element('host', 1), make_element('host', 2), link])
    fake_uidoc = Object(Document=host_doc, ActiveView=Object(Name='1F'))
    class Collector(object):
        Report = Object(LinksTotal=2, LinksLoaded=1, Spaces=[], Unplaced=Object(Count=0), OrphanOpenings=Object(Count=0))
        def __init__(self, doc, config):
            pass
        def Collect(self):
            return shafts
    old_load, old_collector, old_link = sc.load_engine, getattr(db, 'FilteredElementCollector', None), getattr(db, 'RevitLinkInstance', None)
    sc.load_engine = lambda version: (Collector, Object(Load=lambda path: cfg), RuleEngine)
    db.FilteredElementCollector = lambda doc: Object(OfClass=lambda kind: [link])
    db.RevitLinkInstance = Link
    try:
        report = sc.run_check(fake_uidoc)
        assert report['shaft_count'] == 2 and report['rules_run'] == ['A1', 'P1']
        assert len(report['results']) == 4 and report['pass_count'] == 0
        host, linked = report['results'][0], report['results'][2]
        assert host['room_id'] == linked['room_id'] == '1' and linked['room_name'] == 'PS-1'
        assert host['status'] == linked['status'] == 'Violation'
        assert host['records'][0]['source_element_id'] == 'host1'
        assert linked['records'][0]['source_element_id'] == 'host9'
        assert [(r['document'], r['element_id']) for r in linked['source_refs']] == [('linked', '1'), ('linked', '2')]
        assert report['results'][1]['status'] == 'DataMissing'
        assert u'未載入' in report['warnings'][0]
        sc.validate_result(fake_uidoc, report, linked)
        record = shaft_issue(report, linked)
        assert 'SHAFT_CHECK' in dumps(record)  # Real CLR doubles/strings must serialize.
        original_get = linked_doc.GetElement
        linked_doc.GetElement = lambda key: None
        try:
            sc.validate_result(fake_uidoc, report, linked)
            raise AssertionError('Deleted linked element accepted')
        except ValueError:
            pass
        linked_doc.GetElement = original_get
        db.FilteredElementCollector = lambda doc: Object(OfClass=lambda kind: [link, Link()])
        cfg.EnabledRules = List[String](['A1', 'P1'])
        ambiguous = sc.run_check(fake_uidoc)['results'][2]
        assert ambiguous['unresolved'] and not ambiguous['records']
        assert ambiguous['room_id'] == '1' and not ambiguous['room_unique_id']
        from casegraph.shaft_csv import write_problem_csv
        csv_path = os.path.join(os.environ['CASEGRAPH_RULES_DIR'], 'core-findings.csv')
        try:
            assert write_problem_csv(csv_path, report) == 4  # Real CLR measurements/strings.
            with open(csv_path, 'rb') as stream:
                assert u'防火'.encode('utf-8') in stream.read()
        finally:
            if os.path.exists(csv_path):
                os.remove(csv_path)
    finally:
        sc.load_engine, db.FilteredElementCollector, db.RevitLinkInstance = old_load, old_collector, old_link


def test_shaft_window_open_close_does_not_show_panel_or_touch_revit_subscriptions():
    from casegraph import shaft_window as sw, bootstrap
    from casegraph.navigation import PANEL_INSTANCE_KEY
    forms = sys.modules['pyrevit'].forms
    host_app = sys.modules['pyrevit'].HOST_APP
    envvars = sys.modules['pyrevit.coreutils'].envvars
    original = (sw.ShaftWindow, bootstrap.register, bootstrap.open_panel,
                envvars.get_pyrevit_env_var, envvars.set_pyrevit_env_var,
                forms.open_dockable_panel, host_app.app)
    p = new_panel()
    p.open_feature('library')
    p.IssueTitle.Text, p.Description.Text = u'保留標題', u'保留內容'
    p.refresh_timer.Stop()
    store, registered, shown, ran, errors = {PANEL_INSTANCE_KEY: p}, [], [], [], []
    p.log_error = lambda error: errors.append(unicode(error))
    envvars.get_pyrevit_env_var = lambda key: store.get(key)
    envvars.set_pyrevit_env_var = lambda key, value: store.update({key:value})
    bootstrap.register = lambda: registered.append(True)
    bootstrap.open_panel = lambda feature='home': shown.append(feature)
    forms.open_dockable_panel = lambda pane: shown.append(pane)

    class RevitEvent(object):
        additions = 0
        def __iadd__(self, handler):
            self.additions += 1
            return self
        def __isub__(self, handler):
            # Match Revit's API-context restriction, absent in the old WPF mocks.
            raise RuntimeError('Cannot remove a Revit event in a modeless WPF callback')
    revit_event = RevitEvent()
    host_app.app = Object(DocumentChanged=revit_event)

    class HiddenWindow(original[0]):
        def __init__(self, panel):
            original[0].__init__(self, panel)
            # Show/Close real WPF windows without displaying them on the desktop.
            self.ShowActivated, self.ShowInTaskbar, self.Opacity = False, False, 0
    sw.ShaftWindow = HiddenWindow
    window = None
    try:
        for _ in range(3):
            sw.open_window()
            window = store[sw.WINDOW_KEY]
            assert window.IsVisible and p.shaft_window is window
            sw.open_window()
            assert store[sw.WINDOW_KEY] is window  # Reuse the open window.
            assert not shown and p.active_feature == 'library'
            assert p.IssueTitle.Text == u'保留標題' and p.Description.Text == u'保留內容'
            window.report = dict(stale=False)
            p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
            assert window.report['stale']  # Existing panel event forwards changes.
            window.execute(lambda uidoc: ran.append(True), u'排隊測試')
            assert p.handler.action is not None
            window.Close()
            assert window._closed and window.panel is None and window.report is None
            assert p.shaft_window is None and store[sw.WINDOW_KEY] is None
            window.closed(None, None)  # Cleanup is idempotent.
            window.model_changed(None, None)  # A late callback cannot touch disposed UI.
            p.handler.Execute(app)
            assert not ran and not p.busy  # A queued check cannot run after close.
            p.document_changed(None, Object(GetDocument=lambda:uidoc.Document))
            p.refresh_timer.Stop()
        assert revit_event.additions == 0 and not errors
        assert len(registered) == 6
    finally:
        if window is not None and not window._closed:
            window.Close()
        (sw.ShaftWindow, bootstrap.register, bootstrap.open_panel,
         envvars.get_pyrevit_env_var, envvars.set_pyrevit_env_var,
         forms.open_dockable_panel, host_app.app) = original
        p.refresh_timer.Stop()


def test_shaft_window_transfers_preserves_unsaved_and_saves_evidence():
    from casegraph import shaft_window as sw
    from casegraph.shaft_issue import RULES
    forms = sys.modules['pyrevit'].forms
    answers, opened = [], []
    old_alert = getattr(forms, 'alert', None)
    old_open = getattr(forms, 'open_dockable_panel', None)
    forms.alert = lambda *a, **k: answers.pop(0)
    forms.open_dockable_panel = lambda pane: opened.append(pane)
    previous_check, previous_validate = sw.run_check, sw.validate_result
    store = {}
    sys.modules['pyrevit.coreutils'].envvars.get_pyrevit_env_var = lambda key: store.get(key)
    sys.modules['pyrevit.coreutils'].envvars.set_pyrevit_env_var = lambda key, value: store.update({key:value})
    report = dict(document='model', checked_at='2026-11-03 15:00:00', view_name='1F', rules_run=list(RULES),
                  thresholds={'minFireRatingHours': 1.0}, warnings=[], stale=False, shaft_count=1, pass_count=10)
    row = dict(id='1', rule_id='A1', rule_name=u'牆防火時效', group='fire', status='Violation',
               shaft_label='1F PS-1', shaft_key='A', room_unique_id='A', source=None, nature=u'法規', reference=u'規則原始依據',
               message=u'牆防火時效不足', advice=u'調整牆型', actual=.5, required=1., unit='h',
               records=[node('A'), node('B')], source_refs=[], unresolved=False)
    report['results'] = [row]
    sw.run_check = lambda uidoc: copy.deepcopy(report)
    def validate(uidoc, report, row):
        if report['stale'] or uidoc.Document.identity != report['document']:
            raise ValueError(u'請重新檢查')
    sw.validate_result = validate
    class TestWindow(sw.ShaftWindow):
        IsVisible = True
    p = new_panel()
    w = TestWindow(p)
    try:
        w.check_click(None, None)
        p.handler.Execute(app)
        assert w.Findings.Items.Count == 1 and u'1 筆需處理' in w.Summary.Text
        w.Findings.SelectedIndex = 0
        assert w.Record.IsEnabled and u'實測：0.5' in w.Details.Text
        assert u'調整牆型' not in w.Details.Text and w.Suggest.IsEnabled
        p.IssueTitle.Text, p.Description.Text = u'未儲存標題', u'未儲存內容'
        w.suggest_click(None, None)
        p.handler.Execute(app)
        assert p.active_feature == 'suggest' and u'調整牆型' in p.SuggestContext.Text
        assert p.current_suggest_context()['rule_id'] == 'A1'
        assert p.IssueTitle.Text == u'未儲存標題' and p.Description.Text == u'未儲存內容'
        opened[:] = []
        answers.append(False)
        w.record_click(None, None)
        p.handler.Execute(app)
        assert p.IssueTitle.Text == u'未儲存標題' and not opened
        answers.append(True)
        w.record_click(None, None)
        p.handler.Execute(app)
        assert p.active_feature == 'issue' and p.issue_origin['kind'] == 'SHAFT_CHECK'
        assert p.target['source_element_id'] == 'A' and p.target_members[0]['source_element_id'] == 'B'
        assert p.IssueType.SelectedItem.data == 'DESIGN_CONFLICT' and p.Priority.SelectedItem.data == 'High'
        assert u'管道間檢查' in p.IssueOrigin.Text and opened
        def create(payload):
            return dict(id=15, revision=1, status='DRAFT', solution='', reviewer='', analyses=[], draft=copy.deepcopy(payload))
        p.responses.update({'/elements': node('A'), '/cases': create})
        p.save_click(None, None)
        p.handler.Execute(app)
        complete(p)
        assert p.current['draft']['origin']['rule_id'] == 'A1'
        assert extracted_findings[-1] == [(102, False, '1')]
        assert not p.issue_form_unsaved()
        w.model_changed(None, None)
        assert w.report['stale'] and not w.Record.IsEnabled and not w.Suggest.IsEnabled
        before = copy.deepcopy(p.issue_origin)
        w.record_click(None, None)
        p.handler.Execute(app)
        assert p.issue_origin == before and u'重新檢查' in w.Status.Text
        w.check_click(None, None)
        p.handler.Execute(app)
        w.Findings.SelectedIndex = 0
        assert w.Record.IsEnabled
        uidoc.Document.identity = 'another-model'
        w.record_click(None, None)
        p.handler.Execute(app)
        assert p.issue_origin == before and u'重新檢查' in w.Status.Text
    finally:
        uidoc.Document.identity = 'model'
        sw.run_check, sw.validate_result = previous_check, previous_validate
        forms.alert, forms.open_dockable_panel = old_alert, old_open
        w.Close()
        p.refresh_timer.Stop()


def test_shaft_csv_exports_all_findings_cancels_and_rejects_changed_reports():
    import csv
    from casegraph import shaft_window as sw
    forms = sys.modules['pyrevit'].forms
    previous_save, previous_check = getattr(forms, 'save_file', None), sw.run_check
    row = dict(id='1', rule_id='A1', group='fire', status='Violation', shaft_label=u'1F 管道間',
               message=u'牆型 "A,B"\n防火時效不足', actual=0, required=1., unresolved=True)
    report = dict(document='model', checked_at='2026-11-04 15:00:00', rules_run=['A1', 'P1'],
                  warnings=[], stale=False, shaft_count=1, pass_count=2,
                  results=[row, dict(row, id='2', status='Warning'),
                           dict(row, id='3', rule_id='P1', group='mep', status='DataMissing')])
    sw.run_check = lambda uidoc: copy.deepcopy(report)
    answers, dialogs, errors = [], [], []
    def save(**kwargs):
        dialogs.append(kwargs)
        return answers.pop(0)
    forms.save_file = save
    class TestWindow(sw.ShaftWindow):
        IsVisible = True
    path = os.path.join(os.environ['CASEGRAPH_RULES_DIR'], u'管道間問題.csv')
    p = new_panel()
    p.log_error = lambda error: errors.append(unicode(error))
    w = TestWindow(p)
    try:
        assert not w.ExportCsv.IsEnabled
        w.check_click(None, None)
        p.handler.Execute(app)
        assert w.ExportCsv.IsEnabled and w.Findings.SelectedItem is None
        w.Findings.SelectedIndex = 2
        assert not w.Record.IsEnabled and w.ExportCsv.IsEnabled  # Unresolved can still be exported.
        answers.append(None)
        w.export_csv_click(None, None)
        p.handler.Execute(app)
        assert not os.path.exists(path) and u'已取消' in w.Status.Text
        answers.append(path)
        w.export_csv_click(None, None)
        p.handler.Execute(app)
        assert not errors and u'已匯出 3 筆' in w.Status.Text, errors
        assert dialogs[-1]['default_name'] == u'管道間問題_2026-11-04_15-00-00.csv'
        with open(path, 'rb') as stream:
            payload = stream.read()
        assert payload.startswith('\xef\xbb\xbf')
        parsed = list(csv.reader(payload[3:].splitlines(True)))
        assert len(parsed) == 4 and all(len(r) == len(parsed[0]) for r in parsed)
        assert row['message'].encode('utf-8') in parsed[1]
        count = len(dialogs)
        w.model_changed(None, None)
        assert not w.ExportCsv.IsEnabled
        w.export_csv_click(None, None)
        p.handler.Execute(app)
        assert len(dialogs) == count and u'重新' in w.Status.Text
        w.check_click(None, None)
        p.handler.Execute(app)
        assert w.ExportCsv.IsEnabled
        uidoc.Document.identity = 'other-model'
        w.export_csv_click(None, None)
        p.handler.Execute(app)
        assert len(dialogs) == count and u'切換文件' in w.Status.Text
        uidoc.Document.identity = 'model'
        with open(path, 'rb') as stream:
            assert stream.read() == payload  # Refused export preserves an earlier file.
        report['results'] = []
        w.check_click(None, None)
        p.handler.Execute(app)
        assert not w.ExportCsv.IsEnabled
        w.export_csv_click(None, None)
        p.handler.Execute(app)
        assert len(dialogs) == count and u'沒有可匯出' in w.Status.Text
    finally:
        uidoc.Document.identity = 'model'
        forms.save_file, sw.run_check = previous_save, previous_check
        w.Close()
        p.refresh_timer.Stop()
        if os.path.exists(path):
            os.remove(path)


def test_shaft_grouping_filters_preserve_selection_evidence_and_all_csv_rows():
    from casegraph import shaft_window as sw
    forms = sys.modules['pyrevit'].forms
    old_check, old_save = sw.run_check, getattr(forms, 'save_file', None)
    old_write = sw.write_problem_csv
    row = dict(id='1', rule_id='A1', rule_name=u'牆防火時效', group='fire', status='Violation',
               shaft_label='10F PS-1', shaft_key='A', room_unique_id='A', source=None,
               nature=u'法規', reference=u'原規則依據', message=u'牆防火時效不足', advice=u'調整牆型',
               actual=.5, required=1., unit='h', records=[node('A'), node('B')], source_refs=[],
               unresolved=False, level_name='10F', level_elevation_mm=30000)
    report = dict(document='model', checked_at='2026-11-04 16:00:00', view_name='1F',
                  rules_run=['A1', 'A7', 'P1'], thresholds={}, warnings=[], stale=False,
                  shaft_count=4, pass_count=10,
                  results=[row,
                           dict(row, id='2', level_name='B2', level_elevation_mm=-6000, group='mep', rule_id='P1', status='Warning'),
                           dict(row, id='3', level_name='2F', level_elevation_mm=6000, group='maint', rule_id='A7', status='DataMissing'),
                           dict(row, id='4', level_name='2F', level_elevation_mm=6000),
                           dict(row, id='5', level_name='', level_elevation_mm=0, unresolved=True)])
    checks, exports, errors = [], [], []
    def check(uidoc):
        checks.append(True)
        return copy.deepcopy(report)
    sw.run_check = check
    forms.save_file = lambda **kwargs: 'unused.csv'
    sw.write_problem_csv = lambda path, data: exports.append(data) or len(data['results'])
    class TestWindow(sw.ShaftWindow):
        IsVisible = True
    p = new_panel()
    p.log_error = lambda error: errors.append(unicode(error))
    w = TestWindow(p)
    try:
        assert w.GroupBy.SelectedIndex == 0 and not w.GroupFilter.IsEnabled
        w.GroupBy.SelectedIndex = 1  # Safe before the first check.
        assert w.Findings.Items.Count == 0
        w.check_click(None, None)
        p.handler.Execute(app)
        assert not errors and w.Findings.Items.Count == 5
        assert [g.Name for g in w._view.Groups] == ['B2', '2F', '10F', u'未指定樓層']
        assert [g.ItemCount for g in w._view.Groups] == [1, 2, 1, 1]
        w.Findings.SelectedIndex = 2  # Finding 4, on 2F.
        chosen = w.Findings.SelectedItem.data
        details = w.Details.Text
        assert chosen['id'] == '4' and w.Record.IsEnabled
        w.GroupBy.SelectedIndex = 2
        assert [g.Name for g in w._view.Groups] == [u'防火時效及區劃', u'維修門與維修空間', u'管線分隔與間距']
        assert w.Findings.SelectedItem.data is chosen and w.Details.Text == details
        w.GroupBy.SelectedIndex = 3
        assert [g.Name for g in w._view.Groups] == [u'違規', u'警告', u'資料不足']
        assert w.Findings.SelectedItem.data is chosen
        w.GroupFilter.SelectedIndex = 2  # Warnings only: selected violation leaves the view.
        assert w.Findings.Items.Count == 1 and w.Findings.Items[0].data['id'] == '2'
        assert w.Findings.SelectedItem is None and not w.Record.IsEnabled and not w.Locate.IsEnabled
        assert w.Details.Text == '' and u'1 / 5' in w.ResultCount.Text
        assert w.ExportCsv.IsEnabled
        w.export_csv_click(None, None)
        p.handler.Execute(app)
        assert len(exports[0]['results']) == 5  # Export remains the full report.
        w.GroupBy.SelectedIndex = 1
        w.GroupFilter.SelectedIndex = 2  # Only 2F, with two separate findings.
        assert [i.data['id'] for i in w.Findings.Items] == ['3', '4']
        w.Findings.SelectedIndex = 1
        w.model_changed(None, None)
        w.GroupBy.SelectedIndex = 2
        assert not w.Record.IsEnabled and not w.ExportCsv.IsEnabled and w.report['stale']
        assert len(checks) == 1  # Group/filter changes never run Revit checks.
        w.GroupBy.SelectedIndex = 0
        assert not w._view.Groups and w.Findings.Items.Count == 5
        assert not w.GroupFilter.IsEnabled
        report['results'] = []
        w.check_click(None, None)
        p.handler.Execute(app)
        w.GroupBy.SelectedIndex = 3
        assert w.Findings.Items.Count == 0 and w.GroupFilter.Items.Count == 1
        assert not w.ExportCsv.IsEnabled and u'0 / 0' in w.ResultCount.Text
        assert not errors
        def fail(uidoc):
            raise ValueError(u'檢查失敗測試')
        sw.run_check = fail
        w.check_click(None, None)
        p.handler.Execute(app)
        assert w.report is None and w.Findings.Items.Count == 0
        assert w.GroupFilter.Items.Count == 1 and not w.GroupFilter.IsEnabled
        assert u'0 / 0' in w.ResultCount.Text and not w.ExportCsv.IsEnabled
    finally:
        sw.run_check, forms.save_file, sw.write_problem_csv = old_check, old_save, old_write
        w.Close()
        p.refresh_timer.Stop()


def main():
    tests = sorted((key, value) for key, value in globals().items() if key.startswith('test_'))
    for name, test in tests:
        test()
        print('PASS '+name)
    print('{} real IronPython/WPF refresh scenarios passed.'.format(len(tests)))


try:
    main()
except Exception:
    traceback.print_exc()
    raise
