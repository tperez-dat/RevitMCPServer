using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Reports what is visible in a view and where each thing sits on the page.
///
/// This is the precondition for annotating a view. Knowing that a wall is visible is not enough to
/// place a tag or a note against it — the caller also needs its position in the view's own
/// coordinate system, which is the system CREATE_DRAFT_DETAIL draws in. Everything here is
/// projected into that same frame, so a position reported by this command can be handed straight
/// back as a drawing coordinate.
///
/// Unlike LIST_ELEMENTS this takes any view by id or name, so no view has to be activated, and the
/// whole survey runs read-only.
/// </summary>
public sealed class GetViewContentsHandler : IBridgeCommandHandler
{
    /// <summary>Bounding boxes cost real time, so cap the survey rather than stalling Revit.</summary>
    private const int MaxElementsMeasured = 5000;

    public string Command => Commands.GetViewContents;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;
        var view = ResolveView(context, doc);

        if (view.IsTemplate)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"'{Json.SafeName(view)}' is a view template and displays nothing.");

        var frame = ViewFrame.For(view);
        var (limit, offset) = args.Page();
        var includeAnnotation = args.Bool("includeAnnotation", true);

        var wanted = CategoryFilter(doc, args);

        var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();

        var measured = new List<JsonObject>();
        var scanned = 0;
        var truncated = false;

        foreach (var element in collector)
        {
            if (++scanned > MaxElementsMeasured) { truncated = true; break; }

            var category = element.Category;
            if (category is null) continue;

            if (wanted is not null && !wanted.Contains(category.Id.Value)) continue;
            if (!includeAnnotation && category.CategoryType != CategoryType.Model) continue;

            var bounds = Measure(element, view, frame);
            if (bounds is null) continue;          // no geometry to place anything against

            var row = Json.ElementRef(element);
            row["categoryType"] = category.CategoryType.ToString();
            row["isViewSpecific"] = element.ViewSpecific;
            row["viewBounds"] = bounds;
            measured.Add(row);
        }

        // Largest first: with paging, the elements that dominate the drawing are the ones worth
        // annotating, and they should survive truncation.
        var ordered = measured
            .OrderByDescending(r => r["viewBounds"]!["area"]!.GetValue<double>())
            .ToList();

        var page = ordered.Skip(offset).Take(limit).Select(r => (JsonNode?)r);
        var result = Json.Page("elements", page, ordered.Count, offset, limit);

        result["view"] = DescribeView(view, frame);
        result["units"] = "feet; u is right across the page, v is up, depth is toward the viewer";
        result["scanned"] = scanned;

        if (truncated)
        {
            result["truncated"] = true;
            result["note"] = $"Stopped after {MaxElementsMeasured:N0} elements. Pass 'categories' to " +
                             "narrow the survey for a complete answer.";
        }

        return result;
    }

    // --- view description --------------------------------------------------------------------

    private static JsonObject DescribeView(View view, ViewFrame frame)
    {
        var o = new JsonObject
        {
            ["id"] = view.Id.Value,
            ["name"] = Json.SafeName(view),
            ["viewType"] = view.ViewType.ToString(),
            ["scale"] = ViewJson.Scale(view),
            ["detailLevel"] = ViewJson.DetailLevel(view),
            ["discipline"] = ViewJson.Discipline(view),
            ["cropActive"] = ViewJson.CropBoxActive(view),
            // The same frame CREATE_DRAFT_DETAIL uses, so coordinates round-trip between them.
            ["origin"] = Json.From(Units.PointToJson(frame.Origin)),
            ["rightDirection"] = Json.From(Units.PointToJson(frame.Right)),
            ["upDirection"] = Json.From(Units.PointToJson(frame.Up)),
            ["viewDirection"] = Json.From(Units.PointToJson(frame.Normal))
        };

        o["cropBounds"] = CropBounds(view, frame);
        return o;
    }

    /// <summary>The visible page area in view coordinates, which bounds where annotation can go.</summary>
    private static JsonNode? CropBounds(View view, ViewFrame frame)
    {
        try
        {
            var crop = view.CropBox;
            if (crop is null) return null;

            var extents = Extents.Of(Corners(crop), frame);
            return extents?.ToJson();
        }
        catch (Exception)
        {
            // Not every view exposes a crop box.
            return null;
        }
    }

    // --- measurement -------------------------------------------------------------------------

    /// <summary>
    /// An element's extent on the page. The view-scoped bounding box is preferred because it
    /// reflects what this view actually draws; the model-space box is a fallback.
    /// </summary>
    private static JsonNode? Measure(Element element, View view, ViewFrame frame)
    {
        BoundingBoxXYZ? box = null;

        try { box = element.get_BoundingBox(view); }
        catch (Exception) { /* fall through to the model box */ }

        if (box is null)
        {
            try { box = element.get_BoundingBox(null); }
            catch (Exception) { return null; }
        }

        if (box is null) return null;

        return Extents.Of(Corners(box), frame)?.ToJson();
    }

    /// <summary>All eight corners in model space, honouring the box's own transform.</summary>
    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
    {
        var transform = box.Transform ?? Transform.Identity;
        XYZ[] local =
        [
            new(box.Min.X, box.Min.Y, box.Min.Z), new(box.Max.X, box.Min.Y, box.Min.Z),
            new(box.Min.X, box.Max.Y, box.Min.Z), new(box.Max.X, box.Max.Y, box.Min.Z),
            new(box.Min.X, box.Min.Y, box.Max.Z), new(box.Max.X, box.Min.Y, box.Max.Z),
            new(box.Min.X, box.Max.Y, box.Max.Z), new(box.Max.X, box.Max.Y, box.Max.Z)
        ];

        return local.Select(transform.OfPoint);
    }

    // --- filtering / resolution ----------------------------------------------------------------

    private static HashSet<long>? CategoryFilter(Document doc, ArgReader args)
    {
        if (!args.Has("categories")) return null;

        var names = args.Array("categories");
        var ids = new HashSet<long>();

        foreach (var node in names)
        {
            var name = node?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            ids.Add(CategoryResolver.Resolve(doc, name).Id.Value);
        }

        return ids.Count > 0 ? ids : null;
    }

    private static View ResolveView(CommandContext context, Document doc)
    {
        if (context.Args.LongOrNull("viewId") is { } id)
        {
            return doc.GetElement(new ElementId(id)) as View
                   ?? throw new BridgeException(BridgeErrorCode.NotFound, $"Element {id} is not a view.");
        }

        if (context.Args.StringOrNull("viewName") is { } name)
        {
            var matches = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Where(v => string.Equals(Json.SafeName(v), name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return matches.Count switch
            {
                1 => matches[0],
                0 => throw new BridgeException(BridgeErrorCode.NotFound,
                    $"No view is named '{name}'. Call LIST_VIEWS to see what exists."),
                _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"'{name}' matches {matches.Count} views. Pass 'viewId' instead.")
            };
        }

        return doc.ActiveView
               ?? throw new BridgeException(BridgeErrorCode.InvalidState, "There is no active view.");
    }
}

/// <summary>
/// A view's drawing plane: where its origin sits and which way is right, up, and out of the screen.
/// Identical to the frame CREATE_DRAFT_DETAIL draws in, so coordinates are interchangeable.
/// </summary>
internal readonly record struct ViewFrame(XYZ Origin, XYZ Right, XYZ Up, XYZ Normal)
{
    public static ViewFrame For(View view) =>
        new(view.Origin, view.RightDirection, view.UpDirection, view.ViewDirection);

    /// <summary>Projects a model point onto the page: u across, v up, depth toward the viewer.</summary>
    public (double U, double V, double Depth) Project(XYZ point)
    {
        var offset = point - Origin;
        return (offset.DotProduct(Right), offset.DotProduct(Up), offset.DotProduct(Normal));
    }
}

/// <summary>A rectangle on the page, plus how far the geometry sits in front of or behind it.</summary>
internal readonly record struct Extents(
    double MinU, double MinV, double MaxU, double MaxV, double MinDepth, double MaxDepth)
{
    public static Extents? Of(IEnumerable<XYZ> points, ViewFrame frame)
    {
        double minU = double.MaxValue, minV = double.MaxValue, minD = double.MaxValue;
        double maxU = double.MinValue, maxV = double.MinValue, maxD = double.MinValue;
        var any = false;

        foreach (var point in points)
        {
            var (u, v, d) = frame.Project(point);
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            minD = Math.Min(minD, d); maxD = Math.Max(maxD, d);
            any = true;
        }

        return any ? new Extents(minU, minV, maxU, maxV, minD, maxD) : null;
    }

    public JsonObject ToJson()
    {
        var width = MaxU - MinU;
        var height = MaxV - MinV;

        return new JsonObject
        {
            ["minU"] = Units.Round(MinU),
            ["minV"] = Units.Round(MinV),
            ["maxU"] = Units.Round(MaxU),
            ["maxV"] = Units.Round(MaxV),
            ["centreU"] = Units.Round((MinU + MaxU) / 2),
            ["centreV"] = Units.Round((MinV + MaxV) / 2),
            ["width"] = Units.Round(width),
            ["height"] = Units.Round(height),
            ["area"] = Units.Round(width * height),
            ["minDepth"] = Units.Round(MinDepth),
            ["maxDepth"] = Units.Round(MaxDepth),
            // Geometry spanning the view plane is what a section actually cuts through, as opposed
            // to what it merely sees beyond the cut.
            ["crossesViewPlane"] = MinDepth <= 0 && MaxDepth >= 0
        };
    }
}
