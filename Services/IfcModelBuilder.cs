using Microsoft.Extensions.DependencyInjection;
using Rhino3dmToIfc.Console.Models;
using Xbim.Common;
using Xbim.Common.Configuration;
using Xbim.Common.Step21;
using Xbim.Ifc;
using Xbim.Ifc4.ActorResource;
using Xbim.Ifc4.DateTimeResource;
using Xbim.Ifc4.GeometricConstraintResource;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MaterialResource;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.RepresentationResource;
using Xbim.Ifc4.SharedBldgElements;
using Xbim.Ifc4.UtilityResource;
using Xbim.IO;

namespace Rhino3dmToIfc.Console.Services;

public sealed class IfcModelBuilder
{
    private readonly LogService _log;
    private readonly IfcGeometryBuilder _geometryBuilder;
    private readonly IfcPropertySetBuilder _propertySetBuilder;

    public IfcModelBuilder(LogService log, IfcGeometryBuilder geometryBuilder, IfcPropertySetBuilder propertySetBuilder)
    {
        _log = log;
        _geometryBuilder = geometryBuilder;
        _propertySetBuilder = propertySetBuilder;
    }

    public void BuildAndSave(IReadOnlyList<RhinoBimObject> objects, IfcExportOptions options, ExportSummary summary)
    {
        ConfigureXbim();

        var credentials = new XbimEditorCredentials
        {
            ApplicationDevelopersName = "Rhino3dmToIfc",
            ApplicationFullName = "Rhino3dmToIfc.Console",
            ApplicationIdentifier = "Rhino3dmToIfc.Console",
            ApplicationVersion = "1.0",
            EditorsFamilyName = "Console",
            EditorsGivenName = "Rhino3dmToIfc",
            EditorsOrganisationName = "Rhino3dmToIfc"
        };

        using var model = IfcStore.Create(credentials, XbimSchemaVersion.Ifc4, XbimStoreType.InMemoryModel);
        using (var transaction = model.BeginTransaction("Create IFC4 model"))
        {
            var ownerHistory = CreateOwnerHistory(model);
            var context = CreateGeometricRepresentationContext(model);
            var project = CreateProject(model, ownerHistory, context);
            var site = CreateSpatialElement<IfcSite>(model, ownerHistory, "Default Site");
            var building = CreateSpatialElement<IfcBuilding>(model, ownerHistory, "Default Building");

            Relate(model, ownerHistory, project, site, "Project contains site");
            Relate(model, ownerHistory, site, building, "Site contains building");

            var storeys = new Dictionary<string, IfcBuildingStorey>(StringComparer.OrdinalIgnoreCase);
            var materials = new Dictionary<string, IfcMaterial>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in objects)
            {
                if (!source.IsSupportedGeometry)
                {
                    summary.SkippedObjects++;
                    continue;
                }

                try
                {
                    var product = CreateProduct(model, ownerHistory, source);
                    product.ObjectPlacement = CreateLocalPlacement(model);

                    if (!_geometryBuilder.TryAttachMeshRepresentation(product, source, context, out var skipReason))
                    {
                        source.SkipReason = skipReason;
                        summary.SkippedObjects++;
                        continue;
                    }

                    var storey = GetOrCreateStorey(model, ownerHistory, building, storeys, source.IfcStorey);
                    Contain(model, ownerHistory, storey, product);
                    AssociateMaterial(model, ownerHistory, product, source.IfcMaterial, materials);
                    _propertySetBuilder.AttachPropertySets(product, source, summary);

                    summary.ExportedObjects++;
                    _log.Info($"Exported {source.RhinoObjectId} as {source.IfcType} '{source.IfcName}'.");
                }
                catch (Exception ex)
                {
                    summary.FailedObjects++;
                    _log.Error($"Failed to export object {source.RhinoObjectId}.", ex);
                }
            }

            transaction.Commit();
        }

        model.SaveAs(options.OutputPath);
        _log.Info($"Saved IFC file: {options.OutputPath}");
    }

    private static void ConfigureXbim()
    {
        if (!XbimServices.Current.IsConfigured)
        {
            XbimServices.Current.ConfigureServices(services => services.AddXbimToolkit());
        }
    }

    private static IfcOwnerHistory CreateOwnerHistory(IfcStore model)
    {
        var organization = model.Instances.New<IfcOrganization>();
        organization.Name = "Rhino3dmToIfc";

        var person = model.Instances.New<IfcPerson>();
        person.FamilyName = "Console";
        person.GivenName = "Rhino3dmToIfc";

        var personAndOrganization = model.Instances.New<IfcPersonAndOrganization>();
        personAndOrganization.ThePerson = person;
        personAndOrganization.TheOrganization = organization;

        var application = model.Instances.New<IfcApplication>();
        application.ApplicationDeveloper = organization;
        application.ApplicationFullName = "Rhino3dmToIfc.Console";
        application.ApplicationIdentifier = "Rhino3dmToIfc.Console";
        application.Version = "1.0";

        var ownerHistory = model.Instances.New<IfcOwnerHistory>();
        ownerHistory.OwningApplication = application;
        ownerHistory.OwningUser = personAndOrganization;
        ownerHistory.ChangeAction = IfcChangeActionEnum.ADDED;
        ownerHistory.CreationDate = new IfcTimeStamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return ownerHistory;
    }

    private static IfcProject CreateProject(IfcStore model, IfcOwnerHistory ownerHistory, IfcGeometricRepresentationContext context)
    {
        var project = model.Instances.New<IfcProject>();
        project.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        project.OwnerHistory = ownerHistory;
        project.Name = "Rhino3dm IFC4 Export";
        project.RepresentationContexts.Add(context);
        project.UnitsInContext = CreateUnitAssignment(model);
        return project;
    }

    private static IfcUnitAssignment CreateUnitAssignment(IfcStore model)
    {
        var unitAssignment = model.Instances.New<IfcUnitAssignment>();
        var lengthUnit = model.Instances.New<IfcSIUnit>();
        lengthUnit.UnitType = IfcUnitEnum.LENGTHUNIT;
        lengthUnit.Name = IfcSIUnitName.METRE;
        unitAssignment.Units.Add(lengthUnit);
        return unitAssignment;
    }

    private static IfcGeometricRepresentationContext CreateGeometricRepresentationContext(IfcStore model)
    {
        var context = model.Instances.New<IfcGeometricRepresentationContext>();
        context.ContextIdentifier = "Body";
        context.ContextType = "Model";
        context.CoordinateSpaceDimension = 3;
        context.Precision = 1e-5;
        context.WorldCoordinateSystem = CreateAxisPlacement(model);
        return context;
    }

    private static T CreateSpatialElement<T>(IfcStore model, IfcOwnerHistory ownerHistory, string name)
        where T : IfcSpatialStructureElement, IInstantiableEntity
    {
        var element = model.Instances.New<T>();
        element.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        element.OwnerHistory = ownerHistory;
        element.Name = name;
        element.ObjectPlacement = CreateLocalPlacement(model);
        return element;
    }

    private static IfcBuildingStorey GetOrCreateStorey(
        IfcStore model,
        IfcOwnerHistory ownerHistory,
        IfcBuilding building,
        Dictionary<string, IfcBuildingStorey> storeys,
        string storeyName)
    {
        var normalizedName = string.IsNullOrWhiteSpace(storeyName) ? "Level 1" : storeyName.Trim();
        if (storeys.TryGetValue(normalizedName, out var existing))
        {
            return existing;
        }

        var storey = CreateSpatialElement<IfcBuildingStorey>(model, ownerHistory, normalizedName);
        Relate(model, ownerHistory, building, storey, $"Building contains {normalizedName}");
        storeys[normalizedName] = storey;
        return storey;
    }

    private static IfcProduct CreateProduct(IfcStore model, IfcOwnerHistory ownerHistory, RhinoBimObject source)
    {
        IfcProduct product = source.IfcType switch
        {
            "IfcWall" => model.Instances.New<IfcWall>(),
            "IfcSlab" => model.Instances.New<IfcSlab>(),
            "IfcColumn" => model.Instances.New<IfcColumn>(),
            "IfcBeam" => model.Instances.New<IfcBeam>(),
            "IfcDoor" => model.Instances.New<IfcDoor>(),
            "IfcWindow" => model.Instances.New<IfcWindow>(),
            "IfcRoof" => model.Instances.New<IfcRoof>(),
            "IfcStair" => model.Instances.New<IfcStair>(),
            "IfcRailing" => model.Instances.New<IfcRailing>(),
            "IfcCovering" => model.Instances.New<IfcCovering>(),
            _ => model.Instances.New<IfcBuildingElementProxy>()
        };

        product.GlobalId = source.IfcGlobalId;
        product.OwnerHistory = ownerHistory;
        product.Name = string.IsNullOrWhiteSpace(source.IfcName) ? source.ObjectName : source.IfcName;
        if (!string.IsNullOrWhiteSpace(source.IfcDescription))
        {
            product.Description = source.IfcDescription;
        }

        return product;
    }

    private static void Relate(IfcStore model, IfcOwnerHistory ownerHistory, IfcObjectDefinition relatingObject, IfcObjectDefinition relatedObject, string name)
    {
        var relation = model.Instances.New<IfcRelAggregates>();
        relation.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        relation.OwnerHistory = ownerHistory;
        relation.Name = name;
        relation.RelatingObject = relatingObject;
        relation.RelatedObjects.Add(relatedObject);
    }

    private static void Contain(IfcStore model, IfcOwnerHistory ownerHistory, IfcBuildingStorey storey, IfcProduct product)
    {
        var relation = model.Instances.New<IfcRelContainedInSpatialStructure>();
        relation.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        relation.OwnerHistory = ownerHistory;
        relation.Name = $"Storey contains {product.Name}";
        relation.RelatingStructure = storey;
        relation.RelatedElements.Add(product);
    }

    private static void AssociateMaterial(
        IfcStore model,
        IfcOwnerHistory ownerHistory,
        IfcProduct product,
        string materialName,
        Dictionary<string, IfcMaterial> materials)
    {
        if (string.IsNullOrWhiteSpace(materialName))
        {
            return;
        }

        if (!materials.TryGetValue(materialName, out var material))
        {
            material = model.Instances.New<IfcMaterial>();
            material.Name = materialName.Trim();
            materials[materialName] = material;
        }

        var relation = model.Instances.New<IfcRelAssociatesMaterial>();
        relation.GlobalId = IfcGloballyUniqueId.ConvertToBase64(Guid.NewGuid());
        relation.OwnerHistory = ownerHistory;
        relation.RelatingMaterial = material;
        relation.RelatedObjects.Add(product);
    }

    private static IfcLocalPlacement CreateLocalPlacement(IfcStore model)
    {
        var placement = model.Instances.New<IfcLocalPlacement>();
        placement.RelativePlacement = CreateAxisPlacement(model);
        return placement;
    }

    private static IfcAxis2Placement3D CreateAxisPlacement(IfcStore model)
    {
        var origin = model.Instances.New<IfcCartesianPoint>();
        origin.SetXYZ(0, 0, 0);

        var placement = model.Instances.New<IfcAxis2Placement3D>();
        placement.Location = origin;
        return placement;
    }
}
