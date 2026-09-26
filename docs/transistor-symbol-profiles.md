# Transistor symbol selection — 1.3.6 test build

The user's 1.3.5 screenshots exposed six rectangular fallbacks and a related polarity gap:
the previous generic BJT matcher checked B/C/E but not NPN versus PNP. An export success
does not establish that the selected symbol is electrically correct.

## Selection and provenance

Exact native component matches remain first. Otherwise, a single transistor needs an
unambiguous NPN/PNP/NMOS/PMOS type and three distinct functional terminals. Package names,
the Q prefix, and pin count do not establish electrical type. Unknown/conflicting polarity,
duplicate terminals, arrays, optocouplers, prebiased transistors, Darlington, depletion-mode,
JFET and IGBT devices remain review cases rather than being flattened to a single BJT/FET.

The local verified profiles match **both LCSC reference and exact Manufacturer Part**.
They supply polarity absent from the cached EasyEDA category. They cross-check existing
source pin functions, rather than overriding conflicting labels. Only C105432 permits
blank/numeric source names: its manufacturer datasheet explicitly defines B=1, E=2, C=3.
No new network call is required during import; provenance is logged in
`symbol.transistor_binding` and retained in the offline audit.

| LCSC | Exact Manufacturer Part | Type | Pins 1 / 2 / 3 |
|---|---|---|---|
| [C10487](https://www.vishay.com/docs/68741/si2301cd.pdf) | SI2301CDS-T1-GE3 | P-channel MOSFET | G / S / D |
| [C15127](https://www.aosmd.com/pdfs/datasheet/AO3401A.pdf) | AO3401A | P-channel MOSFET | G / S / D |
| [C20917](https://www.aosmd.com/products/mosfets/low-voltage-mosfets-12v-30v/ao3400a) | AO3400A | N-channel MOSFET | G / S / D |
| [C8545](https://www.lcsc.com/datasheet/C8545.pdf) | 2N7002 | N-channel MOSFET | G / S / D |
| [C105432](https://www.lcsc.com/datasheet/C105432.pdf) | S8550(RANGE:120-200) | PNP | B / E / C |
| [C9634](https://www.lcsc.com/datasheet/C9634.pdf) | D882(RANGE:160-320) | NPN | B / C / E |
| [C20526](https://www.lcsc.com/product-detail/C20526.html) | MMBT3904(RANGE:100-300) | NPN | B / E / C |
| [C2145](https://www.lcsc.com/product-detail/C2145.html) | MMBT5551(RANGE:200-300) | NPN | B / E / C |
| [C2146](https://www.lcsc.com/product-detail/C2146.html) | S8050 J3Y(RANGE:200-350) | NPN | B / E / C |
| [C2150](https://www.lcsc.com/product-detail/C2150.html) | SS8050(RANGE:200-350) | NPN | B / E / C |
| [C6749](https://www.lcsc.com/product-detail/C6749.html) | S9013 J3(RANGE:200-350) | NPN | B / E / C |
| [C8512](https://www.lcsc.com/product-detail/C8512.html) | MMBT2222A 1P | NPN | B / E / C |
| [C8326](https://www.lcsc.com/product-detail/C8326.html) | MMBT5401(RANGE:200-300) | PNP | B / E / C |
| [C8542](https://www.lcsc.com/product-detail/C8542.html) | SS8550 Y2(RANGE:200-350) | PNP | B / E / C |
| [C8543](https://www.lcsc.com/product-detail/C8543.html) | S9012 2T1(RANGE:200-350) | PNP | B / E / C |

Supplier polarity records reviewed 2026-09-25. The cached explicit functional pins supply
the mapping except the numeric-only S8550, independently checked against its datasheet.
D882's B/C/E mapping is also independently confirmed against its manufacturer datasheet.

## Artwork

- NMOS: `DIODES_INC_MOSFET_N-CH_SOT-23-3` native artwork.
- PMOS: `DIODES_MOSFET_P-CH_SOT-23-3` native artwork (different channel arrow/body diode).
- NPN: `DIODES_INC_NPN_SOT-23-3` native artwork, remapped by B/C/E function.
- PNP: a documented variant of that NPN drawing, reversing **only the emitter arrow**
  about the native emitter branch midpoint (50, -65 mil). Its tip becomes (0, -30 mil),
  pointing into the base, as in the collection's `SCH - BJT- ON SEMI PNP SOT BEC.PNG`.
  The inspected BJT source directory contains NPN SchLib records but no PNP SchLib record.
  This is a derived variant, not an unchanged native PNP record.

Bindings operate on fresh instances, never cached/source records. Output artwork uses
Altium **Small** width (style 1 / 2 mil); other geometry remains unchanged. Donor-specific
parameters and footprint/simulation models are removed before the imported part's own
metadata is attached. Catalog membership remains 70; the PNP variant is applied on load.

## Acceptance

Tests cover both MOSFET polarities, BJT emitter-arrow direction, D882 pin permutation,
S8550 numeric-pin evidence, unknown/conflicting identities, cached-template immutability,
native save/reopen, Small width, and KiCad export. The 351-part offline audit produces
native preview PNGs and a review SchLib in a fresh output directory. One/three-worker
acceptance must retain identical symbol geometry and pin mappings.

These tests do not constitute Altium GUI visual acceptance. The 3D-placement issue remains
outside this change; no PCB footprint, model pose, or reference-library file is modified.
