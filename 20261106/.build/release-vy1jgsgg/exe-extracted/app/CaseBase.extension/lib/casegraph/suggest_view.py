# -*- coding: utf-8 -*-
"""修改建議 page: past solutions + the user's instruction -> Claude -> Revit MCP.

Claude runs in its own background job so the panel's ExternalEvent work and other
requests keep running. The user chooses either preview or authorized automatic repair.
"""
import threading
from copy import deepcopy
from System import Action
from System.Windows import Visibility
from Autodesk.Revit import DB
from pyrevit import forms
from casegraph.agent import AgentRunner
from casegraph.client import request
from casegraph.extractor import document_id, id_value
from casegraph.element_index import snapshot
from casegraph.check_issue import check_issue
from casegraph.suggestion import step_caption, outcome, repair_scope, scope_problems


class SourceItem(object):
    def __init__(self, data):
        self.data = data
        self.title = data['title']
        self.meta = u'RFI-{} · {} · 可重播 {} 步'.format(data['case_id'], u'已驗證' if data['verified'] else u'未驗證',
                                                     data.get('replayable_steps', 0))
        self.excerpt = (data.get('solution') or u'').strip().split(u'\n')[0][:120]
        self.label = u'{}\n{}'.format(self.title, self.meta)


class StepItem(object):
    def __init__(self, index, step, result=None, enabled=True, blocked=False):
        self.index = index
        self.data = step
        self.caption = step_caption(index, step)
        self.reason = step['reason']
        self.label = self.caption
        self.can_apply = enabled and result is None and not blocked
        self.state = (u'已套用' if result.get('ok') else u'未完成，請重新檢查') if result is not None else (
            u'請檢查模型並重新產生建議' if blocked else u'待套用')


class SuggestView(object):
    agent_factory = AgentRunner

    def initialize_suggest(self):
        self.suggest_steps, self.suggest_job, self.suggest_target = [], None, None
        self.suggest_kind = None
        self.suggest_context = None
        self.suggest_results, self.suggest_blocked = {}, False
        self.suggest_scope = None
        self.suggest_plan_context = None

    def pending_suggestion_indices(self):
        return [] if self.suggest_blocked else [i for i in range(1, len(self.suggest_steps)+1) if i not in self.suggest_results]

    def render_suggestion_steps(self):
        enabled = self.suggest_job is None
        self.SuggestSteps.Items.Clear()
        for index, step in enumerate(self.suggest_steps, 1):
            self.SuggestSteps.Items.Add(StepItem(index, step, self.suggest_results.get(index), enabled, self.suggest_blocked))
        self.SuggestApply.IsEnabled = enabled and bool(self.pending_suggestion_indices())

    def suggestion_mode(self):
        return 'ai' if self.SuggestAiMode.IsChecked else 'cases'

    def suggest_mode_changed(self, sender, args):
        if not self.ready or self.suggest_kind == 'apply':
            return
        self.invalidate_suggestion(u'建議來源已變更，請重新產生建議。')
        ai = self.suggestion_mode() == 'ai'
        self.SuggestCasesPanel.Visibility = Visibility.Collapsed if ai else Visibility.Visible
        self.SuggestModeHint.Text = (u'讀取目前元素，結合你的問題與限制，由 AI 提出修改步驟。' if ai else
                                     u'選取過去解法，依目前元素狀態轉換成修改步驟。')
        if not ai and self.target:
            self.schedule_refresh('suggest')

    def suggest_inputs_changed(self, sender, args):
        if not self.ready or self.suggest_kind == 'apply':
            return
        if sender is self.SuggestSources and self.suggestion_mode() == 'ai':
            return
        if self.suggest_job is not None or self.suggest_steps:
            self.invalidate_suggestion(u'指示或參考案例已變更，請重新產生建議。')

    def set_suggest_context(self, origin):
        self.invalidate_suggestion(u'已帶入檢查問題，請選擇建議來源。')
        self.suggest_context = deepcopy(origin)
        self.show_suggest_context()

    def current_suggest_context(self):
        origin = self.suggest_context or (self.issue_origin if self.origin_matches_target() else None)
        return deepcopy(origin)

    def capture_suggestion_targets(self, uidoc, target):
        """Capture the current selection on the Revit thread, before Claude starts."""
        def read(element):
            record = deepcopy(snapshot(uidoc.Document, element))
            record['properties']['element_id'] = id_value(element.Id)
            return record
        root = read(uidoc.Document.GetElement(target['source_element_id']))
        selection = list(uidoc.Selection.GetElementIds())
        if not selection and any(r['source_document'] != root['source_document'] for r in self.target_members):
            raise ValueError(u'選取物包含其他文件的元素，請重新選取修正範圍。')
        elements = ([uidoc.Document.GetElement(i) for i in selection] if selection else
                    [uidoc.Document.GetElement(r['source_element_id']) for r in self.target_members])
        members, seen = [], {root['source_element_id']}
        for element in elements:
            if element is None:
                raise ValueError(u'選取物已刪除，請重新選取修正範圍。')
            if element.Category is None or isinstance(element, DB.ElementType):
                raise ValueError(u'修正範圍只接受模型實例，請重新選取。')
            if element.UniqueId not in seen:
                members.append(read(element))
                seen.add(element.UniqueId)
        origin = self.current_suggest_context()
        item = self.CheckResults.SelectedItem
        if item and self.check_report and self.check_report['document'] == root['source_document']:
            keys = set(r['source_element_id'] for r in item.data['records'])
            if keys == seen:
                origin = check_issue(self.check_report, item.data, root)['origin']
        # An older issue for the same floor but a different counterpart is not this problem.
        if origin and origin.get('elements') and not any(r.get('source') for r in origin.get('source_refs', [])):
            if origin.get('document') != root['source_document'] or set(r['unique_id'] for r in origin['elements']) != seen:
                origin = None
        return root, members, origin

    def show_suggest_context(self):
        origin = self.suggest_plan_context if self.suggest_scope is not None else self.current_suggest_context()
        if not origin:
            self.SuggestContext.Text = ''
            return
        lines = [u'檢查問題：'+origin.get('message', '')]
        if origin.get('rule_id'):
            lines.append(u'規則：{} {}'.format(origin['rule_id'], origin.get('rule_name', '')))
        if origin.get('advice'):
            lines.extend([u'', u'規則建議（供規劃參考）：', origin['advice']])
        self.SuggestContext.Text = u'\n'.join(lines)

    def suggest_controls(self, applying):
        self.SuggestCaseMode.IsEnabled = self.SuggestAiMode.IsEnabled = not applying
        self.SuggestSources.IsEnabled = not applying
        self.SuggestPrompt.IsReadOnly = applying

    def suggestion_in_document(self, target, action, message):
        def execute(uidoc):
            if (not self.target or self.target['node_id'] != target['node_id'] or
                    document_id(uidoc.Document) != target['source_document'] or
                    uidoc.Document.GetElement(target['source_element_id']) is None):
                self.invalidate_suggestion(u'目標或目前文件已變更，請重新選取元素並產生建議。')
                return
            action(uidoc)
        if not self.run(execute, message):
            self.SuggestStatus.Text = u'Revit 正在處理其他工作，請稍後重試。'

    def spawn(self, worker):
        thread = threading.Thread(target=worker)
        thread.daemon = True
        thread.start()

    def dispatch(self, callback):
        self.Dispatcher.BeginInvoke(Action(callback))

    def show_suggest_sources(self, results):
        self.SuggestSources.Items.Clear()
        for result in results:
            item = SourceItem(result)
            self.SuggestSources.Items.Add(item)
            self.SuggestSources.SelectedItems.Add(item)
        self.SuggestSourceSummary.Text = (u'{} 個同類型的過去解法；選取的會交給 AI 參考。'.format(len(results)) if results
                                          else u'沒有同類型的過去解法；可切換到「透過 AI 建議」並輸入你的指示。')

    def invalidate_suggestion(self, message):
        if self.suggest_kind == 'apply':
            return  # Never interrupt the executor while it is changing the model.
        job, self.suggest_job = self.suggest_job, None
        self.suggest_kind = None
        if job is not None:
            job.cancel()
            self.SuggestRun.IsEnabled, self.SuggestCancel.IsEnabled = True, False
            self.SuggestAutoFix.IsEnabled = True
        self.suggest_steps, self.suggest_target = [], None
        self.suggest_results, self.suggest_blocked = {}, False
        self.suggest_scope = None
        self.suggest_plan_context = None
        self.SuggestScope.Text = ''
        self.SuggestSteps.Items.Clear()
        self.SuggestSummary.Text = self.SuggestResults.Text = ''
        self.SuggestApply.IsEnabled = False
        self.SuggestStatus.Text = message

    def start_agent(self, kind, message, work, done):
        agent = self.suggest_job = self.agent_factory(self.Backend.Text, provider='claude')
        self.suggest_kind = kind
        self.SuggestRun.IsEnabled, self.SuggestCancel.IsEnabled, self.SuggestApply.IsEnabled = False, True, False
        self.SuggestAutoFix.IsEnabled = False
        self.suggest_controls(kind == 'apply')
        self.render_suggestion_steps()
        self.SuggestStatus.Text = message

        def worker():
            try:
                result, failure = work(agent), None
            except Exception as error:
                result, failure = None, unicode(error)
                self.log_error(error)

            def complete():
                if self.suggest_job is not agent:
                    return  # Cancelled or superseded by a target change.
                self.suggest_job, self.suggest_kind = None, None
                self.SuggestRun.IsEnabled, self.SuggestCancel.IsEnabled = True, False
                self.SuggestAutoFix.IsEnabled = True
                self.suggest_controls(False)
                if failure is not None:
                    if kind == 'apply':
                        # A lost reply can follow a committed change; never replay it.
                        self.suggest_blocked = True
                    self.render_suggestion_steps()
                    self.SuggestStatus.Text = failure
                else:
                    done(result)
            self.dispatch(complete)
        self.spawn(worker)

    def suggest_click(self, sender, args):
        self.generate_suggestion(auto_apply=False)

    def suggest_auto_click(self, sender, args):
        self.generate_suggestion(auto_apply=True)

    def generate_suggestion(self, auto_apply=False):
        if self.suggest_job is not None:
            return
        if not self.target:
            self.SuggestStatus.Text = u'請先選取基準元素。'
            return
        instruction = self.SuggestPrompt.Text.strip()
        mode = self.suggestion_mode()
        sources = [item.data for item in self.SuggestSources.SelectedItems] if mode == 'cases' else []
        if mode == 'cases' and not sources:
            self.SuggestStatus.Text = u'請選取至少一個過去案例，或切換到「透過 AI 建議」。'
            return
        if mode == 'ai' and not instruction:
            self.SuggestStatus.Text = u'請輸入要解決的問題、修改目標或限制，供 AI 結合元素現況規劃。'
            return
        target = deepcopy(self.target)
        self.invalidate_suggestion('')
        message = (u'Claude 正在查詢現況並規劃修正，完成後會直接透過 MCP 套用…' if auto_apply
                   else u'Claude 正在透過 MCP 讀取元素，依{}產生建議…'.format(u'你的指示' if mode == 'ai' else u'過去案例'))
        def start(uidoc):
            if mode != self.suggestion_mode() or instruction != self.SuggestPrompt.Text.strip():
                self.SuggestStatus.Text = u'指示已變更，請重新產生建議。'
                return
            root, members, context = self.capture_suggestion_targets(uidoc, target)
            scope = repair_scope(root, members)
            self.suggest_scope, self.suggest_plan_context = scope, deepcopy(context)
            self.show_suggest_context()
            self.SuggestScope.Text = u'本次修正範圍：'+u'；'.join(
                u'{} · ID {}'.format(r['name'], r['properties']['element_id']) for r in [root]+members)
            self.start_agent('plan', message,
                             lambda agent: agent.plan(root, sources, instruction, mode=mode, context=context, members=members),
                             lambda result: self.show_suggestion(root, dict(result, scope=scope), auto_apply))
        self.suggestion_in_document(target, start, u'確認修改目標…')

    def show_suggestion(self, target, result, auto_apply=False):
        self.suggest_scope = result.get('scope')
        problems = scope_problems(result['steps'], self.suggest_scope)
        if problems:
            result = dict(result, steps=[], already_resolved=False, problems=(result.get('problems') or [])+problems)
        self.suggest_steps, self.suggest_target = result['steps'], target['node_id']
        self.suggest_results, self.suggest_blocked = {}, False
        self.render_suggestion_steps()
        notes = [result['summary']] if result.get('summary') else []
        if result.get('already_resolved') and (result.get('resolution') or {}).get('evidence'):
            notes.append(u'本次組合完成證據：'+result['resolution']['evidence'])
        notes += [u'⚠ '+w for w in result.get('warnings', [])] + [u'？'+q for q in result.get('questions', [])]
        notes += [u'已略過：'+p for p in result.get('problems', [])]
        self.SuggestSummary.Text = u'\n'.join(notes)
        self.SuggestApply.IsEnabled = bool(self.suggest_steps)
        retried = u'（第二次規劃）' if result.get('attempts', 1) > 1 else u''
        self.SuggestStatus.Text = (u'Claude 已完成建議{}。可逐項按「套用」，或按「套用全部」。'.format(retried) if self.suggest_steps
                                   else u'Claude 判斷本次目標與選取物之間的問題已修正，不需修改。' if result.get('already_resolved')
                                   else u'Claude {}未產生有效步驟，模型沒有變更；請看說明後補充指示再試。'.format(
                                       u'兩次規劃仍' if result.get('attempts', 1) > 1 else u''))
        if auto_apply and self.suggest_steps:
            if result.get('questions'):
                # Steps stay available; open questions only withdraw the automatic authorization.
                self.SuggestStatus.Text = u'已產生步驟，但 Claude 列出待確認事項，未自動套用。請確認說明後逐項套用或按「套用全部」。'
            else:
                # Clicking 自動修正 authorizes this plan/apply cycle for the pinned target.
                self.suggest_apply(None, None, confirm=False)

    def suggest_apply_step(self, sender, args):
        item = sender.Tag
        if item is None or not item.can_apply or not any(row is item for row in self.SuggestSteps.Items):
            return
        self.suggest_apply(sender, args, step_index=item.index)

    def suggest_apply(self, sender, args, confirm=True, step_index=None):
        plan = self.suggest_steps
        indices = self.pending_suggestion_indices()
        if step_index is not None:
            indices = [step_index] if step_index in indices else []
        steps = [plan[i-1] for i in indices]
        if not steps or self.suggest_job is not None:
            return
        scope = deepcopy(self.suggest_scope)
        problems = scope_problems(steps, scope)
        if problems:
            self.invalidate_suggestion(u'\n'.join(problems))
            return
        if not self.target or self.target['node_id'] != self.suggest_target:
            self.invalidate_suggestion(u'基準元素已變更，請重新產生建議。')
            return
        confirmation = (u'將透過 Revit MCP 套用此項建議：\n'+step_caption(indices[0], steps[0]) if step_index is not None else
                        u'將透過 Revit MCP 依序執行 {} 個未完成步驟修改模型。'.format(len(steps)))
        if confirm and not forms.alert(confirmation+u'\n可在 Revit 以「復原」還原。要繼續嗎？',
                           title=u'案例驅動 BIM 診斷與修正', ok=False, yes=True, no=True):
            self.SuggestStatus.Text = u'已取消，模型沒有變更。'
            return
        base, document = self.Backend.Text, self.target['source_document']
        cases = sorted(set(s['source_case'] for s in steps if s.get('source_case')))

        def work(agent):
            report = agent.apply(steps, scope=scope)
            results = report.get('results') or []
            record = dict(outcome=outcome(results, len(steps)), document=document,
                          element_ids=sorted(set(s['arguments'][key] for s in steps for key in ('elementId', 'ductId')
                                                 if key in s['arguments'])),
                          steps=[dict(step, plan_step=indices[i-1], repair_scope=scope, result=next((r for r in results if r.get('step') == i), None))
                                 for i, step in enumerate(steps, 1)], notes=report.get('summary') or u'')
            recorded = 0
            for case_id in cases:
                try:
                    request(base, '/cases/{}/applications'.format(case_id), record)
                    recorded += 1
                except ValueError:
                    pass  # The model change stands; a missing log entry must not hide it.
            return dict(report, outcome=record['outcome'], recorded=recorded)
        def start(uidoc):
            if self.suggest_job is not None:
                return
            if self.suggest_steps is not plan or self.suggest_scope != scope or any(i not in self.pending_suggestion_indices() for i in indices):
                self.SuggestStatus.Text = u'建議已變更，請重新產生並檢視步驟。'
                return
            message = (u'正在透過 Revit MCP 套用第 {} 項…'.format(indices[0]) if step_index is not None else
                       u'正在透過 Revit MCP 套用 {} 個步驟…'.format(len(steps)))
            self.start_agent('apply', message, work, lambda report: self.show_applied(report, indices))
        # Auto-apply is dispatched after the planning job returns, in the same way as manual apply.
        self.suggestion_in_document(self.target, start, u'確認套用目標…')

    def show_applied(self, report, indices):
        results = dict((r.get('step'), r) for r in report.get('results') or [])
        for position, index in enumerate(indices, 1):
            self.suggest_results[index] = results.get(position) or dict(ok=False, message=u'未收到結果，請先檢查模型。')
        if any(not self.suggest_results[i].get('ok') for i in indices):
            self.suggest_blocked = True
        lines = []
        for index, step in enumerate(self.suggest_steps, 1):
            if index not in self.suggest_results:
                continue
            result = self.suggest_results[index]
            lines.append(u'{} {}{}'.format(u'✓' if result.get('ok') else u'✗', step_caption(index, step),
                                           u'：'+result['message'] if result.get('message') else u''))
        self.SuggestResults.Text = u'\n'.join(lines)
        self.render_suggestion_steps()
        pending = len(self.pending_suggestion_indices())
        completed = sum(1 for r in self.suggest_results.values() if r.get('ok'))
        state = (u'部分套用' if completed else u'未能套用') if self.suggest_blocked else (
            u'已套用 {}／{} 項'.format(completed, len(self.suggest_steps)) if pending else u'已全部套用')
        self.SuggestStatus.Text = state + (
            u'；已記錄到 {} 個參考案例。'.format(report['recorded']) if report.get('recorded') else u'。') + (
            u'尚餘 {} 項，可繼續逐項套用。'.format(pending) if pending else
            u'請先檢查模型並重新產生建議。' if self.suggest_blocked else u'請重新檢查問題是否已解決。')

    def suggest_cancel(self, sender, args):
        job, self.suggest_job, self.suggest_kind = self.suggest_job, None, None
        if job is not None:
            job.cancel()
        self.SuggestRun.IsEnabled, self.SuggestCancel.IsEnabled = True, False
        self.SuggestAutoFix.IsEnabled = True
        self.suggest_controls(False)
        self.suggest_blocked = True
        self.render_suggestion_steps()
        self.SuggestStatus.Text = u'已停止。若停止時正在套用，請在 Revit 檢查模型。'
