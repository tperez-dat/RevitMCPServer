# Coverage against "GDC Model Health Check - For sites Epoch 2.1"

Google CAD-BIM check set. 95 checks total, but **65 sit in `<--Unused-->` sections** — only the
**30** below are in the live P0/P1/P2/MONITORING structure.

Status key: **Yes** = a tool answers it today. **Data** = we return the data but not the
pass/fail rule. **No** = not available.

## P0 — Highest priority

| Check | Type | Status | Tool / gap |
| --- | --- | --- | --- |
| Design Options | built-in | **Yes** | `LIST_DESIGN_OPTIONS` |
| Project Coordinates | built-in | **No** | survey point, project base point, shared coordinates |
| Imported DWG file | custom, 3 filters | **Yes** | `GET_CAD_IMPORTS` separates import from link |
| Warning Count | built-in | **Yes** | `GET_MODEL_WARNINGS` |
| Generic Models | custom, 4 filters | **Data** | `LIST_ELEMENTS` gives the set; the exclusions are rule logic |
| In-Place Families | custom, 4 filters | **Yes** | `LIST_IN_PLACE_FAMILIES` |
| Unhosted Family | custom, 4 filters | **No** | needs a host-is-null test |
| Unhosted Interface Points | custom, 4 filters | **No** | same |
| Link Model Workset | custom, 3 filters | **Data** | workset name is a filter criterion we cannot express |
| Revit Links overlay vs Attachment | custom, 3 filters | **Yes** | `LIST_LINKED_MODELS` reports `attachmentType` |

## P1 — High priority

| Check | Type | Status | Tool / gap |
| --- | --- | --- | --- |
| File Name | built-in | **Data** | path is reported; the naming rule is regex |
| Link Model Name | custom, 3 filters | **Data** | names reported; convention test is regex |
| Elements in Link Workset | custom, 3 filters | **Data** | rule logic |
| Revit Version | built-in | **Yes** | `GET_PROJECT_INFO` |
| File Size | built-in | **Yes** | `GET_MODEL_HEALTH` → `model.fileSizeMb` |
| View Not On Sheets | built-in | **Yes** | `LIST_UNPLACED_VIEWS` |
| Phase Elements | built-in | **Data** | `LIST_PHASES` lists phases; no per-phase element count yet |
| Linked CAD | built-in | **Yes** | `GET_CAD_IMPORTS` |

## P2 — Medium priority

| Check | Type | Status | Tool / gap |
| --- | --- | --- | --- |
| Link Files not pinned | built-in | **No** | `Pinned` on link instances — small addition |
| Views With Hidden Model Elements | built-in | **No** | per-view hidden-element overrides |
| Family Naming Convention | custom, **54 filters** | **No** | a regex matrix; rule engine or nothing |
| Workset Placement | built-in | **Data** | category-to-workset mapping rules |

## MONITORING

| Check | Type | Status | Tool / gap |
| --- | --- | --- | --- |
| Line Styles | built-in | **Yes** | `LIST_STYLES` |
| Legends | built-in | **Yes** | `LIST_VIEWS` with `viewType: Legend` |
| Raster Images | built-in | **Yes** | `LIST_IMAGES` |
| Keynotes | built-in | **No** | keynote table and keynoted elements |
| Worksets Elements Count | built-in | **Yes** | `LIST_WORKSETS` (element counts added this session) |
| Unused Elements | built-in | **Yes** | `GET_PURGEABLE` |
| Level Naming | custom, 2 filters | **Data** | names reported; convention test is regex |
| Revit Links | custom, 2 filters | **Yes** | `LIST_LINKED_MODELS` |

## Tally

- **14 fully covered**
- **7 data present, rule logic missing**
- **9 not available**

## What the gap actually is

Across all 95 checks: **68 are `CheckType="Custom"`**, built from 543 filters composed with
`And` / `Or` / `Exclude`.

The filter vocabulary is small and closed:

| Filter reads | Count |
| --- | --- |
| `Category` (OST_*) | 375 |
| `Parameter` | 65 |
| `TypeOrInstance` | 51 |
| `Workset` | 19 |
| `APIParameter` | 12 |
| `APIType` | 8 |
| `Family` | 7 |
| `Type` | 5 |

Conditions: `Included`, `Equal`, `NotEqual`, `Contains`, `WildCard` (regex), `WildCardNoMatch`,
`HasValue`, `HasNoValue`, `Defined`, `GreaterThan`, `GreaterOrEqual`, `LessOrEqual`, `Duplicated`,
`DoesNotMatchParameter`.

So the shortfall is **not 30 missing tools. It is one missing capability: a rule evaluator.**

`FIND_BY_PARAM` is a single filter with no boolean composition, no regex, and no access to workset,
family or type-vs-instance as criteria. Everything in the "Data" column above is blocked on exactly
that.
