# Coverage against "GDC Model Health Check - For sites Epoch 2.1"

Google CAD-BIM check set. 95 checks total, but **65 sit in `<--Unused-->` sections** — only the
**30** below are in the live P0/P1/P2/MONITORING structure.

Status key: **Yes** = a named tool answers it. **Rule** = `EVALUATE_RULE` expresses it; the rule
is written once and kept with the project, not built into the server. **No** = still not available.

## P0 — Highest priority

| Check | Type | Status | Tool / rule |
| --- | --- | --- | --- |
| Design Options | built-in | **Yes** | `LIST_DESIGN_OPTIONS` |
| Project Coordinates | built-in | **Yes** | `GET_PROJECT_COORDINATES` |
| Imported DWG file | custom, 3 filters | **Yes** | `GET_CAD_IMPORTS` separates import from link |
| Warning Count | built-in | **Yes** | `GET_MODEL_WARNINGS` |
| Generic Models | custom, 4 filters | **Rule** | category `included` + `exclude` filters for the allowed families |
| In-Place Families | custom, 4 filters | **Yes** | `LIST_IN_PLACE_FAMILIES` |
| Unhosted Family | custom, 4 filters | **Rule** | `apiParameter` `HOST_ID_PARAM` + `hasNoValue` |
| Unhosted Interface Points | custom, 4 filters | **Rule** | same, scoped to the interface-point families |
| Link Model Workset | custom, 3 filters | **Rule** | `read: workset` + `wildCardNoMatch` |
| Revit Links overlay vs Attachment | custom, 3 filters | **Yes** | `LIST_LINKED_MODELS` reports `attachmentType` |

## P1 — High priority

| Check | Type | Status | Tool / rule |
| --- | --- | --- | --- |
| File Name | built-in | **Yes** | `GET_PROJECT_INFO` reports the path; the naming rule is a client-side regex |
| Link Model Name | custom, 3 filters | **Rule** | `read: name` + `wildCard` over the link types |
| Elements in Link Workset | custom, 3 filters | **Rule** | `read: workset` + `apiType` `notEqual` `RevitLinkInstance` |
| Revit Version | built-in | **Yes** | `GET_PROJECT_INFO` |
| File Size | built-in | **Yes** | `GET_MODEL_HEALTH` → `model.fileSizeMb` |
| View Not On Sheets | built-in | **Yes** | `LIST_UNPLACED_VIEWS` |
| Phase Elements | built-in | **Rule** | `read: phaseCreated` + `included`, one rule per phase |
| Linked CAD | built-in | **Yes** | `GET_CAD_IMPORTS` |

## P2 — Medium priority

| Check | Type | Status | Tool / rule |
| --- | --- | --- | --- |
| Link Files not pinned | built-in | **Yes** | `LIST_LINKED_MODELS` → `unpinnedInstanceCount` |
| Views With Hidden Model Elements | built-in | **Yes** | `LIST_VIEWS_WITH_HIDDEN_ELEMENTS` |
| Family Naming Convention | custom, **54 filters** | **Rule** | 54 filters is one rule with 54 entries, or a `none` group |
| Workset Placement | built-in | **Rule** | one rule per category: `read: workset` + `wildCardNoMatch` |

## MONITORING

| Check | Type | Status | Tool / rule |
| --- | --- | --- | --- |
| Line Styles | built-in | **Yes** | `LIST_STYLES` |
| Legends | built-in | **Yes** | `LIST_VIEWS` with `viewType: Legend` |
| Raster Images | built-in | **Yes** | `LIST_IMAGES` |
| Keynotes | built-in | **Yes** | `LIST_KEYNOTES` |
| Worksets Elements Count | built-in | **Yes** | `LIST_WORKSETS` |
| Unused Elements | built-in | **Yes** | `GET_PURGEABLE` |
| Level Naming | custom, 2 filters | **Rule** | `read: name` + `wildCardNoMatch` scoped to Levels |
| Revit Links | custom, 2 filters | **Yes** | `LIST_LINKED_MODELS` |

## Tally

- **20 answered by a named tool**
- **10 expressible as a rule**
- **0 not available**

## Why one engine rather than thirty tools

Across all 95 checks: **68 are `CheckType="Custom"`**, built from 543 filters composed with
`And` / `Or` / `Exclude`.

The filter vocabulary is small and closed:

| Filter reads | Count in the file | `EVALUATE_RULE` `read` |
| --- | --- | --- |
| `Category` (OST_*) | 375 | `category` |
| `Parameter` | 65 | `parameter` |
| `TypeOrInstance` | 51 | `typeOrInstance` |
| `Workset` | 19 | `workset` |
| `APIParameter` | 12 | `apiParameter` |
| `APIType` | 8 | `apiType` |
| `Family` | 7 | `family` |
| `Type` | 5 | `type` |

Every condition in the file maps one-to-one: `Included`, `Equal`, `NotEqual`, `Contains`,
`WildCard` (regex), `WildCardNoMatch`, `HasValue`, `HasNoValue`, `Defined`, `GreaterThan`,
`GreaterOrEqual`, `LessOrEqual`, `Duplicated`, `DoesNotMatchParameter`. So do the operators
(`And` / `Or` / `Exclude`) and the result conditions (`FailMatchingElements` / `FailNoElements` /
`CountAndList`).

The shortfall was never 30 missing tools. It was **one missing capability**, and the reason is
ownership: a stable fact about Revit belongs in a named tool, where a purpose-built handler reports
it better than a generic filter could. A rule out of a company check set belongs in the check set,
because it changes every release and there are hundreds of them.

`EVALUATE_RULE` folds filters strictly left to right with no precedence, which is how a Model
Checker check set behaves. A check transcribed from the XML therefore means the same thing here.
Nested `all` / `any` / `none` groups exist for rules written natively, where explicit grouping
reads better than relying on fold order.

### Worked example

"Every Generic Model must sit on a workset whose name starts with GM-", as one rule:

```json
{
  "name": "Generic Models on a GM- workset",
  "scope": { "categories": ["Generic Models"] },
  "filters": [
    { "read": "workset", "condition": "wildCardNoMatch", "value": "^GM-" }
  ],
  "resultCondition": "failMatchingElements"
}
```

Every element that comes back is a failure, and each carries the workset name it was found on as
evidence.

## Still outstanding

`RUN_CHECK_SET(xmlPath)` — reading a Model Checker `.xml` directly and running every check in it.
The evaluator and the result conditions are in place; what remains is the XML parser and the
section/heading structure the report groups by.
