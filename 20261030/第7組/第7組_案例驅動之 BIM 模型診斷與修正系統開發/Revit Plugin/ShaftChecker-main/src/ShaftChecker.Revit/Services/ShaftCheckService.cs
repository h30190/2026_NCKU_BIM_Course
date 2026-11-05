using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;
using ShaftChecker.Core.Recognition;
using ShaftChecker.Core.Rules;
using ShaftChecker.Revit.Bridge;
using ShaftChecker.Revit.Collectors;
using ShaftChecker.Revit.Utils;
using ShaftChecker.Revit.Writers;

namespace ShaftChecker.Revit.Services
{
    /// <summary>
    /// AI（經由 HTTP 橋接）使用的檢討服務。每個方法都在 Revit 主執行緒上被呼叫，
    /// 回傳可直接序列化成 JSON 的 Dictionary。判定一律交給 Core 的規則引擎。
    /// </summary>
    public static class ShaftCheckService
    {
        /// <summary>最近一次檢討結果：highlight / report 使用。</summary>
        private static CheckResult _last;
        private static bool _lastIsWhatIf;

        // ------------------------------------------------------------------ /status

        public static object Status(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "revitVersion", app.Application.VersionNumber },
                { "document", uidoc != null ? uidoc.Document.Title : null },
                { "activeView", uidoc != null && uidoc.Document.ActiveView != null ? uidoc.Document.ActiveView.Name : null },
                { "configPath", AddinPaths.ConfigPath }
            };
        }

        // ------------------------------------------------------------------ /rules

        public static object Rules()
        {
            RuleConfig cfg = AddinPaths.LoadConfig();
            var rules = RuleEngine.AllRules.Select(r => (object)new Dictionary<string, object>
            {
                { "id", r.Id },
                { "name", r.Name },
                { "nature", Labels.Of(r.Nature) },
                { "reference", r.Reference },
                { "group", r.Group },
                { "groupLabel", RuleGroups.Label(r.Group) },
                { "enabled", cfg.IsEnabled(r.Id) },
                { "thresholdKeys", ThresholdKeysOf(r.Id) }
            }).ToList();

            var groups = new[] { RuleGroups.Fire, RuleGroups.Maintenance, RuleGroups.Mep, RuleGroups.Structure }
                .Select(g => (object)new Dictionary<string, object>
                {
                    { "key", g },
                    { "label", RuleGroups.Label(g) },
                    { "rules", RuleEngine.AllRules.Where(r => r.Group == g).Select(r => r.Id).ToList() }
                }).ToList();

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "rules", rules },
                { "groups", groups },
                { "thresholds", cfg.Thresholds() },
                { "overridableKeys", RuleConfig.OverridableKeys },
                { "shaftNamePatterns", cfg.ShaftNamePatterns },
                { "detection", new Dictionary<string, object>
                    {
                        { "confirmedScore", cfg.DetectConfirmedScore },
                        { "likelyScore", cfg.DetectLikelyScore },
                        { "candidateScore", cfg.DetectCandidateScore },
                        { "maxShaftAreaM2", cfg.MaxShaftAreaM2 },
                        { "includeCandidates", cfg.IncludeCandidateShafts },
                        { "signals", "名稱／編號符合 +100；房間參數提到管道間 +60；Shaft Opening 貫穿 +40；內有機電元件 最多 +40；尺寸小而窄 最多 +35；上下樓層位置相疊 最多 +20；名稱像其他空間 −60；門 ≥3 扇 −15；面積過大封頂在非管道間" }
                    }
                },
                { "stairRoomPatterns", cfg.StairRoomPatterns },
                { "parameters", new Dictionary<string, object>
                    {
                        { "fireRating", cfg.FireRatingParam },
                        { "smokeSeal", cfg.SmokeSealParam },
                        { "doorClearWidth", cfg.DoorClearWidthParam }
                    }
                }
            };
        }

        private static string[] ThresholdKeysOf(string ruleId)
        {
            switch (ruleId)
            {
                case "A1": return new[] { "minFireRatingHours" };
                case "A2": return new[] { "minFireRatingHours" };
                case "A6": return new[] { "minClearWidthMm", "minClearDepthMm", "minClearAreaM2" };
                case "A7": return new[] { "minDoorClearWidthMm", "minDoorHeightMm", "doorFrameDeductionMm" };
                case "A8": return new[] { "frontClearanceDepthMm", "frontClearanceHeightMm" };
                case "A9": return new[] { "doorHeadAllowanceMm" };
                case "P2": return new[] { "minPipeClearMm" };
                case "P3": return new[] { "doorBackClearDepthMm" };
                case "P5": return new[] { "damperSearchMm" };
                case "S1": return new[] { "structOverlapTolMm", "minClearWidthMm" };
                case "S2": return new[] { "beamHoleMaxRatio", "beamHoleEndDistFactor", "beamHoleSpacingFactor" };
                default: return new string[0];
            }
        }

        // ------------------------------------------------------------------ /shafts

        public static object Shafts(UIApplication app, Dictionary<string, object> body)
        {
            Document doc = RequireDoc(app).Document;
            RuleConfig cfg = AddinPaths.LoadConfig();
            bool? includeCandidates = MiniJson.GetBool(body, "includeCandidates");
            if (includeCandidates.HasValue) cfg.IncludeCandidateShafts = includeCandidates.Value;

            var collector = new ShaftCollector(doc, cfg);
            List<ShaftInfo> shafts = Filter(collector.Collect(), body);
            DetectionReport rep = collector.Report;
            int candidates = rep.Spaces.Count(x => x.Detection.Level == DetectionLevel.Candidate);

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "count", shafts.Count },
                { "shafts", shafts.Select(ShaftToJson).ToList() },
                { "candidateCount", candidates },
                { "unplacedRoomCount", rep.Unplaced.Count },
                { "orphanOpeningCount", rep.OrphanOpenings.Count },
                { "linksLoaded", rep.LinksLoaded },
                { "linksTotal", rep.LinksTotal },
                { "hint", shafts.Count == 0
                    ? "找不到「確定」或「很可能」的管道間。辨識不只看名稱，也看備註、尺寸、機電元件、Shaft Opening 與上下樓層位置。" +
                      (candidates > 0 ? "另有 " + candidates + " 個疑似空間待確認，請呼叫 find_shaft_candidates。" : "請呼叫 find_shaft_candidates（all=true）查看所有房間的評分。")
                    : null }
            };
        }

        // ------------------------------------------------------------------ /detect

        /// <summary>
        /// 列出辨識結果：確定、很可能、疑似（待確認），以及未放置的管道間房間、沒有房間涵蓋的 Shaft Opening。
        /// all=true 時另外列出分數最高的其他房間，用來檢查評分是否合理。
        /// </summary>
        public static object Detect(UIApplication app, Dictionary<string, object> body)
        {
            Document doc = RequireDoc(app).Document;
            RuleConfig cfg = AddinPaths.LoadConfig();
            DetectionReport rep = new ShaftDetector(doc, cfg).Run();

            List<string> levels = MiniJson.GetStringList(body, "levels");
            string level = MiniJson.GetString(body, "level");
            if (!string.IsNullOrEmpty(level)) levels.Add(level);
            bool all = MiniJson.GetBool(body, "all") ?? false;

            IEnumerable<DetectedSpace> spaces = rep.Spaces;
            if (levels.Count > 0)
                spaces = spaces.Where(x => levels.Any(l => Same(x.Features.LevelName, l)));
            List<DetectedSpace> list = spaces.ToList();

            Func<DetectionLevel, List<object>> pick = lv => list
                .Where(x => x.Detection.Level == lv)
                .OrderByDescending(x => x.Detection.Score)
                .ThenBy(x => x.Features.LevelName)
                .Select(DetectedToJson)
                .ToList();

            var result = new Dictionary<string, object>
            {
                { "ok", true },
                { "counts", new Dictionary<string, object>
                    {
                        { "rooms", rep.Spaces.Count },
                        { "confirmed", rep.Spaces.Count(x => x.Detection.Level == DetectionLevel.Confirmed) },
                        { "likely", rep.Spaces.Count(x => x.Detection.Level == DetectionLevel.Likely) },
                        { "candidate", rep.Spaces.Count(x => x.Detection.Level == DetectionLevel.Candidate) },
                        { "unplacedNamedRooms", rep.Unplaced.Count },
                        { "orphanShaftOpenings", rep.OrphanOpenings.Count },
                        { "mepElementsIndexed", rep.MepIndexed },
                        { "shaftOpeningsIndexed", rep.OpeningsIndexed }
                    }
                },
                { "sources", rep.Sources.Select(x => (object)new Dictionary<string, object>
                    {
                        { "name", x.Name ?? "（主模型）" },
                        { "document", x.Doc.Title },
                        { "isLink", !x.IsHost },
                        { "rooms", rep.Spaces.Count(sp => ReferenceEquals(sp.Src, x)) }
                    }).ToList() },
                { "linksLoaded", rep.LinksLoaded },
                { "linksTotal", rep.LinksTotal },
                { "confirmed", pick(DetectionLevel.Confirmed) },
                { "likely", pick(DetectionLevel.Likely) },
                { "candidates", pick(DetectionLevel.Candidate) },
                { "unplacedRooms", rep.Unplaced.Select(u => (object)new Dictionary<string, object>
                    {
                        { "roomId", u.Id }, { "source", u.SourceName }, { "number", u.Number }, { "name", u.Name },
                        { "level", u.LevelName }, { "reason", u.Reason }
                    }).ToList() },
                { "orphanOpenings", rep.OrphanOpenings.Select(o => (object)new Dictionary<string, object>
                    {
                        { "id", o.Id }, { "source", o.SourceName },
                        { "centerXmm", Math.Round(o.CxMm) }, { "centerYmm", Math.Round(o.CyMm) },
                        { "widthMm", Math.Round(o.WidthMm) }, { "depthMm", Math.Round(o.DepthMm) },
                        { "zFromMm", Math.Round(o.Z0Mm) }, { "zToMm", Math.Round(o.Z1Mm) },
                        { "note", "有垂直開口，但任何樓層都沒有房間涵蓋這個位置，可能漏放了房間" }
                    }).ToList() },
                { "thresholds", new Dictionary<string, object>
                    {
                        { "confirmedScore", cfg.DetectConfirmedScore },
                        { "likelyScore", cfg.DetectLikelyScore },
                        { "candidateScore", cfg.DetectCandidateScore },
                        { "maxShaftAreaM2", cfg.MaxShaftAreaM2 }
                    }
                }
            };

            if (all)
            {
                result["others"] = list
                    .Where(x => x.Detection.Level == DetectionLevel.None)
                    .OrderByDescending(x => x.Detection.Score)
                    .Take(200)
                    .Select(DetectedToJson)
                    .ToList();
            }
            return result;
        }

        private static object DetectedToJson(DetectedSpace d)
        {
            SpaceFeatures f = d.Features;
            return new Dictionary<string, object>
            {
                { "roomId", f.RoomId },
                { "key", d.RoomKey },
                { "source", f.SourceName },
                { "number", f.Number },
                { "name", f.Name },
                { "level", f.LevelName },
                { "areaM2", Math.Round(f.AreaM2, 2) },
                { "clearWidthMm", Math.Round(f.ShortSideMm) },
                { "clearDepthMm", Math.Round(f.LongSideMm) },
                { "detection", DetectionJson(d.Detection) },
                { "signals", new Dictionary<string, object>
                    {
                        { "doors", f.DoorCount },
                        { "mepLinear", f.MepLinearCount },
                        { "mepVertical", f.MepVerticalCount },
                        { "mepEquipment", f.MepEquipmentCount },
                        { "shaftOpenings", f.ShaftOpeningCount },
                        { "stackedLevels", f.StackedLevels },
                        { "parameterHit", f.ParameterHit }
                    }
                }
            };
        }

        private static object DetectionJson(ShaftDetection d)
        {
            if (d == null) return null;
            string code;
            switch (d.Level)
            {
                case DetectionLevel.Confirmed: code = "confirmed"; break;
                case DetectionLevel.Likely: code = "likely"; break;
                case DetectionLevel.Candidate: code = "candidate"; break;
                default: code = "none"; break;
            }
            return new Dictionary<string, object>
            {
                { "level", d.LevelLabel },
                { "code", code },
                { "score", d.Score },
                { "byUser", d.ByUser },
                { "excludedByUser", d.ExcludedByUser },
                { "reasons", d.Reasons }
            };
        }

        // ------------------------------------------------------------------ /confirm

        /// <summary>
        /// 使用者決定「這個空間是不是管道間」。isShaft=true/false，或 value="清除" 取消決定。
        /// rooms：用鍵（/detect 回傳的 key）、房間 Id、編號或名稱指定。決定存在本機設定，不改模型。
        /// </summary>
        public static object Confirm(UIApplication app, Dictionary<string, object> body)
        {
            Document doc = RequireDoc(app).Document;
            RuleConfig cfg = AddinPaths.LoadConfig();

            bool? isShaft = MiniJson.GetBool(body, "isShaft");
            string value = MiniJson.GetString(body, "value");
            string decision;
            if (isShaft.HasValue) decision = isShaft.Value ? "是" : "否";
            else if (value == "是" || value == "否") decision = value;
            else if (value == "清除" || string.Equals(value, "clear", StringComparison.OrdinalIgnoreCase)) decision = null;
            else return Error("請指定 isShaft=true（是管道間）、false（不是），或 value=\"清除\" 取消先前的決定。");

            List<string> targets = MiniJson.GetStringList(body, "rooms");
            if (targets.Count == 0) return Error("請用 rooms 指定要處理的房間（鍵、房間 Id、編號或名稱）。");

            DetectionReport rep = new ShaftDetector(doc, cfg).Run();
            string docKey = ConfirmStore.DocKey(doc);

            var matched = rep.Spaces.Where(sp => targets.Any(t => MatchesSpace(sp, t))).ToList();
            if (matched.Count == 0) return Error("找不到符合的房間：" + string.Join("、", targets));

            foreach (DetectedSpace sp in matched)
                ConfirmStore.Set(docKey, sp.RoomKey, decision);

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "decision", decision ?? "已清除" },
                { "updated", matched.Count },
                { "rooms", matched.Select(sp => (object)new Dictionary<string, object>
                    {
                        { "number", sp.Features.Number }, { "name", sp.Features.Name },
                        { "level", sp.Features.LevelName }, { "source", sp.Features.SourceName }
                    }).ToList() },
                { "note", "決定存在本機（%AppData%\\ShaftChecker），不會改動模型。請重新執行檢討。" }
            };
        }

        private static bool MatchesSpace(DetectedSpace sp, string q)
        {
            if (string.IsNullOrEmpty(q)) return false;
            q = q.Trim();
            return sp.RoomKey == q ||
                   sp.Features.RoomId.ToString(CultureInfo.InvariantCulture) == q ||
                   string.Equals(sp.Features.Number, q, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sp.Features.Name, q, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ /check

        public static object Check(UIApplication app, Dictionary<string, object> body)
        {
            UIDocument uidoc = RequireDoc(app);
            Document doc = uidoc.Document;

            RuleConfig cfg = AddinPaths.LoadConfig().Clone();

            // 指定規則
            List<string> rules = MiniJson.GetStringList(body, "rules");
            if (rules.Count > 0) cfg.EnabledRules = rules;

            // 臨時門檻（假設分析）
            var applied = new Dictionary<string, object>();
            var ignored = new List<string>();
            object ovObj;
            if (body != null && body.TryGetValue("overrides", out ovObj) && ovObj is Dictionary<string, object>)
            {
                foreach (var kv in (Dictionary<string, object>)ovObj)
                {
                    double val = 0;
                    bool isNum = kv.Value != null && double.TryParse(
                        Convert.ToString(kv.Value, CultureInfo.InvariantCulture),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out val);
                    if (isNum && cfg.ApplyOverride(MatchKey(kv.Key), val)) applied[MatchKey(kv.Key)] = val;
                    else ignored.Add(kv.Key);
                }
            }
            bool isWhatIf = applied.Count > 0;

            bool? includeCandidates = MiniJson.GetBool(body, "includeCandidates");
            if (includeCandidates.HasValue) cfg.IncludeCandidateShafts = includeCandidates.Value;

            var collector = new ShaftCollector(doc, cfg);
            List<ShaftInfo> shafts = Filter(collector.Collect(), body);
            CheckResult result = new RuleEngine().Run(shafts, cfg);
            CheckResult hostResult = result.HostOnly(); // 連結模型唯讀：不寫回、不上色
            int candidateCount = collector.Report.Spaces.Count(x => x.Detection.Level == DetectionLevel.Candidate);
            _last = result;
            _lastIsWhatIf = isWhatIf;

            // 假設分析預設不寫回模型；正式檢討預設寫回
            bool writeBack = MiniJson.GetBool(body, "writeBack") ?? !isWhatIf;
            bool highlight = MiniJson.GetBool(body, "highlight") ?? false;
            List<string> missingParams = new List<string>();
            int colored = 0;

            if ((writeBack || highlight) && hostResult.Shafts.Count > 0)
            {
                using (var group = new TransactionGroup(doc, "管道間檢討（AI）"))
                {
                    group.Start();
                    if (writeBack)
                    {
                        using (var t = new Transaction(doc, "寫回檢討結果"))
                        {
                            t.Start();
                            missingParams = ParameterWriter.Write(doc, hostResult);
                            t.Commit();
                        }
                    }
                    if (highlight && GraphicsWriter.CanColor(doc.ActiveView))
                    {
                        using (var t = new Transaction(doc, "違規上色"))
                        {
                            t.Start();
                            colored = GraphicsWriter.Apply(doc, doc.ActiveView, hostResult);
                            t.Commit();
                        }
                    }
                    group.Assimilate();
                }
            }

            bool onlyFailed = MiniJson.GetBool(body, "onlyFailed") ?? false;

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "isWhatIf", isWhatIf },
                { "appliedOverrides", applied },
                { "ignoredOverrides", ignored },
                { "rulesRun", RuleEngine.AllRules.Where(r => cfg.IsEnabled(r.Id)).Select(r => r.Id).ToList() },
                { "thresholdsUsed", cfg.Thresholds() },
                { "summary", Summary(result) },
                { "linkedShafts", result.LinkedCount },
                { "highRise", collector.Scanner != null && collector.Scanner.IsHighRise },
                { "highRiseNote", collector.Scanner != null ? collector.Scanner.HighRiseNote : null },
                { "structureModelFound", collector.Scanner == null || collector.Scanner.StructureFound },
                { "mepModelFound", collector.Scanner == null || collector.Scanner.MepFound },
                { "groupSummary", GroupSummary(result) },
                { "candidateCount", candidateCount },
                { "candidateNote", candidateCount > 0
                    ? "另有 " + candidateCount + " 個疑似管道間尚未納入檢討，請呼叫 find_shaft_candidates 查看並確認。"
                    : null },
                { "wroteBack", writeBack && hostResult.Shafts.Count > 0 && missingParams.Count == 0 },
                { "missingParameters", missingParams },
                { "coloredElements", colored },
                { "shafts", result.Shafts
                    .Where(s => !onlyFailed || s.Overall != Severity.Pass)
                    .Select(sr => ShaftResultToJson(sr, cfg)).ToList() }
            };
        }

        private static string MatchKey(string key)
        {
            string k = (key ?? "").Trim();
            foreach (string known in RuleConfig.OverridableKeys)
                if (string.Equals(known, k, StringComparison.OrdinalIgnoreCase)) return known;
            return k;
        }

        // ------------------------------------------------------------------ /highlight /clear

        public static object Highlight(UIApplication app)
        {
            Document doc = RequireDoc(app).Document;
            if (_last == null) return Error("尚未執行檢討，請先呼叫 check_shafts。");
            if (!GraphicsWriter.CanColor(doc.ActiveView))
                return Error("目前視圖「" + doc.ActiveView.Name + "」不支援圖形覆寫，請切換到平面圖或 3D 視圖。");

            int count;
            using (var t = new Transaction(doc, "管道間檢討：違規上色"))
            {
                t.Start();
                count = GraphicsWriter.Apply(doc, doc.ActiveView, _last.HostOnly());
                t.Commit();
            }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "coloredElements", count },
                { "view", doc.ActiveView.Name },
                { "basedOnWhatIf", _lastIsWhatIf },
                { "legend", "紅=違規、黃=資料不足、橘=警告" }
            };
        }

        public static object Clear(UIApplication app)
        {
            Document doc = RequireDoc(app).Document;
            if (!GraphicsWriter.CanColor(doc.ActiveView)) return Error("目前視圖不支援圖形覆寫。");
            int count;
            using (var t = new Transaction(doc, "管道間檢討：清除上色"))
            {
                t.Start();
                count = GraphicsWriter.Clear(doc.ActiveView);
                t.Commit();
            }
            return new Dictionary<string, object> { { "ok", true }, { "restoredElements", count } };
        }

        // ------------------------------------------------------------------ /select

        /// <summary>
        /// 在 Revit 中選取並定位。
        /// body：key（管道間 RoomKey，最準確）或 shaft（編號／RoomId）；refs（[{id, link}]，link 為連結名稱，主模型省略）；
        /// elementIds（主模型元件 Id，舊版相容）。
        /// 會切換到管道間所在樓層的平面圖，縮放到管道間（與相關元件）範圍，連結模型中的元件也能選取（Revit 2023 以上）。
        /// </summary>
        public static object Select(UIApplication app, Dictionary<string, object> body)
        {
            UIDocument uidoc = RequireDoc(app);
            Document doc = uidoc.Document;
            var notes = new List<string>();

            // 1. 找出管道間：優先用最近一次檢討結果（快、而且和畫面上看到的一致）
            ShaftInfo shaft = FindShaft(doc, MiniJson.GetString(body, "key"), MiniJson.GetString(body, "shaft"));

            // 2. 要選取的元件
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            var targets = new List<Target>();

            foreach (object o in MiniJson.GetList(body, "refs"))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                long id;
                if (!long.TryParse(Convert.ToString(d.ContainsKey("id") ? d["id"] : null, CultureInfo.InvariantCulture).Split('.')[0], out id)) continue;
                string link = d.ContainsKey("link") ? d["link"] as string : null;
                AddTarget(doc, links, targets, id, string.IsNullOrEmpty(link) ? null : link);
            }
            foreach (string sid in MiniJson.GetStringList(body, "elementIds"))
            {
                long id;
                if (long.TryParse(sid.Split('.')[0], out id)) AddTarget(doc, links, targets, id, null);
            }
            // 只給管道間（按「定位」）：選取房間與維修門
            if (targets.Count == 0 && shaft != null)
            {
                AddTarget(doc, links, targets, shaft.RoomId, shaft.SourceName);
                foreach (DoorInfo dr in shaft.Doors) AddTarget(doc, links, targets, dr.Id, shaft.SourceName);
            }

            if (targets.Count == 0 && shaft == null)
                return Error("找不到要定位的管道間或元件（可能模型已變更），請重新執行檢討後再試。");

            // 3. 切換到所在樓層的平面圖：有管道間用管道間樓板標高，否則用第一個元件的底部標高
            double floorZ;
            string levelName = shaft != null ? shaft.LevelName : null;
            if (shaft != null && shaft.CeilingZ > shaft.FloorZ) floorZ = shaft.FloorZ;
            else floorZ = TargetBottomMm(targets);

            bool requested;
            string viewName;
            string viewNote = SwitchToLevelPlan(uidoc, floorZ, levelName, out requested, out viewName);
            if (viewNote != null) notes.Add(viewNote);

            if (requested)
            {
                // 視圖切換是非同步的：等 Revit 閒置（切換完成）後再選取、縮放
                ShaftInfo sh = shaft;
                List<Target> tg = targets;
                Defer(app, a =>
                {
                    UIDocument ud = a.ActiveUIDocument;
                    if (ud == null) return;
                    var late = new List<string>();
                    ApplySelection(ud, tg, late);
                    ZoomTo(ud, sh, tg, late, floorZ);
                });
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "selected", targets.Select(t => t.Id).ToList() },
                    { "selectedCount", targets.Count },
                    { "view", viewName },
                    { "zoomed", false },
                    { "note", "正在切換到「" + viewName + "」，切換完成後會自動縮放並選取" }
                };
            }

            // 4. 選取（主模型 + 連結）
            int selected = ApplySelection(uidoc, targets, notes);

            // 5. 縮放：管道間範圍 ∪ 相關元件（限制在管道間附近，避免長梁、長牆把畫面拉太遠）
            bool zoomed = ZoomTo(uidoc, shaft, targets, notes, floorZ);
            if (!zoomed)
            {
                // 新開的視圖有時要等 Revit 閒置後才能縮放
                ShaftInfo sh = shaft;
                List<Target> tg = targets;
                Defer(app, a =>
                {
                    UIDocument ud = a.ActiveUIDocument;
                    if (ud != null) ZoomTo(ud, sh, tg, new List<string>(), floorZ);
                });
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "selected", targets.Select(t => t.Id).ToList() },
                { "selectedCount", selected },
                { "view", doc.ActiveView != null ? doc.ActiveView.Name : null },
                { "zoomed", zoomed },
                { "note", notes.Count > 0 ? string.Join("；", notes) : null }
            };
        }

        // ---- 延後到 Revit 閒置時執行（視圖切換完成後再縮放、選取）----
        private static Action<UIApplication> _pending;

        private static void Defer(UIApplication app, Action<UIApplication> action)
        {
            _pending = action;
            try
            {
                app.Idling -= OnIdling;
                app.Idling += OnIdling;
            }
            catch (Exception)
            {
                _pending = null;
            }
        }

        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            var app = sender as UIApplication;
            if (app != null) app.Idling -= OnIdling;
            Action<UIApplication> action = _pending;
            _pending = null;
            if (action == null || app == null) return;
            try { action(app); } catch (Exception) { /* 使用者已關閉文件等 */ }
        }

        /// <summary>要選取的元件中最低的底部標高（主模型 mm）；沒有元件時回傳 NaN。</summary>
        private static double TargetBottomMm(List<Target> targets)
        {
            double best = double.NaN;
            foreach (Target t in targets)
            {
                BoundingBoxXYZ bb = null;
                try { bb = t.Element.get_BoundingBox(null); } catch (Exception) { }
                if (bb == null) continue;
                Transform tr = t.Link != null ? t.Link.GetTotalTransform() : Transform.Identity;
                double z = UnitConv.ToMm(tr.OfPoint(bb.Min).Z);
                if (double.IsNaN(best) || z < best) best = z;
            }
            return best;
        }

        /// <summary>一個要選取的元件：主模型元件，或連結模型中的元件（含所屬連結）。</summary>
        private class Target
        {
            public long Id;
            public Element Element;
            public RevitLinkInstance Link;
        }

        private static void AddTarget(Document doc, List<RevitLinkInstance> links, List<Target> targets, long id, string linkName)
        {
            if (targets.Any(t => t.Id == id && (t.Link == null ? linkName == null : t.Link.Name == linkName))) return;
            if (linkName == null)
            {
                Element e = doc.GetElement(ElementIdUtil.FromLong(id));
                if (e != null) targets.Add(new Target { Id = id, Element = e });
                return;
            }
            foreach (RevitLinkInstance li in links.Where(l => l.Name == linkName))
            {
                Document ld = li.GetLinkDocument();
                Element e = ld != null ? ld.GetElement(ElementIdUtil.FromLong(id)) : null;
                if (e == null) continue;
                targets.Add(new Target { Id = id, Element = e, Link = li });
                return;
            }
        }

        private static ShaftInfo FindShaft(Document doc, string key, string query)
        {
            if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(query)) return null;
            Func<IEnumerable<ShaftInfo>, ShaftInfo> pick = list =>
            {
                var arr = list.ToList();
                if (!string.IsNullOrEmpty(key))
                {
                    var byKey = arr.FirstOrDefault(s => s.RoomKey == key);
                    if (byKey != null) return byKey;
                }
                if (string.IsNullOrEmpty(query)) return null;
                return arr.FirstOrDefault(s => s.RoomId.ToString(CultureInfo.InvariantCulture) == query)
                    ?? arr.FirstOrDefault(s => string.Equals(s.Number, query, StringComparison.OrdinalIgnoreCase))
                    ?? arr.FirstOrDefault(s => MatchesShaft(s, query));
            };

            if (_last != null)
            {
                ShaftInfo hit = pick(_last.Shafts.Select(r => r.Shaft));
                if (hit != null) return hit;
            }

            // 還沒檢討過、或是疑似空間：重新收集（含疑似）
            RuleConfig cfg = AddinPaths.LoadConfig();
            cfg.IncludeCandidateShafts = true;
            return pick(new ShaftCollector(doc, cfg).Collect());
        }

        /// <summary>
        /// 切換到指定標高所在樓層的平面圖（目前視圖已經是該樓層平面就不切換）。
        /// requested = true 代表改用非同步切換（RequestViewChange），要等 Revit 閒置後才能縮放。
        /// 回傳需要告訴使用者的說明；沒有則為 null。
        /// </summary>
        private static string SwitchToLevelPlan(UIDocument uidoc, double floorZmm, string levelName,
            out bool requested, out string viewName)
        {
            requested = false;
            Document doc = uidoc.Document;
            viewName = doc.ActiveView != null ? doc.ActiveView.Name : null;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            if (levels.Count == 0) return null;

            // 樓板標高所在的樓層：標高 ≤ 樓板 + 300 mm 的最高樓層；沒有標高資料時用樓層名稱
            Level level = null;
            if (!double.IsNaN(floorZmm))
            {
                level = levels.Where(l => UnitConv.ToMm(l.ProjectElevation) <= floorZmm + 300)
                              .OrderByDescending(l => l.ProjectElevation).FirstOrDefault()
                        ?? levels.OrderBy(l => Math.Abs(UnitConv.ToMm(l.ProjectElevation) - floorZmm)).First();
            }
            if (level == null && !string.IsNullOrEmpty(levelName)) level = levels.FirstOrDefault(l => l.Name == levelName);
            if (level == null) return null;

            var active = doc.ActiveView as ViewPlan;
            if (active != null && active.ViewType == ViewType.FloorPlan && active.GenLevel != null && active.GenLevel.Id.Equals(level.Id))
                return null;

            var openIds = new HashSet<long>(uidoc.GetOpenUIViews().Select(v => v.ViewId.GetIdValue()));
            ViewPlan target = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null && v.GenLevel.Id.Equals(level.Id))
                .OrderByDescending(v => openIds.Contains(v.Id.GetIdValue()))   // 已開啟的視圖優先
                .ThenBy(v => v.CropBoxActive ? 1 : 0)                           // 沒有裁剪範圍的優先
                .ThenBy(v => v.Name.Length)
                .FirstOrDefault();
            if (target == null) return "找不到「" + level.Name + "」的平面圖，已在目前視圖定位";

            viewName = target.Name;
            try
            {
                uidoc.ActiveView = target;
                return null;
            }
            catch (Exception)
            {
                try
                {
                    uidoc.RequestViewChange(target);
                    requested = true;
                    return null;
                }
                catch (Exception)
                {
                    return "無法切換到「" + target.Name + "」，請手動開啟該平面圖後再按一次";
                }
            }
        }

        private static int ApplySelection(UIDocument uidoc, List<Target> targets, List<string> notes)
        {
            if (targets.Count == 0) return 0;
            var hostIds = targets.Where(t => t.Link == null).Select(t => t.Element.Id).ToList();
            bool hasLinked = targets.Any(t => t.Link != null);

#if REVIT2024_OR_GREATER
            if (hasLinked)
            {
                try
                {
                    var refs = new List<Reference>();
                    foreach (Target t in targets)
                    {
                        var r = new Reference(t.Element);
                        refs.Add(t.Link == null ? r : r.CreateLinkReference(t.Link));
                    }
                    uidoc.Selection.SetReferences(refs);
                    int got = uidoc.Selection.GetReferences().Count;
                    if (got < refs.Count)
                        notes.Add("連結模型中的元件沒有被選取：請開啟 Revit 右下角的「選取連結」後再點一次");
                    return got;
                }
                catch (Exception)
                {
                    notes.Add("連結模型中的元件無法選取，已改為只選取主模型元件並縮放到位置");
                }
            }
#else
            if (hasLinked) notes.Add("Revit 2023 以前無法用程式選取連結模型中的元件，已縮放到位置");
#endif
            uidoc.Selection.SetElementIds(hostIds);
            return hostIds.Count;
        }

        /// <summary>縮放目前視圖到管道間範圍（外加 1.5 m），並把相關元件納入（限制在管道間周圍 4 m 內）。</summary>
        private static bool ZoomTo(UIDocument uidoc, ShaftInfo shaft, List<Target> targets, List<string> notes, double floorZmm)
        {
            View view = uidoc.ActiveView;
            if (view == null) return false;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            double z = double.IsNaN(floorZmm) ? 0 : floorZmm;
            double limX0 = double.MinValue, limY0 = double.MinValue, limX1 = double.MaxValue, limY1 = double.MaxValue;

            if (shaft != null && shaft.BoundaryMm.Count >= 3)
            {
                double pad = 1500;
                minX = shaft.BoundaryMm.Min(p => p.X) - pad; maxX = shaft.BoundaryMm.Max(p => p.X) + pad;
                minY = shaft.BoundaryMm.Min(p => p.Y) - pad; maxY = shaft.BoundaryMm.Max(p => p.Y) + pad;
                limX0 = minX - 2500; limX1 = maxX + 2500; limY0 = minY - 2500; limY1 = maxY + 2500;
            }

            foreach (Target t in targets)
            {
                BoundingBoxXYZ bb = null;
                try { bb = t.Element.get_BoundingBox(null); } catch (Exception) { }
                if (bb == null) continue;
                Transform tr = t.Link != null ? t.Link.GetTotalTransform() : Transform.Identity;
                foreach (double x in new[] { bb.Min.X, bb.Max.X })
                    foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                    {
                        XYZ p = tr.OfPoint(new XYZ(x, y, bb.Min.Z));
                        double px = Math.Max(limX0, Math.Min(limX1, UnitConv.ToMm(p.X)));
                        double py = Math.Max(limY0, Math.Min(limY1, UnitConv.ToMm(p.Y)));
                        minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                        minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                    }
            }
            if (minX > maxX) return false;
            if (maxX - minX < 3000) { double c = (minX + maxX) / 2; minX = c - 1500; maxX = c + 1500; }
            if (maxY - minY < 3000) { double c = (minY + maxY) / 2; minY = c - 1500; maxY = c + 1500; }

            // 視圖有裁剪範圍、而管道間在範圍外時，提醒使用者
            try
            {
                if (view.CropBoxActive && view.CropBox != null)
                {
                    BoundingBoxXYZ cb = view.CropBox;
                    XYZ center = new XYZ(UnitConv.FromMm((minX + maxX) / 2), UnitConv.FromMm((minY + maxY) / 2), cb.Transform.Origin.Z);
                    XYZ local = cb.Transform.Inverse.OfPoint(center);
                    if (local.X < cb.Min.X || local.X > cb.Max.X || local.Y < cb.Min.Y || local.Y > cb.Max.Y)
                        notes.Add("目標在「" + view.Name + "」的裁剪範圍外，請關閉裁剪範圍或改用其他平面圖");
                }
            }
            catch (Exception) { }

            try
            {
                foreach (UIView uv in uidoc.GetOpenUIViews())
                {
                    if (!uv.ViewId.Equals(view.Id)) continue;
                    double fz = UnitConv.FromMm(z);
                    uv.ZoomAndCenterRectangle(
                        new XYZ(UnitConv.FromMm(minX), UnitConv.FromMm(minY), fz),
                        new XYZ(UnitConv.FromMm(maxX), UnitConv.FromMm(maxY), fz));
                    return true;
                }
            }
            catch (Exception)
            {
                // 視圖不支援縮放（例如明細表）
            }
            return false;
        }

        // ------------------------------------------------------------------ /report

        public static object Report(UIApplication app)
        {
            Document doc = RequireDoc(app).Document;
            if (_last == null) return Error("尚未執行檢討，請先呼叫 check_shafts。");
            string path = CsvExporter.Export(doc.Title, _last);
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "path", path },
                { "basedOnWhatIf", _lastIsWhatIf },
                { "summary", Summary(_last) }
            };
        }

        // ------------------------------------------------------------------ 共用

        public static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { { "ok", false }, { "error", message } };
        }

        private static UIDocument RequireDoc(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null) throw new InvalidOperationException("Revit 目前沒有開啟中的專案，請先開啟模型。");
            return uidoc;
        }

        private static List<ShaftInfo> Filter(List<ShaftInfo> shafts, Dictionary<string, object> body)
        {
            List<string> levels = MiniJson.GetStringList(body, "levels");
            string level = MiniJson.GetString(body, "level");
            if (!string.IsNullOrEmpty(level)) levels.Add(level);
            List<string> names = MiniJson.GetStringList(body, "shafts");

            IEnumerable<ShaftInfo> q = shafts;
            if (levels.Count > 0)
                q = q.Where(s => levels.Any(l => Same(s.LevelName, l)));
            if (names.Count > 0)
                q = q.Where(s => names.Any(n => MatchesShaft(s, n)));
            return q.ToList();
        }

        /// <summary>樓層比對：「3F」「3」「3樓」「Level 3」皆可對到名稱含 3F/3 的樓層。</summary>
        private static bool Same(string levelName, string query)
        {
            if (string.IsNullOrEmpty(levelName) || string.IsNullOrEmpty(query)) return false;
            string a = Normalize(levelName), b = Normalize(query);
            return a == b || a.TrimEnd('F') == b.TrimEnd('F');
        }

        private static string Normalize(string s)
        {
            return s.ToUpperInvariant().Replace(" ", "").Replace("LEVEL", "").Replace("樓", "F").Replace("層", "F")
                    .TrimStart('0');
        }

        private static bool MatchesShaft(ShaftInfo s, string q)
        {
            if (string.IsNullOrEmpty(q)) return false;
            q = q.Trim();
            return string.Equals(s.Number, q, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(s.Name, q, StringComparison.OrdinalIgnoreCase) ||
                   (s.Number ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.RoomId.ToString(CultureInfo.InvariantCulture) == q;
        }

        private static Dictionary<string, object> Summary(CheckResult r)
        {
            return new Dictionary<string, object>
            {
                { "total", r.Shafts.Count },
                { "pass", r.Shafts.Count(s => s.Overall == Severity.Pass) },
                { "violation", r.Shafts.Count(s => s.Overall == Severity.Violation) },
                { "dataMissing", r.Shafts.Count(s => s.Overall == Severity.DataMissing) },
                { "warning", r.Shafts.Count(s => s.Overall == Severity.Warning) }
            };
        }

        private static object ShaftToJson(ShaftInfo s)
        {
            s.EnsureMetrics();
            return new Dictionary<string, object>
            {
                { "roomId", s.RoomId },
                { "source", s.SourceName },
                { "key", s.RoomKey },
                { "detection", DetectionJson(s.Detection) },
                { "level", s.LevelName },
                { "number", s.Number },
                { "name", s.Name },
                { "clearWidthMm", s.ClearWidthMm },
                { "clearDepthMm", s.ClearDepthMm },
                { "areaM2", s.AreaM2 },
                { "walls", s.Walls.Select(w => (object)new Dictionary<string, object>
                    {
                        { "id", w.Id }, { "type", w.TypeName }, { "fireRating", w.FireRatingRaw }
                    }).ToList() },
                { "doors", s.Doors.Select(d => (object)new Dictionary<string, object>
                    {
                        { "id", d.Id }, { "type", d.TypeName }, { "mark", d.Mark },
                        { "widthMm", d.WidthMm }, { "heightMm", d.HeightMm }, { "clearWidthMm", d.ClearWidthMm },
                        { "fireRating", d.FireRatingRaw }, { "smokeSeal", d.SmokeSeal },
                        { "opensTo", d.OtherSideRoomName },
                        { "frontObstructions", d.FrontObstructions.Select(o => (object)new Dictionary<string, object>
                            { { "id", o.Id }, { "category", o.Category }, { "name", o.Name } }).ToList() }
                    }).ToList() }
            };
        }

        private static readonly string[] GroupKeys = { RuleGroups.Fire, RuleGroups.Maintenance, RuleGroups.Mep, RuleGroups.Structure };

        /// <summary>每個規則分組：有幾個管道間不合格（違規／資料不足／警告）。</summary>
        private static Dictionary<string, object> GroupSummary(CheckResult r)
        {
            var d = new Dictionary<string, object>();
            foreach (string g in GroupKeys)
            {
                var worst = r.Shafts.Select(sr => Worst(sr, g)).ToList();
                d[g] = new Dictionary<string, object>
                {
                    { "label", RuleGroups.Label(g) },
                    { "violation", worst.Count(w => w == Severity.Violation) },
                    { "dataMissing", worst.Count(w => w == Severity.DataMissing) },
                    { "warning", worst.Count(w => w == Severity.Warning) },
                    { "issues", r.Shafts.Sum(sr => sr.Issues.Count(i => i.Severity != Severity.Pass && RuleGroups.Of(i.RuleId) == g)) }
                };
            }
            return d;
        }

        private static Severity Worst(ShaftResult sr, string group)
        {
            var list = sr.Issues.Where(i => RuleGroups.Of(i.RuleId) == group).ToList();
            return list.Count == 0 ? Severity.Pass : list.Max(i => i.Severity);
        }

        private static object ShaftResultToJson(ShaftResult sr, RuleConfig cfg)
        {
            ShaftInfo s = sr.Shaft;
            var groups = new Dictionary<string, object>();
            foreach (string g in GroupKeys)
            {
                var list = sr.Issues.Where(i => RuleGroups.Of(i.RuleId) == g).ToList();
                groups[g] = new Dictionary<string, object>
                {
                    { "result", list.Count == 0 ? "未檢討" : Labels.Of(list.Max(i => i.Severity)) },
                    { "failed", list.Count(i => i.Severity != Severity.Pass) },
                    { "rules", list.Select(i => i.RuleId).Distinct().ToList() }
                };
            }

            object plan = null;
            try { plan = PlanJson(s, cfg); } catch (Exception) { plan = null; }

            return new Dictionary<string, object>
            {
                { "roomId", s.RoomId },
                { "source", s.SourceName },
                { "key", s.RoomKey },
                { "detection", DetectionJson(s.Detection) },
                { "level", s.LevelName },
                { "number", s.Number },
                { "name", s.Name },
                { "clearWidthMm", s.ClearWidthMm },
                { "clearDepthMm", s.ClearDepthMm },
                { "areaM2", s.AreaM2 },
                { "underground", s.IsUnderground },
                { "highRise", s.IsHighRise },
                { "overall", Labels.Of(sr.Overall) },
                { "groups", groups },
                { "plan", plan },
                { "issues", sr.Issues.Select(i => (object)new Dictionary<string, object>
                    {
                        { "rule", i.RuleId },
                        { "ruleName", i.RuleName },
                        { "group", RuleGroups.Of(i.RuleId) },
                        { "nature", Labels.Of(i.Nature) },
                        { "reference", i.Reference },
                        { "result", Labels.Of(i.Severity) },
                        { "message", i.Message },
                        { "suggestion", i.Suggestion },
                        { "actual", i.Actual },
                        { "required", i.Required },
                        { "unit", i.Unit },
                        { "elementIds", s.SourceName == null ? i.ElementIds : new List<long>() },
                        { "refs", i.Refs.Select(r => (object)new Dictionary<string, object> { { "id", r.Id }, { "link", r.Source } }).ToList() }
                    }).ToList() }
            };
        }

        // ------------------------------------------------------------------ 平面示意圖資料

        /// <summary>
        /// 給儀表板畫平面示意圖：管道間邊界、本層梁柱投影（禁區）、管線、維修門、建議移動方向。
        /// 座標以管道間重心為原點（mm），Y 向上。
        /// </summary>
        private static object PlanJson(ShaftInfo s, RuleConfig cfg)
        {
            if (s.BoundaryMm.Count < 3) return null;
            Point2D o = PolygonUtils.Centroid(s.BoundaryMm);
            StructureReport rep = StructureAnalysis.Analyze(s, cfg);
            Func<IEnumerable<Point2D>, List<object>> pts = list => list.Select(p => (object)new List<double> { R(p.X - o.X), R(p.Y - o.Y) }).ToList();

            double minX = s.BoundaryMm.Min(p => p.X) - 800, maxX = s.BoundaryMm.Max(p => p.X) + 800;
            double minY = s.BoundaryMm.Min(p => p.Y) - 800, maxY = s.BoundaryMm.Max(p => p.Y) + 800;
            Func<double, double, bool> near = (x, y) => x >= minX && x <= maxX && y >= minY && y <= maxY;

            var riserHit = new HashSet<string>(rep.Risers.Select(r => r.Mep.Id + "|" + r.Mep.Source));
            var mep = s.Mep.Where(m => ShaftSpace.InStorey(s, m.MinZ, m.MaxZ) &&
                                       (m.IsVertical ? near(m.X1, m.Y1) : near(m.X1, m.Y1) || near(m.X2, m.Y2) ||
                                        ShaftSpace.Samples(m, 8).Any(q => near(q[0], q[1]))))
                .Take(150)
                .Select(m => (object)new Dictionary<string, object>
                {
                    { "id", m.Id },
                    { "source", m.Source },
                    { "label", m.Label },
                    { "kind", m.Kind.ToString() },
                    { "group", MepLabels.Of(m.Group) },
                    { "vertical", m.IsVertical },
                    { "x", R(m.X1 - o.X) }, { "y", R(m.Y1 - o.Y) },
                    { "x2", R(m.X2 - o.X) }, { "y2", R(m.Y2 - o.Y) },
                    { "r", R(m.OuterRadiusMm) },
                    { "footprint", m.IsVertical && m.FootprintMm != null ? pts(m.FootprintMm) : null },
                    { "conflict", riserHit.Contains(m.Id + "|" + m.Source) }
                }).ToList();

            return new Dictionary<string, object>
            {
                { "boundary", pts(s.BoundaryMm) },
                { "frame", new Dictionary<string, object>
                    {
                        { "ux", R4(rep.Frame.Ux) }, { "uy", R4(rep.Frame.Uy) },
                        { "halfU", R(rep.Frame.HalfU) }, { "halfV", R(rep.Frame.HalfV) }
                    }
                },
                { "floorZ", R(s.FloorZ) },
                { "ceilingZ", R(s.CeilingZ) },
                { "shaftM2", Math.Round(rep.ShaftAreaM2, 3) },
                { "blockedM2", Math.Round(rep.BlockedAreaM2, 3) },
                { "freeM2", Math.Round(rep.FreeAreaM2, 3) },
                { "freeWidthMm", R(rep.FreeWidthMm) },
                { "freeDepthMm", R(rep.FreeDepthMm) },
                { "beams", rep.NearbyBeams.Select(b =>
                    {
                        MemberOverlap ov = rep.Members.FirstOrDefault(m => m.IsBeam && m.Id == b.Id && m.Source == b.Source);
                        return (object)new Dictionary<string, object>
                        {
                            { "id", b.Id }, { "name", b.Name }, { "source", b.Source },
                            { "poly", pts(b.Footprint()) },
                            { "bottomZ", R(b.BottomZ) }, { "topZ", R(b.TopZ) },
                            { "clearMm", ShaftSpace.HasStorey(s) ? (object)R(b.BottomZ - s.FloorZ) : null },
                            { "overlapM2", ov != null ? Math.Round(ov.OverlapAreaM2, 3) : 0.0 },
                            { "depthMm", ov != null ? R(ov.OverlapDepthMm) : 0.0 },
                            { "blocking", ov != null && ov.Blocking }
                        };
                    }).ToList() },
                { "columns", rep.NearbyColumns.Select(c =>
                    {
                        MemberOverlap ov = rep.Members.FirstOrDefault(m => !m.IsBeam && m.Id == c.Id && m.Source == c.Source);
                        return (object)new Dictionary<string, object>
                        {
                            { "id", c.Id }, { "name", c.Name }, { "source", c.Source },
                            { "poly", pts(PolygonUtils.ConvexHull(c.FootprintMm)) },
                            { "overlapM2", ov != null ? Math.Round(ov.OverlapAreaM2, 3) : 0.0 },
                            { "blocking", ov != null && ov.Blocking }
                        };
                    }).ToList() },
                { "mep", mep },
                { "doors", s.Doors.Where(d => d.HasGeometry).Select(d => (object)new Dictionary<string, object>
                    {
                        { "id", d.Id }, { "x", R(d.CenterX - o.X) }, { "y", R(d.CenterY - o.Y) },
                        { "ax", R4(d.AlongX) }, { "ay", R4(d.AlongY) }, { "ix", R4(d.IntoX) }, { "iy", R4(d.IntoY) },
                        { "w", R(d.WidthMm) }, { "t", R(d.WallThicknessMm) }
                    }).ToList() },
                { "moves", rep.ShaftMoves.Select(m => (object)new Dictionary<string, object>
                    {
                        { "dx", R4(m.Dx) }, { "dy", R4(m.Dy) }, { "dist", m.DistanceMm }, { "text", m.Text }
                    }).ToList() },
                { "riserMoves", rep.Risers.Where(r => r.Move != null).Select(r => (object)new Dictionary<string, object>
                    {
                        { "id", r.Mep.Id }, { "x", R(r.Mep.X1 - o.X) }, { "y", R(r.Mep.Y1 - o.Y) },
                        { "dx", R4(r.Move.Dx) }, { "dy", R4(r.Move.Dy) }, { "dist", r.Move.DistanceMm }, { "text", r.Move.Text }
                    }).ToList() }
            };
        }

        private static double R(double v) { return Math.Round(v, 0); }
        private static double R4(double v) { return Math.Round(v, 4); }
    }
}
