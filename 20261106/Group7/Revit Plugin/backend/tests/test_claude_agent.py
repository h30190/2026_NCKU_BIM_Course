import json
from pathlib import Path
import subprocess
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import claude_agent
from revit_mcp_client import McpClient
import planning_mcp
from casegraph.suggestion import validate_plan

SCOPE = dict(document='doc', target_id=101, element_ids=[101])


def step(tool='flip_element', **arguments):
    return dict(tool=tool, arguments=dict(elementId=101, **arguments), reason='調整開向', source_case=None)


class FakeMcp:
    def __init__(self, fail_at=None):
        self.calls, self.fail_at = [], fail_at

    def list_tools(self):
        return [dict(name=name) for name in claude_agent.MODIFY_TOOLS]

    def call(self, name, arguments):
        self.calls.append((name, arguments))
        if len(self.calls) == self.fail_at:
            raise TimeoutError('Outcome unknown; inspect Revit.')
        return '完成'


def test_apply_runs_exact_approved_calls_in_order_and_stops_on_uncertain_result():
    steps = [step(flipType='hand'), step('move_element', dx=100), step('delete_element')]
    client = FakeMcp(fail_at=2)
    report = claude_agent.apply_steps(steps, client, SCOPE)
    assert client.calls == [(s['tool'], s['arguments']) for s in steps[:2]]
    assert [r['ok'] for r in report['results']] == [True, False, False]
    assert 'unknown' in report['results'][1]['message']
    assert '未執行' in report['results'][2]['message']


@pytest.mark.parametrize('bad', [step('execute_csharp'), step(flipType='bad'), step('move_element', dx=float('nan')),
                                  step('change_element_type', typeId=True), step(extra='injected')])
def test_invalid_approved_plan_makes_no_calls(bad):
    client = FakeMcp()
    with pytest.raises(ValueError, match='invalid'):
        claude_agent.apply_steps([step(), bad], client, SCOPE)
    assert not client.calls


def test_unavailable_tool_blocks_entire_plan_before_first_write():
    client = FakeMcp()
    client.list_tools = lambda: [dict(name='flip_element')]
    with pytest.raises(ValueError, match='missing tools'):
        claude_agent.apply_steps([step(), step('delete_element')], client, SCOPE)
    assert not client.calls


def test_claude_configuration_uses_read_proxy_and_round_trips_windows_paths(tmp_path):
    command = claude_agent.claude_command('claude.exe', tmp_path,
        dict(type='stdio', command='C:\\Program Files\\node.exe', args=['C:\\中文\\index.js'], env=dict(REVIT_MCP_PORT='8964')),
        'http://127.0.0.1:8000', 'sonnet')
    upstream = json.loads((tmp_path / 'upstream-mcp.json').read_text())['mcpServers']
    assert upstream['revit']['args'] == ['C:\\中文\\index.js']
    configured = json.loads((tmp_path / 'mcp.json').read_text())['mcpServers']
    assert set(configured) == {'revit', 'casegraph'}
    assert all('planning_mcp.py' in s['args'][0] for s in configured.values())
    assert command[command.index('--tools') + 1] == ''
    assert command[command.index('--setting-sources') + 1] == ''
    assert command[command.index('--permission-mode') + 1] == 'dontAsk'
    assert command[command.index('--model') + 1] == 'sonnet'
    assert set(command[command.index('--allowedTools') + 1].split(',')) == set(claude_agent.READ_TOOLS)
    assert '--strict-mcp-config' in command and '--no-session-persistence' in command
    assert '--dangerously-skip-permissions' not in command
    assert json.loads(command[command.index('--settings') + 1])['disableAllHooks']


def test_tool_call_status_records_outcome_without_arguments_or_results():
    def event(tool, failed=False):
        use = dict(type='assistant', message=dict(content=[dict(type='tool_use', id=tool,
            name='mcp__casegraph__'+tool, input={'private': 'model-data'})]))
        result = dict(type='user', message=dict(content=[dict(type='tool_result', tool_use_id=tool,
            is_error=failed, content='private BIM values')]))
        return json.dumps(use)+'\n'+json.dumps(result)
    output = '\n'.join(['not json', 'null', event('casegraph_fix_plan'),
                        event('casegraph_get_case', True), event('casegraph_search_solutions', True)])
    assert claude_agent.tool_call_status(output) == [dict(server='casegraph', tool='casegraph_fix_plan', ok=True),
        dict(server='casegraph', tool='casegraph_get_case', ok=False),
        dict(server='casegraph', tool='casegraph_search_solutions', ok=False)]


def test_read_proxy_hides_writes_and_rejects_even_a_direct_write_call():
    class Upstream:
        def __init__(self): self.calls = []
        def list_tools(self): return [dict(name=n) for n in ('get_element_info', 'move_element', 'delete_element')]
        def request(self, method, params):
            self.calls.append((method, params))
            return dict(content=[dict(type='text', text='read result')])
    upstream = Upstream()
    def call(method, **params):
        return planning_mcp.handle(dict(id=1, method=method, params=params), upstream, 'revit')
    assert call('tools/list')['result']['tools'] == [dict(name='get_element_info')]
    assert 'error' in call('tools/call', name='move_element', arguments=dict(elementId=1, dx=100))
    assert not upstream.calls
    assert 'result' in call('tools/call', name='get_element_info', arguments=dict(elementId=1))
    assert len(upstream.calls) == 1


def test_stream_result_must_be_successful_and_structured():
    value = dict(summary='ok', steps=[])
    assert claude_agent.claude_result(json.dumps(dict(type='result', structured_output=value))) == value
    for result in (dict(type='result', is_error=True, subtype='error_max_budget_usd'), dict(type='result', result='plain text')):
        with pytest.raises(ValueError, match='planning failed'):
            claude_agent.claude_result(json.dumps(result))


def test_parse_plan_decodes_arguments_and_blocks_all_steps_when_one_is_invalid():
    def raw(tool, arguments):
        return dict(tool=tool, arguments_json=json.dumps(arguments), reason='調整', source_case=None)
    result = claude_agent.parse_plan(dict(summary='門', steps=[raw('flip_element', dict(elementId=1))]))
    assert result['steps'][0]['arguments'] == dict(elementId=1)
    result = claude_agent.parse_plan(dict(steps=[raw('flip_element', dict(elementId=1)), raw('delete_element', dict(elementId=-1))]))
    assert not result['steps'] and result['problems']
    with pytest.raises(ValueError, match='invalid tool arguments'):
        claude_agent.parse_plan(dict(steps=[dict(tool='flip_element', arguments_json='invalid')]))


def test_parameter_normalization_happens_before_approval_only():
    raw = step('modify_element_parameter', parameterName='Width', value=3.28)
    validated, problems = validate_plan(dict(steps=[raw]))
    assert not problems and validated[0]['arguments']['value'] == '3.28'
    client = FakeMcp()
    with pytest.raises(ValueError, match='rewriting'):
        claude_agent.apply_steps([raw], client, SCOPE)
    assert not client.calls
    claude_agent.apply_steps(validated, client, SCOPE)
    assert client.calls == [('modify_element_parameter', dict(elementId=101, parameterName='Width', value='3.28'))]


def test_project_config_does_not_inherit_unrelated_global_servers(monkeypatch, tmp_path):
    monkeypatch.setattr(claude_agent, 'ROOT', tmp_path)
    monkeypatch.delenv('CASEGRAPH_MCP_CONFIG', raising=False)
    monkeypatch.setattr(Path, 'home', classmethod(lambda cls: tmp_path))
    (tmp_path / '.claude.json').write_text(json.dumps(dict(mcpServers=dict(revit=dict(command='claude-only')))))
    with pytest.raises(ValueError, match='Configure'):
        claude_agent.revit_server()
    (tmp_path / '.mcp.json').write_text(json.dumps(dict(mcpServers=dict(revit=dict(command='node', args=['server.js'])))))
    assert claude_agent.revit_server()['command'] == 'node'


def test_diagnostics_record_provider_and_state_without_model_data(monkeypatch, tmp_path):
    monkeypatch.setattr(claude_agent, 'ROOT', tmp_path)
    status = claude_agent.RunStatus('plan')
    status.update('completed', step_count=2)
    saved = json.loads(status.path.read_text(encoding='utf-8'))
    assert saved['provider'] == 'Claude' and saved['state'] == 'completed'
    assert saved['step_count'] == 2 and saved['operation'] == 'plan'
    assert not {'prompt', 'arguments', 'target', 'api_key'} & saved.keys()


def fake_server(tmp_path):
    path = tmp_path / 'fake_mcp.py'
    path.write_text('''import json, sys, time
for line in sys.stdin:
    m = json.loads(line)
    if 'id' not in m: continue
    method, p = m['method'], m.get('params', {})
    if method == 'initialize': result = {}
    elif method == 'tools/list':
        result = {'tools': [{'name': 'second'}]} if p.get('cursor') else {'tools': [{'name': 'first'}], 'nextCursor': 'next'}
    elif p['name'] == 'timeout':
        time.sleep(20)
        continue
    elif p['name'] == 'crash': sys.exit(2)
    elif p['name'] == 'protocol_error':
        print(json.dumps({'jsonrpc': '2.0', 'id': m['id'], 'error': {'code': -32602, 'message': 'bad args'}}), flush=True)
        continue
    else:
        result = {'content': [{'type': 'text', 'text': json.dumps({'Success': p['name'] != 'failure', 'Error': 'cannot flip'})}]}
        if p['name'] == 'error': result['isError'] = True
    print(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/message', 'params': {}}), flush=True)
    print(json.dumps({'jsonrpc': '2.0', 'id': m['id'], 'result': result}), flush=True)
''', encoding='utf-8')
    return dict(command=sys.executable, args=['-u', str(path)])


def test_stdio_handshake_notifications_pagination_and_tool_errors(tmp_path):
    with McpClient(fake_server(tmp_path), timeout=2) as client:
        assert [t['name'] for t in client.list_tools()] == ['first', 'second']
        assert 'true' in client.call('ok', dict(elementId=1))
        for tool in ('error', 'failure'):
            with pytest.raises(ValueError, match='cannot flip'):
                client.call(tool, {})
        with pytest.raises(ValueError, match='bad args'):
            client.call('protocol_error', {})
        process = client.process
    assert process.poll() is not None


@pytest.mark.parametrize('tool,exception', [('timeout', TimeoutError), ('crash', EOFError)])
def test_stdio_disconnect_and_deadline_release_process(tmp_path, tool, exception):
    client = McpClient(fake_server(tmp_path), timeout=2)
    with pytest.raises(exception):
        with client:
            client.call(tool, {})
    assert client.process.poll() is not None


def test_planning_proxy_checks_steps_locally_without_forwarding():
    class Upstream:
        def __init__(self): self.calls = []
        def list_tools(self): return [dict(name='casegraph_get_case')]
        def request(self, method, params): self.calls.append(params)
    upstream = Upstream()
    def call(method, **params):
        return planning_mcp.handle(dict(id=1, method=method, params=params), upstream, 'casegraph')
    assert 'casegraph_check_steps' in [t['name'] for t in call('tools/list')['result']['tools']]
    def check(steps):
        return json.loads(call('tools/call', name='casegraph_check_steps', arguments=dict(steps=steps))['result']['content'][0]['text'])
    assert not check([])['ok']
    assert check([step(flipType='hand')])['ok']
    assert check([step(), step('move_element', dx='far')])['problems']
    assert not upstream.calls
    # The Revit proxy never offers it.
    assert 'error' in planning_mcp.handle(dict(id=1, method='tools/call', params=dict(name='casegraph_check_steps')), upstream, 'revit')


def fake_claude(monkeypatch, answers):
    prompts = []
    def run(command, folder, prompt, timeout):
        prompts.append(prompt)
        answer = answers.pop(0)
        if isinstance(answer, Exception):
            raise answer
        return json.dumps(dict(type='result', structured_output=answer))
    monkeypatch.setattr(claude_agent, 'find_claude', lambda: 'claude.exe')
    monkeypatch.setattr(claude_agent, 'revit_server', lambda: dict(command='node'))
    monkeypatch.setattr(claude_agent, 'run_claude', run)
    monkeypatch.setattr(claude_agent, 'read_plan_elements', lambda request, server: [dict(ElementId=101, Name='牆')])
    return prompts


def answer(steps=(), questions=(), resolved=False, resolved_ids=None):
    return dict(summary='查詢完成', already_resolved=resolved, warnings=[], questions=list(questions),
                resolution=dict(element_ids=resolved_ids if resolved_ids is not None else [101], evidence='本次讀值符合要求' if resolved else ''),
                steps=[dict(tool=s['tool'], arguments_json=json.dumps(s['arguments']), reason='依 RFI-1', source_case=1) for s in steps])


def plan_request():
    return dict(target=dict(properties=dict(element_id='101'), name='牆', source_document='doc'),
                solutions=[dict(case_id=1, title='修正牆', solution='移動', verified=True)],
                base_url='http://127.0.0.1:8000')


def test_empty_plan_retries_with_feedback_and_returns_the_steps(monkeypatch):
    prompts = fake_claude(monkeypatch, [answer(questions=['開口要移到哪裡？']), answer([step('move_element', dy=-200)])])
    value = claude_agent.plan(plan_request())
    assert value['steps'][0]['arguments'] == dict(elementId=101, dy=-200) and value['attempts'] == 2
    assert len(prompts) == 2 and '開口要移到哪裡' in prompts[1] and '上一次規劃沒有產生可執行步驟' in prompts[1]


def test_invalid_step_problems_are_fed_back_to_the_retry(monkeypatch):
    prompts = fake_claude(monkeypatch, [answer([step('delete_element', extra=1)]), answer([step('delete_element')])])
    assert claude_agent.plan(plan_request())['steps'] and '不支援的工具參數' in prompts[1]


@pytest.mark.parametrize('first', [answer([step()]), answer(resolved=True)])
def test_valid_or_already_resolved_plans_are_not_retried(monkeypatch, first):
    prompts = fake_claude(monkeypatch, [first])
    assert claude_agent.plan(plan_request())['attempts'] == 1 and len(prompts) == 1


def test_already_resolved_cannot_hide_invalid_steps():
    raw = answer([dict(tool='delete_element', arguments=dict(elementId=None))], resolved=True)
    assert not claude_agent.parse_plan(raw)['already_resolved']


def test_repaired_counterpart_does_not_mark_a_second_pair_resolved(monkeypatch):
    request = plan_request()
    request['members'] = [dict(request['target'], properties=dict(element_id='102'))]
    fake_claude(monkeypatch, [answer(resolved=True, resolved_ids=[101, 102])])
    first = claude_agent.plan(request)
    assert first['already_resolved'] and first['scope']['element_ids'] == [101, 102]
    request['members'] = [dict(request['target'], properties=dict(element_id='103'))]
    move = dict(tool='move_element', arguments=dict(elementId=103, dx=100), reason='第二組', source_case=1)
    prompts = fake_claude(monkeypatch, [answer(resolved=True, resolved_ids=[101, 102]), answer([move])])
    second = claude_agent.plan(request)
    assert not second['already_resolved'] and second['steps'][0]['arguments']['elementId'] == 103
    assert second['scope']['element_ids'] == [101, 103] and second['attempts'] == 2
    assert '全部選取物' in prompts[1] and '其他碰撞' in prompts[0]


def test_plan_cannot_expand_scope_to_other_elements_from_case_or_queries(monkeypatch):
    outside = dict(tool='move_element', arguments=dict(elementId=999, dx=100), reason='其他開口', source_case=1)
    prompts = fake_claude(monkeypatch, [answer([step(), outside]), answer([step()])])
    result = claude_agent.plan(plan_request())
    assert len(result['steps']) == 1 and result['attempts'] == 2
    assert '999 不在本次目標及選取物範圍' in prompts[1]


@pytest.mark.parametrize('scope', [None, dict(document='doc', target_id=101, element_ids=[101, True])])
def test_executor_requires_a_valid_frozen_scope_before_any_call(scope):
    client = FakeMcp()
    client.list_tools = lambda: pytest.fail('Invalid scope must stop before MCP discovery or writes')
    with pytest.raises(ValueError, match='修正範圍'):
        claude_agent.apply_steps([step()], client, scope)


def test_executor_refuses_out_of_scope_steps_and_opening_counterparts_before_writes():
    outside = dict(tool='move_element', arguments=dict(elementId=102, dx=100), reason='', source_case=None)
    opening = step('create_wall_opening', ductId=102, widthMm=400, heightMm=500, expectedGeometry='a'*64)
    for proposed in (outside, opening):
        client = FakeMcp()
        with pytest.raises(ValueError, match='102 不在本次'):
            claude_agent.apply_steps([step(), proposed], client, SCOPE)
        assert not client.calls
    client = FakeMcp()
    scoped = dict(SCOPE, element_ids=[101, 102])
    assert all(r['ok'] for r in claude_agent.apply_steps([outside, opening], client, scoped)['results'])


def test_resolved_requires_current_scope_and_measurement_evidence():
    value = answer(resolved=True)
    value['resolution']['evidence'] = ''
    result = claude_agent.parse_plan(value, SCOPE)
    assert not result['already_resolved'] and result['problems']


def test_failed_retry_keeps_the_first_answer(monkeypatch):
    fake_claude(monkeypatch, [answer(questions=['?']), ValueError('Claude planning timed out.')])
    value = claude_agent.plan(plan_request())
    assert value['questions'] == ['?'] and not value['steps'] and value['attempts'] == 1


def test_plan_worker_timeout_kills_child_and_returns_no_plan(monkeypatch):
    class Hung:
        def __init__(self, *args, **kwargs):
            self.stopped = False
        def communicate(self, *args, **kwargs):
            if not self.stopped:
                raise subprocess.TimeoutExpired('claude', 240)
            return '', ''
    process = Hung()
    monkeypatch.setattr(claude_agent, 'find_claude', lambda: 'claude.exe')
    monkeypatch.setattr(claude_agent, 'revit_server', lambda: dict(command='node'))
    monkeypatch.setattr(claude_agent.subprocess, 'Popen', lambda *a, **kw: process)
    monkeypatch.setattr(claude_agent, 'stop_process', lambda p: setattr(p, 'stopped', True))
    monkeypatch.setattr(claude_agent, 'read_plan_elements', lambda request, server: [dict(ElementId=101)])
    with pytest.raises(ValueError, match='timed out'):
        claude_agent.plan(plan_request())
    assert process.stopped


def test_ai_plan_uses_live_read_and_prompt_without_case_material(monkeypatch):
    prompts = fake_claude(monkeypatch, [answer([step('move_element', dx=100)])])
    request = dict(plan_request(), mode='ai', instruction='向右移動 100 mm',
                   context=dict(message='維修空間不足', advice='保留檢修範圍'))
    result = claude_agent.plan(request)
    assert '向右移動 100 mm' in prompts[0] and '維修空間不足' in prompts[0]
    assert '"ElementId": 101' in prompts[0] and '修正牆' not in prompts[0]
    assert result['steps'][0]['source_case'] is None


def test_ai_proxy_cannot_search_or_read_cases_but_can_check_steps(tmp_path):
    class Upstream:
        def list_tools(self): return [dict(name='casegraph_get_case')]
        def request(self, *args): raise AssertionError('AI mode must not access cases')
    upstream = Upstream()
    message = dict(id=1, method='tools/call', params=dict(name='casegraph_get_case', arguments=dict(case_id=1)))
    assert 'error' in planning_mcp.handle(message, upstream, 'casegraph', 'ai')
    tools = planning_mcp.handle(dict(id=2, method='tools/list'), upstream, 'casegraph', 'ai')['result']['tools']
    assert [tool['name'] for tool in tools] == ['casegraph_check_steps']
    command = claude_agent.claude_command('claude.exe', tmp_path, dict(command='node'), 'http://localhost', mode='ai')
    assert 'casegraph_get_case' not in command[command.index('--allowedTools')+1]
    assert all(server['args'][-2:] == ['--mode', 'ai'] for server in
               json.loads((tmp_path / 'mcp.json').read_text())['mcpServers'].values())


@pytest.mark.parametrize('mode,sources,instruction', [('ai', [], ''), ('cases', [], '修正'), ('other', [], '修正')])
def test_invalid_suggestion_input_stops_before_starting_ai(monkeypatch, mode, sources, instruction):
    monkeypatch.setattr(claude_agent, 'find_claude', lambda: pytest.fail('Must validate input first'))
    with pytest.raises(ValueError):
        claude_agent.plan(dict(plan_request(), mode=mode, solutions=sources, instruction=instruction))


def test_mcp_reads_all_pinned_elements_before_planning_and_refuses_failed_reads(monkeypatch):
    calls = []
    class Client:
        def __init__(self, server): pass
        def __enter__(self): return self
        def __exit__(self, *args): pass
        def call(self, name, args):
            calls.append((name, args))
            return json.dumps(dict(ElementId=args['elementId'], Parameters=[dict(Name='Width', Value='900')]))
    monkeypatch.setattr(claude_agent, 'McpClient', Client)
    request = plan_request()
    request['members'] = [dict(request['target'], properties=dict(element_id='102'))]
    assert len(claude_agent.read_plan_elements(request, {})) == 2
    assert calls == [('get_element_info', dict(elementId=101)), ('get_element_info', dict(elementId=102))]
    monkeypatch.setattr(claude_agent, 'find_claude', lambda: 'claude.exe')
    monkeypatch.setattr(claude_agent, 'revit_server', lambda: {})
    monkeypatch.setattr(claude_agent, 'run_claude', lambda *args: pytest.fail('Failed read must stop planning'))
    Client.call = lambda self, name, args: json.dumps(dict(ElementId=999))
    with pytest.raises(ValueError, match='MCP'):
        claude_agent.plan(request)
    request['context'] = dict(source_refs=[dict(source='MEP link', element_id=101)])
    with pytest.raises(ValueError, match='連結模型'):
        claude_agent.read_plan_elements(request, {})
