-- Character creator for Flash-Engine: THE spawn adapter for this server.
--
-- This is the "custom adapter" case from flash-core's neutral spawn contract. flash-core no
-- longer spawns anyone (setr flash_spawn_adapter "custom"); instead THIS resource decides what
-- happens between join and spawn:
--
--   1. Hide + freeze the player the moment the session starts (no visible fall into the map).
--   2. Ask the server (over flash-core's RPC bridge) whether this character already has a look.
--        - no look  -> NEW character: show the creator UI, let them build a face, save it.
--        - a look   -> RETURNING character: apply it and skip straight to spawn.
--   3. Tell flash-core we're ready:  TriggerServerEvent('flashfw:requestSpawn')
--   4. flash-core answers with WHERE:  'flashfw:spawnAt'(x, y, z, heading, hasPos)
--      -> we place the (already-styled) ped, unfreeze, fade in.
--
-- The placement block in flashfw:spawnAt is the same one flash-core ships in spawn_native.lua
-- (the reference implementation) -- we only put a creator in front of it.

-- === Appearance ==========================================================================
-- Opaque to the server (stored as JSON). Kept deliberately small for a template: swap in the
-- full head-blend / overlay / clothing surface you need.
local function defaultAppearance()
    return {
        gender    = 'male', -- 'male' | 'female' -> freemode model
        father    = 0,      -- 0..45  face parent A (SetPedHeadBlendData shapeFirst)
        mother    = 21,     -- 0..45  face parent B (shapeSecond)
        faceMix   = 0.5,    -- 0..1   blend between the two parents
        skin      = 0,      -- 0..45  skin tone parent
        hair      = 0,      -- hair component (drawable index)
        hairColor = 0,      -- 0..63  hair + eyebrow colour
        eyebrows  = 0,      -- eyebrow overlay index
    }
end

local appearance = defaultAppearance()

-- Apply an appearance table to a ped. Model is handled by rebuildPed (a model swap needs a
-- fresh ped handle first), so this only touches head blend + overlays + hair.
local function applyAppearance(ped, a)
    -- shapeMix uses faceMix; skin is applied at full weight from the single skin parent.
    SetPedHeadBlendData(ped, a.father, a.mother, 0, a.skin, a.skin, 0, a.faceMix + 0.0, 1.0, 0.0, false)
    SetPedComponentVariation(ped, 2, a.hair, 0, 0)            -- component 2 = hair
    SetPedHairColor(ped, a.hairColor, a.hairColor)
    SetPedHeadOverlay(ped, 2, a.eyebrows, 1.0)                -- overlay 2 = eyebrows
    SetPedHeadOverlayColor(ped, 2, 1, a.hairColor, a.hairColor)
end

-- Ensure the ped is the right freemode model, then apply the look. Returns true if the model
-- (and therefore the ped handle) changed -- the caller re-points the camera in that case.
local function rebuildPed(a)
    local wanted = GetHashKey((a.gender == 'female') and 'mp_f_freemode_01' or 'mp_m_freemode_01')
    local changed = false
    if GetEntityModel(PlayerPedId()) ~= wanted then
        RequestModel(wanted)
        local deadline = GetGameTimer() + 10000
        while not HasModelLoaded(wanted) and GetGameTimer() < deadline do Wait(0) end
        if HasModelLoaded(wanted) then
            SetPlayerModel(PlayerId(), wanted)
            SetModelAsNoLongerNeeded(wanted)
            SetPedDefaultComponentVariation(PlayerPedId()) -- clean base before we style it
            changed = true
        end
    end
    applyAppearance(PlayerPedId(), a)
    return changed
end

-- === Creator scene (camera + NUI) ========================================================
-- Camera comes from flash-core's lifecycle primitives (Slice 3): orbitCam places a scripted
-- cam in front of the ped and points it at them -- no hand-rolled CreateCamWithParams here.
local creatorCam

local function openCreator()
    creatorCam = exports['flash-core']:orbitCam({ distance = 2.0, height = 0.2 })
    SetNuiFocus(true, true)
    SendNUIMessage({ action = 'open', appearance = appearance })
    DoScreenFadeIn(500)
end

local function closeCreator()
    SendNUIMessage({ action = 'close' })
    SetNuiFocus(false, false)
    exports['flash-core']:stopCam(creatorCam)
    creatorCam = nil
end

-- === NUI callbacks =======================================================================
-- Live preview: every slider/toggle in the UI posts the whole appearance back.
RegisterNUICallback('update', function(data, cb)
    appearance = data
    local modelChanged = rebuildPed(appearance)
    if modelChanged and creatorCam then
        PointCamAtEntity(creatorCam, PlayerPedId(), 0.0, 0.0, 0.2, true) -- new ped handle
    end
    cb('ok')
end)

-- "Enter world": persist the look, leave the creator, ask flash-core where to spawn.
local confirming = false -- double-click guard: "Enter world" must trigger exactly ONE spawn

RegisterNUICallback('confirm', function(data, cb)
    cb('ok')
    if confirming then return end
    confirming = true
    appearance = data
    CreateThread(function()
        DoScreenFadeOut(500)
        while not IsScreenFadedOut() do Wait(0) end
        rebuildPed(appearance)
        -- Best-effort save (over flash-core's RPC bridge). Wrapped: a DB hiccup must not
        -- trap the player in the creator -- spawning is more important than the save.
        pcall(function()
            exports['flash-core']:rpcCall('charcreator:save', { json.encode(appearance) })
        end)
        closeCreator()
        TriggerServerEvent('flashfw:requestSpawn')
    end)
end)

-- === Spawn contract ======================================================================
-- flash-core answers requestSpawn with WHERE to spawn. The placement itself is flash-core's
-- canonical `spawn` export (Slice 3): collision streaming, the apply pipeline (-> other
-- appliers get a turn, then flashfw:playerReady fires server-side), unfreeze/unhide, fade-in
-- and the standard 'playerSpawned' event -- all in one call. The ped is already styled here,
-- so this resource only decides the coordinates.
RegisterNetEvent('flashfw:spawnAt', function(x, y, z, heading, hasPos)
    if not hasPos then
        -- Brand-new character: your start point (Legion Square here; move as you like).
        x, y, z, heading = 195.17, -933.77, 30.69, 144.0
    end
    exports['flash-core']:spawn(x, y, z, heading)
end)

-- === Entry: hide first, then decide new vs. returning ====================================
CreateThread(function()
    while not NetworkIsSessionStarted() do Wait(100) end

    -- Hide + freeze BEFORE anything else, so the player never visibly falls into the world
    -- while we set up the creator or wait on the spawn contract. One flash-core primitive
    -- (Slice 3) instead of a hand-rolled fade/hide/freeze block; the later `spawn` call
    -- releases all of it again.
    exports['flash-core']:freezeInLimbo(true)

    -- Does this character already have a look? (nil/false -> new -> open the creator.)
    local saved
    pcall(function() saved = exports['flash-core']:rpcCall('charcreator:load') end)

    if saved and saved ~= '' then
        appearance = json.decode(saved)
        rebuildPed(appearance)
        TriggerServerEvent('flashfw:requestSpawn')  -- returning: straight into the world
    else
        appearance = defaultAppearance()
        -- CREATION SPOT (live-test finding): with the custom adapter nothing spawned the ped
        -- yet -- he is at the raw join position in unstreamed nowhere, so the creator camera
        -- would stare into the void. Place him at a fixed, streamed-in spot first.
        local ped = PlayerPedId()
        local cx, cy, cz, ch = 195.17, -933.77, 30.69, 144.0 -- Legion Square (match your spawn)
        RequestCollisionAtCoord(cx, cy, cz)
        SetEntityCoordsNoOffset(ped, cx, cy, cz, false, false, false)
        SetEntityHeading(ped, ch)
        local deadline = GetGameTimer() + 5000
        while not HasCollisionLoadedAroundEntity(ped) and GetGameTimer() < deadline do
            RequestCollisionAtCoord(cx, cy, cz)
            Wait(0)
        end
        rebuildPed(appearance)
        ped = PlayerPedId()                 -- a model swap yields a fresh handle
        SetEntityVisible(ped, true, false)  -- visible for the creator camera
        FreezeEntityPosition(ped, true)     -- stays frozen (preview only)
        openCreator()                       -- new: run the creator, spawn on confirm
    end
end)
