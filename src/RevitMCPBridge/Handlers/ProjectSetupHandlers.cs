using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Creates a level, and optionally the floor plan that goes with it — a level with no view is
/// usually not what someone wanted.
/// </summary>
public sealed class CreateLevelHandler : IBridgeCommandHandler
{
    public string Command => Commands.CreateLevel;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var elevation = args.Double("elevation");

        // Two levels at one elevation is legal but almost always a mistake, so say so rather than
        // silently making a duplicate.
        var existing = RevitResolve.Levels(doc)
            .FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < 1e-6);

        if (existing is not null && !args.Bool("allowDuplicateElevation"))
        {
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"Level '{existing.Name}' is already at elevation {elevation:0.####} ft " +
                $"(id {existing.Id.Value}). Pass 'allowDuplicateElevation': true to add another anyway.");
        }

        var level = Level.Create(doc, elevation)
                    ?? throw new BridgeException(BridgeErrorCode.RevitException,
                        "Revit did not create the level and gave no reason.");

        if (args.StringOrNull("name") is { } name)
        {
            try
            {
                level.Name = name;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"The level was created but '{name}' could not be used as its name — " +
                    "level names must be unique.", ex.Message);
            }
        }

        doc.Regenerate();

        var result = Json.ElementRef(level);
        result["created"] = true;
        result["elevation"] = Units.Round(level.Elevation);
        result["elevationDisplay"] = Units.DisplayValue(level.get_Parameter(BuiltInParameter.LEVEL_ELEV));
        result["units"] = "feet";

        if (args.Bool("createFloorPlan"))
            result["floorPlan"] = CreateFloorPlan(doc, level);

        return result;
    }

    /// <summary>
    /// Adds a floor plan for the level. Needs a floor-plan ViewFamilyType, and Revit refuses a
    /// second plan on a level that already has one.
    /// </summary>
    private static JsonNode? CreateFloorPlan(Document doc, Level level)
    {
        var viewFamilyType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);

        if (viewFamilyType is null)
        {
            return new JsonObject
            {
                ["created"] = false,
                ["error"] = "This model has no floor plan view type, so no plan could be created."
            };
        }

        try
        {
            var plan = ViewPlan.Create(doc, viewFamilyType.Id, level.Id);
            return new JsonObject
            {
                ["created"] = true,
                ["viewId"] = plan.Id.Value,
                ["name"] = Json.SafeName(plan)
            };
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            // The level exists either way; report the plan failure without discarding the level.
            return new JsonObject
            {
                ["created"] = false,
                ["error"] = $"The level was created but its floor plan was not: {ex.Message}"
            };
        }
    }
}

/// <summary>Creates a sheet, with a titleblock unless a placeholder was asked for.</summary>
public sealed class CreateSheetHandler : IBridgeCommandHandler
{
    public string Command => Commands.CreateSheet;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var sheetNumber = args.StringOrNull("sheetNumber");

        // Revit rejects a duplicate sheet number with an unhelpful message, so check first.
        if (sheetNumber is not null)
        {
            var clash = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, sheetNumber,
                    StringComparison.OrdinalIgnoreCase));

            if (clash is not null)
                throw new BridgeException(BridgeErrorCode.InvalidState,
                    $"Sheet number '{sheetNumber}' is already used by '{clash.Name}' " +
                    $"(id {clash.Id.Value}). Sheet numbers must be unique.");
        }

        ViewSheet sheet;

        if (args.Bool("placeholder"))
        {
            sheet = ViewSheet.CreatePlaceholder(doc);
        }
        else
        {
            // Titleblocks have no ElementTypeGroup entry, so they are found by category.
            var titleBlockId = ResolveTitleBlock(context, doc);
            sheet = ViewSheet.Create(doc, titleBlockId);
        }

        if (sheet is null)
            throw new BridgeException(BridgeErrorCode.RevitException,
                "Revit did not create the sheet and gave no reason.");

        if (sheetNumber is not null) sheet.SheetNumber = sheetNumber;
        if (args.StringOrNull("name") is { } name) sheet.Name = name;

        doc.Regenerate();

        return new JsonObject
        {
            ["created"] = true,
            ["id"] = sheet.Id.Value,
            ["sheetNumber"] = sheet.SheetNumber,
            ["name"] = sheet.Name,
            ["isPlaceholder"] = sheet.IsPlaceholder,
            ["titleBlockTypeId"] = TitleBlockOf(sheet)
        };
    }

    /// <summary>
    /// There is no built-in parameter for a sheet's titleblock, so the instance is found by
    /// collecting titleblock-category elements owned by the sheet's own view.
    /// </summary>
    private static long? TitleBlockOf(ViewSheet sheet)
    {
        try
        {
            var instance = new FilteredElementCollector(sheet.Document, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();

            return instance?.GetTypeId() is { } typeId && typeId != ElementId.InvalidElementId
                ? typeId.Value
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A titleblock family symbol, by id or name, else the first loaded one. InvalidElementId is a
    /// legal argument meaning "no titleblock", so an empty project still yields a sheet.
    /// </summary>
    private static ElementId ResolveTitleBlock(CommandContext context, Document doc)
    {
        if (context.Args.LongOrNull("titleBlockTypeId") is { } id)
        {
            var element = doc.GetElement(new ElementId(id));
            if (element is not FamilySymbol symbol
                || symbol.Category?.Id.Value != (long)BuiltInCategory.OST_TitleBlocks)
            {
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"Element {id} is not a titleblock family type.");
            }

            if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
            return symbol.Id;
        }

        var available = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .Cast<FamilySymbol>()
            .ToList();

        if (context.Args.StringOrNull("titleBlockTypeName") is { } name)
        {
            var match = available.FirstOrDefault(s =>
                string.Equals(Json.SafeName(s), name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                throw new BridgeException(BridgeErrorCode.NotFound,
                    $"No titleblock type is named '{name}'. Available: " +
                    (available.Count == 0
                        ? "(none loaded)"
                        : string.Join(", ", available.Select(Json.SafeName))));

            if (!match.IsActive) { match.Activate(); doc.Regenerate(); }
            return match.Id;
        }

        var first = available.FirstOrDefault();
        if (first is null)
        {
            // Revit accepts InvalidElementId here and makes a sheet with no titleblock.
            return ElementId.InvalidElementId;
        }

        if (!first.IsActive) { first.Activate(); doc.Regenerate(); }
        return first.Id;
    }
}

/// <summary>
/// Places a view on a sheet.
///
/// Schedules are not viewports: they need ScheduleSheetInstance. Both are handled here, because a
/// caller should not have to know which kind of view it is holding.
/// </summary>
public sealed class PlaceViewOnSheetHandler : IBridgeCommandHandler
{
    public string Command => Commands.PlaceViewOnSheet;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var sheet = ResolveSheet(context, doc);
        var view = ResolveView(context, doc);

        if (sheet.IsPlaceholder)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"Sheet '{sheet.SheetNumber}' is a placeholder; nothing can be placed on it.");

        var center = args.Has("location")
            ? args.Point("location")
            : SheetCentre(sheet);

        if (view is ViewSchedule schedule)
        {
            var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, center)
                           ?? throw new BridgeException(BridgeErrorCode.RevitException,
                               "Revit did not place the schedule and gave no reason.");

            doc.Regenerate();

            return new JsonObject
            {
                ["placed"] = true,
                ["kind"] = "schedule",
                ["instanceId"] = instance.Id.Value,
                ["scheduleId"] = schedule.Id.Value,
                ["viewName"] = Json.SafeName(schedule),
                ["sheetId"] = sheet.Id.Value,
                ["sheetNumber"] = sheet.SheetNumber,
                ["location"] = Json.From(Units.PointToJson(center))
            };
        }

        // CanAddViewToSheet is the reliable pre-check: a view already placed elsewhere, or one of a
        // kind sheets do not accept, fails here rather than throwing mid-transaction.
        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
        {
            var placedOn = ViewJson.SheetForView(doc, view);
            throw new BridgeException(BridgeErrorCode.InvalidState,
                placedOn is not null
                    ? $"'{Json.SafeName(view)}' is already placed on sheet " +
                      $"{placedOn["sheetNumber"]}. A view can appear on only one sheet."
                    : $"Revit will not place '{Json.SafeName(view)}' ({view.ViewType}) on a sheet. " +
                      "View templates and some internal view kinds cannot be placed.");
        }

        var viewport = Viewport.Create(doc, sheet.Id, view.Id, center)
                       ?? throw new BridgeException(BridgeErrorCode.RevitException,
                           "Revit did not create the viewport and gave no reason.");

        doc.Regenerate();

        return new JsonObject
        {
            ["placed"] = true,
            ["kind"] = "viewport",
            ["viewportId"] = viewport.Id.Value,
            ["viewId"] = view.Id.Value,
            ["viewName"] = Json.SafeName(view),
            ["viewType"] = view.ViewType.ToString(),
            ["sheetId"] = sheet.Id.Value,
            ["sheetNumber"] = sheet.SheetNumber,
            ["detailNumber"] = Units.DisplayValue(
                viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)),
            ["location"] = Json.From(Units.PointToJson(viewport.GetBoxCenter())),
            ["units"] = "feet"
        };
    }

    /// <summary>
    /// Middle of the sheet, so a caller that does not care about placement still gets something
    /// sensible rather than a view stacked at the origin corner.
    /// </summary>
    private static XYZ SheetCentre(ViewSheet sheet)
    {
        try
        {
            var outline = sheet.Outline;
            return new XYZ(
                (outline.Min.U + outline.Max.U) / 2.0,
                (outline.Min.V + outline.Max.V) / 2.0,
                0);
        }
        catch (Exception)
        {
            return XYZ.Zero;
        }
    }

    private static ViewSheet ResolveSheet(CommandContext context, Document doc)
    {
        if (context.Args.LongOrNull("sheetId") is { } id)
        {
            return doc.GetElement(new ElementId(id)) as ViewSheet
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a sheet.");
        }

        var number = context.Args.StringOrNull("sheetNumber")
                     ?? throw new BridgeException(BridgeErrorCode.BadRequest,
                         "Pass 'sheetId' or 'sheetNumber' to identify the sheet.");

        var matches = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new BridgeException(BridgeErrorCode.NotFound,
                $"No sheet has the number '{number}'. Call LIST_SHEETS to see what exists."),
            _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{number}' matches {matches.Count} sheets. Pass 'sheetId' instead.")
        };
    }

    private static View ResolveView(CommandContext context, Document doc)
    {
        if (context.Args.LongOrNull("viewId") is { } id)
        {
            return doc.GetElement(new ElementId(id)) as View
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a view.");
        }

        var name = context.Args.StringOrNull("viewName")
                   ?? throw new BridgeException(BridgeErrorCode.BadRequest,
                       "Pass 'viewId' or 'viewName' to identify the view to place.");

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
}
