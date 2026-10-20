using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Revisions in sequence, with how many clouds reference each.
///
/// A revision with no clouds is the common finding: it was created, issued on the sheet schedule,
/// and nothing was ever marked against it.
/// </summary>
public sealed class GetRevisionsHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetRevisions;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;

        // Clouds carry the revision they belong to, so one pass gives usage for every revision.
        var cloudsByRevision = new Dictionary<long, int>();
        foreach (var cloud in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevisionCloud))
                     .Cast<RevisionCloud>())
        {
            var id = cloud.RevisionId.Value;
            cloudsByRevision[id] = cloudsByRevision.GetValueOrDefault(id) + 1;
        }

        var rows = new JsonArray();
        var unused = 0;
        var issued = 0;

        // GetAllRevisionIds returns them in sequence order, which is the order that means anything.
        foreach (var id in Revision.GetAllRevisionIds(doc))
        {
            if (doc.GetElement(id) is not Revision revision) continue;

            var cloudCount = cloudsByRevision.GetValueOrDefault(id.Value);
            if (cloudCount == 0) unused++;
            if (revision.Issued) issued++;

            rows.Add(new JsonObject
            {
                ["id"] = id.Value,
                ["sequenceNumber"] = revision.SequenceNumber,
                ["revisionNumber"] = revision.RevisionNumber,
                ["description"] = revision.Description,
                ["date"] = revision.RevisionDate,
                ["issued"] = revision.Issued,
                ["issuedBy"] = revision.IssuedBy,
                ["issuedTo"] = revision.IssuedTo,
                ["visibility"] = revision.Visibility.ToString(),
                ["cloudCount"] = cloudCount,
                ["hasNoClouds"] = cloudCount == 0
            });
        }

        return new JsonObject
        {
            ["revisions"] = rows,
            ["count"] = rows.Count,
            ["issuedCount"] = issued,
            ["withNoCloudsCount"] = unused,
            ["totalClouds"] = cloudsByRevision.Values.Sum(),
            ["note"] = "A revision with no clouds still appears on every sheet's revision schedule."
        };
    }
}

/// <summary>
/// View filters, and which views apply them. A filter applied nowhere is clutter; one applied in a
/// hundred views is load-bearing and should not be edited casually.
/// </summary>
public sealed class ListFiltersHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListFilters;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var usage = new Dictionary<long, List<string>>();

        // Templates count as users: a filter applied only through a template is still in service.
        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            ICollection<ElementId> applied;
            try { applied = view.GetFilters(); }
            catch (Exception) { continue; }   // not every view supports filters

            foreach (var filterId in applied)
            {
                if (!usage.TryGetValue(filterId.Value, out var list))
                    usage[filterId.Value] = list = [];

                list.Add(Json.SafeName(view) ?? $"view {view.Id.Value}");
            }
        }

        var filters = new FilteredElementCollector(doc)
            .OfClass(typeof(ParameterFilterElement))
            .Cast<ParameterFilterElement>()
            .ToList();

        var rows = filters.Select(filter =>
        {
            var users = usage.TryGetValue(filter.Id.Value, out var list) ? list : [];

            var categories = new JsonArray();
            try
            {
                foreach (var categoryId in filter.GetCategories())
                    categories.Add(Category.GetCategory(doc, categoryId)?.Name ?? categoryId.Value.ToString());
            }
            catch (Exception) { /* a filter may have no readable category set */ }

            var views = new JsonArray();
            foreach (var name in users.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(25))
                views.Add(name);

            return (JsonNode?)new JsonObject
            {
                ["id"] = filter.Id.Value,
                ["name"] = Json.SafeName(filter),
                ["categories"] = categories,
                ["usedInViewCount"] = users.Count,
                ["isUnused"] = users.Count == 0,
                ["usedInViews"] = views
            };
        })
        .OrderByDescending(r => r!["usedInViewCount"]!.GetValue<int>())
        .ToList();

        var result = Json.Page("filters", rows.Skip(offset).Take(limit), rows.Count, offset, limit);
        result["unusedCount"] = rows.Count(r => r!["isUnused"]!.GetValue<bool>());
        result["note"] = "Views applying a filter are capped at 25 names each; the count is complete.";
        return result;
    }
}

/// <summary>View templates and how many views each governs. One governing nothing is dead.</summary>
public sealed class ListViewTemplatesHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListViewTemplates;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();

        var governed = new Dictionary<long, int>();
        foreach (var view in allViews.Where(v => !v.IsTemplate))
        {
            var templateId = view.ViewTemplateId;
            if (templateId == ElementId.InvalidElementId) continue;
            governed[templateId.Value] = governed.GetValueOrDefault(templateId.Value) + 1;
        }

        var rows = allViews
            .Where(v => v.IsTemplate)
            .Select(template =>
            {
                var count = governed.GetValueOrDefault(template.Id.Value);
                return (JsonNode?)new JsonObject
                {
                    ["id"] = template.Id.Value,
                    ["name"] = Json.SafeName(template),
                    ["viewType"] = template.ViewType.ToString(),
                    ["governedViewCount"] = count,
                    ["isUnused"] = count == 0
                };
            })
            .OrderByDescending(r => r!["governedViewCount"]!.GetValue<int>())
            .ToList();

        var result = Json.Page("templates", rows.Skip(offset).Take(limit), rows.Count, offset, limit);
        result["unusedCount"] = rows.Count(r => r!["isUnused"]!.GetValue<bool>());

        var unTemplated = allViews.Count(v => !v.IsTemplate
                                              && v.ViewTemplateId == ElementId.InvalidElementId
                                              && v is not ViewSheet);
        result["viewsWithNoTemplate"] = unTemplated;
        return result;
    }
}

/// <summary>
/// An inventory of graphic and annotation styles: line patterns, fill patterns, text types and
/// dimension types. Counts rather than a verdict — GET_PURGEABLE already says which are unused,
/// and duplicating that judgement here would only risk disagreeing with it.
/// </summary>
public sealed class ListStylesHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListStyles;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();
        var kind = context.Args.StringOr("kind", "all").ToLowerInvariant();

        var rows = new List<JsonObject>();

        void Collect(string label, Type type, Func<Element, int?>? usage = null)
        {
            if (kind != "all" && kind != label.ToLowerInvariant()) return;

            foreach (var element in new FilteredElementCollector(doc).OfClass(type))
            {
                var row = new JsonObject
                {
                    ["id"] = element.Id.Value,
                    ["name"] = Json.SafeName(element),
                    ["kind"] = label
                };

                if (usage is not null) row["instanceCount"] = usage(element);
                rows.Add(row);
            }
        }

        // Instance counts are cheap for annotation types and genuinely useful; line and fill
        // patterns have no instances to count, so none is claimed.
        var textNotes = CountByType<TextNote>(doc);
        var dimensions = CountByType<Dimension>(doc);

        Collect("textType", typeof(TextNoteType), e => textNotes.GetValueOrDefault(e.Id.Value));
        Collect("dimensionType", typeof(DimensionType), e => dimensions.GetValueOrDefault(e.Id.Value));
        Collect("linePattern", typeof(LinePatternElement));
        Collect("fillPattern", typeof(FillPatternElement));

        var ordered = rows
            .OrderBy(r => r["kind"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(r => r["name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byKind = new JsonObject();
        foreach (var group in ordered.GroupBy(r => r["kind"]!.GetValue<string>()))
            byKind[group.Key] = group.Count();

        var result = Json.Page("styles", ordered.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            ordered.Count, offset, limit);

        result["byKind"] = byKind;
        result["note"] = "Instance counts are reported for text and dimension types only. " +
                         "For what is unused and purgeable, call GET_PURGEABLE.";
        return result;
    }

    private static Dictionary<long, int> CountByType<T>(Document doc) where T : Element
    {
        var counts = new Dictionary<long, int>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(T)))
        {
            var typeId = element.GetTypeId().Value;
            counts[typeId] = counts.GetValueOrDefault(typeId) + 1;
        }
        return counts;
    }
}
