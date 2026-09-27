using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>
/// Detail drafting. Takes a JSON payload rather than fixed parameters, because one call usually
/// draws a whole detail: several lines, some text, a filled region.
/// </summary>
[McpServerToolType]
public sealed class DetailTools(ToolGateway gateway)
{
    [McpServerTool(Name = Commands.CreateDraftDetail)]
    [Description("""
        Creates detail elements in a view from a JSON array: detail lines, arcs, circles, text notes,
        filled regions, and detail components. Requires write mode.

        Needs a view that accepts view-specific detail geometry: a drafting view, a detail view, or a
        plan/section/elevation. A 3D view or schedule will be refused.

        Coordinates are in feet and default to the view's own plane: x runs right, y runs up, from the
        view origin. Pass coordinateSpace='model' to give raw model coordinates instead.

        'elements' is a JSON array. Each entry has a "type" and its own fields:
          {"type":"line","start":{"x":0,"y":0},"end":{"x":5,"y":0},"lineStyle":"Thin Lines"}
          {"type":"arc","start":{...},"end":{...},"pointOnArc":{...}}
          {"type":"arc","center":{"x":0,"y":0},"radius":2,"startAngle":0,"endAngle":90}
          {"type":"circle","center":{"x":0,"y":0},"radius":1.5}
          {"type":"text","location":{"x":1,"y":2},"text":"NOTE","width":3,"typeName":"3/32\" Arial"}
          {"type":"filledRegion","boundary":[{"x":0,"y":0},{"x":2,"y":0},{"x":2,"y":2}],"typeName":"Solid Black"}
          {"type":"detailComponent","location":{"x":0,"y":0},"familyName":"Break Line","rotation":45}

        Angles are in degrees. 'lineStyle' and 'typeName' must already exist in the project, and
        detail component families must already be loaded. An entry that fails is reported
        individually in 'failures' while the valid ones are still created.
        """)]
    public Task<string> CreateDraftDetail(
        [Description("JSON array of detail elements to create. See the tool description for the shape of each entry.")]
        string elements,
        CancellationToken ct,
        [Description("Target view's element id. Defaults to the active view.")] long? viewId = null,
        [Description("Target view's name, instead of viewId.")] string? viewName = null,
        [Description("'view' (default) for coordinates in the view plane, or 'model' for model coordinates.")]
        string coordinateSpace = "view")
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(elements);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(
                $"CREATE_DRAFT_DETAIL failed (BadRequest): 'elements' is not valid JSON — {ex.Message}. " +
                "It must be a JSON array, e.g. [{\"type\":\"line\",\"start\":{\"x\":0,\"y\":0},\"end\":{\"x\":5,\"y\":0}}]");
        }

        if (parsed is not JsonArray array)
        {
            return Task.FromResult(
                "CREATE_DRAFT_DETAIL failed (BadRequest): 'elements' must be a JSON array of " +
                "objects, each with a \"type\" field.");
        }

        if (array.Count == 0)
        {
            return Task.FromResult(
                "CREATE_DRAFT_DETAIL failed (BadRequest): 'elements' is an empty array; there is " +
                "nothing to draw.");
        }

        return gateway.CallAsync(Commands.CreateDraftDetail, ToolGateway.Args(
            ("elements", array), ("viewId", viewId), ("viewName", viewName),
            ("coordinateSpace", coordinateSpace)), ct,
            // A large detail is many API calls in one transaction.
            timeoutMs: 120_000);
    }
}
