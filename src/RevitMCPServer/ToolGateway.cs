using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCP.Contracts;

namespace RevitMCPServer;

/// <summary>
/// Turns a bridge response into what a tool returns to the model.
///
/// Failures come back as readable text rather than exceptions: a model that is told
/// "no level is named 'Levl 1'. Available: Level 1, Level 2" corrects itself, whereas an
/// exception surfaces as an opaque tool error.
/// </summary>
public sealed class ToolGateway(BridgeClient client)
{
    private static readonly JsonSerializerOptions Pretty = new(Protocol.Json) { WriteIndented = true };

    public const int DefaultTimeoutMs = 30_000;

    /// <summary>Model writes can take longer, especially the first placement of a family.</summary>
    public const int WriteTimeoutMs = 60_000;

    public async Task<string> CallAsync(string command, JsonObject args,
        CancellationToken ct, int? timeoutMs = null)
    {
        var effectiveTimeout = timeoutMs
                               ?? (CommandCatalog.RequiresWriteConsent(command)
                                   ? WriteTimeoutMs
                                   : DefaultTimeoutMs);

        var response = await client.CallAsync(command, args, effectiveTimeout, ct).ConfigureAwait(false);

        if (response.Ok)
            return response.Data is null ? "{}" : response.Data.ToJsonString(Pretty);

        return Describe(command, response);
    }

    /// <summary>
    /// Explains a failure, and adds the remedy where the error code implies one the model or the
    /// user can act on.
    /// </summary>
    private static string Describe(string command, BridgeResponse response)
    {
        var text = new System.Text.StringBuilder()
            .Append(command).Append(" failed (").Append(response.Error).Append("): ")
            .Append(response.Message ?? "no message.");

        var hint = response.Error switch
        {
            BridgeErrorCode.WriteNotPermitted =>
                " Ask the user to enable 'Allow MCP Writes' on the MCP Bridge ribbon panel in Revit. " +
                "Do not retry until they confirm.",
            BridgeErrorCode.Timeout =>
                " Revit was busy. Ask the user to dismiss any open dialog, then retry.",
            BridgeErrorCode.NoDocument =>
                " Ask the user to open a Revit project.",
            BridgeErrorCode.Unauthorized =>
                " Revit was probably restarted. The token refreshes on the next call; retry once.",
            _ => null
        };

        if (hint is not null) text.Append(hint);
        return text.ToString();
    }

    /// <summary>Builds an argument object, dropping nulls so absent optionals stay absent.</summary>
    public static JsonObject Args(params (string Name, object? Value)[] entries)
    {
        var o = new JsonObject();

        foreach (var (name, value) in entries)
        {
            if (value is null) continue;

            o[name] = value switch
            {
                JsonNode node => node,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonSerializer.SerializeToNode(value, Protocol.Json)
            };
        }

        return o;
    }

    /// <summary>An {x,y,z} point, as every geometric tool argument expects it.</summary>
    public static JsonObject Point(double x, double y, double z = 0) => new()
    {
        ["x"] = x, ["y"] = y, ["z"] = z
    };
}
