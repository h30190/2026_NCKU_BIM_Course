// 組出前端原型：把 engine/（geometry、rules、review）、範例資料、規則包塞進 src/page.html
// 產出 egress-review.html（claude.ai 用）與 egress-review-standalone.html（直接用瀏覽器開）
const fs = require("fs");
const path = require("path");
const { snapshot, settings, pack } = require("./make_samples.js");
const engine = require("./engine/review.js");

const batch = engine.review(snapshot, settings, pack, { created_at: "2026-10-19T20:05:00+08:00" });
const json = (o) => JSON.stringify(o).replace(/</g, "\\u003c");
const page = fs.readFileSync(path.join(__dirname, "src", "page.html"), "utf8")
  .replace("/*ENGINE*/", () => ["geometry.js", "rules.js", "review.js"].map((f) => fs.readFileSync(path.join(__dirname, "engine", f), "utf8")).join("\n"))
  .replace("/*SNAPSHOT*/null", () => json(snapshot))
  .replace("/*PACK*/null", () => json(pack))
  .replace("/*SETTINGS*/null", () => json(settings))
  .replace("/*BATCH*/null", () => json(batch));
fs.writeFileSync(path.join(__dirname, "egress-review.html"), page, "utf8");

const cut = page.indexOf("</style>") + "</style>".length;
const standalone = `<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>*,*::before,*::after{box-sizing:border-box}body{margin:0;font-size:14px}[hidden]{display:none!important}</style>
${page.slice(0, cut)}
</head>
<body>
${page.slice(cut)}
</body>
</html>
`;
fs.writeFileSync(path.join(__dirname, "egress-review-standalone.html"), standalone, "utf8");
console.log("built");
