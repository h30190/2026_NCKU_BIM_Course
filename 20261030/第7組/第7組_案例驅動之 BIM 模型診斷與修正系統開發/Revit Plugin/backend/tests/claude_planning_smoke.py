"""Opt-in Claude Code approval smoke test using exclusively synthetic MCP data.

Run manually; uses the signed-in Claude Code account. Never connects to Revit, SQLite
or the CaseGraph HTTP service. No actual cases or model contents are read.
"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import casegraph_mcp
import claude_agent


def fixture_api(path, payload=None):
    if path == '/cases/1/fix-plan':
        return dict(case_id=1, title='SYNTHETIC CASE - NO REAL MODEL DATA', ready=False,
                    steps=[dict(step=1, unresolved=['Synthetic missing type ID'])])
    raise ValueError('The synthetic fixture only supports /cases/1/fix-plan.')


def serve(role):
    for line in sys.stdin:
        message = json.loads(line)
        if role == 'casegraph':
            response = casegraph_mcp.handle(message, fixture_api)
        elif 'id' not in message:
            response = None
        else:
            method = message['method']
            if method == 'initialize':
                result = dict(protocolVersion=message['params']['protocolVersion'], capabilities=dict(tools={}),
                              serverInfo=dict(name='synthetic-revit', version='1'))
            elif method == 'tools/list':
                result = dict(tools=[dict(name='get_element_info', description='Read an artificial test fixture; never accesses Revit.',
                    inputSchema=dict(type='object', properties=dict(elementId=dict(type='integer')), required=['elementId']),
                    annotations=dict(readOnlyHint=True, destructiveHint=False, openWorldHint=False, idempotentHint=True))])
            elif method == 'tools/call':
                if message['params']['name'] != 'get_element_info':
                    raise ValueError('Unsupported synthetic tool')
                result = dict(content=[dict(type='text', text=json.dumps(dict(
                    ElementId=1, Name='SYNTHETIC WALL - NO REAL MODEL DATA', TypeId=2)))])
            else:
                result = {}
            response = dict(jsonrpc='2.0', id=message['id'], result=result)
        if response is not None:
            print(json.dumps(response), flush=True)


def smoke():
    fixture = str(Path(__file__).resolve())
    revit = dict(command=sys.executable, args=['-I', fixture, '--serve', 'revit'])
    with tempfile.TemporaryDirectory(prefix='casegraph-synthetic-') as directory:
        folder = Path(directory)
        command = claude_agent.claude_command(claude_agent.find_claude(), folder, revit, 'http://127.0.0.1:1',
            casegraph_server=dict(command=sys.executable, args=['-I', fixture, '--serve', 'casegraph']))
        prompt = ('This is an integration test with completely fictional data. No real Revit instance or database is connected. '
                  'Call revit get_element_info with elementId=1, then call casegraph casegraph_fix_plan with case_id=1 '
                  'and target_element_id=1. Report whether both tool calls succeeded and the unresolved count in the '
                  'summary. Return steps=[], warnings=[], questions=[]. Do not call other tools. Use the actual tool results.')
        result = subprocess.run(command, input=prompt, capture_output=True, encoding='utf-8', timeout=120,
                                creationflags=claude_agent.CREATE_NO_WINDOW, cwd=str(folder))
        if result.returncode:
            raise RuntimeError((result.stderr or result.stdout)[-1800:])
        calls = claude_agent.tool_call_status(result.stdout)
        print(json.dumps(dict(synthetic=True, tool_calls=calls)))
        for server, tool in [('revit', 'get_element_info'), ('casegraph', 'casegraph_fix_plan')]:
            assert any(call['server'] == server and call['tool'] == tool and call['ok'] for call in calls), (server, tool)
        plan = claude_agent.parse_plan(claude_agent.claude_result(result.stdout))
        assert plan['steps'] == []
        print('PASS: Claude read both synthetic MCP tools with dontAsk and the read allowlist; no writes or real model data.')


if __name__ == '__main__':
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8')
    if len(sys.argv) == 3 and sys.argv[1] == '--serve':
        serve(sys.argv[2])
    else:
        smoke()
