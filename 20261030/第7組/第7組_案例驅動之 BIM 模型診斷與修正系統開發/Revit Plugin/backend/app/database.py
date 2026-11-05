from __future__ import annotations

import json
import os
import sqlite3
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator

from .schemas import ElementSnapshot

DATA_DIR = Path(os.getenv("CASEBASE_DATA_DIR", Path(__file__).resolve().parents[1] / "data"))
DATABASE_PATH = DATA_DIR / "casebase.db"


@contextmanager
def connection() -> Iterator[sqlite3.Connection]:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(DATABASE_PATH)
    db.row_factory = sqlite3.Row
    db.execute("PRAGMA foreign_keys = ON")
    try:
        yield db
        db.commit()
    finally:
        db.close()


def initialize() -> None:
    with connection() as db:
        db.executescript(
            """
            CREATE TABLE IF NOT EXISTS projects (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                model_version TEXT
            );
            CREATE TABLE IF NOT EXISTS issues (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id TEXT NOT NULL REFERENCES projects(id),
                title TEXT NOT NULL,
                description TEXT NOT NULL,
                category TEXT NOT NULL,
                priority TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'Open',
                ifc_file_name TEXT,
                ifc_path TEXT,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                resolved_at TEXT,
                confirmed_cause TEXT,
                verified_fix TEXT,
                resolution_notes TEXT
            );
            CREATE TABLE IF NOT EXISTS issue_elements (
                issue_id INTEGER NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
                unique_id TEXT NOT NULL,
                element_id TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                PRIMARY KEY(issue_id, unique_id)
            );
            CREATE TABLE IF NOT EXISTS cases (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_issue_id INTEGER REFERENCES issues(id),
                title TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                confirmed_cause TEXT NOT NULL,
                verified_fix TEXT NOT NULL,
                resolution_notes TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS comparisons (
                issue_id INTEGER NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
                case_id INTEGER NOT NULL REFERENCES cases(id),
                score REAL NOT NULL,
                evidence_json TEXT NOT NULL,
                differences_json TEXT NOT NULL,
                PRIMARY KEY(issue_id, case_id)
            );
            """
        )
        if db.execute("SELECT COUNT(*) FROM cases").fetchone()[0] == 0:
            seed_cases(db)


def seed_cases(db: sqlite3.Connection) -> None:
    snapshots = [
        (
            "Door clearance conflict",
            ElementSnapshot(
                unique_id="sample-door-1",
                element_id="101",
                category="Doors",
                family="Single-Flush",
                type_name="900 x 2100mm",
                level="Level 1",
                dimensions={"Width": 900.0, "Height": 2100.0},
                parameters={"Fire Rating": "60 min"},
                ifc_class="IfcDoor",
            ),
            "Door swing conflicted with a nearby wall due to an incorrect handing.",
            "Change the door handing and confirm the required clearance in the coordination view.",
        ),
        (
            "Structural wall type mismatch",
            ElementSnapshot(
                unique_id="sample-wall-1",
                element_id="201",
                category="Walls",
                family="Basic Wall",
                type_name="Generic - 200mm",
                level="Level 2",
                dimensions={"Width": 200.0},
                parameters={"Structural": "Yes"},
                ifc_class="IfcWall",
            ),
            "Load-bearing wall was assigned a non-structural type.",
            "Replace it with the approved structural wall type and verify structural classification.",
        ),
    ]
    for title, snapshot, cause, fix in snapshots:
        db.execute(
            """INSERT INTO cases (title, metadata_json, confirmed_cause, verified_fix)
               VALUES (?, ?, ?, ?)""",
            (title, snapshot.model_dump_json(), cause, fix),
        )


def snapshot_from_json(value: str) -> ElementSnapshot:
    return ElementSnapshot.model_validate(json.loads(value))
