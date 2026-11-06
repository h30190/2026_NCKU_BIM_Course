// 方案 → MCP 建窗指令清單(不連 Revit)
// 用法:
//   node cli/build_mcp_commands.mjs --model 01 --proposal P1
//   node cli/build_mcp_commands.mjs --proposals output/proposals_model01_xxx.json --proposal P2
import { getModel } from '../lib/catalog.mjs';
import { saveCommands } from '../lib/mcp_commands.mjs';

const a = {};
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i += 2) a[argv[i].replace(/^--/, '')] = argv[i + 1];

try {
  const proposalsFile = a.proposals || getModel(a.model || '01').defaultProposalsFile;
  const { file, commands } = saveCommands(proposalsFile, a.proposal || 'P1');
  console.log(`model_${commands.model_id} ${commands.proposal.proposal_id}(${commands.proposal.scheme},${commands.windows.length} 扇)→ ${file}`);
} catch (e) { console.error('錯誤:' + e.message); process.exit(1); }
