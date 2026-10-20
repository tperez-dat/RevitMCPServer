using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>
/// The rule engine: one tool that evaluates a project's own standards, rather than a tool per
/// standard. Read-only, so it works with write mode off.
/// </summary>
[McpServerToolType]
public sealed class RuleTools(ToolGateway gateway)
{
    [McpServerTool(Name = Commands.EvaluateRule)]
    [Description("""
        Evaluates one rule — a composed filter — against the model, and reports which elements
        match, why, and whether the rule passed.

        Use this for a project's OWN standards: naming conventions, required parameters, workset
        rules, duplicate checks, anything out of a company check set or BIM execution plan. It
        speaks the same filter vocabulary as a Revit Model Checker check set, so a check
        transcribed from one means the same thing here.

        Use a named tool instead for a stable fact about Revit itself — GET_MODEL_WARNINGS,
        GET_PURGEABLE, LIST_LINKED_MODELS and the rest report those better than a generic filter.

        Read-only. Nothing is changed, whatever the rule says.

        The rule's shape:

          {
            "name": "Exterior walls must have a fire rating",
            "scope": { "categories": ["Walls"] },
            "filters": [
              { "read": "parameter", "parameter": "Function", "condition": "equal", "value": "Exterior" },
              { "read": "parameter", "parameter": "Fire Rating", "condition": "hasNoValue", "operator": "and" }
            ],
            "resultCondition": "failMatchingElements"
          }

        scope (required) bounds the walk:
          categories      - array of category names, e.g. ["Walls", "Doors"]. Strongly preferred.
          allCategories   - true to scan the whole model. Slow; say so deliberately.
          includeTypes    - also test type definitions (default false)
          includeInstances- test placed elements (default true)
          viewId          - restrict to what is in one view

        filters (required) is an array. Each entry reads one value and tests it:
          read      - category, parameter, apiParameter, typeOrInstance, workset, apiType,
                      family, type, name, level, designOption, elementId, phaseCreated,
                      phaseDemolished
          parameter - required when read is 'parameter' (Revit's own name, e.g. "Fire Rating")
                      or 'apiParameter' (a BuiltInParameter name, e.g. ALL_MODEL_INSTANCE_COMMENTS)
          condition - included, equal, notEqual, contains, doesNotContain, startsWith, endsWith,
                      wildCard, wildCardNoMatch, hasValue, hasNoValue, defined, notDefined,
                      greaterThan, greaterOrEqual, lessThan, lessOrEqual, duplicated,
                      notDuplicated, matchesParameter, doesNotMatchParameter
          value     - what to compare against. 'values' takes an array instead.
                      'included' also accepts a comma-separated list in 'value'.
          operator  - how this filter joins the ones before it: and (default), or, exclude
                      (= and not). Filters fold strictly LEFT TO RIGHT with no precedence,
                      exactly as a Model Checker check set does.
          caseSensitive - default false

        Notes on specific conditions:
          wildCard / wildCardNoMatch - a .NET regular expression, e.g. "(?i)^EXT-".
          hasNoValue  - matches an element whose parameter is empty OR absent. Use 'notDefined'
                        when you need "the parameter is not on this element at all".
          defined     - the parameter exists, filled in or not.
          duplicated  - the value is shared by another element in scope. Needs a second pass over
                        the scope, so keep the scope tight.
          doesNotMatchParameter / matchesParameter - compares against another parameter on the
                        same element; name it in 'otherParameter'.

        Nesting, when left-to-right folding is not enough: in place of a leaf, pass
        { "any": [ ...filters... ] }, { "all": [...] } or { "none": [...] }.

        resultCondition decides what a match MEANS:
          failMatchingElements (default) - every match is a failure. "Find the bad ones."
          failNoElements                 - matching nothing is the failure. "There must be
                                           at least one of these."
          countAndList                   - no verdict; just count and list.

        Returns: passed (true/false, or null for countAndList), verdict in words, matched count,
        elementsScanned, and a page of matching elements. Each element carries 'evidence' — what
        each filter read off it — which is what makes a result actionable rather than a list of ids.

        Results are paged: pass the returned 'cursor' back for the next page.
        """)]
    public Task<string> EvaluateRule(
        CancellationToken ct,
        [Description("The rule as a JSON object: name, scope, filters, resultCondition. See the description.")]
        string rule,
        [Description("Maximum matching elements to return (default 200, max 2000).")]
        int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null,
        [Description("Evidence rows per element (default 6). 0 omits evidence entirely.")]
        int? evidenceLimit = null)
    {
        JsonNode parsed;
        try
        {
            parsed = JsonNode.Parse(rule)
                     ?? throw new ArgumentException("the rule parsed to null.");
        }
        catch (JsonException ex)
        {
            // Returned rather than thrown: the caller is a language model, and a message naming
            // the position of the problem is what lets it fix its own JSON.
            return Task.FromResult(
                $"The 'rule' argument is not valid JSON: {ex.Message}\n\n" +
                "Pass a JSON object, for example:\n" +
                """{"name":"No 'Copy' in type names","scope":{"categories":["Walls"],"includeTypes":true,"includeInstances":false},"filters":[{"read":"name","condition":"contains","value":"Copy"}]}""");
        }

        if (parsed is not JsonObject spec)
            return Task.FromResult("The 'rule' argument must be a JSON object, not an array or a bare value.");

        // Validated here, before the round trip. The grammar lives in the shared contracts, so a
        // malformed rule is answered immediately with the same message Revit would have given —
        // and a caller correcting itself does not have to wait on the pipe to find out.
        try { RuleParser.Parse(spec); }
        catch (RuleParseException ex) { return Task.FromResult(ex.Message); }

        return gateway.CallAsync(Commands.EvaluateRule, ToolGateway.Args(
                ("rule", spec), ("limit", limit), ("cursor", cursor),
                ("evidenceLimit", evidenceLimit)), ct,
            // A whole-model rule with a duplicate condition walks the model twice.
            timeoutMs: 300_000);
    }
}
