using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>
/// Tools that change or remove existing elements. Both require write mode, and both run in a single
/// named transaction so one Ctrl+Z in Revit reverses them.
/// </summary>
[McpServerToolType]
public sealed class EditTools(ToolGateway gateway)
{
    [McpServerTool(Name = Commands.SetElementParameter)]
    [Description("""
        Sets one parameter on one or more elements, and reports the value Revit actually stored —
        which may be rounded or reinterpreted, so read it back rather than assuming.

        Units matter here. Pass 'value' as text and Revit's own parser applies the project's display
        units, so "8' 6\"" and "8.5" both work for a length, and an enumerated parameter accepts the
        label the user sees. Pass 'rawValue' instead to set the raw internal value — decimal feet for
        lengths, radians for angles. Give exactly one of the two. For a Yes/No parameter, rawValue 1
        or 0 is the reliable form.

        To set a TYPE parameter, pass the type's id (GET_ELEMENT_TYPE_PROPERTIES reports it). That
        changes every instance of that type, so it is deliberately not a flag on an instance id.

        Read-only parameters and elements lacking the parameter are reported per element in
        'failures' while the rest still succeed. Requires write mode.
        """)]
    public Task<string> SetElementParameter(
        [Description("Ids of the elements to change. Pass a single id as a one-element array.")]
        long[] elementIds,
        [Description("Parameter name exactly as Revit shows it, e.g. 'Comments', 'Fire Rating', 'Unconnected Height'.")]
        string parameterName,
        CancellationToken ct,
        [Description("The value as text, interpreted in the project's display units by Revit's parser. Use this or rawValue, not both.")]
        string? value = null,
        [Description("The raw internal value: decimal feet for lengths, radians for angles. Use this or value, not both.")]
        double? rawValue = null)
    {
        if (value is null && rawValue is null)
        {
            return Task.FromResult(
                "SET_ELEMENT_PARAMETER failed (BadRequest): give either 'value' (text, display " +
                "units) or 'rawValue' (number, internal units). Neither was supplied.");
        }

        if (value is not null && rawValue is not null)
        {
            return Task.FromResult(
                "SET_ELEMENT_PARAMETER failed (BadRequest): 'value' and 'rawValue' are alternatives " +
                "— supply exactly one. 'value' is parsed in display units; 'rawValue' is the raw " +
                "internal value in feet.");
        }

        if (elementIds.Length == 0)
        {
            return Task.FromResult(
                "SET_ELEMENT_PARAMETER failed (BadRequest): 'elementIds' is empty.");
        }

        var ids = new JsonArray();
        foreach (var id in elementIds) ids.Add(id);

        // The bridge branches on the JSON value kind, so a number must stay a number on the wire.
        JsonNode payload = rawValue is not null
            ? JsonValue.Create(rawValue.Value)
            : JsonValue.Create(value!);

        return gateway.CallAsync(Commands.SetElementParameter, ToolGateway.Args(
            ("elementIds", ids), ("parameterName", parameterName), ("value", payload)), ct);
    }

    [McpServerTool(Name = Commands.DeleteElement)]
    [Description("""
        Deletes elements. Revit cascades: deleting a wall also deletes the doors and windows hosted
        in it, so the response lists every id actually removed, not just the ones asked for.

        Pass dryRun=true first to see what would go — it lists each target's dependents without
        touching the model. For anything you are not certain about, that is the right first call.

        Some elements cannot be deleted (the last level, an element a constraint depends on); Revit
        refuses and the reason is reported. Ctrl+Z in Revit reverses a deletion. Requires write mode.
        """)]
    public Task<string> DeleteElement(
        [Description("Ids of the elements to delete.")] long[] elementIds,
        CancellationToken ct,
        [Description("Report what would be deleted, including dependents, without changing anything.")]
        bool dryRun = false)
    {
        if (elementIds.Length == 0)
            return Task.FromResult("DELETE_ELEMENT failed (BadRequest): 'elementIds' is empty.");

        var ids = new JsonArray();
        foreach (var id in elementIds) ids.Add(id);

        return gateway.CallAsync(Commands.DeleteElement, ToolGateway.Args(
            ("elementIds", ids), ("dryRun", dryRun)), ct);
    }
}
