using RevitMCPBridge.Handlers;

namespace RevitMCPBridge;

/// <summary>
/// Builds the handler set. A startup assertion keeps the catalogue and the implementations from
/// drifting apart, so a command can never be advertised over MCP without something behind it.
/// </summary>
public static class HandlerRegistry
{
    public static IReadOnlyList<IBridgeCommandHandler> CreateAll()
    {
        var handlers = new IBridgeCommandHandler[]
        {
            // bridge-local
            new BridgeStatusHandler(),

            // project / model structure
            new GetProjectInfoHandler(),
            new ListCategoriesHandler(),
            new ListLevelsHandler(),
            new ListPhasesHandler(),
            new ListWorksetsHandler(),
            new ListLinkedModelsHandler(),

            // elements
            new ListElementsHandler(),
            new CountElementsHandler(),
            new GetElementPropertiesHandler(),
            new GetElementTypePropertiesHandler(),
            new GetElementLocationHandler(),
            new GetSelectionHandler(),
            new GetModelWarningsHandler(),
            new FindByParamHandler(),

            // views / sheets / schedules
            new GetActiveViewHandler(),
            new ListViewsHandler(),
            new ListSheetsHandler(),
            new GetSheetContentsHandler(),
            new GetSchedulesHandler(),

            // families / materials
            new ListFamiliesHandler(),
            new ListFamilyTypesHandler(),
            new ListMaterialsHandler(),
            new GetMaterialPropertiesHandler(),

            // selection / view interaction
            new SetSelectionHandler(),
            new OpenViewHandler(),
            new IsolateInViewHandler(),
        };

        AssertCoverage(handlers);
        return handlers;
    }

    private static void AssertCoverage(IReadOnlyList<IBridgeCommandHandler> handlers)
    {
        var implemented = handlers.Select(h => h.Command)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = RevitMCP.Contracts.CommandCatalog.All
            .Select(c => c.Name)
            .Where(name => !implemented.Contains(name))
            .ToList();

        if (missing.Count > 0)
            BridgeLog.Warn($"Commands in the catalogue with no handler: {string.Join(", ", missing)}", null);

        var duplicates = handlers.GroupBy(h => h.Command, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate handlers for: {string.Join(", ", duplicates)}");
    }
}
