using System.ComponentModel;
using System.Text.Json.Nodes;
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

    [McpServerTool(Name = Commands.GetModelHealth)]
    [Description("""
        Runs EVERY health check and returns one report. Prefer this over calling the individual
        checks one by one when asked to review, audit or health-check a model: it is a single
        consistent snapshot, it cannot be half-performed, and it is one call instead of a dozen.

        Read-only, so it works with write mode off.

        Returns:
          model    - title, path, file size, last written, Revit version, element count, central path
          summary  - flat headline numbers (warnings, purgeable, cadImports, inPlaceFamilies,
                     unplacedViews, groupTypes, revitLinks, worksets and so on), ready to report
                     or chart directly
          sections - each check's full payload, with how long it took
          failures - any section that errored; the rest still ran

        Detail lists are capped by detailLimit. The counts in 'summary' are totals for the whole
        model, not of the sample — so report those, and call the individual tool
        (GET_PURGEABLE, GET_CAD_IMPORTS, LIST_GROUPS and the rest) when a full list is wanted.

        Can be slow on a large model, mostly from the purge calculation. Pass 'sections' to run a
        subset: project, warnings, purgeable, cadImports, images, groups, inPlaceFamilies,
        designOptions, unplacedViews, links, worksets.
        """)]
    public Task<string> GetModelHealth(
        CancellationToken ct,
        [Description("Run only these sections. Omit for all of them.")]
        string[]? sections = null,
        [Description("Rows of detail per section (default 25). Summary counts are unaffected.")]
        int? detailLimit = null)
    {
        JsonArray? sectionList = null;
        if (sections is { Length: > 0 })
        {
            sectionList = [];
            foreach (var name in sections) sectionList.Add(name);
        }

        return gateway.CallAsync(Commands.GetModelHealth, ToolGateway.Args(
            ("sections", sectionList), ("detailLimit", detailLimit)), ct,
            // Every check on a large model; the purge calculation dominates.
            timeoutMs: 600_000);
    }

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

    [McpServerTool(Name = Commands.GetRevisions)]
    [Description("Lists revisions in sequence with their number, description, date, issued state, " +
                 "and how many revision clouds reference each. Flags revisions with no clouds — " +
                 "they still appear on every sheet's revision schedule.")]
    public Task<string> GetRevisions(CancellationToken ct) =>
        gateway.CallAsync(Commands.GetRevisions, ToolGateway.Args(), ct);

    [McpServerTool(Name = Commands.ListFilters)]
    [Description("Lists view filters with their categories and which views apply them, including " +
                 "through view templates. Flags filters applied nowhere. Ordered by usage, so the " +
                 "load-bearing ones come first. " + PagingNote)]
    public Task<string> ListFilters(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListFilters, ToolGateway.Args(("limit", limit), ("cursor", cursor)),
            ct, timeoutMs: 120_000);

    [McpServerTool(Name = Commands.ListViewTemplates)]
    [Description("Lists view templates and how many views each governs, flagging those governing " +
                 "none. Also reports how many views have no template at all, which is usually the " +
                 "more interesting number. " + PagingNote)]
    public Task<string> ListViewTemplates(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListViewTemplates, ToolGateway.Args(("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListStyles)]
    [Description("Inventories line patterns, fill patterns, text types and dimension types, with " +
                 "instance counts for the annotation types. For which of them are unused and " +
                 "purgeable, call GET_PURGEABLE instead — this is inventory, not a verdict. " + PagingNote)]
    public Task<string> ListStyles(
        CancellationToken ct,
        [Description("Limit to one kind: textType, dimensionType, linePattern, fillPattern. Default all.")]
        string kind = "all",
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListStyles, ToolGateway.Args(
            ("kind", kind), ("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListSharedParameters)]
    [Description("Lists bound shared and project parameters with their GUID, binding (instance or " +
                 "type) and categories. Shared parameters have a GUID stable across models and are " +
                 "the only ones safe to coordinate schedules or dashboards on. " + PagingNote)]
    public Task<string> ListSharedParameters(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListSharedParameters, ToolGateway.Args(("limit", limit), ("cursor", cursor)), ct);

    [McpServerTool(Name = Commands.ListRoomAreaSpaceStatus)]
    [Description("Finds rooms, areas and spaces that are unplaced (no location) or not enclosed " +
                 "(placed but the boundary does not close, so they report zero area). Both " +
                 "schedule as blanks and are easy to miss visually. Redundant rooms come through " +
                 "GET_MODEL_WARNINGS instead. " + PagingNote)]
    public Task<string> ListRoomAreaSpaceStatus(
        CancellationToken ct,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.ListRoomAreaSpaceStatus, ToolGateway.Args(("limit", limit), ("cursor", cursor)),
            ct, timeoutMs: 120_000);

    [McpServerTool(Name = Commands.GetElementOwnership)]
    [Description("Reports who owns what in a workshared model. Workset ownership always; " +
                 "element-level checkout only when you name a category, because that is queried " +
                 "per element and is the expensive half. Says plainly when the model is not " +
                 "workshared. " + PagingNote)]
    public Task<string> GetElementOwnership(
        CancellationToken ct,
        [Description("Category to check element ownership within, e.g. 'Walls'. Omit for worksets only.")]
        string? category = null,
        [Description("Maximum rows to return (default 200, max 2000).")] int? limit = null,
        [Description("Cursor from a previous call.")] string? cursor = null) =>
        gateway.CallAsync(Commands.GetElementOwnership, ToolGateway.Args(
            ("category", category), ("limit", limit), ("cursor", cursor)), ct, timeoutMs: 180_000);
}
