"""從《2025 低碳(低蘊含碳)建築評估手冊》抽出附錄一 P-LCC 與附錄二 B-LCC，輸出 CSV 草稿。

用法：
    python -I extract_lebr_tables.py <手冊 PDF 路徑> <輸出資料夾>

輸出（UTF-8 with BOM，Excel 可直接開）：
    p_lcc_2022.csv   附錄一 初級資材碳排資料庫
    b_lcc_2025.csv   附錄二 建築構件碳排資料庫（附表 2-1～2-7 合併）

每列附 PDF 頁碼與手冊印刷頁碼，並自動驗算加總，對不上的列標在 check_flag。
"""
import csv
import re
import sys
from pathlib import Path

import pymupdf

BOOK_PAGE_OFFSET = 12  # PDF 第 64 頁 = 手冊印刷第 52 頁
P_LCC_PAGES = range(64, 68)
B_LCC_PAGES = range(68, 77)

NUM_RE = re.compile(r"^[+\-]?\d+(?:\.\d+)?$")
PAREN_NUM_RE = re.compile(r"[（(]\s*([+\-]?\d+(?:\.\d+)?)\s*[）)]")


def clean(text):
    return re.sub(r"\s*\n\s*", "", text or "").strip()


def to_num(text):
    """'12.3' / '+15.42' / '2.43+13.58' / '73.67（基準）' -> float；無底層等 -> 0.0；其他 -> None"""
    t = clean(text).replace("＋", "+")
    if not t:
        return None
    if t.startswith("無"):
        return 0.0
    t = re.sub(r"[（(].*?[）)]", "", t).strip()
    if NUM_RE.match(t):
        return float(t)
    if re.fullmatch(r"\d+(?:\.\d+)?(?:\+\d+(?:\.\d+)?)+", t):
        return sum(float(x) for x in t.split("+"))
    return None


def paren_num(text):
    """'崁入式鋁框（24.84）' -> 24.84；'無底層' -> 0.0"""
    t = clean(text)
    if t.startswith("無"):
        return 0.0
    m = PAREN_NUM_RE.findall(t)
    return float(m[-1]) if m else None


def close(a, b, tol=0.02):
    # 只用絕對誤差，與核對用 Excel 的驗算公式一致
    return a is not None and b is not None and abs(a - b) <= tol + 1e-9


def fmt(x):
    if x is None:
        return ""
    # 保留到 10 位小數再去尾零，避免 0.00114 這類小數被截掉
    return f"{x:.10f}".rstrip("0").rstrip(".")


def load_tables(pdf_path, pages):
    doc = pymupdf.open(pdf_path)
    tables = []
    for pno in pages:
        for t in doc[pno - 1].find_tables().tables:
            tables.append((pno, t.extract()))
    return tables


def page_cols(pno):
    return {"source_pdf_page": pno, "source_book_page": pno - BOOK_PAGE_OFFSET}


# ---------------------------------------------------------------- 附錄一 P-LCC

def extract_p_lcc(pdf_path):
    stages = ["A1", "A2", "A3", "A4"]
    rows, category = [], ""
    for pno, table in load_tables(pdf_path, P_LCC_PAGES):
        for r in table[2:]:  # 前兩列為表頭
            if r[0] and not r[1]:  # 表尾註解
                continue
            if r[0]:
                category = clean(r[0])
            name, unit, cells, total = clean(r[1]), clean(r[2]), r[3:7], to_num(r[7])
            rec = {"category": category, "name": name, "unit": unit,
                   "a1": None, "a2": None, "a3": None, "a4": None,
                   "merged_span": "", "total_a1_a4": total, "note": ""}
            spans = []
            for i, c in enumerate(cells):
                if c is None or c == "":
                    continue
                v = to_num(c)
                j = i + 1
                while j < 4 and cells[j] is None:  # None = 被合併儲存格覆蓋
                    j += 1
                if v is None:  # 儲存格內是文字說明
                    rec["note"] = clean(c)
                    continue
                rec[f"a{i + 1}"] = v
                if j - i > 1:
                    spans.append(f"{stages[i]}-{stages[j - 1]}")
            rec["merged_span"] = ";".join(spans)

            parts = [rec[k] for k in ("a1", "a2", "a3", "a4") if rec[k] is not None]
            if not parts:
                rec["check_flag"], rec["check_note"] = "INFO", "無分階段數據，僅有總量"
            elif close(sum(parts), total, tol=0.015):
                rec["check_flag"], rec["check_note"] = "OK", ""
            else:
                rec["check_flag"] = "CHECK"
                rec["check_note"] = f"分階段加總 {fmt(sum(parts))} ≠ 總量 {fmt(total)}"
            rec.update(page_cols(pno))
            rows.append(rec)
    for i, rec in enumerate(rows, 1):
        rec["id"] = f"PLCC-{i:03d}"
    return rows


# ---------------------------------------------------------------- 附錄二 B-LCC

WEAR_KEYS = [("高耗損", "H", "高耗損"), ("中耗損", "M", "中耗損"),
             ("低耗損", "L", "低耗損"), ("46-6", "S", "46-6分戶樓板")]
FRAME_CODES = [("鋁框或塑鋼框", "ALPVC"), ("不銹鋼框", "SS"), ("鋁框", "AL"),
               ("塑鋼框", "PVC"), ("木窗框", "WD")]


def wear_of(text):
    t = clean(text)
    for key, code, label in WEAR_KEYS:
        if key in t:
            return code, label
    return None


def frame_code(text):
    for key, code in FRAME_CODES:
        if key in text:
            return code
    return "X"


def ordered(r):
    """一列中非空儲存格（依欄位順序）"""
    return [c for c in r if c]


def new_rec(table, group, name, **kw):
    rec = {"table": table, "group": group, "wear_class": "", "subgroup": "",
           "name": name, "base_a": None, "surface_b": None, "a_text": "", "b_text": "",
           "F_new": None, "renew_base_c": None, "renew_surface_c": None,
           "F_renew": None, "delta_F": None, "is_baseline": False}
    rec.update(kw)
    return rec


def parse_2_1(table, pno):
    out = []
    for r in table[1:]:
        if r[0] and r[0].startswith("減碳量"):
            continue
        if not r[0]:  # 基準列的第二行：（9.05）（19.65）
            out[-1]["a_text"] += clean(r[1])
            out[-1]["b_text"] += clean(r[2])
            continue
        out.append(new_rec("2-1", "OW", clean(r[0]), a_text=clean(r[1]), b_text=clean(r[2]),
                           F_new=to_num(r[3]), renew_surface_c=to_num(r[4]),
                           F_renew=to_num(r[5]), delta_F=to_num(r[6]), **page_cols(pno)))
    for rec in out:
        rec["base_a"], rec["surface_b"] = paren_num(rec["a_text"]), paren_num(rec["b_text"])
        rec["renew_base_c"] = 0.0
        rec["is_baseline"] = "基準" in rec["name"]
    return out


def parse_windows(pieces, table_no, group):
    """附表 2-2 / 2-3：玻璃 × 窗框"""
    out, glass, glass_a = [], "", None
    for pno, table in pieces:
        for r in table:
            v = [clean(c) for c in ordered(r)]
            if not v or v[0].startswith(("玻璃種類", "註")):
                continue
            if len(v) == 7:
                glass, glass_a = v[0], to_num(v[1])
                v = v[2:]
            frame = v[0]
            out.append(new_rec(table_no, group, f"{glass} / {frame}", subgroup=glass,
                               base_a=glass_a, surface_b=paren_num(frame),
                               a_text=f"玻璃 {glass}", b_text=frame,
                               F_new=to_num(v[1]), renew_base_c=to_num(v[2]),
                               renew_surface_c=to_num(v[2]), F_renew=to_num(v[3]),
                               delta_F=to_num(v[4]), frame=frame_code(frame), **page_cols(pno)))
    for rec in out:
        rec["is_baseline"] = rec["frame"] in ("AL", "ALPVC")
    return out


def parse_2_4(table, pno):
    out = []
    for r in table[1:]:
        v = [clean(c) for c in ordered(r)]
        if v[0].startswith("註"):
            continue
        out.append(new_rec("2-4", "CW", v[0], base_a=to_num(v[1]), surface_b=0.0, a_text=v[1],
                           F_new=to_num(v[1]), renew_base_c=to_num(v[2]), renew_surface_c=to_num(v[2]),
                           F_renew=to_num(v[3]), delta_F=to_num(v[4]),
                           is_baseline="基準" in v[1], **page_cols(pno)))
    return out


def parse_2_5(table, pno):
    out, wear = [], None
    for r in table[1:]:
        v = [clean(c) for c in ordered(r)]
        if v[0].startswith("註"):
            continue
        if wear_of(v[0]):
            wear = wear_of(v[0])
            v = v[1:]
        out.append(new_rec("2-5", "IW", v[0], wear_class=wear[1], wear_code=wear[0],
                           base_a=to_num(v[1]), surface_b=0.0, F_new=to_num(v[1]),
                           renew_base_c=to_num(v[2]), renew_surface_c=to_num(v[2]),
                           F_renew=to_num(v[3]), delta_F=to_num(v[4]),
                           is_baseline="基準" in v[0], **page_cols(pno)))
    return out


# 附表 2-6 的更新次數欄跨列斷行難以穩定解析，改依表 3-1 / 表格標示填入（基層, 表層），再由驗算確認
FLOOR_RENEW = {"H": (1.0, 3.0), "M": (0.0, 2.0), "L": (0.0, 0.5), "S": (0.0, 0.5)}


def parse_2_6(pieces):
    out, wear, sub, prefix = [], None, "", ""
    for pno, table in pieces:
        for r in table:
            r = list(r) + [None] * (9 - len(r))
            c0, c1, c2 = clean(r[0]), clean(r[1]), clean(r[2])
            if c0.startswith(("建築分類", "註")):
                continue
            if wear_of(c0):
                wear = wear_of(c0)
            if c1:
                sub = "一般地坪" if "一" in c1 else "木作地坪"
            if not clean(r[5]):  # 沒有新建碳排 → 名稱片段
                if not c2:
                    continue
                if re.match(r"^\d+\.", c2):
                    prefix = c2
                elif out:
                    out[-1]["name"] += c2
                continue
            cb, cs = FLOOR_RENEW[wear[0]]
            if clean(r[3]).startswith("無"):  # 無基層
                cb = 0.0
            out.append(new_rec("2-6", "F", prefix + c2, wear_class=wear[1], wear_code=wear[0],
                               subgroup=sub, base_a=to_num(r[3]), surface_b=to_num(r[4]),
                               a_text=clean(r[3]), b_text=clean(r[4]),
                               F_new=to_num(r[5]), renew_base_c=cb, renew_surface_c=cs,
                               F_renew=to_num(r[7]), delta_F=to_num(r[8]), **page_cols(pno)))
            prefix = ""
    for rec in out:
        rec["is_baseline"] = "基準" in rec["name"]
    # 46-6 一般地坪手冊未標「基準值」，以同組 ΔF=0 的構件推定
    marked = {(r["wear_class"], r["subgroup"]) for r in out if r["is_baseline"]}
    for rec in out:
        if (rec["wear_class"], rec["subgroup"]) not in marked and rec["delta_F"] == 0:
            rec["is_baseline"] = True
            rec["baseline_note"] = "手冊未標示基準，依 ΔF=0 推定"
            marked.add((rec["wear_class"], rec["subgroup"]))
    return out


def parse_2_7(pieces):
    out, wear, sub, sub_a = [], None, "", None
    for pno, table in pieces:
        for r in table:
            v = [clean(c) for c in ordered(r)]
            if not v or v[0].startswith(("建築分類", "註")):
                continue
            if wear_of(v[0]):
                wear = wear_of(v[0])
                v = v[1:]
            elif v[0] == "築）":  # 跨頁的分類名稱尾巴
                v = v[1:]
            if v[0].endswith("基礎（60.10）") or v[0].endswith("基礎（2.57）"):
                sub, sub_a = v[0], paren_num(v[0])
                v = v[1:]
            if len(v) == 1:  # 「值）」之類名稱斷行
                out[-1]["name"] += v[0]
                continue
            out.append(new_rec("2-7", "P", v[0], wear_class=wear[1], wear_code=wear[0],
                               subgroup=sub, base_a=sub_a, surface_b=to_num(v[1]),
                               F_new=to_num(v[2]), renew_base_c=0.0, renew_surface_c=to_num(v[3]),
                               F_renew=to_num(v[4]), delta_F=to_num(v[5]), **page_cols(pno)))
    for rec in out:
        rec["is_baseline"] = "基準" in rec["name"]
    return out


def assign_codes(rows):
    counters = {}
    for rec in rows:
        g, w = rec["group"], rec.get("wear_code", "")
        if g in ("W", "CWW"):
            glass_no = re.match(r"(\d+)", rec["subgroup"]).group(1)
            rec["code"] = f"{g}-G{int(glass_no):02d}-{rec['frame']}"
            continue
        if g == "F":
            key = f"F-{w}-{'G' if rec['subgroup'] == '一般地坪' else 'W'}"
        elif g == "P":
            key = f"P-{w}-{'RC' if 'RC' in rec['subgroup'] else 'GR'}"
        elif g == "IW":
            key = f"IW-{w}"
        else:
            key = g
        start = 0 if g == "CW" else 1  # 附表 2-4 手冊本身從 0 號（基準）開始
        counters[key] = counters.get(key, start - 1) + 1
        rec["code"] = f"{key}{counters[key]:02d}" if g in ("F", "P") else f"{key}-{counters[key]:02d}"


def baseline_key(rec):
    if rec["group"] in ("W", "CWW"):
        return (rec["group"], rec["subgroup"])
    return (rec["group"], rec["wear_class"], rec["subgroup"] if rec["group"] in ("F", "P") else "")


def verify_b_lcc(rows):
    baselines = {}
    for rec in rows:
        if rec["is_baseline"]:
            baselines[baseline_key(rec)] = rec
    for rec in rows:
        notes = []
        a, b = rec["base_a"] or 0.0, rec["surface_b"] or 0.0
        if rec["group"] != "CW" and rec["group"] != "IW" and not close(a + b, rec["F_new"]):
            notes.append(f"a+b={fmt(a + b)} ≠ F={fmt(rec['F_new'])}")
        if rec["group"] in ("W", "CWW", "CW", "IW"):
            expect = rec["F_new"] * rec["renew_surface_c"]
        else:
            expect = a * rec["renew_base_c"] + b * rec["renew_surface_c"]
        if not close(expect, rec["F_renew"]):
            notes.append(f"更新碳排應為 {fmt(expect)}，表列 {fmt(rec['F_renew'])}")
        base = baselines.get(baseline_key(rec))
        rec["baseline_code"] = base["code"] if base else ""
        if base:
            d = (rec["F_new"] + rec["F_renew"]) - (base["F_new"] + base["F_renew"])
            if not close(d, rec["delta_F"]):
                notes.append(f"ΔF 應為 {fmt(d)}，表列 {fmt(rec['delta_F'])}")
        else:
            notes.append("找不到基準構件")
        if rec.get("baseline_note"):
            notes.append(rec["baseline_note"])
        rec["check_flag"] = "CHECK" if notes else "OK"
        rec["check_note"] = "；".join(notes)


def extract_b_lcc(pdf_path):
    by_page = {}
    for pno, table in load_tables(pdf_path, B_LCC_PAGES):
        by_page.setdefault(pno, []).append(table)
    rows = []
    rows += parse_2_1(by_page[68][0], 68)
    rows += parse_windows([(68, by_page[68][1]), (69, by_page[69][0])], "2-2", "W")
    rows += parse_windows([(70, by_page[70][0])], "2-3", "CWW")
    rows += parse_2_4(by_page[71][0], 71)
    rows += parse_2_5(by_page[71][1], 71)
    rows += parse_2_6([(72, by_page[72][0]), (73, by_page[73][0]), (74, by_page[74][0])])
    rows += parse_2_7([(75, by_page[75][0]), (76, by_page[76][0])])
    assign_codes(rows)
    verify_b_lcc(rows)
    return rows


GROUP_NAMES = {"OW": "一般外牆外裝", "W": "外窗", "CWW": "透光帷幕外窗",
               "CW": "不透光帷幕外牆及一般外牆", "IW": "內隔間", "F": "室內地坪", "P": "戶外地坪"}

P_LCC_FIELDS = ["id", "category", "name", "unit", "a1", "a2", "a3", "a4", "merged_span",
                "total_a1_a4", "note", "check_flag", "check_note", "source_pdf_page", "source_book_page"]
B_LCC_FIELDS = ["code", "table", "group", "group_name", "wear_class", "subgroup", "name",
                "base_a", "surface_b", "a_text", "b_text", "F_new", "renew_base_c", "renew_surface_c",
                "F_renew", "delta_F", "is_baseline", "baseline_code", "check_flag", "check_note",
                "source_pdf_page", "source_book_page"]


def write_csv(path, fields, rows):
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        w.writeheader()
        for rec in rows:
            w.writerow({k: fmt(v) if isinstance(v, float) else ("Y" if v is True else "" if v is False else v)
                        for k, v in rec.items()})


def main():
    pdf_path, out_dir = sys.argv[1], Path(sys.argv[2])
    out_dir.mkdir(parents=True, exist_ok=True)
    p_rows = extract_p_lcc(pdf_path)
    b_rows = extract_b_lcc(pdf_path)
    for rec in b_rows:
        rec["group_name"] = GROUP_NAMES[rec["group"]]
    write_csv(out_dir / "p_lcc_2022.csv", P_LCC_FIELDS, p_rows)
    write_csv(out_dir / "b_lcc_2025.csv", B_LCC_FIELDS, b_rows)
    for label, rows in (("P-LCC", p_rows), ("B-LCC", b_rows)):
        flags = {}
        for rec in rows:
            flags[rec["check_flag"]] = flags.get(rec["check_flag"], 0) + 1
        print(label, len(rows), "rows", flags)


if __name__ == "__main__":
    main()
