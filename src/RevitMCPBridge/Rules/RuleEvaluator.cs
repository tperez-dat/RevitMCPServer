using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Rules;

/// <summary>Why one element matched, or did not: one entry per leaf filter that was consulted.</summary>
public sealed record Evidence(string Label, string? Read, bool Matched);

/// <summary>
/// Evaluates a parsed <see cref="Rule"/> against elements.
///
/// Two design points worth stating. First, a flat filter list folds strictly left to right, which
/// is how a Revit Model Checker check set behaves — a rule transcribed from one must mean the same
/// thing here, or the engine is worse than useless. Nested groups exist for rules written natively,
/// where explicit grouping is clearer than relying on fold order.
///
/// Second, two conditions cannot be decided one element at a time: <c>duplicated</c> needs to know
/// what every other element in scope reads, so evaluation is two passes. <see cref="Prime"/> runs
/// first over the whole scope, then <see cref="Evaluate"/> decides.
/// </summary>
public sealed class RuleEvaluator
{
    /// <summary>
    /// A caller-supplied regex is untrusted input running on Revit's own thread, where a
    /// pathological pattern would hang the application rather than just the request.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly Rule _rule;
    private readonly ElementReader _reader;
    private readonly Dictionary<LeafFilter, Regex> _regexes = [];

    /// <summary>Per-filter value distribution, for the conditions that need the whole scope.</summary>
    private readonly Dictionary<LeafFilter, Dictionary<string, int>> _distributions = [];

    private readonly List<LeafFilter> _needDistribution = [];

    public RuleEvaluator(Rule rule, Document doc)
    {
        _rule = rule;
        _reader = new ElementReader(doc);

        foreach (var leaf in Leaves(rule.Root))
        {
            if (leaf.Condition is Condition.WildCard or Condition.WildCardNoMatch)
                _regexes[leaf] = CompileRegex(leaf);

            if (leaf.Condition is Condition.Duplicated or Condition.NotDuplicated)
            {
                _needDistribution.Add(leaf);
                _distributions[leaf] = new Dictionary<string, int>(
                    leaf.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>True when a first pass over the scope is needed before any verdict is valid.</summary>
    public bool NeedsPriming => _needDistribution.Count > 0;

    /// <summary>First pass: record what each element reads, for the duplicate conditions.</summary>
    public void Prime(Element element)
    {
        foreach (var leaf in _needDistribution)
        {
            var value = _reader.Read(element, leaf.Read, leaf.ParameterName);
            if (value.Best is not { Length: > 0 } text) continue;

            var counts = _distributions[leaf];
            counts[text] = counts.TryGetValue(text, out var n) ? n + 1 : 1;
        }
    }

    /// <summary>Second pass: does this element match the rule, and on what evidence.</summary>
    public bool Evaluate(Element element, List<Evidence> evidence) =>
        EvaluateNode(_rule.Root, element, evidence);

    private bool EvaluateNode(RuleNode node, Element element, List<Evidence> evidence) =>
        node switch
        {
            LeafFilter leaf => EvaluateLeaf(leaf, element, evidence),
            GroupFilter group => EvaluateGroup(group, element, evidence),
            _ => false
        };

    private bool EvaluateGroup(GroupFilter group, Element element, List<Evidence> evidence)
    {
        switch (group.Mode)
        {
            case GroupMode.All:
                // Short-circuits, so evidence lists what was actually consulted rather than
                // implying every filter was checked.
                foreach (var child in group.Children)
                    if (!EvaluateNode(child, element, evidence)) return false;
                return true;

            case GroupMode.Any:
                foreach (var child in group.Children)
                    if (EvaluateNode(child, element, evidence)) return true;
                return false;

            case GroupMode.None:
                foreach (var child in group.Children)
                    if (EvaluateNode(child, element, evidence)) return false;
                return true;

            default:
                return EvaluateFold(group, element, evidence);
        }
    }

    /// <summary>
    /// Model Checker's own composition, which is NOT an infix fold.
    ///
    /// In a check set the operator column marks where a clause begins, not how two neighbouring
    /// filters combine. An `Or` or `Exclude` filter starts a new clause; every `And` filter after
    /// it belongs to that clause. The clauses then combine as a union of the `Or` ones minus the
    /// `Exclude` ones:
    ///
    ///     first clause OR (or-clause) OR ... AND NOT (exclude-clause) AND NOT ...
    ///
    /// The GDC check set makes the difference plain. Its "Generic Models" check reads:
    ///
    ///     And      Category        OST_GenericModel  Included  True
    ///     And      TypeOrInstance  Is Element Type   Equal     False
    ///     Exclude  Category        OST_GenericModel  Included  True
    ///     And      Family          Name              WildCard  (?i)(knock out|interface|void|...)
    ///
    /// Read as an infix fold that is `((generic AND instance) AND NOT generic) AND name~pattern`,
    /// which matches nothing at all. Read as clauses it is
    /// `(generic AND instance) AND NOT (generic AND name~pattern)` — "report Generic Models except
    /// the ones whose name says they are legitimately generic", which is obviously the intent.
    ///
    /// A rule whose filters are all `And` behaves identically either way, so the common case is
    /// unaffected.
    /// </summary>
    private bool EvaluateFold(GroupFilter group, Element element, List<Evidence> evidence)
    {
        // Every filter is evaluated rather than short-circuited, so the evidence explains the whole
        // rule instead of stopping at the first miss, and so the clause combination below sees a
        // complete picture.
        var joins = new Join[group.Children.Count];
        var matched = new bool[group.Children.Count];

        for (var i = 0; i < group.Children.Count; i++)
        {
            joins[i] = group.Children[i].Join;
            matched[i] = EvaluateNode(group.Children[i], element, evidence);
        }

        return RuleComposition.CombineClauses(joins, matched);
    }

    private bool EvaluateLeaf(LeafFilter leaf, Element element, List<Evidence> evidence)
    {
        var value = _reader.Read(element, leaf.Read, leaf.ParameterName);
        var matched = Test(leaf, value, element);

        evidence.Add(new Evidence(leaf.Label, Describe(value), matched));
        return matched;
    }

    private static string? Describe(ReadValue value) =>
        !value.Defined ? "(not defined)"
        : !value.HasValue ? "(empty)"
        : value.DisplayText is { Length: > 0 } display && display != value.Text
            ? $"{value.Text} ({display})"
            : value.Best;

    private bool Test(LeafFilter leaf, ReadValue value, Element element)
    {
        var comparison = leaf.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        switch (leaf.Condition)
        {
            case Condition.Defined:
                return value.Defined;
            case Condition.NotDefined:
                return !value.Defined;
            case Condition.HasValue:
                return value.HasValue;
            case Condition.HasNoValue:
                // An undefined parameter has no value, which is what the condition asks. The
                // distinction is available separately through notDefined.
                return !value.HasValue;

            case Condition.Included:
                // With no values listed this is "has a value", which is how a Model Checker
                // category filter with no value behaves.
                return leaf.Values.Count == 0
                    ? value.HasValue
                    : value.Texts().Any(t => leaf.Values.Any(v => string.Equals(t, v, comparison)));

            case Condition.Equal:
                return value.HasValue && AnyValue(leaf, v =>
                    value.Texts().Any(t => string.Equals(t, v, comparison))
                    || NumbersEqual(value, v));

            case Condition.NotEqual:
                // An element with no value is not equal to anything, so it matches.
                return !value.HasValue || !AnyValue(leaf, v =>
                    value.Texts().Any(t => string.Equals(t, v, comparison))
                    || NumbersEqual(value, v));

            case Condition.Contains:
                return value.HasValue && AnyValue(leaf, v =>
                    value.Texts().Any(t => t.Contains(v, comparison)));

            case Condition.DoesNotContain:
                return !value.HasValue || !AnyValue(leaf, v =>
                    value.Texts().Any(t => t.Contains(v, comparison)));

            case Condition.StartsWith:
                return value.HasValue && AnyValue(leaf, v =>
                    value.Texts().Any(t => t.StartsWith(v, comparison)));

            case Condition.EndsWith:
                return value.HasValue && AnyValue(leaf, v =>
                    value.Texts().Any(t => t.EndsWith(v, comparison)));

            case Condition.WildCard:
                return value.HasValue && MatchesRegex(leaf, value);

            case Condition.WildCardNoMatch:
                return !value.HasValue || !MatchesRegex(leaf, value);

            case Condition.GreaterThan:
            case Condition.GreaterOrEqual:
            case Condition.LessThan:
            case Condition.LessOrEqual:
                return Compare(leaf, value);

            case Condition.Duplicated:
                return CountInScope(leaf, value) > 1;

            case Condition.NotDuplicated:
                return value.HasValue && CountInScope(leaf, value) == 1;

            case Condition.MatchesParameter:
                return CompareToOther(leaf, value, element, wantEqual: true, comparison);

            case Condition.DoesNotMatchParameter:
                return CompareToOther(leaf, value, element, wantEqual: false, comparison);

            default:
                return false;
        }
    }

    private static bool AnyValue(LeafFilter leaf, Func<string, bool> test) =>
        leaf.Values.Any(test);

    /// <summary>
    /// Compares numerically where both sides are numbers, so "8.50" and "8.5" are the same value.
    /// Revit stores lengths as doubles, and string equality on those is a coin toss.
    /// </summary>
    private static bool NumbersEqual(ReadValue value, string expected) =>
        value.Number is { } actual
        && double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var want)
        && Math.Abs(actual - want) < 1e-9;

    private bool MatchesRegex(LeafFilter leaf, ReadValue value)
    {
        if (!_regexes.TryGetValue(leaf, out var regex)) return false;

        try
        {
            return value.Texts().Any(regex.IsMatch);
        }
        catch (RegexMatchTimeoutException)
        {
            // Reported as a rule failure, not an element failure: the pattern is the problem.
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"The pattern for '{leaf.Label}' took longer than {RegexTimeout.TotalSeconds:0}s " +
                "to evaluate and was abandoned. Simplify it — nested quantifiers are the usual cause.");
        }
    }

    private static bool Compare(LeafFilter leaf, ReadValue value)
    {
        if (value.Number is not { } actual) return false;

        foreach (var text in leaf.Values)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                continue;

            var hit = leaf.Condition switch
            {
                Condition.GreaterThan => actual > threshold,
                Condition.GreaterOrEqual => actual >= threshold,
                Condition.LessThan => actual < threshold,
                Condition.LessOrEqual => actual <= threshold,
                _ => false
            };

            if (hit) return true;
        }

        return false;
    }

    private int CountInScope(LeafFilter leaf, ReadValue value)
    {
        if (value.Best is not { Length: > 0 } text) return 0;
        return _distributions.TryGetValue(leaf, out var counts) && counts.TryGetValue(text, out var n)
            ? n
            : 0;
    }

    private bool CompareToOther(LeafFilter leaf, ReadValue value, Element element, bool wantEqual,
        StringComparison comparison)
    {
        var other = _reader.Read(element, ReadKind.Parameter, leaf.OtherParameterName);

        // Two empties are equal; one empty and one filled are not.
        if (!value.HasValue || !other.HasValue)
            return wantEqual ? value.HasValue == other.HasValue : value.HasValue != other.HasValue;

        var equal = value.Texts().Any(a => other.Texts().Any(b => string.Equals(a, b, comparison)))
                    || (value.Number is { } x && other.Number is { } y && Math.Abs(x - y) < 1e-9);

        return wantEqual == equal;
    }

    private static Regex CompileRegex(LeafFilter leaf)
    {
        if (leaf.Values.Count != 1)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{leaf.Label}' is a pattern match, so it takes exactly one 'value' — the " +
                "regular expression.");
        }

        var options = RegexOptions.CultureInvariant;
        if (!leaf.CaseSensitive) options |= RegexOptions.IgnoreCase;

        try
        {
            return new Regex(leaf.Values[0], options, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{leaf.Label}' has an invalid regular expression: {ex.Message}");
        }
    }

    /// <summary>Every leaf in the tree, so set-up work happens once rather than per element.</summary>
    public static IEnumerable<LeafFilter> Leaves(RuleNode node) => node switch
    {
        LeafFilter leaf => [leaf],
        GroupFilter group => group.Children.SelectMany(Leaves),
        _ => []
    };
}
