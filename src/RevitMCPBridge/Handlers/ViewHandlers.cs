using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

public sealed class GetActiveViewHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetActiveView;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var view = doc.ActiveView
                   ?? throw new BridgeException(BridgeErrorCode.InvalidState,
                       "This document has no active view.");

        var result = ViewJson.Describe(view);
        result["isTemplate"] = view.IsTemplate;
        result["scale"] = view.Scale;
        result["detailLevel"] = view.DetailLevel.ToString();
        result["discipline"] = view.Discipline.ToString();
        result["cropBoxActive"] = view.CropBoxActive;

        // Which sheet the view sits on, when it is placed — the usual follow-up question.
        result["sheet"] = ViewJson.SheetForView(doc, view);
        return result;
    }
}

public sealed class ListViewsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListViews;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var requestedType = context.Args.StringOrNull("viewType");
        var includeTemplates = context.Args.Bool("includeTemplates");
        var nameContains = context.Args.StringOrNull("nameContains");

        ViewType? filterType = null;
        if (requestedType is not null)
        {
            if (!Enum.TryParse<ViewType>(requestedType, ignoreCase: true, out var parsed))
            {
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"'{requestedType}' is not a Revit view type. Valid values include " +
                    "FloorPlan, CeilingPlan, Elevation, Section, ThreeD, Detail, DraftingView, " +
                    "Schedule, DrawingSheet, Legend, AreaPlan, EngineeringPlan.");
            }
            filterType = parsed;
        }

        var views = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => includeTemplates || !v.IsTemplate)
            .Where(v => filterType is null || v.ViewType == filterType)
            .Where(v => nameContains is null
                        || (Json.SafeName(v)?.Contains(nameContains, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(v => v.ViewType.ToString(), StringComparer.Ordinal)
            .ThenBy(v => Json.SafeName(v), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = views.Select(view =>
        {
            var row = ViewJson.Describe(view);
            row["isTemplate"] = view.IsTemplate;
            row["scale"] = view.Scale;
            return (JsonNode?)row;
        });

        var result = Json.Page("views", rows, views.Count, offset, limit);
        result["viewTypeFilter"] = requestedType;
        return result;
    }
}

public sealed class ListSheetsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListSheets;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();
        var includePlaceholders = context.Args.Bool("includePlaceholders", true);

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsTemplate)
            .Where(s => includePlaceholders || !s.IsPlaceholder)
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = sheets.Select(sheet => (JsonNode?)new JsonObject
        {
            ["id"] = sheet.Id.Value,
            ["sheetNumber"] = sheet.SheetNumber,
            ["name"] = sheet.Name,
            ["isPlaceholder"] = sheet.IsPlaceholder,
            ["revision"] = Units.DisplayValue(sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)),
            ["viewCount"] = sheet.IsPlaceholder ? 0 : sheet.GetAllPlacedViews().Count
        });

        return Json.Page("sheets", rows, sheets.Count, offset, limit);
    }
}

public sealed class GetSheetContentsHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetSheetContents;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var sheet = ResolveSheet(context);

        var views = new JsonArray();

        // Viewports hold the graphical views and carry their placement on the sheet.
        foreach (var viewportId in sheet.GetAllViewports())
        {
            if (doc.GetElement(viewportId) is not Viewport viewport) continue;
            if (doc.GetElement(viewport.ViewId) is not View placed) continue;

            var row = ViewJson.Describe(placed);
            row["viewportId"] = viewport.Id.Value;
            row["detailNumber"] = Units.DisplayValue(
                viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER));
            row["placement"] = Json.From(Units.PointToJson(viewport.GetBoxCenter()));
            views.Add(row);
        }

        // Schedules on a sheet are ScheduleSheetInstance, not viewports, so they need a second pass.
        var schedules = new JsonArray();
        foreach (var instance in new FilteredElementCollector(doc, sheet.Id)
                     .OfClass(typeof(ScheduleSheetInstance))
                     .Cast<ScheduleSheetInstance>())
        {
            if (doc.GetElement(instance.ScheduleId) is not ViewSchedule schedule) continue;

            schedules.Add(new JsonObject
            {
                ["instanceId"] = instance.Id.Value,
                ["scheduleId"] = schedule.Id.Value,
                ["name"] = Json.SafeName(schedule),
                ["isTitleblockRevisionSchedule"] = schedule.IsTitleblockRevisionSchedule,
                ["placement"] = Json.From(Units.PointToJson(instance.Point))
            });
        }

        return new JsonObject
        {
            ["sheetId"] = sheet.Id.Value,
            ["sheetNumber"] = sheet.SheetNumber,
            ["name"] = sheet.Name,
            ["isPlaceholder"] = sheet.IsPlaceholder,
            ["views"] = views,
            ["viewCount"] = views.Count,
            ["schedules"] = schedules,
            ["scheduleCount"] = schedules.Count
        };
    }

    /// <summary>Accepts a sheet id, a sheet number ("A-101"), or a sheet name.</summary>
    private static ViewSheet ResolveSheet(CommandContext context)
    {
        var doc = context.Doc;

        if (context.Args.LongOrNull("sheetId") is { } id)
        {
            return doc.GetElement(new ElementId(id)) as ViewSheet
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a sheet.");
        }

        var key = context.Args.StringOrNull("sheetNumber")
                  ?? context.Args.StringOrNull("name")
                  ?? throw new BridgeException(BridgeErrorCode.BadRequest,
                      "Pass 'sheetId', 'sheetNumber', or 'name' to identify the sheet.");

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => string.Equals(s.SheetNumber, key, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return sheets.Count switch
        {
            1 => sheets[0],
            0 => throw new BridgeException(BridgeErrorCode.NotFound,
                $"No sheet has the number or name '{key}'. Call LIST_SHEETS to see what exists."),
            _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{key}' matches {sheets.Count} sheets. Pass 'sheetId' to disambiguate.")
        };
    }
}

public sealed class GetSchedulesHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetSchedules;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        // Titleblock revision schedules live inside titleblock families and are never what a user
        // means by "the schedules in this project", so they are excluded unless asked for.
        var includeRevisionSchedules = context.Args.Bool("includeTitleblockRevisionSchedules");

        var schedules = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate)
            .Where(s => includeRevisionSchedules || !s.IsTitleblockRevisionSchedule)
            .OrderBy(s => Json.SafeName(s), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = schedules.Select(schedule =>
        {
            var definition = schedule.Definition;

            var row = new JsonObject
            {
                ["id"] = schedule.Id.Value,
                ["name"] = Json.SafeName(schedule),
                ["isTitleblockRevisionSchedule"] = schedule.IsTitleblockRevisionSchedule,
                ["isItemized"] = definition?.IsItemized,
                ["categoryName"] = ScheduleCategoryName(doc, definition)
            };

            // Field names are what a caller needs before deciding to export the schedule.
            if (definition is not null)
            {
                var fields = new JsonArray();
                try
                {
                    foreach (var fieldId in definition.GetFieldOrder())
                    {
                        var field = definition.GetField(fieldId);
                        if (field is not null) fields.Add(field.GetName());
                    }
                }
                catch (Exception)
                {
                    // Some schedule kinds (key schedules, revision schedules) restrict field access.
                }

                row["fields"] = fields;
                row["fieldCount"] = fields.Count;
            }

            return (JsonNode?)row;
        });

        return Json.Page("schedules", rows, schedules.Count, offset, limit);
    }

    private static string? ScheduleCategoryName(Document doc, ScheduleDefinition? definition)
    {
        if (definition is null) return null;

        try
        {
            var categoryId = definition.CategoryId;
            return categoryId == ElementId.InvalidElementId
                ? null
                : Category.GetCategory(doc, categoryId)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Shared view serialisation, so a view looks the same whichever command returned it.</summary>
internal static class ViewJson
{
    public static JsonObject Describe(View view) => new()
    {
        ["id"] = view.Id.Value,
        ["name"] = Json.SafeName(view),
        ["viewType"] = view.ViewType.ToString(),
        ["class"] = view.GetType().Name,
        ["canBePrinted"] = view.CanBePrinted
    };

    /// <summary>The sheet a view is placed on, or null when it is not placed.</summary>
    public static JsonNode? SheetForView(Document doc, View view)
    {
        try
        {
            var viewport = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .FirstOrDefault(v => v.ViewId == view.Id);

            if (viewport is null) return null;
            if (doc.GetElement(viewport.SheetId) is not ViewSheet sheet) return null;

            return new JsonObject
            {
                ["id"] = sheet.Id.Value,
                ["sheetNumber"] = sheet.SheetNumber,
                ["name"] = sheet.Name
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
