using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Sets the active selection.
///
/// No transaction: selection is UI state, not document state.
/// </summary>
public sealed class SetSelectionHandler : IBridgeCommandHandler
{
    public string Command => Commands.SetSelection;

    public JsonNode? Execute(CommandContext context)
    {
        var uiDoc = context.UiDoc
                    ?? throw new BridgeException(BridgeErrorCode.NoDocument,
                        "No document is open, so nothing can be selected.");

        var doc = uiDoc.Document;
        var requested = context.Args.ElementIds("elementIds");

        // Selecting a non-existent id throws; report which ids were bad instead, so the caller can
        // correct the list rather than guessing which entry was wrong.
        var valid = new List<ElementId>(requested.Count);
        var missing = new JsonArray();

        foreach (var id in requested)
        {
            if (doc.GetElement(id) is not null) valid.Add(id);
            else missing.Add(id.Value);
        }

        if (valid.Count == 0)
        {
            throw new BridgeException(BridgeErrorCode.NotFound,
                "None of the given element ids exist in this document, so the selection was not changed.");
        }

        uiDoc.Selection.SetElementIds(valid);

        // Optionally bring the selection into view, which is usually the point of selecting it.
        if (context.Args.Bool("showElements"))
        {
            try { uiDoc.ShowElements(valid); }
            catch (Exception ex) { BridgeLog.Warn("ShowElements failed after SET_SELECTION.", ex); }
        }

        return new JsonObject
        {
            ["selectedCount"] = valid.Count,
            ["requestedCount"] = requested.Count,
            ["missingElementIds"] = missing
        };
    }
}

/// <summary>
/// Activates a view.
///
/// No transaction, deliberately: setting <see cref="UIDocument.ActiveView"/> throws if a
/// transaction is open, which is why UI writes are not wrapped by the dispatcher.
/// </summary>
public sealed class OpenViewHandler : IBridgeCommandHandler
{
    public string Command => Commands.OpenView;

    public JsonNode? Execute(CommandContext context)
    {
        var uiDoc = context.UiDoc
                    ?? throw new BridgeException(BridgeErrorCode.NoDocument,
                        "No document is open, so no view can be activated.");

        var view = Resolve(context, uiDoc.Document);

        if (view.IsTemplate)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"'{Json.SafeName(view)}' is a view template and cannot be opened.");

        // Schedules and legends are activatable; sheets are too. Non-graphical internal views
        // (project browser organisation, system browser) are not, and Revit throws on them.
        var previous = Json.SafeName(uiDoc.ActiveView);

        try
        {
            uiDoc.ActiveView = view;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"Revit refused to activate '{Json.SafeName(view)}' ({view.ViewType}). " +
                "Some view kinds cannot be made active.", ex.Message);
        }

        return new JsonObject
        {
            ["activated"] = true,
            ["viewId"] = view.Id.Value,
            ["name"] = Json.SafeName(view),
            ["viewType"] = view.ViewType.ToString(),
            ["previousView"] = previous
        };
    }

    /// <summary>Accepts a view id or a view name.</summary>
    private static View Resolve(CommandContext context, Document doc)
    {
        if (context.Args.LongOrNull("viewId") is { } id)
        {
            return doc.GetElement(new ElementId(id)) as View
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a view.");
        }

        var name = context.Args.StringOrNull("name")
                   ?? throw new BridgeException(BridgeErrorCode.BadRequest,
                       "Pass 'viewId' or 'name' to identify the view.");

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
                $"'{name}' matches {matches.Count} views. Pass 'viewId' to disambiguate.")
        };
    }
}

/// <summary>
/// Temporarily isolates elements in a view, or clears an existing isolate.
///
/// Opens its own transaction: temporary hide/isolate is an undoable document change, but it sits
/// under <see cref="CommandKind.UiWrite"/> because it alters only how a view displays.
/// </summary>
public sealed class IsolateInViewHandler : IBridgeCommandHandler
{
    public string Command => Commands.IsolateInView;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var view = ResolveView(context, doc);

        if (view.IsTemporaryHideIsolateActive() is false && context.Args.Bool("reset"))
        {
            return new JsonObject
            {
                ["cleared"] = true,
                ["viewId"] = view.Id.Value,
                ["note"] = "No temporary isolate was active in this view."
            };
        }

        using var transaction = new Transaction(doc, "MCP: ISOLATE_IN_VIEW");
        transaction.Start();

        try
        {
            JsonObject result;

            if (context.Args.Bool("reset"))
            {
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                result = new JsonObject
                {
                    ["cleared"] = true,
                    ["viewId"] = view.Id.Value,
                    ["viewName"] = Json.SafeName(view)
                };
            }
            else
            {
                var requested = context.Args.ElementIds("elementIds");

                // Temporary isolate needs elements that are actually visible in this view;
                // ids from elsewhere in the model are silently ignored by Revit, so filter first.
                var visible = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .ToHashSet();

                var isolatable = requested.Where(visible.Contains).ToList();
                var notInView = requested.Where(id => !visible.Contains(id)).Select(id => id.Value).ToList();

                if (isolatable.Count == 0)
                {
                    throw new BridgeException(BridgeErrorCode.InvalidState,
                        $"None of the given elements are visible in '{Json.SafeName(view)}', " +
                        "so there is nothing to isolate. Check the view's category visibility, " +
                        "phase, and crop region, or isolate in a different view.");
                }

                view.IsolateElementsTemporary(isolatable);

                var skipped = new JsonArray();
                foreach (var id in notInView) skipped.Add(id);

                result = new JsonObject
                {
                    ["isolated"] = true,
                    ["viewId"] = view.Id.Value,
                    ["viewName"] = Json.SafeName(view),
                    ["isolatedCount"] = isolatable.Count,
                    ["skippedNotInView"] = skipped,
                    ["note"] = "This is a temporary isolate; it is cleared by Revit's " +
                               "'Reset Temporary Hide/Isolate', or by calling this tool with 'reset': true."
                };
            }

            transaction.Commit();
            return result;
        }
        catch
        {
            if (!transaction.HasEnded()) transaction.RollBack();
            throw;
        }
    }

    private static View ResolveView(CommandContext context, Document doc)
    {
        var view = context.Args.LongOrNull("viewId") is { } id
            ? doc.GetElement(new ElementId(id)) as View
              ?? throw new BridgeException(BridgeErrorCode.NotFound, $"Element {id} is not a view.")
            : doc.ActiveView
              ?? throw new BridgeException(BridgeErrorCode.InvalidState, "There is no active view.");

        // Temporary hide/isolate is meaningless outside a graphical view.
        if (view is ViewSchedule or ViewSheet || view.ViewType is ViewType.Schedule or ViewType.DrawingSheet)
        {
            throw new BridgeException(BridgeErrorCode.InvalidState,
                $"'{Json.SafeName(view)}' is a {view.ViewType}; temporary isolate only works in " +
                "graphical views such as plans, sections, elevations, and 3D views.");
        }

        return view;
    }
}
