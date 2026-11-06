// Window Agent API:模型 → 窗型 → 氣象選方案 → 建窗 → 最終檔案
// 啟動:node server.mjs(預設 http://localhost:3080,網頁介面同址)
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { ROOT, listModels, getModel, listWeather, listOutputs, safePath, readJson } from './lib/catalog.mjs';
import { weatherSummary, generate, summarize, manualOptions, custom, removeCustom } from './lib/proposals.mjs';
import { saveCommands } from './lib/mcp_commands.mjs';
import { revitStatus, verifyModel, windowTypes, existingWindows, build, rollback, review } from './lib/executor.mjs';
import { exportFinal } from './lib/final_package.mjs';
import { weatherAnalysis, exposureAnalysis } from './lib/wind_stats.mjs';
import { planGeometry } from './lib/plan.mjs';
import { airflow } from './lib/airflow.mjs';

const PORT = Number(process.env.PORT || 3080);

// Revit 一次只做一件事:所有會連 Revit 的請求排隊執行
let queue = Promise.resolve();
const serial = (fn) => { const p = queue.then(fn, fn); queue = p.catch(() => {}); return p; };

const routes = [
  ['GET', /^\/api\/health$/, () => ({ ok: true })],

  // 1. 模型
  ['GET', /^\/api\/models$/, () => listModels()],
  ['GET', /^\/api\/revit\/status$/, () => serial(revitStatus)],
  ['GET', /^\/api\/models\/(\d+)\/verify$/, (_, [id]) => serial(() => verifyModel(id))],

  // 2. 窗型
  ['GET', /^\/api\/models\/(\d+)\/window-types$/, (_, [id]) => serial(() => windowTypes(id))],

  // 3. 氣象 → 方案
  ['GET', /^\/api\/weather$/, () => listWeather()],
  ['POST', /^\/api\/weather\/summary$/, (b) => weatherSummary(b.epw, b)],
  ['POST', /^\/api\/weather\/analysis$/, (b) => weatherAnalysis(b.epw, b)], // 風花圖 + 逐月風速
  ['POST', /^\/api\/models\/(\d+)\/exposure$/, (b, [id]) => exposureAnalysis(id, b.epw, b)], // 迎風分數計算過程
  ['GET', /^\/api\/models\/(\d+)\/plan$/, (_, [id]) => planGeometry(id)], // 平面示意圖幾何
  ['GET', /^\/api\/models\/(\d+)\/proposals$/, (_, [id], q) => summarize(q.get('file') || getModel(id).defaultProposalsFile)],
  ['POST', /^\/api\/models\/(\d+)\/proposals$/, (b, [id]) => { const { file } = generate(id, b); return summarize(file); }],
  ['GET', /^\/api\/models\/(\d+)\/manual-options$/, (_, [id], q) => manualOptions(id, q.get('file'), q.get('proposal'))], // 手動調整:各牆各窗型上限
  ['POST', /^\/api\/models\/(\d+)\/proposals\/custom$/, (b, [id]) => custom(id, b)], // 手動調整窗數/窗型 → 新方案
  ['POST', /^\/api\/models\/(\d+)\/proposals\/custom\/delete$/, (b, [id]) => removeCustom(id, b)], // 刪除手動方案
  ['POST', /^\/api\/models\/(\d+)\/airflow$/, (b, [id]) => airflow(id, b)], // 室內風流試算(2D 位勢流)

  // 4. 建窗
  ['POST', /^\/api\/commands$/, (b) => saveCommands(b.proposals_file, b.proposal_id)],
  ['GET', /^\/api\/models\/(\d+)\/existing-windows$/, (_, [id]) => serial(() => existingWindows(id))],
  ['POST', /^\/api\/build$/, (b) => serial(() => build(b.commands_file, b.confirm, { replace_previous: b.replace_previous, delete_window_ids: b.delete_window_ids }))],
  ['POST', /^\/api\/review$/, (b) => review(b.report_file, b)],
  ['POST', /^\/api\/rollback$/, (b) => serial(() => rollback(b.report_file))],

  // 5. 最終檔案
  ['POST', /^\/api\/final$/, (b) => exportFinal(b.report_file)],
  ['GET', /^\/api\/outputs$/, (_, __, q) => listOutputs(q.get('prefix') || '')],
  ['GET', /^\/api\/file$/, (_, __, q) => readJson(path.relative(ROOT, safePath(q.get('path'))))],
];

const send = (res, code, body, type = 'application/json; charset=utf-8') => {
  res.writeHead(code, { 'content-type': type });
  res.end(type.startsWith('application/json') ? JSON.stringify(body, null, 1) : body);
};

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  if (req.method === 'GET' && (url.pathname === '/' || url.pathname === '/index.html')) {
    return send(res, 200, fs.readFileSync(path.join(ROOT, 'web', 'index.html')), 'text/html; charset=utf-8');
  }
  const hit = routes.find(([m, re]) => m === req.method && re.test(url.pathname));
  if (!hit) return send(res, 404, { error: `沒有這個 API:${req.method} ${url.pathname}` });
  try {
    let body = {};
    if (req.method === 'POST') {
      let raw = '';
      for await (const chunk of req) raw += chunk;
      body = raw ? JSON.parse(raw) : {};
    }
    const params = url.pathname.match(hit[1]).slice(1);
    send(res, 200, await hit[2](body, params, url.searchParams));
  } catch (e) {
    send(res, 400, { error: e.message, ...(e.detail && { detail: e.detail }) });
  }
});
server.requestTimeout = 0;
// 只接受本機連線:同網路的其他電腦無法透過這個 API 操作你的 Revit
server.listen(PORT, '127.0.0.1', () => console.log(`Window Agent API: http://localhost:${PORT}`));
