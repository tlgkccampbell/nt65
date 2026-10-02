-- This script runs the cartridge on MAME's NES for test.ps1. test.ps1 writes a script that sets
-- `session` and then runs this one. `session` holds these fields:
--
--   frames   The address of `main::frames`, the program's count of its frames.
--   stop     The frame count at which the run stops.
--   right    The frame counts from which and until which the joypad holds right.
--   skip     The first and last lines of the picture that the screen's hash leaves out.
--   drum     The address of `audio::drum`, the sample.
--   length   The sample's length in bytes.
--   memory   The file the NES's 2K of RAM is saved to when the run stops.
--   events   The file what the script saw is written to when the run stops.
--   shot     The file a picture of the screen is saved to when the run stops.
--
-- While the program runs, the script watches the bus. It notes the line each write to PPUSCROLL
-- lands on during the picture, where only the IRQ writes it, and counts the DMC's reads of the
-- sample, which the CPU never reads. When the run stops it writes the events file, a line for
-- each of these, a name and then numbers:
--
--   splits   each line PPUSCROLL was written on during the picture, with how many times, as
--            `line:count`
--   fetches  the DMC's reads of the sample
--   screen   a hash of the last frame's pixels, FNV-1a's of 32 bits, in hexadecimal, which
--            leaves out the lines `skip` names

local cpu = manager.machine.devices[":maincpu"]
local memory = cpu.spaces["program"]
local screen = manager.machine.screens[":screen"]
local fields = manager.machine.ioport.ports[":ctrl1:joypad:JOYPAD"].fields

local VISIBLE = 240                 -- the lines of the picture
local LINES = 262                   -- the lines of a frame, with vblank's
local STARTED = 10                  -- the frames after which the program has surely cleared RAM

-- The lines PPUSCROLL was written on during the picture, each with how many times.
local splits = {}
-- The DMC's reads of the sample.
local fetches = 0

-- Returns the line the PPU is drawing, from 0 at the top of the picture. The screen says how
-- long it is until line 0 starts again, and how long each line takes.
local function line_now()
    local gone = screen.frame_period - screen:time_until_pos(0, 0)
    return math.floor(gone / screen.scan_period) % LINES
end

-- The taps are kept by the callback that removes them, or they would be collected and removed
-- before the run is done.
local scroll_tap = memory:install_write_tap(0x2005, 0x2005, "split", function(offset, data, mask)
    local line = line_now()
    if line < VISIBLE then
        splits[line] = (splits[line] or 0) + 1
    end
end)
local drum_tap = memory:install_read_tap(session.drum, session.drum + session.length - 1, "drum",
    function(offset, data, mask)
        fetches = fetches + 1
    end)

-- Returns the program's frame count.
local function frames()
    return memory:read_u8(session.frames) | (memory:read_u8(session.frames + 1) << 8)
end

-- Returns FNV-1a's 32-bit hash of the screen's pixels, leaving out the lines `session.skip`
-- names.
local function hash()
    local pixels, width, height = screen:pixels()
    local line = #pixels // height
    local h = 0x811C9DC5
    for y = 0, height - 1 do
        if y < session.skip[1] or y > session.skip[2] then
            for i = y * line + 1, (y + 1) * line do
                h = ((h ~ pixels:byte(i)) * 0x01000193) & 0xFFFFFFFF
            end
        end
    end
    return h
end

-- Writes what the script saw.
local function report()
    local lines = {}
    for line, _ in pairs(splits) do
        lines[#lines + 1] = line
    end
    table.sort(lines)
    local counts = {}
    for _, line in ipairs(lines) do
        counts[#counts + 1] = string.format("%d:%d", line, splits[line])
    end
    local file = io.open(session.events, "w")
    file:write("splits " .. table.concat(counts, " ") .. "\n")
    file:write(string.format("fetches %d\n", fetches))
    file:write(string.format("screen %08X\n", hash()))
    file:close()
end

-- The subscription is kept by the callback that ends it, or it would be collected.
local subscription
local machine_frames = 0
subscription = emu.add_machine_frame_notifier(function()
    -- RAM holds whatever it powered up with until the program has cleared it.
    machine_frames = machine_frames + 1
    if machine_frames < STARTED then
        return
    end
    local now = frames()
    fields["P1 Right"]:set_value((now >= session.right[1] and now < session.right[2]) and 1 or 0)
    if now < session.stop then
        return
    end
    subscription:unsubscribe()
    scroll_tap:remove()
    drum_tap:remove()
    local file = io.open(session.memory, "wb")
    file:write(memory:read_range(0x0000, 0x07FF, 8))
    file:close()
    screen:snapshot(session.shot)
    report()
    manager.machine:exit()
end)
