fx_version 'cerulean'
game 'gta5'

-- flash-core must be running: this resource uses its RPC bridge (rpc.lua) and its neutral
-- spawn contract (flashfw:requestSpawn <-> flashfw:spawnAt). Load order is enforced here.
dependency 'flash-core'

-- Server half (C#): appearance persistence keyed by the character id (cid). Loads the DLL.
server_script 'main.flash'

-- Client half (Lua): THE spawn adapter. It hides the player on join, runs the creator,
-- and drives the flash-core spawn contract. REQUIRED for this to work at all:
--     setr flash_spawn_adapter "custom"
-- otherwise flash-core's built-in spawn_native.lua would also spawn the player and race
-- this resource. See README.md.
client_script 'client.lua'

-- Minimal self-contained creator UI (no build step, no external assets).
ui_page 'html/index.html'
files { 'html/index.html' }
