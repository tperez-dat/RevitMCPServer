using System.ComponentModel;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>Levels, sheets, and getting views onto sheets. All require write mode.</summary>
[McpServerToolType]
public sealed class ProjectSetupTools(ToolGateway gateway)
{
    [McpServerTool(Name = Commands.CreateLevel)]
    [Description("Creates a level at an elevation in decimal feet, optionally named, and optionally " +
                 "with a matching floor plan view — a level with no view is usually not what was " +
                 "wanted. Refuses to duplicate an existing level's elevation unless told to. " +
                 "Level names must be unique. Requires write mode.")]
    public Task<string> CreateLevel(
        [Description("Elevation in decimal feet, measured from the project base point.")] double elevation,
        CancellationToken ct,
        [Description("Level name, e.g. 'Level 3'. Must be unique; Revit assigns one if omitted.")]
        string? name = null,
        [Description("Also create a floor plan view for this level. Default false.")]
        bool createFloorPlan = false,
        [Description("Allow a second level at an elevation that already has one. Default false.")]
        bool allowDuplicateElevation = false) =>
        gateway.CallAsync(Commands.CreateLevel, ToolGateway.Args(
            ("elevation", elevation), ("name", name),
            ("createFloorPlan", createFloorPlan),
            ("allowDuplicateElevation", allowDuplicateElevation)), ct);

    [McpServerTool(Name = Commands.CreateSheet)]
    [Description("Creates a sheet. Uses the first loaded titleblock unless you name one; call " +
                 "LIST_FAMILY_TYPES with category 'Title Blocks' to see the options. Sheet numbers " +
                 "must be unique and a clash is reported before anything is created. Pass " +
                 "placeholder=true for a placeholder sheet with no titleblock. Requires write mode.")]
    public Task<string> CreateSheet(
        CancellationToken ct,
        [Description("Sheet number, e.g. 'A-101'. Must be unique. Revit assigns one if omitted.")]
        string? sheetNumber = null,
        [Description("Sheet name, e.g. 'Ground Floor Plan'.")] string? name = null,
        [Description("Titleblock type name. Defaults to the first loaded titleblock.")]
        string? titleBlockTypeName = null,
        [Description("Titleblock type element id, instead of titleBlockTypeName.")]
        long? titleBlockTypeId = null,
        [Description("Create a placeholder sheet instead of a real one. Default false.")]
        bool placeholder = false) =>
        gateway.CallAsync(Commands.CreateSheet, ToolGateway.Args(
            ("sheetNumber", sheetNumber), ("name", name),
            ("titleBlockTypeName", titleBlockTypeName), ("titleBlockTypeId", titleBlockTypeId),
            ("placeholder", placeholder)), ct);

    [McpServerTool(Name = Commands.PlaceViewOnSheet)]
    [Description("Places a view or a schedule on a sheet. Handles both kinds — schedules are not " +
                 "viewports in Revit, but you do not need to know which you are holding. Defaults to " +
                 "the centre of the sheet if no position is given. A view can appear on only one " +
                 "sheet, and that is checked before anything is created. Requires write mode.")]
    public Task<string> PlaceViewOnSheet(
        CancellationToken ct,
        [Description("The sheet's element id.")] long? sheetId = null,
        [Description("The sheet number, e.g. 'A-101', instead of sheetId.")] string? sheetNumber = null,
        [Description("The view's element id.")] long? viewId = null,
        [Description("The view's exact name, instead of viewId.")] string? viewName = null,
        [Description("x of the placement centre on the sheet, in feet. Defaults to the sheet centre.")]
        double? locationX = null,
        [Description("y of the placement centre on the sheet, in feet. Defaults to the sheet centre.")]
        double? locationY = null)
    {
        // Both coordinates or neither: half a point would silently place at the sheet edge.
        object? location = locationX is not null && locationY is not null
            ? ToolGateway.Point(locationX.Value, locationY.Value)
            : null;

        if ((locationX is null) != (locationY is null))
        {
            return Task.FromResult(
                "PLACE_VIEW_ON_SHEET failed (BadRequest): give both 'locationX' and 'locationY', or " +
                "neither to place at the sheet's centre.");
        }

        return gateway.CallAsync(Commands.PlaceViewOnSheet, ToolGateway.Args(
            ("sheetId", sheetId), ("sheetNumber", sheetNumber),
            ("viewId", viewId), ("viewName", viewName), ("location", location)), ct);
    }
}
