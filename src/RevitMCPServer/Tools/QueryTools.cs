using System.ComponentModel;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>
/// Read-only tools. These never change the model, so they work whether or not the user has enabled
/// write mode in Revit.
///
/// Lengths are decimal feet throughout (Revit's internal unit, and this project's imperial display
/// unit). Parameters come back with both the raw stored value and Revit's own formatted string.
/// </summary>
[McpServerToolType]
public sealed class QueryTools(ToolGateway gateway)
{
    private const string PagingNote =
        "Results are paged: pass the returned 'cursor' back to fetch the next page.";

    [McpServerTool(Name = Commands.BridgeStatus)]
    [Description("Reports whether the Revit bridge is reachable and healthy: the Revit version, " +
                 "uptime, request counts, whether write mode is currently enabled, and the log path. " +
                 "Use this first when another tool fails, because it answers even while Revit is " +
                 "busy or showing a dialog.")]
    public Task<string> BridgeStatus(CancellationToken ct) =>
        gateway.CallAsync(Commands.BridgeStatus, ToolGateway.Args(), ct, timeoutMs: 5_000);

    [McpServerTool(Name = Commands.GetProjectInfo)]
    [Description("Returns metadata for the open Revit project: name, number, client, address, " +
                 "status, issue date, file path, Revit version and build, whether it is workshared, " +
                 "and the active view's phase.")]
    public Task<string> GetProjectInfo(CancellationToken ct) =>
        gateway.CallAsync(Commands.GetProjectInfo, ToolGateway.Args(), ct);

    [McpServerTool(Name = Commands.ListCategories)]
    [Description("Lists the categories in the model. Call this to discover the exact category names " +
                 "other tools expect. Defaults to model categories only (walls, doors, floors); pass " +
                 "modelOnly=false to also see annotation categories such as Revision Clouds, " +
                 "Dimensions, Text Notes, Grids and tags, which are equally queryable with " +
                 "LIST_ELEMENTS. The response says how many categories the filter hid. " + PagingNote)]
    public Task<string> ListCategories(
        CancellationToken ct,
        [Description("Model categories only (walls, doors, etc.), excluding annotation and internal categories. Default true.")]
        bool modelOnly = true,
        [Description("Include an element count per category. Slower on a large model.")]
        bool includeCounts = false,
        [Description("Return only categories that actually contain elements. Implies includeCounts.")]
        bool nonEmptyOnly = false,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call, to fetch the next page.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListCategories, ToolGateway.Args(
            ("modelOnly", modelOnly), ("includeCounts", includeCounts), ("nonEmptyOnly", nonEmptyOnly),
            ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListElements)]
    [Description("Lists element instances in a category, with id, name, category and type. " +
                 "Searches the WHOLE document by default — you never need to open or iterate views " +
                 "to find elements. This works for annotation categories too, so view-specific " +
                 "things such as revision clouds, dimensions, text notes and tags are found in one " +
                 "call: pass category='Revision Clouds'. Use activeViewOnly only when you " +
                 "specifically want what is visible in the current view. " +
                 "Use LIST_CATEGORIES (with modelOnly=false for annotation categories) if unsure of " +
                 "the exact name. " + PagingNote)]
    public Task<string> ListElements(
        [Description("Category name as Revit shows it, e.g. 'Walls', 'Doors', 'Structural Framing'.")]
        string category,
        CancellationToken ct,
        [Description("Restrict to elements visible in the active view. Much faster on a large model.")]
        bool activeViewOnly = false,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call, to fetch the next page.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListElements, ToolGateway.Args(
            ("category", category), ("activeViewOnly", activeViewOnly),
            ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.CountElements)]
    [Description("Counts element instances in a category without listing them. Use this instead of " +
                 "LIST_ELEMENTS when only the number matters.")]
    public Task<string> CountElements(
        [Description("Category name as Revit shows it, e.g. 'Walls'.")] string category,
        CancellationToken ct,
        [Description("Count only elements visible in the active view.")] bool activeViewOnly = false) =>
        gateway.CallAsync(Commands.CountElements, ToolGateway.Args(
            ("category", category), ("activeViewOnly", activeViewOnly)), ct);

    [McpServerTool(Name = Commands.GetElementProperties)]
    [Description("Returns every instance parameter of one element, plus its type, level, workset and " +
                 "design option. Each parameter reports its raw stored value and Revit's formatted " +
                 "display value.")]
    public Task<string> GetElementProperties(
        [Description("The element's id, as returned by any listing tool.")] long elementId,
        CancellationToken ct) =>
        gateway.CallAsync(Commands.GetElementProperties, ToolGateway.Args(("elementId", elementId)), ct);

    [McpServerTool(Name = Commands.GetElementTypeProperties)]
    [Description("Returns the type-level parameters for an element's type. Accepts either an " +
                 "instance id (its type is resolved) or a type id directly. Type parameters are " +
                 "shared by every instance of that type.")]
    public Task<string> GetElementTypeProperties(
        [Description("An element instance id, or a type id.")] long elementId,
        CancellationToken ct) =>
        gateway.CallAsync(Commands.GetElementTypeProperties, ToolGateway.Args(("elementId", elementId)), ct);

    [McpServerTool(Name = Commands.GetElementLocation)]
    [Description("Returns an element's location: a point (with rotation), a curve (with endpoints, " +
                 "length, and centre/radius for arcs), or its bounding box when it has neither. " +
                 "Coordinates are in decimal feet.")]
    public Task<string> GetElementLocation(
        [Description("The element's id.")] long elementId,
        CancellationToken ct) =>
        gateway.CallAsync(Commands.GetElementLocation, ToolGateway.Args(("elementId", elementId)), ct);

    [McpServerTool(Name = Commands.FindByParam)]
    [Description("Finds elements whose parameter matches a value. Scope it with 'category' — " +
                 "matching is done by walking elements, so an unscoped search is slow and capped. " +
                 "Matches against both the raw value and Revit's formatted display value, so \"8.5\" " +
                 "and \"8' - 6\\\"\" both find the same element. " + PagingNote)]
    public Task<string> FindByParam(
        [Description("Parameter name exactly as Revit shows it, e.g. 'Comments', 'Fire Rating', 'Mark'.")]
        string parameterName,
        CancellationToken ct,
        [Description("Value to match. Omit only when match is 'exists'.")] string? value = null,
        [Description("Category to search, e.g. 'Walls'. Required unless allCategories is true.")]
        string? category = null,
        [Description("Search every category. Slow on a large model; prefer 'category'.")]
        bool allCategories = false,
        [Description("How to compare: equals, contains, startsWith, greaterThan, lessThan, or exists. Default equals.")]
        string match = "equals",
        [Description("Make string comparison case-sensitive. Default false.")] bool caseSensitive = false,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.FindByParam, ToolGateway.Args(
            ("parameterName", parameterName), ("value", value), ("category", category),
            ("allCategories", allCategories), ("match", match), ("caseSensitive", caseSensitive),
            ("limit", limit), ("cursor", cursor)), ct,
            // A full-model walk can legitimately outlast the default timeout.
            timeoutMs: allCategories ? 120_000 : ToolGateway.DefaultTimeoutMs);

    [McpServerTool(Name = Commands.GetModelWarnings)]
    [Description("Lists the model's warnings with severity and the ids of the elements at fault. " +
                 "The total warning count is a standard model-health metric. " + PagingNote)]
    public Task<string> GetModelWarnings(
        CancellationToken ct,
        [Description("Include the failing element ids for each warning. Default true.")]
        bool includeFailingElements = true,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.GetModelWarnings, ToolGateway.Args(
            ("includeFailingElements", includeFailingElements), ("limit", limit), ("cursor", cursor)), ct,
            timeoutMs: 60_000);

    [McpServerTool(Name = Commands.ListLevels)]
    [Description("Lists the project's levels in elevation order, with elevation in decimal feet and " +
                 "as Revit displays it. Level names and ids are what the creation tools expect.")]
    public Task<string> ListLevels(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListLevels, ToolGateway.Args(("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListPhases)]
    [Description("Lists the project's phases in sequence order.")]
    public Task<string> ListPhases(CancellationToken ct) =>
        gateway.CallAsync(Commands.ListPhases, ToolGateway.Args(), ct);

    [McpServerTool(Name = Commands.ListWorksets)]
    [Description("Lists worksets. Reports that the model is not workshared rather than failing, if " +
                 "that is the case.")]
    public Task<string> ListWorksets(
        CancellationToken ct,
        [Description("Which kind: user, standard, view, family, other, or all. Default user.")]
        string kind = "user",
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListWorksets, ToolGateway.Args(
            ("kind", kind), ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListLinkedModels)]
    [Description("Lists linked Revit models with their load status, path, attachment type, and how " +
                 "many instances of each are placed.")]
    public Task<string> ListLinkedModels(CancellationToken ct) =>
        gateway.CallAsync(Commands.ListLinkedModels, ToolGateway.Args(), ct);

    [McpServerTool(Name = Commands.GetSelection)]
    [Description("Returns the elements currently selected in Revit, with id, name, category and type.")]
    public Task<string> GetSelection(CancellationToken ct) =>
        gateway.CallAsync(Commands.GetSelection, ToolGateway.Args(), ct);
}
