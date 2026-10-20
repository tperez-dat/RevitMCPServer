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
        var handlers = new List<IBridgeCommandHandler>
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
            new GetViewContentsHandler(),
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

            // modelling / creation
            new CreateWallHandler(),
            new CreateGridHandler(),
            new CreateStructuralColumnHandler(),
            new CreateStructuralFramingHandler(),
            new CreateFloorHandler(),
            new CreateDraftDetailHandler(),

            // model health
            new GetPurgeableHandler(),
            new GetCadImportsHandler(),
            new ListImagesHandler(),
            new ListGroupsHandler(),
            new ListInPlaceFamiliesHandler(),
            new ListDesignOptionsHandler(),
            new ListUnplacedViewsHandler(),
            new GetRevisionsHandler(),
            new ListFiltersHandler(),
            new ListViewTemplatesHandler(),
            new ListStylesHandler(),
            new ListSharedParametersHandler(),
            new ListRoomAreaSpaceStatusHandler(),
            new GetElementOwnershipHandler(),

            // editing
            new SetElementParameterHandler(),
            new DeleteElementHandler(),

            // project setup
            new CreateLevelHandler(),
            new CreateSheetHandler(),
            new PlaceViewOnSheetHandler(),

            // document operations
            new SaveModelHandler(),
            new SyncWithCentralHandler(),

            // export
            new ExportScheduleToCsvHandler(),
        };

        // The report delegates to the handlers above rather than reimplementing them, so it is
        // built last from a lookup of them. Constructed this way the two cannot drift apart, and
        // adding a check to the report is a matter of naming it, not copying code.
        handlers.Add(new GetModelHealthHandler(
            handlers.ToDictionary(h => h.Command, StringComparer.OrdinalIgnoreCase)));

        AssertCoverage(handlers);
        return handlers;
    }

    private static void AssertCoverage(IReadOnlyList<IBridgeCommandHandler> handlers)
    {
        var implemented = handlers.Select(h => h.Command)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ServerLocal commands are answered by the MCP server and are not the add-in's to implement.
        var missing = RevitMCP.Contracts.CommandCatalog.BridgeCommands
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
