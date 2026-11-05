"""
ShaftChecker MCP 伺服器
讓 Claude 以自然語言呼叫 Revit 的管道間檢討（模組一：防火區劃、維修門與空間、管線分隔與間距、避開結構柱梁）。

架構：Claude 桌面版 --(MCP/stdio)--> 本程式 --(HTTP localhost:5099)--> Revit 外掛 --> 規則引擎
判定一律由 Revit 外掛中的規則引擎完成；本程式只負責轉接，AI 只負責理解問題與說明結果。
"""

import json
import os
import urllib.error
import urllib.request
from typing import Optional

try:  # mcp 1.x
    from mcp.server.fastmcp import FastMCP as _Server
except ImportError:  # mcp 2.x 將 FastMCP 改名為 MCPServer
    from mcp.server.mcpserver import MCPServer as _Server

# 注意：Revit 端監聽的是 http://localhost:PORT/，必須用 localhost（不能用 127.0.0.1）
BASE_URL = os.environ.get("SHAFTCHECKER_URL", "http://localhost:5099")
TIMEOUT = 200

INSTRUCTIONS = """
你是建築法規檢討助理，透過工具操作 Revit 檢討「管道間維修空間（模組一：淨空間）」。

【最重要的原則】
1. 合格與否只能根據工具回傳的結果回答，絕對不可以自行推測或計算判定結果。
2. 回答違規項目時，必須附上規則編號（A1…）、條文依據（reference 欄位）、實際值與門檻值。
3. 「資料不足」不等於合格：要明確告訴使用者是哪個元件、缺哪個參數（例如牆類型未填「防火時效」、門未填「遮煙性能」），以及在 Revit 中如何補上。
4. 若工具回傳 ok=false，用白話說明錯誤原因與處理方式（例如 Revit 沒開、沒有開啟模型、Revit 正開著對話框）。

【規則一覽】（分四組；reference 欄位是實際引用的條文）
防火時效及區劃（fire）
- A1 管道間邊界牆防火時效 ≥ 1 小時｜法規｜設計施工編 第79-2條
- A2 維修門防火時效 ≥ 1 小時並具遮煙性能｜法規｜設計施工編 第79-2條
- A4 維修門不得開向樓梯間或排煙室（門另一側房間依階段取得，名稱或「空間類型」參數符合即違規）｜法規｜設計施工編 第97條
- 地下層：A1、A2、A4 照常檢討，reference 會加註地下建築物條文（設計施工編 第203條，第十一章）；underground=true 的管道間要特別說明。
維修門與維修空間（maint）
- A6 管道間淨尺寸：完成面邊界最小外接矩形，短邊＝淨寬、長邊＝淨深；面積取 Room.Area｜設計參數｜設計施工編 第246條
- A7 維修門淨寬、淨高：讀「淨寬」「淨高」參數；沒有時以寬度、高度扣除門框寬度推算｜設計參數
- A8 維修門前作業淨空｜設計參數
- A9 門頂梁下淨高：梁底到門檻須 ≥ 門高 + 預留（建築平面常看不到梁）｜設計參數
管線分隔與間距（mep）
- P1 電氣類（電管、電纜架）與水類管線同一管道間時，兩群之間須有隔牆｜法規｜建築設備編 第2-1條
- P2 立管淨距：中心距扣除兩管半徑與保溫層｜設計參數
- P3 維修門正後方淨空：門後作業區內不得有管線｜設計參數
- P4 高層建築物瓦斯管道間獨立：瓦斯管與允許系統（預設給排水）以外的系統同管道間即違規｜法規｜設計施工編 第246條（原文「瓦斯管之管道間應單獨設置」）
- P5 風管穿出管道間牆處須有防火閘門｜法規｜建築設備編 第92條第4款、第94條；設計施工編 第85條
避開結構柱梁（struct）
- S1 梁柱投影侵入管道間（遮擋面積、梁底到樓板淨高、扣除後可用淨尺寸）、立管穿過梁柱｜設計參數
- S2 管線穿梁：孔徑 ≤ 梁深 1/3、孔心在梁深中央 1/3、距柱面 ≥ 2 倍梁深、孔距 ≥ 3 倍孔徑；局部撞梁或沿梁內配置多半是標高畫錯｜設計參數（結構實務，不是建築法規，以結構技師要求為準）
「法規」項目不合格即為違規；「設計參數」的門檻由設定檔決定，可依使用者要求做假設分析。
每個不合格項目可能有 suggestion（改善建議，例如「管道間可往下（−Y）280 mm 完全避開柱梁」「立管可在管道間內往右（+X）150 mm 避開」「管線下移至少 80 mm 改走梁下」），回答時要一併說明。方向以平面圖為準：+X 往右、+Y 往上。
找不到結構模型（structureModelFound=false）時 A9、S1、S2 無法檢討，要提醒使用者連結並載入結構模型。
P4 只對高層建築物檢討；回傳的 highRiseNote 說明怎麼判斷的。
內政部111年函釋：管道間內每層有連續防火樓板、且貫穿部位符合第85條者，非屬「垂直貫穿樓板之管道間」，可免適用 A1、A2（目前程式尚未自動判別，若使用者提到請說明此例外）。

【怎麼辨識管道間】
外掛不是只看房間名稱，而是多訊號評分：名稱／編號符合 +100、房間參數（備註、部門…）提到管道間 +60、
Shaft Opening 貫穿 +40、內有機電元件 最多 +40、尺寸小而窄 最多 +35、上下樓層位置相疊 最多 +20；
名稱像其他空間（廁所、辦公室、機房…）−60；面積過大則不會被判成管道間。
結果分四級：確定（名稱／參數明確，或使用者已確認）、很可能（多項線索一致，預設納入檢討，但標註為「推測」）、
疑似（有線索但需要人確認，預設不納入檢討）、非管道間。
- 每個管道間都有 detection（等級、分數、reasons 理由）。回答時，凡是「很可能」的都要說明是推測，並列出理由。
- 使用者說「找不到管道間」「是不是漏標了房間」「模型沒有命名」時：呼叫 find_shaft_candidates，
  說明疑似空間與理由，請使用者確認；使用者說「這是」「這不是」時，呼叫 confirm_shaft，再重新檢討。
  不要自行替使用者決定疑似空間是不是管道間。
- find_shaft_candidates 也會回報：名稱像管道間但房間沒有放置/沒有封閉的房間、有 Shaft Opening 卻沒有房間涵蓋的位置
  （可能漏放房間），以及尚未載入的連結模型（裡面的房間不會被掃描）。這些都要主動告訴使用者。
- 位於連結模型（source 欄位有值）的管道間可以檢討，但無法寫回參數、上色或選取。

【工具使用流程】
- 使用者第一次詢問、或問「資料齊不齊」時：先呼叫 list_shafts 盤點，主動指出未填的防火時效、遮煙性能、找不到維修門等問題。
- 檢討：呼叫 check_shafts。使用者指定樓層、管道間編號或只看某類項目時，用 levels / shafts / rules 參數縮小範圍；防火 = A1、A2、A4；維修 = A6、A7、A8、A9；管線 = P1–P5；結構 = S1、S2。
- 假設分析（「如果淨寬改成 800 會怎樣」）：呼叫 check_shafts 並帶 overrides（例如 {"minClearWidthMm": 800}），並和目前門檻的結果比較，說明多了哪些不合格。假設分析不會寫回模型。
- 要看位置：select_element。看某個管道間帶 key；看某一項問題帶該項的 refs 與管道間 key（會切換到該樓層平面、縮放並選取，連結模型元件也能選取）。
- 上色：highlight_issues 以最近一次檢討結果上色；清除用 clear_highlight。
- 報表：export_report 匯出最近一次檢討結果的 CSV，回覆檔案路徑。

【回覆格式】
先給一句總結（幾個管道間、合格幾個、違規幾個、資料不足幾個），再依四個分組列出不合格項目：管道間｜規則｜結果｜說明｜改善建議｜依據。最後提出一個合理的下一步（例如「要我把違規的標紅嗎？」）。
"""

mcp = _Server("ShaftChecker", instructions=INSTRUCTIONS)


def _call(path: str, body: Optional[dict] = None) -> dict:
    """呼叫 Revit 外掛的本機 HTTP 入口。"""
    data = json.dumps(body or {}, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        BASE_URL + path,
        data=data,
        method="POST",
        headers={"Content-Type": "application/json; charset=utf-8"},
    )
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.URLError as e:
        return {
            "ok": False,
            "error": f"無法連線到 Revit（{BASE_URL}）：{e.reason}。"
                     "請確認 Revit 已開啟且已載入 ShaftChecker 外掛；"
                     "可在 Revit「管道間檢討」頁籤按「AI 連線狀態」確認。",
        }
    except Exception as e:  # noqa: BLE001
        return {"ok": False, "error": f"呼叫 Revit 時發生錯誤：{e}"}


def _clean(d: dict) -> dict:
    return {k: v for k, v in d.items() if v not in (None, [], {})}


@mcp.tool()
def revit_status() -> dict:
    """確認 Revit 是否連線、目前開啟的模型與視圖。其他工具失敗時先用這個診斷。"""
    return _call("/status")


@mcp.tool()
def get_rules() -> dict:
    """
    取得檢討規則清單（A1–A9、P1–P5、S1–S2，分四組）：名稱、法規依據、性質（法規/設計參數）、
    目前門檻值、可做假設分析的門檻名稱，以及模型需要填寫的參數名稱。
    使用者問「依據哪條法規」「門檻是多少」「要填什麼參數」時使用。
    """
    return _call("/rules")


@mcp.tool()
def list_shafts(
    levels: Optional[list[str]] = None,
    shafts: Optional[list[str]] = None,
    include_candidates: Optional[bool] = None,
) -> dict:
    """
    盤點模型中的管道間（只讀，不判定合格與否）：樓層、編號、淨寬、淨深、面積、
    邊界牆類型與防火時效、維修門尺寸/防火時效/遮煙性能/通往的房間/門前障礙物。
    用於回答「有哪些管道間」以及檢查資料是否齊全（未填的值會是 null）。

    levels: 樓層名稱，例如 ["3F"]；省略代表全部樓層。
    shafts: 管道間編號或名稱，例如 ["PS-2"]；省略代表全部。
    include_candidates: 是否把「疑似」的空間也列入；省略時只列「確定」與「很可能」。
    每個管道間都有 detection（等級、分數、理由）。回傳的 candidateCount 是尚未納入的疑似空間數。
    """
    return _call("/shafts", _clean({"levels": levels, "shafts": shafts, "includeCandidates": include_candidates}))


@mcp.tool()
def check_shafts(
    levels: Optional[list[str]] = None,
    shafts: Optional[list[str]] = None,
    rules: Optional[list[str]] = None,
    overrides: Optional[dict[str, float]] = None,
    write_back: Optional[bool] = None,
    highlight: bool = False,
    only_failed: bool = False,
    include_candidates: Optional[bool] = None,
) -> dict:
    """
    執行管道間淨空間檢討，回傳每個管道間每條規則的結果（合格/違規/資料不足/警告）、
    說明、實際值、門檻與法規依據。這是唯一可以判定合格與否的工具。

    levels: 只檢討這些樓層，例如 ["3F"]。
    shafts: 只檢討這些管道間編號，例如 ["PS-2"]。
    rules: 只執行這些規則，例如防火 ["A1","A2","A4"]、維修 ["A6","A7","A8","A9"]、
        管線 ["P1","P2","P3","P4","P5"]、結構 ["S1","S2"]；省略代表全部。
    overrides: 假設分析用的臨時門檻（不改設定檔、預設不寫回模型），可用的鍵（長度單位 mm）：
        minFireRatingHours, minClearWidthMm, minClearDepthMm, minClearAreaM2, minDoorClearWidthMm,
        minDoorHeightMm, doorFrameDeductionMm, frontClearanceDepthMm, frontClearanceHeightMm,
        doorHeadAllowanceMm, minPipeClearMm, doorBackClearDepthMm, damperSearchMm,
        structOverlapTolMm, beamHoleMaxRatio, beamHoleEndDistFactor, beamHoleSpacingFactor
        例如 {"minClearWidthMm": 800} 或 {"minPipeClearMm": 100}。
    write_back: 是否把結果寫回 Room 參數（PS_檢討結果 等）；省略時正式檢討寫回、假設分析不寫回。
    highlight: 是否同時在目前視圖把違規元件上色。
    only_failed: 只回傳不合格的管道間（管道間很多時用來縮短回應）。
    include_candidates: 是否把「疑似」的空間也納入檢討；省略時只檢討「確定」與「很可能」。
    回傳的 candidateNote 若有值，要告訴使用者還有疑似空間尚未納入。
    """
    return _call("/check", _clean({
        "levels": levels,
        "shafts": shafts,
        "rules": rules,
        "overrides": overrides,
        "writeBack": write_back,
        "highlight": highlight,
        "onlyFailed": only_failed,
        "includeCandidates": include_candidates,
    }))


@mcp.tool()
def find_shaft_candidates(levels: Optional[list[str]] = None, include_all: bool = False) -> dict:
    """
    列出管道間的辨識結果與依據：確定、很可能、疑似（待確認），每個都有分數與理由；
    另外回報名稱像管道間但沒放置/沒封閉的房間、有 Shaft Opening 卻沒有房間涵蓋的位置、
    各資料來源（主模型與連結模型）的房間數，以及尚未載入的連結模型。
    使用者問「有哪些空間可能是管道間」「是不是漏標了」「為什麼找不到管道間」時使用。

    levels: 只看這些樓層，例如 ["3F"]。
    include_all: 另外列出分數最高的非管道間房間（others），用來檢查評分是否合理。
    """
    return _call("/detect", _clean({"levels": levels, "all": include_all or None}))


@mcp.tool()
def confirm_shaft(rooms: list[str], is_shaft: Optional[bool] = None, clear: bool = False) -> dict:
    """
    記錄使用者對「這個空間是不是管道間」的決定。只有在使用者明確表示時才呼叫，不可自行替使用者決定。
    決定存在本機設定（不改動模型）；之後要重新呼叫 check_shafts 才會反映。

    rooms: 要處理的房間，可用 find_shaft_candidates 回傳的 key、房間 roomId、編號或名稱。
    is_shaft: true = 是管道間（之後固定納入檢討）；false = 不是管道間（排除）。
    clear: true = 取消先前的決定，回到自動辨識。
    """
    body = {"rooms": rooms}
    if clear:
        body["value"] = "清除"
    elif is_shaft is not None:
        body["isShaft"] = is_shaft
    return _call("/confirm", body)


@mcp.tool()
def highlight_issues() -> dict:
    """依最近一次 check_shafts 的結果，在 Revit 目前視圖上色：紅=違規、黃=資料不足、橘=警告。需先執行檢討。"""
    return _call("/highlight")


@mcp.tool()
def clear_highlight() -> dict:
    """清除 highlight_issues 在目前視圖造成的上色。"""
    return _call("/clear")


@mcp.tool()
def select_element(
    shaft: Optional[str] = None,
    key: Optional[str] = None,
    refs: Optional[list[dict]] = None,
    element_ids: Optional[list[int]] = None,
) -> dict:
    """
    在 Revit 中選取元件並定位：自動切換到管道間所在樓層的平面圖、縮放到管道間與相關元件範圍。
    連結模型中的元件也能選取（Revit 2023 以上）。
    shaft: 管道間編號（例如 "PS-2"），會選取該管道間與其維修門。
    key: 管道間的 key（check_shafts 回傳），比編號更準確（不同樓層或連結模型可能有同編號）。
    refs: 某一個不合格項目的相關元件，直接傳 check_shafts 回傳 issues[].refs（[{"id":..,"link":..}]），
          同時帶 key 可縮放到該管道間。
    element_ids: 主模型元件 Id 清單（舊版相容）。
    回傳的 note 若有值（例如目標在視圖裁剪範圍外），要轉告使用者。
    """
    return _call("/select", _clean({"shaft": shaft, "key": key, "refs": refs, "elementIds": element_ids}))


@mcp.tool()
def export_report() -> dict:
    """把最近一次 check_shafts 的結果匯出成 CSV（Excel 可直接開啟），回傳檔案路徑。需先執行檢討。"""
    return _call("/report")


@mcp.tool()
def build_test_model() -> dict:
    """
    在 Revit 目前樓層建立 T1–T6 測試案例（會修改模型）：走廊、六個管道間、樓梯間、一根柱、維修門，
    並設定牆/門防火時效與遮煙性能。T1 合格、T2 A1 違規、T3 A2 違規、T4 A4 違規、T5 A6 違規、T6 A8 違規。
    重新執行會先刪除上一次建立的測試元件。只在使用者明確要求建立測試模型時使用。
    """
    return _call("/testmodel")


if __name__ == "__main__":
    mcp.run()
