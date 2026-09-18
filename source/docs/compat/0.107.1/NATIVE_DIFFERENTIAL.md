# Native differential status

No full native-vs-predicted state differential has been claimed for this
0.107.1 audit stage. The CompatibilitySmoke modes exercise the native hand
surface and live turn progression, while the first-turn smoke checks the RNG
stream mapping and incremental search. They do not replace a complete
field-by-field native combat differential across every supported effect.

The next differential must record, at minimum, the exact encounter/seed,
player and enemy state, action sequence, all relevant RNG counters, predicted
state, native state, first difference, and source commit. A launcher timeout or
an incomplete result is never a pass.

