"""把 P-LCC / B-LCC CSV、參數表與修正清單合併成外掛讀取用的 lebr_2025.json。

用法：
    python -I build_json.py <lebr-db 資料夾>

讀取：
    draft/p_lcc_2022.csv、draft/b_lcc_2025.csv   程式從 PDF 抽出的資料
    source/parameters_2025.json                   參數表與公式常數（手動轉錄）
    corrections.csv                               人工核對後的修正清單
輸出：
    dist/lebr_2025.json

修正清單的「原值」必須等於目前抽出的值，否則中止：避免 PDF 重抽後套用到錯的資料。
"""
import csv
import json
import sys
from datetime import date
from pathlib import Path

SCHEMA_VERSION = "1.0"
# 人工核對完成後改成 "reviewed"，並在 README 記錄核對人與日期
REVIEW_STATUS = "draft"

WEAR_CODES = {"高耗損": "H", "中耗損": "M", "低耗損": "L", "46-6分戶樓板": "S"}

P_FIELDS = {"a1": "a1", "a2": "a2", "a3": "a3", "a4": "a4", "total_a1_a4": "total",
            "name": "name", "unit": "unit", "category": "category", "merged_span": "merged_span", "note": "note"}
B_FIELDS = {"base_a": "base_a", "surface_b": "surface_b", "F_new": "f_new", "renew_base_c": "renew_base_c",
            "renew_surface_c": "renew_surface_c", "F_renew": "f_renew", "delta_F": "delta_f",
            "name": "name", "is_baseline": "is_baseline", "baseline_code": "baseline_code"}
NUMERIC = {"a1", "a2", "a3", "a4", "total", "base_a", "surface_b", "f_new", "renew_base_c",
           "renew_surface_c", "f_renew", "delta_f"}


def read_csv(path):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return list(csv.DictReader(f))
    except UnicodeDecodeError:
        sys.exit(f"{path} 不是 UTF-8 編碼。用 Excel 存檔時請選「CSV UTF-8（逗號分隔）」")


def to_value(key, text):
    if key in NUMERIC:
        return float(text) if text not in ("", None) else None
    if key == "is_baseline":
        return text in ("Y", "true", "True", "1", True)
    return text or None


def p_record(row):
    return {
        "id": row["id"],
        "category": row["category"],
        "name": row["name"],
        "unit": row["unit"],
        "a1": to_value("a1", row["a1"]),
        "a2": to_value("a2", row["a2"]),
        "a3": to_value("a3", row["a3"]),
        "a4": to_value("a4", row["a4"]),
        "merged_span": row["merged_span"] or None,
        "total": to_value("total", row["total_a1_a4"]),
        "note": row["note"] or None,
        "source": {"book_page": int(row["source_book_page"]), "pdf_page": int(row["source_pdf_page"])},
        "check": {"status": row["check_flag"], "note": row["check_note"] or None},
        "corrections": [],
    }


def b_record(row):
    return {
        "code": row["code"],
        "table": row["table"],
        "group": row["group"],
        "wear_class": WEAR_CODES.get(row["wear_class"]),
        "subgroup": row["subgroup"] or None,
        "name": row["name"],
        "base_a": to_value("base_a", row["base_a"]),
        "surface_b": to_value("surface_b", row["surface_b"]),
        "a_text": row["a_text"] or None,
        "b_text": row["b_text"] or None,
        "f_new": to_value("f_new", row["F_new"]),
        "renew_base_c": to_value("renew_base_c", row["renew_base_c"]),
        "renew_surface_c": to_value("renew_surface_c", row["renew_surface_c"]),
        "f_renew": to_value("f_renew", row["F_renew"]),
        "delta_f": to_value("delta_f", row["delta_F"]),
        "is_baseline": row["is_baseline"] == "Y",
        "baseline_code": row["baseline_code"] or None,
        "source": {"book_page": int(row["source_book_page"]), "pdf_page": int(row["source_pdf_page"])},
        "check": {"status": row["check_flag"], "note": row["check_note"] or None},
        "corrections": [],
    }


def same(key, current, original_text):
    expected = to_value(key, original_text)
    if key in NUMERIC and current is not None and expected is not None:
        return abs(current - expected) < 1e-9
    return current == expected


def apply_corrections(path, p_by_id, b_by_code):
    applied = []
    for i, c in enumerate(read_csv(path), 2):  # 第 1 列是表頭
        db, rid, field = c["資料庫"].strip().upper(), c["編號"].strip(), c["欄位"].strip()
        table, fields = (p_by_id, P_FIELDS) if db == "P-LCC" else (b_by_code, B_FIELDS) if db == "B-LCC" else (None, None)
        if table is None:
            sys.exit(f"corrections.csv 第 {i} 列：資料庫必須是 P-LCC 或 B-LCC，收到「{c['資料庫']}」")
        if rid not in table:
            sys.exit(f"corrections.csv 第 {i} 列：找不到編號 {rid}")
        if field not in fields:
            sys.exit(f"corrections.csv 第 {i} 列：欄位「{field}」不能修正，可用欄位：{', '.join(fields)}")
        rec, key = table[rid], fields[field]
        if not same(key, rec[key], c["原值"]):
            sys.exit(f"corrections.csv 第 {i} 列：{rid}.{field} 目前是 {rec[key]}，但修正清單的原值是 {c['原值']}，"
                     "資料可能已重抽，請確認後更新原值")
        entry = {"field": field, "from": rec[key], "to": to_value(key, c["修正值"]), "basis": c["依據"] or None,
                 "decided_by": c["決定者"] or None, "date": c["日期"] or None, "note": c["說明"] or None}
        rec[key] = entry["to"]
        rec["corrections"].append(entry)
        applied.append({"db": db, "id": rid, **entry})
    return applied


def validate(p_rows, b_rows):
    codes = [r["code"] for r in b_rows]
    dup = {c for c in codes if codes.count(c) > 1}
    if dup:
        sys.exit(f"B-LCC 編號重複：{sorted(dup)}")
    by_code = {r["code"]: r for r in b_rows}
    for r in b_rows:
        base = by_code.get(r["baseline_code"])
        if base is None or not base["is_baseline"]:
            sys.exit(f"{r['code']} 的比較基準 {r['baseline_code']} 不存在或不是基準構件")
        if base["group"] != r["group"] or base["wear_class"] != r["wear_class"]:
            sys.exit(f"{r['code']} 的比較基準 {r['baseline_code']} 不在同一組")
    for r in p_rows + b_rows:
        if r["source"]["book_page"] + 12 != r["source"]["pdf_page"]:
            sys.exit(f"{r.get('id') or r.get('code')} 頁碼不一致")


def main():
    root = Path(sys.argv[1])
    p_rows = [p_record(r) for r in read_csv(root / "draft" / "p_lcc_2022.csv")]
    b_rows = [b_record(r) for r in read_csv(root / "draft" / "b_lcc_2025.csv")]
    params = json.loads((root / "source" / "parameters_2025.json").read_text(encoding="utf-8"))
    applied = apply_corrections(root / "corrections.csv",
                                {r["id"]: r for r in p_rows}, {r["code"]: r for r in b_rows})
    validate(p_rows, b_rows)

    db = {
        "meta": {
            "name": "LEBR 低碳（低蘊含碳）建築評估資料庫",
            "manual": "2025低碳(低蘊含碳)建築評估手冊（內政部建築研究所，2025年7月）",
            "manual_version": "2025",
            "schema_version": SCHEMA_VERSION,
            "review_status": REVIEW_STATUS,
            "generated": date.today().isoformat(),
            "units": {"carbon": "kgCO2e", "b_lcc": "kgCO2e/m2", "area": "m2"},
            "notes": [
                "p_lcc = 附錄一 ABRI 初級資材碳排資料庫（P-LCC, 2022），A1～A4 為 cradle to site 碳排。",
                "b_lcc = 附錄二 建築構件碳排資料庫（B-LCC），f_new 為新建碳排、f_renew 為 60 年更新碳排、delta_f 為相對基準構件之減碳量（負數 = 較低碳）。",
                "check.status = CHECK 的列是手冊本身數字不一致（已對照原圖確認不是抽取錯誤），數值維持手冊所印，除非 corrections 有修正。",
                "B-LCC 的 code 為團隊草案，對應 Revit 類型參數 LEBR_Code。",
            ],
        },
        "parameters": params,
        "p_lcc": p_rows,
        "b_lcc": b_rows,
        "corrections_applied": applied,
    }
    out = root / "dist" / "lebr_2025.json"
    out.parent.mkdir(exist_ok=True)
    out.write_text(json.dumps(db, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"{out}  P-LCC {len(p_rows)} 筆、B-LCC {len(b_rows)} 筆、套用修正 {len(applied)} 筆")


if __name__ == "__main__":
    main()
