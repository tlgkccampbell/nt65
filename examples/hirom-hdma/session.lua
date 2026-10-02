-- This script runs the demo on MAME's Super NES for test.ps1, which writes a script that sets
-- `session` and then runs this one. `session` holds these fields:
--
--   frames   The address of `main::frames`, the frames since the screen came on.
--   wanted   How many frames to run for.
--   regions  The memory to write out, each a name, an address and a count of bytes.
--   state    The file the regions are written to, as text, a line to each 32 bytes.
--   screen   The file the screen is written to, as MAME's pixels, four bytes to a dot.
--
-- Once the demo has counted the frames, at the end of a frame, the script writes the two files,
-- saves a snapshot as snap/demo.png and leaves MAME.

local cpu = manager.machine.devices[":maincpu"]
local memory = cpu.spaces["program"]
local screen = manager.machine.screens[":screen"]

-- Returns `count` bytes from `address`, as hexadecimal pairs with a space between them.
local function bytes(address, count)
    local hex = {}
    for i = 0, count - 1 do
        hex[#hex + 1] = string.format("%02X", memory:read_u8(address + i))
    end
    return table.concat(hex, " ")
end

-- The subscription is kept by the callback that ends it, or it would be collected.
local frames
frames = emu.add_machine_frame_notifier(function()
    if memory:read_u32(session.frames) < session.wanted then
        return
    end
    frames:unsubscribe()

    local state = io.open(session.state, "w")
    for _, region in ipairs(session.regions) do
        local name, address, size = region[1], region[2], region[3]
        for at = 0, size - 1, 32 do
            local label = size > 32 and string.format("%s+%03X", name, at) or name
            state:write(string.format("%s %s\n", label, bytes(address + at, math.min(32, size - at))))
        end
    end
    state:close()

    local file = io.open(session.screen, "wb")
    file:write(screen:pixels())
    file:close()
    screen:snapshot("demo.png")
    manager.machine:exit()
end)
