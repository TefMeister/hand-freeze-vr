"""pose_test.py -- does the layer put the frozen hands where the app says, and press what the app says? (v5, 2026-11-25)

A tiny OpenXR app on the OpenXR Simulator with the layer on for THIS process only (as freeze_test.py). It makes a grip pose, a
grip (float), a trigger (button), B (button) and a stick on the Touch controller, freezes both hands, then plays the app:
  1. gives a pose per hand in the second shared block: the game must be told exactly those (the hands' pose to each other, and
     each hand against the head the layer reports);
  2. gives left grip 1, right trigger 1, right B: the game must read those, and 0 for everything else (the stick too);
  3. stops ticking (a closed app): the hands go back to the plain freeze, the inputs to the real ones;
  4. un-freezes: live again.
Nothing has to move. Exit 0 = PASS.   python pose_test.py [--runtime <runtime json>]"""
import argparse, ctypes, json, math, mmap, os, struct, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
LAYER_NAME = "XR_APILAYER_FLATTOVR_hand_freeze_test"   # its own name: an installed Hand Freeze (same name) would be loaded instead
SIM_RUNTIME = r"D:\claude video game stuff\tools\OpenXR-Simulator\openxr_simulator.json"
BOTH = 3
POSED, SYNTH, POSE_LEFT, POSE_RIGHT, SQUEEZE, TRIGGER, BUTTONS, LIVE_VALID, HEAD, EFF_LEFT, EFF_RIGHT, APP_BEAT, INPUTS = 8, 12, 16, 44, 72, 80, 88, 96, 100, 184, 212, 248, 256


def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)
def qinv(q): return (-q[0], -q[1], -q[2], q[3])
def rot(q, v):
    r = qmul(qmul(q, (v[0], v[1], v[2], 0.0)), qinv(q)); return r[:3]
def rel(a, b):   # b in a's frame; a pose = (px, py, pz, qx, qy, qz, qw)
    ia = qinv(a[3:]); d = [b[i] - a[i] for i in range(3)]
    return tuple(rot(ia, d)) + qmul(ia, b[3:])
def apart(a, b):   # metres, degrees
    d = math.dist(a[:3], b[:3]); dot = abs(sum(x * y for x, y in zip(a[3:], b[3:])))
    return d, math.degrees(2 * math.acos(min(1.0, dot)))
def about(axis, deg):
    n = math.sqrt(sum(c * c for c in axis)); s = math.sin(math.radians(deg) / 2) / n
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(math.radians(deg) / 2))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runtime", default=SIM_RUNTIME)
    ap.add_argument("--window-view-control", action="store_true", help="Window View's CONTROL mode on for the run (its layer then holds the game's hands itself): Hand Freeze must still win")
    ap.add_argument("--window-view-dll", help="load THIS Window View layer file for the run (nearer the game than Hand Freeze's), instead of the installed one")
    ap.add_argument("--dll", default=os.path.join(HERE, "..", "dist", "hand_freeze_layer.dll"))
    args = ap.parse_args()
    layer_dir = tempfile.mkdtemp(prefix="handfreeze-pose-test-")
    with open(os.path.join(layer_dir, "hand_freeze_layer.json"), "w") as f:
        json.dump({"file_format_version": "1.0.0", "api_layer": {"name": LAYER_NAME, "library_path": os.path.abspath(args.dll), "api_version": "1.0",
                   "implementation_version": "1", "description": "Hand Freeze (test)"}}, f)
    os.environ["XR_RUNTIME_JSON"] = args.runtime
    os.environ["DISABLE_HAND_FREEZE_LAYER"] = "1"   # the installed copy stays out of this process (its manifest's own switch)
    os.environ["XR_API_LAYER_PATH"] = layer_dir
    os.environ["XR_ENABLE_API_LAYERS"] = LAYER_NAME
    if args.window_view_dll:
        wv_name = "XR_APILAYER_FLATTOVR_window_view_test"
        with open(os.path.join(layer_dir, "window_view_layer.json"), "w") as f:
            json.dump({"file_format_version": "1.0.0", "api_layer": {"name": wv_name, "library_path": os.path.abspath(args.window_view_dll), "api_version": "1.0",
                       "implementation_version": "1", "description": "Window View (test)"}}, f)
        os.environ["DISABLE_WINDOW_VIEW_LAYER"] = "1"   # the installed copy stays out
        os.environ["XR_ENABLE_API_LAYERS"] = wv_name + ";" + LAYER_NAME   # the first named is the nearest to the game
    import glfw, xr
    from OpenGL import WGL
    from xr.utils import SessionStateManager

    main_block = mmap.mmap(-1, 208, tagname="Local\\FlatToVR_HandFreeze_v4")
    if struct.unpack_from("<I", main_block, 0)[0] != 0x5A465248: struct.pack_into("<II", main_block, 0, 0x5A465248, 4)
    pb = mmap.mmap(-1, 264, tagname="Local\\FlatToVR_HandFreeze_Pose_v1")
    if struct.unpack_from("<I", pb, 0)[0] != 0x5A504648: struct.pack_into("<II", pb, 0, 0x5A504648, 1)
    k32 = ctypes.windll.kernel32; k32.GetTickCount64.restype = ctypes.c_uint64
    def frozen(hands):
        if hands:
            for off in (12, 168): struct.pack_into("<i", main_block, off, struct.unpack_from("<i", main_block, off)[0] + 1)
        struct.pack_into("<i", main_block, 180, 0); struct.pack_into("<i", main_block, 8, hands)
    def beat(): struct.pack_into("<Q", pb, APP_BEAT, k32.GetTickCount64())
    def pose_at(off): return struct.unpack_from("<7f", pb, off)
    frozen(0); struct.pack_into("<ii", pb, POSED, 0, 0); struct.pack_into("<Q", pb, APP_BEAT, 0)
    wv, wv_was = None, 0
    if args.window_view_control:   # window-view-vr shared.h: ctrl_mode at 536 of its 644-byte block
        wv = mmap.mmap(-1, 644, tagname="Local\\FlatToVR_WindowView_v2")
        wv_was = struct.unpack_from("<i", wv, 536)[0]; struct.pack_into("<i", wv, 536, 1)

    instance = xr.create_instance(xr.InstanceCreateInfo(enabled_extension_names=[xr.KHR_OPENGL_ENABLE_EXTENSION_NAME]))
    system_id = xr.get_system(instance, xr.SystemGetInfo(form_factor=xr.FormFactor.HEAD_MOUNTED_DISPLAY))
    cfg = xr.ViewConfigurationType.PRIMARY_STEREO
    views_cfg = xr.enumerate_view_configuration_views(instance, system_id, cfg)
    xr.get_opengl_graphics_requirements_khr(instance, system_id)
    glfw.init(); glfw.window_hint(glfw.VISIBLE, glfw.FALSE)
    window = glfw.create_window(320, 240, "pose_test", None, None); glfw.make_context_current(window)
    binding = xr.GraphicsBindingOpenGLWin32KHR(h_dc=WGL.wglGetCurrentDC(), h_glrc=WGL.wglGetCurrentContext())
    session = xr.create_session(instance, xr.SessionCreateInfo(system_id=system_id, next=ctypes.cast(ctypes.pointer(binding), ctypes.c_void_p)))
    stage = xr.create_reference_space(session, xr.ReferenceSpaceCreateInfo(reference_space_type=xr.ReferenceSpaceType.STAGE))
    view = xr.create_reference_space(session, xr.ReferenceSpaceCreateInfo(reference_space_type=xr.ReferenceSpaceType.VIEW))

    P = lambda s: xr.string_to_path(instance, s)
    hands = [P("/user/hand/left"), P("/user/hand/right")]
    aset = xr.create_action_set(instance, xr.ActionSetCreateInfo(action_set_name="test", localized_action_set_name="test", priority=0))
    def action(name, kind): return xr.create_action(aset, xr.ActionCreateInfo(action_type=kind, action_name=name, localized_action_name=name, subaction_paths=hands))
    a_pose, a_aim = action("grip_pose", xr.ActionType.POSE_INPUT), action("aim_pose", xr.ActionType.POSE_INPUT)
    a_grip, a_trig, a_b, a_stick = action("grip", xr.ActionType.FLOAT_INPUT), action("trig", xr.ActionType.BOOLEAN_INPUT), action("b", xr.ActionType.BOOLEAN_INPUT), action("stick", xr.ActionType.VECTOR2F_INPUT)
    binds = []
    for h in ("left", "right"):
        u = "/user/hand/" + h + "/input/"
        binds += [(a_pose, u + "grip/pose"), (a_aim, u + "aim/pose"), (a_grip, u + "squeeze/value"), (a_trig, u + "trigger/value"), (a_stick, u + "thumbstick")]
    binds += [(a_b, "/user/hand/right/input/b/click"), (a_b, "/user/hand/left/input/y/click")]
    xr.suggest_interaction_profile_bindings(instance, xr.InteractionProfileSuggestedBinding(
        interaction_profile=P("/interaction_profiles/oculus/touch_controller"), suggested_bindings=[xr.ActionSuggestedBinding(a, P(p)) for a, p in binds]))
    xr.attach_session_action_sets(session, xr.SessionActionSetsAttachInfo(action_sets=[aset]))
    # the aim spaces are made FIRST: the layer must still pick the grip pose as each hand's reference
    aim_spaces = [xr.create_action_space(session, xr.ActionSpaceCreateInfo(action=a_aim, subaction_path=h)) for h in hands]
    grip_spaces = [xr.create_action_space(session, xr.ActionSpaceCreateInfo(action=a_pose, subaction_path=h)) for h in hands]
    fmt = xr.enumerate_swapchain_formats(session)[0]
    swapchains = [xr.create_swapchain(session, xr.SwapchainCreateInfo(usage_flags=xr.SwapchainUsageFlags.COLOR_ATTACHMENT_BIT, format=fmt, sample_count=1,
                  width=c.recommended_image_rect_width, height=c.recommended_image_rect_height, face_count=1, array_size=1, mip_count=1)) for c in views_cfg]
    mgr = SessionStateManager(instance, session, cfg)

    def read(t):
        def loc(s, base):
            l = xr.locate_space(s, base, t); p, q = l.pose.position, l.pose.orientation
            return (p.x, p.y, p.z, q.x, q.y, q.z, q.w)
        def st(fn, a, h): return fn(session, xr.ActionStateGetInfo(action=a, subaction_path=hands[h]))
        return {"grip": [loc(s, stage) for s in grip_spaces], "grip_view": [loc(s, view) for s in grip_spaces], "aim": [loc(s, stage) for s in aim_spaces],
                "squeeze": [st(xr.get_action_state_float, a_grip, h).current_state for h in (0, 1)],
                "trig": [bool(st(xr.get_action_state_boolean, a_trig, h).current_state) for h in (0, 1)],
                "b": [bool(st(xr.get_action_state_boolean, a_b, h).current_state) for h in (0, 1)],
                "stick": [(lambda v: (v.x, v.y))(st(xr.get_action_state_vector2f, a_stick, h).current_state) for h in (0, 1)]}

    # the poses the "app" gives: built from the head the layer reports, so they are in the layer's room
    script = [("free", 1.5), ("frozen", 1.5), ("given", 2.0), ("pressed", 1.5), ("app gone", 2.5), ("free again", 1.0)]
    got, phase_i, phase_start, given = {}, 0, None, None
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
            if name in ("given", "pressed"):
                if given is None:
                    head = pose_at(HEAD)
                    def in_room(p, q): return tuple(head[i] + rot(head[3:], p)[i] for i in range(3)) + qmul(head[3:], q)
                    given = [in_room((-0.12, -0.05, -0.40), about((0, 1, 0), 25)), in_room((0.10, -0.03, -0.45), about((1, 0.3, 0), -40))]
                    got["head"] = head
                struct.pack_into("<7f", pb, POSE_LEFT, *given[0]); struct.pack_into("<7f", pb, POSE_RIGHT, *given[1])
                press = name == "pressed"
                struct.pack_into("<4f", pb, SQUEEZE, 1.0 if press else 0.0, 0.0, 0.0, 1.0 if press else 0.0)
                struct.pack_into("<ii", pb, BUTTONS, 0, 2 if press else 0)
                struct.pack_into("<ii", pb, POSED, BOTH, BOTH); beat()
            r = read(fs.predicted_display_time)
            r["told"] = [pose_at(EFF_LEFT), pose_at(EFF_RIGHT)]; r["valid"] = struct.unpack_from("<i", pb, LIVE_VALID)[0]
            got[name] = r   # the last frame of each phase is what counts
            xr.end_frame(session, xr.FrameEndInfo(display_time=fs.predicted_display_time, environment_blend_mode=xr.EnvironmentBlendMode.OPAQUE, layers=[]))
            if now - phase_start >= script[phase_i][1]:
                phase_i += 1; phase_start = now
                if phase_i < len(script):
                    nxt = script[phase_i][0]
                    if nxt == "frozen": frozen(BOTH)
                    if nxt == "free again": frozen(0); struct.pack_into("<ii", pb, POSED, 0, 0)
                    print("phase:", nxt, flush=True)
    except SessionStateManager.ExitRenderLoop:
        print("runtime ended the session early")
    frozen(0); struct.pack_into("<ii", pb, POSED, 0, 0)
    if wv is not None: struct.pack_into("<i", wv, 536, wv_was)

    print("layer: heartbeat %d, controller spaces %d (left %d, right %d), poses held %d; report at %d, valid %d, grip refs %d" % (
        struct.unpack_from("<Q", main_block, 16)[0], struct.unpack_from("<i", main_block, 28)[0], struct.unpack_from("<i", main_block, 172)[0],
        struct.unpack_from("<i", main_block, 176)[0], struct.unpack_from("<i", main_block, 32)[0], struct.unpack_from("<Q", pb, 240)[0],
        struct.unpack_from("<i", pb, LIVE_VALID)[0], struct.unpack_from("<i", pb, 260)[0]))
    if os.environ.get("POSE_TEST_DUMP"):
        import subprocess
        out = subprocess.run(["tasklist", "/m", "/fi", "PID eq %d" % os.getpid()], capture_output=True, text=True).stdout
        print("modules:", [w.strip(", ") for w in out.split() if "layer" in w.lower() or "mirror" in w.lower() or "xrfps" in w.lower()])
        print("api layers enabled env:", os.environ.get("XR_ENABLE_API_LAYERS"), "| disable:", os.environ.get("DISABLE_HAND_FREEZE_LAYER"))
    fails = []
    def check(what, ok, detail=""):
        print(("PASS  " if ok else "FAIL  ") + what + (("   " + detail) if detail else ""));
        if not ok: fails.append(what)
    near = lambda a, b: apart(a, b)[0] < 1e-3 and apart(a, b)[1] < 0.1
    g, pr, fz, gone = got.get("given"), got.get("pressed"), got.get("frozen"), got.get("app gone")
    if not (g and pr and fz and gone and given):
        print("INCONCLUSIVE: a phase never ran (no frames from the runtime)"); return 2
    if os.environ.get("POSE_TEST_DUMP"):
        f3 = lambda t: " ".join("%.3f" % c for c in t)
        for k in ("frozen", "given", "pressed", "app gone"): print(k, "L", f3(got[k]["grip"][0]), "| R", f3(got[k]["grip"][1]), "| told L", f3(got[k]["told"][0]))
        print("given L", f3(given[0]), "| R", f3(given[1]), "| head", f3(got["head"]))
    check("the layer reports the head and both hands", g["valid"] == 7, "valid bits %d" % g["valid"])
    check("the hands' pose to each other is the given one", near(rel(g["grip"][0], g["grip"][1]), rel(given[0], given[1])), "off by %.4f m, %.2f deg" % apart(rel(g["grip"][0], g["grip"][1]), rel(given[0], given[1])))
    for h, n in ((0, "left"), (1, "right")):
        check("the %s hand against the head is the given one" % n, near(g["grip_view"][h], rel(got["head"], given[h])), "off by %.4f m, %.2f deg" % apart(g["grip_view"][h], rel(got["head"], given[h])))
        check("the layer says the game is told the given %s pose" % n, near(g["told"][h], given[h]))
        check("the %s aim pose keeps its own offset from the grip pose" % n, near(rel(g["grip"][h], g["aim"][h]), rel(fz["grip"][h], fz["aim"][h])), "off by %.4f m, %.2f deg" % apart(rel(g["grip"][h], g["aim"][h]), rel(fz["grip"][h], fz["aim"][h])))
    check("a given pose is not the frozen one", not near(g["grip"][0], fz["grip"][0]))
    check("nothing pressed while only the pose is given", g["squeeze"] == [0.0, 0.0] and g["trig"] == [False, False] and g["b"] == [False, False], str((g["squeeze"], g["trig"], g["b"])))
    check("left grip 1, right trigger, right B, as given", pr["squeeze"] == [1.0, 0.0] and pr["trig"] == [False, True] and pr["b"] == [False, True], str((pr["squeeze"], pr["trig"], pr["b"])))
    check("the sticks read nothing", all(abs(c) < 1e-6 for s in pr["stick"] for c in s))
    check("the app gone: back to the plain freeze", near(gone["grip"][0], fz["grip"][0]) and near(gone["grip"][1], fz["grip"][1]) and gone["squeeze"][0] < 0.5)
    if wv is not None: print("Window View's CONTROL mode was on for this run (its layer loaded: %s)" % ("window_view_layer.dll" in __import__("subprocess").run(["tasklist", "/m", "/fi", "PID eq %d" % os.getpid()], capture_output=True, text=True).stdout))
    check("answers were replaced", struct.unpack_from("<i", pb, INPUTS)[0] > 0)
    print("PASS" if not fails else "FAIL (%d)" % len(fails))
    return 0 if not fails else 1

if __name__ == "__main__":
    sys.exit(main())
