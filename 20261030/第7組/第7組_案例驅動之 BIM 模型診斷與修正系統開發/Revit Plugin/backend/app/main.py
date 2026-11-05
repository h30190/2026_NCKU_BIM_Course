from __future__ import annotations

import json
from contextlib import asynccontextmanager
from datetime import datetime

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import HTMLResponse

from .database import connection, initialize, snapshot_from_json
from .schemas import CaseMatch, CaseSearchRequest, ResolutionRequest, ResolutionResult, RfiCreate, RfiDetail, RfiSummary
from .service import create_issue, find_matches, save_and_verify_ifc, suggestions_for
from .graph_api import router as graph_router, initialize_graph


@asynccontextmanager
async def lifespan(_: FastAPI):
    initialize()
    initialize_graph()
    yield


app = FastAPI(title="案例驅動 BIM 診斷與修正 API", version="0.1.0", lifespan=lifespan)
app.include_router(graph_router)


@app.get("/", response_class=HTMLResponse)
def dashboard() -> str:
    return """<!doctype html><html><head><title>案例驅動之 BIM 模型診斷與修正系統開發</title>
    <style>body{font:16px system-ui;margin:2rem;max-width:1000px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:.6rem;text-align:left}a{color:#075bb5}</style>
    </head><body><h1>案例驅動之 BIM 模型診斷與修正系統開發</h1><p>在 Revit 開啟「案例驅動 BIM 診斷與修正」面板，即可編輯及審查案例。 <a href="/docs">API documentation</a></p>
    <table><thead><tr><th>ID</th><th>Title</th><th>Priority</th><th>Status</th><th>Created</th></tr></thead>
    <tbody id="issues"></tbody></table><script>
    fetch('/graph/cases').then(r=>r.json()).then(rows=>{for(const x of rows){const tr=document.createElement('tr');for(const value of [x.id,x.draft.title,x.draft.priority,x.status,x.created_at]){const td=document.createElement('td');td.textContent=value;tr.appendChild(td);}document.getElementById('issues').appendChild(tr);}});
    </script></body></html>"""


@app.get("/issues/{issue_id}/view", response_class=HTMLResponse)
def issue_view(issue_id: int) -> str:
    return f"""<!doctype html><html><body style="font:16px system-ui;margin:2rem;max-width:1000px">
    <p><a href="/">Back to issues</a></p><pre id="detail">Loading...</pre>
    <script>fetch('/issues/{issue_id}').then(r=>r.json()).then(x=>document.getElementById('detail').textContent=JSON.stringify(x,null,2));</script>
    </body></html>"""


@app.post("/cases/search", response_model=list[CaseMatch])
def search_cases(request: CaseSearchRequest) -> list[CaseMatch]:
    return find_matches(request.element, request.limit)


@app.get("/cases/{case_id}")
def get_case(case_id: int):
    with connection() as db:
        row = db.execute("SELECT * FROM cases WHERE id = ?", (case_id,)).fetchone()
    if row is None:
        raise HTTPException(status_code=404, detail="Case not found.")
    return {**dict(row), "metadata": json.loads(row["metadata_json"])}


@app.post("/issues", response_model=RfiDetail, status_code=201)
async def submit_issue(payload: str = Form(...), ifc_file: UploadFile | None = File(default=None)) -> RfiDetail:
    try:
        rfi = RfiCreate.model_validate_json(payload)
    except ValueError as error:
        raise HTTPException(status_code=422, detail=f"Invalid issue payload: {error}") from error
    upload = await save_and_verify_ifc(ifc_file, rfi.ifc_global_ids)
    issue_id, matches, suggestions = create_issue(rfi, upload)
    return build_issue_detail(issue_id, matches, suggestions)


@app.get("/issues", response_model=list[RfiSummary])
def list_issues() -> list[RfiSummary]:
    with connection() as db:
        rows = db.execute("SELECT id, title, priority, status, created_at FROM issues ORDER BY id DESC").fetchall()
    return [RfiSummary(**dict(row), created_at=datetime.fromisoformat(row["created_at"])) for row in rows]


@app.get("/issues/{issue_id}", response_model=RfiDetail)
def get_issue(issue_id: int) -> RfiDetail:
    return build_issue_detail(issue_id)


@app.get("/issues/{issue_id}/comparison")
def get_comparison(issue_id: int):
    detail = build_issue_detail(issue_id)
    return {"issue_id": issue_id, "matches": detail.matches, "suggestions": detail.suggestions}


@app.post("/issues/{issue_id}/resolve", response_model=ResolutionResult)
def resolve_issue(issue_id: int, request: ResolutionRequest) -> ResolutionResult:
    with connection() as db:
        issue = db.execute("SELECT * FROM issues WHERE id = ?", (issue_id,)).fetchone()
        if issue is None:
            raise HTTPException(status_code=404, detail="Issue not found.")
        element = db.execute("SELECT metadata_json FROM issue_elements WHERE issue_id = ? ORDER BY element_id LIMIT 1", (issue_id,)).fetchone()
        if element is None:
            raise HTTPException(status_code=409, detail="Issue has no stored element metadata.")
        db.execute(
            """UPDATE issues SET status = 'Resolved', resolved_at = CURRENT_TIMESTAMP, confirmed_cause = ?,
               verified_fix = ?, resolution_notes = ? WHERE id = ?""",
            (request.confirmed_cause, request.verified_fix, request.resolution_notes, issue_id),
        )
        cursor = db.execute(
            """INSERT INTO cases (source_issue_id, title, metadata_json, confirmed_cause, verified_fix, resolution_notes)
               VALUES (?, ?, ?, ?, ?, ?)""",
            (issue_id, issue["title"], element["metadata_json"], request.confirmed_cause,
             request.verified_fix, request.resolution_notes),
        )
    return ResolutionResult(issue_id=issue_id, case_id=cursor.lastrowid, status="Resolved")


def build_issue_detail(issue_id: int, matches: list[CaseMatch] | None = None, suggestions: list[str] | None = None) -> RfiDetail:
    with connection() as db:
        issue = db.execute("SELECT * FROM issues WHERE id = ?", (issue_id,)).fetchone()
        if issue is None:
            raise HTTPException(status_code=404, detail="Issue not found.")
        elements = [snapshot_from_json(row["metadata_json"]) for row in db.execute(
            "SELECT metadata_json FROM issue_elements WHERE issue_id = ? ORDER BY element_id", (issue_id,)
        )]
        if matches is None:
            comparison_rows = db.execute(
                """SELECT c.*, ca.title, ca.confirmed_cause, ca.verified_fix FROM comparisons c
                   JOIN cases ca ON ca.id = c.case_id WHERE c.issue_id = ? ORDER BY c.score DESC""", (issue_id,)
            )
            matches = [
                CaseMatch(case_id=row["case_id"], title=row["title"], score=row["score"],
                          evidence=json.loads(row["evidence_json"]),
                          differences=json.loads(row["differences_json"]),
                          confirmed_cause=row["confirmed_cause"], verified_fix=row["verified_fix"])
                for row in comparison_rows
            ]
    return RfiDetail(
        id=issue["id"], title=issue["title"], description=issue["description"], category=issue["category"],
        priority=issue["priority"], status=issue["status"], created_at=datetime.fromisoformat(issue["created_at"]),
        elements=elements, ifc_file_name=issue["ifc_file_name"], matches=matches,
        suggestions=suggestions if suggestions is not None else suggestions_for(elements[0], matches),
    )
