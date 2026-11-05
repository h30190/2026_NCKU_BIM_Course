#Requires -Version 5.1
# Revit MCP QA/QC Verification Script
# Usage: .\scripts\verify-qaqc.ps1 [-SkipBuild] [-SkipDeploy] [-Version 2024] [-AddinsRoot <path>]
#
# Phases:
#   1. File Structure Integrity
#   2. Cross-Reference Consistency
#   3. Build Configuration Validation
#   4. Build Verification (skip with -SkipBuild)
#   5. Deployment Verification (skip with -SkipDeploy)
#   6. Domain Metadata and Shared SOP Quality
#   7. Cross-Document Alignment (CLAUDE.md / BIM_MCP web / scripts must report same Skill/Domain/Tool counts)
#   8. Document Audience and Encoding Hygiene
#   9. MCP 2026 Compliance (tool annotation coverage: title / readOnlyHint / destructiveHint allow-list)

param(
    [switch]$SkipBuild,
    [switch]$SkipDeploy,
    [string]$Version = "",
    # Phase 5 專用：Addins 根目錄（預設為使用者實際部署位置）。
    # 指向暫存 fixture 即可對 Phase 5 做負向測試，不動真實部署。
    [string]$AddinsRoot = ""
)

$ErrorActionPreference = "Continue"

$scriptDir = $PSScriptRoot
$projectRoot = Split-Path -Parent -Path $scriptDir

$totalPass = 0
$totalFail = 0
$totalSkip = 0
$totalWarn = 0
$failures = @()

function Write-Check {
    param([string]$Name, [bool]$Result, [string]$Detail = "")
    if ($Result) {
        Write-Host "  PASS  $Name" -ForegroundColor Green
        $script:totalPass++
    }
    else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "         $Detail" -ForegroundColor Red }
        $script:totalFail++
        $script:failures += @{ Name = $Name; Detail = $Detail }
    }
}

function Write-Skip {
    param([string]$Name, [string]$Reason = "")
    Write-Host "  SKIP  $Name ($Reason)" -ForegroundColor DarkGray
    $script:totalSkip++
}

function Write-Warn {
    param([string]$Name, [string]$Detail = "")
    Write-Host "  WARN  $Name" -ForegroundColor Yellow
    if ($Detail) { Write-Host "         $Detail" -ForegroundColor DarkYellow }
    $script:totalWarn++
}

# Robust text reader — bypasses Get-Content parameter-binding quirks on some files
function Read-FileText {
    param([string]$Path)
    try {
        if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
        return [System.IO.File]::ReadAllText($Path)
    } catch {
        return $null
    }
}

# Header
Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  Revit MCP QA/QC Verification" -ForegroundColor Cyan
Write-Host "  Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
Write-Host "  Root: $projectRoot" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan

# ─────────────────────────────────────────────
# Phase 1: File Structure Integrity
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "[Phase 1] File Structure Integrity" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

# 1-1: Forbidden files
Write-Host ""
Write-Host "  1-1. Forbidden files (must NOT exist):" -ForegroundColor Cyan
Write-Check "No RevitMCP.2024.csproj" (-not (Test-Path "$projectRoot\MCP\RevitMCP.2024.csproj")) "Legacy file found - delete it"
Write-Check "No RevitMCP.2024.addin" (-not (Test-Path "$projectRoot\MCP\RevitMCP.2024.addin")) "Duplicate addin found - delete it"
Write-Check "No MCP\MCP\ directory" (-not (Test-Path "$projectRoot\MCP\MCP")) "Nested directory found - delete it"
Write-Check "No fix_addin_path.ps1" (-not (Test-Path "$projectRoot\scripts\fix_addin_path.ps1")) "Dangerous script found - delete it"

# 1-2: Required files
Write-Host ""
Write-Host "  1-2. Required files (must exist):" -ForegroundColor Cyan
$requiredFiles = @(
    @("MCP\RevitMCP.csproj", "Unified build file"),
    @("MCP\RevitMCP.addin", "Unified addin config"),
    @("MCP\Application.cs", "Add-in entry point"),
    @("MCP\Core\CommandExecutor.cs", "Command dispatcher"),
    @("MCP\Core\SocketService.cs", "WebSocket service"),
    @("MCP\Core\ExternalEventManager.cs", "UI thread manager"),
    @("MCP\Core\RevitCompatibility.cs", "Cross-version compat layer"),
    @("MCP-Server\src\index.ts", "MCP Server entry"),
    @("MCP-Server\src\tools\index.ts", "Runtime tool registry"),
    @("MCP-Server\src\tools\revit-tools.ts", "Tool definitions"),
    @("MCP-Server\package.json", "Node.js dependencies")
)

foreach ($file in $requiredFiles) {
    $path = Join-Path $projectRoot $file[0]
    Write-Check "$($file[0])" (Test-Path $path) "$($file[1]) not found"
}

# 1-3: AI rule file consistency
Write-Host ""
Write-Host "  1-3. AI rule file consistency:" -ForegroundColor Cyan

$claudeMd = Join-Path $projectRoot "CLAUDE.md"
if (Test-Path $claudeMd) {
    $lines = (Get-Content $claudeMd).Count
    Write-Check "CLAUDE.md exists ($lines lines)" ($lines -gt 100) "CLAUDE.md too short ($lines lines < 100)"
}
else {
    Write-Check "CLAUDE.md exists" $false "Main rule file missing"
}

foreach ($redirect in @("GEMINI.md", "AGENTS.md")) {
    $path = Join-Path $projectRoot $redirect
    if (Test-Path $path) {
        $rawContent = Read-FileText $path
        $content = if ($rawContent) { $rawContent.Trim() } else { "" }
        Write-Check "$redirect is redirect" ($content -eq "CLAUDE.md") "Content is '$content' instead of 'CLAUDE.md'"
    }
    else {
        Write-Check "$redirect exists" $false "Redirect file missing"
    }
}

# 1-4: Personal vault protection — .gitignore must exclude /vault/ and /.obsidian/
# so users' personal knowledge vaults (templates/personal-vault/) can never be pushed.
Write-Host ""
Write-Host "  1-4. Personal vault gitignore protection:" -ForegroundColor Cyan
$gitignore = Read-FileText (Join-Path $projectRoot ".gitignore")
Write-Check ".gitignore excludes /vault/" ($gitignore -match '(?m)^/vault/\s*$') "Add /vault/ to .gitignore"
Write-Check ".gitignore excludes /.obsidian/" ($gitignore -match '(?m)^/\.obsidian/\s*$') "Add /.obsidian/ to .gitignore"
Write-Check "Vault schema template exists" (Test-Path (Join-Path $projectRoot "templates\personal-vault\VAULT-CLAUDE.md")) "templates/personal-vault/VAULT-CLAUDE.md missing"

# ─────────────────────────────────────────────
# Phase 2: Cross-Reference Consistency
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "[Phase 2] Cross-Reference Consistency" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

# 2-1: Stale reference scan
Write-Host ""
Write-Host "  2-1. Stale reference scan:" -ForegroundColor Cyan

$stalePatterns = @(
    @("RevitMCP\.2024\.csproj", "Deleted legacy build file"),
    @("RevitMCP\.2024\.addin", "Deleted legacy addin"),
    @("bin\\Release\.2024", "Old output path"),
    @("bin\\Release\\RevitMCP\.dll", "Old unified output path; use bin\\Release.R{YY}\\RevitMCP.dll"),
    @("MCP\\MCP\\", "Old nested directory"),
    @("fix_addin_path", "Deleted script")
)

# Excluded files (historical/migration docs + 規範性提及)
$excludedFiles = @(
    "CHANGELOG.md",
    "MIGRATION_GUIDE.md",
    "Recent_Update_Review.md",
    ".claude/commands/qaqc.md",        # /qaqc 命令定義本身列舉「禁止檔名」
    "domain/qa-checklist.md",          # QA checklist intentionally lists forbidden legacy paths
    "domain/lessons.md",                # 開發經驗檔，保留 legacy 教訓作為前車之鑑
    "domain/path-maintenance-qa.md",    # 路徑維護 QA，引用舊 nested dir 作為歷史修正紀錄
    ".claude/skills/hj-pr-proposal/references/qa-checklist.md",  # QA checklist intentionally lists forbidden legacy paths (mirror of domain/qa-checklist.md)
    "docs/0328的課程討論.md"            # 歷史教材，保留當時上下文
)

$mdFiles = Get-ChildItem -Path $projectRoot -Filter "*.md" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch "node_modules|\.claude[\\/]plugins|docs[\\/]_archive|docs[\\/]fork-audit|[\\/]log[\\/]" }
    # log/ 為 append-only 事件日誌、docs/fork-audit/ 為 fork 盤點報告（gitignored）：
    # 兩者屬敘事性歷史文件，描述修正/外部 fork 時合法引用禁用檔名，非規範性指引，排除於 stale-ref 掃描。

$staleFound = $false
foreach ($pattern in $stalePatterns) {
    foreach ($file in $mdFiles) {
        $relativePath = $file.FullName.Replace("$projectRoot\", "").Replace("$projectRoot/", "")

        # Skip excluded files — 標準化路徑分隔符為 / 後比對（兼容 Windows 反斜線）
        $normalizedPath = $relativePath.Replace("\", "/")
        $skip = $false
        foreach ($ex in $excludedFiles) {
            $normalizedEx = $ex.Replace("\", "/")
            if ($normalizedPath -like "*$normalizedEx") { $skip = $true; break }
        }
        # 額外排除 docs/0328 開頭的歷史教材檔（避免中文檔名 encoding 問題）
        if ($normalizedPath -like "docs/0328*") { $skip = $true }
        if ($skip) { continue }

        $content = Read-FileText $file.FullName
        if ($content -and $content -match $pattern[0]) {
            # Exception: CLAUDE.md "DO NOT" rules
            if ($relativePath -eq "CLAUDE.md" -and $content -match "DO NOT.*$($pattern[0])") { continue }
            if ($relativePath -eq "CLAUDE.md" -and $content -match "Legacy.*removed") { continue }

            Write-Host "  FAIL  $relativePath references '$($pattern[0])'" -ForegroundColor Red
            Write-Host "         $($pattern[1])" -ForegroundColor Red
            $totalFail++
            $staleFound = $true
            $failures += @{ Name = "Stale ref in $relativePath"; Detail = $pattern[1] }
        }
    }
}
if (-not $staleFound) {
    Write-Check "No stale references in active docs" $true
}

# 2-2: Navigation table check
Write-Host ""
Write-Host "  2-2. Navigation table completeness:" -ForegroundColor Cyan

foreach ($readme in @("README.md", "README.zh-TW.md")) {
    $path = Join-Path $projectRoot $readme
    if (Test-Path $path) {
        $content = Read-FileText $path
        $hasAgents = $content -match "AGENTS\.md"
        $hasLessons = $content -match "domain/lessons\.md"
        $hasSkills = $content -match "\.claude/skills"
        $hasCommands = $content -match "\.claude/commands"
        $allPresent = $hasAgents -and $hasLessons -and $hasSkills -and $hasCommands
        Write-Check "$readme navigation table complete" $allPresent "Missing entries in doc navigation"
    }
}

# ─────────────────────────────────────────────
# Phase 3: Build Configuration Validation
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "[Phase 3] Build Configuration Validation" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

# 3-1: csproj settings
Write-Host ""
Write-Host "  3-1. csproj settings:" -ForegroundColor Cyan
$csproj = Join-Path $projectRoot "MCP\RevitMCP.csproj"
if (Test-Path $csproj) {
    $content = Read-FileText $csproj
    Write-Check "Nice3point.Revit.Sdk reference" ($content -match "Nice3point\.Revit\.Sdk") "Missing SDK reference"
    Write-Check "DeployAddin disabled" ($content -match "<DeployAddin>false</DeployAddin>") "DeployAddin must be false (Nice3point SDK 會自動產生 RevitMCP.{version}.addin 與手動 addin 衝突)"
    Write-Check "Release.R22 config" ($content -match "Release\.R22") "Missing Revit 2022 config"
    Write-Check "Release.R24 config" ($content -match "Release\.R24") "Missing Revit 2024 config"
    Write-Check "Release.R25 config" ($content -match "Release\.R25") "Missing Revit 2025 config"
    Write-Check "Release.R26 config" ($content -match "Release\.R26") "Missing Revit 2026 config"
}
else {
    Write-Check "csproj exists" $false "Cannot validate build config"
}

# 3-2: addin settings
Write-Host ""
Write-Host "  3-2. addin settings:" -ForegroundColor Cyan
$addin = Join-Path $projectRoot "MCP\RevitMCP.addin"
if (Test-Path $addin) {
    $content = Read-FileText $addin
    # Assembly 路徑應為相對路徑 — 接受 "RevitMCP.dll" 或 "RevitMCP\RevitMCP.dll"（Nice3point SDK 子資料夾）
    Write-Check "Relative assembly path" ($content -match "<Assembly>RevitMCP[\\/]?(RevitMCP\.dll|\.dll)</Assembly>|<Assembly>RevitMCP\\RevitMCP\.dll</Assembly>") "Assembly path should be relative (RevitMCP.dll or RevitMCP\RevitMCP.dll)"
    Write-Check "No absolute path in addin" (-not ($content -match "[A-Z]:\\")) "Absolute path found in addin file"
    Write-Check "FullClassName correct" ($content -match "RevitMCP\.Application") "FullClassName should be RevitMCP.Application"

    # Count AddInId occurrences
    $addinIdCount = ([regex]::Matches($content, "<AddInId>")).Count
    Write-Check "Single AddInId" ($addinIdCount -le 2) "Multiple AddInId entries found ($addinIdCount)"
}
else {
    Write-Check "addin file exists" $false "Cannot validate addin config"
}

# 3-3: MCP Server
Write-Host ""
Write-Host "  3-3. MCP Server config:" -ForegroundColor Cyan
$pkg = Join-Path $projectRoot "MCP-Server\package.json"
if (Test-Path $pkg) {
    $content = Read-FileText $pkg
    Write-Check "build script defined" ($content -match '"build"') "No build script in package.json"
    Write-Check "MCP SDK dependency" ($content -match "modelcontextprotocol") "Missing MCP SDK dependency"
}

# 3-4: No hardcoded user account names anywhere in the tracked tree.
# Scope note: this check used to read only MCP-Server\*_config.json. The PATTERN was always right;
# the SCOPE was five files. A Windows account name therefore sat in 15 other tracked files - docs,
# skills, logs, a domain file - for weeks, while the check reported PASS. Same failure shape as the
# non-recursive $scanPaths glob: a file that is never read produces the same green report as a file
# that passes. Default is now SCAN EVERY TRACKED TEXT FILE; exclusions must be explicit below.
Write-Host ""
Write-Host "  3-4. Hardcoded user paths (all tracked text files):" -ForegroundColor Cyan
# Placeholders are the intended form. Anything else in a Users\ path is a real account name.
# The three angle-bracket forms are deliberately distinct, not interchangeable: <YOUR_USERNAME>
# is the reader's own home directory, <CONTRIBUTOR_USERNAME> is a path quoted from a
# contributor's machine, <OTHER_MACHINE_USER> is a second machine belonging to this project.
# Calling someone else's home directory "yours" is wrong, which is why this list grew instead
# of reusing one label. Adding an entry widens what the gate accepts: state the semantics of
# any new placeholder here, or the list becomes an exit for silencing a FAIL rather than a
# vocabulary for describing paths honestly.
$allowedUsers = @(
    '<YOUR_USERNAME>', 'YOUR_USERNAME', '%USERNAME%', '$env:USERNAME', '*',
    '<YOUR_PROJECT_PATH>', 'User', 'USERNAME', 'username', 'xxx', 'XXX', '...',
    '你的名字', '你的使用者名稱', '您的使用者名稱',
    '<CONTRIBUTOR_USERNAME>', '<OTHER_MACHINE_USER>'
)
# Immutable event snapshots (CLAUDE.md): their "Admin" is an explicit teaching example
# (the page literally says "if your account name were X, type cd ...\X\..."), not a leaked account.
# Exactly two tracked files actually contain that Admin placeholder in a home-directory path —
# listed by full path, not by prefix. A prefix like '\docs\0425-' or '\docs\0523-' also matches
# sibling snapshot files that carry no such placeholder (0425-karpathy-wiki.html,
# 0523-dry-run-retrospective.md, 0523-handson.html, 0523-monthly.html), silently widening the
# exclusion to 6 files instead of 2 — exactly the "a file that is never read produces the same
# green report as a file that passes" failure this check exists to prevent. Full paths only; see
# the self-check below that FAILs if the hit count ever drifts from this list's length.
# Note: this file deliberately avoids writing a literal home-directory path anywhere (including in
# this comment), or the check below would flag its own source text.
$pathScanSkip = @(
    '\docs\0425-presentation.html'  # immutable 2026-04-25 snapshot: has the Admin teaching placeholder, not a leaked account
    '\docs\0523-presentation.html'  # immutable 2026-05-23 snapshot: same Admin teaching placeholder as the 0425 snapshot
)
$hardcodedPaths = @()
$pathScanned = 0
$pathScanSkipHits = @()
# git ls-files writes raw UTF-8 filename bytes to stdout (core.quotepath=false disables the
# octal-escaping git would otherwise apply to non-ASCII bytes). PowerShell decodes captured
# native-process stdout through [Console]::OutputEncoding, which on a zh-TW/ja-JP Windows
# console defaults to the legacy code page (e.g. cp950), not UTF-8. Three tracked files with
# CJK filenames were silently mis-decoded before Test-Path ever ran: Test-Path saw a garbled
# path, returned $false, and the `if (-not (Test-Path ...)) { continue }` below skipped them
# with no FAIL, no WARN — the check never judged these 3 files, it never READ them.
# core.quotepath=false alone does not fix this: it only stops git from octal-escaping; the
# console still has to decode the resulting UTF-8 bytes correctly, and cp950 does not.
# Reproduced directly: under a cp950 console, tracked=620 unreadable=3 (the exact 3 CJK-named
# tracked files); under a UTF-8 console, unreadable=0. Scoped to just this one git call and
# restored in `finally` — this must not leave a global encoding side effect for the rest of the
# script or the user's shell.
$prevConsoleOutputEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    Push-Location $projectRoot
    $trackedForPaths = @(& git -c core.quotepath=false ls-files 2>$null)
    Pop-Location
} finally {
    [Console]::OutputEncoding = $prevConsoleOutputEncoding
}
# '>' must stay INSIDE the capture. With it excluded, the placeholder <YOUR_USERNAME> was
# truncated to '<YOUR_USERNAME', never matched the allow-list, and the check flagged its own fix.
$userRx = [regex]'[Uu]sers[\\/]+([^\\/"''`\s\]},;:]+)'
foreach ($rel in $trackedForPaths) {
    if (-not $rel) { continue }
    $full = Join-Path $projectRoot ($rel -replace '/', '\')
    if (-not (Test-Path -LiteralPath $full)) { continue }
    if ($pathScanSkip | Where-Object { $full -like "*$_*" }) { $pathScanSkipHits += $full; continue }
    $content = Read-FileText $full
    if (-not $content) { continue }
    $pathScanned++
    foreach ($m in $userRx.Matches($content)) {
        $raw = $m.Groups[1].Value
        if ($raw.StartsWith('<')) { $u = $raw.TrimEnd('.', ',', ')', '`', '"') }
        else { $u = $raw.TrimEnd('.', ',', ')', '`', '"', '>') }
        # An elided path writes '...' as the account name; trimming dots leaves nothing, and an
        # empty capture must not be reported as a leaked account.
        if (-not $u) { continue }
        if ($allowedUsers -contains $u -or $allowedUsers -contains $raw) { continue }
        $hardcodedPaths += "$rel : account name $u in a home-directory path"
    }
}
$hardcodedPaths = $hardcodedPaths | Sort-Object -Unique
# Floor on the scan universe (inspector review, loop-up S5 correction pass): PASS must never mean
# "scanned zero files". Before this fix, an empty $trackedForPaths (e.g. `git ls-files` returning
# nothing - not a git repo, or run outside one) fell through to "No hardcoded user account names in
# 0 tracked text files" == PASS, exactly the "a file never read produces the same green report as a
# file that passes" failure this check's own header comment (above) already names. Modeled on
# 7-15's own Write-Skip for an empty $toolNames: an empty scan universe is UNVERIFIED, not clean,
# so it must not silently pass. Out-of-scope-of-S5 origin, fixed here because it is the same defect
# class as 7-15's block-comment fix (check runs, reports PASS, nothing was actually verified) and
# lives in the same file this stage already owns.
if ($trackedForPaths.Count -eq 0) {
    Write-Skip "No hardcoded user account names in tracked text files" "git ls-files returned nothing - scan universe empty, cannot verify (not a git repo, or run outside one)"
}
else {
    Write-Check "No hardcoded user account names in $pathScanned tracked text files" ($hardcodedPaths.Count -eq 0) `
        $(if ($hardcodedPaths.Count -gt 0) { "$($hardcodedPaths.Count) hardcoded path(s). Replace the account name with <YOUR_USERNAME>." } else { "" })
    if ($hardcodedPaths.Count -gt 0) { $hardcodedPaths | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }
}

# Self-check: each $pathScanSkip entry must INDEPENDENTLY exclude exactly one tracked file — not
# just an aggregate total. Comparing only totals lets a false-exclude and a false-include cancel
# out and still PASS: e.g. entry A over-matches 2 files while entry B (renamed/deleted target)
# matches 0 — the aggregate reads "2 listed, 2 matched" even though neither entry does what it
# claims. Reproduced: $pathScanSkip = @('\docs\0425-', '\docs\deleted-file.html') gives an
# aggregate listed=2 matched=2. An aggregate-only design would PASS on that input; no such
# check ever shipped, so this is a counterfactual, not a description of a previous version.
# The true per-entry breakdown is
# '\docs\0425-' -> 2 files (over-match, silently widens the exclusion) and
# '\docs\deleted-file.html' -> 0 files (dead entry, excludes nothing). Per-entry breakdown catches
# both; the aggregate catches neither.
$pathScanSkipHits = $pathScanSkipHits | Sort-Object -Unique
$pathScanSkipByEntry = @{}
foreach ($entry in $pathScanSkip) { $pathScanSkipByEntry[$entry] = @() }
foreach ($full in $pathScanSkipHits) {
    foreach ($entry in $pathScanSkip) {
        if ($full -like "*$entry*") { $pathScanSkipByEntry[$entry] += $full }
    }
}
$pathScanSkipBadEntries = @()
foreach ($entry in $pathScanSkip) {
    $hits = @($pathScanSkipByEntry[$entry] | Sort-Object -Unique)
    if ($hits.Count -ne 1) {
        $pathScanSkipBadEntries += "'$entry' matched $($hits.Count) file(s) (want exactly 1): $(if ($hits.Count -gt 0) { $hits -join ', ' } else { '(none)' })"
    }
}
Write-Check "Each pathScanSkip entry excludes exactly 1 tracked file ($($pathScanSkip.Count) listed)" ($pathScanSkipBadEntries.Count -eq 0) `
    $(if ($pathScanSkipBadEntries.Count -gt 0) { $pathScanSkipBadEntries -join ' | ' } else { "" })
if ($pathScanSkipBadEntries.Count -gt 0) { $pathScanSkipBadEntries | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }

# 3-5: Third-party TABC green-material data must not enter version control.
# TABC (財團法人臺灣建築中心) green-material certificate data is a third-party database, not this
# project's own content; this repo is MIT-licensed and must not become a redistribution channel for
# it. Reuses $trackedForPaths from 3-4 (same `git ls-files` snapshot) rather than re-invoking git -
# same floor-on-scan-universe reasoning as 3-4: if the scan universe is empty, SKIP, never a false
# PASS on zero files scanned.
# Two independent FAIL conditions:
#   A) Path pattern: a tracked file whose PATH matches tabc_master_database, real_*_data.json,
#      tabc_search, raw_tabc_search, a green-material/archive/ path, or a scrape_tabc /
#      fetch_all_tabc script name is bulk third-party data (a full local mirror, a website page
#      snapshot, a one-shot scraper's raw output, or the scraper tool itself) by construction,
#      regardless of content. The last three patterns were added after a read-only RED test against
#      the 37 files cwlin/main excluded during the 2026-08 gm-monstrare harvest: the original four
#      patterns matched only 6 of the 37 by path (Condition A alone); the full old rule (Condition A
#      path patterns union Condition B's 20+ threshold) blocked 9, with a 3-file overlap. The gap
#      included archive/reports/wall_summary.txt (11 distinct certificate numbers - under the
#      Condition B threshold, so path-pattern coverage is the only backstop) and 21 of the 22
#      archive/scripts/**/*.py scraper scripts (0 certificate numbers each except one with 2, so
#      Condition B cannot see them at all; filenames such as scrape_tabc.py / fetch_all_tabc_master.py
#      did not match any prior pattern either). green-material/archive/ covers the whole archive tree
#      by directory prefix; scrape_tabc / fetch_all_tabc cover the scraper tools by name so a future
#      copy of one outside that directory is still caught.
#   B) Citation volume: any tracked, non-domain/*.md file citing 20+ DISTINCT `GBM\d{7}` certificate
#      numbers is bulk re-publication, not citation. domain/*.md is exempted outright per this
#      project's hard rule that SOP method files may cite individual certificate numbers as worked
#      examples. The 20 threshold is sourced, not a bare magic number: domain/GM_keyword-search.md
#      legitimately cites 22 distinct GBM numbers as a keyword-to-material SOP lookup table (verified
#      via `grep -oE 'GBM[0-9]{7}' domain/GM_keyword-search.md | sort -u | wc -l` during the 2026-08
#      gm-monstrare harvest), and the next-highest non-domain file in that same harvest (GM_kanban.html
#      / tools/kanban/index.html) cites 11. 20 sits strictly between those two observed values - below
#      the one known legitimate large SOP table (so a future copy of that table into a non-domain file
#      would still be caught) and comfortably above ordinary worked-example counts (2-11) seen
#      elsewhere in this project.
Write-Host ""
Write-Host "  3-5. Third-party TABC data must not enter version control:" -ForegroundColor Cyan

if ($trackedForPaths.Count -eq 0) {
    Write-Skip "No tracked file path matches bulk TABC data patterns" "git ls-files returned nothing - scan universe empty, cannot verify (not a git repo, or run outside one)"
    Write-Skip "No non-domain file cites 20+ distinct TABC certificate numbers" "git ls-files returned nothing - scan universe empty, cannot verify (not a git repo, or run outside one)"
}
else {
    # Condition A: forbidden bulk-data filename patterns. See rationale above for the three patterns
    # added past the original four (green-material/archive/, scrape_tabc, fetch_all_tabc).
    $tabcPathRx = [regex]'tabc_master_database|real_.*_data\.json|tabc_search|raw_tabc_search|green-material/archive/|scrape_tabc|fetch_all_tabc'
    $tabcPathHits = @($trackedForPaths | Where-Object { $_ -and $tabcPathRx.IsMatch($_) })
    Write-Check "No tracked file path matches bulk TABC data patterns" ($tabcPathHits.Count -eq 0) `
        $(if ($tabcPathHits.Count -gt 0) { "Forbidden path(s): $($tabcPathHits -join ', ')" } else { "" })
    if ($tabcPathHits.Count -gt 0) { $tabcPathHits | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }

    # Condition B: per-file distinct-certificate-number ceiling (non-domain/*.md files only).
    # NOTE (index vs working tree): this loop reads DISK content via Read-FileText, not the
    # staged index blob. That is the same gap as this Stage's own S1 blocker -
    # assets/green-material-showcase.template.html sat staged with the OLD (real-certificate)
    # blob while its worktree copy had already been redacted, so a disk-only read would have
    # reported Condition B PASS on a still-dirty index. Considered switching to `git show ":$rel"`
    # per file to read the index blob directly, but measured: ~625 non-domain tracked files x
    # ~0.27s per `git show` subprocess (benchmarked directly, 20 calls = 5.43s) is roughly 170s
    # added to every QA/QC run for one condition - not acceptable, and a `git cat-file --batch`
    # single-process alternative needs a hand-rolled binary-safe stream parser (header line then
    # exact byte-count content, repeated per blob) that is itself new, unverified surface area in
    # a script whose whole point is checks that are provably correct. Chose the cheaper fix instead
    # (2026-08 gm-monstrare harvest, Correction round 3): keep reading disk, but make the gap
    # visible rather than silent - see the index/working-tree agreement check after Condition B
    # below. It is a Write-Warn, not a Write-Check FAIL: an uncommitted edit is normal mid-session
    # (this Stage's own 3 pre-existing baseline diffs are exactly that, and are explicitly out of
    # scope to touch), so treating "index and disk differ" as a hard gate failure would make 3-5
    # noisy on completely ordinary work. The warning still gives full visibility: which tracked,
    # non-domain files disagree, so a reviewer can judge whether Condition B's result for THIS run
    # reflects what `git add` actually staged.
    # $tabcScanned / $tabcUnreadable give this loop the same visibility 3-4 already has via
    # $pathScanned: same defect class as 3-4's own header comment (a cp950 console silently
    # mis-decoding 3 tracked CJK filenames, which Test-Path then saw as garbled paths and skipped
    # with no FAIL, no WARN, no record it was ever skipped) - a `continue` with no counter makes an
    # unscanned file indistinguishable from a scanned-and-clean file in the PASS/FAIL output alone.
    $gbmRx = [regex]'GBM\d{7}'
    $overCiteHits = @()
    $tabcScanned = 0
    $tabcUnreadable = @()
    foreach ($rel in $trackedForPaths) {
        if (-not $rel) { continue }
        if ($rel -like 'domain/*.md') { continue }
        $full = Join-Path $projectRoot ($rel -replace '/', '\')
        if (-not (Test-Path -LiteralPath $full)) { $tabcUnreadable += "$rel (not on disk)"; continue }
        $content = Read-FileText $full
        # $null -eq $content, not -not $content: an empty (0-byte) file reads back as "", which
        # -not treats as falsy and would wrongly record a genuinely-scanned empty file as unreadable.
        if ($null -eq $content) { $tabcUnreadable += "$rel (unreadable)"; continue }
        $tabcScanned++
        $distinct = @($gbmRx.Matches($content) | ForEach-Object { $_.Value } | Sort-Object -Unique)
        if ($distinct.Count -ge 20) {
            $overCiteHits += "$rel : $($distinct.Count) distinct GBM certificate numbers"
        }
    }
    Write-Check "No non-domain file cites 20+ distinct TABC certificate numbers ($tabcScanned scanned)" ($overCiteHits.Count -eq 0) `
        $(if ($overCiteHits.Count -gt 0) { "Likely bulk redistribution, not citation: $($overCiteHits -join ' | ')" } else { "" })
    if ($overCiteHits.Count -gt 0) { $overCiteHits | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }
    Write-Check "Every non-domain tracked file was readable by 3-5" ($tabcUnreadable.Count -eq 0) `
        $(if ($tabcUnreadable.Count -gt 0) { "Unscanned: $($tabcUnreadable -join ', ')" } else { "" })

    # Index vs working-tree agreement, scoped to Condition B's own candidate set (tracked,
    # non-domain/*.md files) - see the NOTE above Condition B for why this is a Write-Warn, not a
    # Write-Check FAIL.
    Push-Location $projectRoot
    $dirtyTrackedAll = @(& git diff --name-only 2>$null)
    Pop-Location
    $dirtyTrackedInScope = @($dirtyTrackedAll | Where-Object { $_ -and ($_ -notlike 'domain/*.md') })
    if ($dirtyTrackedInScope.Count -gt 0) {
        Write-Warn "3-5 Condition B reflects the working tree, not the staged index, for $($dirtyTrackedInScope.Count) file(s)" `
            "Index and disk differ for: $($dirtyTrackedInScope -join ', ') - if one of these is staged later (git add) with different certificate content than what 3-5 just scanned on disk, re-run 3-5 after staging"
    }
}

# ─────────────────────────────────────────────
# Phase 4: Build Verification (Windows only)
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "[Phase 4] Build Verification" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

if ($SkipBuild) {
    Write-Skip "C# build" "Skipped via -SkipBuild flag"
    Write-Skip "MCP Server build" "Skipped via -SkipBuild flag"
}
else {
    # Determine which versions to build
    $versions = @()
    if ($Version) {
        $versions += $Version
    }
    else {
        $versions = @("22", "24", "25", "26")
    }

    Write-Host ""
    Write-Host "  4-1. C# multi-version build:" -ForegroundColor Cyan
    $buildAllPass = $true
    foreach ($ver in $versions) {
        $shortVer = $ver
        if ($ver.Length -eq 4) { $shortVer = $ver.Substring(2) }

        Write-Host "  Building Release.R$shortVer..." -ForegroundColor DarkGray -NoNewline
        $buildResult = & dotnet build -c "Release.R$shortVer" "$projectRoot\MCP\RevitMCP.csproj" 2>&1
        $buildSuccess = $LASTEXITCODE -eq 0

        if ($buildSuccess) {
            $dll = Get-Item "$projectRoot\MCP\bin\Release.R$shortVer\RevitMCP.dll" -ErrorAction SilentlyContinue
            if ($dll) {
                Write-Host "" # newline after -NoNewline
                Write-Check "R$shortVer build ($($dll.Length) bytes)" $true
            }
            else {
                Write-Host ""
                Write-Check "R$shortVer DLL output" $false "Build succeeded but DLL not found"
                $buildAllPass = $false
            }
        }
        else {
            Write-Host ""
            $errorLines = ($buildResult | Select-String "error") -join "; "
            Write-Check "R$shortVer build" $false $errorLines
            $buildAllPass = $false
        }
    }

    Write-Host ""
    Write-Host "  4-2. MCP Server build:" -ForegroundColor Cyan
    $mcpServerDir = Join-Path $projectRoot "MCP-Server"
    if (Test-Path "$mcpServerDir\package.json") {
        Push-Location $mcpServerDir
        $npmResult = & npm run build 2>&1
        $npmSuccess = $LASTEXITCODE -eq 0
        Pop-Location

        $indexJs = Join-Path $mcpServerDir "build\index.js"
        Write-Check "npm run build" ($npmSuccess -and (Test-Path $indexJs)) "MCP Server build failed"
    }
    else {
        Write-Check "MCP Server package.json" $false "Cannot build - package.json missing"
    }
}

# ─────────────────────────────────────────────
# Phase 5: Deployment Verification (Windows only)
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "[Phase 5] Deployment Verification" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

if ($SkipDeploy) {
    Write-Skip "Deployment check" "Skipped via -SkipDeploy flag"
}
else {
    # -AddinsRoot 未指定時使用實際部署位置
    $addinsBase = $AddinsRoot
    if (-not $addinsBase) { $addinsBase = Join-Path $env:APPDATA "Autodesk\Revit\Addins" }

    # 版本→建構組態對應。與 scripts/install-addon.ps1 的 $versionConfigMap（L233-240）
    # 刻意重複定義——兩支都是獨立入口腳本，抽共用模組的 import 成本大於 5 行常值；
    # 修改任一側時必須同步另一側（雙向註解互指）。
    $versionConfigMap = @{
        "2022" = "Release.R22"
        "2023" = "Release.R23"
        "2024" = "Release.R24"
        "2025" = "Release.R25"
        "2026" = "Release.R26"
    }
    # supportedVersions 由對應表導出，檔內單一事實來源（取代原硬編陣列）
    $supportedVersions = @($versionConfigMap.Keys | Sort-Object)

    Write-Host ""
    Write-Host "  5-1. Installed addin locations:" -ForegroundColor Cyan
    $installedVersions = @()
    foreach ($ver in $supportedVersions) {
        $addinsDir = Join-Path $addinsBase $ver
        if (Test-Path $addinsDir) {
            $addinFiles = Get-ChildItem -Path $addinsDir -Filter "*.addin" -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match "RevitMCP|revit-mcp" }

            if ($addinFiles.Count -gt 0) {
                $installedVersions += $ver
                Write-Host "  Revit $ver : $($addinFiles.Count) addin file(s)" -ForegroundColor Gray
                foreach ($f in $addinFiles) {
                    Write-Host "    $($f.FullName)" -ForegroundColor DarkGray
                }
            }
        }
    }

    if ($installedVersions.Count -eq 0) {
        Write-Host "  No RevitMCP installations detected" -ForegroundColor DarkGray
        Write-Skip "Deployment check" "No installations found"
    }

    Write-Host ""
    Write-Host "  5-2. Duplicate addin detection:" -ForegroundColor Cyan
    $duplicateFound = $false
    foreach ($ver in $installedVersions) {
        $addinsDir = Join-Path $addinsBase $ver
        $addinFiles = Get-ChildItem -Path $addinsDir -Filter "*.addin" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "RevitMCP|revit-mcp" }

        if ($addinFiles.Count -gt 1) {
            Write-Check "Revit $ver single addin" $false "Found $($addinFiles.Count) addin files - keep only ONE"
            $duplicateFound = $true
        }
        elseif ($addinFiles.Count -eq 1) {
            # Verify DLL exists — 從 .addin 讀 Assembly 路徑，支援根目錄或子資料夾部署
            $addinContent = Read-FileText $addinFiles[0].FullName
            $dllDir = $addinFiles[0].DirectoryName
            $assemblyPath = if ($addinContent -match "<Assembly>([^<]+)</Assembly>") { $matches[1] } else { "RevitMCP.dll" }
            $dllPath = Join-Path $dllDir $assemblyPath
            if (Test-Path $dllPath) {
                $dll = Get-Item $dllPath
                Write-Check "Revit $ver DLL present ($($dll.Length) bytes)" $true
            }
            else {
                Write-Check "Revit $ver DLL present" $false "DLL missing at $dllPath (from .addin Assembly: $assemblyPath)"
            }
        }
    }
    if (-not $duplicateFound -and $installedVersions.Count -gt 0) {
        Write-Check "No duplicate addin files" $true
    }

    # 各版本部署狀態盤點（#91 標準配置：Addins\<year>\RevitMCP.addin + Addins\<year>\RevitMCP\*.dll）
    $deployStates = @()
    foreach ($ver in $supportedVersions) {
        $addinsDir = Join-Path $addinsBase $ver
        if (-not (Test-Path -LiteralPath $addinsDir)) { continue }   # 該版本 Revit 未安裝：整組靜默略過
        $buildConfig = $versionConfigMap[$ver]
        $binDir = Join-Path $projectRoot "MCP\bin\$buildConfig"
        $deployStates += @{
            Version   = $ver
            AddinsDir = $addinsDir
            Config    = $buildConfig
            BinDir    = $binDir
            HasBuild  = [bool](Test-Path -LiteralPath (Join-Path $binDir "RevitMCP.dll"))
            Deployed  = [bool]($installedVersions -contains $ver)     # 沿用 5-1 的偵測結果
            DeployDir = (Join-Path $addinsDir "RevitMCP")
        }
    }

    # 5-3: Deployed dependency completeness
    Write-Host ""
    Write-Host "  5-3. Deployed dependency completeness (deployed set must cover build output):" -ForegroundColor Cyan
    foreach ($st in $deployStates) {
        $ver = $st.Version
        if (-not $st.Deployed) {
            Write-Skip "Revit $ver dependency completeness" "RevitMCP not deployed for this version (user choice)"
            continue
        }
        if (-not $st.HasBuild) {
            Write-Skip "Revit $ver dependency completeness" "No build output at MCP\bin\$($st.Config) - run dotnet build -c $($st.Config) first"
            continue
        }
        $expected = @(Get-ChildItem -Path $st.BinDir -Filter "*.dll" -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $deployed = @(Get-ChildItem -Path $st.DeployDir -Filter "*.dll" -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $missing = @($expected | Where-Object { $_ -notin $deployed })
        Write-Check "Revit $ver dependency set complete ($($expected.Count) DLLs from $($st.Config))" ($missing.Count -eq 0) `
            $(if ($missing.Count -gt 0) { "Missing in $($st.DeployDir): $($missing -join ', ')" } else { "" })
    }

    # 5-4: Build freshness
    Write-Host ""
    Write-Host "  5-4. Build freshness (deployed RevitMCP.dll SHA256 vs build output):" -ForegroundColor Cyan
    $freshVersions = @()
    $staleVersions = @()
    foreach ($st in $deployStates) {
        $ver = $st.Version
        if (-not $st.Deployed) {
            Write-Skip "Revit $ver build freshness" "RevitMCP not deployed for this version (user choice)"
            continue
        }
        if (-not $st.HasBuild) {
            Write-Skip "Revit $ver build freshness" "No build output at MCP\bin\$($st.Config) - run dotnet build -c $($st.Config) first"
            continue
        }
        $deployedMain = Join-Path $st.DeployDir "RevitMCP.dll"
        if (-not (Test-Path -LiteralPath $deployedMain)) {
            Write-Skip "Revit $ver build freshness" "RevitMCP.dll not present in $($st.DeployDir) (see 5-3)"
            continue
        }
        $srcHash = (Get-FileHash -LiteralPath (Join-Path $st.BinDir "RevitMCP.dll") -Algorithm SHA256).Hash
        $dstHash = (Get-FileHash -LiteralPath $deployedMain -Algorithm SHA256).Hash
        if ($srcHash -eq $dstHash) {
            $freshVersions += $ver
            Write-Check "Revit $ver RevitMCP.dll matches $($st.Config) build output" $true
        }
        else {
            $staleVersions += $ver
            Write-Warn "Revit $ver RevitMCP.dll differs from $($st.Config) build output" `
                "deployed=$($dstHash.Substring(0,12))... build=$($srcHash.Substring(0,12))... - possibly not redeployed after rebuild; run scripts\install-addon.ps1"
        }
    }

    # 5-5: Cross-version deployment consistency (aggregates 5-4 results, no re-hashing)
    Write-Host ""
    Write-Host "  5-5. Cross-version deployment consistency:" -ForegroundColor Cyan
    if ($staleVersions.Count -gt 0) {
        Write-Warn "Deployed versions lag current build outputs" `
            "Stale: $($staleVersions -join ', ')$(if ($freshVersions.Count -gt 0) { "; in-sync: $($freshVersions -join ', ')" })"
    }
    elseif ($freshVersions.Count -gt 0) {
        Write-Check "All comparable deployed versions match current build outputs ($($freshVersions -join ', '))" $true
    }
    else {
        Write-Skip "Cross-version deployment consistency" "No version comparable (no deployments or no build outputs)"
    }

    # 5-6: Legacy root-level DLL residue (pre-#91 layout)
    Write-Host ""
    Write-Host "  5-6. Legacy root-level DLL residue (pre-#91 layout):" -ForegroundColor Cyan
    $residueFound = $false
    foreach ($st in $deployStates) {
        $rootDll = Join-Path $st.AddinsDir "RevitMCP.dll"
        if (Test-Path -LiteralPath $rootDll) {
            $residueFound = $true
            Write-Warn "Revit $($st.Version) has root-level RevitMCP.dll (pre-#91 layout)" `
                "Delete $rootDll - the manifest loads RevitMCP\RevitMCP.dll from the subfolder; the root copy is stale residue"
        }
    }
    if (-not $residueFound -and $deployStates.Count -gt 0) {
        Write-Check "No legacy root-level RevitMCP.dll residue" $true
    }
}

# ─────────────────────────────────────────────
# Phase 6: Domain Metadata and Shared SOP Quality
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "[Phase 6] Domain Metadata and Shared SOP Quality" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

$domainFiles = Get-ChildItem -Path "$projectRoot\domain" -Filter "*.md" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "README.md" }

Write-Host ""
Write-Host "  6-1. Required frontmatter fields:" -ForegroundColor Cyan
$frontmatterFailures = @()
foreach ($df in $domainFiles) {
    $text = Read-FileText $df.FullName
    $rel = $df.FullName.Replace("$projectRoot\", "").Replace("\", "/")
    if (-not $text -or -not ($text -match '(?s)^---\s*\r?\n(.*?)\r?\n---')) {
        $frontmatterFailures += "$rel missing YAML frontmatter"
        continue
    }

    $fm = $matches[1]
    foreach ($required in @("name:", "description:", "metadata:", "version:", "updated:")) {
        if ($fm -notmatch "(?m)^\s*$([regex]::Escape($required))") {
            $frontmatterFailures += "$rel missing $required"
        }
    }
}
Write-Check "Domain frontmatter required fields present" ($frontmatterFailures.Count -eq 0) `
    $(if ($frontmatterFailures.Count -gt 0) { ($frontmatterFailures | Select-Object -First 10) -join "`n" } else { "" })

Write-Host ""
Write-Host "  6-2. Domain files remain shared, not English-only:" -ForegroundColor Cyan
$englishOnlyDomain = @()
foreach ($df in $domainFiles) {
    $text = Read-FileText $df.FullName
    if (-not $text) { continue }
    $rel = $df.FullName.Replace("$projectRoot\", "").Replace("\", "/")
    $hasCjk = $text -match '[\u4e00-\u9fff]'
    if (-not $hasCjk) { $englishOnlyDomain += $rel }
}
if ($englishOnlyDomain.Count -gt 0) {
    Write-Warn "Some Domain files appear English-only" (($englishOnlyDomain | Select-Object -First 10) -join "`n")
}
else {
    Write-Check "Domain files retain Chinese-readable content" $true
}

Write-Host ""
Write-Host "  6-3. Domain related references resolve:" -ForegroundColor Cyan
$brokenRelated = @()
foreach ($df in $domainFiles) {
    $text = Read-FileText $df.FullName
    if (-not $text) { continue }
    $rel = $df.FullName.Replace("$projectRoot\", "").Replace("\", "/")
    foreach ($m in ([regex]'domain/[a-zA-Z0-9_\-\.\/]+\.md').Matches($text)) {
        $target = $m.Value
        if ($target -match '^domain/(xxx|example)[a-zA-Z0-9_\-]*\.md$') { continue }
        $full = Join-Path $projectRoot $target.Replace('/', '\')
        if (-not (Test-Path -LiteralPath $full)) {
            $brokenRelated += "$rel -> $target"
        }
    }
}
Write-Check "Domain-local domain/*.md references resolve" ($brokenRelated.Count -eq 0) `
    $(if ($brokenRelated.Count -gt 0) { ($brokenRelated | Select-Object -First 10) -join "`n" } else { "" })

# ─────────────────────────────────────────────
# Phase 7: Cross-Document Alignment
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "[Phase 7] Cross-Document Alignment" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

# Helpers — single source of truth for counts
function Get-ToolCount {
    $nodeScript = "import('./MCP-Server/build/tools/index.js').then(m=>{console.log(m.registerRevitTools().length)}).catch(()=>process.exit(2))"
    Push-Location $projectRoot
    $result = & node --input-type=module -e $nodeScript 2>$null
    $exit = $LASTEXITCODE
    Pop-Location
    if ($exit -eq 0 -and $result -match '^\d+$') {
        return [int]$result
    }

    Write-Warn "Runtime tool registry count unavailable" "Falling back to source regex count. Run npm run build if this is unexpected."
    $hits = Select-String -Path "$projectRoot\MCP-Server\src\tools\*.ts" `
        -Pattern '^\s+name:\s*[''"]' -ErrorAction SilentlyContinue
    return $hits.Count
}

function Get-ToolNames {
    # Same runtime registry as Get-ToolCount. Returns $null when the build is unavailable,
    # so the caller can SKIP rather than report a false failure.
    $nodeScript = "import('./MCP-Server/build/tools/index.js').then(m=>{console.log(m.registerRevitTools().map(t=>t.name).join('\n'))}).catch(()=>process.exit(2))"
    Push-Location $projectRoot
    $result = & node --input-type=module -e $nodeScript 2>$null
    $exit = $LASTEXITCODE
    Pop-Location
    if ($exit -ne 0 -or -not $result) { return $null }
    return @($result | Where-Object { $_ -match '\S' })
}

function Get-DomainCount {
    # All domain/*.md including meta — single grand total
    $rootCount = (Get-ChildItem -Path "$projectRoot\domain" -Filter "*.md" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'README.md' }).Count
    $refCount = (Get-ChildItem -Path "$projectRoot\domain\references" -Filter "*.md" -ErrorAction SilentlyContinue).Count
    return ($rootCount + $refCount)
}

function Get-SkillCount {
    return (Get-ChildItem -Path "$projectRoot\.claude\skills\*\SKILL.md" -ErrorAction SilentlyContinue).Count
}

function Find-StaleNumbers {
    param([string]$Pattern, [string[]]$Paths, [string[]]$Exclude = @())
    $results = @()
    foreach ($p in $Paths) {
        $hits = Select-String -Path $p -Pattern $Pattern -ErrorAction SilentlyContinue
        foreach ($h in $hits) {
            $isExcluded = $false
            foreach ($ex in $Exclude) { if ($h.Path -like "*$ex*") { $isExcluded = $true; break } }
            if (-not $isExcluded) { $results += $h }
        }
    }
    return $results
}

$toolCount = Get-ToolCount
$domainCount = Get-DomainCount
$skillCount = Get-SkillCount

Write-Host ""
Write-Host "  7-0. Source-of-truth counts:" -ForegroundColor Cyan
Write-Host "    Skills  = $skillCount  (.claude/skills/*/SKILL.md)" -ForegroundColor Gray
Write-Host "    Domain  = $domainCount  (domain/*.md ex README + domain/references/*.md)" -ForegroundColor Gray
Write-Host "    Tools   = $toolCount  (runtime registerRevitTools())" -ForegroundColor Gray

# Exclude: archived snapshots, log files, immutable date-prefixed snapshot HTMLs, external bundled mirrors
# Snapshot policy: every docs/MMDD-*.html is an immutable event snapshot — its numbers reflect
# the event date and are never re-synced. Living documents (BIM_MCP reference) stay in scope.
# Exclusions are enumerated here and nowhere else. Note what is deliberately ABSENT:
# docs\BIM_MCP\2026-*\ stays in scope. Those pages are mixed - frozen day-of-harvest numbers
# sit next to live navigation cards describing the CURRENT index pages. A file-level exemption
# would re-create the very blind spot the recursive glob was added to close.
$skipPatterns = @('_archive', '\log\', '\docs\0425-', '\docs\0523-', 'reference\external')

# Scan target files for claim-site checks (7-1/7-2/7-3)
# DEFAULT IS SCAN. The BIM_MCP glob is recursive on purpose: a non-recursive glob silently
# skipped docs\BIM_MCP\2026-*\ and let three stale domain-count claims sit there while 7-2
# reported PASS. Exclusions must be explicit ($skipPatterns), never a side effect of depth.
$scanPaths = @(
    "$projectRoot\CLAUDE.md",
    "$projectRoot\README.md",
    "$projectRoot\README.zh-TW.md",
    "$projectRoot\docs\DOCUMENT_AUDIENCE_INVENTORY.md",
    "$projectRoot\docs\BIM_MCP\**\*.html",
    "$projectRoot\docs\BIM_MCP\shared.js"
)

# Known claim-site patterns — ONLY match GRAND-TOTAL claim phrases (not "5 個 ARCHI 工具" type batch counts).
# Each: { Pattern (regex w/ 1 capture group) ; Truth ; Label }
# Truth note: Domain Knowledge heading + N Domain refs use $domainCount+1 because 1 entry is from domain/references/
$claimSites = @(
    # Markdown count-table claims (CLAUDE.md / README.md / README.zh-TW.md / DOCUMENT_AUDIENCE_INVENTORY.md)
    @{ Pattern = '\|\s*Runtime MCP tools\s*\|\s*(\d+)\s*\|';           Truth = $toolCount;          Label = '| Runtime MCP tools | N |' },
    @{ Pattern = '\|\s*Domain SOP files\s*\|\s*(\d+)\s*\|';            Truth = $domainCount;        Label = '| Domain SOP files | N |' },
    @{ Pattern = '\|\s*Claude skills\s*\|\s*(\d+)\s*\|';               Truth = $skillCount;         Label = '| Claude skills | N |' },
    # Tool count grand-total claims
    @{ Pattern = '共用\s*(\d+)\s*個工具';                              Truth = $toolCount;          Label = '共用 N 個工具' },
    @{ Pattern = '個\s*Domain[、，]\s*(\d+)\s*個工具';                  Truth = $toolCount;          Label = 'N 個工具 (hero 三層)' },
    @{ Pattern = '\((\d+)\+?\s*commands?\)';                           Truth = $toolCount;          Label = '(N+ commands)'; Dormant = $true },
    @{ Pattern = '\((\d+)\s+tools?,';                                  Truth = $toolCount;          Label = '(N tools, ...)'; Dormant = $true },
    @{ Pattern = '封裝\s*(\d+)\s*個\s*tools?';                          Truth = $toolCount;          Label = '封裝 N 個 tools'; Dormant = $true },
    @{ Pattern = '(\d+)\s*個\s*MCP\s*tools?\b';                        Truth = $toolCount;          Label = '個 MCP tools' },
    @{ Pattern = '(\d+)\s*個\s*原子工具';                              Truth = $toolCount;          Label = '個原子工具' },
    @{ Pattern = '(\d+)\s*個\s*語意化工具';                            Truth = $toolCount;          Label = '個語意化工具' },
    @{ Pattern = 'Tool[s]?[（(](\d+)[)）]';                             Truth = $toolCount;          Label = 'Tool（N）' },
    @{ Pattern = '「(\d+)\s*工具編排平台';                              Truth = $toolCount;          Label = '「N 工具編排平台」'; Dormant = $true },
    @{ Pattern = '警告：(\d+)\s*工具不該';                              Truth = $toolCount;          Label = '警告：N 工具不該' },
    @{ Pattern = '(\d+)\s*個工具可以組合';                              Truth = $toolCount;          Label = 'N 個工具可以組合'; Dormant = $true },
    # tools-index.html claim sites (the page is generated, but the prose around it is not)
    @{ Pattern = 'Tools\s*索引（(\d+)\s*個）';                       Truth = $toolCount;          Label = 'Tools 索引（N 個）' },
    @{ Pattern = 'TOOLS INDEX[^<]*<span[^>]*>(\d+)\s*個';                 Truth = $toolCount;          Label = 'TOOLS INDEX eyebrow N 個' },
    @{ Pattern = '>(\d+)\s+Tools</h4>';                              Truth = $toolCount;          Label = 'hub card N Tools' },
    @{ Pattern = '(\d+)\s*個\s*MCP\s*工具完整索引';                     Truth = $toolCount;          Label = 'N 個 MCP 工具完整索引' },
    @{ Pattern = '八類加總等於\s*(\d+)';                            Truth = $toolCount;          Label = '八類加總等於 N' },
    @{ Pattern = '(\d+)\s*個工具攤開';                                Truth = $toolCount;          Label = 'N 個工具攤開' },
    @{ Pattern = '(\d+)\s*個工具\s*·\s*依用途分組';                      Truth = $toolCount;          Label = 'N 個工具·依用途分組' },
    # "N 個執行層工具" — the phrase lives in two files (shared.js PAGE_META desc, tools-index.html
    # og:title). No pattern above matches this shape, so both copies of the number were unguarded:
    # 7-1 could report PASS while they disagreed with each other and with the registry.
    @{ Pattern = '(\d+)\s*個執行層工具';                               Truth = $toolCount;          Label = 'N 個執行層工具' },
    # Domain count grand-total claims
    @{ Pattern = 'Domain Knowledge.{0,40}（(\d+)\s*個';                Truth = $domainCount; Label = 'Domain Knowledge 標題'; Dormant = $true },
    # (?<![+\d]) excludes increment notation: "+6 Domain SOP" means six were added, not a total of six.
    @{ Pattern = '(?<![+\d])(\d+)\+?\s*個?\s*Domain\b';                Truth = $domainCount; Label = 'N Domain' },
    @{ Pattern = '(\d+)\s*個\s*SOP';                                   Truth = $domainCount; Label = '個 SOP' },
    @{ Pattern = '(\d+)\s*個\s*domain/\*\.md';                         Truth = $domainCount; Label = '個 domain/*.md' },
    @{ Pattern = '(\d+)\s*個\s*<code>domain';                          Truth = $domainCount; Label = '個 <code>domain' },
    # Skill count grand-total claims (must require explicit grand-total context)
    # CLAUDE.md's Skills section dropped the （N 個） heading form and now states the count inline
    # as "(54 skills; count table above is the gate)". The heading pattern below went dormant and
    # nothing guarded the replacement until 7-13 surfaced it.
    @{ Pattern = '(?i)\((\d+)\s+skills\b';                            Truth = $skillCount;         Label = '(N skills) inline' },
    @{ Pattern = '##\s*Skills（(\d+)\s*個）';                           Truth = $skillCount;         Label = '## Skills（N 個）'; Dormant = $true },
    @{ Pattern = 'Skills\s*索引（(\d+)\s*個）';                         Truth = $skillCount;         Label = 'Skills 索引（N 個）' },
    @{ Pattern = '(\d+)\s*個編排層\s*Skill';                            Truth = $skillCount;         Label = 'N 個編排層 Skill' },
    @{ Pattern = '(\d+)\s*Skill\s*vs\b';                               Truth = $skillCount;         Label = 'N Skill vs ...' },
    @{ Pattern = 'Skill\s*=\s*編排（(\d+)\s*個';                        Truth = $skillCount;         Label = 'Skill = 編排（N 個' },
    @{ Pattern = 'SKILLS INDEX[^<]*<span[^>]*>(\d+)\s*個';              Truth = $skillCount;         Label = 'SKILLS INDEX eyebrow N 個' },
    @{ Pattern = '>(\d+)\s+Skills</h4>';                                Truth = $skillCount;         Label = 'hub card N Skills' },

    # --- Stage B-4 additions: expanded claim-site coverage ---
    # Confirmed gate blind spot: existing patterns above are case-sensitive on literal
    # "Domain"/"Skill"/"Tool" and only match a handful of exact phrase shapes, so lowercase
    # variants (e.g. "79 個 domain SOP") and alternate shapes (e.g. "Skill（50・...)") slip
    # through uncaught. Every pattern below was verified against the real scanned files with
    # the same [regex] engine used by Find-ClaimMismatches before being added, specifically to
    # confirm it only matches genuine grand-total claims and never a batch/contextual count
    # (e.g. "2 個 domain 檔手動搬移收編" in contributors.html, or "3 個 Skill 引用"/"5 個 Skill
    # 內" describing a subset, not the whole catalog) — those must NOT be flagged.
    # No existing pattern above is modified, widened, or removed.

    # Domain count: lowercase "domain" and alternate label shapes
    @{ Pattern = '(?i)(\d+)\s*個\s*domain\s*SOP';                       Truth = $domainCount;        Label = '個 domain SOP (case-insensitive)' },
    @{ Pattern = '(?i)(\d+)\s*個\s*知識層';                              Truth = $domainCount;        Label = '個知識層' },
    @{ Pattern = '(?i)(\d+)\s+professional\s+BIM\s+SOPs?';              Truth = $domainCount;        Label = 'N professional BIM SOPs' },
    @{ Pattern = '(?i)Domain\s*索引（(\d+)）';                           Truth = $domainCount;        Label = 'Domain 索引（N）(shared.js nav)' },
    @{ Pattern = 'Domain[（(](\d+)[)）]';                                Truth = $domainCount;        Label = 'Domain（N）hub card' },
    @{ Pattern = 'Skill[s]?[（(](\d+)[)）]';                             Truth = $skillCount;         Label = 'Skill（N）hub card' },

    # Skill count: shapes that don't require a "編排層"/"索引" suffix (still anchored to the
    # exact surrounding phrase, not a bare "N 個 Skill", to avoid the batch-count false
    # positives found in contributor-template.html / architecture-v2.html / skills-index.html)
    @{ Pattern = '本專案目前[^\d]{0,20}(\d+)\s*個\s*Skill\b';            Truth = $skillCount;         Label = '本專案目前 N 個 Skill' },
    @{ Pattern = '(\d+)\s*個\s*Skill\s*完整索引';                        Truth = $skillCount;         Label = 'N 個 Skill 完整索引' },
    @{ Pattern = '←\s*(\d+)\s*個\s*Skill\b';                             Truth = $skillCount;         Label = '← N 個 Skill (vault tree)' },
    @{ Pattern = '由\s*(\d+)\s*個\s*Skill\s*編排觸發';                    Truth = $skillCount;         Label = '由 N 個 Skill 編排觸發' },
    @{ Pattern = '本頁列\s*(\d+)\s*個\s*Skill\b';                        Truth = $skillCount;         Label = '本頁列 N 個 Skill' },
    @{ Pattern = '(\d+)\s*個\s*Skill\s*列表';                            Truth = $skillCount;         Label = 'N 個 Skill 列表' },
    @{ Pattern = 'CATALOG\s*·\s*(\d+)\s*個\s*Skill\b';                   Truth = $skillCount;         Label = 'CATALOG · N 個 Skill' },
    @{ Pattern = '(\d+)\s*個\s*Skill\s*·\s*依用途分組';                   Truth = $skillCount;         Label = 'N 個 Skill · 依用途分組' },
    @{ Pattern = '抽取\s*(\d+)\s*個\s*Skill\b';                          Truth = $skillCount;         Label = '抽取 N 個 Skill' },

    # Tool count: case-insensitive "MCP Tool(s)" and an alternate "透過 N 個工具" shape
    @{ Pattern = '(?i)(\d+)\s*個\s*MCP\s*Tools?\b';                      Truth = $toolCount;          Label = '個 MCP Tool(s) (case-insensitive)' },
    @{ Pattern = '透過\s*(\d+)\s*個工具';                                Truth = $toolCount;          Label = '透過 N 個工具' },

    # Three-layer shorthand "Skill（N・...）→ Domain（N・...）→ Tool（N・...）" (docs/BIM_MCP/index.html)
    @{ Pattern = 'Skill（(\d+)・';                                       Truth = $skillCount;         Label = 'Skill（N・...）three-layer shorthand' },
    @{ Pattern = 'Domain（(\d+)・';                                      Truth = $domainCount;        Label = 'Domain（N・...）three-layer shorthand' },
    @{ Pattern = 'Tool（(\d+)・';                                        Truth = $toolCount;          Label = 'Tool（N・...）three-layer shorthand' }
)

# Resolve all paths (glob → file list)
$scanFiles = @()
foreach ($p in $scanPaths) {
    if ($p -match '\*\*') {
        # "<base>\**\<filter>" - every subdirectory, any depth.
        $base   = $p -replace '\\\*\*\\[^\\]+$', ''
        $filter = ($p -split '\\')[-1]
        $scanFiles += Get-ChildItem -Path $base -Filter $filter -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
    } elseif ($p -match '\*') {
        $scanFiles += Get-ChildItem -Path $p -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
    } elseif (Test-Path -LiteralPath $p) {
        $scanFiles += $p
    }
}
$scanFiles = $scanFiles | Sort-Object -Unique
# Apply skip filter
$scanFiles = $scanFiles | Where-Object {
    $f = $_
    -not ($skipPatterns | Where-Object { $f -like "*$_*" })
}

# 7-1/7-2/7-3 unified exact-match scanner
function Find-ClaimMismatches {
    param([array]$Files, [hashtable]$Site)
    $mismatches = @()
    # Build the regex once, and probe the whole file before splitting it into lines: most
    # (pattern, file) pairs never match at all, and line-splitting every ~50-pattern pass over a
    # 2000-line page is where the runtime goes. Same verdicts, far less work.
    $rx = [regex]$Site.Pattern
    foreach ($f in $Files) {
        $text = Read-FileText $f
        if (-not $text) { continue }
        if (-not $rx.IsMatch($text)) { continue }
        $lines = $text -split "`r?`n"
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $matches = $rx.Matches($lines[$i])
            foreach ($m in $matches) {
                $claimed = [int]$m.Groups[1].Value
                if ($claimed -ne $Site.Truth) {
                    $rel = $f.Replace("$projectRoot\", "").Replace("\", "/")
                    $mismatches += "    $rel`:$($i+1)  '$($Site.Label)' claims $claimed, truth is $($Site.Truth)"
                }
            }
        }
    }
    return $mismatches
}

# 7-1: Tool count exact-match
Write-Host ""
Write-Host "  7-1. Tool count exact-match (truth = $toolCount):" -ForegroundColor Cyan
$toolSites = $claimSites | Where-Object { $_.Truth -eq $toolCount }
$toolMismatches = @()
foreach ($site in $toolSites) {
    $toolMismatches += Find-ClaimMismatches -Files $scanFiles -Site $site
}
Write-Check "All tool-count claims == $toolCount" ($toolMismatches.Count -eq 0) `
    $(if ($toolMismatches.Count -gt 0) { "$($toolMismatches.Count) mismatch(es)." } else { "" })
if ($toolMismatches.Count -gt 0) { $toolMismatches | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow } }

# 7-2: Domain count exact-match
Write-Host ""
Write-Host "  7-2. Domain count exact-match (truth = $domainCount incl references):" -ForegroundColor Cyan
$domainSites = $claimSites | Where-Object { $_.Truth -eq $domainCount }
$domainMismatches = @()
foreach ($site in $domainSites) {
    $domainMismatches += Find-ClaimMismatches -Files $scanFiles -Site $site
}
Write-Check "All domain-count claims == $domainCount" ($domainMismatches.Count -eq 0) `
    $(if ($domainMismatches.Count -gt 0) { "$($domainMismatches.Count) mismatch(es)." } else { "" })
if ($domainMismatches.Count -gt 0) { $domainMismatches | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow } }

# 7-3: Skill count exact-match
Write-Host ""
Write-Host "  7-3. Skill count exact-match (truth = $skillCount):" -ForegroundColor Cyan
$skillSites = $claimSites | Where-Object { $_.Truth -eq $skillCount }
$skillMismatches = @()
foreach ($site in $skillSites) {
    $skillMismatches += Find-ClaimMismatches -Files $scanFiles -Site $site
}
Write-Check "All skill-count claims == $skillCount" ($skillMismatches.Count -eq 0) `
    $(if ($skillMismatches.Count -gt 0) { "$($skillMismatches.Count) mismatch(es)." } else { "" })
if ($skillMismatches.Count -gt 0) { $skillMismatches | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow } }

# 7-4: CLAUDE.md table → real domain files (forward check)
Write-Host ""
Write-Host "  7-4. CLAUDE.md domain table -> real files:" -ForegroundColor Cyan
$claudeMd = Read-FileText "$projectRoot\CLAUDE.md"
# Match real domain paths only; reject literal placeholders like {file}.md, {name}.md
$tablePattern = [regex]'`domain/[a-zA-Z0-9_\-\.\/]+\.md`'
$tableRefs = $tablePattern.Matches($claudeMd) | ForEach-Object { $_.Value.Trim('`') } | Sort-Object -Unique
$missingFiles = @()
foreach ($ref in $tableRefs) {
    $full = Join-Path $projectRoot $ref.Replace('/', '\')
    if (-not (Test-Path $full)) { $missingFiles += $ref }
}
Write-Check "All $($tableRefs.Count) CLAUDE.md domain refs resolve" ($missingFiles.Count -eq 0) `
    $(if ($missingFiles.Count -gt 0) { "Missing: $($missingFiles -join ', ')" } else { "" })

# 7-5: Real domain files → CLAUDE.md table (reverse check)
Write-Host ""
Write-Host "  7-5. Real domain files -> CLAUDE.md table:" -ForegroundColor Cyan
$metaDomain = @('README.md', 'frontmatter-standard.md', 'lessons.md', 'qa-checklist.md',
                'path-maintenance-qa.md', 'session-context-guard.md',
                'tool-capability-boundary.md', 'skill-authoring-standard.md')
$realDomain = Get-ChildItem -Path "$projectRoot\domain" -Filter "*.md" |
    Where-Object { $_.Name -notin $metaDomain } | ForEach-Object { $_.Name }
$notInTable = @()
foreach ($f in $realDomain) {
    if ($claudeMd -notmatch [regex]::Escape("domain/$f")) { $notInTable += $f }
}
Write-Check "All real domain files appear in CLAUDE.md table" ($notInTable.Count -eq 0) `
    $(if ($notInTable.Count -gt 0) { "Missing from table: $($notInTable -join ', ')" } else { "" })

# 7-6: BIM_MCP web internal links — domain/* / .claude/skills/* targets must exist
Write-Host ""
Write-Host "  7-6. BIM_MCP web link resolution:" -ForegroundColor Cyan
$webFiles = @()
$webFiles += Get-ChildItem -Path "$projectRoot\docs\BIM_MCP" -Filter "*.html" -ErrorAction SilentlyContinue
$webFiles += Get-ChildItem -Path "$projectRoot\docs\BIM_MCP\reference" -Filter "*.html" -ErrorAction SilentlyContinue
$linkPattern = [regex]'href="\.\./\.\./(domain/[^"#]+\.md|\.claude/skills/[^"#]+)"'
$brokenLinks = @()
foreach ($wf in $webFiles) {
    $content = Read-FileText $wf.FullName
    $matches = $linkPattern.Matches($content)
    foreach ($m in $matches) {
        $target = $m.Groups[1].Value
        $full = Join-Path $projectRoot $target.Replace('/', '\')
        if (-not (Test-Path $full)) { $brokenLinks += "$($wf.Name) -> $target" }
    }
}
Write-Check "No broken BIM_MCP -> source links" ($brokenLinks.Count -eq 0) `
    $(if ($brokenLinks.Count -gt 0) { "First broken: $($brokenLinks[0])" } else { "" })

# 7-7: Local markdown-link rot lint
# Scans README.md / README.zh-TW.md / DOCS_STRUCTURE.md / domain/*.md / .claude/skills/*/SKILL.md
# for markdown links [text](path) where path is a local relative file. Each target must exist.
Write-Host ""
Write-Host "  7-7. Local markdown link rot lint:" -ForegroundColor Cyan

$linkScanFiles = @()
$linkScanFiles += "$projectRoot\CLAUDE.md"
$linkScanFiles += "$projectRoot\README.md"
$linkScanFiles += "$projectRoot\README.zh-TW.md"
$linkScanFiles += "$projectRoot\docs\DOCS_STRUCTURE.md"
$linkScanFiles += Get-ChildItem -Path "$projectRoot\domain" -Filter "*.md" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
$linkScanFiles += Get-ChildItem -Path "$projectRoot\.claude\skills\*\SKILL.md" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }

# Markdown link [text](path). Capture group 1 = path.
# Exclude: URLs (http://, https://, mailto:), in-page anchors (#...), Windows paths with drive letter
$mdLinkRx = [regex]'\[(?:[^\]]+)\]\(([^)\s]+?)\)'
$rotted = @()
$totalChecked = 0
foreach ($lf in $linkScanFiles) {
    $text = Read-FileText $lf
    if (-not $text) { continue }
    $relFile = $lf.Replace("$projectRoot\", "").Replace("\", "/")
    $fileDir = Split-Path -Parent $lf
    $lines = $text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($m in $mdLinkRx.Matches($lines[$i])) {
            $target = $m.Groups[1].Value
            # Skip URLs, in-page anchors, mailto, image data URIs
            if ($target -match '^(https?:|mailto:|#|data:|ftp:)') { continue }
            # Skip if inside a code span — odd number of backticks before this match
            $before = $lines[$i].Substring(0, $m.Index)
            $backticksBefore = ($before.ToCharArray() | Where-Object { $_ -eq '`' }).Count
            if ($backticksBefore % 2 -eq 1) { continue }
            # Strip trailing #anchor / ?query
            $pathOnly = $target -replace '[#?].*$', ''
            if ([string]::IsNullOrWhiteSpace($pathOnly)) { continue }
            # Skip Windows-style absolute paths (drive letter) — unlikely in markdown but safe
            if ($pathOnly -match '^[A-Z]:[\\/]') { continue }
            $totalChecked++
            # Resolve relative path from the markdown file's directory
            $candidate = Join-Path $fileDir $pathOnly.Replace('/', '\')
            $resolved = $null
            try { $resolved = (Resolve-Path -LiteralPath $candidate -ErrorAction SilentlyContinue).Path } catch {}
            if (-not $resolved -or -not (Test-Path -LiteralPath $resolved)) {
                $rotted += "    ${relFile}:$($i+1)  -> $target"
            }
        }
    }
}
Write-Check "All $totalChecked local markdown links resolve" ($rotted.Count -eq 0) `
    $(if ($rotted.Count -gt 0) { "$($rotted.Count) broken link(s)" } else { "" })
if ($rotted.Count -gt 0) { $rotted | Select-Object -First 20 | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow } }

# 7-8: Snapshot banner — every date-prefixed docs/MMDD-*.html must declare itself an
# immutable snapshot via a data-snapshot="YYYY-MM-DD" attribute, so readers know its
# numbers are historical and QAQC count-sync intentionally skips it.
Write-Host ""
Write-Host "  7-8. Snapshot banner on date-prefixed HTML:" -ForegroundColor Cyan
$snapshotHtml = Get-ChildItem -Path "$projectRoot\docs\*.html" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d{4}-' }
$missingBanner = @()
foreach ($sf in $snapshotHtml) {
    $content = Read-FileText $sf.FullName
    if (-not $content -or $content -notmatch 'data-snapshot="\d{4}-\d{2}-\d{2}"') {
        $missingBanner += $sf.Name
    }
}
Write-Check "All $($snapshotHtml.Count) date-prefixed HTMLs carry data-snapshot banner" ($missingBanner.Count -eq 0) `
    $(if ($missingBanner.Count -gt 0) { "Missing banner: $($missingBanner -join ', ')" } else { "" })

# 7-9: Real domain files → BIM_MCP domain-index.html (forward check)
# Catches domains added to the repo but never carded in the teaching-site index
# (the family-inventory-cleanup omission class — count claim said 45 while the index
# only listed real cards for fewer). Forward-only: extra/illustrative sub-cards such as
# the beam-penetration-* breakdown of mep-csa-clash-detection have no repo file and are
# intentionally NOT counted, so this check ignores them.
Write-Host ""
Write-Host "  7-9. Real domain files -> BIM_MCP domain-index:" -ForegroundColor Cyan
$domainIndexText = Read-FileText "$projectRoot\docs\BIM_MCP\reference\domain-index.html"
$indexDomainNames = Get-ChildItem -Path "$projectRoot\domain" -Filter "*.md" |
    Where-Object { $_.Name -ne "README.md" } | ForEach-Object { $_.Name }
$indexDomainNames += Get-ChildItem -Path "$projectRoot\domain\references" -Filter "*.md" -ErrorAction SilentlyContinue |
    ForEach-Object { "references/$($_.Name)" }
$notInIndex = @()
foreach ($n in $indexDomainNames) {
    if (-not $domainIndexText -or $domainIndexText -notmatch [regex]::Escape($n)) { $notInIndex += $n }
}
Write-Check "All real domain files appear in BIM_MCP domain-index" ($notInIndex.Count -eq 0) `
    $(if ($notInIndex.Count -gt 0) { "Missing card(s): $($notInIndex -join ', ')" } else { "" })
if ($notInIndex.Count -gt 0) { $notInIndex | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }

# 7-10: Real skills <-> BIM_MCP skills-index.html (exactly-one check, both directions)
# Same omission class as 7-9 but for skills — skills-index count can be bumped without
# the matching skill card actually being added.
#
# "Present" is NOT enough. A presence-only check passes when a skill is carded TWICE,
# which is exactly what happened on 2026-08-10: two agents each added an
# archicad-skill-adapter card and QAQC still reported PASS. Count instead of test.
#
# Match with explicit terminators (</div>, </code>) so a skill name that is a prefix of
# another (e.g. `loop` vs `loop-up`) cannot satisfy the other's requirement.
Write-Host ""
Write-Host "  7-10. Real skills <-> BIM_MCP skills-index (exactly one card + one row each):" -ForegroundColor Cyan
$skillsIndexText = Read-FileText "$projectRoot\docs\BIM_MCP\reference\skills-index.html"
$skillNames = Get-ChildItem -Path "$projectRoot\.claude\skills" -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName 'SKILL.md') } | ForEach-Object { $_.Name }

$skillIndexProblems = @()
if (-not $skillsIndexText) {
    $skillIndexProblems += "skills-index.html unreadable"
}
else {
    foreach ($s in $skillNames) {
        $cardCount = ([regex]::Matches($skillsIndexText, 'class="skill-name">/' + [regex]::Escape($s) + '</div>')).Count
        $rowCount  = ([regex]::Matches($skillsIndexText, '<code>/' + [regex]::Escape($s) + '</code>')).Count
        if ($cardCount -eq 0) { $skillIndexProblems += "$s : missing card" }
        elseif ($cardCount -gt 1) { $skillIndexProblems += "$s : DUPLICATE card x$cardCount" }
        if ($rowCount -eq 0) { $skillIndexProblems += "$s : missing quick-table row" }
        elseif ($rowCount -gt 1) { $skillIndexProblems += "$s : DUPLICATE quick-table row x$rowCount" }
    }

    # Reverse direction: a card whose skill directory no longer exists (renamed/deleted skill
    # leaves a stale card, and the forward check above can never see it).
    foreach ($m in [regex]::Matches($skillsIndexText, 'class="skill-name">/([a-z0-9-]+)</div>')) {
        $carded = $m.Groups[1].Value
        if ($skillNames -notcontains $carded) { $skillIndexProblems += "$carded : orphan card (no .claude/skills/$carded/SKILL.md)" }
    }
}

Write-Check "Every skill has exactly one skills-index card and row" ($skillIndexProblems.Count -eq 0) `
    $(if ($skillIndexProblems.Count -gt 0) { "$($skillIndexProblems.Count) problem(s)." } else { "" })
if ($skillIndexProblems.Count -gt 0) { $skillIndexProblems | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }

# 7-11. MCP Registry publish consistency (hard gate).
# server.json <-> MCP-Server/package.json <-> schema must agree (3-place version
# parity + namespace + encoding). Delegates to the same validator the publish
# workflow uses. If drift is found, run the mcp-registry-sync (Sonnet) agent to fix
# (see CLAUDE.md -> MCP Registry Publish Consistency).
Write-Host ""
Write-Host "  7-11. MCP Registry publish consistency:" -ForegroundColor Cyan
$pyCmd = $null
foreach ($c in @('python3', 'python', 'py')) {
    $cmd = Get-Command $c -ErrorAction SilentlyContinue
    if (-not $cmd) { continue }
    # Windows: `python3` 常是 Microsoft Store 的 App Execution Alias stub（%LOCALAPPDATA%\Microsoft\WindowsApps\），
    # 執行只回 exit 9009 而不跑 Python，會讓本閘門誤判 FAIL。跳過 WindowsApps stub，並實測候選能真的執行。
    if ($cmd.Source -and $cmd.Source -like '*\WindowsApps\*') { continue }
    & $c -c "import sys" *> $null
    if ($LASTEXITCODE -eq 0) { $pyCmd = $c; break }
}
$validatorPath = "$projectRoot\scripts\validate_publish_consistency.py"
if (-not $pyCmd) {
    Write-Skip "MCP Registry publish consistency (server.json <-> package.json <-> schema)" "no python found"
}
elseif (-not (Test-Path $validatorPath)) {
    Write-Skip "MCP Registry publish consistency (server.json <-> package.json <-> schema)" "validator script missing"
}
else {
    Push-Location $projectRoot
    & $pyCmd $validatorPath *> $null
    $registryOk = ($LASTEXITCODE -eq 0)
    Pop-Location
    Write-Check "MCP Registry publish consistency (server.json <-> package.json <-> schema)" $registryOk `
        "Run 'python scripts/validate_publish_consistency.py'; use the mcp-registry-sync (Sonnet) agent to fix drift"
}

# 7-12: .agents/skills mirror fidelity — GIT-TRACKED mirrors only
#
# `.agents/skills/<name>/SKILL.md` mirrors let non-Claude clients (Agy / Codex) discover skills.
# The tracked ones arrived per-skill from contributors (commit 604cafe, `.agents` + `.claude` pair),
# NOT from a generator — so there is no rule that every skill must be mirrored, and this check does
# not require one. What it requires is that a mirror the repo ships still matches its source: a
# silently drifting mirror is worse than no mirror, because a non-Claude client reads it as current
# instructions and nothing anywhere reports a problem.
#
# ⚠️ Scope is deliberately limited to `git ls-files`. `.agents/skills/` is a MIXED namespace:
# locally-installed tools (observed: an OpenAI Codex desktop app) scan the project and write their
# own untracked mirrors into the same directory. Those are the user's machine state, not repo
# content — failing QA/QC on them would turn this gate red for reasons unrelated to the repository,
# on any machine that happens to have such a tool installed. Do not widen this to a filesystem scan.
Write-Host ""
Write-Host "  7-12. .agents/skills mirror fidelity (git-tracked mirrors only):" -ForegroundColor Cyan
$mirrorProblems = @()
$mirrorChecked = 0
Push-Location $projectRoot
$trackedMirrors = @(& git ls-files '.agents/skills/*/SKILL.md' 2>$null)
Pop-Location
if (-not $trackedMirrors -or $trackedMirrors.Count -eq 0) {
    Write-Skip ".agents/skills mirror fidelity" "no tracked mirrors"
}
else {
    function Get-NormalisedText([string]$p) {
        if (-not (Test-Path $p)) { return $null }
        return ((Get-Content -LiteralPath $p -Raw -Encoding UTF8) -replace "`r`n", "`n").TrimEnd()
    }
    foreach ($rel in $trackedMirrors) {
        $skillName = (Split-Path (Split-Path $rel -Parent) -Leaf)
        $mirrorPath = Join-Path $projectRoot ($rel -replace '/', '\')
        $sourcePath = Join-Path "$projectRoot\.claude\skills\$skillName" 'SKILL.md'
        $mirrorChecked++
        if (-not (Test-Path $sourcePath)) {
            $mirrorProblems += "$skillName : orphan mirror (no .claude/skills/$skillName/SKILL.md)"
            continue
        }
        if ((Get-NormalisedText $sourcePath) -ne (Get-NormalisedText $mirrorPath)) {
            $mirrorProblems += "$skillName : tracked mirror out of sync with .claude/skills/$skillName/SKILL.md"
        }
    }
    Write-Check "Tracked .agents/skills mirrors match their sources ($mirrorChecked checked)" ($mirrorProblems.Count -eq 0) `
        $(if ($mirrorProblems.Count -gt 0) { "$($mirrorProblems.Count) problem(s). Re-copy the source over the mirror, or drop the mirror from git." } else { "" })
    if ($mirrorProblems.Count -gt 0) { $mirrorProblems | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }
}

# ─────────────────────────────────────────────
# Phase 8: Document Audience and Encoding Hygiene
# ─────────────────────────────────────────────
Write-Host ""
# 7-13: Claim-pattern liveness
Write-Host ""
Write-Host "  7-13. Claim-pattern liveness:" -ForegroundColor Cyan
# A pattern that matches nothing and a pattern that matches N correct sites both report PASS.
# That makes silent coverage loss invisible: reword a page and its guard quietly stops guarding.
# Dormant = $true means "the claim site is known to be gone" - an explicit, reviewable decision.
$deadPatterns = @()
$activeSites  = @($claimSites | Where-Object { -not $_.Dormant })
$dormantCount = @($claimSites | Where-Object { $_.Dormant }).Count
# Read each file once up front instead of once per pattern (48 patterns x 19 files was 900+ reads).
$scanTexts = @()
foreach ($f in $scanFiles) {
    $t = Read-FileText $f
    if ($t) { $scanTexts += $t }
}
foreach ($site in $activeSites) {
    $rx = [regex]$site.Pattern
    $found = $false
    foreach ($t in $scanTexts) {
        if ($rx.IsMatch($t)) { $found = $true; break }
    }
    if (-not $found) { $deadPatterns += $site.Label }
}
Write-Check "All $($activeSites.Count) active claim patterns still match a live site ($dormantCount dormant)" ($deadPatterns.Count -eq 0) `
    $(if ($deadPatterns.Count -gt 0) { "$($deadPatterns.Count) pattern(s) match nothing. Either the claim was reworded (find it and re-guard it) or the site is gone (mark Dormant = `$true)." } else { "" })
if ($deadPatterns.Count -gt 0) { $deadPatterns | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }

Write-Host ""
# 7-14: Real tools <-> BIM_MCP tools-index
Write-Host ""
Write-Host "  7-14. Real tools -> BIM_MCP tools-index (exactly one card each):" -ForegroundColor Cyan
# Domain (7-9) and Skill (7-10) were enumerated against the hub; Tool - the largest layer - was not.
# 7-1 only checks that the tool-count NUMBER is stated correctly; a correct count is compatible with zero
# tools being documented. This check closes that gap by enumerating, not counting.
$toolsIndex = Join-Path $projectRoot "docs\BIM_MCP\reference\tools-index.html"
$toolNames  = Get-ToolNames
if (-not $toolNames) {
    Write-Skip "tools-index enumeration" "runtime tool registry unavailable (run npm run build)"
}
elseif (-not (Test-Path -LiteralPath $toolsIndex)) {
    Write-Check "Every tool has exactly one tools-index card" $false "docs/BIM_MCP/reference/tools-index.html is missing"
}
else {
    $indexText = Read-FileText $toolsIndex
    $cardProblems = @()
    foreach ($t in $toolNames) {
        $hits = ([regex]('data-tool="' + [regex]::Escape($t) + '"')).Matches($indexText).Count
        if ($hits -ne 1) { $cardProblems += "$t : $hits card(s), expected exactly 1" }
    }
    # Reverse direction: a card with no matching runtime tool is just as wrong.
    $carded = @(([regex]'data-tool="([^"]+)"').Matches($indexText) | ForEach-Object { $_.Groups[1].Value })
    foreach ($c in $carded) {
        if ($toolNames -notcontains $c) { $cardProblems += "$c : card exists but no such runtime tool" }
    }
    # The page is generated, so its own derived numbers (badge tallies, per-category counts) must
    # agree with the cards it actually contains. Guarding these with more regex claim-sites would be
    # the hand-written-list trap again; checking generation self-consistency is the structural answer.
    $cardCount = ([regex]'class="tool-card"').Matches($indexText).Count
    $badge = @{
        readonly    = ([regex]'tool-badge readonly').Matches($indexText).Count
        write       = ([regex]'tool-badge write').Matches($indexText).Count
        destructive = ([regex]'tool-badge destructive').Matches($indexText).Count
    }
    $stated = @{}
    foreach ($pair in @(@('readonly', '唯讀'), @('write', '會寫入'), @('destructive', '破壞性'))) {
        $m = ([regex]("<strong>" + $pair[1] + "</strong>（(\d+) 個）")).Match($indexText)
        if ($m.Success) { $stated[$pair[0]] = [int]$m.Groups[1].Value }
    }
    foreach ($k in @('readonly', 'write', 'destructive')) {
        if (-not $stated.ContainsKey($k)) {
            $cardProblems += "badge tally for '$k' is stated nowhere on the page"
        }
        elseif ($stated[$k] -ne $badge[$k]) {
            $cardProblems += "badge tally mismatch for '$k': prose says $($stated[$k]), cards carry $($badge[$k])"
        }
    }
    $badgeSum = $badge.readonly + $badge.write + $badge.destructive
    if ($badgeSum -ne $cardCount) {
        $cardProblems += "every card must carry exactly one badge: $cardCount cards but $badgeSum badges"
    }
    $catNums = @(([regex]'class="cat-header">[^<]*（(\d+) 個）').Matches($indexText) |
        ForEach-Object { [int]$_.Groups[1].Value })
    $catSum = ($catNums | Measure-Object -Sum).Sum
    if ($catNums.Count -eq 0) {
        $cardProblems += "no category headers found - the page structure changed"
    }
    elseif ($catSum -ne $cardCount) {
        $cardProblems += "category headers sum to $catSum but the page has $cardCount cards"
    }

    Write-Check "Every one of $($toolNames.Count) tools has exactly one tools-index card, and the page's own tallies agree" ($cardProblems.Count -eq 0) `
        $(if ($cardProblems.Count -gt 0) { "$($cardProblems.Count) problem(s). Regenerate the page from registerRevitTools() rather than hand-editing." } else { "" })
    if ($cardProblems.Count -gt 0) { $cardProblems | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow } }
}

Write-Host ""
# 7-15: Registered tool -> C# dispatcher case reconciliation (forward only)
Write-Host ""
Write-Host "  7-15. Registered tool -> dispatcher case (TS declares, C# must implement):" -ForegroundColor Cyan
# Why this exists: a tool the TS layer registers but the C# dispatcher cannot execute reaches
# MCP/Core/CommandExecutor.cs's `default:` and throws NotImplementedException($"未實作的命令: ...")
# at CALL time - loud to the caller, invisible to QA/QC. This class of defect has recurred
# (issue #111, again #125). This check catches it before shipping instead of at a user's desk.
#
# Direction is FORWARD ONLY (registered tool -> some dispatcher case), by design, not oversight:
# MCP/Core/CommandExecutor.cs plus every MCP/Core/Commands/*.cs partial carries far more
# `case "..."` string labels (~230+) than there are registered tools (the current registry count), because the same
# switch-on-string idiom is reused for internal, non-tool things too (e.g. filter operators like
# "equals"/"contains"/"not_equals" inside the element-query evaluator). A reverse check - "every
# case label must trace to a tool" - would be pure noise against that gap, flagging dozens of
# legitimate internal cases as if they were dead tools. So only the direction that maps to a real
# defect (declared-but-unreachable tool) is checked here.
if (-not $toolNames) {
    Write-Skip "Tool -> dispatcher reconciliation" "runtime tool registry unavailable (run npm run build)"
}
else {
    # Rename map is PARSED out of MCP-Server/src/tools/revit-tools.ts, not hardcoded, so a future
    # rename doesn't require editing this check. Today that file rewrites exactly one tool name
    # before sending it to C# - a ternary in executeRevitTool():
    #   const commandName = toolName === "query_elements_with_filter" ? "query_elements" : toolName;
    # Comparing raw tool names without applying this map would falsely flag that redirect as an
    # orphan (registered name query_elements_with_filter has no case of that name - only
    # query_elements does).
    $revitToolsTsPath = Join-Path $projectRoot "MCP-Server\src\tools\revit-tools.ts"
    $renameMap = @{}
    $revitToolsTsText = Read-FileText $revitToolsTsPath
    if ($revitToolsTsText) {
        $renameRx = [regex]'toolName\s*===\s*"([^"]+)"\s*\?\s*"([^"]+)"\s*:\s*toolName'
        foreach ($m in $renameRx.Matches($revitToolsTsText)) { $renameMap[$m.Groups[1].Value] = $m.Groups[2].Value }
    }

    # C# case labels: CommandExecutor.cs + every MCP/Core/Commands/*.cs, GLOBBED - never a hand-typed
    # file list. A hand-typed list of "the partials that carry case labels" was already wrong once
    # (issue #125's reporter named six; the actual number is eight) and a hardcoded list rots the
    # same way again the next time a partial is added or a case moves between files.
    $dispatcherFiles = @(Join-Path $projectRoot "MCP\Core\CommandExecutor.cs") +
        @(Get-ChildItem -Path "$projectRoot\MCP\Core\Commands\*.cs" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    # Case labels are matched WITHOUT a `^` line-start anchor and AFTER stripping comments - two
    # bugs found together by inspector review (loop-up S5 correction pass):
    #  1) MAJOR silent false-negative: `/* ... */` block comments (and `// ...` line comments) were
    #     never stripped before matching, so a case commented out - e.g. `/* TODO: case "x": */` -
    #     still counted as "implemented". A dispatcher case inside a comment cannot execute; the real
    #     dispatcher would still throw NotImplementedException while this check reported PASS.
    #     Comments MUST be stripped before the case-label scan, not left to chance.
    #  2) minor: a `^\s*` anchor only finds the FIRST case label on a physical line, missing any
    #     second-or-later label on lines like `case "a": case "b":`. That direction is fail-safe for
    #     a real orphan (extra unmatched labels only shrink the orphan set, never hide a genuine
    #     orphan), but it silently defeats the QUARANTINE DRIFT self-check: a quarantined tool
    #     implemented as a second label on a shared line would keep WARNing forever instead of
    #     FAILing as drifted. Matching every occurrence in the text (not anchored to line start)
    #     fixes both; the HashSet already de-duplicates.
    $blockCommentRx = [regex]'(?s)/\*.*?\*/'
    $lineCommentRx = [regex]'//[^\r\n]*'
    $caseLabelRx = [regex]'case\s+"([^"]+)"\s*:'
    $dispatcherCaseLabels = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($f in $dispatcherFiles) {
        $text = Read-FileText $f
        if (-not $text) { continue }
        # Strip block comments first (they can span lines), then line comments on what remains.
        # Block comments are replaced with a space, not removed outright, so tokens on either side
        # of a stripped comment never accidentally fuse into a new match.
        $stripped = $blockCommentRx.Replace($text, ' ')
        $stripped = $lineCommentRx.Replace($stripped, '')
        foreach ($m in $caseLabelRx.Matches($stripped)) { [void]$dispatcherCaseLabels.Add($m.Groups[1].Value) }
    }

    # Quarantine: a list of NAMED, REASONED exceptions to a still-live check - never a filter
    # applied before the check runs. Issue #111 explicitly rejected a "known unimplemented" list
    # that removes tools from consideration at registration time, because that demotes "declaration
    # and implementation disagree" from a bug to a permanent normal state. This list does the
    # opposite: the tool stays fully visible in tools/list and in $toolNames above, the mismatch
    # stays on the books, and the reason prints as a WARN on every single run.
    $knownUnimplemented = @(
        @{ Name = 'check_sanitary_fixture_requirements'; Reason = 'Registered at MCP-Server/src/tools/room-tools.ts:300. No domain/*.md defines a sanitary-fixture-count-by-building-occupancy table, and CLAUDE.md Domain Method Compliance forbids supplying that table from model knowledge - so it cannot be implemented right now. It cannot be unregistered either: that would drop the tool count by one and force a CLAUDE.md count-table edit, out of scope here. Pending: an authoritative legal source, requested from the maintainer by the issue reporter.' }
    )
    $knownUnimplementedNames = @($knownUnimplemented | ForEach-Object { $_.Name })

    $orphans = @()
    $quarantinedStillOrphan = @()
    foreach ($t in $toolNames) {
        $mappedName = if ($renameMap.ContainsKey($t)) { $renameMap[$t] } else { $t }
        if (-not $dispatcherCaseLabels.Contains($mappedName)) {
            if ($knownUnimplementedNames -contains $t) { $quarantinedStillOrphan += $t }
            else { $orphans += $t }
        }
    }

    # Drift self-check, modeled on 3-4's $pathScanSkip per-entry self-check: a quarantined tool that
    # is NO LONGER an orphan (someone implemented it) must FAIL, not silently keep WARNing. A stale
    # quarantine entry swallowing a now-working tool is exactly the rot this gate exists to prevent.
    $driftedQuarantine = @($knownUnimplemented | Where-Object { $quarantinedStillOrphan -notcontains $_.Name })

    # One combined PASS/FAIL verdict covering both failure modes this gate guards against: a real
    # (un-quarantined) orphan, or a quarantine entry that has drifted stale. Either alone must FAIL.
    $reconcileProblems = @()
    if ($orphans.Count -gt 0) { $reconcileProblems += "$($orphans.Count) registered tool(s) have no dispatcher case: $($orphans -join ', ')" }
    if ($driftedQuarantine.Count -gt 0) { $reconcileProblems += "$($driftedQuarantine.Count) `$knownUnimplemented entry(ies) now HAVE a dispatcher case - remove from the quarantine list in scripts/verify-qaqc.ps1: $(($driftedQuarantine | ForEach-Object { $_.Name }) -join ', ')" }

    Write-Check "Registered tools reconcile against the C# dispatcher ($($dispatcherCaseLabels.Count) case labels across $($dispatcherFiles.Count) dispatcher files, $($renameMap.Count) rename(s) applied, $($knownUnimplemented.Count) quarantined)" ($reconcileProblems.Count -eq 0) `
        $(if ($reconcileProblems.Count -gt 0) { $reconcileProblems -join ' | ' } else { "" })
    if ($orphans.Count -gt 0) {
        $orphans | ForEach-Object { Write-Host "    $_ : no matching case in MCP/Core/CommandExecutor.cs or MCP/Core/Commands/*.cs" -ForegroundColor Red }
    }

    foreach ($q in $knownUnimplemented) {
        if ($quarantinedStillOrphan -contains $q.Name) {
            Write-Warn "Quarantined orphan tool: $($q.Name)" $q.Reason
        }
    }
}

Write-Host ""
Write-Host "[Phase 8] Document Audience and Encoding Hygiene" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

function Test-Mojibake {
    param([string]$Text)
    if (-not $Text) { return $false }
    return ($Text -match '�|嚗|銝|蝣|摰|撠|閬|瘜|憭|蝺|頝|瑼|雿')
}

Write-Host ""
Write-Host "  8-1. Audience inventory exists:" -ForegroundColor Cyan
$inventory = Join-Path $projectRoot "docs\DOCUMENT_AUDIENCE_INVENTORY.md"
Write-Check "docs/DOCUMENT_AUDIENCE_INVENTORY.md exists" (Test-Path -LiteralPath $inventory) "Document audience inventory missing"

Write-Host ""
Write-Host "  8-2. Canonical AI docs are English-oriented and mojibake-free:" -ForegroundColor Cyan
$canonicalAiDocs = @(
    "$projectRoot\CLAUDE.md",
    "$projectRoot\.claude\commands\qaqc.md"
)
$aiDocFailures = @()
foreach ($doc in $canonicalAiDocs) {
    $text = Read-FileText $doc
    $rel = $doc.Replace("$projectRoot\", "").Replace("\", "/")
    if (-not $text) {
        $aiDocFailures += "$rel missing or unreadable"
        continue
    }
    if (Test-Mojibake $text) { $aiDocFailures += "$rel contains mojibake-risk tokens" }
}
Write-Check "Canonical AI docs pass encoding check" ($aiDocFailures.Count -eq 0) `
    $(if ($aiDocFailures.Count -gt 0) { ($aiDocFailures | Select-Object -First 10) -join "`n" } else { "" })

Write-Host ""
Write-Host "  8-3. README docs are mojibake-free:" -ForegroundColor Cyan
$readmeFailures = @()
foreach ($doc in @("$projectRoot\README.md", "$projectRoot\README.zh-TW.md")) {
    $text = Read-FileText $doc
    $rel = $doc.Replace("$projectRoot\", "").Replace("\", "/")
    if (-not $text) {
        $readmeFailures += "$rel missing or unreadable"
        continue
    }
    if (Test-Mojibake $text) { $readmeFailures += "$rel contains mojibake-risk tokens" }
}
Write-Check "README.md and README.zh-TW.md pass encoding check" ($readmeFailures.Count -eq 0) `
    $(if ($readmeFailures.Count -gt 0) { ($readmeFailures | Select-Object -First 10) -join "`n" } else { "" })

Write-Host ""
Write-Host "  8-4. AI skill migration warning scan:" -ForegroundColor Cyan
$skillMojibake = @()
Get-ChildItem -Path "$projectRoot\.claude\skills\*\SKILL.md" -ErrorAction SilentlyContinue | ForEach-Object {
    $text = Read-FileText $_.FullName
    if ($text -and (Test-Mojibake $text)) {
        $skillMojibake += $_.FullName.Replace("$projectRoot\", "").Replace("\", "/")
    }
}
if ($skillMojibake.Count -gt 0) {
    Write-Warn "Some skill docs still need English/UTF-8 migration" (($skillMojibake | Select-Object -First 10) -join "`n")
}
else {
    Write-Check "Skill docs pass mojibake warning scan" $true
}

# ─────────────────────────────────────────────
# Phase 9: MCP 2026 Compliance
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "[Phase 9] MCP 2026 Compliance" -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────────" -ForegroundColor DarkGray

# Invokes the built registry the same way Phase 7's Get-ToolCount does (node --input-type=module
# -e against build/tools/index.js). Does not rebuild — honors -SkipBuild by using whatever build/
# already exists. Returns $null if the built registry is unavailable so the caller can Skip
# instead of failing the whole phase.
function Get-ToolAnnotationReport {
    $nodeScript = "import('./MCP-Server/build/tools/index.js').then(m=>{const tools=m.registerRevitTools();const allow=new Set(['delete_element','dedup_detail_elements_in_view','curate_mep_sizes']);const missingTitle=[];const missingReadOnlyHint=[];const badDestructive=[];for(const t of tools){const title=t.title;if(typeof title!=='string'||title.trim().length===0)missingTitle.push(t.name);const ro=t.annotations?t.annotations.readOnlyHint:undefined;if(typeof ro!=='boolean')missingReadOnlyHint.push(t.name);const destructive=t.annotations?t.annotations.destructiveHint:undefined;if(destructive===true&&!allow.has(t.name))badDestructive.push(t.name);}console.log(JSON.stringify({total:tools.length,missingTitle:missingTitle,missingReadOnlyHint:missingReadOnlyHint,badDestructive:badDestructive}));}).catch(()=>process.exit(2))"
    Push-Location $projectRoot
    $result = & node --input-type=module -e $nodeScript 2>$null
    $exit = $LASTEXITCODE
    Pop-Location
    if ($exit -eq 0 -and $result) {
        try {
            return ($result | ConvertFrom-Json)
        }
        catch {
            return $null
        }
    }
    return $null
}

# MCP Apps (io.modelcontextprotocol/ui): for every tool that declares _meta.ui.resourceUri, confirm
# the ui:// resource resolves via readAppResource with the exact Apps MIME, non-empty HTML, and no
# external src/href/url() references (self-contained / CSP-safe). Returns $null if build/ is absent.
function Get-AppResourceReport {
    $nodeScript = "Promise.all([import('./MCP-Server/build/tools/index.js'),import('./MCP-Server/build/apps/register-apps.js')]).then(([tm,am])=>{const tools=tm.registerRevitTools();const ui=tools.filter(t=>t._meta&&t._meta.ui&&t._meta.ui.resourceUri);const out=[];const rx=/(?:src|href)\s*=\s*['\x22]?https?:|url\(\s*['\x22]?https?:/gi;for(const t of ui){const uri=t._meta.ui.resourceUri;let ok=false,mime='',bytes=0,ext=0;try{const r=am.readAppResource(uri);const c=r&&r.contents&&r.contents[0];if(c){mime=c.mimeType;const html=c.text||'';bytes=html.length;const scan=html.replace(/<script[\s\S]*?<\/script>/gi,'');ext=(scan.match(rx)||[]).length;ok=mime==='text/html;profile=mcp-app'&&bytes>0&&/^\s*<!doctype/i.test(html)&&ext===0;}}catch(e){mime='ERR:'+e.message;}out.push({name:t.name,uri,ok,mime,bytes,ext});}console.log(JSON.stringify({uiToolCount:ui.length,resources:out}));}).catch(()=>process.exit(2))"
    Push-Location $projectRoot
    $result = & node --input-type=module -e $nodeScript 2>$null
    $exit = $LASTEXITCODE
    Pop-Location
    if ($exit -eq 0 -and $result) {
        try {
            return ($result | ConvertFrom-Json)
        }
        catch {
            return $null
        }
    }
    return $null
}

Write-Host ""
Write-Host "  9-1. Tool annotation coverage (title / readOnlyHint / destructiveHint allow-list):" -ForegroundColor Cyan
$builtRegistry = Join-Path $projectRoot "MCP-Server\build\tools\index.js"
if (-not (Test-Path $builtRegistry)) {
    Write-Skip "Tool annotation coverage (build/tools/index.js)" "Built registry missing - run npm run build in MCP-Server first"
}
elseif (-not ($annotationReport = Get-ToolAnnotationReport)) {
    # 已 build 但無法評估 registerRevitTools() → 視為 FAIL（不可靜默略過，以免回歸漏檢）。
    Write-Check "Tool annotation coverage: built registry evaluates" $false "build/tools/index.js exists but registerRevitTools() failed to evaluate - run: node --input-type=module -e ""import('./MCP-Server/build/tools/index.js').then(m=>m.registerRevitTools())"""
}
else {
    $destructiveAllowList = @('delete_element', 'dedup_detail_elements_in_view', 'curate_mep_sizes')
    $missingTitle = @($annotationReport.missingTitle)
    $missingReadOnlyHint = @($annotationReport.missingReadOnlyHint)
    $badDestructive = @($annotationReport.badDestructive)

    Write-Check "All $($annotationReport.total) tools declare a non-empty title" ($missingTitle.Count -eq 0) `
        $(if ($missingTitle.Count -gt 0) { "Missing/empty title: $($missingTitle -join ', ')" } else { "" })

    Write-Check "All $($annotationReport.total) tools declare boolean annotations.readOnlyHint" ($missingReadOnlyHint.Count -eq 0) `
        $(if ($missingReadOnlyHint.Count -gt 0) { "Missing/non-boolean readOnlyHint: $($missingReadOnlyHint -join ', ')" } else { "" })

    Write-Check "destructiveHint=true confined to allow-list ($($destructiveAllowList -join ', '))" ($badDestructive.Count -eq 0) `
        $(if ($badDestructive.Count -gt 0) { "Unexpected destructiveHint=true on: $($badDestructive -join ', ')" } else { "" })
}

Write-Host ""
Write-Host "  9-2. MCP Apps UI resource integrity (ui:// resolves / MIME / self-contained):" -ForegroundColor Cyan
$appReport = Get-AppResourceReport
if (-not $appReport) {
    Write-Skip "MCP Apps UI resource integrity" "Runtime registry / app bundle unavailable - run npm run build in MCP-Server first"
}
elseif ([int]$appReport.uiToolCount -eq 0) {
    Write-Skip "MCP Apps UI resource integrity" "No tools declare _meta.ui.resourceUri"
}
else {
    foreach ($res in $appReport.resources) {
        $detail = if (-not $res.ok) { "mime=$($res.mime) bytes=$($res.bytes) externalRefs=$($res.ext)" } else { "" }
        Write-Check "App '$($res.name)' -> $($res.uri) self-contained $($res.mime)" ([bool]$res.ok) $detail
    }
}

# ─────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────
Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  QA/QC Summary" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  PASS : $totalPass" -ForegroundColor Green
Write-Host "  FAIL : $totalFail" -ForegroundColor $(if ($totalFail -gt 0) { "Red" } else { "Green" })
Write-Host "  WARN : $totalWarn" -ForegroundColor $(if ($totalWarn -gt 0) { "Yellow" } else { "Green" })
Write-Host "  SKIP : $totalSkip" -ForegroundColor DarkGray
Write-Host ""

if ($totalFail -gt 0) {
    Write-Host "  FAILURES:" -ForegroundColor Red
    foreach ($f in $failures) {
        Write-Host "    - $($f.Name)" -ForegroundColor Red
        if ($f.Detail) {
            Write-Host "      $($f.Detail)" -ForegroundColor DarkGray
        }
    }
    Write-Host ""
    Write-Host "  RESULT: FAILED" -ForegroundColor Red
    exit 1
}
elseif ($totalSkip -gt 0) {
    Write-Host "  RESULT: PASSED (with skipped checks)" -ForegroundColor Yellow
    exit 0
}
else {
    Write-Host "  RESULT: ALL PASSED" -ForegroundColor Green
    exit 0
}
