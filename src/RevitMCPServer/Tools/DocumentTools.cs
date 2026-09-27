using System.ComponentModel;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>
/// File-level document operations. These require write mode and can take a long time, so they carry
/// longer timeouts than the model tools.
/// </summary>
[McpServerToolType]
public sealed class DocumentTools(ToolGateway gateway)
{
    [McpServerTool(Name = Commands.SaveModel)]
    [Description("Saves the active Revit model in place. Reports that there was nothing to save if " +
                 "the model is unmodified. Never does a Save As: a model that has never been saved " +
                 "is refused, because choosing where it lives is the user's decision. On a " +
                 "workshared model this saves the local file only — use SYNC_WITH_CENTRAL to publish " +
                 "to central. Requires write mode.")]
    public Task<string> SaveModel(CancellationToken ct) =>
        gateway.CallAsync(Commands.SaveModel, ToolGateway.Args(), ct, timeoutMs: 300_000);

    [McpServerTool(Name = Commands.SyncWithCentral)]
    [Description("Synchronises a workshared model with central, relinquishing borrowed elements by " +
                 "default so the sync does not leave other users blocked. Fails plainly if the model " +
                 "is not workshared. This can take minutes on a large model and may surface Revit " +
                 "dialogs that need dismissing. Requires write mode.")]
    public Task<string> SyncWithCentral(
        CancellationToken ct,
        [Description("Comment recorded against the synchronisation.")] string? comment = null,
        [Description("Relinquish elements borrowed by this session. Default true.")]
        bool relinquishBorrowedElements = true,
        [Description("Relinquish owned user worksets. Default false — this gives up worksets you own.")]
        bool relinquishUserWorksets = false,
        [Description("Save the local file before synchronising. Default true.")]
        bool saveLocalBefore = true,
        [Description("Save the local file after synchronising. Default true.")]
        bool saveLocalAfter = true) =>
        gateway.CallAsync(Commands.SyncWithCentral, ToolGateway.Args(
            ("comment", comment),
            ("relinquishBorrowedElements", relinquishBorrowedElements),
            ("relinquishUserWorksets", relinquishUserWorksets),
            ("saveLocalBefore", saveLocalBefore),
            ("saveLocalAfter", saveLocalAfter)), ct,
            // Synchronising a large model over a network share genuinely takes this long.
            timeoutMs: 600_000);
}
