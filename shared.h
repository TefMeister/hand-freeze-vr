// shared.h -- the switch between the Hand Freeze app and the OpenXR layer: one small shared-memory block per Windows
// session. The app writes `frozen` and the generations; the layer (inside the game) writes the rest. The C# app reads
// and writes it by the byte offsets in the comments, so the layout must never change without bumping
// HAND_FREEZE_VERSION and the mapping name together.
// v2 (2026-10-13): LEFT, RIGHT and BOTH rows in the app, so `frozen` is a bit mask and each hand has its own generation.
// v3 (2026-10-14): `buttons`, the hands whose buttons, triggers, grips and sticks are held too (Tefa: "Hand Freeze needs
// the ability to freeze button presses holds as well"); the layer counts the input states it took in `inputs_held`.
// v4 (2026-10-25): the RIGHT STICK CLICK freezes and unfreezes both hands (Tefa: "is there also a way to make Rthumb
// freeze/unfreeze hands while the hand freeze program is running? it's bound to crouch in game ... so that when hand freeze
// is running, right thumbstick press doesn't crouch the player and freezes/unfreezes hands instead"). The app ticks it on
// (`stick_toggle`) and says whether a freeze holds the buttons too (`toggle_buttons`); it writes `app_heartbeat_ms`, so the
// click only works while the app is open. The layer counts its toggles in `toggle_seq`.
#pragma once
#include <windows.h>
#include <cstdint>

#define HAND_FREEZE_MAPPING_NAME L"Local\\FlatToVR_HandFreeze_v4"
#define HAND_FREEZE_MAGIC 0x5A465248u   // "HRFZ"
#define HAND_FREEZE_VERSION 4u
#define HAND_FREEZE_SIZE 208            // bytes; the app maps exactly this much (HandFreeze.cs SIZE)
#define HAND_FREEZE_LEFT 1              // bits of `frozen`
#define HAND_FREEZE_RIGHT 2

#pragma pack(push, 4)
struct HandFreezeShared {
    uint32_t magic;                  //   0
    uint32_t version;                //   4
    volatile LONG frozen;            //   8  app: HAND_FREEZE_LEFT | HAND_FREEZE_RIGHT bits held still
    volatile LONG generation_left;   //  12  app: +1 on every new freeze of the left hand, so the layer takes a fresh pose
    uint64_t layer_heartbeat_ms;     //  16  layer: GetTickCount64() at its last pose call
    uint32_t layer_pid;              //  24  layer: the game's process id
    volatile LONG controller_spaces; //  28  layer: controller spaces the game made
    volatile LONG poses_held;        //  32  layer: poses taken since the layer loaded (rises at each freeze)
    uint32_t game_name_pid;          //  36  layer: whose name is in game_name
    wchar_t game_name[64];           //  40  layer: the game's exe name
    volatile LONG generation_right;  // 168  app: the same for the right hand
    volatile LONG left_spaces;       // 172  layer: controller spaces known to be the left hand
    volatile LONG right_spaces;      // 176  layer: ... the right hand (the rest: hand not known, held only with BOTH)
    volatile LONG buttons;           // 180  app: the hands (same bits) whose buttons, triggers, grips and sticks are held too
    volatile LONG inputs_held;       // 184  layer: input states taken since the layer loaded (rises at each buttons freeze)
    volatile LONG stick_toggle;      // 188  app: 1 = the right stick click freezes / unfreezes both hands (and never reaches the game)
    volatile LONG toggle_buttons;    // 192  app: 1 = a freeze by the click holds the buttons too (the app's BUTTONS TOO box)
    volatile LONG toggle_seq;        // 196  layer: +1 at every freeze or unfreeze by the click (the app beeps)
    uint64_t app_heartbeat_ms;       // 200  app: GetTickCount64() at its last tick; the click works only while it is fresh
};                                   // 208 bytes
#pragma pack(pop)
static_assert(sizeof(HandFreezeShared) == HAND_FREEZE_SIZE, "the app reads this layout by offset");
