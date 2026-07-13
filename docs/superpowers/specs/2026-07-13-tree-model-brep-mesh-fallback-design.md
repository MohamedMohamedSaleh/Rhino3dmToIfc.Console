# Tree Model Brep-First Geometry Export Design

## Goal

Improve Rhino-to-IFC export fidelity for `TREE MODEL.3DM`, especially for:

- curved branch transitions
- angled branch connections
- trimmed junction pieces
- non-planar or partially curved Brep faces

The exporter should prefer faithful Brep-derived body geometry and automatically fall back to mesh-based IFC bodies when Brep export is not reliable for a given object.

## Current State

`Services/IfcGeometryBuilder.cs` currently uses three main paths:

- curves are exported as `IfcPolyline`
- Breps are exported through polygonal face extraction, with a mesh fallback
- meshes and some other geometry are exported as `IfcTriangulatedFaceSet`

This works for simple objects, but the tree model contains curved and angled junction geometry that is sensitive to:

- coarse fixed trim sampling
- loop extraction assumptions that work best for planar faces
- all-or-nothing Brep polygon export at object level

These limits can produce weak tessellation, incomplete body generation, or skipped objects for complex transition pieces.

## Proposed Approach

Use a staged geometry exporter with a stronger Brep-first pipeline:

1. Export curves through the existing curve representation path.
2. Export Breps and surfaces through an improved Brep body pipeline.
3. If Brep body generation fails or produces unusable output, fall back to a higher-quality mesh body for that object.
4. Preserve detailed failure logging so unsupported edge cases are visible in the export log.

The key design principle is object preservation over perfection: a complex object should appear in IFC even when exact Brep-style export is not possible.

## Geometry Pipeline Design

### 1. Geometry routing

Refactor `IfcGeometryBuilder` so routing is explicit and easier to extend:

- `Curve -> Curve representation`
- `Brep/Surface -> Improved Brep body attempt`
- `Brep/Surface -> Adaptive mesh fallback`
- `Mesh/Extrusion -> Mesh body`

Surfaces should be converted to Breps first, then follow the same Brep pipeline.

### 2. Improved Brep body attempt

The Brep path should continue to prefer IFC body geometry derived from Brep topology, but with better preprocessing:

- adaptive sampling for trims and non-polyline curves
- stronger point cleanup for duplicates and near-collinear runs
- clearer separation between loop extraction, projection, triangulation, and IFC writing
- validation checks after each stage so one bad face does not silently poison the whole object

The exporter should treat Brep faces as candidates for export, but if the resulting body is too incomplete or unstable, it should abandon the Brep body for that object and switch to mesh fallback.

### 3. Adaptive mesh fallback

When Brep export is not dependable, generate a mesh body from Rhino geometry with better preparation than the current minimal fallback:

- prefer Rhino render/preview meshes when present
- generate Brep meshes for Breps and surfaces when face meshes are missing or sparse
- normalize and compact the combined mesh before IFC export
- ensure curved transitions and angled junctions keep enough tessellation density to remain visually faithful

This path should produce `IfcTriangulatedFaceSet` output and must be reliable enough to preserve all major geometry in the tree model.

## Refactor Structure

Split the current monolithic geometry builder into smaller internal stages inside `IfcGeometryBuilder`:

- geometry routing helpers
- Brep export helpers
- curve and trim sampling helpers
- mesh extraction and mesh preparation helpers
- shared point cleanup and validation helpers

This can stay in one file for now, but the responsibilities should be clearly separated so future geometry types can be added without growing the main method further.

## Failure and Fallback Rules

Fallback should occur when any of these happens:

- no usable loops can be extracted from enough Brep faces
- polygon construction or triangulation fails
- resulting Brep body has no valid faces
- sampled geometry is too sparse to represent the object safely
- mesh generation from the Brep produces a valid result when Brep body export does not

If both Brep export and mesh fallback fail:

- skip the object
- write the most specific failure reason available to the log

## Testing Strategy

Add focused geometry tests around the refactor, especially for:

- curve sampling producing stable polyline output
- trim sampling for curved edges
- loop cleanup removing duplicate and collinear noise without collapsing valid shapes
- Brep objects falling back to mesh export when polygonal export is not viable

Because the main validation target is `TREE MODEL.3DM`, verification should also include running the exporter against that file and checking the log plus resulting IFC size/coverage for skipped or failed objects.

## Success Criteria

The refactor is successful when:

- the tree model exports more reliably without losing the angled and curved branch junction pieces shown in the screenshots
- complex parts prefer Brep-derived output when possible
- objects that cannot be exported through the Brep path still appear through mesh fallback
- the export log clearly explains any remaining skipped objects

## Non-Goals

This change does not attempt to:

- reconstruct analytic IFC sweep solids for the branches
- classify pipe semantics from geometry alone
- guarantee perfect downstream parametric editability in every IFC viewer

The focus is faithful geometry export and robust fallback behavior.
