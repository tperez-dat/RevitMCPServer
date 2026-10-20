using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;
using RevitMCPBridge.Rules;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Evaluates one composed rule against the model.
///
/// This is the generic answer to a class of question the named tools cannot cover: a project's own
/// standards. "Every wall on an exterior workset must have a fire rating", "no type name may
/// contain 'Copy'", "no two rooms may share a number" — those live in a company's check set, change
/// every release, and there are hundreds of them. One engine that speaks the same filter vocabulary
/// as a Revit Model Checker check set answers all of them; a tool per rule answers none of them
/// next year.
///
/// The named tools stay. They answer stable facts about Revit itself, where the question is the
/// same in every project and a purpose-built handler reports it better than a generic filter could.
/// This answers the questions a rulebook owns.
/// </summary>
public sealed class EvaluateRuleHandler : IBridgeCommandHandler
{
    /// <summary>Elements walked before the scan is abandoned, so a stray rule cannot hang Revit.</summary>
    private const int MaxElementsScanned = 200_000;

    public string Command => Commands.EvaluateRule;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;
        var (limit, offset) = args.Page();

        // A rule may arrive as 'rule' or inline at the top level, because both read naturally and
        // a caller should not have to guess which.
        var spec = args.Has("rule") ? args.Object("rule") : InlineSpec(args);

        Rule rule;
        try { rule = RuleParser.Parse(spec); }
        catch (RuleParseException ex)
        {
            // The grammar is validated in the contracts assembly, which knows nothing of the
            // bridge; a parse failure is still a bad request to the caller.
            throw new BridgeException(BridgeErrorCode.BadRequest, ex.Message);
        }

        var evidenceLimit = Math.Max(0, args.IntOrNull("evidenceLimit") ?? 6);
        var stopwatch = Stopwatch.StartNew();

        var evaluator = new RuleEvaluator(rule, doc);
        var candidates = Candidates(doc, rule.Scope);

        var scanned = 0;
        var truncated = false;

        // The duplicate conditions need the whole scope before any element can be judged, so the
        // scan runs twice when one is present. The candidate list is materialised once either way:
        // re-running a collector would not guarantee the same elements twice.
        if (evaluator.NeedsPriming)
        {
            foreach (var element in candidates) evaluator.Prime(element);
        }

        var matches = new List<(Element Element, List<Evidence> Evidence)>();

        foreach (var element in candidates)
        {
            if (++scanned > MaxElementsScanned) { truncated = true; break; }

            var evidence = new List<Evidence>();
            if (evaluator.Evaluate(element, evidence)) matches.Add((element, evidence));
        }

        var page = matches.Skip(offset).Take(limit).Select(match =>
        {
            var entry = Json.ElementRef(match.Element);

            // The evidence is the point. An id with no reason attached cannot be acted on, and a
            // caller that has to re-query to find out why is a caller that will guess instead.
            if (evidenceLimit > 0)
            {
                var array = new JsonArray();
                foreach (var item in match.Evidence.Take(evidenceLimit))
                {
                    array.Add(new JsonObject
                    {
                        ["filter"] = item.Label,
                        ["read"] = item.Read,
                        ["matched"] = item.Matched
                    });
                }

                entry["evidence"] = array;
            }

            return (JsonNode?)entry;
        });

        var result = Json.Page("elements", page, matches.Count, offset, limit);

        result["rule"] = rule.Name;
        if (rule.Description is not null) result["description"] = rule.Description;

        result["resultCondition"] = Camel(rule.ResultCondition.ToString());
        result["matched"] = matches.Count;
        result["elementsScanned"] = scanned;
        result["scope"] = DescribeScope(rule.Scope);
        result["elapsedMs"] = stopwatch.ElapsedMilliseconds;

        // The verdict, so a caller reporting a check set does not have to re-derive it from the
        // count and get the sense of 'failNoElements' backwards.
        result["passed"] = rule.ResultCondition switch
        {
            ResultCondition.FailMatchingElements => matches.Count == 0,
            ResultCondition.FailNoElements => matches.Count > 0,
            _ => null
        };

        result["verdict"] = rule.ResultCondition switch
        {
            ResultCondition.FailMatchingElements => matches.Count == 0
                ? "Passed: nothing matched."
                : $"Failed: {matches.Count} element(s) matched and each is a failure.",
            ResultCondition.FailNoElements => matches.Count > 0
                ? $"Passed: {matches.Count} element(s) matched, which is what was required."
                : "Failed: nothing matched, and at least one match was required.",
            _ => $"{matches.Count} element(s) matched. No pass/fail was requested."
        };

        if (truncated)
        {
            result["truncated"] = true;
            result["note"] = $"Stopped after {MaxElementsScanned:N0} elements, so the result is " +
                             "incomplete. Narrow 'scope.categories'.";
        }

        return result;
    }

    /// <summary>
    /// The elements the rule is evaluated against, materialised so a two-pass rule sees exactly the
    /// same set both times.
    /// </summary>
    private static List<Element> Candidates(Document doc, RuleScope scope)
    {
        var view = scope.ViewId is { } id
            ? doc.GetElement(new ElementId(id)) as View
              ?? throw new BridgeException(BridgeErrorCode.NotFound,
                  $"No view with id {id} exists in this document.")
            : null;

        var elements = new List<Element>();
        var seen = new HashSet<long>();

        void Collect(FilteredElementCollector collector)
        {
            foreach (var element in collector)
            {
                if (seen.Add(element.Id.Value)) elements.Add(element);
            }
        }

        FilteredElementCollector Base() =>
            view is null
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, view.Id);

        if (scope.AllCategories)
        {
            if (scope.IncludeInstances) Collect(Base().WhereElementIsNotElementType());
            if (scope.IncludeTypes) Collect(Base().WhereElementIsElementType());
        }
        else
        {
            foreach (var name in scope.Categories)
            {
                var category = CategoryResolver.Resolve(doc, name);
                var filter = new ElementCategoryFilter(category.Id);

                if (scope.IncludeInstances)
                    Collect(Base().WhereElementIsNotElementType().WherePasses(filter));

                if (scope.IncludeTypes)
                    Collect(Base().WhereElementIsElementType().WherePasses(filter));
            }
        }

        // Stable order, so paging is reproducible and a truncated scan always takes the same slice.
        return elements.OrderBy(e => e.Id.Value).ToList();
    }

    private static JsonObject DescribeScope(RuleScope scope)
    {
        var categories = new JsonArray();
        foreach (var name in scope.Categories) categories.Add(name);

        return new JsonObject
        {
            ["categories"] = categories,
            ["allCategories"] = scope.AllCategories,
            ["includeInstances"] = scope.IncludeInstances,
            ["includeTypes"] = scope.IncludeTypes,
            ["viewId"] = scope.ViewId
        };
    }

    /// <summary>
    /// Accepts the rule written at the top level of the arguments rather than nested under 'rule'.
    /// Only the rule's own keys are carried over, so paging arguments cannot be mistaken for part
    /// of the rule.
    /// </summary>
    private static JsonObject InlineSpec(ArgReader args)
    {
        var spec = new JsonObject();

        foreach (var key in new[]
                 {
                     "name", "description", "scope", "filters", "resultCondition", "caseSensitive"
                 })
        {
            if (args.Has(key)) spec[key] = args.Raw(key)?.DeepClone();
        }

        if (spec["filters"] is null)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"{Commands.EvaluateRule} needs a rule: either 'rule' as an object, or 'filters' " +
                "and 'scope' passed directly. See the tool description for the shape.");
        }

        return spec;
    }

    private static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
