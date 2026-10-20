using System.Text.Json.Nodes;
using RevitMCP.Contracts;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// The rule grammar. This is the one part of the engine that can be tested off Revit, and it is
/// also the part a language model interacts with directly, so what a malformed rule is told
/// matters as much as what a valid one parses to.
/// </summary>
public class RuleParserTests
{
    private static JsonObject Spec(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonObject Minimal(string filters, string scope = """{"categories":["Walls"]}""") =>
        Spec($$"""{"name":"r","scope":{{scope}},"filters":{{filters}}}""");

    [Fact]
    public void ParsesAFlatRule()
    {
        var rule = RuleParser.Parse(Minimal("""
            [{"read":"parameter","parameter":"Fire Rating","condition":"hasNoValue"}]
            """));

        Assert.Equal("r", rule.Name);
        Assert.Equal(ResultCondition.FailMatchingElements, rule.ResultCondition);
        Assert.Equal(["Walls"], rule.Scope.Categories);

        var leaf = Assert.IsType<LeafFilter>(Assert.Single(rule.Root.Children));
        Assert.Equal(ReadKind.Parameter, leaf.Read);
        Assert.Equal(Condition.HasNoValue, leaf.Condition);
        Assert.Equal("Fire Rating", leaf.ParameterName);
    }

    [Fact]
    public void FlatFiltersFoldLeftToRight()
    {
        // Model Checker's own semantics, and the reason a transcribed check means the same thing
        // here. The first filter's operator is ignored.
        var rule = RuleParser.Parse(Minimal("""
            [{"read":"name","condition":"hasValue","operator":"or"},
             {"read":"name","condition":"contains","value":"x","operator":"exclude"}]
            """));

        Assert.Equal(GroupMode.Fold, rule.Root.Mode);
        Assert.Collection(rule.Root.Children,
            first => Assert.Equal(Join.Or, first.Join),
            second => Assert.Equal(Join.Exclude, second.Join));
    }

    [Theory]
    [InlineData("any", GroupMode.Any)]
    [InlineData("all", GroupMode.All)]
    [InlineData("none", GroupMode.None)]
    public void ParsesNestedGroups(string key, GroupMode expected)
    {
        var rule = RuleParser.Parse(Minimal($$"""
            [{"{{key}}":[{"read":"name","condition":"contains","value":"a"},
                         {"read":"name","condition":"contains","value":"b"}]}]
            """));

        var group = Assert.IsType<GroupFilter>(Assert.Single(rule.Root.Children));
        Assert.Equal(expected, group.Mode);
        Assert.Equal(2, group.Children.Count);
    }

    [Fact]
    public void IncludedSplitsACommaSeparatedValue()
    {
        // The convention a Model Checker category filter uses, so a transcribed one works.
        var rule = RuleParser.Parse(Minimal("""
            [{"read":"category","condition":"included","value":"Walls, Floors ,Doors"}]
            """));

        var leaf = Assert.IsType<LeafFilter>(rule.Root.Children[0]);
        Assert.Equal(["Walls", "Floors", "Doors"], leaf.Values);
    }

    [Fact]
    public void ASingleValueIsNotSplitForOtherConditions()
    {
        // A type name legitimately contains a comma, and splitting it would silently change the rule.
        var rule = RuleParser.Parse(Minimal("""
            [{"read":"name","condition":"equal","value":"Wall, Exterior"}]
            """));

        Assert.Equal(["Wall, Exterior"], Assert.IsType<LeafFilter>(rule.Root.Children[0]).Values);
    }

    [Fact]
    public void ValuesArrayIsHonouredOverValue()
    {
        var rule = RuleParser.Parse(Minimal("""
            [{"read":"name","condition":"equal","values":["a","b"],"value":"ignored"}]
            """));

        Assert.Equal(["a", "b"], Assert.IsType<LeafFilter>(rule.Root.Children[0]).Values);
    }

    [Fact]
    public void CaseSensitivityIsInheritedByNestedFilters()
    {
        var rule = RuleParser.Parse(Spec("""
            {"name":"r","caseSensitive":true,"scope":{"categories":["Walls"]},
             "filters":[{"all":[{"read":"name","condition":"contains","value":"x"}]}]}
            """));

        var group = Assert.IsType<GroupFilter>(rule.Root.Children[0]);
        Assert.True(Assert.IsType<LeafFilter>(group.Children[0]).CaseSensitive);
    }

    [Fact]
    public void ConditionAndReadNamesAreForgivingAboutSpellingStyle()
    {
        var rule = RuleParser.Parse(Minimal("""
            [{"read":"API_Parameter","parameter":"ALL_MODEL_INSTANCE_COMMENTS",
              "condition":"Has No Value"}]
            """));

        var leaf = Assert.IsType<LeafFilter>(rule.Root.Children[0]);
        Assert.Equal(ReadKind.ApiParameter, leaf.Read);
        Assert.Equal(Condition.HasNoValue, leaf.Condition);
    }

    [Fact]
    public void ScopeMustBeBounded()
    {
        // A whole-model walk has to be asked for, not arrived at by omission.
        var ex = Assert.Throws<RuleParseException>(() => RuleParser.Parse(Spec("""
            {"name":"r","scope":{},"filters":[{"read":"name","condition":"hasValue"}]}
            """)));

        Assert.Contains("allCategories", ex.Message);
    }

    [Fact]
    public void AWholeModelScanIsAllowedWhenAskedFor()
    {
        var rule = RuleParser.Parse(Minimal(
            """[{"read":"name","condition":"hasValue"}]""",
            """{"allCategories":true}"""));

        Assert.True(rule.Scope.AllCategories);
    }

    [Fact]
    public void AMissingScopeIsRejectedRatherThanDefaultingToTheWholeModel()
    {
        Assert.Throws<RuleParseException>(() => RuleParser.Parse(Spec("""
            {"name":"r","filters":[{"read":"name","condition":"hasValue"}]}
            """)));
    }

    [Fact]
    public void AParameterReadWithoutAParameterNameIsRejected()
    {
        var ex = Assert.Throws<RuleParseException>(() => RuleParser.Parse(Minimal("""
            [{"read":"parameter","condition":"hasValue"}]
            """)));

        Assert.Contains("'parameter'", ex.Message);
    }

    [Fact]
    public void AConditionNeedingAValueIsRejectedWithoutOne()
    {
        var ex = Assert.Throws<RuleParseException>(() => RuleParser.Parse(Minimal("""
            [{"read":"name","condition":"contains"}]
            """)));

        Assert.Contains("value", ex.Message);
    }

    [Theory]
    [InlineData("hasValue")]
    [InlineData("hasNoValue")]
    [InlineData("defined")]
    [InlineData("notDefined")]
    [InlineData("duplicated")]
    [InlineData("notDuplicated")]
    [InlineData("included")]
    public void ConditionsThatNeedNoValueParseWithoutOne(string condition)
    {
        var rule = RuleParser.Parse(Minimal($$"""[{"read":"name","condition":"{{condition}}"}]"""));
        Assert.Single(rule.Root.Children);
    }

    [Fact]
    public void ComparingToAnotherParameterNeedsThatParameterNamed()
    {
        var ex = Assert.Throws<RuleParseException>(() => RuleParser.Parse(Minimal("""
            [{"read":"parameter","parameter":"Mark","condition":"doesNotMatchParameter"}]
            """)));

        Assert.Contains("otherParameter", ex.Message);
    }

    [Fact]
    public void AnUnknownConditionListsTheValidOnes()
    {
        // The caller is a language model; the message is what lets it correct itself.
        var ex = Assert.Throws<RuleParseException>(() => RuleParser.Parse(Minimal("""
            [{"read":"name","condition":"sortOfEquals","value":"x"}]
            """)));

        Assert.Contains("wildCard", ex.Message);
        Assert.Contains("hasNoValue", ex.Message);
    }

    [Fact]
    public void AnUnknownReadListsTheValidOnes()
    {
        var ex = Assert.Throws<RuleParseException>(() => RuleParser.Parse(Minimal("""
            [{"read":"colour","condition":"hasValue"}]
            """)));

        Assert.Contains("workset", ex.Message);
        Assert.Contains("apiParameter", ex.Message);
    }

    [Fact]
    public void EmptyFiltersAreRejected()
    {
        Assert.Throws<RuleParseException>(() => RuleParser.Parse(Minimal("[]")));
    }

    [Theory]
    [InlineData("failMatchingElements", ResultCondition.FailMatchingElements)]
    [InlineData("failNoElements", ResultCondition.FailNoElements)]
    [InlineData("countAndList", ResultCondition.CountAndList)]
    public void ParsesEveryResultCondition(string text, ResultCondition expected)
    {
        var rule = RuleParser.Parse(Spec($$"""
            {"name":"r","scope":{"categories":["Walls"]},"resultCondition":"{{text}}",
             "filters":[{"read":"name","condition":"hasValue"}]}
            """));

        Assert.Equal(expected, rule.ResultCondition);
    }

    [Fact]
    public void ScopeDefaultsToInstancesOnly()
    {
        // Types are opted into: a rule about "the walls" means the placed ones, and silently
        // doubling the scope would change every count the engine reports.
        var rule = RuleParser.Parse(Minimal("""[{"read":"name","condition":"hasValue"}]"""));

        Assert.True(rule.Scope.IncludeInstances);
        Assert.False(rule.Scope.IncludeTypes);
    }

    [Fact]
    public void EveryReadAndConditionNameIsAccepted()
    {
        // Guards the tool description against the enums: a name documented but not parseable, or
        // renamed in the enum, fails here rather than in front of a user.
        foreach (var read in Enum.GetNames<ReadKind>())
        {
            var needsParameter = read is nameof(ReadKind.Parameter) or nameof(ReadKind.ApiParameter);
            var parameter = needsParameter ? ""","parameter":"Mark" """ : "";

            var rule = RuleParser.Parse(Minimal(
                $$"""[{"read":"{{read}}"{{parameter}},"condition":"hasValue"}]"""));

            Assert.Equal(Enum.Parse<ReadKind>(read),
                Assert.IsType<LeafFilter>(rule.Root.Children[0]).Read);
        }

        foreach (var condition in Enum.GetNames<Condition>())
        {
            var other = condition.EndsWith("MatchParameter") || condition.EndsWith("MatchesParameter")
                ? ""","otherParameter":"Type Mark" """
                : "";

            var rule = RuleParser.Parse(Minimal(
                $$"""[{"read":"parameter","parameter":"Mark","condition":"{{condition}}","value":"1"{{other}}}]"""));

            Assert.Equal(Enum.Parse<Condition>(condition),
                Assert.IsType<LeafFilter>(rule.Root.Children[0]).Condition);
        }
    }
}

/// <summary>
/// The rule engine's tool description is the only guide a language model gets to the grammar. A
/// read kind or condition the engine accepts but the description never mentions is unreachable in
/// practice, so the two are asserted to agree.
/// </summary>
public class RuleToolDescriptionTests
{
    private static string Description()
    {
        var method = typeof(RevitMCPServer.Tools.RuleTools)
            .GetMethod(nameof(RevitMCPServer.Tools.RuleTools.EvaluateRule))!;

        var attribute = method
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single();

        return attribute.Description;
    }

    [Fact]
    public void DocumentsEveryReadKind()
    {
        var description = Description();

        foreach (var name in Enum.GetNames<ReadKind>())
        {
            var camel = char.ToLowerInvariant(name[0]) + name[1..];
            Assert.Contains(camel, description);
        }
    }

    [Fact]
    public void DocumentsEveryCondition()
    {
        var description = Description();

        foreach (var name in Enum.GetNames<Condition>())
        {
            var camel = char.ToLowerInvariant(name[0]) + name[1..];
            Assert.Contains(camel, description);
        }
    }

    [Fact]
    public void DocumentsEveryResultConditionAndOperator()
    {
        var description = Description();

        foreach (var name in Enum.GetNames<ResultCondition>())
            Assert.Contains(char.ToLowerInvariant(name[0]) + name[1..], description);

        foreach (var name in Enum.GetNames<Join>())
            Assert.Contains(name.ToLowerInvariant(), description);
    }

    [Fact]
    public void DocumentsTheGroupKeys()
    {
        var description = Description();

        // Nesting is unreachable if it is not described, since there is nothing to discover it from.
        Assert.Contains("\"any\"", description);
        Assert.Contains("\"all\"", description);
        Assert.Contains("\"none\"", description);
    }
}
