using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

public sealed class ListElementsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListElements;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var category = context.Args.String("category");
        var (limit, offset) = context.Args.Page();

        // Scoping to the active view is far cheaper on a large model and is usually what is meant
        // by "the walls I'm looking at".
        View? view = null;
        if (context.Args.Bool("activeViewOnly"))
            view = doc.ActiveView ?? throw new BridgeException(BridgeErrorCode.InvalidState,
                "There is no active view to scope the query to.");

        var elements = CategoryResolver.CollectInstances(doc, category, view)
            .ToElements();

        var page = elements.Skip(offset).Take(limit)
            .Select(e => (JsonNode?)Json.ElementRef(e));

        var result = Json.Page("elements", page, elements.Count, offset, limit);
        result["category"] = category;
        result["scopedToView"] = view?.Name;
        return result;
    }
}

public sealed class CountElementsHandler : IBridgeCommandHandler
{
    public string Command => Commands.CountElements;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var category = context.Args.String("category");

        View? view = null;
        if (context.Args.Bool("activeViewOnly"))
            view = doc.ActiveView;

        // GetElementCount avoids materialising the elements, so this stays fast on a big model.
        var count = CategoryResolver.CollectInstances(doc, category, view).GetElementCount();

        return new JsonObject
        {
            ["category"] = category,
            ["count"] = count,
            ["scopedToView"] = view?.Name
        };
    }
}

public sealed class GetElementPropertiesHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetElementProperties;

    public JsonNode? Execute(CommandContext context)
    {
        var element = context.RequireElement(context.Args.Long("elementId"));

        var result = Json.ElementRef(element);
        result["uniqueId"] = element.UniqueId;
        result["levelId"] = element.LevelId != ElementId.InvalidElementId ? element.LevelId.Value : null;
        result["levelName"] = element.LevelId != ElementId.InvalidElementId
            ? Json.SafeName(element.Document.GetElement(element.LevelId))
            : null;
        result["workset"] = WorksetName(element);
        result["designOption"] = Json.SafeName(
            element.Document.GetElement(element.DesignOption?.Id ?? ElementId.InvalidElementId));
        result["parameters"] = Json.Parameters(element);
        return result;
    }

    private static string? WorksetName(Element element)
    {
        var doc = element.Document;
        if (!doc.IsWorkshared) return null;

        try
        {
            return doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class GetElementTypePropertiesHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetElementTypeProperties;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;

        // Accept either an instance (resolve its type) or a type id directly, because a caller
        // holding an id from LIST_FAMILY_TYPES should not have to know which kind it has.
        var id = context.Args.Long("elementId");
        var element = context.RequireElement(id);

        var type = element as ElementType;
        if (type is null)
        {
            var typeId = element.GetTypeId();
            if (typeId == ElementId.InvalidElementId)
                throw new BridgeException(BridgeErrorCode.NotFound,
                    $"Element {id} ({element.Category?.Name ?? element.GetType().Name}) has no " +
                    "element type — some elements, such as levels and grids, are typeless.");

            type = doc.GetElement(typeId) as ElementType
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"The type of element {id} could not be resolved.");
        }

        var result = Json.ElementRef(type);
        result["uniqueId"] = type.UniqueId;
        result["familyName"] = type.FamilyName;
        result["forElementId"] = id;
        result["parameters"] = Json.Parameters(type);
        return result;
    }
}

public sealed class GetElementLocationHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetElementLocation;

    public JsonNode? Execute(CommandContext context)
    {
        var element = context.RequireElement(context.Args.Long("elementId"));

        var result = Json.ElementRef(element);
        result["location"] = Describe(element);
        return result;
    }

    /// <summary>
    /// Point, curve, or — when the element has neither, as with most hosted and swept geometry —
    /// its bounding box, so the caller always gets something positional back.
    /// </summary>
    private static JsonNode Describe(Element element)
    {
        switch (element.Location)
        {
            case LocationPoint point:
                return new JsonObject
                {
                    ["kind"] = "point",
                    ["point"] = Json.From(Units.PointToJson(point.Point)),
                    ["rotation"] = SafeRotation(point)
                };

            case LocationCurve locationCurve:
                return CurveJson(locationCurve.Curve);

            default:
                var box = element.get_BoundingBox(null);
                if (box is null)
                    return new JsonObject
                    {
                        ["kind"] = "none",
                        ["note"] = "This element exposes neither a location nor a bounding box."
                    };

                return new JsonObject
                {
                    ["kind"] = "boundingBox",
                    ["min"] = Json.From(Units.PointToJson(box.Min)),
                    ["max"] = Json.From(Units.PointToJson(box.Max)),
                    ["note"] = "This element has no point or curve location; its extents are reported instead."
                };
        }
    }

    private static double? SafeRotation(LocationPoint point)
    {
        // Rotation throws for location points that do not support it.
        try { return Math.Round(point.Rotation, 6); }
        catch (Exception) { return null; }
    }

    private static JsonObject CurveJson(Curve curve)
    {
        var o = new JsonObject
        {
            ["kind"] = "curve",
            ["curveType"] = curve.GetType().Name,
            ["isBound"] = curve.IsBound
        };

        if (curve.IsBound)
        {
            o["start"] = Json.From(Units.PointToJson(curve.GetEndPoint(0)));
            o["end"] = Json.From(Units.PointToJson(curve.GetEndPoint(1)));
        }

        try { o["length"] = Units.Round(curve.Length); }
        catch (Exception) { /* unbound curves have no length */ }

        if (curve is Arc arc)
        {
            o["center"] = Json.From(Units.PointToJson(arc.Center));
            o["radius"] = Units.Round(arc.Radius);
        }

        return o;
    }
}

public sealed class GetSelectionHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetSelection;

    public JsonNode? Execute(CommandContext context)
    {
        var uiDoc = context.UiDoc
                    ?? throw new BridgeException(BridgeErrorCode.NoDocument,
                        "No document is open, so nothing can be selected.");

        var doc = uiDoc.Document;
        var ids = uiDoc.Selection.GetElementIds();

        var array = new JsonArray();
        foreach (var id in ids)
        {
            var element = doc.GetElement(id);
            if (element is not null) array.Add(Json.ElementRef(element));
        }

        return new JsonObject
        {
            ["count"] = array.Count,
            ["elements"] = array
        };
    }
}

public sealed class GetModelWarningsHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetModelWarnings;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();
        var includeElements = context.Args.Bool("includeFailingElements", true);

        var warnings = doc.GetWarnings();

        var page = warnings.Skip(offset).Take(limit).Select(warning =>
        {
            var entry = new JsonObject
            {
                ["description"] = warning.GetDescriptionText(),
                ["severity"] = warning.GetSeverity().ToString()
            };

            try { entry["definitionGuid"] = warning.GetFailureDefinitionId().Guid.ToString(); }
            catch (Exception) { /* a few failure definitions have no stable id */ }

            if (includeElements)
            {
                var ids = new JsonArray();
                foreach (var id in warning.GetFailingElements()) ids.Add(id.Value);
                entry["failingElementIds"] = ids;
            }

            return (JsonNode?)entry;
        });

        var result = Json.Page("warnings", page, warnings.Count, offset, limit);

        // A warning count is the headline number users actually ask for.
        result["totalWarnings"] = warnings.Count;
        return result;
    }
}
