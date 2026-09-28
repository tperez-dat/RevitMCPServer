using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>
/// Tools that change what the user sees. These require write mode to be enabled from the MCP Bridge
/// ribbon panel in Revit; until it is, they refuse and say so.
/// </summary>
[McpServerToolType]
public sealed class InteractionTools(ToolGateway gateway)
{
    [McpServerTool(Name = Commands.SetSelection)]
    [Description("Selects the given elements in Revit, replacing the current selection. Ids that do " +
                 "not exist are reported back rather than failing the whole call. Requires write mode.")]
    public Task<string> SetSelection(
        [Description("Element ids to select.")] long[] elementIds,
        CancellationToken ct,
        [Description("Also scroll the view to show the selected elements. Default false.")]
        bool showElements = false)
    {
        var ids = new JsonArray();
        foreach (var id in elementIds) ids.Add(id);

        return gateway.CallAsync(Commands.SetSelection, ToolGateway.Args(
            ("elementIds", ids), ("showElements", showElements)), ct);
    }

    [McpServerTool(Name = Commands.OpenView)]
    [Description("Makes a view active in Revit, by id or name, so the user can see it. " +
                 "This is for showing a human something — NOT for finding elements. Do not open " +
                 "views to search the model: LIST_ELEMENTS and FIND_BY_PARAM already query the " +
                 "whole document in one call, including annotation categories, and iterating views " +
                 "is both far slower and disruptive to the user. View templates and non-graphical " +
                 "internal views cannot be activated. Requires write mode.")]
    public Task<string> OpenView(
        CancellationToken ct,
        [Description("The view's element id.")] long? viewId = null,
        [Description("The view's exact name.")] string? name = null) =>
        gateway.CallAsync(Commands.OpenView, ToolGateway.Args(("viewId", viewId), ("name", name)), ct);

    [McpServerTool(Name = Commands.IsolateInView)]
    [Description("Temporarily isolates elements in a view, hiding everything else. This is Revit's " +
                 "temporary hide/isolate, so it is undoable and does not alter saved view settings. " +
                 "Pass reset=true to clear it. Only works in graphical views, not schedules or " +
                 "sheets. Requires write mode.")]
    public Task<string> IsolateInView(
        CancellationToken ct,
        [Description("Element ids to isolate. Required unless reset is true.")] long[]? elementIds = null,
        [Description("The view to act on. Defaults to the active view.")] long? viewId = null,
        [Description("Clear the temporary isolate instead of setting one.")] bool reset = false)
    {
        JsonArray? ids = null;
        if (elementIds is { Length: > 0 })
        {
            ids = new JsonArray();
            foreach (var id in elementIds) ids.Add(id);
        }

        return gateway.CallAsync(Commands.IsolateInView, ToolGateway.Args(
            ("elementIds", ids), ("viewId", viewId), ("reset", reset)), ct);
    }
}
