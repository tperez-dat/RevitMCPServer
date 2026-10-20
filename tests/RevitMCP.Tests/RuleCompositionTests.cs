using RevitMCP.Contracts;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// How a flat filter list combines.
///
/// This got shipped wrong once — read as an infix fold rather than as clauses — so the cases below
/// are taken from the filter patterns of a real 95-check Model Checker set rather than invented.
/// An infix reading inverts or empties every one of them.
/// </summary>
public class RuleCompositionTests
{
    private static bool Combine(params (Join Join, bool Matched)[] filters) =>
        RuleComposition.CombineClauses(
            filters.Select(f => f.Join).ToList(),
            filters.Select(f => f.Matched).ToList());

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void AllAndIsAPlainConjunction(bool a, bool b, bool expected)
    {
        // The common case must be unaffected by clause handling.
        Assert.Equal(expected, Combine((Join.And, a), (Join.And, b)));
    }

    [Fact]
    public void ASingleFilterIsItsOwnVerdict()
    {
        Assert.True(Combine((Join.And, true)));
        Assert.False(Combine((Join.And, false)));
    }

    [Fact]
    public void TheFirstFiltersOperatorIsIgnored()
    {
        // A check set stores one on every filter including the first, where it means nothing.
        Assert.True(Combine((Join.Or, true)));
        Assert.True(Combine((Join.Exclude, true)));
        Assert.False(Combine((Join.Exclude, false)));
    }

    /// <summary>
    /// The GDC set's "Generic Models" check, which is the case that proves clauses:
    ///
    ///   And      Category        OST_GenericModel  Included  True
    ///   And      TypeOrInstance  Is Element Type   Equal     False
    ///   Exclude  Category        OST_GenericModel  Included  True
    ///   And      Family          Name              WildCard  (?i)(knock out|interface|void|...)
    ///
    /// Meaning: report Generic Model instances, except those whose family name says they are
    /// legitimately generic.
    /// </summary>
    [Theory]
    // generic, instance, (generic again), name matches the allowed pattern  ->  excluded
    [InlineData(true, true, true, true, false)]
    // generic instance whose name does NOT match the allowed pattern       ->  reported
    [InlineData(true, true, true, false, true)]
    // not a generic model at all                                           ->  not reported
    [InlineData(false, true, false, false, false)]
    // a generic model TYPE rather than an instance                         ->  not reported
    [InlineData(true, false, true, false, false)]
    public void ExcludeStartsAClauseThatAbsorbsTheAndAfterIt(
        bool generic, bool instance, bool genericAgain, bool nameAllowed, bool expected)
    {
        var verdict = Combine(
            (Join.And, generic),
            (Join.And, instance),
            (Join.Exclude, genericAgain),
            (Join.And, nameAllowed));

        Assert.Equal(expected, verdict);
    }

    [Fact]
    public void AnInfixReadingOfThatCheckWouldMatchNothing()
    {
        // ((generic AND instance) AND NOT generic) AND name  ==  always false.
        // Stated as a test because it is the whole reason this file exists: a plausible-looking
        // engine that silently reports zero failures for a check that should report many.
        var infix = InfixFold(
            (Join.And, true), (Join.And, true), (Join.Exclude, true), (Join.And, false));

        Assert.False(infix);
        Assert.True(Combine(
            (Join.And, true), (Join.And, true), (Join.Exclude, true), (Join.And, false)));

        static bool InfixFold(params (Join Join, bool Matched)[] filters)
        {
            var accumulated = filters[0].Matched;
            foreach (var (join, matched) in filters.Skip(1))
            {
                accumulated = join switch
                {
                    Join.Or => accumulated || matched,
                    Join.Exclude => accumulated && !matched,
                    _ => accumulated && matched
                };
            }
            return accumulated;
        }
    }

    /// <summary>
    /// The set's "Walls - Workset Check": two independent conditions unioned, not interleaved.
    ///
    ///   And  OST_Walls / instance / type~ext / workset!~ext
    ///   Or   OST_Walls / instance / type~int / workset!~int
    /// </summary>
    [Theory]
    // an exterior wall on a non-exterior workset  -> reported by the first clause
    [InlineData(true, true, false, false, true)]
    // an interior wall on a non-interior workset  -> reported by the second clause
    [InlineData(false, false, true, true, true)]
    // an exterior wall correctly placed            -> not reported
    [InlineData(true, false, false, false, false)]
    // neither                                      -> not reported
    [InlineData(false, false, false, false, false)]
    public void OrStartsAnIndependentClause(
        bool extType, bool extWrongWorkset, bool intType, bool intWrongWorkset, bool expected)
    {
        var verdict = Combine(
            (Join.And, true),                 // OST_Walls
            (Join.And, true),                 // instance
            (Join.And, extType),
            (Join.And, extWrongWorkset),
            (Join.Or, true),                  // OST_Walls
            (Join.And, true),                 // instance
            (Join.And, intType),
            (Join.And, intWrongWorkset));

        Assert.Equal(expected, verdict);
    }

    /// <summary>
    /// The set's view checks chain two Exclude clauses: "views in the EXPORT group whose name does
    /// not say Navis, excluding templates and excluding legends".
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]   // neither a template nor a legend -> reported
    [InlineData(true, false, false)]   // a template                      -> excluded
    [InlineData(false, true, false)]   // a legend                        -> excluded
    [InlineData(true, true, false)]    // both                            -> excluded
    public void SeveralExcludeClausesAllSubtract(bool isTemplate, bool isLegend, bool expected)
    {
        var verdict = Combine(
            (Join.And, true),                 // OST_Views
            (Join.And, true),                 // View Group contains EXPORT
            (Join.And, true),                 // name does not match Navis
            (Join.Exclude, true),             // OST_Views
            (Join.And, isTemplate),
            (Join.Exclude, true),             // OST_Views
            (Join.And, isLegend));

        Assert.Equal(expected, verdict);
    }

    [Fact]
    public void AnExcludeClauseCannotResurrectAnAlreadyFailedRule()
    {
        // Subtracting from nothing leaves nothing, whatever the exclude clause says.
        Assert.False(Combine((Join.And, false), (Join.Exclude, true)));
        Assert.False(Combine((Join.And, false), (Join.Exclude, false)));
    }

    [Fact]
    public void ClausesCombineLeftToRightSoAnOrAfterAnExcludeStillWidens()
    {
        // first=false, exclude ignored against nothing, then Or adds its own clause.
        Assert.True(Combine(
            (Join.And, false), (Join.Exclude, true), (Join.Or, true)));
    }

    [Fact]
    public void MismatchedInputsAreRejectedRatherThanGuessed()
    {
        Assert.Throws<ArgumentException>(() =>
            RuleComposition.CombineClauses([Join.And, Join.Or], [true]));
    }

    [Fact]
    public void AnEmptyListMatchesNothing()
    {
        Assert.False(RuleComposition.CombineClauses([], []));
    }
}
