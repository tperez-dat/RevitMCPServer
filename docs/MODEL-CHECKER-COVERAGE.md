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

## How the check set's filters map onto `EVALUATE_RULE`

Two translations matter, and both were got wrong on the first attempt here:

**The operator column marks clauses, not infix operators.** `Or` and `Exclude` open a new clause
that absorbs the `And` filters following it. Read as an infix fold, this check set's "Generic
Models" check means `((generic AND instance) AND NOT generic) AND name~pattern` — which matches
nothing. Read as clauses it means `(generic AND instance) AND NOT (generic AND name~pattern)`,
which is the evident intent. `EVALUATE_RULE` implements clauses; see `docs/RULES.md`.

**`Category` + `Included` filters joined by `And` are a union, not an intersection.** The
"Family Naming Convention" check joins 50 of them with `And`; nothing is in 50 categories at once,
so they plainly mean "in any of these". In `EVALUATE_RULE` that union is `scope.categories`, which
is why a 54-filter check becomes a 3-filter rule.

## The transcription

[`docs/rules/gdc-epoch-2.1.json`](rules/gdc-epoch-2.1.json) holds all twelve filter-based checks:
eight as rules, four noted as better answered by a named tool. Every rule in that file is parsed by
the engine's own grammar on each build, so it cannot rot silently.

Transcribing by hand rather than parsing the XML was deliberate, and the file itself shows why: the
live **Link Model Name** check stores its naming test as `Condition="WildCard"` — a positive match
— while its `ResultCondition` is `FailMatchingElements` and its failure message reads *"the
following link models don't meet our model naming convention"*. As written it reports every
correctly named link and misses the wrong ones. A parser would reproduce that faithfully and
silently. A human reading the check noticed it. It is transcribed as `wildCardNoMatch` with the
discrepancy recorded in the rule's description.

## Do you need RUN_CHECK_SET?

Probably not, and it is no longer planned.

The 18 built-in checks are answered by named tools, which report them better than a generic filter
could — `GET_MODEL_HEALTH` runs all of them in one call. The 12 filter-based checks are transcribed
once, above. What an XML parser would add is not running the check set; it is *re-reading* the check
set each time, and that buys little against the cost of reimplementing Autodesk's semantics for 95
checks and owning the fidelity risk forever. Where the two disagreed, there would be two sources of
truth and no way to tell which was right.

Keep the BIM Interoperability Tools add-in for the report the project contractually expects. Use
the MCP server for the part Model Checker cannot do: handing back element ids you can select,
isolate and fix, and answering follow-up questions without re-running anything.

One practical note if automating Model Checker itself was ever the plan: its API is **no longer
supported** as of Interoperability Tools v10, which ships with Revit 2026. Version 9.2 keeps it for
Revit 2022–2025. This project targets 2025–2027, so the add-in cannot be driven programmatically
across that whole range.
