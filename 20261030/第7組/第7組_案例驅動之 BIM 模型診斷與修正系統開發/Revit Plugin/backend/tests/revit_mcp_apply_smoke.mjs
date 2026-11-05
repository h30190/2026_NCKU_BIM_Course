// Exercise the real Python executor and configured Node MCP bridge against a
// local simulated Revit endpoint. No real Revit model or Claude account is used.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, rmdir, unlink, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '../..');
const config = JSON.parse(await readFile(path.join(root, '.mcp.json'), 'utf8'));
const revit = config.mcpServers.revit;
const bridge = path.resolve(revit.args[0]);
const require = createRequire(bridge);
const { WebSocketServer } = require('ws');
const python = process.argv[2] || path.join(root, 'backend/.venv/Scripts/python.exe');
const folder = await mkdtemp(path.join(os.tmpdir(), 'casegraph-mcp-apply-'));
const configPath = path.join(folder, 'mcp.json');
const endpoint = new WebSocketServer({ host: 'localhost', port: 0 });
await once(endpoint, 'listening');
const port = endpoint.address().port;
assert.notEqual(port, 8964, 'must never use the real Revit service port');

let commands = [], clients = [], closeCodes = [], failAt = 0;
endpoint.on('connection', (socket, request) => {
  clients.push(new URL(request.url, 'http://localhost').searchParams.get('client'));
  socket.on('close', code => closeCodes.push(code));
  socket.on('message', raw => {
    const command = JSON.parse(raw);
    commands.push({ tool: command.CommandName, arguments: command.Parameters });
    const success = commands.length !== failAt;
    socket.send(JSON.stringify({ RequestId: command.RequestId, Success: success,
      Data: { message: '模擬修改完成' }, Error: success ? null : 'Synthetic Revit transaction failure' }));
  });
});

const step = (tool, args) => ({ tool, arguments: { elementId: 101, ...args }, reason: '模擬測試', source_case: null });
const steps = [
  step('modify_element_parameter', { parameterName: '註解', value: 'MCP 中文測試' }),
  step('change_element_type', { typeId: 202 }),
  step('move_element', { dx: 100, dy: -10, dz: 0 }),
  step('flip_element', { flipType: 'hand' }),
  step('delete_element', {}),
  step('create_wall_opening', { ductId: 102, widthMm: 450, heightMm: 550, expectedGeometry: 'a'.repeat(64) })
];

async function apply(failureIndex) {
  commands = []; clients = []; closeCodes = []; failAt = failureIndex;
  const child = spawn(python, [path.join(root, 'backend/claude_agent.py')], {
    cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, CASEGRAPH_MCP_CONFIG: configPath,
      CASEGRAPH_CLAUDE: path.join(folder, 'must-not-run-claude.exe') }
  });
  let stdout = '', stderr = '';
  child.stdout.setEncoding('utf8').on('data', data => { stdout += data; });
  child.stderr.setEncoding('utf8').on('data', data => { stderr += data; });
  const exited = once(child, 'close');
  const timer = setTimeout(() => {
    if (process.platform === 'win32') {
      spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    } else child.kill();
  }, 15000);
  try {
    child.stdin.end(JSON.stringify({ operation: 'apply', steps,
      scope: { document: 'synthetic', target_id: 101, element_ids: [101, 102] } }));
    const [code] = await exited;
    assert.equal(code, 0, stderr || stdout);
    const envelope = JSON.parse(stdout);
    assert.ok(!envelope.is_error, stdout);
    assert.deepEqual(clients, ['Claude-CaseGraph']);
    assert.deepEqual(closeCodes, [1000], 'executor must release the connection normally');
    return envelope.structured_output.results;
  } finally {
    clearTimeout(timer);
  }
}

try {
  await writeFile(configPath, JSON.stringify({ mcpServers: { revit: {
    ...revit, env: { ...revit.env, REVIT_MCP_PORT: String(port) }
  } } }), 'utf8');
  let results = await apply(0);
  assert.deepEqual(results.map(result => result.ok), [true, true, true, true, true, true]);
  assert.deepEqual(commands, steps.map(({ tool, arguments: args }) => ({ tool, arguments: args })),
    'every approved name and argument must reach the bridge unchanged');
  results = await apply(2);
  assert.deepEqual(results.map(result => result.ok), [true, false, false, false, false, false]);
  assert.match(results[1].message, /Synthetic Revit transaction failure/);
  assert.deepEqual(commands, steps.slice(0, 2).map(({ tool, arguments: args }) => ({ tool, arguments: args })),
    'failed writes must not be retried or followed by later steps');
  console.log('PASS: real stdio MCP bridge, all six modification tools including wall opening, exact UTF-8 arguments, failure stops execution, normal connection release');
} finally {
  for (const socket of endpoint.clients) socket.terminate();
  await new Promise(resolve => endpoint.close(resolve));
  await unlink(configPath).catch(error => { if (error.code !== 'ENOENT') throw error; });
  await rmdir(folder);
}
