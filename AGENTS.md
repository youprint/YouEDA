# YouEDA repository guidance

## Release builds

- Version 1.4.0 is the desktop release. Keep assembly/file versions and release notes
  synchronized; diagnostics must derive the version from the assembly, not a literal.
- Apply both documented AltiumSharp patches on the README-pinned commit. The SDK
  SourceLink patch removes the obsolete explicit 8.0.0 package; use a serviced .NET 10
  SDK and keep vulnerability auditing enabled. Do not suppress NU190x for releases.
- Ship the full Windows x64 framework-dependent folder, licenses and SHA-256 checksum.
  Keep private component caches, CSVs, diagnostics and generated user libraries out of Git.

## Schematic-symbol policy

- User-confirmed exception (1.3.7): automatically choose the existing EasyEDA fallback for
  LDO/voltage-regulator category/type metadata, before automatic native matching, without a
  picker prompt. Preserve the seven explicit choices C14289, C5446, C58069, C6186, C6187,
  C71136 and C3113 even if metadata is sparse. CJ431/C3113 is an individual voltage-reference
  preference, not permission to classify every reference as an LDO. Manual overrides still win.
  This fallback generates a readable body from source pins, not the original EasyEDA artwork.
  Keep all source pin names/numbers, including tabs; do not substitute guessed regulator pinouts.
- Reuse native Optoisolator automatically for reviewed C109227/LTV-817S-TA1-C and
  C115450/LTV-217-B-G: 1=A, 2=K, 3=E, 4=C. Numeric-only pins require the exact reviewed
  identity; reject conflicts. Passive two-pin crystals and four-pin crystals with explicit
  OSC1/OSC2 plus two GND pins use native crystal artwork with function-bound numbering.
  Do not flatten active oscillators or multi-unit devices. Preserve Small and fresh-instance
  isolation; see docs/native-common-symbols.md and NativeCommonSymbolTests.
- C16133/TAJB107K006RNJ and C7171/TAJA106K016RNJ use native Polarised Capacitor,
  pin 1 positive, pin 2 negative, as verified from their source artwork and footprint marks.
  Require exact identities for numeric-only polarity; do not apply this convention to all
  tantalum parts. Test source polarity conflicts and the native plus-marker side.
- Treat the user's native Altium `.SchLib` collection as the source of truth. Resolve an exact
  component from `Desktop\Library` before choosing a generic common symbol or drawing an EasyEDA
  fallback.
- Preserve native symbol artwork, pin mapping, arcs, text parameters, and Altium line styles.
  Do not replace a matching source symbol with a guessed/generated approximation.
- Use the bundled common catalog only for a safe family match with compatible pin mapping.
  EasyEDA-generated schematic geometry is the last fallback.
- The bundled catalog contains 71 curated, native symbols. Changes to its membership must be
  verified by reopening the generated `.SchLib` and updating the smoke-test expectation.
- Altium schematic line-size enum mapping is: `0 = Smallest`, `1 = Small`, `2 = Medium`,
  `3 = Large`. When reproducing native line work, preserve the source style; do not assume the
  numeric value from an older API description.

- Quality checks must distinguish pin-number compatibility from datasheet-certified electrical
  correctness. Reject known polarity/function conflicts; never choose the first record of a
  multi-symbol library just because its filename matches, or use package name as component identity.
- Two-terminal diode templates have numeric hidden pin names. Only explicitly reviewed template
  artwork roles may be bound to source A/K (or Anode/Cathode, diode-only A/C or +/-) functions.
  Preserve each source pin number; never assume pin 1 is cathode. Modify fresh instances only,
  never cached/native source records. Ordinary diode, LED, Zener and Schottky artwork are distinct;
  unknown polarity, TVS variants, bridges and arrays must not be guessed from two terminals alone.
- Ferrite beads retain the native Ferrite Chip symbol. Their description must say Ferrite bead,
  even with an L designator; the L prefix does not make them ordinary coil inductors.
- Reviewed diode networks/TVS/bridges/four-pin switches require exact supplier+MPN profiles,
  not pin-count matching. See `docs/discrete-network-symbol-profiles.md`. Preserve topology,
  every terminal and permanent switch pairs; PSM712 is not a common-cathode diode pair.
  Test drawn connectivity, save/reopen and Small strokes. Keep unreviewed variants in review.
  Remove donor parameters from both the model and serialized read-order output.
- Transistor matching must prove NPN/PNP/NMOS/PMOS, not just B/C/E or G/S/D. Use exact
  supplier+MPN profiles or explicit electrical metadata and bind source numbers by function.
  Numeric-only pins require a documented per-part datasheet mapping (currently C105432).
  Reject conflicting polarity/functions, arrays and specialised transistors. See
  `docs/transistor-symbol-profiles.md` for the reviewed PNP arrow variant and profile sources.
  Operate on fresh copies, preserve native geometry, use Small strokes, strip donor models,
  and test arrow direction plus save/reopen, not merely successful export counts.
- Symbol-picker tests must use the view-model's real combined directory/catalog search path.
  An empty catalog must not silently act like a user-approved fallback. Log actual picker display,
  result and unavailable-catalog cases separately from prompt scheduling.
- Native inductors may use elliptical arcs (RECORD=11), not circular arcs. Never discard those
  coils as "missing artwork". Preserve their Small width and native core bars. Generated ordinary
  coils require exactly two terminals; beads, coupled coils and transformers must not lose pins.
- SchLib graphic vertices use 10 mil DXP units plus `_FRAC`, not 0.1 mil units. Reader/writer tests
  must include native authored dimensions, not just self-consistent round trips. Do not restore
  preview-only 100x scaling heuristics. Elliptical-arc LINEWIDTH is a style index, not a DXP length.
- The ignored AltiumSharp checkout needs `patches/altium-native-symbol-coordinates.patch` on
  the README-pinned revision. Keep this tracked patch synchronized with dependency source edits.
- Run `--audit-symbol-quality <csv> <cache> <new-json-report>` through KiCadSmoke for the 351-part
  offline audit. Keep review-needed decisions explicit; matching terminals alone is not certification.

## CSV/text GUI import contract

- The GUI importer accepts plain LCSC references matching `C` plus digits, such as `C11702`.
- Separators are newline, comma, semicolon, tab, and space. Duplicate references are removed.
- Prefer one unquoted LCSC code per row. A heading and extra CSV columns are ignored.
- Do not quote a part code (`"C11702"`) until the parser is deliberately upgraded to handle CSV
  quoting. For audit lists, retain `Quantity`, `Value`, `Description`, `JLCPCB Category`, and
  `Notes` alongside the unquoted `LCSC Part` column.
- For the user's JLCPCB Basic Parts library, use the supplied 351-part CSV as the primary
  acceptance dataset. If a family is ambiguous, verify it using LCSC/JLCPCB and the part's
  EasyEDA payload before selecting or changing a symbol.

## JLCPCB price snapshots

- Select the lowest valid purchase-quantity tier, never an unqualified JSON-LD bulk offer
  when tiers exist. Normalize old cached snapshots on consumption without changing timestamps.
  Retain `JLCPCB Price Tier Quantity` and `JLCPCB Price Basis`: quantity-one availability is
  different from a per-piece estimate at supplier MOQ. Clear stale price fields on replacement.
- Store `JLCPCB Unit Price` as the **per-one-component** price and set `JLCPCB Price Quantity`
  to `1`. Preserve the supplier's actual purchase constraint separately in
  `JLCPCB Minimum Order Quantity`, plus the full `JLCPCB Price Tiers` field.
- Label the source and timestamp. Public LCSC/JLCPCB-supply-chain data is a changing supplier
  snapshot, not a binding PCBA quote. Price lookup is best-effort and must never block CAD import.

## Large-batch family libraries

- For manual exports of more than five resolved rows, offer exactly one optional organization
  choice. The default remains normal shared-library output; keep small manual exports normal.
  Run full import always uses family-based output without an organization prompt, regardless
  of batch size or selected Altium/KiCad format (1.3.2+).
- Family mode is additive: retain the normal combined library and create a separate timestamped
  run under the selected output's `Family Libraries` folder. Use paired family directory/file
  names such as `Resistors\Resistors.SchLib` and `Resistors\Resistors.PcbLib`.
- `Desktop\Library` is a read-only naming/format reference. Never write, rename, delete, or
  overwrite a file there, and reject an output directory equal to or below that root.
- Classify from strong component metadata first (tags/category/type/description/manufacturer),
  then designator prefix. Do not infer a family from package alone. Assign exactly one family and
  send uncertain items to `Other` rather than presenting a per-component category prompt.
- Write `family-classification.csv` and `import-summary.json` in every family run. Preserve every
  original LCSC `C…` reference and report confidence/evidence for later review.
- Altium family mode (1.3+) uses up to three exclusive whole-family workers, with a one-worker
  fallback. Never share mutable symbol/library instances across family writers. Use the OS
  scheduler, not forced CPU affinity. Normal output and KiCad stay serial.
- Share one three-slot model-download batch across all families, never one download pool per
  worker. Preserve network pacing/backoff. Complete and checkpoint automatic work before prompts.
- Prioritize active families' current model dependencies ahead of two-part look-ahead and CSV
  prefetch. Never cancel/restart in-flight models just to change queue order. Report waiting,
  converting, and saving separately; assigned jobs do not imply busy CPU cores.
- Fetch a model UUID once per batch and reuse its immutable payload, retaining each part's own
  pose. Use validated seven-day UUID-keyed STEP/OBJ cache entries; don't negative-cache failures.
  Reuse identical named footprint geometry within a writer; changed geometry/model pose must
  invalidate reuse. Keep every LCSC symbol and parameter. Do not skip an uncached per-part CAD
  request solely because the package name is familiar: the payload also supplies its symbol.
- Merge verified family artifacts into combined libraries only after every family writer joins.
  Preserve unrelated existing entries and all native symbol styles, opt-outs, and STEP metadata.
  Remap colliding embedded-model IDs; resolve duplicate footprints by import order, not task order.
- Verify both combined files before publishing and retain previous files in the unique family
  run. Attempt rollback on partial publication failure; do not describe the pair as crash-atomic
  or imply automatic GUI resume. Record successes/failures in `conversion-results.json`.

## Validation

- C2488 and C32677 composed networks use authored 50-mil-grid connection points
  (1.3.10). Preserve bridge polarity, PSM712 IO1/IO2/GND and 12V/7V branches,
  passive pins, Small strokes, native donor shapes and pad-enclosing courtyards.
  Do not replace these with raw A1/A1/K labels or generic per-pin snapping.

- KiCad 1.3.9 family finalization must update filenames, unique family nicknames, symbol
  Footprint properties and both tables together. Generate aggregate tables at the run root.
  Validate two or more families together, model resolution from an unrelated project,
  symbol-only exports and overwrite refusal. Normal upserts preserve existing user tables.
- Newly exported KiCad STEP links and library tables use absolute installed paths, not
  consuming-project-relative paths. Clearly document regeneration/relinking after a move.
- Grid correction is a per-unit rigid translation only when every pin fits the same
  50-mil grid phase. Move artwork with pins; preserve radii, lengths, numbers and source
  objects. Leave incompatible units unchanged with a diagnostic; never independently snap.
- KiCad changes must retain wire-tip pin anchors (native Altium locations are body roots),
  correct EasyEDA pin-type enums, non-mirrored pad angles and Y-up 3D offsets with inverse
  stored Euler angles. Run KiCadGeometryTests and verify a real KiCad netlist connection.
  Layer 99 is F.Fab body artwork; generate a separate pad-enclosing F.CrtYd envelope.
  Preserve Small strokes, foreground arrowhead fill and all available source/price metadata.
  Honor all-hidden source pin-name visibility without deleting electrical names. Keep source
  designator prefixes. Route documentation/library markers to non-silkscreen layers; exclude
  documentation-only geometry from the physical courtyard bounds.
  See docs/kicad-quality-validation.md for offline 351-part validation and STEP measurements.
  Do not automatically centre all models, snap all pins, fabricate missing datasheet URLs,
  or claim that pin-number matching and successful rendering certify electrical correctness.

- Keep global import options in Settings, not as additional batch-panel checkboxes. Preserve
  per-component footprint/3D checkboxes in the results table and show a compact settings summary,
  especially when CAD refresh bypasses cache. Lock import settings during active jobs; retain
  existing preference lifetimes. Test real Settings bindings, busy state, and compact layout.
- Tooltips must use matching themed background/text colours, not the OS tooltip surface with
  application-styled text. Keep long help text wrapped and verify rendered popup contrast in
  all three themes, including after changing the active theme.
- Do not restore the removed Altium/KiCad library viewer workspaces or dedicated loaders.
  Preserve both exporters and the main importer's previews. Agent-facing CLI work is deferred
  until desktop issues are resolved; do not document unsupported CLI import commands as working.
- Desktop performance policy: 3 CAD workers, 2 independent pricing workers, and 3 model
  download workers. Preserve shared per-host request-start pacing and `Retry-After` cooldowns;
  never accelerate native `.SchLib`/`.PcbLib` output with concurrent writes to one file.
- Keep GUI Altium libraries open for a run, checkpoint every 10 processed parts per writer and before
  deferred prompts/final completion. Verify temporary output before replacing each file.
- Honor the 7-day validated CAD cache, explicit refresh option, and cancellation. Join all
  workers before enabling export; starting a new search clears the visible results list.
- Keep the live speed indicator on a one-second UI timer (even when requests stall). Show
  average throughput, elapsed time, and estimated remaining time; exclude symbol-prompt
  waiting time from conversion throughput. Never claim a measured speedup without a benchmark.
- Run batch concurrency/cache/cancellation/export/speed tests in `KiCadSmoke` and the
  real-XAML `UiLayoutSmoke` after changing this pipeline.
- Run family scheduler/merge tests, including locked-target rollback, model collisions, native
  style equivalence, and per-part failure isolation. Use `--validate-cached-families` with the
  user's 351-part CSV and existing raw cache for offline one/three-worker acceptance. Test output
  must be a new directory, never the user's active export. Clearly separate offline timing from
  full import timing including 3D downloads.
- Diagnostic logs are opt-in, local JSON lines under LocalAppData/YouEDA/Logs. Include phase
  start/end/duration, IDs, cache reuse, request status/backoff, queue priority, waits, checkpoint
  and merge events. Emit five-second unfinished-operation heartbeats. Never dump CAD/STEP,
  headers, credentials, or arbitrary exception response bodies. Logging failure cannot fail
  import. Preserve cancelled/failed outcome distinctions; an operation ending isn't success.
- Full import must await all lookup/pricing workers before exporting. Stop search or empty
  results must prevent automatic export. Capture format/output, prevent reentrancy, preserve
  matching checkbox choices, and retain necessary deferred-symbol prompts. Full import bypasses
  only the organization prompt and automatically selects family libraries. Test small/large
  automatic batches and normal/family/cancel choices for manual exports.
- Run delayed-network/model-cache/reuse/logging/full-workflow tests as well as offline geometry
  acceptance and real-XAML checks after changing the 1.3.1 pipeline.

- Run `dotnet run --project tests\KiCadSmoke\KiCadSmoke.csproj` after symbol-catalog or export
  changes. Verify exact native source matching and catalog count as part of that run.
- For visual schematic/footprint fixes, inspect the generated library in Altium Designer and
  compare against the native source and relevant LCSC/JLCPCB record before publishing.
