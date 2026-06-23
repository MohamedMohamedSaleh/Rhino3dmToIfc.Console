using Rhino.Geometry;
using Rhino3dmToIfc.Console.Models;
using Xbim.Ifc4.GeometricModelResource;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.RepresentationResource;

namespace Rhino3dmToIfc.Console.Services;

public sealed class IfcGeometryBuilder
{
    private readonly LogService _log;

    public IfcGeometryBuilder(LogService log)
    {
        _log = log;
    }

    public bool TryAttachMeshRepresentation(IfcProduct product, RhinoBimObject source, IfcGeometricRepresentationContext context, out string skipReason)
    {
        skipReason = string.Empty;

        if (source.Geometry is not Mesh mesh)
        {
            skipReason = source.SkipReason;
            return false;
        }

        if (mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
        {
            skipReason = "Mesh has no vertices or faces.";
            _log.Warning($"Skipping {source.RhinoObjectId}: {skipReason}");
            return false;
        }

        var model = product.Model;
        var pointList = model.Instances.New<IfcCartesianPointList3D>();
        foreach (var vertex in mesh.Vertices)
        {
            var coords = pointList.CoordList.GetAt(pointList.CoordList.Count);
            coords.Add(new IfcLengthMeasure(vertex.X));
            coords.Add(new IfcLengthMeasure(vertex.Y));
            coords.Add(new IfcLengthMeasure(vertex.Z));
        }

        var faceSet = model.Instances.New<IfcTriangulatedFaceSet>();
        faceSet.Coordinates = pointList;
        faceSet.Closed = mesh.IsClosed;

        var triangleCount = 0;
        foreach (var face in mesh.Faces)
        {
            if (face.IsTriangle)
            {
                AddTriangle(faceSet, face.A, face.B, face.C);
                triangleCount++;
            }
            else if (face.IsQuad)
            {
                AddTriangle(faceSet, face.A, face.B, face.C);
                AddTriangle(faceSet, face.A, face.C, face.D);
                triangleCount += 2;
            }
        }

        if (triangleCount == 0)
        {
            skipReason = "Mesh has no triangle or quad faces.";
            _log.Warning($"Skipping {source.RhinoObjectId}: {skipReason}");
            return false;
        }

        var shapeRepresentation = model.Instances.New<IfcShapeRepresentation>();
        shapeRepresentation.ContextOfItems = context;
        shapeRepresentation.RepresentationIdentifier = "Body";
        shapeRepresentation.RepresentationType = "Tessellation";
        shapeRepresentation.Items.Add(faceSet);

        var productShape = model.Instances.New<IfcProductDefinitionShape>();
        productShape.Representations.Add(shapeRepresentation);
        product.Representation = productShape;
        return true;
    }

    private static void AddTriangle(IfcTriangulatedFaceSet faceSet, int a, int b, int c)
    {
        var indices = faceSet.CoordIndex.GetAt(faceSet.CoordIndex.Count);
        indices.Add(new IfcPositiveInteger(a + 1));
        indices.Add(new IfcPositiveInteger(b + 1));
        indices.Add(new IfcPositiveInteger(c + 1));
    }
}
