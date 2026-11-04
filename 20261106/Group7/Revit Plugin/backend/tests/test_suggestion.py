import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension' / 'lib'))
from casegraph.suggestion import (PLAN_SCHEMA, READ_TOOLS, plan_prompt, validate_plan, step_caption, apply_prompt,
                                  apply_tools, quote, command_arguments, parse_output, outcome)


def target():
    return dict(node_id='doc:door', name='門 D-01', source_document='doc', source_element_id='door',
                properties=dict(element_id='334206', type_id='100', category='Doors', family='單開門', type_name='900 x 2100'))


def test_plan_prompt_carries_target_solutions_instruction_and_safety_rules():
    text = plan_prompt(target(), [dict(case_id=3, title='門寬不足', verified=True, status='VERIFIED',
                                       solution='改用 1000 寬門型', replayable_steps=2)], '門太窄，照之前的做法')
    assert '"element_id": "334206"' in text and '"case_id": 3' in text and '門太窄，照之前的做法' in text
    assert '絕對不要修改模型' in text and 'casegraph_fix_plan' in text and '304.8' in text
    # The plan run may read but never modify.
    assert not any(t.endswith(('modify_element_parameter', 'change_element_type', 'move_element', 'flip_element', 'delete_element'))
                   for t in READ_TOOLS)
    assert '（沒有）' in plan_prompt(target(), [], '')
    assert 'mcp__revit__get_element_geometry' in READ_TOOLS
    assert 'replayable_steps=0' in text and 'casegraph_get_case' in text
    assert 'expect_before/expect_after' in text and 'Volume' in text


def test_repair_scope_is_exact_and_rejects_other_documents_and_type_writes():
    from casegraph.suggestion import repair_scope, scope_problems
    root = target()
    selected = dict(root, properties=dict(element_id='883479'))
    scope = repair_scope(root, [selected, selected])
    assert scope['element_ids'] == [334206, 883479]
    with pytest.raises(ValueError, match='其他文件'):
        repair_scope(root, [dict(selected, source_document='another-model')])
    with pytest.raises(ValueError, match='有效元素 ID'):
        repair_scope(dict(root, properties=dict(element_id=True)))
    assert scope_problems([dict(arguments=dict(elementId=999))], scope)
    assert not scope_problems([dict(arguments=dict(elementId=883479, typeId=999))], scope)


def test_only_complete_steps_for_allowed_tools_survive():
    plan = dict(steps=[
        dict(tool='modify_element_parameter', arguments=dict(elementId=334206, parameterName='Width', value=3.28), reason='加寬', source_case=3),
        dict(tool='flip_element', arguments=dict(elementId=334206, flipType='facing'), reason='反轉'),
        dict(tool='execute_csharp', arguments=dict(elementId=1), reason='x'),
        dict(tool='move_element', arguments=dict(elementId='334206', dx=10), reason='x'),
        dict(tool='change_element_type', arguments=dict(elementId=334206), reason='x'),
        dict(tool='modify_element_parameter', arguments=dict(elementId=334206, parameterName='Mark'), reason='x')])
    steps, problems = validate_plan(plan)
    assert [s['tool'] for s in steps] == ['modify_element_parameter', 'flip_element']
    assert steps[0]['arguments']['value'] == '3.28'  # Revit MCP expects a string value.
    assert len(problems) == 4 and '不允許的工具' in problems[0]
    assert step_caption(1, steps[0]) == '1. 修改參數 · 元素 334206 · Width = 3.28（Revit 內部單位）'
    assert step_caption(2, steps[1]) == '2. 翻轉 · 元素 334206 · facing'


def test_apply_run_gets_exactly_the_approved_steps_and_their_tools():
    steps = [dict(tool='flip_element', arguments=dict(elementId=5, flipType='hand'), reason='r', source_case=None)]
    text = apply_prompt(steps)
    assert json.loads(text[text.index('['):]) == [dict(step=1, tool='mcp__revit__flip_element', arguments=dict(elementId=5, flipType='hand'))]
    assert apply_tools(steps) == ['mcp__revit__flip_element', 'mcp__revit__get_element_info']


def test_opening_plan_requires_explicit_sizes_identity_and_fresh_preview_token():
    args = dict(elementId=101, ductId=102, widthMm=450, heightMm=550, expectedGeometry='a' * 64)
    step = dict(tool='create_wall_opening', arguments=args, reason='依 SOLVE 尺寸', source_case=42)
    assert validate_plan(dict(steps=[step])) == ([step], [])
    assert '450 × 550 mm' in step_caption(1, step)
    for key, value in [('ductId', True), ('ductId', '102'), ('widthMm', 0), ('heightMm', -1),
                       ('offsetZMm', float('nan')), ('widthMm', float('inf')), ('expectedGeometry', ''),
                       ('expectedGeometry', 'a' * 64 + '\n'), ('unexpected', 'value')]:
        altered = dict(step, arguments=dict(args, **{key: value}))
        accepted, errors = validate_plan(dict(steps=[altered]))
        assert not accepted and errors, key
    assert 'mcp__revit__preview_wall_opening' in READ_TOOLS
    assert 'mcp__revit__create_wall_opening' not in READ_TOOLS
    prompt = plan_prompt(target(), [], '所有都參考 SOLVE')
    assert 'snapshot' in prompt and 'expectedGeometry' in prompt and 'already_exists' in prompt


def windows_argv(line):
    """Parse a command line exactly as claude.exe will, with CommandLineToArgvW."""
    import ctypes
    from ctypes import wintypes
    parse = ctypes.windll.shell32.CommandLineToArgvW
    parse.argtypes, parse.restype = [wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_int)], ctypes.POINTER(wintypes.LPWSTR)
    count = ctypes.c_int()
    argv = parse('claude.exe ' + line, ctypes.byref(count))
    try:
        return [argv[i] for i in range(1, count.value)]
    finally:
        ctypes.windll.kernel32.LocalFree(argv)


@pytest.mark.skipif(sys.platform != 'win32', reason='Windows command-line parsing')
@pytest.mark.parametrize('value', ['', 'plain', 'with space', 'a"b', 'C:\\Program Files\\x\\', '{"type":"object"}', 'x\\"y', '中文 路徑'])
def test_quoting_round_trips_through_windows_parsing(value):
    assert windows_argv(quote(value)) == [value]


@pytest.mark.skipif(sys.platform != 'win32', reason='Windows command-line parsing')
def test_whole_command_line_round_trips():
    argv = windows_argv(command_arguments('C:\\cfg dir\\agent-mcp.json', PLAN_SCHEMA, READ_TOOLS))
    assert json.loads(argv[argv.index('--json-schema')+1]) == PLAN_SCHEMA and argv[argv.index('--tools')+1] == ''


def test_command_restricts_tools_servers_and_permissions():
    line = command_arguments('C:\\cfg dir\\agent-mcp.json', PLAN_SCHEMA, READ_TOOLS)
    assert line.startswith('-p --output-format json --json-schema "{') and ' --tools "" ' in line
    assert '--strict-mcp-config --mcp-config "C:\\cfg dir\\agent-mcp.json"' in line
    assert '--permission-mode dontAsk' in line and '--no-session-persistence' in line
    assert '--allowedTools ' + ','.join(READ_TOOLS) in line


def test_structured_output_parsing_and_outcomes():
    assert parse_output(json.dumps(dict(type='result', is_error=False, structured_output=dict(steps=[])))) == dict(steps=[])
    fenced = json.dumps(dict(is_error=False, result='好的：\n```json\n{"summary": "s", "steps": []}\n```'))
    assert parse_output(fenced) == dict(summary='s', steps=[])
    with pytest.raises(ValueError, match='執行失敗'):
        parse_output(json.dumps(dict(is_error=True, result='budget exceeded')))
    with pytest.raises(ValueError, match='可讀'):
        parse_output('Error: not logged in')
    assert outcome([dict(ok=True), dict(ok=True)], 2) == 'APPLIED'
    assert outcome([dict(ok=True), dict(ok=False)], 2) == 'PARTIAL'
    assert outcome([], 2) == 'FAILED'
