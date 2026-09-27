using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Creates detail elements in a view from a JSON payload: detail lines, arcs, circles, text notes,
/// filled regions, and detail components.
///
/// Detail elements are view-specific, so everything here needs a target view that accepts them —
/// a drafting or detail view, or a plan/section/elevation. A 3D view never does.
///
/// Coordinates default to the view's own plane ('coordinateSpace': 'view'), because that is how a
/// detail is drawn: x to the right, y up, relative to the view origin. Pass 'model' to supply raw
/// model coordinates instead.
/// </summary>
public sealed class CreateDraftDetailHandler : IBridgeCommandHandler
{
    public string Command => Commands.CreateDraftDetail;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var view = ResolveView(context, doc);
        var toModel = BuildTransform(context, view);

        var items = context.Args.Array("elements");
        if (items.Count == 0)
            throw new BridgeException(BridgeErrorCode.BadRequest,
                "'elements' is empty; there is nothing to draw.");

        var created = new JsonArray();
        var failures = new JsonArray();

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject item)
            {
                failures.Add(Failure(index, null, "Entry is not an object."));
                continue;
            }

            var reader = new ArgReader(item, $"CREATE_DRAFT_DETAIL.elements[{index}]");
            var kind = reader.String("type").ToLowerInvariant();

            try
            {
                var made = kind switch
                {
                    "line" or "detailline" => Line(doc, view, reader, toModel),
                    "arc" => Arc(doc, view, reader, toModel),
                    "circle" => Circle(doc, view, reader, toModel),
                    "text" or "textnote" => Text(doc, view, reader, toModel),
                    "filledregion" or "region" => Region(doc, view, reader, toModel),
                    "detailcomponent" or "component" => Component(context, doc, view, reader, toModel),
                    _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                        $"'{kind}' is not a detail element type. Use line, arc, circle, text, " +
                        "filledRegion, or detailComponent.")
                };

                made["index"] = index;
                made["type"] = kind;
                created.Add(made);
            }
            catch (BridgeException ex)
            {
                // One bad entry should not discard a whole valid detail, so failures are collected
                // and reported per entry. The transaction still commits what succeeded.
                failures.Add(Failure(index, kind, ex.Message));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                failures.Add(Failure(index, kind, ex.Message));
            }
        }

        if (created.Count == 0)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                "No detail element could be created. First failure: " +
                (failures.FirstOrDefault()?["error"]?.GetValue<string>() ?? "unknown."));
        }

        doc.Regenerate();

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["viewName"] = Json.SafeName(view),
            ["viewType"] = view.ViewType.ToString(),
            ["createdCount"] = created.Count,
            ["failedCount"] = failures.Count,
            ["created"] = created,
            ["failures"] = failures
        };
    }

    private static JsonObject Failure(int index, string? kind, string error) => new()
    {
        ["index"] = index,
        ["type"] = kind,
        ["error"] = error
    };

    // --- view and coordinate handling -------------------------------------------------------

    private static View ResolveView(CommandContext context, Document doc)
    {
        var view = context.Args.LongOrNull("viewId") is { } id
            ? doc.GetElement(new ElementId(id)) as View
              ?? throw new BridgeException(BridgeErrorCode.NotFound, $"Element {id} is not a view.")
            : context.Args.StringOrNull("viewName") is { } name
                ? FindByName(doc, name)
                : doc.ActiveView
                  ?? throw new BridgeException(BridgeErrorCode.InvalidState, "There is no active view.");

        if (view.IsTemplate)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"'{Json.SafeName(view)}' is a view template; detail elements cannot be drawn in it.");

        // 3D views and schedules cannot host view-specific detail geometry at all.
        if (view.ViewType is ViewType.ThreeD or ViewType.Schedule or ViewType.ColumnSchedule
            or ViewType.PanelSchedule or ViewType.Walkthrough or ViewType.Rendering)
        {
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"'{Json.SafeName(view)}' is a {view.ViewType}. Detail elements need a drafting " +
                "view, a detail view, or a plan/section/elevation.");
        }

        return view;
    }

    private static View FindByName(Document doc, string name)
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
            0 => throw new BridgeException(BridgeErrorCode.NotFound, $"No view is named '{name}'."),
            _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{name}' matches {matches.Count} views. Pass 'viewId' instead.")
        };
    }

    /// <summary>
    /// Maps an incoming point to model space. In view space, x runs along the view's right
    /// direction and y along its up direction from the view origin, which keeps a detail's
    /// coordinates the same whether the view is a plan, a section, or a drafting view.
    /// </summary>
    private static Func<XYZ, XYZ> BuildTransform(CommandContext context, View view)
    {
        var space = context.Args.StringOr("coordinateSpace", "view").ToLowerInvariant();

        return space switch
        {
            "model" => point => point,
            "view" => point => view.Origin
                               + view.RightDirection * point.X
                               + view.UpDirection * point.Y,
            _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'coordinateSpace' must be 'view' or 'model' (got '{space}').")
        };
    }

    // --- element builders --------------------------------------------------------------------

    private static JsonObject Line(Document doc, View view, ArgReader reader, Func<XYZ, XYZ> toModel)
    {
        var curve = Geometry.BoundLine(doc, toModel(reader.Point("start")), toModel(reader.Point("end")), "line");
        var detail = doc.Create.NewDetailCurve(view, curve);
        ApplyLineStyle(doc, detail, reader.StringOrNull("lineStyle"));

        return new JsonObject
        {
            ["id"] = detail.Id.Value,
            ["length"] = Units.Round(curve.Length)
        };
    }

    private static JsonObject Arc(Document doc, View view, ArgReader reader, Func<XYZ, XYZ> toModel)
    {
        Autodesk.Revit.DB.Arc arc;

        // Three-point is the unambiguous way to state an arc; centre/radius/angles is the
        // convenient one. Support both.
        if (reader.Has("pointOnArc"))
        {
            arc = Autodesk.Revit.DB.Arc.Create(
                toModel(reader.Point("start")),
                toModel(reader.Point("end")),
                toModel(reader.Point("pointOnArc")));
        }
        else
        {
            var center = toModel(reader.Point("center"));
            var radius = reader.PositiveDouble("radius");
            var startAngle = reader.DoubleOr("startAngle", 0) * Math.PI / 180.0;
            var endAngle = reader.DoubleOr("endAngle", 90) * Math.PI / 180.0;

            if (Math.Abs(endAngle - startAngle) < 1e-9)
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    "'startAngle' and 'endAngle' are equal, which describes no arc.");

            arc = Autodesk.Revit.DB.Arc.Create(center, radius, startAngle, endAngle,
                view.RightDirection, view.UpDirection);
        }

        var detail = doc.Create.NewDetailCurve(view, arc);
        ApplyLineStyle(doc, detail, reader.StringOrNull("lineStyle"));

        return new JsonObject
        {
            ["id"] = detail.Id.Value,
            ["radius"] = Units.Round(arc.Radius)
        };
    }

    private static JsonObject Circle(Document doc, View view, ArgReader reader, Func<XYZ, XYZ> toModel)
    {
        var center = toModel(reader.Point("center"));
        var radius = reader.PositiveDouble("radius");

        // Revit has no single closed-arc curve, so a full circle is two bound semicircles.
        var first = Autodesk.Revit.DB.Arc.Create(center, radius, 0, Math.PI,
            view.RightDirection, view.UpDirection);
        var second = Autodesk.Revit.DB.Arc.Create(center, radius, Math.PI, 2 * Math.PI,
            view.RightDirection, view.UpDirection);

        var lineStyle = reader.StringOrNull("lineStyle");
        var a = doc.Create.NewDetailCurve(view, first);
        var b = doc.Create.NewDetailCurve(view, second);
        ApplyLineStyle(doc, a, lineStyle);
        ApplyLineStyle(doc, b, lineStyle);

        return new JsonObject
        {
            ["id"] = a.Id.Value,
            ["ids"] = new JsonArray { a.Id.Value, b.Id.Value },
            ["radius"] = Units.Round(radius),
            ["note"] = "A full circle is two semicircular detail arcs; Revit has no closed arc curve."
        };
    }

    private static JsonObject Text(Document doc, View view, ArgReader reader, Func<XYZ, XYZ> toModel)
    {
        var position = toModel(reader.Point("location"));
        var text = reader.String("text");

        var typeId = ResolveTypeId<TextNoteType>(doc, reader, "typeName",
            d => d.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType), "text note");

        TextNote note;

        if (reader.Has("width"))
        {
            // Revit enforces per-type width limits and throws outside them; clamp so a reasonable
            // request succeeds rather than failing on a bound the caller cannot know.
            var requested = reader.PositiveDouble("width");
            var min = TextNote.GetMinimumAllowedWidth(doc, typeId);
            var max = TextNote.GetMaximumAllowedWidth(doc, typeId);
            var width = Math.Clamp(requested, min, max);

            note = TextNote.Create(doc, view.Id, position, width, text, new TextNoteOptions { TypeId = typeId });

            var result = new JsonObject
            {
                ["id"] = note.Id.Value,
                ["width"] = Units.Round(width)
            };

            if (Math.Abs(width - requested) > 1e-9)
            {
                result["note"] = $"Width was clamped from {requested:0.###} to {width:0.###} ft, " +
                                 $"the allowed range for this text type ({min:0.###}-{max:0.###} ft).";
            }

            return result;
        }

        note = TextNote.Create(doc, view.Id, position, text, new TextNoteOptions { TypeId = typeId });
        return new JsonObject { ["id"] = note.Id.Value };
    }

    private static JsonObject Region(Document doc, View view, ArgReader reader, Func<XYZ, XYZ> toModel)
    {
        var points = reader.Points("boundary", 3).Select(toModel).ToList();
        var loop = Geometry.ClosedLoop(doc, points, "filled region boundary");

        var typeId = ResolveTypeId<FilledRegionType>(doc, reader, "typeName", null, "filled region");

        var region = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });

        return new JsonObject
        {
            ["id"] = region.Id.Value,
            ["boundaryPointCount"] = points.Count
        };
    }

    private static JsonObject Component(CommandContext context, Document doc, View view,
        ArgReader reader, Func<XYZ, XYZ> toModel)
    {
        var position = toModel(reader.Point("location"));

        var symbol = FindDetailSymbol(doc, reader);
        if (!symbol.IsActive)
        {
            symbol.Activate();
            doc.Regenerate();
        }

        var instance = doc.Create.NewFamilyInstance(position, symbol, view)
                       ?? throw new BridgeException(BridgeErrorCode.RevitException,
                           "Revit did not place the detail component.");

        if (reader.Has("rotation"))
        {
            var degrees = reader.Double("rotation");
            if (Math.Abs(degrees) > 1e-9)
            {
                // Rotate about the view's normal so the component turns in the plane of the detail.
                var axis = Autodesk.Revit.DB.Line.CreateBound(position, position + view.ViewDirection);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis, degrees * Math.PI / 180.0);
            }
        }

        return new JsonObject
        {
            ["id"] = instance.Id.Value,
            ["familyName"] = symbol.FamilyName,
            ["typeName"] = Json.SafeName(symbol)
        };
    }

    private static FamilySymbol FindDetailSymbol(Document doc, ArgReader reader)
    {
        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_DetailComponents)
            .Cast<FamilySymbol>()
            .ToList();

        if (candidates.Count == 0)
            throw new BridgeException(BridgeErrorCode.NotFound,
                "No detail component families are loaded in this model. Load one first — the " +
                "bridge cannot create a family that does not exist.");

        if (reader.LongOrNull("typeId") is { } id)
        {
            return doc.GetElement(new ElementId(id)) as FamilySymbol
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a family type.");
        }

        var typeName = reader.StringOrNull("typeName");
        var familyName = reader.StringOrNull("familyName");

        var matches = candidates
            .Where(s => typeName is null
                        || string.Equals(Json.SafeName(s), typeName, StringComparison.OrdinalIgnoreCase))
            .Where(s => familyName is null
                        || string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            var available = string.Join(", ", candidates
                .Select(s => $"{s.FamilyName}: {Json.SafeName(s)}")
                .Distinct().OrderBy(s => s).Take(20));

            throw new BridgeException(BridgeErrorCode.NotFound,
                $"No loaded detail component matches family '{familyName ?? "(any)"}' / type " +
                $"'{typeName ?? "(any)"}'. Available: {available}");
        }

        return matches[0];
    }

    // --- shared lookups ----------------------------------------------------------------------

    private static ElementId ResolveTypeId<T>(Document doc, ArgReader reader, string nameArg,
        Func<Document, ElementId>? fallback, string what) where T : ElementType
    {
        var available = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();

        if (reader.StringOrNull(nameArg) is { } name)
        {
            var match = available.FirstOrDefault(t =>
                string.Equals(Json.SafeName(t), name, StringComparison.OrdinalIgnoreCase));

            if (match is not null) return match.Id;

            throw new BridgeException(BridgeErrorCode.NotFound,
                $"No {what} type is named '{name}'. Available: " +
                string.Join(", ", available.Select(Json.SafeName).Where(n => n is not null).Take(20)));
        }

        if (fallback is not null)
        {
            var defaultId = fallback(doc);
            if (defaultId != ElementId.InvalidElementId) return defaultId;
        }

        return available.FirstOrDefault()?.Id
               ?? throw new BridgeException(BridgeErrorCode.NotFound,
                   $"This model has no {what} types, so none can be created.");
    }

    /// <summary>
    /// Applies a named line style. Styles live as subcategories of Lines, and the name a user knows
    /// ("Thin Lines", "Medium Lines") is the subcategory name.
    /// </summary>
    private static void ApplyLineStyle(Document doc, CurveElement curve, string? styleName)
    {
        if (string.IsNullOrEmpty(styleName)) return;

        var available = curve.GetLineStyleIds()
            .Select(doc.GetElement)
            .OfType<GraphicsStyle>()
            .ToList();

        var match = available.FirstOrDefault(s =>
            string.Equals(s.Name, styleName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new BridgeException(BridgeErrorCode.NotFound,
                $"'{styleName}' is not a line style available to this curve. Available: " +
                string.Join(", ", available.Select(s => s.Name).OrderBy(n => n).Take(25)));
        }

        curve.LineStyle = match;
    }
}
