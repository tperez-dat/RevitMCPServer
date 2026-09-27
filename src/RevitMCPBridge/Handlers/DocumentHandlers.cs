using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Saves the active document.
///
/// Runs outside a transaction: Revit refuses a save while one is open, which is why SAVE_MODEL is a
/// DocumentWrite rather than a ModelWrite. This never does a Save As — choosing where a model
/// lives is the user's decision, not an assistant's.
/// </summary>
public sealed class SaveModelHandler : IBridgeCommandHandler
{
    public string Command => Commands.SaveModel;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;

        if (doc.IsReadOnly)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                "The document is read-only and cannot be saved.");

        if (string.IsNullOrEmpty(doc.PathName))
        {
            throw new BridgeException(BridgeErrorCode.InvalidState,
                "This model has never been saved, so it has no file path. Ask the user to save it " +
                "once from Revit; after that SAVE_MODEL can keep it saved.");
        }

        // A workshared model saves locally; central needs SYNC_WITH_CENTRAL, so be explicit
        // about which of the two just happened.
        if (!doc.IsModified)
        {
            return new JsonObject
            {
                ["saved"] = false,
                ["path"] = doc.PathName,
                ["isWorkshared"] = doc.IsWorkshared,
                ["note"] = "The model had no unsaved changes, so nothing was written."
            };
        }

        try
        {
            doc.Save();
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            throw new BridgeException(BridgeErrorCode.RevitException,
                "Revit could not save the model. It may be open read-only, the file may be locked, " +
                "or a Revit dialog may be waiting.", ex.Message);
        }

        return new JsonObject
        {
            ["saved"] = true,
            ["path"] = doc.PathName,
            ["isWorkshared"] = doc.IsWorkshared,
            ["note"] = doc.IsWorkshared
                ? "Saved the local file. Changes are not in central until SYNC_WITH_CENTRAL runs."
                : "Saved."
        };
    }
}

/// <summary>
/// Synchronises a workshared model with central.
///
/// Outside a transaction, like SAVE_MODEL. This can take minutes on a large model and may surface
/// Revit dialogs, so callers should allow a long timeout.
/// </summary>
public sealed class SyncWithCentralHandler : IBridgeCommandHandler
{
    public string Command => Commands.SyncWithCentral;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        if (!doc.IsWorkshared)
        {
            throw new BridgeException(BridgeErrorCode.InvalidState,
                "This model is not workshared, so there is no central model to synchronise with. " +
                "Use SAVE_MODEL instead.");
        }

        if (doc.IsReadOnly)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                "The document is read-only and cannot be synchronised.");

        var comment = args.StringOrNull("comment");

        // Default to relinquishing what this session owns, which is what a user means by
        // "sync" — holding borrowed elements after a sync blocks everyone else.
        var relinquish = new RelinquishOptions(false)
        {
            CheckedOutElements = args.Bool("relinquishBorrowedElements", true),
            StandardWorksets = args.Bool("relinquishStandardWorksets", true),
            ViewWorksets = args.Bool("relinquishViewWorksets", true),
            FamilyWorksets = args.Bool("relinquishFamilyWorksets", true),
            UserWorksets = args.Bool("relinquishUserWorksets", false)
        };

        var syncOptions = new SynchronizeWithCentralOptions
        {
            Comment = comment ?? "Synchronised via Revit MCP bridge",
            SaveLocalBefore = args.Bool("saveLocalBefore", true),
            SaveLocalAfter = args.Bool("saveLocalAfter", true)
        };

        syncOptions.SetRelinquishOptions(relinquish);

        var transactOptions = new TransactWithCentralOptions();

        try
        {
            doc.SynchronizeWithCentral(transactOptions, syncOptions);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            throw new BridgeException(BridgeErrorCode.RevitException,
                "Revit could not synchronise with central. Another user may hold the central model, " +
                "the network path may be unreachable, or there may be conflicting edits that need " +
                "resolving in Revit.", ex.Message);
        }

        return new JsonObject
        {
            ["synchronised"] = true,
            ["path"] = doc.PathName,
            ["comment"] = syncOptions.Comment,
            ["relinquishedBorrowedElements"] = relinquish.CheckedOutElements,
            ["relinquishedUserWorksets"] = relinquish.UserWorksets,
            ["savedLocalBefore"] = syncOptions.SaveLocalBefore,
            ["savedLocalAfter"] = syncOptions.SaveLocalAfter
        };
    }
}
