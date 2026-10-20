using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Views in which elements have been hidden one by one with "Hide in View".
///
/// This is the hardest of the documentation checks to catch by eye and the easiest to leave behind:
/// an element hidden in a view stays hidden when the model changes around it, so a drawing can go
/// out missing something nobody meant to omit. Category visibility and view filters are legitimate
/// and are not reported — only element-level hides, which is what <c>Element.IsHidden(view)</c>
/// answers.
///
/// There is no API that asks a view for its hidden elements, so the only way to answer is to test
/// candidate elements against each view. That is a product of two numbers, so both are capped and
/// both caps are reported: a count here is a lower bound whenever 'truncated' is set, never a
/// quietly wrong total.
/// </summary>
public sealed class ListViewsWithHiddenElementsHandler : IBridgeCommandHandler
{
    /// <summary>Views tested unless the caller raises it. Enough for a full sheet set.</summary>
    private const int DefaultMaxViews = 300;

    /// <summary>
    /// Candidate elements tested per view. The candidate set is built once and reused, so this is
    /// also the size of that set.
    /// </summary>
    private const int DefaultMaxElements = 20_000;

    public string Command => Commands.ListViewsWithHiddenElements;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;
        var (limit, offset) = args.Page();

        var maxViews = Math.Max(1, args.IntOrNull("maxViews") ?? DefaultMaxViews);
        var maxElements = Math.Max(1, args.IntOrNull("maxElements") ?? DefaultMaxElements);
        var viewTypeFilter = args.StringOrNull("viewType");
        var sampleSize = Math.Max(0, args.IntOrNull("sampleSize") ?? 10);

        var views = CandidateViews(doc, viewTypeFilter);
        var viewsTruncated = views.Count > maxViews;
        if (viewsTruncated) views = views.Take(maxViews).ToList();

        // One candidate set for every view. Built from model elements only: an annotation is
        // view-specific and hiding one is a drafting decision, not a coordination problem.
        var candidates = ModelElements(doc, maxElements, out var elementsTruncated);

        var rows = new List<JsonObject>();
        var totalHidden = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var view in views)
        {
            var hidden = new List<Element>();

            foreach (var element in candidates)
            {
                try
                {
                    if (element.IsHidden(view)) hidden.Add(element);
                }
                catch (Exception)
                {
                    // Not every element can be asked about every view; that is not a hidden element.
                }
            }

            if (hidden.Count == 0) continue;

            totalHidden += hidden.Count;

            var byCategory = hidden
                .GroupBy(e => e.Category?.Name ?? "(no category)")
                .OrderByDescending(g => g.Count())
                .ToList();

            var categories = new JsonArray();
            foreach (var group in byCategory)
                categories.Add(new JsonObject { ["category"] = group.Key, ["count"] = group.Count() });

            var sample = new JsonArray();
            foreach (var element in hidden.Take(sampleSize)) sample.Add(Json.ElementRef(element));

            rows.Add(new JsonObject
            {
                ["viewId"] = view.Id.Value,
                ["viewName"] = Json.SafeName(view),
                ["viewType"] = view.ViewType.ToString(),
                ["isTemplate"] = view.IsTemplate,
                ["hiddenCount"] = hidden.Count,
                ["byCategory"] = categories,
                ["sample"] = sample
            });
        }

        var ordered = rows
            .OrderByDescending(r => r["hiddenCount"]!.GetValue<int>())
            .ThenBy(r => r["viewName"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = Json.Page("views", ordered.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            ordered.Count, offset, limit);

        result["viewsWithHiddenElements"] = ordered.Count;
        result["viewsTested"] = views.Count;
        result["elementsTestedPerView"] = candidates.Count;
        result["totalHiddenInstances"] = totalHidden;
        result["elapsedMs"] = stopwatch.ElapsedMilliseconds;
        result["truncated"] = viewsTruncated || elementsTruncated;

        if (viewsTruncated || elementsTruncated)
        {
            var reasons = new List<string>();
            if (viewsTruncated) reasons.Add($"only the first {maxViews} views were tested");
            if (elementsTruncated) reasons.Add($"only the first {maxElements:N0} model elements were tested");

            result["note"] = string.Join("; ", reasons) +
                             ". Counts are therefore lower bounds. Raise 'maxViews'/'maxElements', " +
                             "or pass 'viewType' to narrow the search.";
        }
        else
        {
            result["note"] = "Element-level hides only. Hidden categories and view filters are " +
                             "not reported here: those are deliberate view settings, not leftovers.";
        }

        return result;
    }

    /// <summary>
    /// Views worth testing. Sheets and Revit's internal browser views are excluded; templates are
    /// included only when asked for, because a hide stored in a template is a template bug worth
    /// seeing separately from a per-view one.
    /// </summary>
    private static List<View> CandidateViews(Document doc, string? viewTypeFilter)
    {
        var includeTemplates = false;
        var wanted = viewTypeFilter;

        if (string.Equals(wanted, "templates", StringComparison.OrdinalIgnoreCase))
        {
            includeTemplates = true;
            wanted = null;
        }

        var views = new List<View>();

        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(View)))
        {
            if (element is not View view) continue;
            if (view.IsTemplate != includeTemplates) continue;
            if (view.ViewType is ViewType.Internal or ViewType.ProjectBrowser
                or ViewType.SystemBrowser or ViewType.DrawingSheet) continue;

            if (wanted is not null
                && !view.ViewType.ToString().Equals(wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            views.Add(view);
        }

        // Stable order, so paging and truncation are reproducible between calls.
        return views.OrderBy(v => v.Id.Value).ToList();
    }

    /// <summary>
    /// The candidate element set: model elements, in id order so the cap always takes the same
    /// slice. View-specific elements are excluded — a detail line hidden in its own view is not a
    /// finding.
    /// </summary>
    private static List<Element> ModelElements(Document doc, int max, out bool truncated)
    {
        var elements = new List<Element>();
        truncated = false;

        var collector = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .WhereElementIsViewIndependent();

        foreach (var element in collector)
        {
            if (element.Category is null) continue;

            if (elements.Count >= max) { truncated = true; break; }

            elements.Add(element);
        }

        return elements.OrderBy(e => e.Id.Value).ToList();
    }
}
