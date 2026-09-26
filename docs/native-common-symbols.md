# Native common symbol automatic selection (1.3.7)

## Tantalum capacitors

C16133/TAJB107K006RNJ and C7171/TAJA106K016RNJ reuse `Polarised_Capacitor`.
Both exact manufacturer identities are checked. Reviewed EasyEDA schematic primitives
have pin 1 at x=30, pin 2 at x=0, and the positive cross at x=23 (on pin 1's side).
C16133 cross: horizontal (25,-4)-(21,-4), vertical (23,-6)-(23,-2).
C7171 cross: horizontal (21,-5)-(25,-5), vertical (23,-7)-(23,-3).
Their PCB pin 1 is the left-hand pad adjacent to the polarity band; C7171 also has an
explicit left-hand plus silkscreen. This establishes pin 1 positive / pin 2 negative
from source data, not merely a conventional numbering assumption.

The [KYOCERA AVX TAJ datasheet, first page](https://datasheets.kyocera-avx.com/TAJ.pdf)
identifies the polarity band as anode-positive. The native drawing's plus cross is beside
its left terminal 1 at (-100,0) mil; the right terminal 2 is negative. Native curved plate
artwork and Small strokes remain intact. Unknown numeric-only parts or conflicting roles
remain reviewable, even if their packages look identical.

## Optocouplers

The user's catalog contains `Optoisolator`. Its native drawing has the input LED anode
at terminal 1, cathode at 2, NPN emitter at 3, and collector at 4. Inspected native
coordinates: 1=(-180,0), 2=(-180,-200), 3=(80,-200), 4=(80,0), in mils.
The input cathode bar is at y=-140; the emitter arrow is on the lower output branch.

Reviewed identities (both supplier ID and manufacturer part must match):

| LCSC | Manufacturer part | Source labels 1/2/3/4 |
|---|---|---|
| C109227 | LTV-817S-TA1-C | 1/2/3/4; roles from manufacturer pin diagram |
| C115450 | LTV-217-B-G | AN/CAT/EM/COL; explicit source roles |

LITEON's [LTV-817 series datasheet, printed page 5](https://www.datasheets.com/lite-on/ltv-817s-b/datasheet.pdf)
identifies 1=anode, 2=cathode, 3=emitter, 4=collector for the S package.
The [LTV-2X7 manufacturer datasheet](https://optoelectronics.liteon.com/upload/download/DS70-2009-0016/LTV-2X7%20sereis%20201610.pdf)
is the corresponding LTV-217 reference; the verified cached C115450 pin labels independently
give the same functions. The supplier identity is [LCSC C115450](https://www.lcsc.com/product-detail/C115450.html).
The manufacturer-hosted PDFs timed out during this review; no claim is made that the entire
latest datasheets were retrieved. The LTV-817 manufacturer-authored mirror and source pin
data were readable. Electrical ratings are not copied from the native donor.

Only these reviewed exact optocoupler identities auto-bind; other output topologies and
conflicting labels do not receive this mapping just because they have four terminals.

## Crystals

- Passive quartz category, two numbered terminals with passive/numeric/OSC/XTAL labels:
  reuse `Abracon_Crystal_ABLS`; bind its two electrodes to both original terminal numbers.
- Passive quartz category, four terminals with one OSC1, one OSC2 and two GND labels:
  reuse `Abracon_Crystal_ABM8G`. Native 1/3 are crystal electrodes; 2/4 are case grounds.
  Bind by source roles, so a different source numbering cannot swap a ground and electrode.
- C13738, C9002 and C9006 have OSC1=1, OSC2=3, GND=2/4 in the cached payload.
- Powered oscillators, TCXO/VCXO/OCXO, MEMS, resonators, unknown numeric-only four-pin
  devices and multi-unit symbols are not inferred from pin count. They remain outside this rule.

These are artwork reuse decisions, not replacements of the imported manufacturer part.
Donor ratings/models are cleared. The existing 71-symbol catalog remains unchanged.
Tests check geometry equality, functional mapping, swapped numbering, Small strokes,
native save/reopen, conflicting input rejection and cached-template immutability.
