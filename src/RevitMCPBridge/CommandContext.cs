using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>
/// Everything a handler is allowed to touch. Constructed on Revit's thread inside the external
/// event, so holding one outside <see cref="IBridgeCommandHandler.Execute"/> is never valid.
/// </summary>
public sealed class CommandContext
{
    public CommandContext(UIApplication uiApp, BridgeRequest request)
    {
        UiApp = uiApp;
        Request = request;
        Args = new ArgReader(request.Args ?? new JsonObject(), request.Command);
    }

    public UIApplication UiApp { get; }
    public BridgeRequest Request { get; }
    public ArgReader Args { get; }

    public UIDocument? UiDoc => UiApp.ActiveUIDocument;

    /// <summary>The active document, or throws <see cref="BridgeException"/> if none is open.</summary>
    public Document Doc =>
        UiApp.ActiveUIDocument?.Document
        ?? throw new BridgeException(BridgeErrorCode.NoDocument,
            "No document is open in Revit. Open a project and retry.");

    /// <summary>Resolves an element id that must exist in the active document.</summary>
    public Element RequireElement(long id)
    {
        var element = Doc.GetElement(new ElementId(id));
        return element ?? throw new BridgeException(BridgeErrorCode.NotFound,
            $"No element with id {id} exists in this document.");
    }
}

/// <summary>A failure the bridge understands and can report cleanly, rather than a raw exception.</summary>
public sealed class BridgeException(BridgeErrorCode code, string message, string? detail = null)
    : Exception(message)
{
    public BridgeErrorCode Code { get; } = code;
    public string? Detail { get; } = detail;
}

public interface IBridgeCommandHandler
{
    /// <summary>One of <see cref="Commands"/>.</summary>
    string Command { get; }

    /// <summary>
    /// Runs on Revit's main thread. For a <see cref="CommandKind.ModelWrite"/> command the
    /// dispatcher has already opened a transaction; the handler must not open its own.
    /// </summary>
    JsonNode? Execute(CommandContext context);
}
