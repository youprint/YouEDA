# KiCad export quality corrections

The 351-part JLCPCB Basic list is the acceptance set. Existing output and reference
libraries must remain untouched: generate each acceptance run into a fresh folder.

## Conversion invariants

- Native Altium pin locations are body roots. KiCad pin locations are **wire tips**.
  Move by the pin length along the native orientation, then turn the pin inward by
  180 degrees. Preserve numbers and do not mutate the native symbol instance.
- EasyEDA pin positions are already wire tips. Preserve their relative geometry; convert
  the raw direction to `(rotation + 180) % 360`. EasyEDA electrical types are
  0 unspecified, 1 input, 2 output, 3 bidirectional, 4 power input. They are not the
  Altium enum. Unknown types remain unspecified, not invented passive terminals.
- A unit whose pins all share a 50-mil-grid phase may be translated as one rigid
  shape onto that grid. Artwork follows the identical translation; ellipse radii
  and pin lengths do not change. Incompatible spacing remains untouched and is
  logged as `symbol.grid_review`. Regression checks account for the permitted
  translation and still verify the electrical wire tip and direction.
- PCB positions use Y-down; schematic and 3D offsets use Y-up. KiCad pad orientation
  preserves the raw EasyEDA angle, including non-cardinal rotations. KiCad stored
  3D Euler angles have the opposite sign to EasyEDA's angles.
- Footprint arcs use start/mid/end points and preserve the sweep. Full circles are
  split into two arcs. Layer 99 is a body outline on F.Fab, not a courtyard.
  Generate a conservative F.CrtYd envelope covering rotated/custom pads, body,
  holes and graphics, with 0.25 mm clearance rounded outward to the 0.05 mm grid.
- Keep layer-12 documentation in Cmts.User, layer-15 mechanical drawings in
  Dwgs.User, and library pin-one markers (101) in F.Fab, consistent with their
  non-silkscreen treatment in the Altium exporter. Do not turn unknown layers into
  manufacturing silkscreen. Documentation/marker positions do not enlarge courtyards.
- Reference/value fields follow geometry bounds. Preserve source metadata and
  available price snapshots as hidden properties; keep the electrical value in
  `Component Value` when `Value` contains the MPN. Do not invent missing datasheets.
- Preserve the source component reference prefix. When all source pin names are
  hidden, use KiCad's symbol-wide pin-name visibility setting, retaining the actual
  names in the file. This avoids A/K and crystal/op-amp labels over the artwork.
  Mixed per-pin name visibility still needs review because KiCad's setting is global.
- Preserve native Small strokes (0.0508 mm) for polygons/ellipses as well as lines.
  Filled arrowheads use foreground fill; filled body rectangles use background fill.

## Repeatable tests

Run the complete .NET suite first:

```powershell
dotnet run --project tests/KiCadSmoke/KiCadSmoke.csproj
dotnet run --project tests/KiCadSmoke/KiCadSmoke.csproj -- --validate-cached-kicad <previous-audit-data.json> <CAD-cache> <new-library-directory>
dotnet run --project tests/KiCadSmoke/KiCadSmoke.csproj -- --validate-cached-kicad-families <previous-audit-data.json> <CAD-cache> <new-library-directory>
```

The cached acceptance mode is a **test harness**, not the proposed public import CLI.
It requires the existing native catalog, model cache and local pricing snapshots.
It does not run an online import or establish import throughput.
Snapshot application is entirely offline and retains original timestamps even for
expired cached prices; those acceptance values are not freshly fetched quotations.

Family acceptance must resolve each table URI and each symbol Footprint nickname,
with exactly one family assignment per component. Absolute STEP references must
resolve from a project outside the export tree. Combined-library upserts must
leave existing project tables untouched. Re-export/relink after relocating an
installed library; paths are no longer implicitly relative to `${KIPRJMOD}`.

With KiCad's Python and CLI:

```powershell
python tests/KiCadSmoke/verify_kicad_acceptance.py <new-library> <previous-audit-directory> <new-report-directory>
kicad-cli sch export netlist --output <report>/pin-anchor-test.net <report>/pin-anchor-test.kicad_sch
kicad-cli sym export svg --output <new-svg-directory> <new-library>/youeda.kicad_sym
kicad-cli fp export svg --layers "F.Cu,F.SilkS,F.Fab,F.CrtYd" --output <existing-new-svg-directory> <new-library>/youeda.pretty
```

In the resulting netlist, R1 pin 1 must belong to `VISIBLE_OUTER_END`. A text-only
pin-position assertion is not sufficient. The generated inspection boards cover
every distinct footprint/model/pose group from the previous audit. Run DRC and
top-view rendering on each, then export GLB with `pcb export glb --no-board-body`.
`inspect_glb_placement.py --manifest <audit-data.json> --report <new-json> <GLB-files>`
measures actual tessellated STEP bounds, retaining every part sharing each geometry.

## Remaining review boundaries

Matching pin/pad numbers does not certify electrical functions or package identity.
Generated conservative courtyards are not manufacturer-certified IPC land patterns.
Do not silently snap all symbol pins or centre every STEP model: either can destroy
intentional geometry. In particular, some cached EasyEDA SVGNODE origins are stale
or displaced, including C13482's approximately 400/300 origin in a 4000/3000
footprint canvas. Preserve evidence and report these rather than guessing a transform.
The GLB script's XY and Z thresholds are review flags, not proof that unflagged
models have correct lead orientation. Existing STEP data and per-part poses remain
separate from UUID-keyed asset caching.

Some source silkscreen crosses copper; removing those lines automatically would
also remove possible polarity marks. Keep the remaining DRC findings explicit.
Complex EasyEDA schematic SVG paths, VIA/TEXT footprint primitives and unsupported
region/cutout semantics still require review. Missing source datasheets remain empty.

## Reference-code review

[uPesy/easyeda2kicad.py](https://github.com/uPesy/easyeda2kicad.py) was inspected for
pin enums, pad angles, footprint arcs and 3D transforms. No Python implementation
was copied or bundled. Its AGPL-3.0 code is a format reference, not an oracle:
`export_kicad_3d_model.py` explicitly notes that STEP translation is not baked in
like WRL translation. Our STEP validation therefore uses KiCad's own tessellation.
KiCad's 3D renderer applies negative stored Euler rotations, independently confirming
the sign convention. Reference geometry should always be validated against KiCad
and the source part, not solely against another converter.
