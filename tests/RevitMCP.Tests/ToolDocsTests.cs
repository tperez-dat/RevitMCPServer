using RevitMCP.Contracts;
using RevitMCPServer;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// The published tool list is what a user pastes into a prompt or an issue, so a stale one is worse
/// than none. These assert the committed files are what the generator produces now, which turns a
/// forgotten regeneration into a failing test rather than a wrong document.
/// </summary>
public class ToolDocsTests
{
    private static string RepoRoot
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
            return directory!.FullName;
        }
    }

    [Fact]
    public void EveryCatalogueCommandIsDocumentedExactlyOnce()
    {
        // ToolDocs.Write refuses to produce a partial list, so a non-zero exit is the generator
        // telling us a command was added to the catalogue and nowhere else.
        var temporary = Directory.CreateTempSubdirectory("tooldocs");
        try
        {
            Assert.Equal(0, ToolDocs.Write(temporary.FullName));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("TOOLS.txt")]
    [InlineData("TOOLS-names.txt")]
    public void TheCommittedFileMatchesWhatTheGeneratorProduces(string fileName)
    {
        var temporary = Directory.CreateTempSubdirectory("tooldocs");
        try
        {
            Assert.Equal(0, ToolDocs.Write(temporary.FullName));

            var generated = File.ReadAllText(Path.Combine(temporary.FullName, fileName));
            var committed = File.ReadAllText(Path.Combine(RepoRoot, "docs", fileName));

            Assert.Equal(Normalise(generated), Normalise(committed));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }

        // Line endings differ between the machine that generated the file and the one checking it.
        static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd();
    }

    [Fact]
    public void TheToolListStatesTheRightNumberOfTools()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "docs", "TOOLS.txt"));
        Assert.Contains($"{CommandCatalog.All.Count} tools", text);
    }

    [Fact]
    public void TheNamesFileListsEveryCommandAndNothingElse()
    {
        var names = File.ReadAllLines(Path.Combine(RepoRoot, "docs", "TOOLS-names.txt"))
            .Where(line => line.Length > 0)
            .ToList();

        Assert.Equal(CommandCatalog.All.Count, names.Count);
        Assert.Equal(
            CommandCatalog.All.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));
    }
}
