-- Walks a campaign draft step by step through a mock host, satisfying each step's own end.
-- Original Red War walker from the session that wrote the Red War drafts; MissionHost.cs runs its successor,
-- src/SunriseStudio.Missions/Simulation/mission_host.lua. Usage: lua5.4 walk2.lua <scripts> <sdk/lua> <mission_name> [play|skip|guard|reload]
local M, K, name = arg[1], arg[2], arg[3]
package.path = M .. "/?.lua;" .. K .. "/?.lua;" .. package.path
local campaign = require("lib.campaign")
local captured
local real_new = campaign.new
campaign.new = function(content) captured = content; return real_new(content) end
local prog = require(name .. "." .. name)
local c = captured
local vars, placed, dead, out, done = {}, {}, {}, {}, {}
local state = {variable = function(_, k) return vars[k] end}
local function slot(id)
    return setmetatable({id = id}, {__index = function(_, method)
        return function(_, args)
            if method == "set_cinematic_active" then out[#out + 1] = "  CINE " .. (args.active and "on " or "off ") .. id:sub(-34) end
            if method == "set_directive" then
                local d = args.directive
                out[#out + 1] = "  GOAL " .. tostring(d.title) .. (d.progress and (" / " .. d.progress) or "")
                    .. (type(args.progress) == "table" and (" [" .. table.concat(args.progress, " of ") .. "]")
                        or type(args.progress) == "number" and (" [" .. args.progress .. " of " .. tostring(args.progress_target) .. ", plain]")
                        or "")
                    .. (args.timer_seconds and (" (" .. args.timer_seconds .. "s)") or "")
                assert(type(args.progress) ~= "table" or d.counter, "list progress on a directive without a counter")
            end
        end
    end})
end
local ctx = {attempt_generation = 3, sdk = {device_transitions = setmetatable({}, {__index = function(_, k) return k end})}}
function ctx:set_variable(k, v) vars[k] = v end
function ctx:clear_variable(k) vars[k] = nil end
function ctx:slot(id) return slot(id) end
function ctx:squad(sq) return {place = function() placed[sq] = true end} end
function ctx:cohort(a)
    for _, sq in ipairs(a.squads) do if not placed[sq] or not dead[sq] then return {cleared = false} end end
    return {cleared = true}
end
function ctx:scene(id)
    assert(type(id) == "string", "scene id")
    return {
        activate = function(_, a) for k in next, a do assert(k == "spawn", "activate key " .. k) end
            out[#out + 1] = "  SCENE " .. id:sub(-30) .. (a.spawn and " +cast" or "") end,
        send_event = function(_, a) assert(type(a.key) == "number", "send_event key"); out[#out + 1] = "  INPUT " .. id:sub(-30) .. string.format(" %08X", a.key) end,
    }
end
function ctx:select_state() end
local timers = {}
function ctx:start_timer(name, ms) assert(type(name) == "string" and math.type(ms) == "integer"); timers[name] = true end
function ctx:hold_spawn() end
function ctx:activate_objects(a) assert(type(a.slots) == "table") end
function ctx:cancel_timer(name) timers[name] = nil end
function ctx:complete_mission() out[#out + 1] = "  COMPLETE" end
function ctx:grant_reward() return 1 end
function ctx:log(t)
    local id = t:match("step (%S+) done")
    if id then done[id] = true end
end
local function ev(f) f.attempt_generation = 3; return f end
local function call(h, f) if prog[h] then prog[h](ctx, state, ev(f)) end end
local function list(v) if v == nil then return {} end if type(v) == "table" then return v end return {v} end
local function kill()
    local n = 0
    for sq in next, placed do if not dead[sq] then dead[sq] = true; n = n + 1 end end
    call("on_event_squad_state", {previous_alive_count = n, alive_count = 0})
end
prog.on_start(ctx, state)
for _, cut in ipairs(c.intro or {}) do
    call("on_event_region_changed", {region_index = cut.state.region_index})
    call("on_event_cinematic_terminated", {slot = {id = cut.cinematic}})
end
call("on_event_client_state_changed", {entered = true, held_region_index = c.legs[1].state.region_index})
local legs = {}
for _, leg in ipairs(c.legs) do legs[leg.id] = leg end
local stuck
for index, step in ipairs(c.steps) do
    out[#out + 1] = step.id
    for _ = 1, 3 do
        for name in next, timers do timers[name] = nil; call("on_event_timer_elapsed", {timer_name = name}) end
    end
    local e = step.ends or {}
    -- Fire encounter triggers so the fights a clear waits on are placed.
    for _, enc in ipairs(c.encounters or {}) do
        local src = enc.trigger or enc.monitor
        if src then call(enc.trigger and "on_event_player_trigger" or "on_event_trigger_entered", {slot = {id = src}}) end
    end
    if e.region then call("on_event_region_changed", {region_index = legs[e.region].state.region_index}) end
    if e.clear or e.counted then kill() end
    for _, o in ipairs(e.destroyed or {}) do call("on_event_object_state", {slot = {id = o}, present = false}) end
    if e.ghost_link then
        call("on_event_ghost_link_state", {slot = {id = e.ghost_link}, active = true, progress = 0.5})
        call("on_event_ghost_link_state", {slot = {id = e.ghost_link}, active = false, progress = 1})
    end
    if e.interact then call("on_event_object_interacted", {slot = {id = e.interact}}) end
    if e.scene then call("on_event_scene_finished", {slot = {id = e.scene}}) end
    if e.health then call("on_event_damage_state", {slot = {id = e.health.slot}, health = 0}) end
    for _, m in ipairs(list(e.monitor)) do call("on_event_trigger_entered", {slot = {id = m}}) end
    for _, t in ipairs(list(e.trigger)) do call("on_event_player_trigger", {slot = {id = t}}) end
    -- A leg's region report arms its triggers; send each leg as the step's triggers would need it.
    if not done[step.id] then
        for _, leg in ipairs(c.legs) do call("on_event_region_changed", {region_index = leg.state.region_index}) end
        for _, t in ipairs(list(e.trigger)) do call("on_event_player_trigger", {slot = {id = t}}) end
        for _, m in ipairs(list(e.monitor)) do call("on_event_trigger_entered", {slot = {id = m}}) end
        if e.clear or e.counted then kill() end
    end
    if not done[step.id] then stuck = step.id; break end
end
local mode = arg[4] or "play"
if not stuck then
    for _, cut in ipairs(c.outro or {}) do
        if mode == "play" then
            call("on_event_region_changed", {region_index = cut.state.region_index})
            call("on_event_cinematic_terminated", {slot = {id = cut.cinematic}})
        elseif mode == "skip" then
            call("on_event_region_changed", {region_index = cut.state.region_index})
            call("on_event_cinematic_skip_requested", {slot = {id = cut.cinematic}})
            call("on_event_cinematic_terminated", {slot = {id = cut.cinematic}})
        end
    end
    if mode == "guard" then
        for name in next, timers do timers[name] = nil; call("on_event_timer_elapsed", {timer_name = name}) end
    end
    if mode == "reload" then prog.on_load(ctx, state); prog.on_load(ctx, state) end
end
local completes = 0
for _, l in ipairs(out) do print(l); if l == "  COMPLETE" then completes = completes + 1 end end
print("COMPLETES " .. completes .. " (outro " .. #(c.outro or {}) .. ", mode " .. mode .. ")")
print(stuck and ("STUCK at " .. stuck) or "WALKED ALL STEPS")
