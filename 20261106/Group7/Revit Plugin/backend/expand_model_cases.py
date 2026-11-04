"""Append 100 native-model cases atomically, retaining existing cases and history."""
import argparse
import copy
import hashlib
import html
import json
import re
import sqlite3
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path
from urllib.request import Request, urlopen

from app import database
from app.graph_api import attach_issue
from app.graph_models import Draft
from app.case_scope import type_match

ROOT = Path(__file__).resolve().parents[1]
EXPORT = ROOT / 'backend/data/case-catalog-export.json'
PLAN = ROOT / 'backend/data/case-catalog-plan.json'
RECEIPT = ROOT / 'backend/data/case-catalog-import.json'
BATCH = 'u1-u21-expansion-100-v1'

# Each topic describes a treatment scenario, not an invented finding in the RVT.
# Native observations are printed separately and never rewritten as a diagnosis.
TOPICS = {
 'Walls': [
 ('CONNECTION_ERROR', '牆端接合缺口處理', '牆端與相鄰牆體的接合出現缺口或收口不符', '在平面及剖面核對端點、定位線與接合順序；先確認哪一段為保留主體。', '按核定收口調整牆端延伸、允許接合或接合順序；再檢查依附門窗。', '牆端收口、門窗宿主及房間邊界一致。'),
 ('PARAMETER_ERROR', '牆頂與樓層約束修正', '牆頂高度或偏移與所在樓層不一致', '讀取牆底、牆頂樓層及上下偏移，在剖面量測實際牆頂。', '按核定詳圖修改該牆實例的頂部約束或高度；有附接時先核對附接對象。', '牆頂與樓板、天花收口位置符合核定詳圖。'),
 ('GEOMETRY_CLASH', '牆體重複與重疊處理', '範圍檢查發現疑似多餘牆段或實體重疊', '框選此牆附近範圍，核對階段、設計選項及宿主；只採用實體相交結果。', '已接合及同線連接依既有排除規則處理；確認為多餘副本後才移除，先重新配置依附元件。', '保留合理接合，複查範圍內沒有未處理的實體碰撞。'),
 ('PARAMETER_ERROR', '牆厚與類型選用修正', '牆體厚度或類型與該位置的核定做法不符', '比較目前類型、厚度、材料層及定位線，核對結構與建築圖。', '需要新做法時先複製類型，再替換受影響牆段；復核牆面定位及開口。', '牆厚、完成面與開口尺寸一致，其他牆段未被意外改動。'),
 ('DESIGN_CONFLICT', '牆體房間邊界缺口處理', '房間邊界未依設計封閉或被多餘牆段分隔', '在房間平面查看牆端、Room Bounding、階段及房間分隔線。', '修正邊界設定或端點；保留合理開口，不把房間與牆的重合當作碰撞。', '房間範圍及面積重新計算後符合核定配置。'),
 ('CONSTRUCTION_ISSUE', '牆面材料與收口協調', '牆面材料層與鄰近開口或完成面收口不一致', '在局部剖面核對牆層順序、面層厚度與門窗框位置。', '按核定收口調整材料層或局部詳圖；需更換類型時先確認影響實例。', '模型材料、剖面與收口詳圖使用同一做法。'),
 ('IFC_MAPPING_ERROR', '牆體交換分類修正', '匯出後牆體分類或識別資訊與專案交換要求不一致', '核對 Revit 類別、IFC 覆寫及需交換的型別資料。', '修正對應欄位後匯出包含此牆的範圍，在接收端比對分類、層次與識別碼。', '接收端可追溯到原始牆實例，材料及尺寸欄位可讀。')],
 'Doors': [
 ('SPATIAL_CLEARANCE', '門扇開向與通行干涉處理', '門扇開啟範圍遮擋通道或鄰近配置', '核對門弧、實例方向及使用空間，區分門扇寬與有效淨寬。', '比較翻轉開向、移動鄰近設備或調整門位的方案，按核定配置修改實例。', '門可開啟、通行空間及宿主洞口均符合核定配置。'),
 ('PARAMETER_ERROR', '門表與模型尺寸不一致處理', '門表、門扇及洞口採用不同尺寸定義', '讀取實際寬高，確認欄位表示門扇、門框或洞口。', '修正參數關聯及門表欄位，類型尺寸需要變更時先檢查同型實例。', '量測與門表採用一致定義，洞口與框體相配。'),
 ('CONNECTION_ERROR', '門與宿主開口接合處理', '門框或開孔未隨宿主牆正確更新', '查看原生宿主關係並核對牆厚、門位、階段及族群切孔設定。', '按核定門位修正實例或族群開孔，重新載入前保存族群版本。', '宿主開孔與門框一致，依附關係可追溯。'),
 ('DESIGN_CONFLICT', '同型門方向差異處理', '同型門的翻轉或鏡射方向與各自房間配置不符', '比較 Hand、Facing 方向和各門所在空間，不假設同型門必須同向。', '只調整沒有設計依據的實例方向，同步更新門表與定位尺寸。', '各門方向符合各自位置，合理差異有紀錄。'),
 ('IFC_MAPPING_ERROR', '門的交換參數缺漏處理', '接收端缺少門的分類、尺寸或編號', '核對族群類別、型別名稱、標記及 IFC 覆寫欄位。', '依專案交換表修正映射，重新匯出局部範圍並比對該門。', '接收端可由識別碼追溯該門，必要欄位完整。')],
 'Windows': [
 ('PARAMETER_ERROR', '窗台與窗頂標高修正', '窗台或窗頂與核定立面標高不同', '以所在樓層為共同基準核對窗台高度、窗高及窗頂位置。', '調整該窗實例標高，核對宿主牆頂與上方構件，再更新立面標註。', '平面、立面、剖面及窗表標高一致。'),
 ('CONNECTION_ERROR', '窗框與宿主洞口修正', '窗框尺寸與宿主開孔或牆厚不相配', '查看宿主牆、窗寬高與框體控制參數，檢查分扇及洞口定義。', '按核定詳圖修正洞口或框體控制參數，再檢查同型窗實例。', '洞口、框體及外牆收口相配。'),
 ('SPATIAL_CLEARANCE', '開窗操作空間干涉處理', '窗扇開啟或檢修範圍與鄰近配置干涉', '核對開啟方式及平面、立面中的操作範圍。', '協調開啟方向、窗位或鄰近配置；按核定方案更新窗及尺寸。', '窗可操作及維修，調整沒有影響其他開口。'),
 ('IFC_MAPPING_ERROR', '窗型命名與交換分類修正', 'DW 類名稱與接收端物件用途分類不一致', '以實際 Revit 類別與核定用途確認映射，不單靠名稱判定門窗。', '修正窗表欄位及 IFC 覆寫後測試匯出，逐一比對該實例。', '用途、分類及識別在兩端一致。'),
 ('CONSTRUCTION_ISSUE', '窗邊收口與分扇協調', '分扇配置或窗邊收口與核定安裝詳圖不一致', '核對窗寬高、分扇、窗台與牆面交接位置。', '按核定資料調整分扇參數或安裝詳圖，同步更新窗表。', '框體、分扇與安裝收口使用同一核定版本。')],
 'Structural Columns': [
 ('PARAMETER_ERROR', '柱上下偏移與柱長修正', '柱長或柱端標高與核定樓層關係不符', '核對基底、頂部樓層及上下偏移，在剖面查看柱端。', '按核定標高修正實例約束；有附接時先核對附接對象。', '柱端、柱長及上下樓層銜接一致。'),
 ('DESIGN_CONFLICT', '柱截面方向與定位修正', '矩形柱旋轉方向或位置與核定定位不符', '在平面核對截面長短邊、旋轉及與軸線的定位尺寸。', '經核定後修正實例方向或位置，檢查連接梁牆與相關標註。', '截面、軸線定位與核定結構圖一致。'),
 ('CONNECTION_ERROR', '柱端附接與樓層銜接處理', '柱端附接對象或樓層銜接不符', '在剖面核對柱端、附接目標及起訖樓層。', '按核定配置修正附接或約束；需改變結構配置時先取得核定。', '柱端與相接構件位置正確，未產生多餘延伸。'),
 ('GEOMETRY_CLASH', '柱與不同類別實體碰撞處理', '範圍檢查發現柱與不同類別構件實體相交', '排除房間、線及非實體；已接合或同線的組合按既有規則跳過。', '逐組定位並辨別合理連接與干涉，按核定方案調整相關實例。', '重新檢查無未處理碰撞，合理結構接合保留。')],
 'Floors': [
 ('PARAMETER_ERROR', '樓板完成面與厚度協調', '完成面標高或材料層厚度與核定做法不符', '讀取類型結構、總厚度及樓層偏移，在剖面比對相鄰門底與牆頂。', '按核定做法調整材料層或實例偏移；新做法先建立獨立類型。', '各層厚度合計、完成面與相關收口一致。')],
 'Detail Items': [
 ('SPATIAL_CLEARANCE', '操作與通行空間協調', '平面配置未預留操作、開啟或通行範圍', '核對此詳圖表示的物件尺寸，依核定產品資料在所屬平面檢查使用範圍。', '協調物件位置、朝向或鄰近配置，更新實例及定位尺寸。', '平面外形及使用範圍符合核定資料，未以二維詳圖宣稱三維碰撞。'),
 ('PARAMETER_ERROR', '詳圖尺寸與設備規格修正', '詳圖的長寬或數量定義與核定規格不一致', '逐項核對實例與類型尺寸，區分單件、整組及安裝範圍。', '修正尺寸參數或其關聯；需要其他規格時建立相應類型並更新標註。', '圖面尺寸、數量及規格使用一致定義。'),
 ('DESIGN_CONFLICT', '配置方向與鏡射修正', '詳圖的轉角、開啟或接口方向與配置不符', '在所屬視圖核對旋轉、鏡射及與入口或接口的相對位置。', '按核定配置調整個別詳圖方向及位置，同步更新標註。', '方向、接口側別與定位圖一致。'),
 ('CONSTRUCTION_ISSUE', '檢修與安裝範圍協調', '安裝或更換所需空間未在配置圖完整表達', '對照核定物件資料確認檢修面、支承、接口及搬入路徑。', '協調配置與相鄰構件，補充安裝及維修範圍至平面和詳圖。', '安裝範圍及接口與核定資料一致，相關專業已覆核。'),
 ('PARAMETER_ERROR', '同型詳圖實例差異修正', '同型詳圖存在沒有設計依據的尺寸差異', '比較同型實例的實際尺寸及所屬視圖，確認實例參數與類型參數來源。', '只修正無依據的差異，保留位置需求造成的合理尺寸變化。', '實例差異有設計依據，修改未連帶覆寫其他物件。')]
}


def api(path, payload=None):
    request = Request('http://127.0.0.1:8000/graph'+path,
        data=json.dumps(payload, ensure_ascii=False).encode('utf-8') if payload is not None else None,
        headers={'Content-Type': 'application/json'})
    with urlopen(request, timeout=30) as response:
        return json.load(response)


def prepare(export, existing):
    assert export['state'] == 'complete' and export['read_only'] and not export['errors']
    assert export['revit_version'] == '2022'
    used = {r['draft']['graph']['root'] for r in existing}
    numbers = [int(m.group(1)) for r in existing for m in [re.search(r'CASE-(\d+)', r['draft']['title'])] if m]
    records = export['elements']
    # Spread new roots across all available categories and types, prioritizing
    # elements not already represented by an earlier case.
    grouped = defaultdict(list)
    for root in records:
        p = root['properties']
        if p['category'] in TOPICS:
            grouped[(root['node_id'] in used, p['category'], p['type_id'])].append(root)
    chosen = []
    for is_used in (False, True):
        groups = [sorted(v, key=lambda n:int(n['properties']['element_id'])) for k,v in sorted(grouped.items()) if k[0] == is_used]
        while any(groups) and len(chosen) < 100:
            for group in groups:
                if group and len(chosen) < 100:
                    chosen.append(group.pop(0))
    assert len(chosen) == len({n['node_id'] for n in chosen}) == 100
    counts, prepared = Counter(), []
    for offset, root in enumerate(chosen, max(numbers, default=0)+1):
        p = root['properties']
        category = p['category']
        topic_index = counts[category] % len(TOPICS[category])
        counts[category] += 1
        issue, title, scenario, investigate, fix, check = TOPICS[category][topic_index]
        graph = copy.deepcopy(export['graphs'][root['source_element_id']])
        assert next(n for n in graph['nodes'] if n['node_id'] == graph['root']) == root
        for n in graph['nodes']:
            assert n['source_document'] == export['document'] and n['source_element_id']
            assert n['node_id'] == export['document']+':'+n['source_element_id']
        peer = next((n for n in records if n['node_id'] != root['node_id'] and type_match(root,n)), None)
        if peer:
            if peer['node_id'] not in {n['node_id'] for n in graph['nodes']}:
                graph['nodes'].append(copy.deepcopy(peer))
            graph['edges'].append(dict(source=root['node_id'], target=peer['node_id'], relationship_type='RELATED_TO',
                validation_status='unverified', properties={}, evidence='同型元件參數比較；不表示碰撞或實體連接。'))
        facts = ['元件 ID：'+p['element_id'], '族群／類型：{}／{}'.format(p['family'],p['type_name']),
                 '類別：'+category, '樓層：'+(p.get('level') or '由下列約束或所屬視圖核對'),
                 '所屬視圖：'+(p.get('owner_view_name') or '模型元件'),
                 '尺寸：'+('；'.join('{} {:g} mm'.format(k,v) for k,v in p.get('dimensions',{}).items()) or '標準尺寸欄位未提供，需核對族群參數')]
        for value in p.get('constraints',{}).values():
            display = '{:g} mm'.format(value['mm']) if 'mm' in value else value['display']
            if display:
                facts.append(value['label']+'：'+display)
        for edge in graph['edges']:
            if edge['relationship_type'] == 'HOSTED_BY':
                host = next(n for n in graph['nodes'] if n['node_id'] == edge['target'])
                facts.append('宿主：{} · ID {}'.format(host['name'],host['properties']['element_id']))
        if peer:
            facts.append('同型比較元件 ID：'+peer['properties']['element_id'])
        warnings = p.get('revit_warnings',[])
        if warnings:
            facts += ['Revit 原生警告：'+w for w in warnings]
        marker = 'model-catalog:{}/{}:{}'.format(BATCH,root['source_element_id'],topic_index)
        description = '模型讀取資料\n'+'\n'.join(facts)+'\n\n問題情境\n若'+scenario+'，可依本案例處理。'
        description += '\n\n資料狀態\n元件與上列參數來自 RVT；此問題情境及解法供處理參考，未確認該元件已發生此問題或已完成修正。'
        if p.get('view_specific'):
            description += '\n此為二維詳圖元件，定位時切換至所屬視圖；不納入實體碰撞。'
        draft = Draft.model_validate(dict(title='CASE-{:03}｜{}｜{} · ID {}'.format(offset,p['type_name'],title,p['element_id']),
            issue_type=issue, priority='Medium', description=description,
            notes=marker+'\n來源：'+export['source_path']+'\nUniqueId：'+root['source_element_id']+'\n讀取時間：'+export['exported_at'], graph=graph)).model_dump(mode='json')
        solution = '\n'.join(['處理方式', '1. 在 Revit 定位 {}（ID {}），保存修改前參數及視圖紀錄。'.format(p['type_name'],p['element_id']),
            '2. '+investigate, '3. '+fix,
            '4. '+('與同型元件 ID '+peer['properties']['element_id']+' 比較，逐一確認合理差異。' if peer else '逐一檢查受影響的依附元件及相關圖面。'),
            '5. 更新受影響的平面、立面或明細表，記錄修改前後差異。', '', '驗收條件', '□ '+check,
            '□ 保存實際修改及複查結果，由審核者確認後才更新為已驗證。', '', '目前狀態：已提出解法；模型修正及效果尚待驗證。'])
        prepared.append(dict(marker=marker, draft=draft, solution=solution, root=root, topic=title))
    return dict(batch=BATCH, document=export['document'], exported_at=export['exported_at'], source_path=export['source_path'], cases=prepared)


def apply_plan(path, plan, backup_dir):
    items = plan['cases']
    assert len(items) == len({i['marker'] for i in items}) == 100
    for item in items:
        Draft.model_validate(item['draft'])
        assert item['solution'].strip() and item['draft']['graph']['root'] == item['root']['node_id']
    backup = Path(backup_dir)/('before-100-cases-'+datetime.now().strftime('%Y%m%d-%H%M%S-%f')+'.db')
    backup.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(path) as source, sqlite3.connect(backup) as target:
        source.backup(target)
    db = sqlite3.connect(path)
    try:
        db.execute('PRAGMA foreign_keys=ON')
        db.execute('BEGIN IMMEDIATE')
        existing = {}
        for cid, payload in db.execute('SELECT c.id,r.payload FROM graph_cases c JOIN graph_revisions r ON r.case_id=c.id AND r.version=c.revision'):
            marker = json.loads(payload).get('notes','').split('\n')[0]
            if marker in existing and marker.startswith('model-catalog:'+BATCH):
                raise ValueError('Duplicate existing batch marker')
            existing[marker] = cid
        receipt = []
        for item in items:
            cid = existing.get(item['marker'])
            action = 'preserved' if cid else 'created'
            if cid is None:
                cid = db.execute('INSERT INTO graph_cases(status,solution) VALUES (?,?)',('RESOLVED',item['solution'])).lastrowid
                draft = attach_issue(Draft.model_validate(item['draft']),cid)
                db.execute('INSERT INTO graph_revisions(case_id,version,payload) VALUES (?,1,?)',(cid,draft.model_dump_json()))
                for status, solution in [('REPORTED',''),('RESOLVED',item['solution'])]:
                    db.execute('INSERT INTO graph_history(case_id,status,solution,reviewer) VALUES (?,?,?,?)',(cid,status,solution,''))
                n = item['root']
                db.execute('INSERT OR IGNORE INTO graph_elements VALUES (?,?,?)',(n['source_document'],n['source_element_id'],json.dumps(n,ensure_ascii=False)))
            receipt.append(dict(case_id=cid, action=action, element_id=item['root']['properties']['element_id'], marker=item['marker']))
        db.commit()
    except Exception:
        db.rollback()
        raise
    finally:
        db.close()
    return dict(batch=BATCH, backup=str(backup), cases=receipt, model_modified=False)


def report(plan, receipt):
    esc = lambda s:html.escape(str(s))
    cards = []
    for item, row in zip(plan['cases'],receipt['cases']):
        cards.append('<details><summary>RFI-{} · {}</summary><pre>{}</pre><pre>{}</pre></details>'.format(row['case_id'],esc(item['draft']['title']),esc(item['draft']['description']),esc(item['solution'])))
    text = '''<!doctype html><html lang="zh-Hant"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>U1–U21 · 100 個元件案例</title>
<style>body{font:16px/1.7 'Microsoft JhengHei',sans-serif;max-width:1000px;margin:24px auto;padding:18px;background:#f4f7fa;color:#16384a}details{padding:16px;background:white;border:1px solid #ccd7e0;margin:10px 0}summary{cursor:pointer;font-weight:bold}pre{font:inherit;white-space:pre-wrap}input{width:95%;padding:12px;font:inherit}</style>
<h1>100 個元件案例與解法</h1><p>全部連結至 U1–U21 模型中的實際元件。狀態為「已提出解法」，實際修正及效果待驗證。</p><p>在 Revit 讀取基準元素，開啟案例庫並選「所有」問題類型；點選案例即可高亮對應元件。</p><input aria-label="搜尋案例" placeholder="搜尋元件 ID、案例、問題或解法" oninput="for(const c of document.querySelectorAll('details'))c.hidden=!c.textContent.toLowerCase().includes(this.value.toLowerCase())">'''
    destination = ROOT/'samples/model-cases-100.html'
    destination.write_text(text+'\n'.join(cards)+'</html>',encoding='utf-8')
    return str(destination)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--apply',action='store_true')
    args = parser.parse_args()
    before = api('/cases')
    if PLAN.exists():
        plan = json.loads(PLAN.read_text(encoding='utf-8'))
    else:
        plan = prepare(json.loads(EXPORT.read_text(encoding='utf-8')),before)
        plan['export_sha256'] = hashlib.sha256(EXPORT.read_bytes()).hexdigest()
        PLAN.write_text(json.dumps(plan,ensure_ascii=False,indent=2),encoding='utf-8')
    assert plan['export_sha256'] == hashlib.sha256(EXPORT.read_bytes()).hexdigest(), 'Native export changed; review plan before import'
    if not args.apply:
        print(json.dumps(dict(prepared=len(plan['cases']), categories=Counter(i['root']['properties']['category'] for i in plan['cases'])),ensure_ascii=True))
        return
    receipt = apply_plan(database.DATABASE_PATH,plan,database.DATA_DIR/'backups')
    RECEIPT.write_text(json.dumps(receipt,ensure_ascii=False,indent=2),encoding='utf-8')
    after = api('/cases')
    byid = {r['id']:r for r in after}
    assert all(byid[r['id']] == r for r in before), 'Existing cases changed'
    assert len(after) == len(before)+sum(r['action']=='created' for r in receipt['cases'])
    for item,row in zip(plan['cases'],receipt['cases']):
        saved = byid[row['case_id']]
        if row['action'] == 'created':
            assert saved['status']=='RESOLVED' and not saved['reviewer'] and saved['solution']==item['solution']
        for issue in [item['draft']['issue_type'],'ALL']:
            results = api('/cases/related',dict(target=item['root'],issue_type=issue,limit=50))['results']
            assert row['case_id'] in {r['case_id'] for r in results}, (row['case_id'],issue)
    receipt.update(total_cases=len(after), previous_cases_preserved=len(before), verified_queries=200,
        categories=dict(Counter(i['root']['properties']['category'] for i in plan['cases'])),
        issue_types=dict(Counter(i['draft']['issue_type'] for i in plan['cases'])), report=report(plan,receipt))
    RECEIPT.write_text(json.dumps(receipt,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in receipt.items() if k!='cases'},ensure_ascii=True))


if __name__ == '__main__':
    main()
