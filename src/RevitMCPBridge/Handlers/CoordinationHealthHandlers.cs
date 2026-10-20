using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Shared and project parameters with their bindings: which categories they apply to, and whether
/// they bind to instances or types. Mismatched bindings across models are a standard cause of
/// schedules and dashboards silently missing data.
/// </summary>
public sealed class ListSharedParametersHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListSharedParameters;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        // A shared parameter has a GUID that is stable across models; a project parameter does not,
        // which is precisely why only shared ones are safe to coordinate on.
        var sharedGuids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)))
        {
            if (element is not SharedParameterElement shared) continue;
            try { sharedGuids[shared.GetDefinition().Name] = shared.GuidValue.ToString(); }
            catch (Exception) { /* definition unavailable */ }
        }

        var rows = new List<JsonObject>();
        var iterator = doc.ParameterBindings.ForwardIterator();

        while (iterator.MoveNext())
        {
            if (iterator.Key is not Definition definition) continue;

            var categories = new JsonArray();
            string? bindingKind = null;

            if (iterator.Current is ElementBinding binding)
            {
                bindingKind = binding is InstanceBinding ? "instance" : "type";
                foreach (Category category in binding.Categories) categories.Add(category.Name);
            }

            var isShared = sharedGuids.TryGetValue(definition.Name, out var guid);

            rows.Add(new JsonObject
            {
                ["name"] = definition.Name,
                ["isShared"] = isShared,
                ["guid"] = isShared ? guid : null,
                ["binding"] = bindingKind,
                ["categoryCount"] = categories.Count,
                ["categories"] = categories,
                ["dataType"] = SafeTypeName(definition)
            });
        }

        var ordered = rows
            .OrderByDescending(r => r["isShared"]!.GetValue<bool>())
            .ThenBy(r => r["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = Json.Page("parameters", ordered.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            ordered.Count, offset, limit);

        result["sharedCount"] = ordered.Count(r => r["isShared"]!.GetValue<bool>());
        result["projectOnlyCount"] = ordered.Count(r => !r["isShared"]!.GetValue<bool>());
        result["note"] = "Only bound parameters appear here. A shared parameter present in the " +
                         "shared parameter file but not bound in this project is not listed.";
        return result;
    }

    private static string? SafeTypeName(Definition definition)
    {
        try { return definition.GetDataType().TypeId; }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Rooms, areas and spaces that are not doing their job: placed nowhere, or placed but enclosing
/// nothing. Both schedule as blanks and both are easy to miss visually.
/// </summary>
public sealed class ListRoomAreaSpaceStatusHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListRoomAreaSpaceStatus;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var rows = new List<JsonObject>();
        var counts = new Dictionary<string, (int Total, int Unplaced, int NotEnclosed)>();

        void Scan(string label, BuiltInCategory category)
        {
            var elements = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .Cast<SpatialElement>()
                .ToList();

            var unplaced = 0;
            var notEnclosed = 0;

            foreach (var element in elements)
            {
                // No location means the element exists in the schedule but was never placed.
                var isUnplaced = element.Location is null;

                // Placed but zero area means its boundary does not close - Revit's "not enclosed".
                var area = SafeArea(element);
                var isNotEnclosed = !isUnplaced && area is <= 0;

                if (isUnplaced) unplaced++;
                if (isNotEnclosed) notEnclosed++;
                if (!isUnplaced && !isNotEnclosed) continue;

                rows.Add(new JsonObject
                {
                    ["id"] = element.Id.Value,
                    ["kind"] = label,
                    ["name"] = Json.SafeName(element),
                    ["number"] = element.Number,
                    ["level"] = Json.SafeName(doc.GetElement(element.LevelId)),
                    ["status"] = isUnplaced ? "unplaced" : "notEnclosed",
                    ["area"] = area
                });
            }

            counts[label] = (elements.Count, unplaced, notEnclosed);
        }

        Scan("room", BuiltInCategory.OST_Rooms);
        Scan("area", BuiltInCategory.OST_Areas);
        Scan("space", BuiltInCategory.OST_MEPSpaces);

        var ordered = rows
            .OrderBy(r => r["kind"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(r => r["number"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = Json.Page("problems", ordered.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            ordered.Count, offset, limit);

        var summary = new JsonObject();
        foreach (var (label, value) in counts)
        {
            summary[label] = new JsonObject
            {
                ["total"] = value.Total,
                ["unplaced"] = value.Unplaced,
                ["notEnclosed"] = value.NotEnclosed
            };
        }

        result["byKind"] = summary;
        result["totalProblems"] = ordered.Count;
        result["note"] = "Unplaced means the element has no location. Not enclosed means it is " +
                         "placed but its boundary does not close, so it reports zero area. " +
                         "Redundant rooms are reported by Revit as warnings; see GET_MODEL_WARNINGS.";
        return result;
    }

    private static double? SafeArea(SpatialElement element)
    {
        try { return Units.Round(element.Area); }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Who owns and last changed elements in a workshared model.
///
/// Checkout status is a per-element query, so this is scoped rather than run over the whole model:
/// asking 30,000 times would take minutes to answer a question nobody asked that broadly. Worksets
/// are summarised wholesale because their ownership is a single cheap property.
/// </summary>
public sealed class GetElementOwnershipHandler : IBridgeCommandHandler
{
    /// <summary>Hard ceiling on per-element checkout queries, whatever was asked for.</summary>
    private const int MaxElementsQueried = 5000;

    public string Command => Commands.GetElementOwnership;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        if (!doc.IsWorkshared)
        {
            return new JsonObject
            {
                ["isWorkshared"] = false,
                ["note"] = "This model is not workshared, so nothing is owned or checked out."
            };
        }

        var result = new JsonObject { ["isWorkshared"] = true };

        // Workset ownership first: cheap, and usually the answer people actually want.
        var worksets = new JsonArray();
        foreach (var workset in new FilteredWorksetCollector(doc)
                     .OfKind(WorksetKind.UserWorkset)
                     .ToWorksets()
                     .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
        {
            worksets.Add(new JsonObject
            {
                ["id"] = workset.Id.IntegerValue,
                ["name"] = workset.Name,
                ["owner"] = string.IsNullOrEmpty(workset.Owner) ? null : workset.Owner,
                ["isOwned"] = !string.IsNullOrEmpty(workset.Owner),
                ["isOpen"] = workset.IsOpen
            });
        }

        result["worksets"] = worksets;
        result["ownedWorksetCount"] = worksets.Count(w => w?["isOwned"]?.GetValue<bool>() == true);

        // Element-level ownership only when a category is named, since it is the expensive half.
        var category = args.StringOrNull("category");
        if (category is null)
        {
            result["note"] = "Workset ownership only. Pass 'category' to also report element-level " +
                             "checkout status, which is queried per element and so is scoped.";
            return result;
        }

        var (limit, offset) = args.Page();
        var elements = CategoryResolver.CollectInstances(doc, category).ToElements();
        var truncated = elements.Count > MaxElementsQueried;

        var owned = new List<JsonObject>();
        var byOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements.Take(MaxElementsQueried))
        {
            CheckoutStatus status;
            try { status = WorksharingUtils.GetCheckoutStatus(doc, element.Id); }
            catch (Exception) { continue; }

            if (status == CheckoutStatus.NotOwned) continue;

            var info = SafeTooltip(doc, element.Id);
            var owner = info?.Owner ?? "(unknown)";
            byOwner[owner] = byOwner.GetValueOrDefault(owner) + 1;

            owned.Add(new JsonObject
            {
                ["id"] = element.Id.Value,
                ["name"] = Json.SafeName(element),
                ["category"] = element.Category?.Name,
                ["checkoutStatus"] = status.ToString(),
                ["owner"] = info?.Owner,
                ["creator"] = info?.Creator,
                ["lastChangedBy"] = info?.LastChangedBy
            });
        }

        var page = Json.Page("elements", owned.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            owned.Count, offset, limit);

        var ownerSummary = new JsonObject();
        foreach (var (owner, count) in byOwner.OrderByDescending(e => e.Value))
            ownerSummary[owner] = count;

        result["category"] = category;
        result["elementsQueried"] = Math.Min(elements.Count, MaxElementsQueried);
        result["ownedElementCount"] = owned.Count;
        result["byOwner"] = ownerSummary;
        result["ownedElements"] = page;

        if (truncated)
        {
            result["truncated"] = true;
            result["truncationNote"] = $"Only the first {MaxElementsQueried:N0} of " +
                                       $"{elements.Count:N0} elements were queried.";
        }

        return result;
    }

    private static WorksharingTooltipInfo? SafeTooltip(Document doc, ElementId id)
    {
        try { return WorksharingUtils.GetWorksharingTooltipInfo(doc, id); }
        catch (Exception) { return null; }
    }
}
