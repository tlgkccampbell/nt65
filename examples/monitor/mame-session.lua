-- This script runs the monitor on MAME's Apple IIGS for test.ps1 and run.ps1. Each of those
-- writes a script that sets `session` and then runs this one. `session` holds these fields:
--
--   image    The binary file, which is loaded at `load` and entered there.
--   start    The address where the monitor starts, at which `typed` is typed.
--   finish   The address where the monitor leaves, at which the screen is saved to the file
--            `screen`.
--
-- A session that gives no `start` is for someone at the keyboard. Nothing is typed or saved
-- then, and MAME needs no debugger.
--
-- The machine boots with no disk until the firmware gives up looking for one, and the file is
-- then loaded straight into memory, as BRUN would load it.

local BOOT_FRAMES = 300

local cpu = manager.machine.devices[":maincpu"]
local memory = cpu.spaces["program"]

local load = session.load
local start = session.start
local finish = session.finish

-- Returns the character that a byte of the text screen shows. Normal video has bit 7 set, and
-- inverse video has the capitals and signs at $00 to $3F.
local function character(byte)
    byte = byte & 0x7F
    if byte < 0x20 then
        byte = byte + 0x40
    end
    return string.char(byte)
end

-- Returns the 80-column screen, one row to a line. Each row of 80 is 40 bytes of auxiliary
-- memory in bank $E1, for the even columns, and 40 bytes of main memory in bank $E0, for the
-- odd ones.
local function screen()
    local rows = {}
    for row = 0, 23 do
        local base = 0x0400 + (row % 8) * 0x80 + (row // 8) * 0x28
        local text = {}
        for column = 0, 39 do
            text[#text + 1] = character(memory:read_u8(0xE10000 + base + column))
            text[#text + 1] = character(memory:read_u8(0xE00000 + base + column))
        end
        rows[#rows + 1] = (table.concat(text):gsub("%s+$", ""))
    end
    return table.concat(rows, "\n") .. "\n"
end

-- MAME runs with its debugger on and no window for it, so that breakpoints stop the machine
-- where the session starts and ends. The function below sees each stop and lets the machine
-- carry on. The debugger also stops the machine as it starts, before either breakpoint is set.
local function stopped()
    local at = cpu.state["PC"].value
    if at == start then
        manager.machine.natkeyboard:post(session.typed)
    elseif at == finish then
        local file = io.open(session.screen, "wb")
        file:write(screen())
        file:close()
        manager.machine:exit()
        return
    end
    manager.machine.debugger.execution_state = "run"
end

if start then
    emu.register_periodic(function()
        if manager.machine.debugger.execution_state == "stop" then
            stopped()
        end
    end)
end

-- The subscription is kept by the callback that ends it, or it would be collected.
local frames = 0
local booting
booting = emu.add_machine_frame_notifier(function()
    frames = frames + 1
    if frames ~= BOOT_FRAMES then
        return
    end
    booting:unsubscribe()
    local file = io.open(session.image, "rb")
    local image = file:read("a")
    file:close()
    for i = 1, #image do
        memory:write_u8(load + i - 1, image:byte(i))
    end
    -- This leaves the machine as BRUN does, in emulation mode with D and B at 0 and the stack
    -- in page 1.
    cpu.state["E"].value = 1
    cpu.state["PB"].value = 0
    cpu.state["DB"].value = 0
    cpu.state["D"].value = 0
    cpu.state["S"].value = 0x01FF
    cpu.state["P"].value = 0x34
    cpu.state["PC"].value = load
    if start then
        cpu.debug:bpset(start, "1", "")
        cpu.debug:bpset(finish, "1", "")
    end
end)
