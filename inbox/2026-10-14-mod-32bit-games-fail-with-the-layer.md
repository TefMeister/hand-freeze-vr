# 2026-10-14 (from `/lm manhunt-2003-vr`, dev PC): the layer breaks every 32-bit OpenXR game

**What happened.** Manhunt (32-bit) could not start OpenXR: `xrCreateInstance` returned -32. With
`XR_LOADER_DEBUG=all`, the 32-bit loader said it skipped `XR_APILAYER_FLATTOVR_hand_freeze` because
`hand_freeze_layer.dll` "is not a valid Win32 application" (error 193), then "Failed loading layer information",
and the instance failed `[verified-live 2026-10-14, n=1]`. Setting `DISABLE_HAND_FREEZE_LAYER=1` fixed it at once.

**Why.** The layer is registered under `HKCU\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit`. HKCU is shared by
32-bit and 64-bit programs (no WOW6432Node split there), so a 32-bit loader reads the same entry and finds a
64-bit DLL. A failed implicit layer fails the whole instance in that loader `[verified-live 2026-10-14, n=1]`.

**Affects:** every 32-bit OpenXR game on a PC with Hand Freeze installed (Manhunt, Alan Wake's 32-bit bridge)
`[inferred-static]`. Manhunt now switches the layer off for itself; other 32-bit games will not.

**Possible fixes (owner's choice):** ship a 32-bit `hand_freeze_layer.dll` too, with a 32-bit manifest registered
where only 32-bit loaders look; or register the 64-bit manifest under HKLM (64-bit view) instead of HKCU, so 32-bit
loaders do not see it (needs admin); or both.
