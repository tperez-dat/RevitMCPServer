using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

public sealed class GetProjectInfoHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetProjectInfo;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var info = doc.ProjectInformation;
        var app = doc.Application;

        return new JsonObject
        {
            ["title"] = doc.Title,
            ["name"] = info?.Name,
            ["number"] = info?.Number,
            ["clientName"] = info?.ClientName,
            ["address"] = info?.Address,
            ["author"] = info?.Author,
            ["buildingName"] = info?.BuildingName,
            ["organizationName"] = info?.OrganizationName,
            ["organizationDescription"] = info?.OrganizationDescription,
            ["status"] = info?.Status,
            ["issueDate"] = info?.IssueDate,
            ["path"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
            ["isWorkshared"] = doc.IsWorkshared,
            ["isFamilyDocument"] = doc.IsFamilyDocument,
            ["isReadOnly"] = doc.IsReadOnly,
            ["isModified"] = doc.IsModified,
            ["revitVersion"] = app.VersionNumber,
            ["revitBuild"] = app.VersionBuild,
            ["revitName"] = app.VersionName,
            ["displayUnitSystem"] = doc.DisplayUnitSystem.ToString(),
            ["activePhase"] = ActivePhaseName(doc),
            ["phaseCount"] = doc.Phases.Size
        };
    }

    /// <summary>The phase of the active view, which is what "current phase" means to a user.</summary>
    private static string? ActivePhaseName(Document doc)
    {
        try
        {
            var view = doc.ActiveView;
            var phaseParam = view?.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (phaseParam is null) return null;

            return doc.GetElement(phaseParam.AsElementId()) is Phase phase ? phase.Name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
