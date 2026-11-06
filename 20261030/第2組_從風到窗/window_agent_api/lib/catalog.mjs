// 資料包內的檔案位置與讀取。所有路徑都相對於資料包根目錄,方便整包搬移或公開。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const OUTPUT_DIR = path.join(ROOT, 'output');
export const WEATHER_DIR = path.join(ROOT, 'data', 'weather');

export const readJson = (p) => JSON.parse(fs.readFileSync(path.resolve(ROOT, p), 'utf8'));
export const rel = (p) => path.relative(ROOT, p).split(path.sep).join('/');
export const stamp = () => new Date().toISOString().replace(/[-:]/g, '').replace(/\..*/, '').replace('T', '_');

export function writeOutput(name, data) {
  fs.mkdirSync(OUTPUT_DIR, { recursive: true });
  const file = path.join(OUTPUT_DIR, name);
  fs.writeFileSync(file, typeof data === 'string' ? data : JSON.stringify(data, null, 1), 'utf8');
  return rel(file);
}

// 只允許讀取資料包內的檔案(API 收到的路徑一律經過這裡)
export function safePath(p) {
  const full = path.resolve(ROOT, p);
  if (!full.startsWith(ROOT + path.sep)) throw new Error(`路徑必須在資料包內:${p}`);
  if (!fs.existsSync(full)) throw new Error(`找不到檔案:${p}`);
  return full;
}

const registry = () => readJson('data/models.json');
export const conventions = () => registry().revit_conventions;
export const listModels = () => registry().models;

export function getModel(modelId) {
  const id = String(modelId).padStart(2, '0');
  const m = listModels().find((x) => x.model_id === id);
  if (!m) throw new Error(`沒有模型 ${modelId},可用:${listModels().map((x) => x.model_id).join(', ')}`);
  return {
    ...m,
    building: readJson(`${m.data_dir}/building_data.json`),
    families: readJson(`${m.data_dir}/window_families.json`),
    defaultProposalsFile: `${m.data_dir}/proposals_default.json`,
  };
}

export const shared = () => ({
  windowTypes: readJson('data/shared/window_types.json'),
  rules: readJson('data/shared/design_rules_wind.json'),
  checklist: readJson('data/shared/ventilation_compliance_checklist.json'),
});

export const listWeather = () =>
  fs.readdirSync(WEATHER_DIR).filter((f) => /\.epw$/i.test(f)).map((f) => ({ file: `data/weather/${f}`, name: f }));

export function listOutputs(prefix = '') {
  if (!fs.existsSync(OUTPUT_DIR)) return [];
  return fs.readdirSync(OUTPUT_DIR).filter((f) => f.startsWith(prefix)).sort().reverse().map((f) => `output/${f}`);
}
