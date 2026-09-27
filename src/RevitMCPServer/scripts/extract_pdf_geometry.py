#!/usr/bin/env python3
"""Extract vector geometry and text from a PDF, as JSON on stdout.

Intended for pulling dimensions and linework off a scanned-in or exported drawing sheet so it can
be compared against, or rebuilt in, a Revit model.

Licence note: this depends on PyMuPDF, which is AGPL-3.0 (or a paid commercial licence from
Artifex). That obligation attaches to distributing this script, not to the rest of the project,
which is why the PDF work lives behind a subprocess boundary rather than being linked in.

Units: PDF user-space points, 72 per inch. Revit works in decimal feet, so a caller converts with
points / 72 / 12.
"""

from __future__ import annotations

import argparse
import json
import sys

POINTS_PER_FOOT = 864.0  # 72 points/inch * 12 inches/foot


class ExtractError(Exception):
    """A failure worth reporting as JSON on stdout, so the caller can read the reason."""


def parse_pages(spec: str | None, page_count: int) -> list[int]:
    """Turns "1", "1-3", "1,4-5" into zero-based page indices."""
    if not spec:
        return list(range(page_count))

    wanted: list[int] = []
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            start_text, _, end_text = part.partition("-")
            start, end = int(start_text), int(end_text)
        else:
            start = end = int(part)
        if start < 1 or end < start:
            raise ValueError(f"'{part}' is not a valid page range (pages are 1-based).")
        wanted.extend(range(start - 1, min(end, page_count)))

    # De-duplicate while keeping the requested order.
    return list(dict.fromkeys(i for i in wanted if 0 <= i < page_count))


def point(p) -> dict:
    return {"x": round(p.x, 4), "y": round(p.y, 4)}


def describe_drawing(drawing: dict, max_points: int) -> dict | None:
    """Flattens one PyMuPDF drawing into JSON-friendly primitives."""
    items = []

    for item in drawing.get("items", []):
        kind = item[0]

        if kind == "l":                                  # line
            items.append({"type": "line", "start": point(item[1]), "end": point(item[2])})
        elif kind == "re":                               # rectangle
            rect = item[1]
            items.append({
                "type": "rect",
                "x0": round(rect.x0, 4), "y0": round(rect.y0, 4),
                "x1": round(rect.x1, 4), "y1": round(rect.y1, 4),
                "width": round(rect.width, 4), "height": round(rect.height, 4),
            })
        elif kind == "qu":                               # quad
            quad = item[1]
            items.append({
                "type": "quad",
                "points": [point(quad.ul), point(quad.ur), point(quad.lr), point(quad.ll)],
            })
        elif kind == "c":                                # cubic bezier
            items.append({
                "type": "curve",
                "start": point(item[1]),
                "control1": point(item[2]),
                "control2": point(item[3]),
                "end": point(item[4]),
            })

        if len(items) >= max_points:
            break

    if not items:
        return None

    rect = drawing.get("rect")
    return {
        "items": items,
        "itemCount": len(items),
        "strokeColor": list(drawing["color"]) if drawing.get("color") else None,
        "fillColor": list(drawing["fill"]) if drawing.get("fill") else None,
        "lineWidth": round(drawing["width"], 4) if drawing.get("width") is not None else None,
        "bbox": {
            "x0": round(rect.x0, 4), "y0": round(rect.y0, 4),
            "x1": round(rect.x1, 4), "y1": round(rect.y1, 4),
        } if rect is not None else None,
    }


def extract(path: str, pages: str | None, max_items: int, include_text: bool) -> dict:
    try:
        # PyMuPDF 1.24.3+ exposes 'pymupdf'; the old 'fitz' name warns on 1.28 and is going away.
        try:
            import pymupdf as fitz
        except ImportError:
            import fitz
    except ImportError:
        raise ExtractError(
            "PyMuPDF is not installed. Install it with: pip install pymupdf  "
            "(note: PyMuPDF is AGPL-3.0 or requires a commercial licence from Artifex)."
        )

    try:
        document = fitz.open(path)
    except Exception as exc:                             # noqa: BLE001 - surfaced as JSON
        raise ExtractError(f"Could not open '{path}': {exc}")

    with document:
        indices = parse_pages(pages, document.page_count)

        result = {
            "ok": True,
            "path": path,
            "pageCount": document.page_count,
            "pagesExtracted": [i + 1 for i in indices],
            "units": "PDF points (72 per inch)",
            "pointsPerFoot": POINTS_PER_FOOT,
            "isEncrypted": document.is_encrypted,
            "pages": [],
        }

        remaining = max_items

        for index in indices:
            page = document[index]
            rect = page.rect

            drawings = []
            for drawing in page.get_drawings():
                if remaining <= 0:
                    break
                described = describe_drawing(drawing, remaining)
                if described is None:
                    continue
                drawings.append(described)
                remaining -= described["itemCount"]

            page_data = {
                "page": index + 1,
                "width": round(rect.width, 4),
                "height": round(rect.height, 4),
                "rotation": page.rotation,
                "drawings": drawings,
                "drawingCount": len(drawings),
                "primitiveCount": sum(d["itemCount"] for d in drawings),
            }

            if include_text:
                # Text with positions is what makes a dimension string usable: the number alone
                # cannot be matched back to the linework it annotates.
                spans = []
                for block in page.get_text("dict")["blocks"]:
                    if block.get("type") != 0:           # 0 == text
                        continue
                    for line in block.get("lines", []):
                        for span in line.get("spans", []):
                            text = span.get("text", "").strip()
                            if not text:
                                continue
                            bbox = span["bbox"]
                            spans.append({
                                "text": text,
                                "size": round(span.get("size", 0), 2),
                                "bbox": {
                                    "x0": round(bbox[0], 4), "y0": round(bbox[1], 4),
                                    "x1": round(bbox[2], 4), "y1": round(bbox[3], 4),
                                },
                            })
                page_data["text"] = spans
                page_data["textSpanCount"] = len(spans)

            result["pages"].append(page_data)

        result["truncated"] = remaining <= 0
        if result["truncated"]:
            result["note"] = (f"Stopped at the {max_items}-primitive cap. Raise maxItems or "
                              "extract fewer pages for a complete result.")

        return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract geometry from a PDF as JSON.")
    parser.add_argument("--pdf", required=True, help="Path to the PDF.")
    parser.add_argument("--pages", help='Pages to read, 1-based, e.g. "1" or "1-3" or "1,4".')
    parser.add_argument("--max-items", type=int, default=20000,
                        help="Cap on geometric primitives returned (default 20000).")
    parser.add_argument("--no-text", action="store_true", help="Skip text extraction.")
    arguments = parser.parse_args()

    try:
        payload = extract(arguments.pdf, arguments.pages, arguments.max_items, not arguments.no_text)
    except (ExtractError, ValueError) as exc:
        # Always answer on stdout: the caller parses stdout and would otherwise see nothing at all.
        json.dump({"ok": False, "error": str(exc)}, sys.stdout)
        return 1

    json.dump(payload, sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
