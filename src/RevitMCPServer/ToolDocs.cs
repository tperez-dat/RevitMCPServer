using System.Text;
using RevitMCP.Contracts;

namespace RevitMCPServer;

/// <summary>
/// Generates docs/TOOLS.txt and docs/TOOLS-names.txt from the command catalogue.
///
/// Committed as code rather than left as a one-off script because the two files drifted once
/// already: a hand-maintained tool list is wrong the first time a command is added and nobody
/// finds out until a user pastes it somewhere.
/// </summary>
public static class ToolDocs
{
    /// <summary>Section heading, the note under it, and the commands in it, in reading order.</summary>
    private static readonly (string Heading, string Note, string[] Commands)[] Groups =
    [
        ("MODEL HEALTH",
         "Read-only QC. Run with write mode off. GET_MODEL_HEALTH runs all of these at once.",
         [
             Commands.GetModelHealth, Commands.GetModelWarnings, Commands.GetPurgeable,
             Commands.GetCadImports, Commands.ListImages, Commands.ListGroups,
             Commands.ListInPlaceFamilies, Commands.ListDesignOptions, Commands.ListUnplacedViews,
             Commands.GetRevisions, Commands.ListFilters, Commands.ListViewTemplates,
             Commands.ListStyles, Commands.ListSharedParameters, Commands.ListRoomAreaSpaceStatus,
             Commands.GetElementOwnership, Commands.GetProjectCoordinates,
             Commands.ListViewsWithHiddenElements, Commands.ListKeynotes
         ]),

        ("RULES",
         "One engine for a project's own standards, instead of a tool per rule. Read-only. Speaks\n" +
         "the same filter vocabulary as a Revit Model Checker check set.",
         [Commands.EvaluateRule]),

        ("QUERY / READ TOOLS",
         "No opt-in needed. These never change the model.",
         [
             Commands.GetProjectInfo, Commands.ListElements, Commands.CountElements,
             Commands.GetElementProperties, Commands.GetElementTypeProperties,
             Commands.GetElementLocation, Commands.FindByParam, Commands.ListCategories,
             Commands.ListLevels, Commands.ListPhases, Commands.ListWorksets,
             Commands.ListLinkedModels, Commands.GetActiveView, Commands.ListViews,
             Commands.GetViewContents, Commands.ListSheets, Commands.GetSheetContents,
             Commands.GetSchedules, Commands.ListFamilies, Commands.ListFamilyTypes,
             Commands.ListMaterials, Commands.GetMaterialProperties, Commands.GetSelection,
             Commands.BridgeStatus
         ]),

        ("SELECTION / VIEW INTERACTION",
         "Requires 'Allow MCP Writes' on the Revit ribbon.",
         [Commands.SetSelection, Commands.OpenView, Commands.IsolateInView]),

        ("MODELLING / CREATION",
         "Requires 'Allow MCP Writes'. Each runs in one named transaction.",
         [
             Commands.CreateWall, Commands.CreateGrid, Commands.CreateStructuralColumn,
             Commands.CreateStructuralFraming, Commands.CreateFloor, Commands.CreateDraftDetail,
             Commands.CreateLevel, Commands.CreateSheet, Commands.PlaceViewOnSheet
         ]),

        ("EDITING",
         "Requires 'Allow MCP Writes'. Each runs in one named transaction.",
         [Commands.SetElementParameter, Commands.DeleteElement]),

        ("DOCUMENT OPERATIONS",
         "Requires 'Allow MCP Writes'. Run outside a transaction.",
         [Commands.SaveModel, Commands.SyncWithCentral]),

        ("EXPORT / INTEGRATION",
         "",
         [Commands.ExportScheduleToCsv, Commands.ExtractPdfGeometry])
    ];

    /// <summary>
    /// Writes both files and returns a process exit code. Fails rather than writing a partial list
    /// if a command is in the catalogue but in no group: a silently omitted tool is the exact
    /// failure this generator exists to prevent.
    /// </summary>
    public static int Write(string docsDirectory)
    {
        var grouped = Groups.SelectMany(g => g.Commands).ToList();

        var duplicated = grouped.GroupBy(c => c, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicated.Count > 0)
        {
            Console.Error.WriteLine($"Listed in more than one section: {string.Join(", ", duplicated)}");
            return 1;
        }

        var ungrouped = CommandCatalog.All.Select(c => c.Name)
            .Except(grouped, StringComparer.Ordinal).ToList();

        if (ungrouped.Count > 0)
        {
            Console.Error.WriteLine(
                $"In the catalogue but in no documented section: {string.Join(", ", ungrouped)}. " +
                "Add them to ToolDocs.Groups.");
            return 1;
        }

        var unknown = grouped.Except(CommandCatalog.All.Select(c => c.Name), StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Documented but not in the catalogue: {string.Join(", ", unknown)}");
            return 1;
        }

        Directory.CreateDirectory(docsDirectory);

        File.WriteAllText(Path.Combine(docsDirectory, "TOOLS.txt"), BuildList());
        File.WriteAllText(Path.Combine(docsDirectory, "TOOLS-names.txt"),
            string.Join(Environment.NewLine, grouped) + Environment.NewLine);

        Console.Error.WriteLine($"Wrote {CommandCatalog.All.Count} tools to {docsDirectory}.");
        return 0;
    }

    private static string BuildList()
    {
        var descriptions = CommandCatalog.All.ToDictionary(c => c.Name, c => c.Description,
            StringComparer.Ordinal);

        // One column width for the whole document, so the descriptions line up when pasted.
        var width = CommandCatalog.All.Max(c => c.Name.Length) + 2;

        var text = new StringBuilder();
        text.AppendLine($"REVIT MCP TOOL LIST  -  {CommandCatalog.All.Count} tools");
        text.AppendLine("RevitMCPBridge named pipe + RevitMCPServer (MCP stdio)");
        text.AppendLine("Lengths and coordinates are decimal feet throughout.");
        text.AppendLine("Every list tool is paged: pass the returned 'cursor' back for the next page.");

        foreach (var (heading, note, commands) in Groups)
        {
            text.AppendLine();
            text.AppendLine(heading);
            text.AppendLine(new string('-', heading.Length));
            if (note.Length > 0) text.AppendLine(note);

            foreach (var command in commands)
                text.AppendLine($"  {command.PadRight(width)}{descriptions[command]}");
        }

        return text.ToString();
    }
}
