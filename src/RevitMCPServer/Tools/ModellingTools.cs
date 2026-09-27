using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>
/// Tools that create geometry. Every one requires write mode, enabled from the MCP Bridge ribbon
/// panel in Revit, and runs in its own named transaction so the user can undo it with Ctrl+Z.
///
/// All coordinates and lengths are decimal feet. Types and levels can be given by name or id; when
/// a name does not match, the error lists what is available.
/// </summary>
[McpServerToolType]
public sealed class ModellingTools(ToolGateway gateway)
{
    [McpServerTool(Name = Commands.CreateWall)]
    [Description("Creates a wall along a straight line. The wall sits on its level, so the z of the " +
                 "start and end points is ignored — use 'offset' to raise or lower it. Requires write mode.")]
    public Task<string> CreateWall(
        [Description("Start x, in feet.")] double startX,
        [Description("Start y, in feet.")] double startY,
        [Description("End x, in feet.")] double endX,
        [Description("End y, in feet.")] double endY,
        [Description("Wall height in feet, measured up from the level.")] double height,
        CancellationToken ct,
        [Description("Level name to build on, e.g. 'Level 1'. Defaults to the active view's level.")]
        string? levelName = null,
        [Description("Level element id, instead of levelName.")] long? levelId = null,
        [Description("Wall type name, e.g. 'Generic - 8\"'. Defaults to the project's default wall type.")]
        string? wallTypeName = null,
        [Description("Wall type element id, instead of wallTypeName.")] long? wallTypeId = null,
        [Description("Vertical offset from the level, in feet. Default 0.")] double offset = 0,
        [Description("Flip the wall's orientation. Default false.")] bool flip = false,
        [Description("Mark the wall as structural. Default false.")] bool structural = false) =>
        gateway.CallAsync(Commands.CreateWall, ToolGateway.Args(
            ("start", ToolGateway.Point(startX, startY)),
            ("end", ToolGateway.Point(endX, endY)),
            ("height", height), ("levelName", levelName), ("levelId", levelId),
            ("wallTypeName", wallTypeName), ("wallTypeId", wallTypeId),
            ("offset", offset), ("flip", flip), ("structural", structural)), ct);

    [McpServerTool(Name = Commands.CreateGrid)]
    [Description("Creates a straight grid line, optionally naming it. Grid names must be unique in " +
                 "the model. Requires write mode.")]
    public Task<string> CreateGrid(
        [Description("Start x, in feet.")] double startX,
        [Description("Start y, in feet.")] double startY,
        [Description("End x, in feet.")] double endX,
        [Description("End y, in feet.")] double endY,
        CancellationToken ct,
        [Description("Grid name, e.g. 'A' or '1'. Must be unique; Revit assigns one if omitted.")]
        string? name = null) =>
        gateway.CallAsync(Commands.CreateGrid, ToolGateway.Args(
            ("start", ToolGateway.Point(startX, startY)),
            ("end", ToolGateway.Point(endX, endY)),
            ("name", name)), ct);

    [McpServerTool(Name = Commands.CreateStructuralColumn)]
    [Description("Places a structural column at a point. Height comes from the top level or a top " +
                 "offset, not from a height argument. The family type must already be loaded — call " +
                 "LIST_FAMILY_TYPES with category 'Structural Columns' to see the options. " +
                 "Requires write mode.")]
    public Task<string> CreateStructuralColumn(
        [Description("x of the insertion point, in feet.")] double x,
        [Description("y of the insertion point, in feet.")] double y,
        CancellationToken ct,
        [Description("Base level name. Defaults to the active view's level.")] string? baseLevelName = null,
        [Description("Base level element id, instead of baseLevelName.")] long? baseLevelId = null,
        [Description("Top level name. Without it the column uses the family's default height.")]
        string? topLevelName = null,
        [Description("Top level element id, instead of topLevelName.")] long? topLevelId = null,
        [Description("Column type name, e.g. 'W10X49'.")] string? typeName = null,
        [Description("Column type element id, instead of typeName.")] long? typeId = null,
        [Description("Family name, to disambiguate a type name used by several families.")]
        string? familyName = null,
        [Description("Base offset from the base level, in feet.")] double? baseOffset = null,
        [Description("Top offset from the top level, in feet.")] double? topOffset = null,
        [Description("Rotation about the vertical axis, in degrees.")] double? rotation = null) =>
        gateway.CallAsync(Commands.CreateStructuralColumn, ToolGateway.Args(
            ("location", ToolGateway.Point(x, y)),
            ("baseLevelName", baseLevelName), ("baseLevelId", baseLevelId),
            ("topLevelName", topLevelName), ("topLevelId", topLevelId),
            ("typeName", typeName), ("typeId", typeId), ("familyName", familyName),
            ("baseOffset", baseOffset), ("topOffset", topOffset), ("rotation", rotation)), ct);

    [McpServerTool(Name = Commands.CreateStructuralFraming)]
    [Description("Creates a structural beam between two points. Unlike a wall, a beam keeps the z it " +
                 "is given; omit z to place it at the level's elevation. The framing family must " +
                 "already be loaded — call LIST_FAMILY_TYPES with category 'Structural Framing'. " +
                 "Requires write mode.")]
    public Task<string> CreateStructuralFraming(
        [Description("Start x, in feet.")] double startX,
        [Description("Start y, in feet.")] double startY,
        [Description("End x, in feet.")] double endX,
        [Description("End y, in feet.")] double endY,
        CancellationToken ct,
        [Description("Start z in feet. Defaults to the level's elevation.")] double startZ = 0,
        [Description("End z in feet. Defaults to the level's elevation.")] double endZ = 0,
        [Description("Reference level name. Defaults to the active view's level.")] string? levelName = null,
        [Description("Reference level element id, instead of levelName.")] long? levelId = null,
        [Description("Framing type name, e.g. 'W12X26'.")] string? typeName = null,
        [Description("Framing type element id, instead of typeName.")] long? typeId = null,
        [Description("Family name, to disambiguate a type name used by several families.")]
        string? familyName = null) =>
        gateway.CallAsync(Commands.CreateStructuralFraming, ToolGateway.Args(
            ("start", ToolGateway.Point(startX, startY, startZ)),
            ("end", ToolGateway.Point(endX, endY, endZ)),
            ("levelName", levelName), ("levelId", levelId),
            ("typeName", typeName), ("typeId", typeId), ("familyName", familyName)), ct);

    [McpServerTool(Name = Commands.CreateFloor)]
    [Description("Creates a floor from a closed boundary polygon. List the points in order around " +
                 "the perimeter; the loop is closed automatically. All points must share one " +
                 "elevation — use 'heightOffset' to raise the floor above its level. Requires write mode.")]
    public Task<string> CreateFloor(
        [Description("Boundary x coordinates in feet, in order around the perimeter.")] double[] boundaryX,
        [Description("Boundary y coordinates in feet, matching boundaryX by position.")] double[] boundaryY,
        CancellationToken ct,
        [Description("Level name to host the floor. Defaults to the active view's level.")]
        string? levelName = null,
        [Description("Level element id, instead of levelName.")] long? levelId = null,
        [Description("Floor type name, e.g. 'Generic - 12\"'. Defaults to the project's default.")]
        string? floorTypeName = null,
        [Description("Floor type element id, instead of floorTypeName.")] long? floorTypeId = null,
        [Description("Height above the level, in feet. Default 0.")] double? heightOffset = null,
        [Description("Mark the floor as structural. Default false.")] bool structural = false)
    {
        if (boundaryX.Length != boundaryY.Length)
        {
            return Task.FromResult(
                $"CREATE_FLOOR failed (BadRequest): boundaryX has {boundaryX.Length} values but " +
                $"boundaryY has {boundaryY.Length}. They must be the same length, since each pair " +
                "is one boundary point.");
        }

        if (boundaryX.Length < 3)
        {
            return Task.FromResult(
                $"CREATE_FLOOR failed (BadRequest): a floor boundary needs at least 3 points " +
                $"(got {boundaryX.Length}).");
        }

        var boundary = new JsonArray();
        for (var i = 0; i < boundaryX.Length; i++)
            boundary.Add(ToolGateway.Point(boundaryX[i], boundaryY[i]));

        return gateway.CallAsync(Commands.CreateFloor, ToolGateway.Args(
            ("boundary", boundary), ("levelName", levelName), ("levelId", levelId),
            ("floorTypeName", floorTypeName), ("floorTypeId", floorTypeId),
            ("heightOffset", heightOffset), ("structural", structural)), ct);
    }
}
