-- LemonCoop: local 2-player split-screen co-op for Lemon Cake (UE 4.24, UE4SS).
--
-- What the game does that we have to work around:
--  * BP_Player.BeginPlay creates a full-screen WBP_HUD, and (if IsPlayerStart, default true)
--    pauses the game and opens the main menu.  Player 2 must not do that.
--  * Every interactable (BP_Item, BP_Counter, BP_KitchenOven, ...) caches `Player` =
--    GetPlayerCharacter(0) at BeginPlay and uses Player->CurrentItem for pick-up / place / serve.
--    BP_Player.Interact loops all INT_Interact actors and calls IsOverlappingActor(self) right
--    before Interact(), so we hook that native and point `Player` at whoever is interacting.
--  * Money / XP are granted via Player->AddMoney / GainExperience; we forward P2's to P1.
--  * The camera is a fixed per-room camera on BP_Location.CameraBase; room changes are keyed to
--    `BP_Location.Player` (P1).  P2 gets its own CameraActor placed by the same room rule.

local UEHelpers = require("UEHelpers")

local MOD = "[LemonCoop] "
local function log(fmt, ...) print(MOD .. string.format(fmt, ...) .. "\n") end

-- ---------------------------------------------------------------- config
local Config = {
    AutoJoin = true,            -- spawn player 2 automatically once player 1 is in-game
    AutoJoinDelayTicks = 120,   -- frames of unpaused P1 input before auto-join (~2 s)
    Debug = false,              -- test keys F5..F9, O, B, N, M, K and a 5 s status log; some cheat, keep off for play
    -- true : gamepad 1 drives player 2, player 1 uses keyboard/mouse (menus need the mouse anyway).
    -- false: gamepad 1 -> player 1, gamepad 2 -> player 2.
    FirstGamepadToPlayer2 = true,
    SplitLayout = "Horizontal",  -- "Horizontal" (top/bottom) or "Vertical" (side by side); F1 toggles in-game
}

-- ---------------------------------------------------------------- state
local State = {
    p1Pawn = nil,        -- placed BP_Player possessed by PC0
    p2Pawn = nil,        -- BP_Player spawned for PC1
    p2PC = nil,
    p2Camera = nil,      -- CameraActor for player 2
    creatingP2 = false,  -- set while CreatePlayer runs so NotifyOnNewObject can tag the pawn
    lastRoomY = nil,
}

-- Interactables whose `Player` should follow the interacting pawn (hand / tray logic).
-- Managers (BP_Location, BP_DailyCycle, BP_Music, widgets, ...) stay bound to P1.
-- Minigames / pets (BP_Net, BP_BugMinigame, BP_Animal, BP_Cat, BP_CatCafe, BP_Pet) toggle
-- Player->DisableInput/EnableInput across delays, so they stay P1-only for now.
local SwapPlayerClasses = {
    "BP_Item_C", "BP_Counter_C", "BP_KitchenOven_C", "BP_KitchenMixer_C", "BP_Freezer_C",
    "BP_Garbage_C", "BP_Client_C", "BP_ClientDish_C", "BP_ItemSpawner_C", "BP_ItemRail_C",
    "BP_ItemRailSpawner_C", "BP_Spill_C", "BP_MagicBroom_C",
}

-- CameraBase relative-Y per room, from BP_Location's bytecode (MoveComponentTo targets).
local RoomCameraY = { Greenhouse = -55, Kitchen = 892, Store = 1837, Bedroom = 3140 }
local BEDROOM_Y = -3300   -- world Y below which a pawn is in the bedroom scene

-- ---------------------------------------------------------------- helpers
local function isValid(o)
    if o == nil or type(o) ~= "userdata" then return false end
    local ok, valid = pcall(function() return o:IsValid() end)
    if not ok or not valid then return false end
    local okpk, pk = pcall(function() return o:HasAnyInternalFlags(EInternalObjectFlags.PendingKill) end)
    return not (okpk and pk)
end

-- Pointer equality; wrapper identity is not reliable for UE4SS objects.
local function same(a, b)
    if not isValid(a) or not isValid(b) then return false end
    local ok, r = pcall(function() return a:GetAddress() == b:GetAddress() end)
    return ok and r
end

local function nameOf(o)
    if not isValid(o) then return "nil" end
    local ok, n = pcall(function() return o:GetFName():ToString() end)
    return ok and n or "?"
end

local function className(obj)
    local ok, cls = pcall(function() return obj:GetClass():GetFName():ToString() end)
    return ok and cls or "?"
end

local function findFirst(shortClass)
    local all = FindAllOf(shortClass)
    if not all then return nil end
    for _, o in ipairs(all) do if isValid(o) then return o end end
    return nil
end

local function getPC(index)
    local ws = UEHelpers.GetGameplayStatics()
    local ctx = UEHelpers.GetPlayerController()
    return ws:GetPlayerController(ctx, index)
end

local function getP1Pawn()
    if isValid(State.p1Pawn) then return State.p1Pawn end
    local okpc, pc0 = pcall(getPC, 0)
    if not okpc then log("getP1Pawn: getPC failed: %s", tostring(pc0)); return nil end
    if isValid(pc0) and isValid(pc0.Pawn) then State.p1Pawn = pc0.Pawn end
    return State.p1Pawn
end

-- ---------------------------------------------------------------- P2 pawn tagging
-- Fires for every BP_Player construction; only the one built during CreatePlayer is P2.
NotifyOnNewObject("/Game/Blueprints/Player/BP_Player.BP_Player_C", function(pawn)
    if not State.creatingP2 then return end
    pawn.IsPlayerStart = false          -- skip main-menu/pause branch of BeginPlay
    State.p2Pawn = pawn
    log("tagged P2 pawn %s", pawn:GetFullName())
end)


-- ---------------------------------------------------------------- gamepad mappings (runtime)
-- The game's key-rebinding UI (WBP_ControlsSingle) removes mappings by name and re-adds the saved
-- keyboard key whenever a save is loaded, which wipes gamepad bindings supplied through config.
-- So the mod re-adds them itself whenever they are missing. Movement uses the game's spare
-- ArrowForward/ArrowRight axes (same AddMovementInput code path, not touched by the rebind UI).
local GamepadAxes = {
    { "ArrowForward", "Gamepad_LeftY", 1.0 },
    { "ArrowRight", "Gamepad_LeftX", 1.0 },
}
local GamepadActions = {
    { "Interact", "Gamepad_FaceButton_Bottom" },
    { "Sprint", "Gamepad_FaceButton_Left" },
    { "Sprint", "Gamepad_RightTrigger" },
    { "Pause", "Gamepad_Special_Right" },
    { "Recipe 1", "Gamepad_DPad_Up" },
    { "Recipe 2", "Gamepad_DPad_Right" },
    { "Recipe 3", "Gamepad_DPad_Down" },
    { "Recipe 4", "Gamepad_DPad_Left" },
    { "Recipe 5", "Gamepad_LeftShoulder" },
    { "Recipe 6", "Gamepad_RightShoulder" },
}

local function ensureGamepadMappings(verbose)
    local settings = StaticFindObject("/Script/Engine.Default__InputSettings")
    if not isValid(settings) then return end
    local have, nA, nC, listing = {}, 0, 0, {}
    local ok, err = pcall(function()
        settings.AxisMappings:ForEach(function(_, e) local m = e:get(); nA = nA + 1; local k = "A|" .. m.AxisName:ToString() .. "|" .. m.Key.KeyName:ToString(); have[k] = true; if k:find("Gamepad") then table.insert(listing, k) end end)
        settings.ActionMappings:ForEach(function(_, e) local m = e:get(); nC = nC + 1; local k = "C|" .. m.ActionName:ToString() .. "|" .. m.Key.KeyName:ToString(); have[k] = true; if k:find("Gamepad") then table.insert(listing, k) end end)
    end)
    if verbose then log("mappings: axis=%d action=%d gamepad entries: %s", nA, nC, table.concat(listing, " ")) end
    if not ok then log("mappings: read failed: %s", tostring(err)); return end
    local added, failed = 0, nil
    -- UE4SS fills top-level fields from a table but leaves the nested FKey blank, so after the add
    -- we set Key.KeyName on the appended element in place.
    for _, a in ipairs(GamepadAxes) do
        if not have["A|" .. a[1] .. "|" .. a[2]] then
            local ok2, err2 = pcall(function()
                settings:AddAxisMapping({ AxisName = FName(a[1]), Scale = a[3], Key = { KeyName = FName(a[2]) } }, false)
                local last = settings.AxisMappings[settings.AxisMappings:GetArrayNum()]
                last.AxisName = FName(a[1]); last.Scale = a[3]; last.Key.KeyName = FName(a[2])
            end)
            if ok2 then added = added + 1 else failed = tostring(err2) end
        end
    end
    for _, c in ipairs(GamepadActions) do
        if not have["C|" .. c[1] .. "|" .. c[2]] then
            local ok2, err2 = pcall(function()
                settings:AddActionMapping({ ActionName = FName(c[1]), bShift = false, bCtrl = false, bAlt = false, bCmd = false, Key = { KeyName = FName(c[2]) } }, false)
                local last = settings.ActionMappings[settings.ActionMappings:GetArrayNum()]
                last.ActionName = FName(c[1]); last.Key.KeyName = FName(c[2])
            end)
            if ok2 then added = added + 1 else failed = tostring(err2) end
        end
    end
    if added > 0 then
        pcall(function() settings:ForceRebuildKeymaps() end)
        log("mappings: re-added %d gamepad bindings", added)
    elseif verbose then
        log("mappings: all gamepad bindings present")
    end
    if failed then log("mappings: add failed: %s", failed) end
end

if Config.Debug then RegisterKeyBind(Key.K, function() ExecuteInGameThread(function() ensureGamepadMappings(true) end) end) end

-- ---------------------------------------------------------------- spawn point for P2
-- The map has no PlayerStart, so the GameMode falls back to the WorldSettings (world origin), which
-- is inside geometry; APawn spawns with AdjustIfPossibleButDontSpawnIfColliding and the pawn dies.
RegisterHook("/Script/Engine.GameModeBase:FindPlayerStart", function() end, function(Context, Player, IncomingName)
    if State.creatingP2 and isValid(State.p2Start) then return State.p2Start end
end)

do
    local cdo = StaticFindObject("/Game/Blueprints/Player/BP_Player.Default__BP_Player_C")
    if isValid(cdo) then
        cdo.SpawnCollisionHandlingMethod = 2   -- AdjustIfPossibleButAlwaysSpawn
        cdo.AutoPossessAI = 0                  -- Disabled: PC1 possesses directly
        log("BP_Player CDO: spawn handling=%s autoPossessAI=%s", tostring(cdo.SpawnCollisionHandlingMethod), tostring(cdo.AutoPossessAI))
    else
        log("BP_Player CDO not found yet")
    end
end

-- ---------------------------------------------------------------- input diagnostics
-- The game ships keyboard-only mappings; ours come from Saved/Config/WindowsNoEditor/Input.ini.
do
    local settings = StaticFindObject("/Script/Engine.Default__InputSettings")
    local nAxis, nAction, tAxis, tAction = 0, 0, 0, 0
    local ok, err = pcall(function()
        settings.AxisMappings:ForEach(function(_, e)
            tAxis = tAxis + 1
            local name = e:get().Key.KeyName:ToString()
            if name:find("^Gamepad") then nAxis = nAxis + 1 end
        end)
        settings.ActionMappings:ForEach(function(_, e)
            tAction = tAction + 1
            local name = e:get().Key.KeyName:ToString()
            if name:find("^Gamepad") then nAction = nAction + 1 end
        end)
    end)
    if ok then
        log("input: %d/%d axis and %d/%d action mappings are gamepad%s", nAxis, tAxis, nAction, tAction,
            (nAxis == 0 and nAction == 0) and "  <-- NONE: gamepad bindings not loaded" or "")
    else
        log("input: could not read InputSettings (%s)", tostring(err))
    end
end

-- ---------------------------------------------------------------- interaction: Player swap
-- Interactables hand off to each other (counter -> item -> ...), each using its own cached
-- `Player`, so when a pawn starts an interaction every hand-interactable must point at it.
--
-- Trigger: BP_Player's interact loop calls Actor->IsOverlappingActor(self) for every INT_Interact
-- actor. That is a member native (EX_Context path) which UE4SS can pre-hook safely. Static library
-- functions (GetAllActorsWithInterface, GetPlayerCharacter) go through EX_CallMath and hooking
-- them breaks the call for the whole game in UE4SS 3.0.1, so they are deliberately not hooked.
local REACH = 300   -- units; loose items within this range of the presser are handed over too

-- Hand the interactables the presser is touching (and loose items within reach) to that pawn.
-- Everything else keeps its current owner, so an oven or customer P1 is dealing with is not
-- re-owned by P2 pressing Interact elsewhere.
local function swapAllTo(pawn)
    local n = 0
    for _, cls in ipairs(SwapPlayerClasses) do
        for _, a in ipairs(FindAllOf(cls) or {}) do
            if isValid(a) and not same(a.Player, pawn) then
                local near = false
                pcall(function()
                    near = a:IsOverlappingActor(pawn) or (cls == "BP_Item_C" and a:GetDistanceTo(pawn) < REACH)
                end)
                if near then
                    local ok, err = pcall(function() a.Player = pawn end)
                    if ok then n = n + 1 else log("swap failed on %s: %s", a:GetFullName(), tostring(err)) end
                end
            end
        end
    end
    if n > 0 then log("swap: %d nearby interactables -> %s", n, nameOf(pawn)) end
end

RegisterHook("/Script/Engine.Actor:GetAttachedActors", function(Context)
    local pawn = Context:get()
    if not isValid(pawn) or className(pawn) ~= "BP_Player_C" then return end
    if State.activePawn and same(pawn, State.activePawn) then return end
    State.activePawn = pawn
    swapAllTo(pawn)
end)

-- Actors spawned during the interaction (spawner -> new BP_Item, served dish) cache
-- GetPlayerCharacter(0) = P1 in their own BeginPlay; re-point them at the interacting pawn.
-- (Post-hook: BeginPlay's synchronous part has run by the time this fires.)
for _, path in ipairs({
    "/Game/Blueprints/Items/BP_Item.BP_Item_C:ReceiveBeginPlay",
    "/Game/Blueprints/Shop/BP_ClientDish.BP_ClientDish_C:ReceiveBeginPlay",
}) do
    local ok, err = pcall(RegisterHook, path, function(Context)
        if not isValid(State.activePawn) then return end
        local actor = Context:get()
        if isValid(actor) then actor.Player = State.activePawn; log("new %s -> %s", nameOf(actor), nameOf(State.activePawn)) end
    end)
    if not ok then log("could not hook %s: %s", path, tostring(err)) end
end

-- ---------------------------------------------------------------- economy: forward P2 -> P1
-- Both are Blueprint functions, so UE4SS only offers post-hooks. P2 shares P1's HUD widget
-- (see fixupPlayer2), so the HUD side effects already happened once; we only move the numbers.
RegisterHook("/Game/Blueprints/Player/BP_Player.BP_Player_C:AddMoney", function(Context, Gain)
    local p2, p1 = State.p2Pawn, getP1Pawn()
    local who = Context:get()
    if not isValid(p2) or not isValid(p1) or not same(who, p2) then return end
    local g = Gain:get()
    p1.Money = p1.Money + g
    p2.Money = 0
end)

RegisterHook("/Game/Blueprints/Player/BP_Player.BP_Player_C:GainExperience", function(Context, Gain)
    local p2, p1 = State.p2Pawn, getP1Pawn()
    local who = Context:get()
    if not isValid(p2) or not isValid(p1) or not same(who, p2) then return end
    p2.Experience = 0                                   -- P2 never levels
    if p1.Level >= 40 then return end
    local need = p1.ExperienceCurve:GetFloatValue(p1.Level)
    p1.Experience = p1.Experience + Gain:get() / need
    if p1.Experience >= 1 then p1:LevelUp() end
end)

-- ---------------------------------------------------------------- camera for P2
-- BP_Location is a placed level actor; find it once. FindAllOf scans every UObject and must not
-- run per frame (UE4SS can trip over freed objects while the game churns items/effects).
local function getLocation()
    if not isValid(State.location) then State.location = findFirst("BP_Location_C") end
    return State.location
end

-- World transform for a camera looking at the room whose CameraBase relative-Y is roomY.
-- Derived from the live camera: same rotation, location shifted along BP_Location's local Y.
local function roomCameraWorld(loc, roomY)
    local cam, base = loc.Camera, loc.CameraBase
    if not isValid(cam) or not isValid(base) then return nil end
    local camLoc = cam:K2_GetComponentLocation()
    local camRot = cam:K2_GetComponentRotation()
    local baseRelY = base.RelativeLocation.Y
    local actorRot = loc:K2_GetActorRotation()
    local scale = loc:GetActorScale3D()
    local km = UEHelpers.GetKismetMathLibrary()
    local delta = km:GreaterGreater_VectorRotator({ X = 0, Y = (roomY - baseRelY) * scale.Y, Z = 0 }, actorRot)
    return { X = camLoc.X + delta.X, Y = camLoc.Y + delta.Y, Z = camLoc.Z + delta.Z }, camRot
end

local function roomOf(loc, pawn)
    -- Order matters: bedroom exit volume sits inside the store, check specific volumes first.
    local checks = {
        { "GreenhouseCollision", "Greenhouse" },
        { "KitchenCollision", "Kitchen" },
        { "StoreCollision", "Store" },
    }
    for _, c in ipairs(checks) do
        local comp = loc[c[1]]
        if isValid(comp) and comp:IsOverlappingActor(pawn) then return c[2] end
    end
    -- The bedroom has no trigger volume (the game enters/leaves it by teleport); it lies beyond
    -- the store along world -Y (BedroomExitCollision is at y=-3699, the bed area around y=-4450).
    local ok, y = pcall(function() return pawn:K2_GetActorLocation().Y end)
    if ok and y < BEDROOM_Y then return "Bedroom" end
    return nil
end

local function spawnP2Camera(loc)
    local camClass = StaticFindObject("/Script/Engine.CameraActor")
    if not isValid(camClass) then log("CameraActor class not found"); return nil end
    local world = loc:GetWorld()
    local l = loc:K2_GetActorLocation()
    local ok, actor = pcall(function()
        return world:SpawnActor(camClass, { X = l.X, Y = l.Y, Z = l.Z + 500 }, { Pitch = 0, Yaw = 0, Roll = 0 })
    end)
    if not ok or not isValid(actor) then log("camera spawn failed: %s", tostring(actor)); return nil end
    -- Match the game camera lens.
    local camComp, src = actor.CameraComponent, loc.Camera
    if isValid(camComp) and isValid(src) then
        camComp:SetFieldOfView(src.FieldOfView)
        camComp:SetProjectionMode(src.ProjectionMode)
        camComp:SetOrthoWidth(src.OrthoWidth)
        camComp:SetConstraintAspectRatio(false)
    end
    log("spawned P2 camera %s", actor:GetFullName())
    return actor
end

local function updateP2Camera()
    local p2, loc, cam = State.p2Pawn, getLocation(), State.p2Camera
    if not isValid(p2) or not isValid(loc) or not isValid(cam) then return end
    local room = roomOf(loc, p2) or (State.lastRoom or "Kitchen")
    if room == State.lastRoom and State.lastRoomY then return end
    local y = RoomCameraY[room]
    local wl, wr = roomCameraWorld(loc, y)
    if not wl then return end
    local hit = {}
    cam:K2_SetActorLocationAndRotation(wl, wr, false, hit, true)
    State.lastRoom, State.lastRoomY = room, y
    log("P2 camera -> %s (%.0f, %.0f, %.0f)", room, wl.X, wl.Y, wl.Z)
end

-- ---------------------------------------------------------------- post-spawn fix-ups
local function fixupPlayer2()
    local p1, p2, pc1 = getP1Pawn(), State.p2Pawn, State.p2PC
    if not (isValid(p1) and isValid(p2) and isValid(pc1)) then log("fixup: missing objects"); return end
    local pc0 = getPC(0)

    -- BeginPlay gave P2 its own full-screen HUD and focused it for PC0. Drop it and share P1's.
    if isValid(p2.WBP_HUD) and not same(p2.WBP_HUD, p1.WBP_HUD) then
        local hud = p2.WBP_HUD
        -- WBP_HUD creates its menus as separate viewport widgets; drop every one of the duplicates.
        local removed = 0
        for _, name in ipairs({ "WBP_Options", "WBP_Dialogue", "WBP_PlayerCreation", "WBP_Controls", "WBP_Library",
                                "WBP_RecipeDaily", "WBP_OrderAll", "WBP_DailyOverview", "WBP_Shop", "WBP_MainMenu" }) do
            pcall(function()
                local w = hud[name]
                if isValid(w) and not same(w, p1.WBP_HUD[name]) then w:RemoveFromParent(); removed = removed + 1 end
            end)
        end
        hud:RemoveFromParent()
        log("removed P2's duplicate HUD and %d of its menus", removed)
    end
    p2.WBP_HUD = p1.WBP_HUD
    p2.WBP_MainMenu = p1.WBP_MainMenu
    p2.WBP_SaveGame = p1.WBP_SaveGame
    p2.IsCineEntryComplete = true
    p2.Money, p2.Experience = 0, 0

    -- Same stick-forward for both players: MoveForward/MoveRight use GetControlRotation, and P1 keeps
    -- the rotation it was possessed with (fixed-camera game), so copy it to PC1.
    pcall(function() pc1:SetControlRotation(pc0:GetControlRotation()) end)

    local wbl = StaticFindObject("/Script/UMG.Default__WidgetBlueprintLibrary")
    if isValid(wbl) and isValid(p1.WBP_HUD) then
        wbl:SetInputMode_GameAndUIEx(pc0, p1.WBP_HUD, 0, false)
        wbl:SetInputMode_GameOnly(pc1)
    end

    -- Camera.
    local loc = getLocation()
    if isValid(loc) then
        State.p2Camera = spawnP2Camera(loc)
        State.lastRoom, State.lastRoomY = nil, nil
        if isValid(State.p2Camera) then
            updateP2Camera()
            pc1:SetViewTargetWithBlend(State.p2Camera, 0, 0, 0, false)
        else
            pc1:SetViewTargetWithBlend(loc, 0, 0, 0, false)   -- fallback: share P1's camera
        end
    end
    log("fixup done: P2 pawn=%s pc=%s", p2:GetFullName(), pc1:GetFullName())
end

-- ---------------------------------------------------------------- split-screen layout
-- The viewport re-reads TwoPlayerSplitscreenLayout when it lays out the players each frame,
-- so changing the setting is enough; the override poke just forces an immediate refresh.
local function setSplitLayout(layout)
    local gms = UEHelpers.GetGameMapsSettings()
    gms.TwoPlayerSplitscreenLayout = (layout == "Vertical") and 1 or 0
    pcall(function()
        local vc = UEHelpers.GetGameViewportClient()
        vc:SetDisableSplitscreenOverride(true); vc:SetDisableSplitscreenOverride(false)
    end)
    State.splitLayout = layout
    log("split-screen layout: %s", layout)
end

-- ---------------------------------------------------------------- dual monitor (F8)
-- UE4 renders all local players into one window, so "P2 on the second monitor" = one borderless
-- window spanning both monitors with a vertical split, each player's view sized to its monitor.
-- Lua cannot move OS windows, so DualMonitor.ps1 (launched here) computes the geometry, writes
-- dualmonitor.txt, and applies the borderless span once the game has switched resolution.
-- This mod's folder (…/Mods/LemonCoop/), taken from this script's own path so it works in any install.
local ModDir = (function()
    local src = (debug.getinfo(1, "S").source or ""):gsub("^@", ""):gsub("\\", "/")
    return src:match("^(.*/)Scripts/[^/]*$") or "Mods/LemonCoop/"   -- fallback: relative to Binaries/Win64
end)()
local function consoleCmd(cmd)
    local ok, err = pcall(function()
        local ksl = StaticFindObject("/Script/Engine.Default__KismetSystemLibrary")
        ksl:ExecuteConsoleCommand(getPC(0), cmd, nil)
    end)
    if not ok then log("console command failed (%s): %s", cmd, tostring(err)) end
    return ok
end

-- Per-player viewport fractions for the two-player vertical layout (index 2 in SplitscreenInfo).
local function setVerticalSlots(p1, p2)
    local applied = 0
    local ok, err = pcall(function()
        local vc = UEHelpers.GetGameViewportClient()
        -- ESplitScreenType order: None, TwoPlayer_Horizontal, TwoPlayer_Vertical, ... (ForEach index is 1-based)
        vc.SplitscreenInfo:ForEach(function(i, e)
            if i ~= 3 then return end
            local info = e:get()
            info.PlayerData:ForEach(function(j, d)
                local pd = d:get(); local v = (j == 1) and p1 or p2
                if v then pd.OriginX = v[1]; pd.SizeX = v[2]; pd.OriginY = 0.0; pd.SizeY = 1.0; applied = applied + 1 end
            end)
        end)
    end)
    if ok and applied == 2 then log("split slots: P1 %.2f/%.2f, P2 %.2f/%.2f", p1[1], p1[2], p2[1], p2[2]); return true end
    if not ok then log("split slots not adjustable (%s); using the default 50/50 vertical split", tostring(err)) end
    return ok
end

local function readDualLayout()
    local fh = io.open(ModDir .. "dualmonitor.txt", "r")
    if not fh then return nil end
    local line = fh:read("*l"); fh:close()
    local w, h, x, y, p1o, p1s, p2o, p2s = line:match("(%d+) (%d+) (%-?%d+) (%-?%d+) ([%d%.]+) ([%d%.]+) ([%d%.]+) ([%d%.]+)")
    if not w then return nil end
    return { w = tonumber(w), h = tonumber(h), p1 = { tonumber(p1o), tonumber(p1s) }, p2 = { tonumber(p2o), tonumber(p2s) } }
end

local function dualMonitor(enable)
    if enable then
        os.remove(ModDir .. "dualmonitor.txt")
        os.execute('start "" /min powershell -NoProfile -ExecutionPolicy Bypass -File "' .. ModDir:gsub("/", "\\") .. 'DualMonitor.ps1"')
        State.dualPending = 40      -- ticks to wait for the helper's layout file
        log("dual monitor: helper started, waiting for layout")
    else
        setVerticalSlots({ 0, 0.5 }, { 0.5, 0.5 })
        setSplitLayout("Horizontal")
        consoleCmd("r.SetRes 2560x1440wf")
        os.execute('start "" /min powershell -NoProfile -ExecutionPolicy Bypass -File "' .. ModDir:gsub("/", "\\") .. 'DualMonitor.ps1" -restore')
        State.dualMonitor = false
        log("dual monitor: off")
    end
end

RegisterKeyBind(Key.F8, function()
    ExecuteInGameThread(function() dualMonitor(not State.dualMonitor) end)
end)

RegisterKeyBind(Key.F1, function()
    ExecuteInGameThread(function()
        setSplitLayout((State.splitLayout or Config.SplitLayout) == "Vertical" and "Horizontal" or "Vertical")
    end)
end)

-- ---------------------------------------------------------------- join / leave
-- Spawn point next to P1. The map has no PlayerStart, so the GameMode would otherwise spawn at
-- the world origin, where APawn's AdjustIfPossibleButDontSpawnIfColliding kills the pawn immediately.
local function placeP2Start(p1)
    local l, r = p1:K2_GetActorLocation(), p1:K2_GetActorRotation()
    -- Movement is relative to the controller rotation; P1 body facing varies, so use PC0 control rotation.
    pcall(function() r = getPC(0):GetControlRotation() end)
    local km = UEHelpers.GetKismetMathLibrary()
    local right = km:GetRightVector(r)
    local spot = { X = l.X + right.X * 150, Y = l.Y + right.Y * 150, Z = l.Z + 20 }
    if isValid(State.p2Start) then
        local hit = {}
        State.p2Start:K2_SetActorLocationAndRotation(spot, r, false, hit, true)
        return State.p2Start
    end
    local cls = StaticFindObject("/Script/Engine.PlayerStart")
    if not isValid(cls) then log("PlayerStart class not found"); return nil end
    State.p2Start = p1:GetWorld():SpawnActor(cls, spot, r)
    log("PlayerStart at %s -> %s", string.format("(%.0f, %.0f, %.0f)", spot.X, spot.Y, spot.Z), isValid(State.p2Start) and State.p2Start:GetFullName() or "FAILED")
    return State.p2Start
end

local function joinPlayer2()
    State.joinAttempted = true
    if isValid(State.p2Pawn) then log("P2 already present"); return end
    local p1 = getP1Pawn()
    if not isValid(p1) then log("no P1 pawn yet"); return end
    local gms = UEHelpers.GetGameMapsSettings()
    gms.bUseSplitscreen = true
    gms.bOffsetPlayerGamepadIds = Config.FirstGamepadToPlayer2
    setSplitLayout(State.splitLayout or Config.SplitLayout)
    log("gamepad routing: first gamepad -> %s", Config.FirstGamepadToPlayer2 and "player 2" or "player 1")
    ExecuteInGameThread(function()
        placeP2Start(p1)
        local gs = UEHelpers.GetGameplayStatics()
        local existing = gs:GetPlayerController(p1, 1)
        State.creatingP2 = true
        local ok, err = pcall(function()
            if isValid(existing) then
                State.p2PC = existing
                gs:GetGameMode(p1):RestartPlayer(existing)      -- PC1 exists but lost its pawn
            else
                State.p2PC = gs:CreatePlayer(p1, 1, true)
            end
        end)
        State.creatingP2 = false
        if not ok then log("join error: %s", tostring(err)); return end
        if not isValid(State.p2PC) then log("no player controller for P2"); return end
        if not isValid(State.p2PC.Pawn) then log("P2 controller has no pawn (spawn failed)"); State.p2Pawn = nil; return end
        State.p2Pawn = State.p2PC.Pawn
        log("P2 controller %s pawn %s", State.p2PC:GetFullName(), State.p2Pawn:GetFullName())
        fixupPlayer2()
    end)
end

local function leavePlayer2()
    if not isValid(State.p2PC) then log("no P2"); return end
    ExecuteInGameThread(function()
        if isValid(State.p2Camera) then State.p2Camera:K2_DestroyActor() end
        UEHelpers.GetGameplayStatics():RemovePlayer(State.p2PC, true)
        State.p2PC, State.p2Pawn, State.p2Camera = nil, nil, nil
        State.joinAttempted = true
        log("P2 removed")
    end)
end

local function teleportP2ToP1()
    local p1, p2 = getP1Pawn(), State.p2Pawn
    if not (isValid(p1) and isValid(p2)) then return end
    ExecuteInGameThread(function()
        local l = p1:K2_GetActorLocation()
        local hit = {}
        p2:K2_SetActorLocationAndRotation({ X = l.X + 120, Y = l.Y, Z = l.Z }, p1:K2_GetActorRotation(), false, hit, true)
        local n = p2:K2_GetActorLocation()
        log("F4: P2 teleported to (%.0f,%.0f,%.0f)", n.X, n.Y, n.Z)
    end)
end

-- ---------------------------------------------------------------- debug / test keys
if Config.Debug then
local function v3(v) return v and string.format("(%.0f, %.0f, %.0f)", v.X, v.Y, v.Z) or "nil" end

local function dumpStatus()
    local p1, p2, pc1, loc = getP1Pawn(), State.p2Pawn, State.p2PC, getLocation()
    log("P1 pawn=%s loc=%s", isValid(p1) and p1:GetFullName() or "nil", isValid(p1) and v3(p1:K2_GetActorLocation()) or "nil")
    if isValid(p2) then
        local skel = "nil"; pcall(function() skel = p2.Mesh.SkeletalMesh:GetFullName() end)
        local hidden, vis = "?", "?"; pcall(function() hidden = tostring(p2:IsHidden()) end); pcall(function() vis = tostring(p2.Mesh:IsVisible()) end)
        log("P2 pawn=%s loc=%s hidden=%s meshVisible=%s skel=%s", p2:GetFullName(), v3(p2:K2_GetActorLocation()), hidden, vis, skel)
        log("P2 controller=%s possessed=%s", isValid(p2.Controller) and p2.Controller:GetFullName() or "nil", tostring(isValid(p2.Controller)))
        if isValid(loc) then
            for _, n in ipairs({ "GreenhouseCollision", "KitchenCollision", "StoreCollision", "BedroomExitCollision" }) do
                local c = loc[n]
                if isValid(c) then log("  %s at %s overlapsP2=%s overlapsP1=%s", n, v3(c:K2_GetComponentLocation()), tostring(c:IsOverlappingActor(p2)), tostring(c:IsOverlappingActor(p1))) end
            end
            log("  CameraBase rel=%s Camera world=%s", v3(loc.CameraBase.RelativeLocation), v3(loc.Camera:K2_GetComponentLocation()))
        end
    else log("P2 pawn: none") end
    for _, pc in ipairs(FindAllOf("PlayerController") or {}) do
        if isValid(pc) then log("  PC %s player=%s id=%s pawn=%s", pc:GetFullName(), isValid(pc.Player) and pc.Player:GetFullName() or "nil",
            isValid(pc.Player) and tostring(pc.Player.ControllerId) or "?", isValid(pc.Pawn) and pc.Pawn:GetFullName() or "nil") end
    end
    for _, pw in ipairs(FindAllOf("BP_Player_C") or {}) do
        if isValid(pw) then log("  pawn %s loc=%s ctrl=%s", pw:GetFullName(), v3(pw:K2_GetActorLocation()), isValid(pw.Controller) and pw.Controller:GetFullName() or "nil") end
    end
    for _, c in ipairs(FindAllOf("CameraActor") or {}) do if isValid(c) then log("  cam %s loc=%s", c:GetFullName(), v3(c:K2_GetActorLocation())) end end
    local okc, pcx = pcall(getPC, 1); log("  getPC(1): ok=%s valid=%s", tostring(okc), tostring(okc and isValid(pcx)))
    if isValid(pc1) then log("PC1 viewtarget=%s", isValid(pc1:GetViewTarget()) and pc1:GetViewTarget():GetFullName() or "nil") end
    if isValid(State.p2Camera) then log("P2 camera loc=%s", v3(State.p2Camera:K2_GetActorLocation())) end
end

local roomCycle = { "GreenhouseCollision", "KitchenCollision", "StoreCollision" }
local roomIdx = 0
local function cycleP2Room()
    local p2, loc = State.p2Pawn, getLocation()
    if not (isValid(p2) and isValid(loc)) then return end
    roomIdx = roomIdx % #roomCycle + 1
    local c = loc[roomCycle[roomIdx]]
    if not isValid(c) then return end
    ExecuteInGameThread(function()
        local l = c:K2_GetComponentLocation()
        local hit = {}
        p2:K2_SetActorLocationAndRotation({ X = l.X, Y = l.Y, Z = l.Z + 100 }, { Pitch = 0, Yaw = 0, Roll = 0 }, false, hit, true)
        log("P2 -> %s %s", roomCycle[roomIdx], v3(p2:K2_GetActorLocation()))
    end)
end

-- Simulate a pawn pressing Interact: same walk BP_Player does (swap, overlap test, Interact()).
local function interactAs(pawn, label)
    if not isValid(pawn) then log("no %s", label); return end
    ExecuteInGameThread(function()
        State.activePawn = pawn
        swapAllTo(pawn)
        for _, cls in ipairs(SwapPlayerClasses) do
            for _, a in ipairs(FindAllOf(cls) or {}) do
                if isValid(a) and a:IsOverlappingActor(pawn) then
                    log("%s interact -> %s", label, a:GetFullName())
                    a:Interact()
                    local function held(p)
                        local it = isValid(p) and p.CurrentItem or nil
                        if not isValid(it) then return "nil" end
                        local par = nil; pcall(function() par = it:GetAttachParentActor() end)
                        return nameOf(it) .. " (attached to " .. nameOf(par) .. ")"   -- nameOf checks validity; GetFName on a null wrapper is an AV
                    end
                    log("  P2 holds %s | P1 holds %s", held(State.p2Pawn), held(getP1Pawn()))
                    return
                end
            end
        end
        log("%s interact: nothing overlapping", label)
    end)
end
RegisterKeyBind(Key.F7, function() interactAs(State.p2Pawn, "P2") end)
RegisterKeyBind(Key.F9, function()
    local p1, p2 = getP1Pawn(), State.p2Pawn
    if not (isValid(p1) and isValid(p2)) then return end
    ExecuteInGameThread(function()
        local l = p2:K2_GetActorLocation()
        local hit = {}
        p1:K2_SetActorLocationAndRotation({ X = l.X + 80, Y = l.Y, Z = l.Z }, { Pitch = 0, Yaw = 0, Roll = 0 }, false, hit, true)
    end)
    interactAs(p1, "P1")
end)
local spawnerIdx = 0
local function p2ToNextSpawner()
    local p2 = State.p2Pawn
    if not isValid(p2) then return end
    ExecuteInGameThread(function()
        local list = {}
        for _, a in ipairs(FindAllOf("BP_ItemSpawner_C") or {}) do
            if isValid(a) and not a.IsPlant and tostring(a.ItemToSpawn) ~= "None" then table.insert(list, a) end
        end
        if #list == 0 then log("no ingredient spawners"); return end
        spawnerIdx = spawnerIdx % #list + 1
        local a = list[spawnerIdx]
        if not a.IsUnlocked then a.IsUnlocked = true; log("  (test) unlocked %s", nameOf(a)) end
        local l = a:K2_GetActorLocation()
        local hit = {}
        p2:K2_SetActorLocationAndRotation({ X = l.X, Y = l.Y, Z = l.Z + 60 }, { Pitch = 0, Yaw = 0, Roll = 0 }, false, hit, true)
        log("P2 -> spawner %d/%d %s spawns=%s at %s", spawnerIdx, #list, nameOf(a), tostring(a.ItemToSpawn), v3(l))
    end)
end
RegisterKeyBind(Key.F8, p2ToNextSpawner)
local itemIdx = 0
RegisterKeyBind(Key.O, function()
    local p2 = State.p2Pawn
    if not isValid(p2) then return end
    ExecuteInGameThread(function()
        local list = {}
        for _, a in ipairs(FindAllOf("BP_Item_C") or {}) do if isValid(a) then table.insert(list, a) end end
        if #list == 0 then log("no items"); return end
        itemIdx = itemIdx % #list + 1
        local a = list[itemIdx]
        local l = a:K2_GetActorLocation()
        local hit = {}
        p2:K2_SetActorLocationAndRotation({ X = l.X, Y = l.Y + 40, Z = l.Z + 40 }, { Pitch = 0, Yaw = 0, Roll = 0 }, false, hit, true)
        log("P2 -> item %d/%d %s (%s) at %s held-by=%s", itemIdx, #list, nameOf(a), tostring(a.Name), v3(l), nameOf(a.Player))
    end)
end)


RegisterKeyBind(Key.F5, function() ExecuteInGameThread(dumpStatus) end)
RegisterKeyBind(Key.F6, cycleP2Room)
end

-- Re-acquire player 2 after a hot reload (Ctrl+R) so the keybinds keep working.
do
    local ok, pc1 = pcall(getPC, 1)
    if ok and isValid(pc1) and isValid(pc1.Pawn) then
        State.p2PC, State.p2Pawn = pc1, pc1.Pawn
        local cams = FindAllOf("CameraActor")
        if cams then for _, c in ipairs(cams) do if isValid(c) then State.p2Camera = c end end end
        log("re-acquired P2 after reload")
    end
end

-- Runtime input tests (Debug only): B drop P2 onto P1, N focus viewport, M toggle gamepad offset
if Config.Debug then
RegisterKeyBind(Key.B, function()
    local p1, p2 = getP1Pawn(), State.p2Pawn
    if not (isValid(p1) and isValid(p2)) then return end
    ExecuteInGameThread(function()
        local l = p1:K2_GetActorLocation(); local hit = {}
        p2:K2_SetActorLocationAndRotation({ X = l.X, Y = l.Y, Z = l.Z + 20 }, p1:K2_GetActorRotation(), false, hit, true)
        log("B: P2 dropped onto P1 at (%.0f,%.0f,%.0f)", l.X, l.Y, l.Z)
    end)
end)
RegisterKeyBind(Key.N, function()
    ExecuteInGameThread(function()
        local ok, err = pcall(function()
            local wbl = StaticFindObject("/Script/UMG.Default__WidgetBlueprintLibrary")
            wbl:SetFocusToGameViewport()
        end)
        log("N: all-user focus -> game viewport (%s)", ok and "ok" or tostring(err))
    end)
end)
RegisterKeyBind(Key.M, function()
    ExecuteInGameThread(function()
        local gms = UEHelpers.GetGameMapsSettings()
        gms.bOffsetPlayerGamepadIds = not gms.bOffsetPlayerGamepadIds
        log("M: bOffsetPlayerGamepadIds now %s", tostring(gms.bOffsetPlayerGamepadIds))
    end)
end)
end
RegisterKeyBind(Key.F2, joinPlayer2)
RegisterKeyBind(Key.F3, leavePlayer2)
RegisterKeyBind(Key.F4, teleportP2ToP1)

-- Periodic tick (150 ms) on the game thread: auto-join gate, interaction window expiry and
-- P2 room-camera follow. Deliberately NOT a per-frame Blueprint hook: a post-hook on the pawn's
-- axis event crashed inside UE4SS's parameter wrapper (IsA on a garbage pointer) after a few
-- minutes of play. Hot reload is disabled in UE4SS-settings.ini, so LoopAsync is safe here.
local p1Ticks = 0
local activeAge = 0
LoopAsync(150, function()
    ExecuteInGameThread(function()
        -- interaction window: keep activePawn for one tick after it was set
        if State.activePawn then
            activeAge = activeAge + 1
            if activeAge > 1 then State.activePawn = nil; activeAge = 0 end
        end
        -- auto-join once P1 is in-game (unpaused, no menu widget visible) for ~2 s
        if Config.AutoJoin and not State.joinAttempted and not isValid(State.p2Pawn) then
            local p1 = getP1Pawn()
            if isValid(p1) then
                local blocked = true
                pcall(function() blocked = UEHelpers.GetGameplayStatics():IsGamePaused(p1) or menuVisible() end)
                p1Ticks = blocked and 0 or (p1Ticks + 1)
                if p1Ticks >= 13 then
                    State.joinAttempted = true
                    log("auto-join: player 1 is in-game, adding player 2")
                    joinPlayer2()
                end
            end
        end
        -- Bedroom is entered/left by teleports the game applies to P1 only; keep P2 on the same side.
        if isValid(State.p2Pawn) then
            local p1 = getP1Pawn()
            if isValid(p1) then
                local ok, mismatch = pcall(function()
                    return (p1:K2_GetActorLocation().Y < BEDROOM_Y) ~= (State.p2Pawn:K2_GetActorLocation().Y < BEDROOM_Y)
                end)
                if ok and mismatch then
                    State.bedMismatch = (State.bedMismatch or 0) + 1
                    if State.bedMismatch >= 4 then        -- ~0.6 s, lets the game finish P1 fade/teleport
                        local l, r = p1:K2_GetActorLocation(), p1:K2_GetActorRotation()
                        local hit = {}
                        State.p2Pawn:K2_SetActorLocationAndRotation({ X = l.X + 100, Y = l.Y, Z = l.Z }, r, false, hit, true)
                        State.lastRoom, State.lastRoomY = nil, nil
                        log("P2 followed P1 across the bedroom boundary")
                        State.bedMismatch = 0
                    end
                else
                    State.bedMismatch = 0
                end
            end
        end
        -- Keep player 2 receiving game input. The game re-focuses its HUD widget for P1 after
        -- loading a save; a focused UMG widget can swallow gamepad input before the viewport routes
        -- it to PC1, so re-assert PC1 input mode (game+UI, focus on viewport, no mouse lock) each second.
        -- Analog stick events only reach the game while the viewport has Slate focus; the game
        -- leaves focus on a HUD widget after loading a save (buttons still work, sticks do not).
        -- Once per second, when no menu is open and the game is unpaused, put focus back on the
        -- viewport (all users). Mouse clicks on the HUD keep working; only text boxes need focus,
        -- and those live in menus, which we skip.
        State.focusTick = (State.focusTick or 0) + 1
        if isValid(State.p2PC) and State.focusTick % 7 == 0 then
            pcall(function()
                local p1 = getP1Pawn()
                if isValid(p1) and not UEHelpers.GetGameplayStatics():IsGamePaused(p1) and not menuVisible() then
                    StaticFindObject("/Script/UMG.Default__WidgetBlueprintLibrary"):SetFocusToGameViewport()
                    if not State.focusLogged then State.focusLogged = true; log("keeping Slate focus on the game viewport (analog stick fix)") end
                end
            end)
        end
        -- dual monitor: apply the helper's layout once it is available
        if State.dualPending then
            State.dualPending = State.dualPending - 1
            local lay = readDualLayout()
            if lay then
                State.dualPending = nil
                setVerticalSlots(lay.p1, lay.p2)
                setSplitLayout("Vertical")
                consoleCmd(string.format("r.SetRes %dx%dw", lay.w, lay.h))
                State.dualMonitor = true
                log("dual monitor: on (%dx%d, P1 slot %.2f/%.2f, P2 slot %.2f/%.2f)", lay.w, lay.h, lay.p1[1], lay.p1[2], lay.p2[1], lay.p2[2])
            elseif State.dualPending <= 0 then
                State.dualPending = nil
                log("dual monitor: helper produced no layout (only one monitor?)")
            end
        end
        -- gamepad bindings: the rebind UI wipes them on save load; restore whenever missing
        State.mapTick = (State.mapTick or 0) + 1
        if State.mapTick % 13 == 0 then pcall(ensureGamepadMappings, false) end
        -- periodic diagnostics (every ~5 s) while P2 exists
        State.diagTick = (State.diagTick or 0) + 1
        if Config.Debug and isValid(State.p2Pawn) and State.diagTick % 33 == 0 then
            pcall(function()
                local p2, pc1, p1 = State.p2Pawn, State.p2PC, getP1Pawn()
                local pc0 = getPC(0)
                local v = p2:GetVelocity(); local l = p2:K2_GetActorLocation()
                local function b(x) local ok, r = pcall(x); return ok and tostring(r) or "?" end
                local v1 = p1:GetVelocity(); local l1 = p1:K2_GetActorLocation()
                local mi = "?"; pcall(function() local m = p2:GetLastMovementInputVector(); mi = string.format("%.2f/%.2f", m.X, m.Y) end)
                local mi1 = "?"; pcall(function() local m = p1:GetLastMovementInputVector(); mi1 = string.format("%.2f/%.2f", m.X, m.Y) end)
                local vis = ""; pcall(function() for _, cls in ipairs(MenuWidgetClasses) do for _, w in ipairs(FindAllOf(cls) or {}) do if isValid(w) and w:IsVisible() then vis = vis .. cls .. " " end end end end)
                local maps = "?"; pcall(function()
                    local settings = StaticFindObject("/Script/Engine.Default__InputSettings"); local ga, ta, gc, tc = 0, 0, 0, 0
                    settings.AxisMappings:ForEach(function(_, e) ta = ta + 1; if e:get().Key.KeyName:ToString():find("^Gamepad") then ga = ga + 1 end end)
                    settings.ActionMappings:ForEach(function(_, e) tc = tc + 1; if e:get().Key.KeyName:ToString():find("^Gamepad") then gc = gc + 1 end end)
                    maps = string.format("axis %d/%d action %d/%d", ga, ta, gc, tc) end)
                log("diag P2 loc=(%.0f,%.0f,%.0f) vel=%.0f moveIn=%s mode=%s tick=%s/%s maps=[%s] | P1 loc=(%.0f,%.0f,%.0f) vel=%.0f moveIn=%s | menus=[%s] paused=%s ctrl=%s pc1.pawn=%s moveIgnored=%s cine=%s speed=%s hidden=%s | P1 moveIgnored=%s | offsetIds=%s pc1.id=%s",
                    l.X, l.Y, l.Z, math.sqrt(v.X*v.X+v.Y*v.Y), mi,
                    b(function() return p2.CharacterMovement.MovementMode end),
                    b(function() return p2:IsActorTickEnabled() end), b(function() return p2.CharacterMovement:IsComponentTickEnabled() end), maps,
                    l1.X, l1.Y, l1.Z, math.sqrt(v1.X*v1.X+v1.Y*v1.Y), mi1, vis,
                    b(function() return UEHelpers.GetGameplayStatics():IsGamePaused(p2) end),
                    nameOf(p2.Controller), isValid(pc1) and nameOf(pc1.Pawn) or "nil",
                    b(function() return pc1:IsMoveInputIgnored() end), b(function() return pc1.bCinematicMode end),
                    b(function() return p2.CharacterMovement.MaxWalkSpeed end), b(function() return p2.bHidden end),
                    b(function() return pc0:IsMoveInputIgnored() end),
                    b(function() return UEHelpers.GetGameMapsSettings().bOffsetPlayerGamepadIds end),
                    b(function() return pc1.Player.ControllerId end))
            end)
        end
        -- camera
        if isValid(State.p2Pawn) and isValid(State.p2Camera) then
            local ok, err = pcall(updateP2Camera)
            if not ok then log("camera update error: %s", tostring(err)) end
        end
    end)
    return false
end)

log("mod folder: %s", ModDir)
log("loaded. AutoJoin=%s Debug=%s | F1 = toggle split layout, F8 = dual monitor, F2 = join player 2, F3 = remove, F4 = teleport P2 to P1", tostring(Config.AutoJoin), tostring(Config.Debug))
