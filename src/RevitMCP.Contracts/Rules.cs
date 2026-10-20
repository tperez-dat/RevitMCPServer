using System.Text.Json.Nodes;

namespace RevitMCP.Contracts;

/// <summary>
/// A rule that does not parse. Carried as its own type rather than a bridge error so the MCP
/// server can reject a malformed rule before it reaches Revit, and so the grammar has no
/// dependency on the add-in.
/// </summary>
public sealed class RuleParseException(string message) : Exception(message);

/// <summary>What a filter reads off an element before comparing it.</summary>
public enum ReadKind
{
    /// <summary>The element's category name.</summary>
    Category,
    /// <summary>A parameter looked up by name, on the instance then on its type.</summary>
    Parameter,
    /// <summary>A built-in parameter addressed by its API enum name, e.g. ALL_MODEL_INSTANCE_COMMENTS.</summary>
    ApiParameter,
    /// <summary>"Type" or "Instance" — whether this element is a type definition or a placed one.</summary>
    TypeOrInstance,
    /// <summary>The workset name. Reads as not defined in a model that is not workshared.</summary>
    Workset,
    /// <summary>The element's API class name, e.g. Wall, FamilyInstance, Floor.</summary>
    ApiType,
    /// <summary>The family name.</summary>
    Family,
    /// <summary>The type name.</summary>
    Type,
    /// <summary>The element's own name.</summary>
    Name,
    /// <summary>The name of the element's level.</summary>
    Level,
    /// <summary>The design option name. Reads as not defined for an element in the main model.</summary>
    DesignOption,
    /// <summary>The element id, as a number.</summary>
    ElementId,
    /// <summary>The name of the phase in which the element was created.</summary>
    PhaseCreated,
    /// <summary>The name of the phase in which the element was demolished.</summary>
    PhaseDemolished
}

/// <summary>
/// How a read value is compared. These are the conditions a Revit Model Checker check set uses,
/// named the same way, so a rule written against that vocabulary means the same thing here.
/// </summary>
public enum Condition
{
    /// <summary>The value is one of the listed values. With no value listed, the same as HasValue.</summary>
    Included,
    Equal,
    NotEqual,
    Contains,
    DoesNotContain,
    StartsWith,
    EndsWith,
    /// <summary>A .NET regular expression match.</summary>
    WildCard,
    /// <summary>A .NET regular expression that must NOT match.</summary>
    WildCardNoMatch,
    /// <summary>Present and non-empty.</summary>
    HasValue,
    /// <summary>Absent, or present and empty.</summary>
    HasNoValue,
    /// <summary>The parameter exists on this element at all, whether or not it is filled in.</summary>
    Defined,
    /// <summary>The parameter does not exist on this element.</summary>
    NotDefined,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    /// <summary>More than one element in scope shares this value.</summary>
    Duplicated,
    /// <summary>Exactly one element in scope has this value.</summary>
    NotDuplicated,
    /// <summary>The value differs from the value of another named parameter on the same element.</summary>
    DoesNotMatchParameter,
    /// <summary>The value equals the value of another named parameter on the same element.</summary>
    MatchesParameter
}

/// <summary>
/// Where a clause begins. In a Model Checker check set this column marks structure, not an infix
/// operator: see <see cref="RuleComposition.CombineClauses"/>.
/// </summary>
public enum Join
{
    /// <summary>Joins the clause currently being built.</summary>
    And,
    /// <summary>Opens a clause that is unioned into the result so far.</summary>
    Or,
    /// <summary>Opens a clause that is subtracted from the result so far.</summary>
    Exclude
}

/// <summary>What a match means.</summary>
public enum ResultCondition
{
    /// <summary>Every matching element is a failure. The usual shape: "find the bad ones".</summary>
    FailMatchingElements,
    /// <summary>Matching nothing is the failure. For "there must be at least one of these".</summary>
    FailNoElements,
    /// <summary>No verdict — report the count and the list.</summary>
    CountAndList
}

/// <summary>One node of a rule: either a leaf test or a group of nodes.</summary>
public abstract record RuleNode(Join Join);

/// <summary>A single test against one value read off the element.</summary>
public sealed record LeafFilter(
    Join Join,
    ReadKind Read,
    Condition Condition,
    string? ParameterName,
    IReadOnlyList<string> Values,
    string? OtherParameterName,
    bool CaseSensitive,
    string Label) : RuleNode(Join);

/// <summary>A nested group, for when clause order is not expressive enough.</summary>
public sealed record GroupFilter(Join Join, GroupMode Mode, IReadOnlyList<RuleNode> Children)
    : RuleNode(Join);

public enum GroupMode
{
    /// <summary>Every child must match.</summary>
    All,
    /// <summary>At least one child must match.</summary>
    Any,
    /// <summary>No child may match.</summary>
    None,
    /// <summary>
    /// Children grouped into clauses by their own joins — a Model Checker check set's own
    /// semantics. See <see cref="RuleComposition.CombineClauses"/>.
    /// </summary>
    Fold
}

/// <summary>Which elements the rule is evaluated against.</summary>
public sealed record RuleScope(
    IReadOnlyList<string> Categories,
    bool AllCategories,
    bool IncludeTypes,
    bool IncludeInstances,
    long? ViewId);

/// <summary>A complete, validated rule.</summary>
public sealed record Rule(
    string Name,
    string? Description,
    RuleScope Scope,
    GroupFilter Root,
    ResultCondition ResultCondition);

/// <summary>
/// Turns the JSON a caller sends into a validated <see cref="Rule"/>.
///
/// Every rejection names the offending field and lists the accepted values, because the caller is
/// a language model: a precise message is what lets it correct itself on the next attempt instead
/// of guessing again.
/// </summary>
public static class RuleParser
{
    public static Rule Parse(JsonObject spec)
    {
        var name = Text(spec, "name") ?? "(unnamed rule)";
        var description = Text(spec, "description");

        var filtersNode = spec["filters"]
            ?? throw Bad("a rule needs 'filters': an array of at least one filter.");

        if (filtersNode is not JsonArray filters || filters.Count == 0)
            throw Bad("'filters' must be a non-empty array.");

        var caseSensitive = Flag(spec, "caseSensitive") ?? false;

        var children = new List<RuleNode>();
        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i] is not JsonObject filter)
                throw Bad($"'filters[{i}]' must be an object.");
            children.Add(ParseNode(filter, $"filters[{i}]", caseSensitive));
        }

        return new Rule(
            name,
            description,
            ParseScope(spec["scope"] as JsonObject),
            new GroupFilter(Join.And, GroupMode.Fold, children),
            ParseResultCondition(Text(spec, "resultCondition")));
    }

    private static RuleNode ParseNode(JsonObject node, string path, bool inheritedCase)
    {
        var join = ParseJoin(Text(node, "operator") ?? Text(node, "join"), path);
        var caseSensitive = Flag(node, "caseSensitive") ?? inheritedCase;

        // A group is identified by which of the three group keys it carries.
        foreach (var (key, mode) in new[]
                 {
                     ("all", GroupMode.All), ("any", GroupMode.Any),
                     ("none", GroupMode.None), ("fold", GroupMode.Fold)
                 })
        {
            if (node[key] is not JsonArray group) continue;

            if (group.Count == 0) throw Bad($"'{path}.{key}' must not be empty.");

            var children = new List<RuleNode>();
            for (var i = 0; i < group.Count; i++)
            {
                if (group[i] is not JsonObject child)
                    throw Bad($"'{path}.{key}[{i}]' must be an object.");
                children.Add(ParseNode(child, $"{path}.{key}[{i}]", caseSensitive));
            }

            return new GroupFilter(join, mode, children);
        }

        return ParseLeaf(node, path, join, caseSensitive);
    }

    private static LeafFilter ParseLeaf(JsonObject node, string path, Join join, bool caseSensitive)
    {
        var readText = Text(node, "read") ?? Text(node, "property")
            ?? throw Bad($"'{path}' needs 'read': one of {Names<ReadKind>()}.");

        var read = ParseEnum<ReadKind>(readText, $"{path}.read");

        var conditionText = Text(node, "condition")
            ?? throw Bad($"'{path}' needs 'condition': one of {Names<Condition>()}.");

        var condition = ParseEnum<Condition>(conditionText, $"{path}.condition");

        var parameterName = Text(node, "parameter") ?? Text(node, "parameterName");

        if (read is ReadKind.Parameter or ReadKind.ApiParameter && parameterName is null)
        {
            throw Bad($"'{path}' reads '{read}', so it needs 'parameter' naming which one. " +
                      (read == ReadKind.ApiParameter
                          ? "For apiParameter this is a BuiltInParameter name, e.g. ALL_MODEL_INSTANCE_COMMENTS."
                          : "For parameter this is the name as it appears in Revit, e.g. 'Fire Rating'."));
        }

        var values = ReadValues(node, condition, path);
        var other = Text(node, "otherParameter") ?? Text(node, "compareToParameter");

        if (condition is Condition.DoesNotMatchParameter or Condition.MatchesParameter && other is null)
        {
            throw Bad($"'{path}' uses '{condition}', so it needs 'otherParameter' naming the " +
                      "parameter to compare against.");
        }

        if (RequiresValue(condition) && values.Count == 0)
            throw Bad($"'{path}' uses '{condition}', so it needs 'value' (or 'values').");

        var label = read == ReadKind.Parameter || read == ReadKind.ApiParameter
            ? $"{parameterName} {Camel(condition.ToString())}"
            : $"{Camel(read.ToString())} {Camel(condition.ToString())}";

        return new LeafFilter(join, read, condition, parameterName, values, other, caseSensitive,
            label.Trim());
    }

    /// <summary>
    /// The values to compare against. 'values' is the explicit multi-value form. A single 'value'
    /// is taken verbatim, except for 'included', where a comma-separated list is the convention
    /// Model Checker uses and callers will expect.
    /// </summary>
    private static IReadOnlyList<string> ReadValues(JsonObject node, Condition condition, string path)
    {
        if (node["values"] is JsonArray array)
        {
            var list = new List<string>(array.Count);
            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i]?.ToString();
                if (!string.IsNullOrEmpty(item)) list.Add(item);
            }

            if (list.Count == 0) throw Bad($"'{path}.values' must contain at least one value.");
            return list;
        }

        if (node["value"] is not { } single) return [];

        var text = single.ToString();

        return condition == Condition.Included
            ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [text];
    }

    private static bool RequiresValue(Condition condition) => condition is not (
        Condition.HasValue or Condition.HasNoValue or Condition.Defined or Condition.NotDefined
        or Condition.Duplicated or Condition.NotDuplicated
        or Condition.DoesNotMatchParameter or Condition.MatchesParameter or Condition.Included);

    private static RuleScope ParseScope(JsonObject? scope)
    {
        // A missing scope is not an unbounded one. With no categories and no explicit
        // allCategories the walk would collect nothing, so the rule would report a clean pass
        // against zero elements - the worst possible answer to a compliance question.
        scope ??= new JsonObject();

        var categories = new List<string>();
        if (scope["categories"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node?.ToString() is { Length: > 0 } name) categories.Add(name);
            }
        }
        else if (Text(scope, "category") is { Length: > 0 } one)
        {
            categories.Add(one);
        }

        var all = Flag(scope, "allCategories") ?? false;

        if (categories.Count == 0 && !all)
        {
            throw Bad("'scope' needs 'categories' (an array of category names) to bound the " +
                      "search, because a rule is evaluated by walking elements. Pass " +
                      "'allCategories': true to accept a whole-model scan instead.");
        }

        long? viewId = null;
        if (scope["viewId"] is { } view && long.TryParse(view.ToString(), out var parsed))
            viewId = parsed;

        return new RuleScope(
            categories,
            all,
            IncludeTypes: Flag(scope, "includeTypes") ?? false,
            IncludeInstances: Flag(scope, "includeInstances") ?? true,
            ViewId: viewId);
    }

    private static ResultCondition ParseResultCondition(string? text) =>
        text is null
            ? ResultCondition.FailMatchingElements
            : ParseEnum<ResultCondition>(text, "resultCondition");

    private static Join ParseJoin(string? text, string path) =>
        text is null ? Join.And : ParseEnum<Join>(text, $"{path}.operator");

    // --- small helpers -----------------------------------------------------------------------

    private static T ParseEnum<T>(string text, string field) where T : struct, Enum =>
        Enum.TryParse<T>(text.Replace("_", "").Replace(" ", ""), ignoreCase: true, out var value)
            ? value
            : throw Bad($"'{field}' must be one of {Names<T>()} (got '{text}').");

    private static string Names<T>() where T : struct, Enum =>
        string.Join(", ", Enum.GetNames<T>().Select(Camel));

    private static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static string? Text(JsonObject o, string key) =>
        o[key]?.ToString() is { Length: > 0 } s ? s : null;

    private static bool? Flag(JsonObject o, string key)
    {
        if (o[key] is not { } node) return null;
        return bool.TryParse(node.ToString(), out var value) ? value : null;
    }

    private static RuleParseException Bad(string message) =>
        new($"{Commands.EvaluateRule}: {message}");
}

/// <summary>
/// How a flat filter list combines into one verdict.
///
/// Kept separate, pure, and here rather than in the add-in because this is the part of the engine
/// that is easy to get wrong and impossible to test against a live Revit document. It was wrong
/// once: an earlier version read the operator column as an infix fold, which inverts the meaning of
/// every check in a real check set that uses Exclude.
/// </summary>
public static class RuleComposition
{
    /// <summary>
    /// Combines per-filter results the way a Revit Model Checker check set does.
    ///
    /// The operator column marks where a clause BEGINS, not how two neighbouring filters combine.
    /// An <see cref="Join.Or"/> or <see cref="Join.Exclude"/> filter opens a new clause and every
    /// <see cref="Join.And"/> filter after it belongs to that clause. The clauses then combine as
    /// the union of the first and the Or-clauses, minus the Exclude-clauses.
    ///
    /// A list of all-And filters is a single clause, so it behaves as a plain conjunction.
    /// </summary>
    /// <param name="joins">Each filter's operator, in order.</param>
    /// <param name="matched">Whether each filter matched, in the same order.</param>
    public static bool CombineClauses(IReadOnlyList<Join> joins, IReadOnlyList<bool> matched)
    {
        if (joins.Count != matched.Count)
            throw new ArgumentException("joins and matched must be the same length.", nameof(matched));

        var included = false;
        var seenFirst = false;
        var index = 0;

        while (index < joins.Count)
        {
            var opensWith = joins[index];

            // The clause is this filter plus every And-joined filter following it.
            var end = index + 1;
            while (end < joins.Count && joins[end] == Join.And) end++;

            var clause = true;
            for (var i = index; i < end; i++) clause &= matched[i];

            // The first clause opens the result whatever its own operator says. A check set ignores
            // the leading operator too, and a leading Or or Exclude has nothing to combine with.
            if (!seenFirst)
            {
                included = clause;
                seenFirst = true;
            }
            else
            {
                included = opensWith switch
                {
                    Join.Or => included || clause,
                    Join.Exclude => included && !clause,
                    // Unreachable: an And-joined filter is absorbed into the preceding clause and
                    // can only open one as the very first filter. Kept so a change degrades safely.
                    _ => included && clause
                };
            }

            index = end;
        }

        return included;
    }
}
