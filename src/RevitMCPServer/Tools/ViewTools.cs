using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>Views, sheets, schedules, families and materials. All read-only.</summary>
[McpServerToolType]
public sealed class ViewTools(ToolGateway gateway)
{
    private const string PagingNote =
        "Results are paged: pass the returned 'cursor' back to fetch the next page.";

    [McpServerTool(Name = Commands.GetActiveView)]
    [Description("Returns the view the user is currently looking at: name, type, scale, detail " +
                 "level, discipline, and the sheet it is placed on if any. Useful for grounding a " +
                 "request like 'isolate these in the current view'.")]
    public Task<string> GetActiveView(CancellationToken ct) =>
        gateway.CallAsync(Commands.GetActiveView, ToolGateway.Args(), ct);

    [McpServerTool(Name = Commands.ListViews)]
    [Description("Lists views, optionally filtered by view type or name fragment. View templates are " +
                 "excluded by default. " + PagingNote)]
    public Task<string> ListViews(
        CancellationToken ct,
        [Description("Filter by type: FloorPlan, CeilingPlan, Elevation, Section, ThreeD, Detail, " +
                     "DraftingView, Schedule, DrawingSheet, Legend, AreaPlan, EngineeringPlan.")]
        string? viewType = null,
        [Description("Only views whose name contains this text (case-insensitive).")]
        string? nameContains = null,
        [Description("Include view templates. Default false.")] bool includeTemplates = false,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListViews, ToolGateway.Args(
            ("viewType", viewType), ("nameContains", nameContains),
            ("includeTemplates", includeTemplates), ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListSheets)]
    [Description("Lists sheets with sheet number, name, current revision, and how many views are " +
                 "placed on each. " + PagingNote)]
    public Task<string> ListSheets(
        CancellationToken ct,
        [Description("Include placeholder sheets. Default true.")] bool includePlaceholders = true,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListSheets, ToolGateway.Args(
            ("includePlaceholders", includePlaceholders), ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.GetSheetContents)]
    [Description("Lists what is placed on one sheet: each view with its detail number and position, " +
                 "and each placed schedule. Identify the sheet by id, sheet number (e.g. 'A-101'), " +
                 "or name.")]
    public Task<string> GetSheetContents(
        CancellationToken ct,
        [Description("The sheet's element id.")] long? sheetId = null,
        [Description("The sheet number, e.g. 'A-101'.")] string? sheetNumber = null,
        [Description("The sheet's name.")] string? name = null) =>
        gateway.CallAsync(Commands.GetSheetContents, ToolGateway.Args(
            ("sheetId", sheetId), ("sheetNumber", sheetNumber), ("name", name)), ct);

    [McpServerTool(Name = Commands.GetViewContents)]
    [Description("""
        Reports everything visible in a view AND where each thing sits on the page. This is the tool
        to call before annotating a view.

        Takes any view by id or name - the view does NOT need to be open, so this works with write
        mode off. Never open views just to inspect them.

        Positions are in the view's own coordinate system, in feet: 'u' runs right across the page,
        'v' runs up, and the origin is the view's origin. These are the SAME coordinates
        CREATE_DRAFT_DETAIL draws in, so a position reported here can be passed straight back as a
        drawing coordinate. 'depth' is toward the viewer, so geometry with crossesViewPlane=true is
        what a section actually cuts through rather than what it sees beyond the cut.

        Each element reports minU/minV/maxU/maxV, its centre, width, height and area. The view
        reports its crop bounds in the same coordinates, which is the area annotation must stay
        inside.

        Results are ordered largest first, so the elements that dominate the drawing survive paging.
        Existing annotation is included and marked with categoryType, so you can see what is already
        tagged rather than duplicating it.
        """)]
    public Task<string> GetViewContents(
        CancellationToken ct,
        [Description("The view's element id. Omit to use the active view.")] long? viewId = null,
        [Description("The view's exact name, instead of viewId.")] string? viewName = null,
        [Description("Limit to these category names, e.g. ['Walls','Floors']. Omit for everything visible.")]
        string[]? categories = null,
        [Description("Include annotation elements already in the view. Default true.")]
        bool includeAnnotation = true,
        [Description("Maximum elements to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null)
    {
        JsonArray? categoryList = null;
        if (categories is { Length: > 0 })
        {
            categoryList = [];
            foreach (var name in categories) categoryList.Add(name);
        }

        return gateway.CallAsync(Commands.GetViewContents, ToolGateway.Args(
            ("viewId", viewId), ("viewName", viewName), ("categories", categoryList),
            ("includeAnnotation", includeAnnotation), ("limit", limit), ("cursor", cursor)), ct,
            // Measuring every element in a busy view is real work.
            timeoutMs: 120_000);
    }

    [McpServerTool(Name = Commands.GetSchedules)]
    [Description("Lists the project's schedules with their category and column names. Titleblock " +
                 "revision schedules are excluded by default, since they live inside titleblock " +
                 "families rather than being project schedules. " + PagingNote)]
    public Task<string> GetSchedules(
        CancellationToken ct,
        [Description("Also include titleblock revision schedules. Default false.")]
        bool includeTitleblockRevisionSchedules = false,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.GetSchedules, ToolGateway.Args(
            ("includeTitleblockRevisionSchedules", includeTitleblockRevisionSchedules),
            ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListFamilies)]
    [Description("Lists loadable families, optionally filtered by category or name fragment, with " +
                 "how many types each has. Note that system families (walls, floors, roofs) are not " +
                 "included — use LIST_FAMILY_TYPES for their types. " + PagingNote)]
    public Task<string> ListFamilies(
        CancellationToken ct,
        [Description("Filter by category, e.g. 'Doors', 'Structural Framing'.")] string? category = null,
        [Description("Only families whose name contains this text.")] string? nameContains = null,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListFamilies, ToolGateway.Args(
            ("category", category), ("nameContains", nameContains),
            ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListFamilyTypes)]
    [Description("Lists element types — both loadable family types and system types such as wall and " +
                 "floor types. Filter by family name, category, or name fragment. Loadable types " +
                 "report whether they are active, which Revit requires before first placement. " +
                 "Use this to get the exact type name or id the creation tools expect. " + PagingNote)]
    public Task<string> ListFamilyTypes(
        CancellationToken ct,
        [Description("Filter to one family, e.g. 'M_Wide Flange-Column'.")] string? familyName = null,
        [Description("Filter by category, e.g. 'Walls', 'Structural Columns'.")] string? category = null,
        [Description("Only types whose name contains this text.")] string? nameContains = null,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListFamilyTypes, ToolGateway.Args(
            ("familyName", familyName), ("category", category), ("nameContains", nameContains),
            ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListMaterials)]
    [Description("Lists materials with their class, category and colour. " + PagingNote)]
    public Task<string> ListMaterials(
        CancellationToken ct,
        [Description("Only materials whose name contains this text.")] string? nameContains = null,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListMaterials, ToolGateway.Args(
            ("nameContains", nameContains), ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.GetMaterialProperties)]
    [Description("Returns one material's full properties: appearance (colour, transparency, " +
                 "shininess), fill patterns, all parameters, and its structural and thermal assets " +
                 "(density, Young's modulus, conductivity) when it has them. Identify it by id or name.")]
    public Task<string> GetMaterialProperties(
        CancellationToken ct,
        [Description("The material's element id.")] long? materialId = null,
        [Description("The material's exact name.")] string? name = null) =>
        gateway.CallAsync(Commands.GetMaterialProperties, ToolGateway.Args(
            ("materialId", materialId), ("name", name)), ct);
}
