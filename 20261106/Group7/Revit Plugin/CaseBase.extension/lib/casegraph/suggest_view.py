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
from casegraph.suggestion_revit import other_same_type, inspect_type_changes, verify_type_changes


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
        self.state = (u'已符合，無需變更' if result.get('ok') and result.get('changed') is False else
                      u'已套用' if result.get('ok') else u'未完成，請重新檢查') if result is not None else (
            u'請檢查模型並重新產生建議' if blocked else u'待套用')


class ChoiceOption(object):
    """One clickable answer. With steps it applies them; otherwise it re-plans with the answer."""
    def __init__(self, question, data=None):
        data = data or {}
        self.question, self.other = question, not data
        self.label = data.get('label') or u'其他'
        self.button = u'其他…' if self.other else self.label
        self.steps = deepcopy(data.get('steps') or [])
        self.instruction = data.get('instruction') or u''
        self.select_ids = list(data.get('select_ids') or [])
        self.then_replan = bool(data.get('then_replan')) and bool(self.steps)
        captions = u'；'.join(step_caption(i, s) for i, s in enumerate(self.steps, 1))
        follow = u'，完成後自動重新規劃' if self.then_replan else u''
        adds = u'、'.join(u'ID {}'.format(i) for i in self.select_ids)
        self.tooltip = (u'自行輸入做法後重新規劃。' if self.other else
                        u'確認後直接套用：'+captions+follow if self.steps else
                        u'自動把 {} 加入修正範圍並重新規劃。'.format(adds) if self.select_ids else u'依此選擇重新規劃。')
        detail = data.get('detail') or u''
        self.hint = None if self.other else u'［{}］{}{}'.format(
            self.label, detail, (u'（可直接套用：' + captions + follow + u'）') if self.steps else
                (u'（自動加入 {} 並重新規劃）'.format(adds)) if self.select_ids else (
                u'（按下後依此重新規劃）' if not data.get('problems') else u'（步驟無效，按下後重新規劃）'))


class ChoiceItem(object):
    def __init__(self, data):
        from System.Collections.Generic import List
        self.question = data['question']
        options = [ChoiceOption(self.question, o) for o in data.get('options') or []] + [ChoiceOption(self.question)]
        self.options = List[object](options)
        self.hint = u'\n'.join(o.hint for o in options if o.hint)


class OtherSuggestion(object):
    def __init__(self, record, steps):
        self.data = record
        self.title = record['name']
        p = record['properties']
        self.detail = u'{} · {} · ID {}'.format(p.get('category') or '', p.get('type_name') or '', p.get('element_id') or '')
        self.reason = u'同一原類型，建議另行檢查：'+u'；'.join(s['reason'] for s in steps)


class SuggestView(object):
    agent_factory = AgentRunner

    def initialize_suggest(self):
        self.suggest_steps, self.suggest_job, self.suggest_target = [], None, None
        self.suggest_kind = None
        self.suggest_context = None
        self.suggest_results, self.suggest_blocked = {}, False
        self.suggest_scope = None
        self.suggest_plan_context = None
        self.suggest_candidates, self.suggest_records = [], []
        self.suggest_finishing = False

    def clear_suggest_choices(self):
        self.SuggestChoices.Items.Clear()

    def render_suggest_choices(self, choices):
        self.clear_suggest_choices()
        for choice in choices or []:
            self.SuggestChoices.Items.Add(ChoiceItem(choice))

    def suggest_choice_click(self, sender, args):
        option = sender.Tag
        if (option is None or self.suggest_job is not None or self.suggest_finishing or
                not any(o is option for item in self.SuggestChoices.Items for o in item.options)):
            return
        answer = u'回答「{}」：'.format(option.question) + (u'' if option.other else option.label + (
            u'。' + option.instruction if option.instruction else u''))
        if option.steps:
            # The option's steps were validated with the plan; they replace the main steps.
            self.suggest_steps = deepcopy(option.steps)
            self.suggest_results, self.suggest_blocked = {}, False
            self.render_suggestion_steps()
            self.render_other_suggestions()
            self.SuggestStatus.Text = u'已選擇「{}」。'.format(option.label)
            self.suggest_followup = answer if option.then_replan else None
            self.suggest_apply(None, None)
            return
        self.add_suggest_answer(answer)
        if option.other:
            self.SuggestPrompt.Focus()
            self.SuggestPrompt.CaretIndex = len(self.SuggestPrompt.Text)
            self.SuggestStatus.Text = u'請在指示欄補充你的做法，再按「產生建議」。'
            return
        # Elements the plan needs (e.g. a blocking beam) join the scope by this click, not by the AI.
        self.generate_suggestion(auto_apply=False, extra_ids=option.select_ids or None)

    def add_suggest_answer(self, answer):
        text = self.SuggestPrompt.Text.rstrip()
        self.SuggestPrompt.Text = (text + u'\n' if text else u'') + answer

    def pending_suggestion_indices(self):
        return [] if self.suggest_blocked else [i for i in range(1, len(self.suggest_steps)+1) if i not in self.suggest_results]

    def render_suggestion_steps(self):
        enabled = self.suggest_job is None and not self.suggest_finishing
        self.SuggestSteps.Items.Clear()
        for index, step in enumerate(self.suggest_steps, 1):
            self.SuggestSteps.Items.Add(StepItem(index, step, self.suggest_results.get(index), enabled, self.suggest_blocked))
        self.SuggestApply.IsEnabled = enabled and bool(self.pending_suggestion_indices())
        self.SuggestChoices.IsEnabled = enabled

    def render_other_suggestions(self):
        self.SuggestOther.Items.Clear()
        by_id = dict((int(r['properties']['element_id']), r) for r in self.suggest_records)
        for record in self.suggest_candidates:
            steps = [s for s in self.suggest_steps if s['arguments']['elementId'] in by_id and
                     by_id[s['arguments']['elementId']]['properties'].get('type_id') == record['properties'].get('type_id')]
            if steps:
                self.SuggestOther.Items.Add(OtherSuggestion(record, steps))
        self.SuggestOtherPanel.Visibility = Visibility.Visible if self.SuggestOther.Items.Count else Visibility.Collapsed

    def suggest_other_select(self, sender, args):
        item = sender.Tag
        if self.suggest_job is not None or self.suggest_finishing or not any(row is item for row in self.SuggestOther.Items):
            return
        def select(uidoc):
            record = item.data
            if document_id(uidoc.Document) != record['source_document']:
                raise ValueError(u'請回到來源模型後再選取。')
            element = uidoc.Document.GetElement(record['source_element_id'])
            if element is None:
                raise ValueError(u'元件已刪除，請重新產生建議。')
            from System.Collections.Generic import List
            uidoc.Selection.SetElementIds(List[DB.ElementId]([element.Id]))
            self.set_target(snapshot(uidoc.Document, element), refresh=False, members=[])
            self.invalidate_suggestion(u'已選取此元件；請按「產生建議」核對後再修改。')
        self.run(select, u'選取其他同類元件…')

    def suggestion_mode(self):
        return 'ai' if self.SuggestAiMode.IsChecked else 'cases'

    def suggest_mode_changed(self, sender, args):
        if not self.ready or self.suggest_kind == 'apply' or self.suggest_finishing:
            return
        self.invalidate_suggestion(u'建議來源已變更，請重新產生建議。')
        ai = self.suggestion_mode() == 'ai'
        self.SuggestCasesPanel.Visibility = Visibility.Collapsed if ai else Visibility.Visible
        self.SuggestModeHint.Text = (u'讀取目前元素，結合你的問題與限制，由 AI 提出修改步驟。' if ai else
                                     u'選取過去解法，依目前元素狀態轉換成修改步驟。')
        if not ai and self.target:
            self.schedule_refresh('suggest')

    def suggest_inputs_changed(self, sender, args):
        if not self.ready or self.suggest_kind == 'apply' or self.suggest_finishing:
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

    def capture_suggestion_targets(self, uidoc, target, extra_ids=None):
        """Capture the current selection on the Revit thread, before Claude starts.
        extra_ids: elements that an option the user clicked adds to the pinned target and members."""
        def read(element):
            record = deepcopy(snapshot(uidoc.Document, element))
            record['properties']['element_id'] = id_value(element.Id)
            return record
        selection = [] if extra_ids else list(uidoc.Selection.GetElementIds())
        if extra_ids and target is None:
            raise ValueError(u'請先讀取基準元素後再加入其他元件。')
        if not selection and target is None:
            raise ValueError(u'請先在 Revit 選取要修正的元件，或讀取面板基準元素。')
        if not selection and any(r['source_document'] != target['source_document'] for r in self.target_members):
            raise ValueError(u'選取物包含其他文件的元素，請重新選取修正範圍。')
        elements = ([uidoc.Document.GetElement(i) for i in selection] if selection else
                    [uidoc.Document.GetElement(r['source_element_id']) for r in [target]+self.target_members])
        if extra_ids:
            from casegraph.suggestion_revit import element_id
            elements += [uidoc.Document.GetElement(element_id(i)) for i in extra_ids]
        records, seen = [], set()
        for element in elements:
            if element is None:
                raise ValueError(u'選取物已刪除，請重新選取修正範圍。')
            if element.Category is None or isinstance(element, DB.ElementType):
                raise ValueError(u'修正範圍只接受模型實例，請重新選取。')
            if element.UniqueId not in seen:
                records.append(read(element))
                seen.add(element.UniqueId)
        root, members = records[0], records[1:]
        origin = self.current_suggest_context()
        item = self.CheckResults.SelectedItem
        if item and self.check_report and self.check_report['document'] == root['source_document']:
            keys = set(r['source_element_id'] for r in item.data['records'])
            if keys == seen:
                origin = check_issue(self.check_report, item.data, root)['origin']
        # An older issue for the same floor but a different counterpart is not this problem.
        if origin and origin.get('elements') and not any(r.get('source') for r in origin.get('source_refs', [])):
            involved = set(r['unique_id'] for r in origin['elements'])
            # Elements added by an option (e.g. a blocking beam) extend the same problem's scope.
            if origin.get('document') != root['source_document'] or (
                    not involved <= seen if extra_ids else involved != seen):
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
        if self.suggest_kind == 'apply' or self.suggest_finishing:
            return  # Never interrupt the executor while it is changing the model.
        job, self.suggest_job = self.suggest_job, None
        self.suggest_kind = None
        self.suggest_followup = None
        if job is not None:
            job.cancel()
            self.SuggestRun.IsEnabled, self.SuggestCancel.IsEnabled = True, False
            self.SuggestAutoFix.IsEnabled = True
        self.suggest_steps, self.suggest_target = [], None
        self.suggest_results, self.suggest_blocked = {}, False
        self.suggest_scope = None
        self.suggest_plan_context = None
        self.suggest_candidates, self.suggest_records = [], []
        self.SuggestOther.Items.Clear()
        self.SuggestOtherPanel.Visibility = Visibility.Collapsed
        self.SuggestScope.Text = ''
        self.clear_suggest_choices()
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
                    if kind == 'plan':
                        self.restore_suggest_selection()
                else:
                    done(result)
            self.dispatch(complete)
        self.spawn(worker)

    def remember_suggest_selection(self, uidoc):
        """Revit selection when 產生建議 starts; planning must leave it as the user had it."""
        self.suggest_selection = (document_id(uidoc.Document), [id_value(i) for i in uidoc.Selection.GetElementIds()])

    def restore_suggest_selection(self):
        """Put back the selection captured at 產生建議 if something cleared it while planning.
        A selection the user changed meanwhile, another document or deleted elements are left alone."""
        saved, self.suggest_selection = getattr(self, 'suggest_selection', None), None
        if not saved or not saved[1]:
            return
        def restore(uidoc):
            if document_id(uidoc.Document) != saved[0] or list(uidoc.Selection.GetElementIds()):
                return
            from System.Collections.Generic import List
            from casegraph.suggestion_revit import element_id
            elements = [uidoc.Document.GetElement(element_id(i)) for i in saved[1]]
            ids = List[DB.ElementId]([e.Id for e in elements if e is not None])
            if ids.Count:
                uidoc.Selection.SetElementIds(ids)
        self.run(restore, u'保留原有選取…')

    def suggest_click(self, sender, args):
        self.generate_suggestion(auto_apply=False)

    def suggest_auto_click(self, sender, args):
        self.generate_suggestion(auto_apply=True)

    def generate_suggestion(self, auto_apply=False, extra_ids=None):
        if self.suggest_job is not None or self.suggest_finishing:
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
            self.remember_suggest_selection(uidoc)
            root, members, context = self.capture_suggestion_targets(uidoc, target, extra_ids)
            if extra_ids:
                # Show the enlarged scope in Revit too, so the user sees what may be changed.
                from System.Collections.Generic import List
                ids = [uidoc.Document.GetElement(r['source_element_id']) for r in [root]+members]
                uidoc.Selection.SetElementIds(List[DB.ElementId]([e.Id for e in ids if e is not None]))
            scope = repair_scope(root, members)
            self.set_target(root, refresh=False, keep_checks=True, members=members, refresh_suggestions=False)
            self.suggest_records = deepcopy([root]+members)
            try:
                self.suggest_candidates = other_same_type(uidoc, self.suggest_records)
            except Exception as error:
                self.log_error(error)
                self.suggest_candidates = []  # Optional candidates must not block the selected repair.
            self.suggest_scope, self.suggest_plan_context = scope, deepcopy(context)
            self.show_suggest_context()
            self.SuggestScope.Text = u'本次修正範圍：'+u'；'.join(
                u'{} · ID {}'.format(r['name'], r['properties']['element_id']) for r in [root]+members)
            self.start_agent('plan', message,
                             lambda agent: agent.plan(root, sources, instruction, mode=mode, context=context, members=members),
                             lambda result: self.show_suggestion(root, dict(result, scope=scope), auto_apply))
        if target is None:
            self.run(start, u'讀取目前選取元件…')
        else:
            self.suggestion_in_document(target, start, u'確認修改目標…')

    def show_suggestion(self, target, result, auto_apply=False):
        self.suggest_scope = result.get('scope')
        problems = scope_problems(result['steps'], self.suggest_scope)
        if problems:
            result = dict(result, steps=[], already_resolved=False, problems=(result.get('problems') or [])+problems)
        self.suggest_steps, self.suggest_target = result['steps'], target['node_id']
        self.suggest_results, self.suggest_blocked = {}, False
        self.render_suggestion_steps()
        self.render_other_suggestions()
        notes = [result['summary']] if result.get('summary') else []
        if result.get('already_resolved') and (result.get('resolution') or {}).get('evidence'):
            notes.append(u'本次組合完成證據：'+result['resolution']['evidence'])
        # Older workers return plain question strings; they still get an 「其他…」 answer button.
        choices = result.get('choices') or [dict(question=q, options=[]) for q in result.get('questions', [])]
        notes += [u'⚠ '+w for w in result.get('warnings', [])]
        notes += [u'已略過：'+p for p in result.get('problems', [])]
        self.SuggestSummary.Text = u'\n'.join(notes)
        self.render_suggest_choices(choices)
        self.SuggestApply.IsEnabled = bool(self.suggest_steps)
        retried = u'（第二次規劃）' if result.get('attempts', 1) > 1 else u''
        self.SuggestStatus.Text = (u'Claude 已完成建議{}。可逐項按「套用」，或按「套用本次全部修改」。'.format(retried) if self.suggest_steps
                                   else u'Claude 判斷本次目標與選取物之間的問題已修正，不需修改。' if result.get('already_resolved')
                                   else u'需要你決定做法：請在下方選擇一個選項，模型尚未變更。' if choices
                                   else u'Claude {}未產生有效步驟，模型沒有變更；請看說明後補充指示再試。'.format(
                                       u'兩次規劃仍' if result.get('attempts', 1) > 1 else u''))
        if auto_apply and self.suggest_steps:
            if result.get('questions'):
                # Steps stay available; open questions only withdraw the automatic authorization.
                self.SuggestStatus.Text = u'已產生步驟，但 Claude 列出待確認事項，未自動套用。請確認說明後逐項套用或按「套用本次全部修改」。'
                self.restore_suggest_selection()
            else:
                # Clicking 自動修正 authorizes this plan/apply cycle for the pinned target.
                self.suggest_selection = None
                self.suggest_apply(None, None, confirm=False)
        else:
            self.restore_suggest_selection()

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
        if not steps or self.suggest_job is not None or self.suggest_finishing:
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
                        u'將透過 Revit MCP 依序執行 {} 個未完成步驟修改模型：\n'.format(len(steps)) +
                        u'\n'.join(step_caption(i, s) for i, s in zip(indices, steps)))
        if confirm and not forms.alert(confirmation+u'\n可在 Revit 以「復原」還原。要繼續嗎？',
                           title=u'案例驅動 BIM 診斷與修正', ok=False, yes=True, no=True):
            self.SuggestStatus.Text = u'已取消，模型沒有變更。'
            return
        base, document = self.Backend.Text, self.target['source_document']
        applied_target = deepcopy(self.target)
        cases = sorted(set(s['source_case'] for s in steps if s.get('source_case')))

        def record_report(report):
            results = report.get('results') or []
            record = dict(outcome=outcome(results, len(steps)), document=document,
                          element_ids=sorted(set(s['arguments'][key] for s in steps for key in ('elementId', 'ductId', 'mepId')
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
            states = inspect_type_changes(uidoc, steps)
            def done(report):
                self.suggest_finishing = True
                self.SuggestRun.IsEnabled = self.SuggestAutoFix.IsEnabled = self.SuggestCancel.IsEnabled = False
                self.suggest_controls(True)
                def finish(verified):
                    # Persist only verified results, then enable the remaining steps.
                    def record_worker():
                        try:
                            logged = record_report(verified)
                        except Exception as error:
                            self.log_error(error)
                            logged = dict(verified, recorded=0)
                        def display():
                            self.suggest_finishing = False
                            self.SuggestRun.IsEnabled = self.SuggestAutoFix.IsEnabled = True
                            self.suggest_controls(False)
                            self.show_applied(logged, indices)
                            followup, self.suggest_followup = getattr(self, 'suggest_followup', None), None
                            if followup and not self.suggest_blocked and not self.pending_suggestion_indices():
                                # The option's first move changed the geometry; plan the rest automatically.
                                self.add_suggest_answer(followup + u'（上一步已套用）')
                                self.generate_suggestion(auto_apply=False)
                        self.dispatch(display)
                    self.spawn(record_worker)
                def verify(current):
                    try:
                        if document_id(current.Document) != document:
                            raise ValueError(u'目前文件已變更，無法核對類型，請回到來源模型檢查。')
                        verified = verify_type_changes(current, report, states)
                        if any(r.get('changed') for r in verified.get('results') or []):
                            # The Properties palette keeps showing the old type of a still-selected element
                            # after an external change; its Edit Type → OK then writes that old type back.
                            from System.Collections.Generic import List
                            current.Selection.SetElementIds(List[DB.ElementId]())
                            verified = dict(verified, selection_cleared=True)
                        element = current.Document.GetElement(applied_target['source_element_id'])
                        if element is not None and self.target and self.target['node_id'] == applied_target['node_id']:
                            self.set_target(snapshot(current.Document, element), refresh=False, keep_checks=True)
                        else:
                            self.suggest_blocked = True
                        finish(verified)
                    except Exception as error:
                        failed = dict(report, results=[dict(r, ok=False, message=unicode(error)) for r in report.get('results') or []])
                        finish(failed)
                if not self.run(verify, u'核對修改結果並更新類型…'):
                    finish(dict(report, results=[dict(r, ok=False, message=u'無法核對修改結果，請檢查模型。')
                                                 for r in report.get('results') or []]))
            self.start_agent('apply', message, lambda agent: agent.apply(steps, scope=scope), done)
        # Auto-apply is dispatched after the planning job returns, in the same way as manual apply.
        self.suggestion_in_document(self.target, start, u'確認套用目標…')

    def show_applied(self, report, indices):
        results = dict((r.get('step'), r) for r in report.get('results') or [])
        for position, index in enumerate(indices, 1):
            self.suggest_results[index] = results.get(position) or dict(ok=False, message=u'未收到結果，請先檢查模型。')
        if any(not self.suggest_results[i].get('ok') for i in indices):
            self.suggest_blocked = True
        self.clear_suggest_choices()  # The model changed; earlier alternatives are stale.
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
        if not pending and not self.suggest_blocked and all(r.get('changed') is False for r in self.suggest_results.values()):
            state = u'目前類型已符合，本次沒有變更'
        self.SuggestStatus.Text = state + (
            u'；已記錄到 {} 個參考案例。'.format(report['recorded']) if report.get('recorded') else u'。') + (
            u'尚餘 {} 項，可繼續逐項套用。'.format(pending) if pending else
            u'請先檢查模型並重新產生建議。' if self.suggest_blocked else u'請重新檢查問題是否已解決。') + (
            u'\n已取消 Revit 選取，避免 Properties 顯示舊類型；請重新點選元件查看新類型，確認後記得儲存檔案。'
            if report.get('selection_cleared') else u'')

    def suggest_cancel(self, sender, args):
        if self.suggest_finishing:
            return
        job, self.suggest_job, self.suggest_kind = self.suggest_job, None, None
        if job is not None:
            job.cancel()
        self.SuggestRun.IsEnabled, self.SuggestCancel.IsEnabled = True, False
        self.SuggestAutoFix.IsEnabled = True
        self.suggest_controls(False)
        self.suggest_blocked = True
        self.render_suggestion_steps()
        self.SuggestStatus.Text = u'已停止。若停止時正在套用，請在 Revit 檢查模型。'
