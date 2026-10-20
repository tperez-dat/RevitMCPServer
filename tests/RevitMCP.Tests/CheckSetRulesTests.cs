using System.Text.Json.Nodes;
using RevitMCP.Contracts;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// The committed rule files under docs/rules.
///
/// These are transcriptions of a real check set, which makes them exactly the kind of thing that
/// rots: a condition renamed in the engine leaves a file that no longer parses, and nobody finds
/// out until someone runs a health check and gets an error instead of an answer. Parsing every
/// rule here turns that into a build failure.
/// </summary>
public class CheckSetRulesTests
{
    private static string RulesDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null
                   && !File.Exists(Path.Combine(directory.FullName, "RevitMCPServer.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "docs", "rules");
        }
    }

    public static TheoryData<string> RuleFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(RulesDirectory, "*.json"))
                data.Add(Path.GetFileName(path));
            return data;
        }
    }

    [Fact]
    public void ThereIsAtLeastOneRuleFile()
    {
        // Guards the theory below: a theory over an empty set passes vacuously.
        Assert.NotEmpty(Directory.GetFiles(RulesDirectory, "*.json"));
    }

    [Theory]
    [MemberData(nameof(RuleFiles))]
    public void EveryRuleInTheFileParses(string fileName)
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(RulesDirectory, fileName)));
        var rules = Assert.IsType<JsonArray>(document!["rules"]);

        Assert.NotEmpty(rules);

        foreach (var node in rules)
        {
            var spec = Assert.IsType<JsonObject>(node);
            var name = spec["name"]?.GetValue<string>();

            Assert.False(string.IsNullOrWhiteSpace(name),
                $"{fileName}: a rule has no name, so a failure report could not identify it.");

            Rule parsed;
            try
            {
                parsed = RuleParser.Parse(spec);
            }
            catch (RuleParseException ex)
            {
                Assert.Fail($"{fileName}: rule '{name}' does not parse: {ex.Message}");
                throw;
            }

            Assert.Equal(name, parsed.Name);

            // A transcribed check that says nothing about why it exists is unmaintainable: the
            // next reader cannot tell which check in the source set it came from.
            Assert.False(string.IsNullOrWhiteSpace(parsed.Description),
                $"{fileName}: rule '{name}' has no description naming its source check.");
        }
    }

    [Theory]
    [MemberData(nameof(RuleFiles))]
    public void EveryRuleBoundsItsScope(string fileName)
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(RulesDirectory, fileName)));

        foreach (var node in (JsonArray)document!["rules"]!)
        {
            var rule = RuleParser.Parse((JsonObject)node!);

            // allCategories is allowed, but it must be deliberate rather than an empty scope.
            Assert.True(rule.Scope.AllCategories || rule.Scope.Categories.Count > 0,
                $"{fileName}: rule '{rule.Name}' has an unbounded scope.");
        }
    }

    [Fact]
    public void TheGdcFileCoversEveryFilterBasedCheckItClaimsTo()
    {
        // The transcription is only trustworthy if it is complete. Twelve of the thirty live
        // checks in that set are filter-based; eight are rules here and four are listed as
        // answered by a named tool instead.
        var path = Path.Combine(RulesDirectory, "gdc-epoch-2.1.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;

        var rules = ((JsonArray)document["rules"]!).Count;
        var delegated = ((JsonObject)document["_covered_by_a_named_tool_instead"]!).Count;

        Assert.Equal(12, rules + delegated);
    }
}
