using System.ComponentModel;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>
/// Read-only model health checks. None of these change the model, so they all work with write mode
/// off — which is how a QC pass should be run.
/// </summary>
[McpServerToolType]
public sealed class ModelHealthTools(ToolGateway gateway)
{
    private const string PagingNote =
        "Results are paged: pass the returned 'cursor' back to fetch the next page.";

    [McpServerTool(Name = Commands.GetPurgeable)]
    [Description("Lists everything Revit's Purge Unused would offer to remove, grouped by category " +
                 "so you can see where the bloat is — typically line patterns, materials and unused " +
                 "family types. Uses Revit's own purge calculation, not a guess based on types " +
                 "having no instances. Reports only; nothing is purged. " + PagingNote)]
    public Task<string> GetPurgeable(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.GetPurgeable, ToolGateway.Args(("limit", limit), ("cursor", cursor)),
            ct, timeoutMs: 180_000);

    [McpServerTool(Name = Commands.GetCadImports)]
    [Description("Lists DWG/DXF content, separating imports from links and listing imports first. " +
                 "An import is permanent and drags its line patterns and text styles into the " +
                 "project; a link can be unloaded. Reports the source path, whether each is " +
                 "view-specific, and which view owns it. " + PagingNote)]
    public Task<string> GetCadImports(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.GetCadImports, ToolGateway.Args(("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListImages)]
    [Description("Lists raster images and PDFs placed in the model, with their source path and how " +
                 "many times each is placed. An image type with zero placements is still in the " +
                 "file and still costs size. " + PagingNote)]
    public Task<string> ListImages(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListImages, ToolGateway.Args(("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListGroups)]
    [Description("Lists model and detail group types with instance counts, flagging those used " +
                 "zero times (dead weight) and exactly once (usually a mistake rather than reuse). " +
                 "Ordered by instance count. " + PagingNote)]
    public Task<string> ListGroups(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListGroups, ToolGateway.Args(("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListInPlaceFamilies)]
    [Description("Lists in-place families with their category and how many instances exist. " +
                 "In-place families cannot be reused in another project and schedule poorly, so a " +
                 "high count signals modelling that belongs in loadable families. " + PagingNote)]
    public Task<string> ListInPlaceFamilies(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListInPlaceFamilies, ToolGateway.Args(("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListDesignOptions)]
    [Description("Lists design option sets and the options within them, marking the primary option " +
                 "and counting the elements in each. Says plainly when the model has none.")]
    public Task<string> ListDesignOptions(CancellationToken ct) =>
        gateway.CallAsync(Commands.ListDesignOptions, ToolGateway.Args(), ct, timeoutMs: 120_000);

    [McpServerTool(Name = Commands.ListUnplacedViews)]
    [Description("Lists views not placed on any sheet, summarised by view type. Sheets, view " +
                 "templates and Revit's internal browser views are excluded, and schedules placed " +
                 "by a schedule instance correctly count as placed. " + PagingNote)]
    public Task<string> ListUnplacedViews(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListUnplacedViews, ToolGateway.Args(("limit", limit), ("cursor", cursor)),
            ct, timeoutMs: 120_000);
}
