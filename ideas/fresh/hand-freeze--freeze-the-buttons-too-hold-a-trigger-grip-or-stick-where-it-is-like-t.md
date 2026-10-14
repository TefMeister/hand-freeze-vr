# Freeze the buttons too: hold a trigger, grip or stick where it is, like the hands

Order: 19000
From: the ideas repo, `games/hand-freeze.md` (<https://github.com/TefMeister/mod-ideas/blob/main/games/hand-freeze.md>), copied 2026-10-14

`[raw]` · `[looks doable]` — ⚠️ **checked against Hand Freeze's own design only, not built or tried in a game yet.**

> "do you think there is a way to also make buttons freeze, like the controllers do in the hand freeze program?"

Next to freezing where the hands are, freeze what the fingers are doing: keep the trigger pulled (or
half-pulled), the grip squeezed, a stick pushed one way, or a button held, while the controllers lie on the
desk. Useful for tuning poses that only happen while a button is held, such as aiming, a held grip on the
forestock, or a finger curled on the trigger.

**What it'd take:** Hand Freeze's add-on already sits in the path where games ask the headset where the
controllers are. Games ask about buttons through the same add-on point, so the add-on can answer those too:
remember each button's value at the end of the countdown and keep giving that answer until UN-FREEZE.
In the window, probably a tick box per row ("buttons too") or a separate snowflake for buttons. Note that
it only works for games that use OpenXR, the same limit Hand Freeze already has: RE2 through REFramework
may use OpenVR instead, where Visceral's own b169 tick box is the route.
