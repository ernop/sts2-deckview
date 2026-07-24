#!/usr/bin/env python3
"""Public-CI checks that do not require proprietary STS2 assemblies."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def fail(message: str) -> None:
    print(f"metadata check failed: {message}", file=sys.stderr)
    raise SystemExit(1)


root_manifest = json.loads((ROOT / "deckview.json").read_text(encoding="utf-8"))
workshop_manifest = json.loads(
    (ROOT / "workshop/content/deckview.json").read_text(encoding="utf-8")
)
if root_manifest != workshop_manifest:
    fail("deckview.json and workshop/content/deckview.json differ")

source = (ROOT / "DeckViewMod.cs").read_text(encoding="utf-8")
match = re.search(r'TestedGameVersion\s*=\s*"v([^"]+)"', source)
if not match:
    fail("could not find TestedGameVersion")
if match.group(1) != root_manifest["min_game_version"]:
    fail("TestedGameVersion and min_game_version differ")

if root_manifest.get("affects_gameplay") is not False:
    fail("DeckView must remain marked visibility-only")
description = root_manifest.get("description", "").lower()
for phrase in ("slay the spire 2", "does not change", "gameplay"):
    if phrase not in description:
        fail(f"manifest description is missing '{phrase}'")

for relative in ("README.md", "DEVELOPMENT.md", "PUBLISHING.md", "docs/requirements.md"):
    text = (ROOT / relative).read_text(encoding="utf-8")
    # "work or crash" is intentionally NOT flagged: DEVELOPMENT.md legitimately documents the
    # strict dev-build failure mode (DECKVIEW_PUBLIC controls the public revert-with-warning).
    for stale in ("v0.108.0", "ToggleOffset", "MiniMapView"):
        if stale.lower() in text.lower():
            fail(f"{relative} contains stale text '{stale}'")

game_version = root_manifest["min_game_version"]
for relative in ("README.md", "DEVELOPMENT.md", "PUBLISHING.md"):
    text = (ROOT / relative).read_text(encoding="utf-8")
    if f"v{game_version}" not in text:
        fail(f"{relative} does not mention current STS2 v{game_version}")

publishing = (ROOT / "PUBLISHING.md").read_text(encoding="utf-8")
mod_version = root_manifest["version"]
expected_archive = f"deckview-{mod_version}-sts2-{game_version}.zip"
if expected_archive not in publishing:
    fail(f"PUBLISHING.md does not name current archive {expected_archive}")

for relative in ("docs/images/deck-view.png", "docs/images/flat-map.png"):
    size = (ROOT / relative).stat().st_size
    if size >= 1_000_000:
        fail(f"{relative} is {size} bytes; Workshop preview must be below 1 MB")

if not (ROOT / "LICENSE").is_file():
    fail("LICENSE is missing")
if "DumpMinimapGraph = true" in source:
    fail("production map dumps are enabled")

print(
    f"metadata OK: DeckView {root_manifest['version']} / "
    f"STS2 {root_manifest['min_game_version']}"
)
