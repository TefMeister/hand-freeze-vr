// click_test.cpp -- v4: does the right stick click freeze and unfreeze both hands, and does the game never see it?
//
// No headset and no simulator can press a controller button, so this program plays BOTH sides of the layer: it loads
// hand_freeze_layer.dll directly, hands it a pretend runtime underneath (bindings, syncs and button states kept in this
// file), and plays a game on top that binds "crouch" to the right stick click and "jump" to A. Then it presses buttons and
// checks what the game sees and what the layer wrote into the shared block.
//
//   click_test.exe <path to hand_freeze_layer.dll>
// Prints one line per check and "ALL PASS" or "FAILED: n". Exit 0 only when every check passed.
#define XR_NO_PROTOTYPES
#include <windows.h>
#include <openxr/openxr.h>
#include <openxr/openxr_loader_negotiation.h>

#include <cstdio>
#include <cstring>
#include <map>
#include <set>
#include <string>
#include <vector>

#include "../shared.h"

namespace {

// ---- the pretend runtime ------------------------------------------------------------------------------------------------
std::map<std::string, XrPath> g_paths;
std::vector<std::string> g_path_text{""};
std::map<XrAction, std::vector<XrPath>> g_bound;          // action -> its suggested paths (the touch profile)
std::map<XrAction, XrActionSet> g_set_of;
std::set<XrActionSet> g_attached, g_synced;
std::set<std::string> g_pressed;                          // input paths held down right now
std::map<XrAction, bool> g_now, g_before;                 // each action's state at the last sync and the one before
uintptr_t g_next_handle = 0x1000;
const XrInstance kInstance = (XrInstance)(uintptr_t)0x77;
const XrSession kSession = (XrSession)(uintptr_t)0x88;

XrPath path_of(const char* s) {
    auto it = g_paths.find(s);
    if (it != g_paths.end()) return it->second;
    g_path_text.push_back(s);
    return g_paths[s] = (XrPath)(g_path_text.size() - 1);
}

XRAPI_ATTR XrResult XRAPI_CALL rt_StringToPath(XrInstance, const char* s, XrPath* p) { *p = path_of(s); return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL rt_PathToString(XrInstance, XrPath p, uint32_t cap, uint32_t* n, char* out) {
    if (p == XR_NULL_PATH || p >= g_path_text.size()) return XR_ERROR_PATH_INVALID;
    const std::string& t = g_path_text[(size_t)p];
    *n = (uint32_t)t.size() + 1;
    if (cap >= *n && out != nullptr) memcpy(out, t.c_str(), *n);
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL rt_CreateActionSet(XrInstance, const XrActionSetCreateInfo*, XrActionSet* s) { *s = (XrActionSet)(g_next_handle++); return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL rt_CreateAction(XrActionSet set, const XrActionCreateInfo*, XrAction* a) { *a = (XrAction)(g_next_handle++); g_set_of[*a] = set; return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL rt_Suggest(XrInstance, const XrInteractionProfileSuggestedBinding* sb) {
    if (sb->interactionProfile != path_of("/interaction_profiles/oculus/touch_controller")) return XR_SUCCESS;
    for (auto& b : g_bound) b.second.clear();   // a new suggestion for a profile replaces the old one
    for (uint32_t i = 0; i < sb->countSuggestedBindings; ++i) g_bound[sb->suggestedBindings[i].action].push_back(sb->suggestedBindings[i].binding);
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL rt_Attach(XrSession, const XrSessionActionSetsAttachInfo* info) {
    for (uint32_t i = 0; i < info->countActionSets; ++i) g_attached.insert(info->actionSets[i]);
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL rt_Sync(XrSession, const XrActionsSyncInfo* info) {
    g_synced.clear();
    for (uint32_t i = 0; i < info->countActiveActionSets; ++i) g_synced.insert(info->activeActionSets[i].actionSet);
    g_before = g_now;
    for (auto& b : g_bound) {
        bool down = false;
        for (XrPath p : b.second) down = down || g_pressed.count(g_path_text[(size_t)p]) != 0;
        g_now[b.first] = down && g_synced.count(g_set_of[b.first]) != 0;
    }
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL rt_StateBool(XrSession, const XrActionStateGetInfo* gi, XrActionStateBoolean* st) {
    const bool active = g_synced.count(g_set_of[gi->action]) != 0 && !g_bound[gi->action].empty();
    st->isActive = active;
    st->currentState = active && g_now[gi->action];
    st->changedSinceLastSync = active && g_now[gi->action] != g_before[gi->action];
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL rt_StateFloat(XrSession, const XrActionStateGetInfo*, XrActionStateFloat* st) { st->isActive = XR_FALSE; return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL rt_StateVec2(XrSession, const XrActionStateGetInfo*, XrActionStateVector2f* st) { st->isActive = XR_FALSE; return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL rt_BoundSources(XrSession, const XrBoundSourcesForActionEnumerateInfo* bi, uint32_t cap, uint32_t* n, XrPath* out) {
    const auto& v = g_bound[bi->action];
    *n = (uint32_t)v.size();
    for (uint32_t i = 0; i < *n && i < cap && out != nullptr; ++i) out[i] = v[i];
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL rt_Unused() { return XR_SUCCESS; }

XRAPI_ATTR XrResult XRAPI_CALL rt_GetInstanceProcAddr(XrInstance, const char* name, PFN_xrVoidFunction* f) {
    struct E { const char* n; PFN_xrVoidFunction f; };
    static const E table[] = {
        {"xrStringToPath", (PFN_xrVoidFunction)rt_StringToPath}, {"xrPathToString", (PFN_xrVoidFunction)rt_PathToString},
        {"xrCreateActionSet", (PFN_xrVoidFunction)rt_CreateActionSet}, {"xrCreateAction", (PFN_xrVoidFunction)rt_CreateAction},
        {"xrSuggestInteractionProfileBindings", (PFN_xrVoidFunction)rt_Suggest}, {"xrAttachSessionActionSets", (PFN_xrVoidFunction)rt_Attach},
        {"xrSyncActions", (PFN_xrVoidFunction)rt_Sync}, {"xrGetActionStateBoolean", (PFN_xrVoidFunction)rt_StateBool},
        {"xrGetActionStateFloat", (PFN_xrVoidFunction)rt_StateFloat}, {"xrGetActionStateVector2f", (PFN_xrVoidFunction)rt_StateVec2},
        {"xrEnumerateBoundSourcesForAction", (PFN_xrVoidFunction)rt_BoundSources},
        {"xrCreateReferenceSpace", (PFN_xrVoidFunction)rt_Unused}, {"xrLocateSpace", (PFN_xrVoidFunction)rt_Unused},
        {"xrCreateActionSpace", (PFN_xrVoidFunction)rt_Unused}, {"xrDestroySpace", (PFN_xrVoidFunction)rt_Unused},
        {"xrDestroySession", (PFN_xrVoidFunction)rt_Unused},
    };
    for (const auto& e : table) if (strcmp(name, e.n) == 0) { *f = e.f; return XR_SUCCESS; }
    *f = nullptr;
    return XR_ERROR_FUNCTION_UNSUPPORTED;
}
XRAPI_ATTR XrResult XRAPI_CALL rt_CreateApiLayerInstance(const XrInstanceCreateInfo*, const XrApiLayerCreateInfo*, XrInstance* i) { *i = kInstance; return XR_SUCCESS; }

// ---- checks ------------------------------------------------------------------------------------------------------------
int g_failed = 0;
void check(const char* what, bool ok) { printf("%s %s\n", ok ? "PASS" : "FAIL", what); if (!ok) ++g_failed; }

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) { fprintf(stderr, "usage: click_test <hand_freeze_layer.dll>\n"); return 2; }
    // the shared block, as the app would leave it: the box ticked, the app open, nothing frozen
    HANDLE map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(HandFreezeShared), HAND_FREEZE_MAPPING_NAME);
    auto* sh = (HandFreezeShared*)MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(HandFreezeShared));
    if (sh == nullptr) { printf("no shared block\n"); return 2; }
    sh->magic = HAND_FREEZE_MAGIC; sh->version = HAND_FREEZE_VERSION;
    sh->frozen = 0; sh->buttons = 0; sh->stick_toggle = 1; sh->toggle_buttons = 0; sh->app_heartbeat_ms = GetTickCount64();

    HMODULE lib = LoadLibraryA(argv[1]);
    if (lib == nullptr) { printf("could not load %s (%lu)\n", argv[1], GetLastError()); return 2; }
    auto negotiate = (PFN_xrNegotiateLoaderApiLayerInterface)GetProcAddress(lib, "xrNegotiateLoaderApiLayerInterface");
    XrNegotiateLoaderInfo li{XR_LOADER_INTERFACE_STRUCT_LOADER_INFO, XR_LOADER_INFO_STRUCT_VERSION, sizeof(XrNegotiateLoaderInfo),
                             1, XR_CURRENT_LOADER_API_LAYER_VERSION, XR_MAKE_VERSION(1, 0, 0), XR_CURRENT_API_VERSION};
    XrNegotiateApiLayerRequest req{XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST, XR_API_LAYER_INFO_STRUCT_VERSION, sizeof(XrNegotiateApiLayerRequest)};
    if (negotiate == nullptr || XR_FAILED(negotiate(&li, "XR_APILAYER_FLATTOVR_hand_freeze", &req))) { printf("negotiation failed\n"); return 2; }
    XrApiLayerNextInfo next{XR_LOADER_INTERFACE_STRUCT_API_LAYER_NEXT_INFO, XR_API_LAYER_NEXT_INFO_STRUCT_VERSION, sizeof(XrApiLayerNextInfo)};
    strcpy_s(next.layerName, "pretend runtime");
    next.nextGetInstanceProcAddr = rt_GetInstanceProcAddr;
    next.nextCreateApiLayerInstance = rt_CreateApiLayerInstance;
    XrApiLayerCreateInfo lci{XR_LOADER_INTERFACE_STRUCT_API_LAYER_CREATE_INFO, XR_API_LAYER_CREATE_INFO_STRUCT_VERSION, sizeof(XrApiLayerCreateInfo)};
    lci.nextInfo = &next;
    XrInstanceCreateInfo ci{XR_TYPE_INSTANCE_CREATE_INFO};
    XrInstance inst = XR_NULL_HANDLE;
    if (XR_FAILED(req.createApiLayerInstance(&ci, &lci, &inst))) { printf("instance failed\n"); return 2; }

    // the game, calling through the layer
    auto get = [&](const char* n) { PFN_xrVoidFunction f = nullptr; req.getInstanceProcAddr(inst, n, &f); return f; };
    auto CreateActionSet = (PFN_xrCreateActionSet)get("xrCreateActionSet");
    auto CreateAction = (PFN_xrCreateAction)get("xrCreateAction");
    auto Suggest = (PFN_xrSuggestInteractionProfileBindings)get("xrSuggestInteractionProfileBindings");
    auto Attach = (PFN_xrAttachSessionActionSets)get("xrAttachSessionActionSets");
    auto Sync = (PFN_xrSyncActions)get("xrSyncActions");
    auto StateBool = (PFN_xrGetActionStateBoolean)get("xrGetActionStateBoolean");
    auto StringToPath = (PFN_xrStringToPath)get("xrStringToPath");
    XrPath right_hand = XR_NULL_PATH;
    StringToPath(inst, "/user/hand/right", &right_hand);

    XrActionSetCreateInfo si{XR_TYPE_ACTION_SET_CREATE_INFO};
    strcpy_s(si.actionSetName, "game"); strcpy_s(si.localizedActionSetName, "game");
    XrActionSet game = XR_NULL_HANDLE;
    CreateActionSet(inst, &si, &game);
    XrAction crouch = XR_NULL_HANDLE, jump = XR_NULL_HANDLE;
    XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO};
    ai.actionType = XR_ACTION_TYPE_BOOLEAN_INPUT;
    strcpy_s(ai.actionName, "crouch"); strcpy_s(ai.localizedActionName, "crouch"); CreateAction(game, &ai, &crouch);
    strcpy_s(ai.actionName, "jump"); strcpy_s(ai.localizedActionName, "jump"); CreateAction(game, &ai, &jump);
    XrPath p_click, p_a, p_touch;
    StringToPath(inst, "/user/hand/right/input/thumbstick/click", &p_click);
    StringToPath(inst, "/user/hand/right/input/a/click", &p_a);
    StringToPath(inst, "/interaction_profiles/oculus/touch_controller", &p_touch);
    XrActionSuggestedBinding b[2] = {{crouch, p_click}, {jump, p_a}};
    XrInteractionProfileSuggestedBinding sb{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
    sb.interactionProfile = p_touch; sb.countSuggestedBindings = 2; sb.suggestedBindings = b;
    check("the game's suggestion goes through", XR_SUCCEEDED(Suggest(inst, &sb)));
    check("the layer added its own binding beside the game's", g_bound.size() >= 3);
    XrSessionActionSetsAttachInfo at{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
    at.countActionSets = 1; at.actionSets = &game;
    check("the game's attach goes through", XR_SUCCEEDED(Attach(kSession, &at)));
    check("the layer attached its own set beside the game's", g_attached.size() == 2);

    XrActiveActionSet active{game, XR_NULL_PATH};
    XrActionsSyncInfo sy{XR_TYPE_ACTIONS_SYNC_INFO};
    sy.countActiveActionSets = 1; sy.activeActionSets = &active;
    auto frame = [&](XrAction a, XrPath sub, XrActionStateBoolean& st) {
        Sync(kSession, &sy);
        XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
        gi.action = a; gi.subactionPath = sub;
        st = XrActionStateBoolean{XR_TYPE_ACTION_STATE_BOOLEAN};
        StateBool(kSession, &gi, &st);
    };
    XrActionStateBoolean st{};

    frame(crouch, XR_NULL_PATH, st);
    check("nothing pressed: no crouch, nothing frozen", !st.currentState && sh->frozen == 0);
    const LONG seq0 = sh->toggle_seq;

    g_pressed = {"/user/hand/right/input/thumbstick/click"};
    frame(crouch, XR_NULL_PATH, st);
    check("click: both hands frozen", sh->frozen == (HAND_FREEZE_LEFT | HAND_FREEZE_RIGHT) && sh->buttons == 0 && sh->toggle_seq == seq0 + 1);
    check("click: the game does not see it (no crouch)", !st.currentState && !st.changedSinceLastSync);
    for (int i = 0; i < 3; ++i) frame(crouch, right_hand, st);
    check("held down: still frozen, toggled once only, still no crouch (asked for the right hand)", sh->frozen == 3 && sh->toggle_seq == seq0 + 1 && !st.currentState);

    g_pressed.clear();
    frame(crouch, XR_NULL_PATH, st);
    check("let go: no crouch and no change seen", !st.currentState && !st.changedSinceLastSync && sh->frozen == 3);

    g_pressed = {"/user/hand/right/input/a/click"};
    frame(jump, XR_NULL_PATH, st);
    check("A still reaches the game (jump)", st.currentState == XR_TRUE);
    g_pressed.clear();
    frame(jump, XR_NULL_PATH, st);

    g_pressed = {"/user/hand/right/input/thumbstick/click"};
    frame(crouch, XR_NULL_PATH, st);
    check("second click: both hands unfrozen, still no crouch", sh->frozen == 0 && sh->buttons == 0 && sh->toggle_seq == seq0 + 2 && !st.currentState);
    g_pressed.clear();
    frame(crouch, XR_NULL_PATH, st);

    sh->toggle_buttons = 1; sh->app_heartbeat_ms = GetTickCount64();
    g_pressed = {"/user/hand/right/input/thumbstick/click"};
    frame(crouch, XR_NULL_PATH, st);
    check("BUTTONS TOO ticked: the click freezes the buttons as well", sh->frozen == 3 && sh->buttons == 3 && !st.currentState);
    g_pressed.clear();
    frame(crouch, XR_NULL_PATH, st);
    g_pressed = {"/user/hand/right/input/thumbstick/click"};
    frame(crouch, XR_NULL_PATH, st);
    check("...and the next click lets both go", sh->frozen == 0 && sh->buttons == 0);
    g_pressed.clear();
    frame(crouch, XR_NULL_PATH, st);

    sh->stick_toggle = 0;
    g_pressed = {"/user/hand/right/input/thumbstick/click"};
    frame(crouch, XR_NULL_PATH, st);
    check("box unticked: the click crouches again and freezes nothing", st.currentState == XR_TRUE && sh->frozen == 0);
    g_pressed.clear();
    frame(crouch, XR_NULL_PATH, st);

    sh->stick_toggle = 1; sh->app_heartbeat_ms = GetTickCount64() - 10000;   // the app closed (or hung) 10 s ago
    g_pressed = {"/user/hand/right/input/thumbstick/click"};
    frame(crouch, XR_NULL_PATH, st);
    check("app not running: the click crouches and freezes nothing", st.currentState == XR_TRUE && sh->frozen == 0);

    printf(g_failed == 0 ? "ALL PASS\n" : "FAILED: %d\n", g_failed);
    return g_failed == 0 ? 0 : 1;
}
