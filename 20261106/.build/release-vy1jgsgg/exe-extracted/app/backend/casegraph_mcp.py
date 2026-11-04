"""CaseGraph MCP server (stdio): recorded BIM solutions for an AI agent.

Used together with the Revit MCP server: CaseGraph finds a recorded solution and
turns its model-change evidence into Revit MCP tool calls; the agent shows the
plan, asks the user, applies it with the Revit tools and records the outcome.
Standard library only; the CaseGraph backend stays the single database writer.
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'CaseBase.extension' / 'lib'))
from casegraph.runtime import python_executable

BASE = os.environ.get('CASEGRAPH_URL', 'http://127.0.0.1:8000').rstrip('/')
VERSION = '1.1.0'
INSTRUCTIONS = '''CaseGraph holds BIM issue cases and reviewed solutions recorded in Revit.
Workflow for "fix this like before":
1. Identify the element with the Revit MCP tools (get_selected_elements, get_element_info).
2. casegraph_search_solutions with the user's description plus the element's category/family/type.
3. Always casegraph_get_case and casegraph_fix_plan for the chosen case, even when the search reports zero replayable steps.
   Read the recorded snapshot, solution and movement before/after positions. Older generated movement records can be recovered.
   Resolve every `unresolved` item (related element IDs, target type IDs) with Revit MCP tools.
4. Read get_element_geometry with geometryType=boundingbox for positions (millimetres); get_element_info alone lacks geometry.
   Compare live geometry with expect_before and expect_after. Do not repeat an already completed move.
   Map related elements using related["element:<recorded ID>"]; never reuse old IDs across models without resolving them.
   Computed/read-only values such as Volume are observations, not commands. Exhaust read tools before asking for model data.
   Show all steps, notes and warnings
   to the user and get explicit confirmation BEFORE calling any Revit MCP tool that changes the model.
5. Apply step by step with the named Revit MCP tool and arguments; stop on the first failure.
6. Check that the problem is gone, then call casegraph_record_application with the outcome.
Prefer verified solutions; say clearly when a solution is unverified. Never invent steps.'''

TOOLS = [
    dict(name='casegraph_search_solutions',
         annotations=dict(readOnlyHint=True, destructiveHint=False, idempotentHint=True, openWorldHint=False),
         description='Search recorded BIM issue solutions by natural-language description and element type. '
                     'Returns ranked cases with their solution text and how many steps can be replayed automatically.',
         inputSchema=dict(type='object', properties=dict(
             query=dict(type='string', description='Problem description in any language, e.g. "門和牆碰撞" or "door swing blocked"'),
             category=dict(type='string', description='Revit category of the problem element, e.g. Doors, Walls'),
             family=dict(type='string', description='Family name of the problem element'),
             type_name=dict(type='string', description='Type name of the problem element'),
             issue_type=dict(type='string', enum=['ALL', 'GEOMETRY_CLASH', 'SPATIAL_CLEARANCE', 'PARAMETER_ERROR', 'IFC_MAPPING_ERROR',
                                                  'CONNECTION_ERROR', 'DESIGN_CONFLICT', 'CONSTRUCTION_ISSUE', 'OTHER']),
             verified_only=dict(type='boolean', default=True, description='Only human-verified solutions (default true)'),
             recipe_only=dict(type='boolean', default=False, description='Only solutions with replayable model changes'),
             limit=dict(type='integer', default=5, minimum=1, maximum=20)))),
    dict(name='casegraph_get_case',
         annotations=dict(readOnlyHint=True, destructiveHint=False, idempotentHint=True, openWorldHint=False),
         description='Read one case: full solution text, review history, original element/geometry snapshot, recipe and recent applications. '
                     'Read this even when search reports zero replayable steps; old records may only have solution text.',
         inputSchema=dict(type='object', required=['case_id'], properties=dict(case_id=dict(type='integer')))),
    dict(name='casegraph_fix_plan',
         annotations=dict(readOnlyHint=True, destructiveHint=False, idempotentHint=True, openWorldHint=False),
         description='Turn a case\'s recorded model changes into Revit MCP tool calls for a target element. '
                     'Values are converted to Revit internal units. Steps with `unresolved` items are not ready. '
                     'This never changes the model.',
         inputSchema=dict(type='object', required=['case_id', 'target_element_id'], properties=dict(
             case_id=dict(type='integer'),
             target_element_id=dict(type='integer', description='Revit element ID of the element to fix'),
             target_type_id=dict(type='integer', description='Element ID of its type (needed for type parameters)'),
             target_category=dict(type='string'), target_family=dict(type='string'), target_type_name=dict(type='string'),
             target_document=dict(type='string', description='CaseGraph document identity, if known'),
             related=dict(type='object', additionalProperties=dict(type='integer'),
                          description='Target-model IDs keyed by "element:<recorded ID>", e.g. {"element:123": 456}. '
                                      'A relation key such as HOSTED_BY works only when it identifies one recorded element.'),
             type_ids=dict(type='object', additionalProperties=dict(type='integer'),
                           description='Target-model type IDs keyed by "family／type_name" as named in unresolved items')))),
    dict(name='casegraph_record_application',
         annotations=dict(readOnlyHint=False, destructiveHint=False, idempotentHint=False, openWorldHint=False),
         description='Record the outcome of applying a solution, so future searches know whether it worked.',
         inputSchema=dict(type='object', required=['case_id', 'outcome'], properties=dict(
             case_id=dict(type='integer'),
             outcome=dict(type='string', enum=['APPLIED', 'PARTIAL', 'FAILED', 'REJECTED']),
             element_ids=dict(type='array', items=dict(type='integer')),
             steps=dict(type='array', items=dict(type='object'), description='Steps actually executed and their results'),
             resolved=dict(type='boolean', description='Whether a re-check confirmed the problem is gone'),
             document=dict(type='string'), notes=dict(type='string')))),
]


def http(path, payload=None):
    data = None if payload is None else json.dumps(payload).encode('utf-8')
    request = Request(BASE+'/graph'+path, data=data, headers={'Content-Type': 'application/json'})
    try:
        with urlopen(request, timeout=30) as response:
            return json.load(response)
    except HTTPError as error:
        detail = error.read().decode('utf-8', 'replace')
        try:
            detail = json.loads(detail).get('detail', detail)
        except ValueError:
            pass
        raise ValueError('CaseGraph {}: {}'.format(error.code, detail))


REQUIRED_API = 6


def current(status):
    if status.get('api_version', 0) < REQUIRED_API:
        raise ValueError('The running CaseGraph backend is outdated (API {}); restart it with Start-CaseGraph.ps1.'.format(
            status.get('api_version')))
    return status


def ensure_backend():
    try:
        return current(http('/health'))
    except (URLError, OSError):
        pass
    root = Path(__file__).resolve().parent
    python = python_executable(str(root.parent), windowless=True)
    subprocess.Popen([python, str(root/'run_backend.py')], cwd=str(root.parent),
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(20):
        time.sleep(.5)
        try:
            return current(http('/health'))
        except (URLError, OSError):
            continue
    raise ValueError('CaseGraph backend is not running; run Start-CaseGraph.ps1 in the workspace.')


def compact_case(case, recipe):
    draft = case['draft']
    graph = draft['graph']
    fields = ('element_id', 'category', 'family', 'type_name', 'type_id', 'location', 'bounding_box',
              'units', 'FacingOrientation', 'HandOrientation', 'host_id')
    nodes = [dict(node_id=n['node_id'], name=n['name'], source_document=n['source_document'],
                  unique_id=n.get('source_element_id'), properties={k: n['properties'][k] for k in fields if k in n['properties']})
             for n in graph['nodes'] if n['node_type'] == 'ELEMENT']
    ids = {n['node_id'] for n in nodes}
    return dict(case_id=case['id'], title=draft['title'], status=case['status'], issue_type=draft['issue_type'],
                description=draft['description'], solution=case['solution'], reviewer=case['reviewer'],
                origin=draft.get('origin'),
                snapshot=dict(revision=case['revision'], root=graph['root'], nodes=nodes,
                              edges=[e for e in graph['edges'] if e['source'] in ids and e['target'] in ids]),
                history=[dict(status=h['status'], created_at=h['created_at'], solution=h.get('solution'), has_evidence=bool(h.get('evidence')))
                         for h in case['history'][-20:]],
                recipe=recipe)


def call_tool(name, args, api):
    if name == 'casegraph_search_solutions':
        return api('/solutions/search', dict((k, v) for k, v in args.items() if v is not None))
    if name == 'casegraph_get_case':
        case_id = int(args['case_id'])
        return dict(compact_case(api('/cases/{}'.format(case_id)), api('/cases/{}/recipe'.format(case_id))),
                    applications=api('/cases/{}/applications'.format(case_id))[-20:])
    if name == 'casegraph_fix_plan':
        target = dict(element_id=args['target_element_id'], type_id=args.get('target_type_id'),
                      category=args.get('target_category'), family=args.get('target_family'),
                      type_name=args.get('target_type_name'), document=args.get('target_document'))
        return api('/cases/{}/fix-plan'.format(int(args['case_id'])),
                   dict(target=target, related=args.get('related') or {}, type_ids=args.get('type_ids') or {}))
    if name == 'casegraph_record_application':
        body = dict((k, args[k]) for k in ('outcome', 'element_ids', 'steps', 'resolved', 'document', 'notes') if args.get(k) is not None)
        return api('/cases/{}/applications'.format(int(args['case_id'])), body)
    raise KeyError(name)


def handle(message, api):
    """One JSON-RPC message in, one response (or None for notifications) out."""
    method, ident = message.get('method'), message.get('id')
    if ident is None:
        return None
    try:
        if method == 'initialize':
            requested = (message.get('params') or {}).get('protocolVersion') or '2025-06-18'
            result = dict(protocolVersion=requested, capabilities=dict(tools=dict(listChanged=False)),
                          serverInfo=dict(name='casegraph', version=VERSION), instructions=INSTRUCTIONS)
        elif method == 'ping':
            result = {}
        elif method == 'tools/list':
            result = dict(tools=TOOLS)
        elif method == 'tools/call':
            params = message.get('params') or {}
            try:
                value = call_tool(params.get('name'), params.get('arguments') or {}, api)
                result = dict(content=[dict(type='text', text=json.dumps(value, ensure_ascii=False, indent=1))])
            except KeyError as error:
                return dict(jsonrpc='2.0', id=ident, error=dict(code=-32602, message='Unknown tool or argument: {}'.format(error)))
            except (ValueError, URLError, OSError) as error:
                # Tool failures are reported to the model, not as protocol errors.
                result = dict(content=[dict(type='text', text=str(error))], isError=True)
        else:
            return dict(jsonrpc='2.0', id=ident, error=dict(code=-32601, message='Method not found: {}'.format(method)))
    except Exception as error:
        return dict(jsonrpc='2.0', id=ident, error=dict(code=-32603, message=str(error)))
    return dict(jsonrpc='2.0', id=ident, result=result)


def main():
    stdin = open(sys.stdin.fileno(), 'r', encoding='utf-8', closefd=False)
    stdout = open(sys.stdout.fileno(), 'w', encoding='utf-8', newline='\n', closefd=False)
    started = []

    def api(path, payload=None):
        if not started:
            ensure_backend()
            started.append(True)
        return http(path, payload)
    for line in stdin:
        if not line.strip():
            continue
        try:
            message = json.loads(line)
        except ValueError:
            response = dict(jsonrpc='2.0', id=None, error=dict(code=-32700, message='Parse error'))
        else:
            response = handle(message, api)
        if response is not None:
            stdout.write(json.dumps(response, ensure_ascii=False)+'\n')
            stdout.flush()


if __name__ == '__main__':
    main()
