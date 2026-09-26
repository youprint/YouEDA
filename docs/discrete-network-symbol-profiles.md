# Reviewed discrete networks (1.3.6 test build)

These are exact LCSC-reference + manufacturer-part profiles, not rules based on a package,
filename, pin count, or a broad `Diode` category. A missing/different MPN, wrong terminal set,
duplicate terminal, multi-unit source, or unexpected source pin name keeps the part in review.
The six newly reported rectangular symbols, plus the related SMBJ6.5CA, are covered.

| LCSC / exact MPN | Topology and pin mapping | Evidence |
| --- | --- | --- |
| C2488 / MB10S-50MIL | Four-diode bridge; 1=+, 2=-, 3/4=AC. Diode paths 2→3→1 and 2→4→1. | [MDD datasheet, page 1](https://datasheet.lcsc.com/datasheet/pdf/d166b641999f7c4a86c69b88af3f89c6.pdf?productCode=C2488) |
| C2500 / BAV99,215 | Two series diodes; 1=A1, 2=K2, 3=K1/A2. Not common cathode. | [Nexperia, section 5](https://assets.nexperia.com/documents/data-sheet/BAV99.pdf) |
| C68978 / BAV70 | Two common-cathode diodes; 1=A1, 2=A2, 3=K1/K2. | [JSCJ diagram, page 1](https://datasheet.lcsc.com/datasheet/pdf/6e2e0793b76aba6348e571f5e0d12d7d.pdf?productCode=C68978) |
| C78395 / P6SMB6.8CA/TR13 | Two-terminal bidirectional TVS, not an ordinary diode. | [Supplier's polarity specification](https://www.lcsc.com/product-detail/C78395.html) |
| C7420377 / SMBJ6.5CA | Two-terminal bidirectional TVS. | [Supplier's polarity specification](https://www.lcsc.com/product-detail/C7420377.html) |
| C32677 / PSM712-LF-T7 | Two asymmetric anti-series avalanche pairs; pin 1=IO1, 2=IO2, 3=GND. The 12 V cathodes face IO, 7 V cathodes face GND. | [ProTek diagram and electrical table, pages 1–2](https://protekdevices.com/wp-content/uploads/datasheets/psm712.pdf) |
| C318884 / TS-1187A-B-A-B | Normally-open momentary switch. Permanent pairs A/B (1/2) and C/D (3/4); button joins pairs. | [XKB drawing, sheet 1](https://datasheet.lcsc.com/datasheet/pdf/56c8799ae5193945a16a1ffbe378246a.pdf?productCode=C318884) |

The XKB schematic uses terminal letters. Their mapping to footprint numbers is retained from
the part's original EasyEDA pin definition (1=A, 2=B, 3=C, 4=D); it is not a generic switch rule.
PSM712's cached `A1,A1,K` names are misleading for this network. Only this exact profile accepts
that known raw naming and substitutes documented IO1/IO2/GND. The raw component is never edited.
The 12V/7V drawing labels denote the manufacturer's stand-off branches, not clamping voltages.

## Native artwork and controlled adaptations

- BAV70 uses `DIODE_ARRAY_COMMON_CATHODE` without changing its network geometry.
- Bidirectional TVS parts use the reviewed `Diode_Zener_Bidirectional` native drawing. The
  similarly named `Diode_TVS` catalog record contains duplicate polygons and is not used here.
- The catalog grows from 70 to **71** by adding the actual `SPST-NO 4 PIN` native record from
  `C:\altium-library\symbols\Button`. Its stacked duplicate pins are separated in output copies
  with explicit connecting lines, retaining four visible connection points and native contacts.
- No matching series/bridge/PSM712 native records were found in the supplied collection.
  These networks compose copies of the native `Diode` or `Diode_Zener` polygons/polylines with
  rotations, translations and connecting lines. They do not redraw those diode primitives.
  The composition asserts the reviewed donor shape before use and fails if it changes.
- Output copies use **Small** (style index 1 / 2 mil) and passive pins. Donor models/parameters
  are discarded before the imported part's own metadata and footprint are assigned. The
  native writer patch prevents removed donor parameters from returning through read-order data.
- All source reference libraries remain read-only; every result is a separate instance.

## Verification and limits

### 1.3.10 connection-grid correction

C2488's composed bridge uses pin roots at (0, +/-250) and (+/-300, 0) mil,
with the original 100-mil pin lengths. Only connecting wires are extended; donor
diode shapes and bridge polarity are unchanged. C32677's two complete branches
move from +/-160 to +/-150 mil, retaining IO1/IO2/GND, passive pin types and 12V/7V
labels. This is an exact-profile layout correction, not generic per-pin snapping.
Both Altium and KiCad receive the corrected composition. Courtyard generation and
3D poses are unchanged. KiCad explicitly exports these two reviewed compositions'
separate polarity and 12V/7V text annotations while retaining hidden pin names.
`verify_network_grid.py` checks the exported labels, roles and grid, and tests all
seven wire-tip connections through a real KiCad netlist. Upstream uPesy output was used for comparison only; no code
or artwork was copied from it.

`DiscreteNetworkSymbolTests` checks topology by tracing artwork lines and diode terminals,
including bridge directions, BAV99's middle tap, independent PSM712 pairs, and normally-open
switch pairs. It also checks all pins, duplicate/missing/conflicting source rejection, opposite
TVS artwork, Small widths, immutable native templates, SchLib save/reopen, and KiCad strokes.
The 351-part offline audit and one-/three-worker export acceptance remain required.

Programmatic previews and file round trips are not Altium GUI acceptance. This is a test build
until the user confirms these symbols inside Altium. PCB/3D placement work remains paused.
