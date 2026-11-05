# -*- coding: utf-8 -*-
"""Read-only adapter to the original C# collector and rule engine.

Call only in the shared panel's Revit ExternalEvent. No HTTP bridge, parameter
writes, transactions, or duplicated Python rule implementations are needed.
"""
import datetime
import os
import clr
from Autodesk.Revit import DB
from System import Int64, String
from System.Collections.Generic import List
from casegraph.extractor import document_id, id_value
from casegraph.element_index import snapshot
from casegraph.shaft_issue import RULES, STATUSES

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))


def load_engine(version):
    folder = os.path.join(ROOT, 'ShaftChecker-main', 'src', 'ShaftChecker.Revit', 'bin', 'Release', str(version))
    for name in ('ShaftChecker.Core', 'ShaftChecker.Revit'):
        path = os.path.join(folder, name+'.dll')
        if not os.path.isfile(path):
            raise ValueError(u'缺少 Revit {} 的 ShaftChecker。請執行 tools\\Build-ShaftChecker.ps1 -RevitVersion {}，再重新啟動 Revit。'.format(version, version))
        clr.AddReferenceToFileAndPath(path)
    from ShaftChecker.Revit.Collectors import ShaftCollector
    from ShaftChecker.Core.Config import RuleConfig
    from ShaftChecker.Core.Rules import RuleEngine
    return ShaftCollector, RuleConfig, RuleEngine


def element_id(value):
    try:
        return DB.ElementId(Int64(value))
    except Exception:
        return DB.ElementId(int(value))


def run_check(uidoc):
    doc = uidoc.Document
    collector_type, config_type, engine_type = load_engine(doc.Application.VersionNumber)
    path = os.path.join(ROOT, 'ShaftChecker-main', 'src', 'ShaftChecker.Revit', 'config', 'shaft-rules.json')
    if not os.path.isfile(path):
        raise ValueError(u'找不到 ShaftChecker 檢查設定檔：'+path)
    cfg = config_type.Load(path)
    # Honor disabled rules, but never include the separate structure group.
    rules = [r for r in RULES if cfg.IsEnabled(r)]
    if not rules:
        raise ValueError(u'設定檔未啟用這三類管道間檢查規則。')
    cfg.EnabledRules = List[String](rules)
    collector = collector_type(doc, cfg)
    shafts = collector.Collect()
    result = engine_type().Run(shafts, cfg)
    links = list(DB.FilteredElementCollector(doc).OfClass(DB.RevitLinkInstance))
    report = dict(document=document_id(doc), document_name=getattr(doc, 'Title', ''),
                  checked_at=datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                  view_name=uidoc.ActiveView.Name, rules_run=rules,
                  thresholds=dict((str(kv.Key), kv.Value) for kv in cfg.Thresholds()),
                  results=[], shaft_count=result.Shafts.Count, pass_count=0, warnings=[], stale=False)
    rep = collector.Report
    unloaded = rep.LinksTotal-rep.LinksLoaded
    if unloaded:
        report['warnings'].append(u'{} 個連結未載入，未檢查其中的模型資料。'.format(unloaded))
    candidates = sum(1 for s in rep.Spaces if str(s.Detection.Level) == 'Candidate')
    if candidates and not cfg.IncludeCandidateShafts:
        report['warnings'].append(u'{} 個疑似管道間尚未納入，請先確認辨識設定。'.format(candidates))
    if rep.Unplaced.Count or rep.OrphanOpenings.Count:
        report['warnings'].append(u'另有 {} 個未放置房間、{} 個未對應房間的管道開口，未完成檢查。'.format(
            rep.Unplaced.Count, rep.OrphanOpenings.Count))
    missing_rules = [r for r in RULES if r not in rules]
    if missing_rules:
        report['warnings'].append(u'設定檔停用的規則：'+u'、'.join(missing_rules))
    cache = {}

    def resolve(source, number):
        key = (source or '', str(number))
        if key in cache:
            return cache[key]
        link = None
        if source:
            matches = [l for l in links if l.Name == source and l.GetLinkDocument() is not None]
            if len(matches) != 1:
                return None, None
            link = matches[0]
        source_doc = link.GetLinkDocument() if link else doc
        element = source_doc.GetElement(element_id(number))
        if element is None:
            return None, None
        actual = snapshot(source_doc, element)
        ref = dict(source=source or None, document=actual['source_document'], unique_id=element.UniqueId,
                   element_id=id_value(element.Id), name=actual['name'],
                   link_unique_id=link.UniqueId if link else None)
        # The current CaseBase graph resolves host elements. Use the link instance
        # as a host anchor and preserve the actual linked element in source_refs.
        anchor = snapshot(doc, link) if link else actual
        cache[key] = (ref, anchor)
        return ref, anchor

    for sr in result.Shafts:
        shaft = sr.Shaft
        room_ref, room_anchor = resolve(shaft.SourceName, shaft.RoomId)
        for issue in sr.Issues:
            status = str(issue.Severity)
            if status == 'Pass':
                report['pass_count'] += 1
                continue
            if status not in STATUSES:
                continue
            group = 'fire' if issue.RuleId in ('A1', 'A2', 'A4') else 'mep' if issue.RuleId.startswith('P') else 'maint'
            refs, records = [], []
            unresolved = room_ref is None
            if room_ref:
                # Keep the room as location evidence, not an implicit selection.
                refs.append(room_ref)
            for item in issue.Refs:
                ref, anchor = resolve(item.Source, item.Id)
                if ref is None:
                    unresolved = True
                    refs.append(dict(source=item.Source, element_id=str(item.Id), unique_id=None, document=None, name=u'來源無法對應'))
                else:
                    if ref not in refs:
                        refs.append(ref)
                    if not any(r['node_id'] == anchor['node_id'] for r in records):
                        records.append(anchor)
            # Only findings without concrete problem elements use a room target.
            # Unresolved wall/door/pipe references must never fall back to the room.
            if issue.Refs.Count == 0 and room_anchor is not None:
                records.append(room_anchor)
            report['results'].append(dict(id=str(len(report['results'])+1), group=group, status=status,
                rule_id=issue.RuleId, rule_name=issue.RuleName, message=issue.Message, advice=issue.Suggestion or '',
                nature=u'法規' if str(issue.Nature) == 'Regulation' else u'設計參數', reference=issue.Reference,
                actual=issue.Actual, required=issue.Required, unit=issue.Unit, source=shaft.SourceName,
                shaft_label=shaft.Label, shaft_key=shaft.RoomKey, room_unique_id=room_ref['unique_id'] if room_ref else '',
                room_id=str(shaft.RoomId), room_number=shaft.Number, room_name=shaft.Name, level_name=shaft.LevelName,
                level_elevation_mm=shaft.FloorZ,
                records=records, source_refs=refs, unresolved=unresolved))
    return report


def model_data_changed(args):
    """Ignore view-only navigation transactions, not changes to scan inputs.

    ShowElements can create/update a view and raise DocumentChanged. The finding
    still describes the original check scope. Deletions and unreadable events
    remain conservative, including changes originating in linked documents.
    """
    try:
        if list(args.GetDeletedElementIds()):
            return True
        doc = args.GetDocument()
        ids = list(args.GetAddedElementIds()) + list(args.GetModifiedElementIds())
        return any(not isinstance(doc.GetElement(i), DB.View) for i in ids)
    except Exception:
        return True


def validate_report(uidoc, report):
    if report.get('stale') or document_id(uidoc.Document) != report['document']:
        raise ValueError(u'模型已變更或已切換文件，請重新執行管道間檢查。')


def validate_result(uidoc, report, row):
    validate_report(uidoc, report)
    for ref in row['source_refs']:
        source_doc = uidoc.Document
        if ref.get('source'):
            link = source_doc.GetElement(ref.get('link_unique_id') or '')
            source_doc = link.GetLinkDocument() if isinstance(link, DB.RevitLinkInstance) else None
        if source_doc is None or not ref.get('unique_id') or document_id(source_doc) != ref.get('document'):
            raise ValueError(u'來源模型或連結已變更，請重新檢查。')
        if source_doc.GetElement(ref['unique_id']) is None:
            raise ValueError(u'結果元件已刪除，請重新檢查。')
