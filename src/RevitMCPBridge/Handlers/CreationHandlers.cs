using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// All creation handlers run inside a transaction the dispatcher already opened, named after the
/// command, so a single Ctrl+Z reverses whatever they made.
/// </summary>
public sealed class CreateWallHandler : IBridgeCommandHandler
{
    public string Command => Commands.CreateWall;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var start = args.Point("start");
        var end = args.Point("end");
        var level = RevitResolve.Level(context);
        var height = args.PositiveDouble("height");

        var wallType = RevitResolve.ElementTypeOf<WallType>(context, "wallTypeId", "wallTypeName",
            d => d.GetDefaultElementTypeId(ElementTypeGroup.WallType));

        // A wall's curve is taken at its level, so ignore any z the caller supplied and say so.
        var elevation = level.Elevation;
        var curve = Geometry.BoundLine(doc,
            new XYZ(start.X, start.Y, elevation),
            new XYZ(end.X, end.Y, elevation),
            "CREATE_WALL");

        var offset = args.DoubleOr("offset", 0);
        var flip = args.Bool("flip");
        var structural = args.Bool("structural");

        var wall = Wall.Create(doc, curve, wallType.Id, level.Id, height, offset, flip, structural);

        if (wall is null)
            throw new BridgeException(BridgeErrorCode.RevitException,
                "Revit did not create the wall and gave no reason.");

        // Revit needs a regeneration before the new element's derived values can be read back.
        doc.Regenerate();

        var result = Json.ElementRef(wall);
        result["created"] = true;
        result["levelId"] = level.Id.Value;
        result["levelName"] = level.Name;
        result["height"] = Units.Round(height);
        result["length"] = Units.Round(curve.Length);
        result["start"] = Json.From(Units.PointToJson(curve.GetEndPoint(0)));
        result["end"] = Json.From(Units.PointToJson(curve.GetEndPoint(1)));
        result["units"] = "feet";

        if (Math.Abs(start.Z) > 1e-9 || Math.Abs(end.Z) > 1e-9)
        {
            result["note"] = "The z of 'start'/'end' was ignored: a wall sits on its level. " +
                             "Use 'offset' to raise or lower it relative to that level.";
        }

        return result;
    }
}

public sealed class CreateGridHandler : IBridgeCommandHandler
{
    public string Command => Commands.CreateGrid;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var start = args.Point("start");
        var end = args.Point("end");

        // Grids are vertical datums: they take a plan-projected line, so flatten to one elevation.
        var line = Geometry.BoundLine(doc,
            new XYZ(start.X, start.Y, 0),
            new XYZ(end.X, end.Y, 0),
            "CREATE_GRID");

        var grid = Grid.Create(doc, line)
                   ?? throw new BridgeException(BridgeErrorCode.RevitException,
                       "Revit did not create the grid and gave no reason.");

        // Naming is a separate step, and a duplicate name is a common, recoverable mistake.
        if (args.StringOrNull("name") is { } name)
        {
            try
            {
                grid.Name = name;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"The grid was created but '{name}' could not be used as its name — " +
                    "grid names must be unique in the model.", ex.Message);
            }
        }

        doc.Regenerate();

        var result = Json.ElementRef(grid);
        result["created"] = true;
        result["start"] = Json.From(Units.PointToJson(line.GetEndPoint(0)));
        result["end"] = Json.From(Units.PointToJson(line.GetEndPoint(1)));
        result["length"] = Units.Round(line.Length);
        result["units"] = "feet";
        return result;
    }
}

public sealed class CreateStructuralColumnHandler : IBridgeCommandHandler
{
    public string Command => Commands.CreateStructuralColumn;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var location = args.Point("location");
        var baseLevel = RevitResolve.Level(context, "baseLevelId", "baseLevelName");
        var symbol = RevitResolve.FamilySymbol(context, BuiltInCategory.OST_StructuralColumns);

        var point = new XYZ(location.X, location.Y, baseLevel.Elevation);

        var column = doc.Create.NewFamilyInstance(point, symbol, baseLevel, StructuralType.Column)
                     ?? throw new BridgeException(BridgeErrorCode.RevitException,
                         "Revit did not create the column and gave no reason.");

        doc.Regenerate();

        // A column's height comes from its top level (or a top offset), not from a height argument.
        Level? topLevel = null;
        if (args.Has("topLevelId") || args.Has("topLevelName"))
        {
            topLevel = RevitResolve.Level(context, "topLevelId", "topLevelName");
            SetLevelParameter(column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel,
                "top level");
        }

        if (args.Has("baseOffset"))
            SetDouble(column, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, args.Double("baseOffset"));

        if (args.Has("topOffset"))
            SetDouble(column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, args.Double("topOffset"));

        if (args.Has("rotation"))
            Rotate(doc, column, point, args.Double("rotation"));

        doc.Regenerate();

        var result = Json.ElementRef(column);
        result["created"] = true;
        result["location"] = Json.From(Units.PointToJson(point));
        result["baseLevelId"] = baseLevel.Id.Value;
        result["baseLevelName"] = baseLevel.Name;
        result["topLevelName"] = topLevel?.Name;
        result["units"] = "feet";

        if (topLevel is null)
        {
            result["note"] = "No top level was given, so the column uses the family's default " +
                             "height. Pass 'topLevelName' or 'topOffset' to control it.";
        }

        return result;
    }

    private static void SetLevelParameter(Element element, BuiltInParameter id, Level level, string what)
    {
        var parameter = element.get_Parameter(id);
        if (parameter is null || parameter.IsReadOnly)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"This family does not expose a settable {what} parameter.");

        parameter.Set(level.Id);
    }

    private static void SetDouble(Element element, BuiltInParameter id, double value)
    {
        var parameter = element.get_Parameter(id);
        if (parameter is not null && !parameter.IsReadOnly) parameter.Set(value);
    }

    /// <summary>Rotates about the vertical axis through the insertion point. Degrees in, radians out.</summary>
    private static void Rotate(Document doc, Element element, XYZ point, double degrees)
    {
        if (Math.Abs(degrees) < 1e-9) return;

        var axis = Line.CreateBound(point, point + XYZ.BasisZ);
        ElementTransformUtils.RotateElement(doc, element.Id, axis, degrees * Math.PI / 180.0);
    }
}

public sealed class CreateStructuralFramingHandler : IBridgeCommandHandler
{
    public string Command => Commands.CreateStructuralFraming;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var start = args.Point("start");
        var end = args.Point("end");
        var level = RevitResolve.Level(context);
        var symbol = RevitResolve.FamilySymbol(context, BuiltInCategory.OST_StructuralFraming);

        // Unlike a wall, a beam keeps the z it is given; default to the level when none is supplied.
        var elevation = level.Elevation;
        var startPoint = new XYZ(start.X, start.Y, args.Has("start") && start.Z != 0 ? start.Z : elevation);
        var endPoint = new XYZ(end.X, end.Y, args.Has("end") && end.Z != 0 ? end.Z : elevation);

        var curve = Geometry.BoundLine(doc, startPoint, endPoint, "CREATE_STRUCTURAL_FRAMING");

        var beam = doc.Create.NewFamilyInstance(curve, symbol, level, StructuralType.Beam)
                   ?? throw new BridgeException(BridgeErrorCode.RevitException,
                       "Revit did not create the framing member and gave no reason.");

        doc.Regenerate();

        var result = Json.ElementRef(beam);
        result["created"] = true;
        result["levelId"] = level.Id.Value;
        result["levelName"] = level.Name;
        result["start"] = Json.From(Units.PointToJson(startPoint));
        result["end"] = Json.From(Units.PointToJson(endPoint));
        result["length"] = Units.Round(curve.Length);
        result["units"] = "feet";
        return result;
    }
}

public sealed class CreateFloorHandler : IBridgeCommandHandler
{
    public string Command => Commands.CreateFloor;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var points = args.Points("boundary", 3);
        var level = RevitResolve.Level(context);
        var structural = args.Bool("structural");

        var floorType = RevitResolve.ElementTypeOf<FloorType>(context, "floorTypeId", "floorTypeName",
            d => d.GetDefaultElementTypeId(ElementTypeGroup.FloorType));

        Geometry.RequireHorizontal(points, "CREATE_FLOOR boundary");

        // The boundary is taken at the level's elevation; a height offset is a parameter, not geometry.
        var flattened = points.Select(p => new XYZ(p.X, p.Y, level.Elevation)).ToList();
        var loop = Geometry.ClosedLoop(doc, flattened, "CREATE_FLOOR boundary");

        Floor floor;
        try
        {
            // Floor.Create is the current API on every version this add-in targets (2025-2027);
            // the old doc.Create.NewFloor overloads were removed after 2022.
            floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id, structural, null, 0.0);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                "Revit rejected the floor boundary. It must be a closed, planar, non-self-" +
                "intersecting loop. Check that the points trace the perimeter in order.", ex.Message);
        }

        if (floor is null)
            throw new BridgeException(BridgeErrorCode.RevitException,
                "Revit did not create the floor and gave no reason.");

        if (args.Has("heightOffset"))
        {
            var parameter = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
            if (parameter is not null && !parameter.IsReadOnly)
                parameter.Set(args.Double("heightOffset"));
        }

        doc.Regenerate();

        var result = Json.ElementRef(floor);
        result["created"] = true;
        result["levelId"] = level.Id.Value;
        result["levelName"] = level.Name;
        result["boundaryPointCount"] = flattened.Count;
        result["structural"] = structural;
        result["area"] = Units.Round(
            floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0);
        result["areaDisplay"] = Units.DisplayValue(floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED));
        result["units"] = "feet; area in square feet";
        return result;
    }
}
