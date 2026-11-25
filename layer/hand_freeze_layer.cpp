// hand_freeze_layer.cpp -- an OpenXR API layer that holds both controllers still on request, in any OpenXR game.
//
// Why (Tefa, 2026-10-13): tuning hand poses in VR means moving sliders with a mouse while the in-game hands stay where
// they should be. Holding two controllers in one hand while doing that is miserable. With this layer, the Hand Freeze
// desktop app counts down (so the controllers can be held comfortably in place), then every controller stays where it
// was at that moment, fixed in the ROOM; the headset keeps tracking, so the player can lean in and look around the
// hands. The game itself never pauses.
//
// How: the layer sits between the game and the OpenXR runtime (registered as an implicit layer by the app). Every
// space made by xrCreateActionSpace is a controller (or other tracked-device) pose; reference spaces (view, local,
// stage) are the headset and the room and are never touched. At the first pose call after a freeze begins, each
// controller's pose is taken in a LOCAL reference space the layer makes for that session (fixed in the room). While
// frozen, any request "controller relative to base" is answered as (base relative to LOCAL, live)^-1 x (kept pose), so
// a game asking relative to the head (VIEW) sees the hand stay in the room as the head moves, not stuck to the face.
// Velocities are zeroed. The switch is a small shared-memory block (shared.h) the app writes; the layer writes a
// heartbeat into it so the app can say which game is connected.
// v2 (2026-10-13): LEFT / RIGHT / BOTH. Each controller space is sorted into a hand once: by the subaction path it was
// made with (/user/hand/left or /right), else by where its action is bound (xrEnumerateBoundSourcesForAction). A space
// whose hand cannot be told is held only when both hands are frozen.
// v3 (2026-10-14): THE BUTTONS TOO. With the app's "BUTTONS TOO" box ticked, a freeze also holds what the fingers are
// doing: every action state the game asks for (xrGetActionStateBoolean / Float / Vector2f: buttons, triggers, grips,
// sticks) is taken once at the first ask after the freeze and answered the same way until UN-FREEZE, with
// changedSinceLastSync false. An action is sorted into a hand like a space: by the subaction path it is asked with,
// else by where it is bound; one bound to both hands (or to neither) is held only with BOTH. Poses (xrGetActionStatePose)
// are left alone: they only say whether a controller is active.
// v4 (2026-10-25): THE RIGHT STICK CLICK. The layer adds a tiny action set of its own (one button: the right thumbstick
// click) to the game's: its binding is appended to the game's own suggestions for the Touch, Index, WMR, Pico and Touch
// Pro / Plus controllers (xrSuggestInteractionProfileBindings), the set is attached beside the game's and synced with it
// (Window View 1.5's method). While the app's box is ticked and the app is open, each click freezes both hands (or
// unfreezes them if any are frozen), and every game button bound to the right stick click reads as not pressed, so the
// game never sees it (no crouch). A profile the game never suggests is left alone; if the runtime refuses ours, the game's
// own call is made again without it, so the game never pays for it. Our own reads go straight to the next layer.
// v5 (2026-11-25): PUT THE HANDS SOMEWHERE (shared.h, the second block). While a hand is frozen and the app gives it a pose, the
// game is told that pose instead of the kept one; a hand in `synth` has its grip, trigger and face buttons answered from the block
// and everything else as "not pressed". Which action is a grip, a trigger or a button is read from the game's own suggested
// bindings. Every frame the layer reports the head, the real controllers and what the game is told, in its room.
#define XR_NO_PROTOTYPES
#include <windows.h>
#include <openxr/openxr.h>
#include <openxr/openxr_loader_negotiation.h>

#include <cstring>
#include <map>
#include <mutex>
#include <set>
#include <type_traits>
#include <vector>

#include "../shared.h"

namespace {

PFN_xrGetInstanceProcAddr g_next_gipa = nullptr;
PFN_xrCreateActionSpace g_next_create_action_space = nullptr;
PFN_xrCreateReferenceSpace g_next_create_reference_space = nullptr;
PFN_xrDestroySpace g_next_destroy_space = nullptr;
PFN_xrDestroySession g_next_destroy_session = nullptr;
PFN_xrLocateSpace g_next_locate_space = nullptr;
PFN_xrLocateSpaces g_next_locate_spaces = nullptr;
PFN_xrLocateSpacesKHR g_next_locate_spaces_khr = nullptr;
PFN_xrPathToString g_next_path_to_string = nullptr;
PFN_xrEnumerateBoundSourcesForAction g_next_bound_sources = nullptr;
PFN_xrGetActionStateBoolean g_next_state_bool = nullptr;
PFN_xrGetActionStateFloat g_next_state_float = nullptr;
PFN_xrGetActionStateVector2f g_next_state_vec2 = nullptr;
PFN_xrStringToPath g_next_string_to_path = nullptr;
PFN_xrCreateActionSet g_next_create_action_set = nullptr;
PFN_xrCreateAction g_next_create_action = nullptr;
PFN_xrSuggestInteractionProfileBindings g_next_suggest = nullptr;
PFN_xrAttachSessionActionSets g_next_attach = nullptr;
PFN_xrSyncActions g_next_sync = nullptr;
XrInstance g_instance = XR_NULL_HANDLE;

enum Hand : int { HAND_NOT_LOOKED = 0, HAND_LEFT = 1, HAND_RIGHT = 2, HAND_UNKNOWN = 3 };
struct Controller {
    XrSession session; XrAction action; XrPath subaction;
    int hand = HAND_NOT_LOOKED;
    bool kept = false; LONG kept_generation = 0; XrPosef pose_in_room{};
};
// v3: one input = one action asked with one subaction path (a "grab" asked for the left hand and for the right are two)
struct Input {
    XrSession session; XrAction action; XrPath subaction;
    int hand = HAND_NOT_LOOKED;
    bool kept = false; LONG kept_generation = 0;
    XrActionStateBoolean b{}; XrActionStateFloat f{}; XrActionStateVector2f v{};   // the kept state, one of the three used
    float synth_last = -1.0f;        // v5: the last value answered from the pose block (for "changed since the last ask")
};
std::recursive_mutex g_mtx;
std::map<XrSpace, Controller> g_controllers;   // every space made by xrCreateActionSpace
std::map<XrSession, XrSpace> g_room;           // our own LOCAL space per session: the frame the kept poses live in
std::map<std::pair<XrAction, XrPath>, Input> g_inputs;   // every action state the game has asked for (v3)
// v4: the right stick click (see "v4: the right stick click" below)
std::map<XrSession, bool> g_ours_attached;
bool g_click_down = false;            // our right stick click, as read at the last sync
struct Hide { int bound = -1; ULONGLONG retry_ms = 0; bool hid_last = false; };   // bound: -1 not known yet, 0 no, 1 yes
std::map<std::pair<XrAction, XrPath>, Hide> g_hide;   // game buttons, and whether each is bound to the right stick click


// ---- pose maths (unit quaternions, metres) -----------------------------------------------------------------------
XrQuaternionf qmul(XrQuaternionf a, XrQuaternionf b) {
    return {a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z};
}
XrQuaternionf qinv(XrQuaternionf q) { return {-q.x, -q.y, -q.z, q.w}; }
XrVector3f rot(XrQuaternionf q, XrVector3f v) {
    const XrQuaternionf r = qmul(qmul(q, {v.x, v.y, v.z, 0}), qinv(q));
    return {r.x, r.y, r.z};
}
// pose of B in A's frame, given both in the room: inverse(a) x b
XrPosef relative(const XrPosef& a, const XrPosef& b) {
    const XrQuaternionf ia = qinv(a.orientation);
    const XrVector3f d{b.position.x - a.position.x, b.position.y - a.position.y, b.position.z - a.position.z};
    return {qmul(ia, b.orientation), rot(ia, d)};
}
constexpr XrSpaceLocationFlags kValid = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;

// ---- the shared switch -------------------------------------------------------------------------------------------
HandFreezeShared* g_shared = nullptr;

HandFreezeShared* shared() {
    if (g_shared != nullptr) return g_shared;
    HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(HandFreezeShared), HAND_FREEZE_MAPPING_NAME);
    if (h == nullptr) return nullptr;
    auto* p = static_cast<HandFreezeShared*>(MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(HandFreezeShared)));
    if (p == nullptr) { CloseHandle(h); return nullptr; }
    if (p->magic != HAND_FREEZE_MAGIC) { p->version = HAND_FREEZE_VERSION; p->magic = HAND_FREEZE_MAGIC; }   // first one in sets it up
    g_shared = p;   // the handle stays open for the life of the process, on purpose
    return p;
}

void heartbeat() {
    auto* s = shared();
    if (s == nullptr) return;
    s->layer_heartbeat_ms = GetTickCount64();
    s->layer_pid = GetCurrentProcessId();
    {
        std::lock_guard _{g_mtx};
        LONG left = 0, right = 0;
        for (const auto& c : g_controllers) { left += c.second.hand == HAND_LEFT; right += c.second.hand == HAND_RIGHT; }
        s->controller_spaces = static_cast<LONG>(g_controllers.size());
        s->left_spaces = left;
        s->right_spaces = right;
    }
    if (s->game_name[0] == 0 || s->game_name_pid != s->layer_pid) {
        wchar_t path[MAX_PATH]{};
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        const wchar_t* name = wcsrchr(path, L'\\');
        name = name ? name + 1 : path;
        wcsncpy_s(s->game_name, name, _TRUNCATE);
        s->game_name_pid = s->layer_pid;
    }
}

// which hands are held right now (HAND_FREEZE_LEFT / _RIGHT bits); 0 = none
LONG frozen_now() {
    auto* s = shared();
    return s == nullptr ? 0 : s->frozen;
}

int hand_of_path(XrPath path) {
    if (path == XR_NULL_PATH || g_next_path_to_string == nullptr) return HAND_UNKNOWN;
    char text[XR_MAX_PATH_LENGTH]{};
    uint32_t n = 0;
    if (XR_FAILED(g_next_path_to_string(g_instance, path, XR_MAX_PATH_LENGTH, &n, text))) return HAND_UNKNOWN;
    if (std::strncmp(text, "/user/hand/left", 15) == 0) return HAND_LEFT;
    if (std::strncmp(text, "/user/hand/right", 16) == 0) return HAND_RIGHT;
    return HAND_UNKNOWN;
}

// sort an action (a controller space's, or an input's) into a hand, once it can be told (bindings exist only after the
// game syncs its actions): by the subaction path, else by where the action is bound
int sort_hand(XrSession session, XrAction action, XrPath subaction) {
    int hand = hand_of_path(subaction);
    if (hand == HAND_UNKNOWN && g_next_bound_sources != nullptr) {
        XrBoundSourcesForActionEnumerateInfo info{XR_TYPE_BOUND_SOURCES_FOR_ACTION_ENUMERATE_INFO};
        info.action = action;
        XrPath sources[16]{};
        uint32_t n = 0;
        if (XR_SUCCEEDED(g_next_bound_sources(session, &info, 16, &n, sources)) && n > 0) {
            int seen = hand_of_path(sources[0]);
            for (uint32_t i = 1; i < n && i < 16; ++i) if (hand_of_path(sources[i]) != seen) seen = HAND_UNKNOWN;
            hand = seen;
        }
    }
    return hand;
}

int hand_of(Controller& c) {
    if (c.hand == HAND_LEFT || c.hand == HAND_RIGHT) return c.hand;
    return c.hand = sort_hand(c.session, c.action, c.subaction);
}

// is this hand held under `mask` (HAND_FREEZE_LEFT / _RIGHT bits)? a hand that cannot be told: only with both
bool wanted_hand(int hand, LONG mask) {
    const LONG both = HAND_FREEZE_LEFT | HAND_FREEZE_RIGHT;
    return hand == HAND_LEFT ? (mask & HAND_FREEZE_LEFT) != 0 : hand == HAND_RIGHT ? (mask & HAND_FREEZE_RIGHT) != 0 : (mask & both) == both;
}

LONG generation_of(int hand) {
    auto* s = shared();
    return hand == HAND_LEFT ? s->generation_left : hand == HAND_RIGHT ? s->generation_right : s->generation_left * 65536 + s->generation_right;
}

// v3: which hands' buttons are held right now; 0 = none
LONG buttons_now() {
    auto* s = shared();
    return s == nullptr ? 0 : s->buttons;
}

// v6 (2026-11-25, Tefa: "when i froze hands i could not then use the pointers"): ANOTHER LAYER'S OWN BUTTONS ARE NEVER HELD.
// A layer nearer the game (Window View) reads its own actions through this one; with BUTTONS TOO its stick click was kept
// at "not pressed" like one of the game's, so its pointers could not be brought out while a hand was frozen. Such actions
// are known by their name's start when they are made (xrCreateAction passes through here), and are answered live, always.
std::set<XrAction> g_not_the_games;
bool other_layers_action(const char* name) {
    static const char* const kStarts[] = {"wv_"};   // Window View (window-view-vr layer/controllers.inl)
    for (const char* s : kStarts) if (std::strncmp(name, s, std::strlen(s)) == 0) return true;
    return false;
}

// The held answer for an action state. `live` is the runtime's answer this call; on the first ask after a freeze it is
// kept (if the action is active) and passed through, every later ask gets the kept state back with "not changed".
template <class T>
void held_input(XrSession session, const XrActionStateGetInfo* info, T& live, T Input::*slot) {
    std::lock_guard _{g_mtx};
    const LONG mask = buttons_now();
    if (mask == 0 || info == nullptr) return;
    if (g_not_the_games.count(info->action) != 0) return;   // v6: another layer's own button
    auto& in = g_inputs[{info->action, info->subactionPath}];
    if (in.session == XR_NULL_HANDLE) { in.session = session; in.action = info->action; in.subaction = info->subactionPath; }
    if (in.hand != HAND_LEFT && in.hand != HAND_RIGHT) in.hand = sort_hand(session, info->action, info->subactionPath);
    if (!wanted_hand(in.hand, mask)) return;
    const LONG generation = generation_of(in.hand);
    if (in.kept && in.kept_generation != generation) in.kept = false;   // a new freeze: fresh state
    if (!in.kept) {
        if (!live.isActive) return;   // nothing to keep yet (the controller is asleep or not bound)
        in.*slot = live;
        in.kept = true;
        in.kept_generation = generation;
        InterlockedIncrement(&shared()->inputs_held);
        return;   // the first answer is the live one, the same one we keep
    }
    T out = in.*slot;
    out.type = live.type;
    out.next = live.next;
    out.changedSinceLastSync = XR_FALSE;
    live = out;
}

// ---- v5: the given poses ---------------------------------------------------------------------------------------------
// what each of the game's actions is bound to, from its own suggestions (any profile): bits per hand, the right hand's shifted
enum : unsigned { K_SQUEEZE = 1, K_TRIGGER = 2, K_LOW = 4, K_HIGH = 8, K_GRIP_POSE = 16, K_AIM_POSE = 32, K_ANY = 64, K_RIGHT_SHIFT = 8 };
std::map<XrAction, unsigned> g_action_bits;
std::map<XrSession, XrSpace> g_view;           // our own VIEW space per session: the head
HandFreezePose* g_pose = nullptr;
XrTime g_report_time = 0;
constexpr ULONGLONG kPoseFreshMs = 1500;       // the app's poses count while it ticked this recently

HandFreezePose* pose_block() {
    if (g_pose != nullptr) return g_pose;
    HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(HandFreezePose), HAND_FREEZE_POSE_MAPPING_NAME);
    if (h == nullptr) return nullptr;
    auto* p = static_cast<HandFreezePose*>(MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(HandFreezePose)));
    if (p == nullptr) { CloseHandle(h); return nullptr; }
    if (p->magic != HAND_FREEZE_POSE_MAGIC) { p->version = HAND_FREEZE_POSE_VERSION; p->magic = HAND_FREEZE_POSE_MAGIC; }
    g_pose = p;
    return p;
}
bool pose_fresh(const HandFreezePose* p) { return p != nullptr && p->app_heartbeat_ms != 0 && GetTickCount64() - p->app_heartbeat_ms < kPoseFreshMs; }
LONG hand_bit(int hand) { return hand == HAND_LEFT ? HAND_FREEZE_LEFT : hand == HAND_RIGHT ? HAND_FREEZE_RIGHT : 0; }

XrPosef compose(const XrPosef& a, const XrPosef& b) {   // b, given in a's frame, in a's parent frame
    const XrVector3f r = rot(a.orientation, b.position);
    return {qmul(a.orientation, b.orientation), {a.position.x + r.x, a.position.y + r.y, a.position.z + r.z}};
}
void put_pose(float* out, const XrPosef& p) {
    out[0] = p.position.x; out[1] = p.position.y; out[2] = p.position.z;
    out[3] = p.orientation.x; out[4] = p.orientation.y; out[5] = p.orientation.z; out[6] = p.orientation.w;
}
bool get_pose(const float* in, XrPosef& p) {
    float v[7]; std::memcpy(v, in, sizeof v);   // one copy: the app may be writing
    const float n = v[3] * v[3] + v[4] * v[4] + v[5] * v[5] + v[6] * v[6];
    if (!(n > 0.9f && n < 1.1f)) return false;  // not a rotation (half written, or never set)
    for (float f : v) if (!(f > -100.0f && f < 100.0f)) return false;
    p = {{v[3], v[4], v[5], v[6]}, {v[0], v[1], v[2]}};
    return true;
}

// the hand's reference space in this session: the one on the game's grip pose if it has one, else the first made
XrSpace ref_space(XrSession session, int hand, bool* is_grip = nullptr) {
    XrSpace first = XR_NULL_HANDLE;
    for (auto& c : g_controllers) {
        if (c.second.session != session || hand_of(c.second) != hand) continue;
        auto b = g_action_bits.find(c.second.action);
        const unsigned bits = b == g_action_bits.end() ? 0 : (hand == HAND_RIGHT ? b->second >> K_RIGHT_SHIFT : b->second);
        if ((bits & K_GRIP_POSE) != 0) { if (is_grip != nullptr) *is_grip = true; return c.first; }
        if (first == XR_NULL_HANDLE) first = c.first;
    }
    if (is_grip != nullptr) *is_grip = false;
    return first;
}

// the pose the app gives this controller space, in the room; false = none (not given, stale, or not a rotation)
bool given_pose(XrSpace space, const Controller& c, int hand, XrTime time, XrPosef& in_room) {
    const HandFreezePose* p = pose_block();
    if (!pose_fresh(p) || (p->posed & hand_bit(hand)) == 0) return false;
    XrPosef target{};
    if (!get_pose(hand == HAND_LEFT ? p->pose_left : p->pose_right, target)) return false;
    const XrSpace ref = ref_space(c.session, hand);
    in_room = target;
    if (ref != XR_NULL_HANDLE && ref != space) {   // another space of the same hand (the aim pose beside the grip pose): its own offset
        XrSpaceLocation o{XR_TYPE_SPACE_LOCATION};
        if (XR_SUCCEEDED(g_next_locate_space(space, ref, time, &o)) && (o.locationFlags & kValid) == kValid) in_room = compose(target, o.pose);
    }
    return true;
}

// once per frame time: the head, the real controllers and what the game is told, for the app's sums
void report(XrSession session, XrTime time) {
    HandFreezePose* p = pose_block();
    if (p == nullptr || time == g_report_time) return;
    g_report_time = time;
    auto r = g_room.find(session);
    if (r == g_room.end()) return;
    LONG valid = 0, grip = 0;
    auto v = g_view.find(session);
    XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
    if (v != g_view.end() && XR_SUCCEEDED(g_next_locate_space(v->second, r->second, time, &loc)) && (loc.locationFlags & kValid) == kValid) {
        put_pose(p->head, loc.pose); valid |= HAND_FREEZE_LIVE_HEAD;
    }
    const LONG frozen = frozen_now();
    for (int hand : {HAND_LEFT, HAND_RIGHT}) {
        bool is_grip = false;
        const XrSpace ref = ref_space(session, hand, &is_grip);
        if (ref == XR_NULL_HANDLE) continue;
        if (is_grip) grip |= hand_bit(hand);
        const Controller& c = g_controllers[ref];
        XrSpaceLocation now{XR_TYPE_SPACE_LOCATION};
        const bool real = XR_SUCCEEDED(g_next_locate_space(ref, r->second, time, &now)) && (now.locationFlags & kValid) == kValid;
        XrPosef eff = now.pose;
        bool have = real;
        if ((frozen & hand_bit(hand)) != 0) {
            XrPosef g{};
            if (given_pose(ref, c, hand, time, g)) { eff = g; have = true; }
            else if (c.kept && c.kept_generation == generation_of(hand)) { eff = c.pose_in_room; have = true; }
        }
        if (real) put_pose(hand == HAND_LEFT ? p->real_left : p->real_right, now.pose);
        if (have) { put_pose(hand == HAND_LEFT ? p->eff_left : p->eff_right, eff); valid |= hand == HAND_LEFT ? HAND_FREEZE_LIVE_LEFT : HAND_FREEZE_LIVE_RIGHT; }
    }
    p->ref_grip = grip;
    p->live_valid = valid;
    p->live_ms = GetTickCount64();
}
void report_for(XrSpace space, XrTime time) {
    std::lock_guard _{g_mtx};
    auto c = g_controllers.find(space);
    if (c != g_controllers.end()) report(c->second.session, time);
}

// a synth hand's answer for an action state: its grip, trigger and face buttons from the block, anything else "not pressed"
template <class T, class Set>
void synth_input(const XrActionStateGetInfo* info, T& state, Set set) {
    const HandFreezePose* p = pose_block();
    if (info == nullptr || !pose_fresh(p)) return;
    const LONG mask = p->synth;
    if (mask == 0) return;
    std::lock_guard _{g_mtx};
    if (g_not_the_games.count(info->action) != 0) return;   // v6: another layer's own button
    auto b = g_action_bits.find(info->action);
    const unsigned bits = b == g_action_bits.end() ? 0 : b->second;
    const int hand = hand_of_path(info->subactionPath);
    float value = 0.0f;
    bool any = false;
    for (int h : {HAND_LEFT, HAND_RIGHT}) {
        if (hand != HAND_UNKNOWN && hand != h) continue;
        const unsigned k = h == HAND_RIGHT ? bits >> K_RIGHT_SHIFT : bits & ((1u << K_RIGHT_SHIFT) - 1);
        if (hand == HAND_UNKNOWN && k == 0) continue;          // asked for no hand, and not bound to this one
        if ((mask & hand_bit(h)) == 0) { if (hand == HAND_UNKNOWN && k != 0) return; continue; }   // shared with a hand that is live: leave it
        any = true;
        const int i = h == HAND_RIGHT ? 1 : 0;
        float x = 0.0f;
        if ((k & K_SQUEEZE) != 0) x = p->squeeze[i] > x ? p->squeeze[i] : x;
        if ((k & K_TRIGGER) != 0) x = p->trigger[i] > x ? p->trigger[i] : x;
        if ((k & K_LOW) != 0 && (p->buttons[i] & HAND_FREEZE_BTN_LOW) != 0) x = 1.0f;
        if ((k & K_HIGH) != 0 && (p->buttons[i] & HAND_FREEZE_BTN_HIGH) != 0) x = 1.0f;
        value = x > value ? x : value;
    }
    if (!any) return;
    auto& in = g_inputs[{info->action, info->subactionPath}];
    set(state, value);
    state.isActive = XR_TRUE;
    state.changedSinceLastSync = in.synth_last >= 0.0f && in.synth_last != value ? XR_TRUE : XR_FALSE;
    in.synth_last = value;
    InterlockedIncrement(&pose_block()->inputs_posed);
}

// remember what each action of a suggestion is bound to (v5)
void note_bindings(const XrInteractionProfileSuggestedBinding* sb) {
    if (sb == nullptr || g_next_path_to_string == nullptr) return;
    std::lock_guard _{g_mtx};
    for (uint32_t i = 0; i < sb->countSuggestedBindings; ++i) {
        char text[XR_MAX_PATH_LENGTH]{};
        uint32_t n = 0;
        if (XR_FAILED(g_next_path_to_string(g_instance, sb->suggestedBindings[i].binding, XR_MAX_PATH_LENGTH, &n, text))) continue;
        unsigned shift;
        const char* rest;
        if (std::strncmp(text, "/user/hand/left/", 16) == 0) { shift = 0; rest = text + 16; }
        else if (std::strncmp(text, "/user/hand/right/", 17) == 0) { shift = K_RIGHT_SHIFT; rest = text + 17; }
        else continue;
        auto is = [&](const char* part) { const size_t l = std::strlen(part); return std::strncmp(rest, part, l) == 0 && (rest[l] == 0 || rest[l] == '/'); };
        unsigned k = K_ANY;
        if (is("input/squeeze")) k |= K_SQUEEZE;
        else if (is("input/trigger")) k |= K_TRIGGER;
        else if (is("input/a") || is("input/x")) k |= K_LOW;
        else if (is("input/b") || is("input/y")) k |= K_HIGH;
        else if (is("input/grip")) k |= K_GRIP_POSE;
        else if (is("input/aim")) k |= K_AIM_POSE;
        g_action_bits[sb->suggestedBindings[i].action] |= k << shift;
    }
}

// The held answer for "controller `space` relative to `base`" at `time`. False = answer live (not a controller, the
// base is itself a controller, or no usable pose yet).
bool held_pose(XrSpace space, XrSpace base, XrTime time, LONG mask, XrPosef& out) {
    std::lock_guard _{g_mtx};
    auto c = g_controllers.find(space);
    if (c == g_controllers.end() || g_controllers.count(base) != 0) return false;
    auto r = g_room.find(c->second.session);
    if (r == g_room.end()) return false;
    const int hand = hand_of(c->second);
    if (!wanted_hand(hand, mask)) return false;
    auto* s = shared();
    const LONG generation = generation_of(hand);
    if (c->second.kept && c->second.kept_generation != generation) c->second.kept = false;   // a new freeze: fresh pose
    if (!c->second.kept) {
        XrSpaceLocation now{XR_TYPE_SPACE_LOCATION};
        if (XR_SUCCEEDED(g_next_locate_space(space, r->second, time, &now)) && (now.locationFlags & kValid) == kValid) {
            c->second.pose_in_room = now.pose;
            c->second.kept = true;
            c->second.kept_generation = generation;
            InterlockedIncrement(&s->poses_held);
        }
    }
    XrPosef in_room = c->second.pose_in_room;
    const bool given = given_pose(space, c->second, hand, time, in_room);   // v5: the app's pose wins over the kept one
    if (!given && !c->second.kept) return false;
    XrSpaceLocation b{XR_TYPE_SPACE_LOCATION};   // where the base is in the room right now (the head keeps moving)
    if (XR_FAILED(g_next_locate_space(base, r->second, time, &b)) || (b.locationFlags & kValid) != kValid) return false;
    out = relative(b.pose, in_room);
    return true;
}

// ---- the hooked functions ----------------------------------------------------------------------------------------
XRAPI_ATTR XrResult XRAPI_CALL hf_xrCreateActionSpace(XrSession session, const XrActionSpaceCreateInfo* info, XrSpace* space) {
    const XrResult r = g_next_create_action_space(session, info, space);
    if (XR_FAILED(r) || space == nullptr) return r;
    std::lock_guard _{g_mtx};
    // v6.1 (Tefa 2026-11-25: the pointers "come out but cannot be moved, they are stuck to hands"): Window View's aim rays are
    // spaces of its own actions, made through this layer; held, its lasers stood on the frozen hands. Never one of ours.
    if (info != nullptr && g_not_the_games.count(info->action) != 0) return r;
    Controller c{};
    c.session = session;
    c.action = info != nullptr ? info->action : XR_NULL_HANDLE;
    c.subaction = info != nullptr ? info->subactionPath : XR_NULL_PATH;
    g_controllers[*space] = c;
    if (g_room.count(session) == 0 && g_next_create_reference_space != nullptr) {
        XrReferenceSpaceCreateInfo ci{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
        ci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
        ci.poseInReferenceSpace.orientation.w = 1.0f;
        XrSpace room = XR_NULL_HANDLE;
        if (XR_SUCCEEDED(g_next_create_reference_space(session, &ci, &room))) g_room[session] = room;
        ci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;   // v5: the head, for the app's sums
        XrSpace view = XR_NULL_HANDLE;
        if (XR_SUCCEEDED(g_next_create_reference_space(session, &ci, &view))) g_view[session] = view;
    }
    return r;
}

XRAPI_ATTR XrResult XRAPI_CALL hf_xrDestroySpace(XrSpace space) {
    {
        std::lock_guard _{g_mtx};
        g_controllers.erase(space);
    }
    return g_next_destroy_space(space);
}

XRAPI_ATTR XrResult XRAPI_CALL hf_xrDestroySession(XrSession session) {
    {
        std::lock_guard _{g_mtx};
        g_room.erase(session);   // the runtime destroys the session's spaces with it
        g_view.erase(session);
        for (auto it = g_controllers.begin(); it != g_controllers.end();) {
            if (it->second.session == session) it = g_controllers.erase(it); else ++it;
        }
        for (auto it = g_inputs.begin(); it != g_inputs.end();) {
            if (it->second.session == session) it = g_inputs.erase(it); else ++it;
        }
        g_ours_attached.erase(session);
        g_hide.clear();
    }
    return g_next_destroy_session(session);
}

// v3: the three action-state asks; the runtime answers first, then the kept state replaces the answer while held
// ---- v4: the right stick click -------------------------------------------------------------------------------------
XrActionSet g_set = XR_NULL_HANDLE;
XrAction g_click = XR_NULL_HANDLE;
bool g_actions_failed = false;

constexpr ULONGLONG kAppFreshMs = 3000;   // the app counts as open if it ticked this recently
bool toggle_on() {
    auto* s = shared();
    return s != nullptr && s->stick_toggle != 0 && s->app_heartbeat_ms != 0 && GetTickCount64() - s->app_heartbeat_ms < kAppFreshMs;
}

XrPath to_path(const char* text) {
    XrPath p = XR_NULL_PATH;
    if (g_next_string_to_path != nullptr) g_next_string_to_path(g_instance, text, &p);
    return p;
}

bool make_action() {
    if (g_set != XR_NULL_HANDLE) return g_click != XR_NULL_HANDLE;
    if (g_actions_failed || g_instance == XR_NULL_HANDLE || g_next_create_action_set == nullptr || g_next_create_action == nullptr) return false;
    XrActionSetCreateInfo si{XR_TYPE_ACTION_SET_CREATE_INFO};
    strcpy_s(si.actionSetName, "flattovr_hand_freeze");
    strcpy_s(si.localizedActionSetName, "Hand Freeze");
    if (XR_FAILED(g_next_create_action_set(g_instance, &si, &g_set))) { g_actions_failed = true; g_set = XR_NULL_HANDLE; return false; }
    XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO};
    strcpy_s(ai.actionName, "hf_right_stick_click");
    strcpy_s(ai.localizedActionName, "Hand Freeze right stick click");
    ai.actionType = XR_ACTION_TYPE_BOOLEAN_INPUT;
    if (XR_FAILED(g_next_create_action(g_set, &ai, &g_click))) { g_actions_failed = true; g_click = XR_NULL_HANDLE; return false; }
    return true;
}

bool has_right_stick(XrPath profile) {
    static const char* const kProfiles[] = {
        "/interaction_profiles/oculus/touch_controller", "/interaction_profiles/valve/index_controller",
        "/interaction_profiles/microsoft/motion_controller", "/interaction_profiles/bytedance/pico4_controller",
        "/interaction_profiles/facebook/touch_controller_pro", "/interaction_profiles/meta/touch_controller_plus",
        "/interaction_profiles/meta/touch_pro_controller", "/interaction_profiles/meta/touch_plus_controller",
    };
    for (const char* p : kProfiles) if (profile == to_path(p)) return true;
    return false;
}

XRAPI_ATTR XrResult XRAPI_CALL hf_xrSuggestInteractionProfileBindings(XrInstance instance, const XrInteractionProfileSuggestedBinding* sb) {
    note_bindings(sb);   // v5
    if (sb == nullptr || !make_action() || !has_right_stick(sb->interactionProfile)) return g_next_suggest(instance, sb);
    std::vector<XrActionSuggestedBinding> all(sb->suggestedBindings, sb->suggestedBindings + sb->countSuggestedBindings);
    all.push_back({g_click, to_path("/user/hand/right/input/thumbstick/click")});
    XrInteractionProfileSuggestedBinding copy = *sb;
    copy.countSuggestedBindings = static_cast<uint32_t>(all.size());
    copy.suggestedBindings = all.data();
    const XrResult r = g_next_suggest(instance, &copy);
    return XR_SUCCEEDED(r) ? r : g_next_suggest(instance, sb);   // never at the game's cost
}

XRAPI_ATTR XrResult XRAPI_CALL hf_xrAttachSessionActionSets(XrSession session, const XrSessionActionSetsAttachInfo* info) {
    if (info == nullptr || g_set == XR_NULL_HANDLE) return g_next_attach(session, info);
    std::vector<XrActionSet> sets(info->actionSets, info->actionSets + info->countActionSets);
    sets.push_back(g_set);
    XrSessionActionSetsAttachInfo copy = *info;
    copy.countActionSets = static_cast<uint32_t>(sets.size());
    copy.actionSets = sets.data();
    XrResult r = g_next_attach(session, &copy);
    const bool ours = XR_SUCCEEDED(r);
    if (!ours) r = g_next_attach(session, info);
    std::lock_guard _{g_mtx};
    g_ours_attached[session] = ours;
    return r;
}

// a click: freeze both hands, or unfreeze them if any are frozen (the app's own FREEZE / UN-FREEZE, without the countdown)
void click_toggle() {
    auto* s = shared();
    if (s == nullptr) return;
    const LONG both = HAND_FREEZE_LEFT | HAND_FREEZE_RIGHT;
    if (s->frozen != 0) {
        s->frozen = 0;
        s->buttons = 0;
    } else {
        InterlockedIncrement(&s->generation_left);    // fresh poses for both hands
        InterlockedIncrement(&s->generation_right);
        s->buttons = s->toggle_buttons != 0 ? both : 0;   // before the poses, as the app does
        s->frozen = both;
    }
    InterlockedIncrement(&s->toggle_seq);
}

XRAPI_ATTR XrResult XRAPI_CALL hf_xrSyncActions(XrSession session, const XrActionsSyncInfo* info) {
    bool ours = false;
    {
        std::lock_guard _{g_mtx};
        auto it = g_ours_attached.find(session);
        ours = it != g_ours_attached.end() && it->second;
    }
    if (!ours || info == nullptr) return g_next_sync(session, info);
    std::vector<XrActiveActionSet> sets(info->activeActionSets, info->activeActionSets + info->countActiveActionSets);
    sets.push_back({g_set, XR_NULL_PATH});
    XrActionsSyncInfo copy = *info;
    copy.countActiveActionSets = static_cast<uint32_t>(sets.size());
    copy.activeActionSets = sets.data();
    XrResult r = g_next_sync(session, &copy);
    if (XR_FAILED(r)) return g_next_sync(session, info);
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
    gi.action = g_click;
    XrActionStateBoolean st{XR_TYPE_ACTION_STATE_BOOLEAN};
    const bool down = XR_SUCCEEDED(g_next_state_bool(session, &gi, &st)) && st.isActive && st.currentState;
    if (down && !g_click_down && toggle_on()) click_toggle();
    g_click_down = down;
    return r;
}

// is this game button bound to the right stick click? (bindings exist only after the game syncs: asked again each second
// until the runtime names them)
int bound_to_right_click(XrSession session, XrAction action, XrPath subaction, Hide& h) {
    if (h.bound >= 0) return h.bound;
    const ULONGLONG now = GetTickCount64();
    if (now < h.retry_ms) return -1;
    h.retry_ms = now + 1000;
    if (hand_of_path(subaction) == HAND_LEFT || g_next_bound_sources == nullptr || g_next_path_to_string == nullptr) return h.bound = 0;
    XrBoundSourcesForActionEnumerateInfo bi{XR_TYPE_BOUND_SOURCES_FOR_ACTION_ENUMERATE_INFO};
    bi.action = action;
    XrPath sources[16]{};
    uint32_t n = 0;
    if (XR_FAILED(g_next_bound_sources(session, &bi, 16, &n, sources)) || n == 0) return -1;
    h.bound = 0;
    static const char kStick[] = "/user/hand/right/input/thumbstick";
    for (uint32_t i = 0; i < n && i < 16; ++i) {
        char text[XR_MAX_PATH_LENGTH]{};
        uint32_t len = 0;
        if (XR_FAILED(g_next_path_to_string(g_instance, sources[i], XR_MAX_PATH_LENGTH, &len, text))) continue;
        if (std::strncmp(text, kStick, sizeof kStick - 1) != 0) continue;
        const char* rest = text + sizeof kStick - 1;
        if (*rest == 0 || std::strcmp(rest, "/click") == 0) h.bound = 1;   // the stick's own button (a bare stick path on a button = the click)
    }
    return h.bound;
}

// while the click is ours, a game button bound to it reads "not pressed, not changed" (and once more on the release)
void hide_click(XrSession session, const XrActionStateGetInfo* info, XrActionStateBoolean& state) {
    if (info == nullptr || info->action == g_click || !toggle_on()) return;
    std::lock_guard _{g_mtx};
    Hide& h = g_hide[{info->action, info->subactionPath}];
    if (bound_to_right_click(session, info->action, info->subactionPath, h) != 1) return;
    if (g_click_down || h.hid_last) { state.currentState = XR_FALSE; state.changedSinceLastSync = XR_FALSE; }
    h.hid_last = g_click_down;
}

XRAPI_ATTR XrResult XRAPI_CALL hf_xrGetActionStateBoolean(XrSession session, const XrActionStateGetInfo* info, XrActionStateBoolean* state) {
    const XrResult r = g_next_state_bool(session, info, state);
    if (XR_SUCCEEDED(r) && state != nullptr) {
        hide_click(session, info, *state); held_input(session, info, *state, &Input::b);
        if (info == nullptr || info->action != g_click) synth_input(info, *state, [](XrActionStateBoolean& s, float v) { s.currentState = v > 0.5f ? XR_TRUE : XR_FALSE; });
    }
    return r;
}
XRAPI_ATTR XrResult XRAPI_CALL hf_xrGetActionStateFloat(XrSession session, const XrActionStateGetInfo* info, XrActionStateFloat* state) {
    const XrResult r = g_next_state_float(session, info, state);
    if (XR_SUCCEEDED(r) && state != nullptr) {
        held_input(session, info, *state, &Input::f);
        synth_input(info, *state, [](XrActionStateFloat& s, float v) { s.currentState = v; });
    }
    return r;
}
XRAPI_ATTR XrResult XRAPI_CALL hf_xrGetActionStateVector2f(XrSession session, const XrActionStateGetInfo* info, XrActionStateVector2f* state) {
    const XrResult r = g_next_state_vec2(session, info, state);
    if (XR_SUCCEEDED(r) && state != nullptr) {
        held_input(session, info, *state, &Input::v);
        synth_input(info, *state, [](XrActionStateVector2f& s, float) { s.currentState = {0.0f, 0.0f}; });
    }
    return r;
}

void zero_velocity(XrSpaceVelocity* v) { v->linearVelocity = {0, 0, 0}; v->angularVelocity = {0, 0, 0}; }

XRAPI_ATTR XrResult XRAPI_CALL hf_xrLocateSpace(XrSpace space, XrSpace base, XrTime time, XrSpaceLocation* location) {
    const XrResult r = g_next_locate_space(space, base, time, location);
    heartbeat();
    report_for(space, time);
    XrPosef held{};
    const LONG mask = XR_SUCCEEDED(r) ? frozen_now() : 0;
    if (location == nullptr || mask == 0 || !held_pose(space, base, time, mask, held)) return r;
    location->pose = held;
    location->locationFlags |= kValid | XR_SPACE_LOCATION_POSITION_TRACKED_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
    for (auto* n = static_cast<XrBaseOutStructure*>(location->next); n != nullptr; n = n->next) {
        if (n->type == XR_TYPE_SPACE_VELOCITY) zero_velocity(reinterpret_cast<XrSpaceVelocity*>(n));
    }
    return r;
}

XrResult locate_spaces(PFN_xrLocateSpaces next, XrSession session, const XrSpacesLocateInfo* info, XrSpaceLocations* out) {
    const XrResult r = next(session, info, out);
    heartbeat();
    if (info != nullptr) { std::lock_guard _{g_mtx}; report(session, info->time); }
    const LONG mask = XR_SUCCEEDED(r) ? frozen_now() : 0;
    if (info == nullptr || out == nullptr || out->locations == nullptr || mask == 0) return r;
    XrSpaceVelocities* vel = nullptr;
    for (auto* n = static_cast<XrBaseOutStructure*>(out->next); n != nullptr; n = n->next) {
        if (n->type == XR_TYPE_SPACE_VELOCITIES) vel = reinterpret_cast<XrSpaceVelocities*>(n);
    }
    const uint32_t count = info->spaceCount < out->locationCount ? info->spaceCount : out->locationCount;
    for (uint32_t i = 0; i < count; ++i) {
        XrPosef held{};
        if (!held_pose(info->spaces[i], info->baseSpace, info->time, mask, held)) continue;
        out->locations[i].pose = held;
        out->locations[i].locationFlags |= kValid | XR_SPACE_LOCATION_POSITION_TRACKED_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
        if (vel != nullptr && vel->velocities != nullptr && i < vel->velocityCount) {
            vel->velocities[i].linearVelocity = {0, 0, 0};
            vel->velocities[i].angularVelocity = {0, 0, 0};
        }
    }
    return r;
}
XRAPI_ATTR XrResult XRAPI_CALL hf_xrLocateSpaces(XrSession s, const XrSpacesLocateInfo* i, XrSpaceLocations* o) { return locate_spaces(g_next_locate_spaces, s, i, o); }
XRAPI_ATTR XrResult XRAPI_CALL hf_xrLocateSpacesKHR(XrSession s, const XrSpacesLocateInfo* i, XrSpaceLocations* o) { return locate_spaces(g_next_locate_spaces_khr, s, i, o); }

// v6: note the actions another layer makes for itself (see other_layers_action)
XRAPI_ATTR XrResult XRAPI_CALL hf_xrCreateAction(XrActionSet set, const XrActionCreateInfo* info, XrAction* action) {
    const XrResult r = g_next_create_action(set, info, action);
    if (XR_SUCCEEDED(r) && info != nullptr && action != nullptr && other_layers_action(info->actionName)) { std::lock_guard _{g_mtx}; g_not_the_games.insert(*action); }
    return r;
}

XRAPI_ATTR XrResult XRAPI_CALL hf_xrGetInstanceProcAddr(XrInstance instance, const char* name, PFN_xrVoidFunction* function) {
    const XrResult r = g_next_gipa(instance, name, function);
    if (XR_FAILED(r) || function == nullptr || *function == nullptr || name == nullptr) return r;
    // ours only for functions the runtime really has, so a missing extension stays missing
    struct Hook { const char* name; PFN_xrVoidFunction ours; PFN_xrVoidFunction* next; };
    static const Hook hooks[] = {
        {"xrCreateActionSpace", (PFN_xrVoidFunction)hf_xrCreateActionSpace, (PFN_xrVoidFunction*)&g_next_create_action_space},
        {"xrDestroySpace", (PFN_xrVoidFunction)hf_xrDestroySpace, (PFN_xrVoidFunction*)&g_next_destroy_space},
        {"xrDestroySession", (PFN_xrVoidFunction)hf_xrDestroySession, (PFN_xrVoidFunction*)&g_next_destroy_session},
        {"xrLocateSpace", (PFN_xrVoidFunction)hf_xrLocateSpace, (PFN_xrVoidFunction*)&g_next_locate_space},
        {"xrLocateSpaces", (PFN_xrVoidFunction)hf_xrLocateSpaces, (PFN_xrVoidFunction*)&g_next_locate_spaces},
        {"xrLocateSpacesKHR", (PFN_xrVoidFunction)hf_xrLocateSpacesKHR, (PFN_xrVoidFunction*)&g_next_locate_spaces_khr},
        {"xrGetActionStateBoolean", (PFN_xrVoidFunction)hf_xrGetActionStateBoolean, (PFN_xrVoidFunction*)&g_next_state_bool},
        {"xrGetActionStateFloat", (PFN_xrVoidFunction)hf_xrGetActionStateFloat, (PFN_xrVoidFunction*)&g_next_state_float},
        {"xrGetActionStateVector2f", (PFN_xrVoidFunction)hf_xrGetActionStateVector2f, (PFN_xrVoidFunction*)&g_next_state_vec2},
        // v4: the right stick click
        {"xrSuggestInteractionProfileBindings", (PFN_xrVoidFunction)hf_xrSuggestInteractionProfileBindings, (PFN_xrVoidFunction*)&g_next_suggest},
        {"xrAttachSessionActionSets", (PFN_xrVoidFunction)hf_xrAttachSessionActionSets, (PFN_xrVoidFunction*)&g_next_attach},
        {"xrSyncActions", (PFN_xrVoidFunction)hf_xrSyncActions, (PFN_xrVoidFunction*)&g_next_sync},
        {"xrCreateAction", (PFN_xrVoidFunction)hf_xrCreateAction, (PFN_xrVoidFunction*)&g_next_create_action},   // v6
    };
    for (const auto& h : hooks) {
        if (std::strcmp(name, h.name) == 0) { *h.next = *function; *function = h.ours; break; }
    }
    return r;
}

// the functions the layer itself calls, fetched once the instance exists (the game may never ask for some of them)
void fetch_next(XrInstance instance) {
    auto get = [&](const char* n, auto& slot) {
        PFN_xrVoidFunction f = nullptr;
        if (slot == nullptr && XR_SUCCEEDED(g_next_gipa(instance, n, &f)) && f != nullptr) slot = reinterpret_cast<std::remove_reference_t<decltype(slot)>>(f);
    };
    get("xrCreateReferenceSpace", g_next_create_reference_space);
    get("xrLocateSpace", g_next_locate_space);
    get("xrCreateActionSpace", g_next_create_action_space);
    get("xrDestroySpace", g_next_destroy_space);
    get("xrDestroySession", g_next_destroy_session);
    get("xrPathToString", g_next_path_to_string);
    get("xrEnumerateBoundSourcesForAction", g_next_bound_sources);
    get("xrStringToPath", g_next_string_to_path);
    get("xrCreateActionSet", g_next_create_action_set);
    get("xrCreateAction", g_next_create_action);
    get("xrGetActionStateBoolean", g_next_state_bool);
    get("xrSuggestInteractionProfileBindings", g_next_suggest);
    get("xrAttachSessionActionSets", g_next_attach);
    get("xrSyncActions", g_next_sync);
    g_instance = instance;
}

XRAPI_ATTR XrResult XRAPI_CALL hf_xrCreateApiLayerInstance(const XrInstanceCreateInfo* info, const XrApiLayerCreateInfo* layer_info, XrInstance* instance) {
    if (layer_info == nullptr || layer_info->nextInfo == nullptr) return XR_ERROR_INITIALIZATION_FAILED;
    g_next_gipa = layer_info->nextInfo->nextGetInstanceProcAddr;
    XrApiLayerCreateInfo next_info = *layer_info;   // the next layer down gets the chain one step further on
    next_info.nextInfo = layer_info->nextInfo->next;
    const XrResult r = layer_info->nextInfo->nextCreateApiLayerInstance(info, &next_info, instance);
    if (XR_SUCCEEDED(r) && instance != nullptr) fetch_next(*instance);
    return r;
}

} // namespace

// 32-bit build (v3.1): XRAPI_CALL is __stdcall there, which decorates the export to _xrNegotiate...@12, and the loader then
// says "negotiation function xrNegotiateLoaderApiLayerInterface was not found" (seen 2026-10-14). Export the plain name too.
#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:xrNegotiateLoaderApiLayerInterface=_xrNegotiateLoaderApiLayerInterface@12")
#endif
extern "C" __declspec(dllexport) XrResult XRAPI_CALL xrNegotiateLoaderApiLayerInterface(const XrNegotiateLoaderInfo* loader_info, const char* /*layer_name*/, XrNegotiateApiLayerRequest* request) {
    if (loader_info == nullptr || request == nullptr || loader_info->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO ||
        request->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST ||
        loader_info->minInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION || loader_info->maxInterfaceVersion < XR_CURRENT_LOADER_API_LAYER_VERSION) {
        return XR_ERROR_INITIALIZATION_FAILED;
    }
    request->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    request->layerApiVersion = loader_info->maxApiVersion < XR_CURRENT_API_VERSION ? loader_info->maxApiVersion : XR_CURRENT_API_VERSION;
    request->getInstanceProcAddr = hf_xrGetInstanceProcAddr;
    request->createApiLayerInstance = hf_xrCreateApiLayerInstance;
    return XR_SUCCESS;
}
