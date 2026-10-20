# EVALUATE_RULE — a cookbook

One tool for a project's own standards, instead of a tool per standard. Read-only: nothing is
changed, whatever the rule says.

A rule is a scope, a list of filters, and what a match means.

```json
{
  "name": "Exterior walls must have a fire rating",
  "scope": { "categories": ["Walls"] },
  "filters": [
    { "read": "parameter", "parameter": "Function", "condition": "equal", "value": "Exterior" },
    { "read": "parameter", "parameter": "Fire Rating", "condition": "hasNoValue", "operator": "and" }
  ],
  "resultCondition": "failMatchingElements"
}
```

Every element returned carries `evidence`: what each filter read off it. That is the point —
a list of ids with no reason attached cannot be acted on.

## When not to use it

Use a named tool for a stable fact about Revit itself. `GET_MODEL_WARNINGS`, `GET_PURGEABLE`,
`LIST_LINKED_MODELS`, `GET_PROJECT_COORDINATES` and the rest report those better than a generic
filter could, and they need no rule written first.

Use `EVALUATE_RULE` when the question belongs to a rulebook: a naming convention, a required
parameter, a workset rule, anything out of a BIM execution plan or a company check set. Those
change every release and there are hundreds of them, which is exactly why they should not be
compiled into the server.

## Composition

In a Revit Model Checker check set the operator column marks **where a clause begins**, not how
two neighbouring filters combine. `EVALUATE_RULE` does the same: `or` or `exclude` opens a new
clause, and every `and` filter after it belongs to that clause. The clauses then combine as the
union of the first and the `or` ones, minus the `exclude` ones.

```
[A and, B and, C exclude, D and]   ==   (A AND B) AND NOT (C AND D)
[A and, B and, C or,      D and]   ==   (A AND B)  OR     (C AND D)
```

A list of all-`and` filters is a single clause, so the common case is a plain conjunction.

`operator` is one of:

| | |
| --- | --- |
| `and` (default) | joins the current clause |
| `or` | opens a clause that is unioned in |
| `exclude` | opens a clause that is subtracted |

The first filter's `operator` is ignored — it has nothing to combine with, and a check set stores
one there anyway.

### Why it is clauses and not a fold

This is worth understanding, because the other reading looks just as plausible and silently
reports zero failures. The GDC check set's "Generic Models" check is:

```
And      Category        OST_GenericModel  Included  True
And      TypeOrInstance  Is Element Type   Equal     False
Exclude  Category        OST_GenericModel  Included  True
And      Family          Name              WildCard  (?i)(knock out|interface|void|access|...)
```

As an infix fold that is `((generic AND instance) AND NOT generic) AND name~pattern`, which
matches **nothing at all** — the third filter excludes exactly what the first included. As clauses
it is `(generic AND instance) AND NOT (generic AND name~pattern)`: "report Generic Models except
the ones whose name says they are legitimately generic." That is obviously the intent, and it is
what this engine does.

### Nesting

When clause order is not expressive enough, nest instead of relying on it:

```json
{ "any": [
    { "read": "name", "condition": "wildCard", "value": "^EXT-" },
    { "read": "name", "condition": "wildCard", "value": "^EX-" }
]}
```

`all`, `any` and `none` all take an array of filters and may be nested further. A group carries its
own `operator`, so it joins the clause structure around it like any other entry.

## What a match means

| `resultCondition` | Meaning |
| --- | --- |
| `failMatchingElements` (default) | Every match is a failure. "Find the bad ones." |
| `failNoElements` | Matching nothing is the failure. "There must be at least one of these." |
| `countAndList` | No verdict. Count and list. |

The response carries `passed` (`null` for `countAndList`) and `verdict` in words, so a caller
reporting a check set does not have to re-derive the sense of `failNoElements`.

## Scope

`scope` is required and must be bounded, because a rule is evaluated by walking elements.

| Key | Default | |
| --- | --- | --- |
| `categories` | — | Array of category names. Strongly preferred. |
| `allCategories` | `false` | Whole-model scan. Slow; ask for it deliberately. |
| `includeInstances` | `true` | Test placed elements. |
| `includeTypes` | `false` | Test type definitions too. |
| `viewId` | — | Only what is in that one view. |

A rule about "the walls" means the placed ones, so types are opted into. Turning them on silently
would change every count the engine reports.

## Reads

| `read` | What it reads |
| --- | --- |
| `category` | The element's category name |
| `parameter` | A parameter by the name Revit shows — instance first, then its type |
| `apiParameter` | A `BuiltInParameter` by its API name, e.g. `HOST_ID_PARAM` |
| `typeOrInstance` | `"Type"` or `"Instance"` |
| `workset` | The workset name (null in a non-workshared model) |
| `apiType` | The API class name, e.g. `Wall`, `FamilyInstance` |
| `family` | The family name |
| `type` | The type name |
| `name` | The element's own name |
| `level` | The level name |
| `designOption` | The design option name |
| `elementId` | The element id, as a number |
| `phaseCreated` / `phaseDemolished` | The phase name |

`parameter` follows Revit's own precedence: the instance, then its type. That is the value shown in
the properties palette, so it is the value the rule is about — a check that only looked at the
instance would report every wall in the model as unfilled.

## Conditions

| `condition` | |
| --- | --- |
| `included` | The value is one of the listed values. A comma-separated `value` is split, as in Model Checker. With no value, the same as `hasValue`. |
| `equal` / `notEqual` | Compared as text and, where both sides are numeric, as numbers — so `8.50` and `8.5` are the same value |
| `contains` / `doesNotContain` | |
| `startsWith` / `endsWith` | |
| `wildCard` / `wildCardNoMatch` | A .NET regular expression, e.g. `(?i)^EXT-` |
| `hasValue` | Present and non-empty |
| `hasNoValue` | Empty **or** absent |
| `defined` / `notDefined` | The parameter is on this element at all, filled in or not |
| `greaterThan` / `greaterOrEqual` / `lessThan` / `lessOrEqual` | Numeric |
| `duplicated` / `notDuplicated` | The value is, or is not, shared with another element in scope |
| `matchesParameter` / `doesNotMatchParameter` | Compares against another parameter on the same element; name it in `otherParameter` |

`caseSensitive` defaults to `false` and may be set on the rule or on an individual filter; a filter
inherits the rule's setting.

Two notes worth knowing:

- `hasNoValue` matches an element whose parameter is **absent as well as empty**. When you need
  "the parameter is not on this element", use `notDefined`.
- `duplicated` needs to know what every other element in scope reads, so the scope is walked twice.
  Keep it tight.

## Worked rules

### A naming convention

```json
{
  "name": "Level names follow L## - Description",
  "scope": { "categories": ["Levels"] },
  "filters": [
    { "read": "name", "condition": "wildCardNoMatch", "value": "^L\\d{2} - .+" }
  ]
}
```

### A required parameter, on types rather than instances

```json
{
  "name": "Every door type has a Type Mark",
  "scope": { "categories": ["Doors"], "includeTypes": true, "includeInstances": false },
  "filters": [
    { "read": "parameter", "parameter": "Type Mark", "condition": "hasNoValue" }
  ]
}
```

### A workset rule

```json
{
  "name": "Generic Models live on a GM- workset",
  "scope": { "categories": ["Generic Models"] },
  "filters": [
    { "read": "workset", "condition": "wildCardNoMatch", "value": "^GM-" }
  ]
}
```

### Unhosted families

```json
{
  "name": "Interface points must be hosted",
  "scope": { "categories": ["Generic Models"] },
  "filters": [
    { "read": "family", "condition": "contains", "value": "Interface Point" },
    { "read": "apiParameter", "parameter": "HOST_ID_PARAM", "condition": "hasNoValue", "operator": "and" }
  ]
}
```

### No duplicate room numbers

```json
{
  "name": "Room numbers are unique",
  "scope": { "categories": ["Rooms"] },
  "filters": [
    { "read": "parameter", "parameter": "Number", "condition": "duplicated" }
  ]
}
```

### Two parameters that must agree

```json
{
  "name": "Mark matches Type Mark",
  "scope": { "categories": ["Mechanical Equipment"] },
  "filters": [
    { "read": "parameter", "parameter": "Mark", "condition": "doesNotMatchParameter",
      "otherParameter": "Type Mark" }
  ]
}
```

### Something that must exist

```json
{
  "name": "The model has at least one level above 100'",
  "scope": { "categories": ["Levels"] },
  "filters": [
    { "read": "parameter", "parameter": "Elevation", "condition": "greaterThan", "value": "100" }
  ],
  "resultCondition": "failNoElements"
}
```

Note the unit: Revit stores lengths in decimal feet, and `greaterThan` compares the stored value.

## Limits

- The walk stops after 200,000 elements and says so. Narrow `scope.categories`.
- A regular expression is abandoned after one second and reported as a bad rule rather than
  allowed to hang Revit.
- Results are paged. Pass the returned `cursor` back for the next page.

## Running a whole check set

Transcribe its filter-based checks into a rule file once and keep it with the project.
[`docs/rules/gdc-epoch-2.1.json`](rules/gdc-epoch-2.1.json) is a worked example: all twelve
filter-based checks of a real 95-check set, eight as rules and four noted as better answered by a
named tool. Every rule in that directory is parsed by the engine's own grammar on each build, so a
file cannot rot unnoticed.

Reading the check-set XML directly is **not planned**, and
[`docs/MODEL-CHECKER-COVERAGE.md`](MODEL-CHECKER-COVERAGE.md) explains why. The short version: the
built-in checks are already answered by named tools, the filter-based ones transcribe once, and a
parser would reproduce the check set's own authoring mistakes faithfully and silently — the GDC set
has one.
