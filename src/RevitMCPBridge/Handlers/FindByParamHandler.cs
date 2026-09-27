using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Finds elements by parameter name and value.
///
/// Parameter lookup by name cannot be pushed into a Revit filter (names are per-definition, and
/// shared/project parameters are not addressable by a built-in id), so this walks elements and
/// inspects them. That is why a category scope is required unless the caller explicitly asks for
/// a whole-model scan, and why the walk is capped.
/// </summary>
public sealed class FindByParamHandler : IBridgeCommandHandler
{
    /// <summary>Upper bound on elements inspected, so a stray whole-model query cannot hang Revit.</summary>
    private const int MaxElementsScanned = 200_000;

    private enum MatchMode { Equals, Contains, StartsWith, GreaterThan, LessThan, Exists }

    public string Command => Commands.FindByParam;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var parameterName = args.String("parameterName");
        var (limit, offset) = args.Page();
        var mode = ParseMode(args.StringOr("match", "equals"));
        var caseSensitive = args.Bool("caseSensitive");

        var category = args.StringOrNull("category");
        var allCategories = args.Bool("allCategories");

        if (category is null && !allCategories)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                "FIND_BY_PARAM needs a 'category' to scope the search, because parameters are " +
                "matched by walking elements. Pass 'allCategories': true to accept a full-model " +
                "scan instead.");
        }

        // 'exists' is the one mode that needs no value.
        var expected = mode == MatchMode.Exists ? null : args.String("value");

        var collector = category is not null
            ? CategoryResolver.CollectInstances(doc, category)
            : new FilteredElementCollector(doc).WhereElementIsNotElementType();

        var matches = new List<Element>();
        var scanned = 0;
        var truncated = false;

        foreach (var element in collector)
        {
            if (++scanned > MaxElementsScanned) { truncated = true; break; }

            var parameter = element.LookupParameter(parameterName);
            if (parameter is null || !parameter.HasValue) continue;

            if (Matches(parameter, mode, expected, caseSensitive))
                matches.Add(element);
        }

        var page = matches.Skip(offset).Take(limit).Select(element =>
        {
            var entry = Json.ElementRef(element);

            // Echo the matched value: it is the evidence for why this element came back.
            var parameter = element.LookupParameter(parameterName);
            if (parameter is not null)
            {
                entry["matchedValue"] = Json.From(Units.RawValue(parameter));
                entry["matchedDisplayValue"] = Units.DisplayValue(parameter);
            }

            return (JsonNode?)entry;
        });

        var result = Json.Page("elements", page, matches.Count, offset, limit);
        result["parameterName"] = parameterName;
        result["match"] = mode.ToString();
        result["scope"] = category ?? "(all categories)";
        result["elementsScanned"] = scanned;

        if (truncated)
        {
            result["truncated"] = true;
            result["note"] = $"Stopped after scanning {MaxElementsScanned:N0} elements. " +
                             "Narrow the search with 'category' for complete results.";
        }

        return result;
    }

    private static MatchMode ParseMode(string raw) => raw.ToLowerInvariant() switch
    {
        "equals" or "eq" => MatchMode.Equals,
        "contains" => MatchMode.Contains,
        "startswith" => MatchMode.StartsWith,
        "greaterthan" or "gt" => MatchMode.GreaterThan,
        "lessthan" or "lt" => MatchMode.LessThan,
        "exists" => MatchMode.Exists,
        _ => throw new BridgeException(BridgeErrorCode.BadRequest,
            $"'match' must be one of equals, contains, startsWith, greaterThan, lessThan, exists (got '{raw}').")
    };

    private static bool Matches(Parameter parameter, MatchMode mode, string? expected, bool caseSensitive)
    {
        if (mode == MatchMode.Exists) return true;
        if (expected is null) return false;

        // Numeric comparisons work on the stored value; string ones consider both the raw text and
        // Revit's formatted display value, so "8' 6\"" and "8.5" both find the same wall.
        if (mode is MatchMode.GreaterThan or MatchMode.LessThan)
        {
            if (!TryGetNumber(parameter, out var actual)
                || !double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                return false;

            return mode == MatchMode.GreaterThan ? actual > threshold : actual < threshold;
        }

        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        foreach (var candidate in new[] { RawText(parameter), Units.DisplayValue(parameter) })
        {
            if (string.IsNullOrEmpty(candidate)) continue;

            var hit = mode switch
            {
                MatchMode.Equals => string.Equals(candidate, expected, comparison),
                MatchMode.Contains => candidate.Contains(expected, comparison),
                MatchMode.StartsWith => candidate.StartsWith(expected, comparison),
                _ => false
            };

            if (hit) return true;
        }

        return false;
    }

    private static bool TryGetNumber(Parameter parameter, out double value)
    {
        switch (parameter.StorageType)
        {
            case StorageType.Double:
                value = parameter.AsDouble();
                return true;
            case StorageType.Integer:
                value = parameter.AsInteger();
                return true;
            default:
                return double.TryParse(RawText(parameter), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value);
        }
    }

    private static string? RawText(Parameter parameter) =>
        Units.RawValue(parameter) is { } raw
            ? Convert.ToString(raw, CultureInfo.InvariantCulture)
            : null;
}
