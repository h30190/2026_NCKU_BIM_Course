"""把 P-LCC / B-LCC CSV 草稿做成中文核對用 Excel。

用法：
    python -I build_review_xlsx.py <手冊 PDF 路徑> <draft 資料夾>

另外用「PDF 純文字」（和抽表格不同的方法）比對每列的數字與名稱是否真的出現在該頁，
結果放在「第二種方法比對」欄。
"""
import csv
import re
import sys
from pathlib import Path

import pymupdf
from openpyxl import Workbook
from openpyxl.comments import Comment
from openpyxl.formatting.rule import FormulaRule
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.datavalidation import DataValidation

FONT = "Microsoft JhengHei"
F_BODY = Font(name=FONT, size=10)
F_BOLD = Font(name=FONT, size=10, bold=True)
F_HEAD = Font(name=FONT, size=10, bold=True, color="FFFFFF")
F_TITLE = Font(name=FONT, size=14, bold=True)
FILL_HEAD = PatternFill("solid", fgColor="44546A")
FILL_CHECK = PatternFill("solid", fgColor="E7E6E6")
FILL_INPUT = PatternFill("solid", fgColor="FFF2CC")
FILL_BASE = PatternFill("solid", fgColor="DDEBF7")
FILL_BAD = PatternFill("solid", fgColor="F8CBAD")
THIN = Side(style="thin", color="BFBFBF")
BORDER = Border(left=THIN, right=THIN, top=THIN, bottom=THIN)
WRAP = Alignment(wrap_text=True, vertical="top")
CENTER = Alignment(horizontal="center", vertical="center", wrap_text=True)

NUM_TOKEN = re.compile(r"\d+(?:\.\d+)?")
REVIEW_OPTIONS = '"✓ 正確,✗ 有誤,? 不確定"'


# ---------------------------------------------------------------- 第二種方法：PDF 純文字比對

class PageText:
    def __init__(self, pdf_path):
        doc = pymupdf.open(pdf_path)
        self.text = {i + 1: doc[i].get_text() for i in range(doc.page_count)}

    def numbers(self, page):
        return {float(t) for t in NUM_TOKEN.findall(self.text[page])}

    def squashed(self, page):
        return re.sub(r"\s+", "", self.text[page])

    def check(self, page, values, names, carried_values=(), carried_names=()):
        """carried_*：從上一列沿用、可能印在前一頁的值（跨頁的玻璃種類、基層結構）"""
        nums, nums_prev = self.numbers(page), self.numbers(page) | self.numbers(page - 1)
        missing = [v for v in values if v not in ("", None) and abs(float(v)) not in nums]
        missing += [v for v in carried_values if v not in ("", None) and abs(float(v)) not in nums_prev]
        squashed, squashed_prev = self.squashed(page), self.squashed(page - 1) + self.squashed(page)
        missing_names = [n for n in names if n and re.sub(r"\s+", "", n) not in squashed]
        missing_names += [n for n in carried_names if n and re.sub(r"\s+", "", n) not in squashed_prev]
        parts = []
        if missing:
            parts.append("本頁文字找不到數字 " + "、".join(str(v) for v in missing))
        if missing_names:
            parts.append("名稱需人工確認（跨列斷行）")
        return "一致" if not parts else "；".join(parts)


def read_csv(path):
    with open(path, encoding="utf-8-sig") as f:
        return list(csv.DictReader(f))


def num(v):
    if v in ("", None):
        return None
    f = float(v)
    return int(f) if f.is_integer() else f


# ---------------------------------------------------------------- 共用寫表

def setup_sheet(ws, title, note, columns, freeze="C5"):
    """columns: list of (key, 中文標題, 寬度, 說明)

    freeze_panes 只能設一次：openpyxl 重設時會留下重複的 selection，Excel 會拒絕開檔。
    """
    ws["A1"] = title
    ws["A1"].font = F_TITLE
    ws["A2"] = note
    ws["A2"].font = F_BODY
    ws["A2"].alignment = Alignment(wrap_text=True, vertical="top")
    ws.merge_cells(start_row=2, start_column=1, end_row=2, end_column=len(columns))
    ws.row_dimensions[2].height = 48
    for i, (key, head, width, tip) in enumerate(columns, 1):
        c = ws.cell(row=4, column=i, value=head)
        c.font, c.fill, c.alignment, c.border = F_HEAD, FILL_HEAD, CENTER, BORDER
        if tip:
            c.comment = Comment(tip, "LEBR 核對")
        ws.column_dimensions[get_column_letter(i)].width = width
    ws.row_dimensions[4].height = 42
    ws.freeze_panes = freeze
    return {key: get_column_letter(i) for i, (key, *_rest) in enumerate(columns, 1)}


def style_row(ws, r, ncols, col, check_keys, input_keys, baseline=False):
    for i in range(1, ncols + 1):
        c = ws.cell(row=r, column=i)
        c.font, c.border, c.alignment = F_BODY, BORDER, WRAP
    for k in check_keys:
        ws[f"{col[k]}{r}"].fill = FILL_CHECK
    for k in input_keys:
        ws[f"{col[k]}{r}"].fill = FILL_INPUT
    if baseline:
        ws[f"{col['name']}{r}"].fill = FILL_BASE


def add_review_tools(ws, col, first, last):
    dv = DataValidation(type="list", formula1=REVIEW_OPTIONS, allow_blank=True)
    ws.add_data_validation(dv)
    dv.add(f"{col['review']}{first}:{col['review']}{last}")
    ws.conditional_formatting.add(
        f"{col['result']}{first}:{col['result']}{last}",
        FormulaRule(formula=[f'LEFT({col["result"]}{first},1)="⚠"'], fill=FILL_BAD))
    ws.conditional_formatting.add(
        f"{col['xcheck']}{first}:{col['xcheck']}{last}",
        FormulaRule(formula=[f'{col["xcheck"]}{first}<>"一致"'], fill=FILL_BAD))
    ws.auto_filter.ref = f"A4:{get_column_letter(ws.max_column)}{last}"


# ---------------------------------------------------------------- 附錄一

P_COLS = [
    ("id", "編號", 10, "程式給的流水號，手冊沒有"),
    ("category", "分類", 9, ""),
    ("name", "材料/工項名稱", 30, ""),
    ("unit", "單位", 6, ""),
    ("a1", "原料開採\nA1", 10, ""),
    ("a2", "原料運輸\nA2", 10, ""),
    ("a3", "產品生產\nA3", 10, ""),
    ("a4", "成品運輸\nA4", 10, ""),
    ("total", "總碳排量\nA1~A4\n(kgCO2e)", 11, ""),
    ("merged", "合併儲存格", 16, "手冊上這格橫跨好幾個階段時，數字放在第一個階段欄，這裡寫出跨了哪幾欄"),
    ("note", "手冊備註文字", 22, ""),
    ("page", "手冊頁碼\n(印刷頁)", 9, ""),
    ("calc", "驗算：\n各階段加總", 11, "公式：A1+A2+A3+A4"),
    ("result", "驗算結果", 16, "加總和總量差 0.015 以內算通過"),
    ("xcheck", "第二種方法比對", 22, "用 PDF 純文字（不靠表格結構）確認這列數字真的出現在該頁"),
    ("review", "你的核對", 11, "下拉選單：✓ 正確 / ✗ 有誤 / ? 不確定"),
    ("remark", "備註", 24, "看到什麼問題寫在這裡"),
]


def write_p_lcc(wb, rows, pages):
    ws = wb.create_sheet("附錄一 初級資材")
    col = setup_sheet(ws, "附錄一　ABRI 初級資材碳排資料庫（P-LCC, 2022）",
                      "手冊第 52～55 頁。灰底欄位是程式自動驗算（公式，會跟著你改的數字重算）；黃底欄位請你填。"
                      "「合併儲存格」：例如鋼胚（高爐）的 2.26 在手冊上是 A1～A3 三格合併，表示這三個階段合計 2.26。",
                      P_COLS)
    r = 5
    for row in rows:
        vals = {k: num(row[k]) for k in ("a1", "a2", "a3", "a4")}
        data = {"id": row["id"], "category": row["category"], "name": row["name"], "unit": row["unit"],
                **vals, "total": num(row["total_a1_a4"]),
                "merged": ("A1～A4 中 " + row["merged_span"].replace("-", "～") + " 為合併格") if row["merged_span"] else "",
                "note": row["note"], "page": int(row["source_book_page"])}
        for k, v in data.items():
            ws[f"{col[k]}{r}"] = v
        a = f"{col['a1']}{r}:{col['a4']}{r}"
        ws[f"{col['calc']}{r}"] = f'=IF(COUNT({a})=0,"",SUM({a}))'
        ws[f"{col['result']}{r}"] = (f'=IF(COUNT({a})=0,"只有總量（無法驗算）",'
                                     f'IF(ABS(SUM({a})-{col["total"]}{r})<=0.015,"✓ 通過","⚠ 加總≠總量"))')
        ws[f"{col['xcheck']}{r}"] = pages.check(int(row["source_pdf_page"]),
                                                [row[k] for k in ("a1", "a2", "a3", "a4", "total_a1_a4")],
                                                [row["name"]])
        style_row(ws, r, len(P_COLS), col, ("calc", "result", "xcheck"), ("review", "remark"))
        r += 1
    add_review_tools(ws, col, 5, r - 1)


# ---------------------------------------------------------------- 附錄二

B_TABLES = {
    "2-1": dict(sheet="附表2-1 外牆外裝", title="附表 2-1　一般外牆外裝新建碳排 Fowj、更新碳排 Fowj*、減碳量 ΔFowj（kgCO2e/㎡）",
                a="基層碳排 a", a_txt="基層構造", b="表層碳排 b", b_txt="表層構造", F="新建碳排\nFowj = a+b",
                Fr="更新碳排\nFowj* = b×c", dF="減碳量\nΔFowj", wear=False, sub=None,
                note="基準構件：RC 外牆貼磁磚。ΔF = 本構件(F+F*) − 基準構件(F+F*)，負數代表比基準低碳。"),
    "2-2": dict(sheet="附表2-2 外窗", title="附表 2-2　外窗新建碳排 Fwj、更新碳排 Fwj*、減碳量 ΔFwj（kgCO2e/㎡）",
                a="玻璃碳排 a", a_txt=None, b="窗框碳排 b", b_txt="窗框構造", F="新建碳排\nFwj = a+b",
                Fr="更新碳排\nFwj* = c×(a+b)", dF="減碳量\nΔFwj", wear=False, sub="玻璃種類",
                note="每種玻璃各自以「鋁框」為基準比較。"),
    "2-3": dict(sheet="附表2-3 透光帷幕", title="附表 2-3　透光帷幕外窗新建碳排 Fwj、更新碳排 Fwj*、減碳量 ΔFwj（kgCO2e/㎡）",
                a="玻璃碳排 a", a_txt=None, b="窗框碳排 b", b_txt="窗框構造", F="新建碳排\nFwj = a+b",
                Fr="更新碳排\nFwj* = c×(a+b)", dF="減碳量\nΔFwj", wear=False, sub="玻璃種類",
                note="每種玻璃各自以「帷幕牆式鋁框或塑鋼框」為基準比較。"),
    "2-4": dict(sheet="附表2-4 不透光帷幕", title="附表 2-4　不透光帷幕外牆及一般外牆新建碳排 Fcwj、更新碳排 Fcwj*、減碳量 ΔFcwj（kgCO2e/㎡）",
                a=None, a_txt=None, b=None, b_txt=None, F="新建碳排\nFcwj", Fr="更新碳排\nFcwj* = Fcwj×c",
                dF="減碳量\nΔFcwj", wear=False, sub=None,
                note="基準構件：傳統 15cm RC 外牆。手冊這張表只有新建碳排一個數字，沒有分基層/表層。"),
    "2-5": dict(sheet="附表2-5 內隔間", title="附表 2-5　內隔間新建碳排 Fiwj、更新碳排 Fiwj*、減碳量 ΔFiwj（kgCO2e/㎡）",
                a=None, a_txt=None, b=None, b_txt=None, F="新建碳排\nFiwj", Fr="更新碳排\nFiwj* = Fiwj×c",
                dF="減碳量\nΔFiwj", wear=True, sub=None,
                note="分高／中／低耗損三種建築類別（更新次數不同），各自以磚牆雙面粉刷為基準。"),
    "2-6": dict(sheet="附表2-6 室內地坪", title="附表 2-6　室內地坪新建碳排 Ffj、更新碳排 Ffj*、減碳量 ΔFfj（kgCO2e/㎡）",
                a="基層碳排 a", a_txt="基層(手冊原文)", b="表層碳排 b", b_txt="表層(手冊原文)", F="新建碳排\nFfj = a+b",
                Fr="更新碳排\nFfj* = a×基層c + b×表層c", dF="減碳量\nΔFfj", wear=True, sub="地坪種類",
                note="分建築類別 × 一般/木作地坪，各自有基準。⚠ 這張表的「更新次數」在 PDF 裡跨列斷行，程式是依建築類別填入"
                     "（高耗損 基層1/表層3、中耗損 0/2、低耗損與46-6 0/0.5），請特別核對這兩欄。"),
    "2-7": dict(sheet="附表2-7 戶外地坪", title="附表 2-7　戶外地坪新建碳排 Fpj、更新碳排 Fpj*、減碳量 ΔFpj（kgCO2e/㎡）",
                a="基層結構碳排 a", a_txt=None, b="表層碳排 b", b_txt=None, F="新建碳排\nFpj = a+b",
                Fr="更新碳排\nFpj* = b×c", dF="減碳量\nΔFpj", wear=True, sub="基層結構",
                note="分建築類別 × RC 基礎/碎石基礎，各自有基準。基層 60 年不更新，只算表層更新。"),
}


def b_columns(t):
    cols = [("code", "編號", 15, "程式提的編號草案，將來填在 Revit 類型參數 LEBR_Code")]
    if t["wear"]:
        cols.append(("wear", "建築類別", 10, "高耗損：商場旅館餐廳醫療等；中耗損：出租辦公、學校、廳舍；低耗損：住宅、自用辦公"))
    if t["sub"]:
        cols.append(("sub", t["sub"], 18, ""))
    cols.append(("name", "構造名稱", 34, "藍底 = 該組的比較基準構件"))
    if t["a_txt"]:
        cols.append(("a_txt", t["a_txt"], 18, ""))
    if t["a"]:
        cols.append(("a", t["a"], 10, ""))
    if t["b_txt"]:
        cols.append(("b_txt", t["b_txt"], 22, ""))
    if t["b"]:
        cols.append(("b", t["b"], 10, ""))
    cols += [("F", t["F"], 11, ""),
             ("cb", "基層\n更新次數", 8, "手冊未分基層/表層的表，這兩欄填同一個數字"),
             ("cs", "表層\n更新次數 c", 8, ""),
             ("Fr", t["Fr"], 12, ""),
             ("dF", t["dF"], 10, "負數 = 比基準低碳"),
             ("base", "比較基準\n編號", 14, ""),
             ("page", "手冊頁碼\n(印刷頁)", 9, ""),
             ("vF", "驗算：\n新建", 9, ""),
             ("vFr", "驗算：\n更新", 9, ""),
             ("vdF", "驗算：\n減碳量", 9, "本構件(F+F*) − 基準(F+F*)"),
             ("result", "驗算結果", 16, "三項驗算和手冊值差 0.02 以內算通過"),
             ("xcheck", "第二種方法比對", 22, "用 PDF 純文字確認這列數字真的出現在該頁"),
             ("review", "你的核對", 11, "下拉選單：✓ 正確 / ✗ 有誤 / ? 不確定"),
             ("remark", "備註", 24, "")]
    return cols


def write_b_table(wb, tno, rows, pages):
    t = B_TABLES[tno]
    cols = b_columns(t)
    ws = wb.create_sheet(t["sheet"])
    col = setup_sheet(ws, t["title"], t["note"] + "　灰底 = 程式驗算（公式）；黃底 = 請你填。", cols)
    first = 5
    last = first + len(rows) - 1
    code_rng = f"${col['code']}${first}:${col['code']}${last}"
    for i, row in enumerate(rows):
        r = first + i
        a, b = num(row["base_a"]) or 0, num(row["surface_b"]) or 0
        data = {"code": row["code"], "wear": row["wear_class"], "sub": row["subgroup"], "name": row["name"],
                "a_txt": row["a_text"], "a": a, "b_txt": row["b_text"], "b": b,
                "F": num(row["F_new"]), "cb": num(row["renew_base_c"]), "cs": num(row["renew_surface_c"]),
                "Fr": num(row["F_renew"]), "dF": num(row["delta_F"]), "base": row["baseline_code"],
                "page": int(row["source_book_page"])}
        for k, v in data.items():
            if k in col:
                ws[f"{col[k]}{r}"] = v
        A = f"{col['a']}{r}" if "a" in col else f"{col['F']}{r}"  # 2-4/2-5 沒有 a、b，直接用 F
        B = f"{col['b']}{r}" if "b" in col else "0"
        F, Fr, dF = (f"{col[k]}{r}" for k in ("F", "Fr", "dF"))
        ws[f"{col['vF']}{r}"] = f"={A}+{B}"
        ws[f"{col['vFr']}{r}"] = f"={A}*{col['cb']}{r}+{B}*{col['cs']}{r}"
        m = f"MATCH({col['base']}{r},{code_rng},0)"
        F_rng = f"${col['F']}${first}:${col['F']}${last}"
        Fr_rng = f"${col['Fr']}${first}:${col['Fr']}${last}"
        ws[f"{col['vdF']}{r}"] = f"=IFERROR(({F}+{Fr})-(INDEX({F_rng},{m})+INDEX({Fr_rng},{m})),\"無基準\")"
        ws[f"{col['result']}{r}"] = (
            f'=IF(ISNUMBER({col["vdF"]}{r}),IF(AND(ABS({col["vF"]}{r}-{F})<=0.02,ABS({col["vFr"]}{r}-{Fr})<=0.02,'
            f'ABS({col["vdF"]}{r}-{dF})<=0.02),"✓ 通過","⚠ "&IF(ABS({col["vF"]}{r}-{F})>0.02,"新建不符 ","")'
            f'&IF(ABS({col["vFr"]}{r}-{Fr})>0.02,"更新不符 ","")&IF(ABS({col["vdF"]}{r}-{dF})>0.02,"減碳量不符","")),'
            f'"⚠ 找不到基準")')
        values = [row["F_new"], row["F_renew"], row["delta_F"]]
        carried_values, carried_names = [], []
        if tno not in ("2-4", "2-5"):
            values += [row["surface_b"]]
        if tno in ("2-2", "2-3", "2-7"):
            carried_values += [row["base_a"]]
        elif row["a_text"] and "+" not in row["a_text"]:
            values += [row["base_a"]]
        if tno in ("2-2", "2-3"):
            names, carried_names = [row["b_text"]], [row["subgroup"]]
        else:
            names = [row["name"]]
        ws[f"{col['xcheck']}{r}"] = pages.check(int(row["source_pdf_page"]), values, names,
                                                carried_values, carried_names)
        style_row(ws, r, len(cols), col, ("vF", "vFr", "vdF", "result", "xcheck"), ("review", "remark"),
                  baseline=row["is_baseline"] == "Y")
    add_review_tools(ws, col, first, last)


# ---------------------------------------------------------------- 說明頁與待確認頁

GUIDE = [
    ("這份檔案是什麼", None),
    ("把《2025 低碳（低蘊含碳）建築評估手冊》附錄一、附錄二的表格，用程式從 PDF 抽出來，"
     "整理成將來 Revit 外掛要讀的資料庫。外掛只會讀數字，所以每個數字都必須和手冊一致。", None),
    ("", None),
    ("程式已經幫你檢查過什麼", None),
    ("① 驗算：手冊的數字之間有固定關係（例如 新建 = 基層 + 表層、減碳量 = 本構件 − 基準）。"
     "只要有一個數字抽錯，驗算通常就會對不上。「驗算結果」欄是 Excel 公式，你改數字它會自動重算。", None),
    ("② 第二種方法比對：另外用 PDF 純文字（不看表格格線）確認每列的數字確實出現在那一頁。", None),
    ("③ 被標成 ⚠ 的列，已經把 PDF 轉成圖片人工看過，抽取正確，是手冊本身印的數字自相矛盾，見「⚠ 需確認」分頁。", None),
    ("", None),
    ("程式檢查不到、需要你看的地方（依重要性）", None),
    ("1. 名稱和單位：驗算只檢查數字，名稱抄錯、單位錯（kg / ㎡ / m3 / 塊）程式抓不到。", None),
    ("2. 數字放在哪一欄：例如 A1 和 A2 合併格的數字，是不是真的放在對的階段。請對照「合併儲存格」欄。", None),
    ("3.「驗算結果」寫「只有總量（無法驗算）」的列：這些沒有其他數字可以互相印證，要逐筆對照手冊。", None),
    ("4. 附表 2-6 的更新次數：程式依建築類別填入，不是直接從 PDF 抽的。", None),
    ("5. 其餘「✓ 通過」的列：抽查即可，例如每頁看 3～5 列。", None),
    ("", None),
    ("怎麼填", None),
    ("每一列最右邊的「你的核對」（黃底）點一下會出現下拉選單：✓ 正確 / ✗ 有誤 / ? 不確定。"
     "有問題就在「備註」寫手冊上看到的正確內容。範例：你的核對 =「✗ 有誤」，備註 =「手冊 p.53 單位是 ㎡ 不是 kg」。", None),
    ("用表頭的篩選按鈕，可以只看 ⚠ 的列或還沒核對的列。", None),
    ("", None),
    ("顏色說明", None),
    ("灰底：程式自動驗算　｜　黃底：請你填　｜　藍底：該組的比較基準構件　｜　紅底：驗算不符或比對有疑問", None),
    ("", None),
    ("名詞", None),
    ("新建碳排 F：蓋的時候這個構件每平方公尺的碳排（含材料製造、運輸）。", None),
    ("更新碳排 F*：建築 60 年使用期間，這個構件要翻修幾次 × 每次的碳排。", None),
    ("減碳量 ΔF：和同類的基準構件比，(F + F*) 少了多少；負數代表比較低碳。", None),
    ("建築類別：高／中／低耗損，決定更新次數（表 3-1）。住宅屬低耗損、學校屬中耗損、商場屬高耗損。", None),
    ("編號：程式提的草案（例如 F-H-G01 = 室內地坪、高耗損、一般地坪第 1 項），團隊可以改。", None),
]

ISSUES = [
    ("附錄一", "石膏", "53", "A1～A3 合併 0.035、A4 0.0038，總量印 0.35", "加總約 0.039，總量看起來小數點錯一位", "採 0.039？或查原始資料"),
    ("附錄一", "門窗鋁框（㎡）", "52", "A1～A3 合併 14.4、A4 0.0055，總量印 14.38", "加總 14.4055，差 0.025，小數尾差", "影響很小，採手冊總量 14.38 即可"),
    ("附錄一", "PE 防水布", "55", "A1～A3 0.252、A4 0，總量印 0.042", "加總 0.252 ≠ 0.042，不知道哪個才對", "需查原始資料"),
    ("附表 2-1", "5. 金屬模板搗灌 RC 外牆 塗料外裝、6. 預鑄乾式 RC 外牆 塗料外裝", "56", "減碳量印 -25.51",
     "驗算 -25.454，差 0.056，小數尾差（3. RC 外牆塗料外裝也差 0.016）", "影響很小，採手冊值即可"),
    ("附表 2-3", "12mm 普通或吸熱玻璃 / 帷幕鋁框", "58", "a 24.6 + b 30.32，新建碳排印 55.92", "應為 54.92（同表其他玻璃都符合 a+b）", "採 54.92"),
    ("附表 2-3", "12mm 普通或吸熱玻璃 / 不銹鋼框", "58", "減碳量印 +16.31", "因為上一列印錯，驗算變 15.31；同表其他列都是 +16.31，本列手冊值應是對的", "維持 +16.31，修正上一列即可"),
    ("附表 2-6", "中耗損 木作地坪 6. 塑膠地板/方塊地毯", "61", "表層 5.29 × 表層更新 2 次，更新碳排印 15.87", "應為 10.58；15.87 = 5.29×3，像是誤用高耗損的次數", "採 10.58，減碳量也要跟著改成 -44.82"),
    ("附表 2-6", "46-6 分戶樓板 一般地坪 1. 緩衝材水泥砂漿+貼地磚", "62", "手冊沒寫「基準值」", "這列底色是灰的而且減碳量 0，推定它是基準", "確認推定正確"),
    ("附表 2-6", "46-6 一般地坪 2. 鋼絲網補強混凝土+貼地磚", "62", "減碳量印 +4.63", "驗算 +6.18，差 1.55，原因不明", "需查原始資料"),
    ("附表 2-6", "46-6 一般地坪 3. 鋼絲網補強混凝土+塑膠地磚", "62", "減碳量印 -16.91", "驗算 -15.36，同樣差 1.55", "需查原始資料"),
]


def write_guide(wb):
    ws = wb.active
    ws.title = "說明（先看這頁）"
    ws["A1"] = "LEBR 資料庫核對表（草稿）"
    ws["A1"].font = F_TITLE
    ws.column_dimensions["A"].width = 110
    for i, (text, _) in enumerate(GUIDE, 3):
        c = ws.cell(row=i, column=1, value=text)
        is_head = text and not text[0].isdigit() and len(text) < 20 and text[0] not in "①②③灰"
        c.font = Font(name=FONT, size=12 if is_head else 10, bold=bool(is_head))
        c.alignment = Alignment(wrap_text=True, vertical="top")


def write_issues(wb):
    ws = wb.create_sheet("⚠ 需確認")
    cols = [("src", "出處", 10, ""), ("name", "項目", 34, ""), ("page", "手冊頁碼", 9, ""),
            ("print", "手冊印的內容", 34, ""), ("why", "問題", 40, ""), ("proposal", "建議處理", 26, ""),
            ("decision", "團隊決定", 20, "你們決定採用哪個值"), ("remark", "備註", 24, "")]
    col = setup_sheet(ws, "⚠ 手冊本身數字不一致（已用原圖確認不是抽取錯誤）",
                      "這些項目抽出來的數字和手冊印的一樣（主要的 8 筆已把 PDF 轉成圖片人工看過；2 項小數尾差由兩種抽取方法一致確認），"
                      "是手冊自己的數字對不起來，"
                      "需要團隊決定用哪個值，或詢問建研所／查官方勘誤。", cols, freeze="A5")
    for i, issue in enumerate(ISSUES, 5):
        for k, v in zip(("src", "name", "page", "print", "why", "proposal"), issue):
            ws[f"{col[k]}{i}"] = int(v) if k == "page" else v
        style_row(ws, i, len(cols), col, (), ("decision", "remark"))


def main():
    pdf_path, draft = sys.argv[1], Path(sys.argv[2])
    pages = PageText(pdf_path)
    p_rows = read_csv(draft / "p_lcc_2022.csv")
    b_rows = read_csv(draft / "b_lcc_2025.csv")
    wb = Workbook()
    write_guide(wb)
    write_issues(wb)
    write_p_lcc(wb, p_rows, pages)
    for tno in B_TABLES:
        write_b_table(wb, tno, [r for r in b_rows if r["table"] == tno], pages)
    out = draft / "LEBR資料庫_核對用.xlsx"
    wb.save(out)
    print(out)


if __name__ == "__main__":
    main()
