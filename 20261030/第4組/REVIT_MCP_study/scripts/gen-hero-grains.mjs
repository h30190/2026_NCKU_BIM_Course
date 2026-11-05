// Generates the two "grains" hero SVGs (skills / domain) from canonical counts.
// Self-contained animated SVG (embedded CSS @keyframes) — works as <img>, no runtime lib.
// Re-run whenever counts change so the hero art never goes stale again:
//   node scripts/gen-hero-grains.mjs
import { writeFileSync } from 'node:fs';

const OUT = new URL('../docs/BIM_MCP/images/', import.meta.url);

// canonical category palette (matches domain-index colour coding)
// Canvas height is derived from the grid + legend so the art never clips as
// counts grow — only `cols` needs picking per dataset to keep width <= 1200.
const grainsSVG = ({ title, sub, groups, cols }) => {
  const cells = [];
  groups.forEach(g => { for (let i = 0; i < g.count; i++) cells.push(g.color); });
  const S = 70, GAP = 18, X0 = 120, Y0 = 250;
  const rows = Math.ceil(cells.length / cols);

  const rects = cells.map((c, i) => {
    const x = X0 + (i % cols) * (S + GAP);
    const y = Y0 + Math.floor(i / cols) * (S + GAP);
    const delay = (0.15 + i * 0.045).toFixed(3);
    return `  <rect class="g" x="${x}" y="${y}" width="${S}" height="${S}" rx="14" fill="${c}" style="animation-delay:${delay}s"/>`;
  }).join('\n');

  const legendY = Y0 + rows * (S + GAP) + 40;
  const legend = groups.map((g, i) => {
    const lx = 120 + i * 200;
    return `  <g transform="translate(${lx},${legendY})"><rect width="22" height="22" rx="6" fill="${g.color}"/><text x="32" y="17" class="lg">${g.label} · ${g.count}</text></g>`;
  }).join('\n');

  const total = cells.length;
  const H = legendY + 60;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 ${H}" width="1200" height="${H}" font-family="'Noto Sans TC','Helvetica Neue',Inter,sans-serif">
<style>
  .g{transform-box:fill-box;transform-origin:center;opacity:0;
     animation:pop .6s cubic-bezier(.16,1,.3,1) forwards, glow 4.5s ease-in-out infinite;}
  @keyframes pop{0%{opacity:0;transform:scale(.35) translateY(14px)}60%{opacity:1;transform:scale(1.12)}100%{opacity:1;transform:scale(1)}}
  @keyframes glow{0%,100%{filter:drop-shadow(0 0 0 rgba(0,0,0,0))}50%{filter:drop-shadow(0 0 7px rgba(255,255,255,.22))}}
  .num{fill:#fff;font-size:128px;font-weight:800;letter-spacing:-3px}
  .t{fill:rgba(255,255,255,.92);font-size:40px;font-weight:700}
  .s{fill:rgba(255,255,255,.45);font-size:24px;letter-spacing:1px}
  .lg{fill:rgba(255,255,255,.72);font-size:21px}
</style>
  <rect width="1200" height="${H}" rx="0" fill="#0a0a0a"/>
  <text x="118" y="150" class="num">${total}</text>
  <text x="120" y="205" class="t">${title}</text>
  <text x="120" y="240" class="s">${sub}</text>
${rects}
${legend}
</svg>
`;
};

// Skills categories (docs/BIM_MCP/reference/skills-index.html, A-F) re-bucketed
// into the same 5 colour-coded semantics used by the domain chart:
//   A 法規檢核(6) -> 法規/compliance
//   B 設計協作(4) + C 文件生產(16) + D 模型分析 non-MEP(16) -> 流程/workflow
//   E 開發運維 non-external(4) + F 進階開發/文件作者 non-external(3) -> 維運/ops
//   D 模型分析 MEP subset: detect-clashes, mep-settings-curation, set-project-units(3) -> MEP
//   E/F external-bridge skills: hj-pr-proposal, archicad-skill-adapter(2) -> 外部/references
const skills = grainsSVG({
  title: 'Skills · 編排層',
  sub: 'AI WORKFLOW ORCHESTRATION · 5 CATEGORIES',
  cols: 10,
  groups: [
    { label: '法規', count: 6, color: '#ef4444' },
    { label: '流程', count: 40, color: '#60a5fa' },
    { label: '維運', count: 10, color: '#fbbf24' },
    { label: 'MEP', count: 3, color: '#4ade80' },
    { label: '外部', count: 2, color: '#a78bfa' },
  ],
});

// Domain categories from docs/BIM_MCP/reference/domain-index.html cat-summary.
const domain = grainsSVG({
  title: 'Domain · 知識層',
  sub: 'SHARED BIM SOP · 5 CATEGORIES',
  cols: 10,
  groups: [
    { label: '法規', count: 11, color: '#ef4444' },
    { label: '流程', count: 42, color: '#60a5fa' },
    { label: '維運', count: 11, color: '#fbbf24' },
    { label: 'MEP', count: 18, color: '#4ade80' },
    { label: '外部', count: 1, color: '#a78bfa' },
  ],
});

writeFileSync(new URL('skills__hero__grains.svg', OUT), skills);
writeFileSync(new URL('domain__hero__grains.svg', OUT), domain);
console.log('generated: skills__hero__grains.svg (61), domain__hero__grains.svg (83)');
