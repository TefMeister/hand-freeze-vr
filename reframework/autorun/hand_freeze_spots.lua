-- hand_freeze_spots.lua -- tells the Hand Freeze window what the gun in hand looks like and where it is (2026-11-25).
--
-- Tefa: "make a rough 3d picture of the gun that is 360 rotatable and have these tabs ... placed where the hand gets placed".
-- Four times a second, while a gun is in hand, this writes reframework/data/hand_freeze/gun_now.json:
--   gun     the gun's object name ("ri3046_Inventory")
--   joints  every joint of the gun: name, parent (index into this list, -1 = none), place in the gun's own frame (metres:
--           +x the gun's left, +y up, +z the muzzle)
--   zones   every grip zone that is on (Visceral RE8's zones_state.json), in the same frame, with its radius
--   cam     the gun's root as the camera sees it (place and turn in the camera's frame: x right, y up, -z ahead), from which the
--           window works out how the gun sits in the right controller
-- READ ONLY: it changes nothing in the game. A game without Visceral's zones still gets the gun and its joints.
local OUT_DIR, OUT_FILE = "hand_freeze", "hand_freeze/gun_now.json"
local ZONES_FILE = "visceral_re8/zones_state.json"
local STEP_SEC = 0.25
local last_t, last_err = 0, ""

local function gun_object()
    for _, tname in ipairs({ "re8vr", "re2vr", "re3vr", "re2" }) do
        local v = rawget(_G, tname)
        if type(v) == "table" or type(v) == "userdata" then
            local w
            pcall(function() w = v.weapon end)
            if w then
                local go
                pcall(function() go = w:call("get_GameObject") end)
                if go == nil then pcall(function() go = w:get_field("<owner>k__BackingField") end) end
                if go == nil then go = w end
                local ok, name = pcall(function() return go:call("get_Name") end)
                local ok2, tf = pcall(function() return go:call("get_Transform") end)
                if ok and ok2 and name and tf then return tostring(name), tf end
            end
        end
    end
    return nil, nil
end

local function v3(v) return { v.x, v.y, v.z } end

local function step()
    local name, tf = gun_object()
    if not name then
        json.dump_file(OUT_FILE, { t = os.time(), gun = "" }, 0)
        return
    end
    local gp, gq = tf:call("get_Position"), tf:call("get_Rotation")
    local inv = gq:inverse()
    local out = { t = os.time(), gun = name, joints = {}, zones = {} }
    local js = tf:call("get_Joints")
    local index = {}
    local list = js and js:get_elements() or {}
    for i, j in ipairs(list) do
        local n = tostring(j:call("get_Name"))
        index[n] = i - 1
    end
    for _, j in ipairs(list) do
        local n = tostring(j:call("get_Name"))
        local par = -1
        pcall(function()
            local p = j:call("get_Parent")
            if p then par = index[tostring(p:call("get_Name"))] or -1 end
        end)
        out.joints[#out.joints + 1] = { n = n, par = par, p = v3(inv * (j:call("get_Position") - gp)) }
    end
    local ok, z = pcall(json.load_file, ZONES_FILE)
    if ok and type(z) == "table" and type(z.zones) == "table" then
        for _, e in ipairs(z.zones) do
            if e.on and e.x then
                out.zones[#out.zones + 1] = { key = e.key, r = (e.r_cm or 0) / 100.0, p = v3(inv * (Vector3f.new(e.x, e.y, e.z) - gp)) }
            end
        end
    end
    local cam = sdk.get_primary_camera()
    if cam then
        local ctf = cam:call("get_GameObject"):call("get_Transform")
        local cinv = ctf:call("get_Rotation"):inverse()
        local q = cinv * gq
        out.cam = { p = v3(cinv * (gp - ctf:call("get_Position"))), q = { q.x, q.y, q.z, q.w } }
    end
    json.dump_file(OUT_FILE, out, 0)
end

re.on_frame(function()
    local now = os.clock()
    if now - last_t < STEP_SEC then return end
    last_t = now
    local ok, err = pcall(step)
    if not ok and tostring(err) ~= last_err then
        last_err = tostring(err)
        log.info("[hand_freeze_spots] " .. last_err)
    end
end)
