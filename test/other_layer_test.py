"""other_layer_test.py -- does a freeze with BUTTONS TOO leave another layer's own buttons alone? (v6, 2026-11-25)

Tefa: "when i froze hands i could not then use the pointers". Window View's layer sits nearer the game than Hand Freeze's and
reads its own actions (wv_left_stick_click ...) through it; with BUTTONS TOO, Hand Freeze kept those at "not pressed" like the
game's, so the pointers could not be brought out while a hand was frozen.

A tiny OpenXR app on the OpenXR Simulator with the layer on for THIS process only (as pose_test.py). It plays both parts: it
makes one button the way the game would ("sprint") and two the way Window View does ("wv_left_stick_click", "wv_trigger"),
freezes both hands with their buttons, and asks for all three every frame. The layer counts every answer it starts to hold
(`inputs_held`): it must hold the game's one and neither of the others.
Nothing has to move. Exit 0 = PASS.   python other_layer_test.py [--runtime <runtime json>] [--dll <layer dll>]"""
import argparse, ctypes, json, mmap, os, struct, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
LAYER_NAME = "XR_APILAYER_FLATTOVR_hand_freeze_test"   # its own name: an installed Hand Freeze (same name) would be loaded instead
SIM_RUNTIME = r"D:\claude video game stuff\tools\OpenXR-Simulator\openxr_simulator.json"
FROZEN, GEN_LEFT, GEN_RIGHT, BUTTONS, INPUTS_HELD = 8, 12, 168, 180, 184


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runtime", default=SIM_RUNTIME)
    ap.add_argument("--dll", default=os.path.join(HERE, "..", "dist", "hand_freeze_layer.dll"))
    args = ap.parse_args()
    layer_dir = tempfile.mkdtemp(prefix="handfreeze-other-layer-test-")
    with open(os.path.join(layer_dir, "hand_freeze_layer.json"), "w") as f:
        json.dump({"file_format_version": "1.0.0", "api_layer": {"name": LAYER_NAME, "library_path": os.path.abspath(args.dll), "api_version": "1.0",
                   "implementation_version": "1", "description": "Hand Freeze (test)"}}, f)
    os.environ["XR_RUNTIME_JSON"] = args.runtime
    os.environ["DISABLE_HAND_FREEZE_LAYER"] = "1"   # the installed copies stay out of this process
    os.environ["DISABLE_WINDOW_VIEW_LAYER"] = "1"
    os.environ["XR_API_LAYER_PATH"] = layer_dir
    os.environ["XR_ENABLE_API_LAYERS"] = LAYER_NAME
    import glfw, xr
    from OpenGL import WGL
    from xr.utils import SessionStateManager

    block = mmap.mmap(-1, 208, tagname="Local\\FlatToVR_HandFreeze_v4")
    if struct.unpack_from("<I", block, 0)[0] != 0x5A465248: struct.pack_into("<II", block, 0, 0x5A465248, 4)
    def rd(off): return struct.unpack_from("<i", block, off)[0]
    def freeze(hands):   # as the app does: the generations, the buttons, then the hands
        if hands:
            for off in (GEN_LEFT, GEN_RIGHT): struct.pack_into("<i", block, off, rd(off) + 1)
        struct.pack_into("<i", block, BUTTONS, hands); struct.pack_into("<i", block, FROZEN, hands)
    was = (rd(FROZEN), rd(BUTTONS))
    if was[0] != 0:
        print("INCONCLUSIVE: hands are frozen by the real Hand Freeze right now; un-freeze first"); return 2
    freeze(0)

    instance = xr.create_instance(xr.InstanceCreateInfo(enabled_extension_names=[xr.KHR_OPENGL_ENABLE_EXTENSION_NAME]))
    system_id = xr.get_system(instance, xr.SystemGetInfo(form_factor=xr.FormFactor.HEAD_MOUNTED_DISPLAY))
    cfg = xr.ViewConfigurationType.PRIMARY_STEREO
    xr.enumerate_view_configuration_views(instance, system_id, cfg)
    xr.get_opengl_graphics_requirements_khr(instance, system_id)
    glfw.init(); glfw.window_hint(glfw.VISIBLE, glfw.FALSE)
    window = glfw.create_window(320, 240, "other_layer_test", None, None); glfw.make_context_current(window)
    binding = xr.GraphicsBindingOpenGLWin32KHR(h_dc=WGL.wglGetCurrentDC(), h_glrc=WGL.wglGetCurrentContext())
    session = xr.create_session(instance, xr.SessionCreateInfo(system_id=system_id, next=ctypes.cast(ctypes.pointer(binding), ctypes.c_void_p)))

    P = lambda s: xr.string_to_path(instance, s)
    aset = xr.create_action_set(instance, xr.ActionSetCreateInfo(action_set_name="test", localized_action_set_name="test", priority=0))
    def action(name, kind): return xr.create_action(aset, xr.ActionCreateInfo(action_type=kind, action_name=name, localized_action_name=name))
    a_game = action("sprint", xr.ActionType.BOOLEAN_INPUT)                   # the game's: the left stick click
    a_wv_click = action("wv_left_stick_click", xr.ActionType.BOOLEAN_INPUT)  # Window View's own, on the same button
    a_wv_trig = action("wv_trigger", xr.ActionType.FLOAT_INPUT)              # ... and its right trigger
    binds = [(a_game, "/user/hand/left/input/thumbstick/click"), (a_wv_click, "/user/hand/left/input/thumbstick/click"), (a_wv_trig, "/user/hand/right/input/trigger/value")]
    xr.suggest_interaction_profile_bindings(instance, xr.InteractionProfileSuggestedBinding(
        interaction_profile=P("/interaction_profiles/oculus/touch_controller"), suggested_bindings=[xr.ActionSuggestedBinding(a, P(p)) for a, p in binds]))
    xr.attach_session_action_sets(session, xr.SessionActionSetsAttachInfo(action_sets=[aset]))
    mgr = SessionStateManager(instance, session, cfg)

    script = [("free", 1.5), ("game's button only", 1.5), ("all three", 1.5)]
    held, active, phase_i, phase_start = {}, {}, 0, None
    try:
        while phase_i < len(script):
            glfw.poll_events()
            while True:
                try: mgr.handle_xr_event(xr.poll_event(instance))
                except xr.EventUnavailable: break
            fs = mgr.begin_frame()
            if fs is None: time.sleep(0.005); continue
            now = time.perf_counter()
            if phase_start is None: phase_start = now
            name = script[phase_i][0]
            xr.sync_actions(session, xr.ActionsSyncInfo(active_action_sets=[xr.ActiveActionSet(aset, xr.NULL_PATH)]))
            g = xr.get_action_state_boolean(session, xr.ActionStateGetInfo(action=a_game))
            active["game"] = bool(g.is_active)
            if name != "game's button only":
                active["wv click"] = bool(xr.get_action_state_boolean(session, xr.ActionStateGetInfo(action=a_wv_click)).is_active)
                active["wv trigger"] = bool(xr.get_action_state_float(session, xr.ActionStateGetInfo(action=a_wv_trig)).is_active)
            held[name] = rd(INPUTS_HELD)
            xr.end_frame(session, xr.FrameEndInfo(display_time=fs.predicted_display_time, environment_blend_mode=xr.EnvironmentBlendMode.OPAQUE, layers=[]))
            if now - phase_start >= script[phase_i][1]:
                phase_i += 1; phase_start = now
                if phase_i == 1: freeze(3)   # both hands, buttons too
                if phase_i < len(script): print("phase:", script[phase_i][0], flush=True)
    except SessionStateManager.ExitRenderLoop:
        print("runtime ended the session early")
    freeze(0)

    fails = []
    def check(what, ok, detail=""):
        print(("PASS  " if ok else "FAIL  ") + what + (("   " + detail) if detail else ""))
        if not ok: fails.append(what)
    if len(held) < 3:
        print("INCONCLUSIVE: a phase never ran (no frames from the runtime)"); return 2
    if not all(active.get(k) for k in ("game", "wv click", "wv trigger")):
        print("INCONCLUSIVE: the runtime gave no live buttons (%s), so nothing could be held" % active); return 2
    took_game = held["game's button only"] - held["free"]; took_rest = held["all three"] - held["game's button only"]
    check("the game's button is held by the freeze", took_game == 1, "held %d" % took_game)
    check("another layer's own buttons are not", took_rest == 0, "held %d more (wv_left_stick_click, wv_trigger)" % took_rest)
    print("PASS" if not fails else "FAIL (%d)" % len(fails))
    return 0 if not fails else 1

if __name__ == "__main__":
    sys.exit(main())
