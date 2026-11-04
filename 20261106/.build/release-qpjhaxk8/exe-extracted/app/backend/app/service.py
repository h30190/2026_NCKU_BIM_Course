from __future__ import annotations

import json
import shutil
from pathlib import Path
from typing import Iterable

from fastapi import HTTPException, UploadFile

from .database import DATA_DIR, connection, snapshot_from_json
from .schemas import CaseMatch, Difference, ElementSnapshot, RfiCreate, UploadResult

UPLOAD_DIR = DATA_DIR / "uploads"
ALLOWED_STATUSES = {"Open", "In Progress", "Resolved", "Closed"}


def find_matches(element: ElementSnapshot, limit: int = 5) -> list[CaseMatch]:
    with connection() as db:
        rows = db.execute("SELECT * FROM cases ORDER BY id").fetchall()
    matches = [
        compare_to_case(element, row)
        for row in rows
        if shares_element_identity(element, snapshot_from_json(row["metadata_json"]))
    ]
    return sorted((match for match in matches if match.score >= 0.35), key=lambda item: item.score, reverse=True)[:limit]


def shares_element_identity(element: ElementSnapshot, historical: ElementSnapshot) -> bool:
    """Require one primary classification match before secondary similarity scoring."""
    for current, previous in (
        (element.ifc_class, historical.ifc_class),
        (element.category, historical.category),
    ):
        if current and previous and current != previous:
            return False
    return any(
        current and previous and current == previous
        for current, previous in (
            (element.ifc_class, historical.ifc_class),
            (element.category, historical.category),
            (element.family, historical.family),
            (element.type_name, historical.type_name),
        )
    )


def compare_to_case(element: ElementSnapshot, case_row) -> CaseMatch:
    historical = snapshot_from_json(case_row["metadata_json"])
    evidence: list[str] = []
    differences: list[Difference] = []
    score = 0.0

    def compare(field: str, current: str | None, previous: str | None, weight: float) -> None:
        nonlocal score
        if current and previous and current == previous:
            score += weight
            evidence.append(f"Same {field}: {current}")
        elif current or previous:
            differences.append(Difference(field=field, current=current, historical=previous))

    compare("IFC class", element.ifc_class, historical.ifc_class, 0.35)
    compare("category", element.category, historical.category, 0.25)
    compare("family", element.family, historical.family, 0.15)
    compare("type", element.type_name, historical.type_name, 0.15)
    for name, current in element.dimensions.items():
        prior = historical.dimensions.get(name)
        if prior is None:
            differences.append(Difference(field=f"dimension:{name}", current=str(current), historical=None))
        elif abs(current - prior) <= max(1.0, abs(prior) * 0.02):
            score += 0.05
            evidence.append(f"Comparable {name}: {current:g}")
        else:
            differences.append(Difference(field=f"dimension:{name}", current=str(current), historical=str(prior)))
    for name, current in element.parameters.items():
        prior = historical.parameters.get(name)
        if prior is not None and current == prior:
            score += 0.03
            evidence.append(f"Same parameter {name}: {current}")
        elif prior is not None:
            differences.append(Difference(field=f"parameter:{name}", current=current, historical=prior))

    return CaseMatch(
        case_id=case_row["id"],
        title=case_row["title"],
        score=round(min(score, 1.0), 2),
        evidence=evidence,
        differences=differences,
        confirmed_cause=case_row["confirmed_cause"],
        verified_fix=case_row["verified_fix"],
    )


def suggestions_for(element: ElementSnapshot, matches: Iterable[CaseMatch]) -> list[str]:
    suggestions: list[str] = []
    if element.category == "Doors" and not element.parameters.get("Fire Rating"):
        suggestions.append("Hypothesis: verify the door fire-rating parameter; it is missing from the submitted metadata.")
    if element.category == "Walls" and element.parameters.get("Structural", "").lower() in {"no", "false"}:
        suggestions.append("Hypothesis: verify whether the wall requires a structural classification.")
    for match in matches:
        if match.score >= 0.7:
            suggestions.append(f"Verified historical fix ({match.title}): {match.verified_fix}")
    return suggestions or ["No reliable diagnosis: add project-specific details or resolve this issue to create a verified case."]


async def save_and_verify_ifc(upload: UploadFile | None, expected_global_ids: list[str]) -> UploadResult | None:
    if upload is None or not upload.filename:
        return None
    if Path(upload.filename).suffix.lower() != ".ifc":
        raise HTTPException(status_code=400, detail="Attachment must be an IFC file.")
    if not expected_global_ids:
        raise HTTPException(
            status_code=400,
            detail="Selective IFC verification requires an IFC GlobalId for every submitted element.",
        )

    UPLOAD_DIR.mkdir(parents=True, exist_ok=True)
    file_name = Path(upload.filename).name
    destination = UPLOAD_DIR / file_name
    with destination.open("wb") as output:
        shutil.copyfileobj(upload.file, output)
    byte_count = destination.stat().st_size
    if byte_count == 0:
        destination.unlink(missing_ok=True)
        raise HTTPException(status_code=400, detail="The IFC attachment is empty.")

    verified_ids = verify_ifc_scope(destination, expected_global_ids)
    return UploadResult(file_name=file_name, byte_count=byte_count, verified_global_ids=verified_ids)


def verify_ifc_scope(path: Path, expected_global_ids: list[str]) -> list[str]:
    if not expected_global_ids:
        return []
    try:
        import ifcopenshell
    except ImportError as error:
        raise HTTPException(status_code=503, detail="IfcOpenShell is required to validate selective IFC uploads.") from error
    try:
        model = ifcopenshell.open(str(path))
        found = {entity.GlobalId for entity in model.by_type("IfcRoot") if getattr(entity, "GlobalId", None)}
        allowed_context = {"IfcProject", "IfcSite", "IfcBuilding", "IfcBuildingStorey"}
        exported_products = {
            entity.GlobalId for entity in model.by_type("IfcProduct")
            if getattr(entity, "GlobalId", None) and entity.is_a() not in allowed_context
        }
    except Exception as error:
        raise HTTPException(status_code=400, detail=f"Unable to read IFC attachment: {error}") from error
    missing = sorted(set(expected_global_ids) - found)
    unexpected_products = exported_products - set(expected_global_ids)
    if missing or unexpected_products:
        raise HTTPException(
            status_code=400,
            detail="Selective IFC verification failed; attachment does not match the requested element scope.",
        )
    return sorted(found & set(expected_global_ids))


def create_issue(payload: RfiCreate, ifc: UploadResult | None) -> tuple[int, list[CaseMatch], list[str]]:
    primary = payload.elements[0]
    matches = find_matches(primary)
    suggestions = suggestions_for(primary, matches)
    with connection() as db:
        db.execute(
            "INSERT OR REPLACE INTO projects (id, name, model_version) VALUES (?, ?, ?)",
            (payload.project_id, payload.project_name, payload.model_version),
        )
        cursor = db.execute(
            """INSERT INTO issues (project_id, title, description, category, priority, ifc_file_name, ifc_path)
               VALUES (?, ?, ?, ?, ?, ?, ?)""",
            (payload.project_id, payload.title, payload.description, payload.category, payload.priority,
             ifc.file_name if ifc else None, str(UPLOAD_DIR / ifc.file_name) if ifc else None),
        )
        issue_id = cursor.lastrowid
        for element in payload.elements:
            db.execute(
                "INSERT INTO issue_elements (issue_id, unique_id, element_id, metadata_json) VALUES (?, ?, ?, ?)",
                (issue_id, element.unique_id, element.element_id, element.model_dump_json()),
            )
        for match in matches:
            db.execute(
                """INSERT INTO comparisons (issue_id, case_id, score, evidence_json, differences_json)
                   VALUES (?, ?, ?, ?, ?)""",
                (issue_id, match.case_id, match.score,
                 json.dumps(match.evidence), json.dumps([item.model_dump() for item in match.differences])),
            )
    return issue_id, matches, suggestions
