"""Read-only stdio MCP adapter for suggestion planning; forwards to the real MCP server."""
import argparse
import json
from pathlib import Path
import sys

from revit_mcp_client import McpClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'CaseBase.extension/lib'))
from casegraph.suggestion import CHECK_TOOL, read_tools, check_steps

# Answered here, never forwarded: validates proposed steps without touching a model.
CHECK = dict(name=CHECK_TOOL, annotations=dict(readOnlyHint=True, destructiveHint=False, idempotentHint=True),
             description='Validate proposed Revit MCP modification steps with the same rules the executor uses. '
                         'Call before returning a plan and fix every reported problem; any invalid step blocks the whole plan.',
             inputSchema=dict(type='object', required=['steps'], properties=dict(steps=dict(type='array', items=dict(
                 type='object', required=['tool', 'arguments'], properties=dict(
                     tool=dict(type='string'), arguments=dict(type='object'), reason=dict(type='string'),
                     source_case=dict(type=['integer', 'null'])))))))


def handle(message, client, server, mode='cases'):
    if 'id' not in message:
        return None
    response = dict(jsonrpc='2.0', id=message['id'])
    prefix = 'mcp__' + server + '__'
    allowed = {name[len(prefix):] for name in read_tools(mode) if name.startswith(prefix)}
    method, params = message['method'], message.get('params') or {}
    if method == 'initialize':
        response['result'] = dict(protocolVersion='2025-06-18', capabilities=dict(tools={}),
                                  serverInfo=dict(name='casegraph-planning-'+server, version='1.0.0'))
    elif method == 'tools/list':
        response['result'] = dict(tools=[tool for tool in client.list_tools() if tool['name'] in allowed]
                                  + ([CHECK] if CHECK_TOOL in allowed else []))
    elif method == 'tools/call':
        if params.get('name') == CHECK_TOOL and CHECK_TOOL in allowed:
            value = check_steps((params.get('arguments') or {}).get('steps'))
            response['result'] = dict(content=[dict(type='text', text=json.dumps(value, ensure_ascii=False))])
        elif params.get('name') not in allowed:
            response['error'] = dict(code=-32602, message='Only named read tools are available during planning.')
        else:
            response['result'] = client.request('tools/call', params)
    elif method == 'ping':
        response['result'] = {}
    else:
        response['error'] = dict(code=-32601, message='Method not supported')
    return response


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True)
    parser.add_argument('--server', required=True, choices=['revit', 'casegraph'])
    parser.add_argument('--mode', choices=['cases', 'ai'], default='cases')
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text(encoding='utf-8'))
    with McpClient(config['mcpServers'][args.server]) as client:
        for line in sys.stdin:
            message = None
            try:
                message = json.loads(line)
                response = handle(message, client, args.server, args.mode)
            except Exception as error:
                response = dict(jsonrpc='2.0', id=message.get('id') if isinstance(message, dict) else None,
                                error=dict(code=-32603, message=str(error)))
            if response is not None:
                print(json.dumps(response, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8')
    main()
