using RevitMCP.Contracts;
using Xunit;

namespace RevitMCP.Tests;

public class PagingTests
{
    [Theory]
    [InlineData(null, Paging.DefaultLimit)]
    [InlineData(0, Paging.DefaultLimit)]
    [InlineData(-5, Paging.DefaultLimit)]
    [InlineData(50, 50)]
    [InlineData(99999, Paging.MaxLimit)]
    public void ClampsLimitIntoRange(int? requested, int expected) =>
        Assert.Equal(expected, Paging.Clamp(requested));

    [Fact]
    public void RoundTripsACursor()
    {
        var cursor = Paging.EncodeCursor(400);

        Assert.True(Paging.TryDecodeCursor(cursor, out var offset));
        Assert.Equal(400, offset);
    }

    [Fact]
    public void TreatsAnAbsentCursorAsTheStart()
    {
        Assert.True(Paging.TryDecodeCursor(null, out var fromNull));
        Assert.Equal(0, fromNull);

        Assert.True(Paging.TryDecodeCursor("", out var fromEmpty));
        Assert.Equal(0, fromEmpty);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("bm90LWEtY3Vyc29y")]       // valid base64, wrong content
    [InlineData("bzotNQ==")]               // "o:-5" — a negative offset
    public void RejectsACursorItDidNotIssue(string cursor) =>
        Assert.False(Paging.TryDecodeCursor(cursor, out _));
}

public class CommandCatalogTests
{
    [Fact]
    public void EveryDeclaredCommandIsInTheCatalogue()
    {
        var declared = typeof(Commands)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        var catalogued = CommandCatalog.All.Select(c => c.Name).ToHashSet();

        Assert.Equal(declared.Count, CommandCatalog.All.Count);
        Assert.All(declared, name => Assert.Contains(name, catalogued));
    }

    [Fact]
    public void CatalogueHasNoDuplicateNames()
    {
        var names = CommandCatalog.All.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(Commands.CreateWall, true)]
    [InlineData(Commands.CreateDraftDetail, true)]
    [InlineData(Commands.SetSelection, true)]
    [InlineData(Commands.OpenView, true)]
    [InlineData(Commands.IsolateInView, true)]
    [InlineData(Commands.GetProjectInfo, false)]
    [InlineData(Commands.ListElements, false)]
    [InlineData(Commands.BridgeStatus, false)]
    [InlineData(Commands.ExportScheduleToCsv, false)]
    public void GatesExactlyTheCommandsThatChangeSomething(string command, bool expected) =>
        Assert.Equal(expected, CommandCatalog.RequiresWriteConsent(command));

    [Fact]
    public void LooksUpCommandsCaseInsensitively()
    {
        Assert.True(CommandCatalog.TryGet("get_project_info", out var spec));
        Assert.Equal(Commands.GetProjectInfo, spec.Name);
    }

    [Fact]
    public void SeparatesBridgeCommandsFromServerLocalOnes()
    {
        Assert.Equal(new[] { Commands.ExtractPdfGeometry },
            CommandCatalog.ServerLocalCommands.Select(c => c.Name).ToArray());

        Assert.DoesNotContain(Commands.ExtractPdfGeometry,
            CommandCatalog.BridgeCommands.Select(c => c.Name));
    }
}
