using System.Text.Json.Nodes;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Health check. Deliberately answers without Revit's main thread, so it still responds while
/// Revit is mid-command or showing a modal dialog — the moment you most want to ask what is wrong.
/// </summary>
public sealed class BridgeStatusHandler : IBridgeCommandHandler
{
    public string Command => Commands.BridgeStatus;

    public JsonNode? Execute(CommandContext context)
    {
        var server = RevitMCPBridgeApp.Server;

        return new JsonObject
        {
            ["listening"] = server?.IsRunning ?? false,
            ["protocolVersion"] = Protocol.Version,
            ["pipeName"] = Protocol.PipeName,
            ["revitVersion"] = RevitMCPBridgeApp.RevitVersion,
            ["processId"] = Environment.ProcessId,
            ["startedUtc"] = RevitMCPBridgeApp.StartedUtc.ToString("O"),
            ["uptimeSeconds"] = (long)(DateTimeOffset.UtcNow - RevitMCPBridgeApp.StartedUtc).TotalSeconds,
            ["writesAllowed"] = WriteConsent.Enabled,
            ["connectionsAccepted"] = server?.ConnectionsAccepted ?? 0,
            ["requestsHandled"] = server?.RequestsHandled ?? 0,
            ["lastError"] = server?.LastError,
            ["commandCount"] = CommandCatalog.All.Count,
            ["logPath"] = BridgeLog.FilePath
        };
    }
}
