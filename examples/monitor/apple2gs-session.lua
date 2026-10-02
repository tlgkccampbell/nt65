-- This script runs the monitor on MAME's Apple IIGS for test.ps1 and run.ps1. Each of those
-- writes a script that sets `session` and then runs this one, and starts MAME with a ProDOS 8
-- disk in the first drive. `session` holds these fields:
--
--   image    The binary file, which is loaded at `load` and entered there.
--   typed    The lines to type, each ended by a carriage return.
--   screen   The file the 80-column screen is saved to when the monitor leaves.
--
-- A session that gives no `typed` is for someone at the keyboard. Nothing is typed or saved
-- then.
--
-- MAME runs with its debugger on and no window for it, which stops the machine where the script
-- needs to step in. The machine boots ProDOS from the disk. Once ProDOS is in memory, it loads
-- the first system program on the disk at `load` and enters it there. The debugger stops it as
-- it does, and the script loads the file over that program and enters it instead, as BRUN would
-- enter it.
--
-- The monitor marks two places with `wdm`, which the IIGS runs as a two-byte instruction that
-- does nothing: where it is about to read a line, and where it leaves. Its operand says which.
-- The debugger stops the machine at every `wdm`, where the script types the session's next
-- line, or saves the screen and quits. A line is typed only once the monitor reads it, because a
-- key pressed while ProDOS reads or writes the disk is lost.

local WDM = 0x42
local READING = 1
local LEAVING = 2

-- The MLI's entry, which holds a JMP once ProDOS is in memory.
local MLI = 0xBF00
local JMP = 0x4C

local cpu = manager.machine.devices[":maincpu"]
local memory = cpu.spaces["program"]
local debugger = manager.machine.debugger

-- The lines still to type, each with its carriage return.
local lines = {}
for line in (session.typed or ""):gmatch("[^\r]*\r") do
    lines[#lines + 1] = line
end

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

-- Loads the file over the program ProDOS is entering, and enters the file instead.
local function enter()
    local file = io.open(session.image, "rb")
    local image = file:read("a")
    file:close()
    for i = 1, #image do
        memory:write_u8(session.load + i - 1, image:byte(i))
    end
    -- This leaves the machine as BRUN does, in emulation mode with D and B at 0 and the stack
    -- in page 1.
    cpu.state["E"].value = 1
    cpu.state["PB"].value = 0
    cpu.state["DB"].value = 0
    cpu.state["D"].value = 0
    cpu.state["S"].value = 0x01FF
    cpu.state["P"].value = 0x34
    cpu.state["PC"].value = session.load
    cpu.debug:bpclear()
    if session.typed then
        -- A registerpoint's condition is read before every instruction, and stops the machine
        -- where it holds. The braces keep `==` from being read as an assignment.
        debugger:command(string.format("rpset {b@curpc == %x}", WDM))
    end
end

-- Sees each stop and lets the machine carry on. The debugger first stops the machine as it
-- starts, where the script sets the breakpoint that stops it as ProDOS enters a program. ProDOS
-- runs its own loader at the same address before the MLI is in place, which the breakpoint's
-- condition passes over.
local armed = false
local entered = false
local function stopped()
    local at = cpu.state["PC"].value
    if not armed then
        armed = true
        cpu.debug:bpset(session.load, string.format("b@%x == %x", MLI, JMP), "")
    elseif not entered and at == session.load then
        entered = true
        enter()
    elseif memory:read_u8(at) == WDM then
        local hook = memory:read_u8(at + 1)
        if hook == READING and #lines > 0 then
            manager.machine.natkeyboard:post(table.remove(lines, 1))
        elseif hook == LEAVING then
            local file = io.open(session.screen, "wb")
            file:write(screen())
            file:close()
            manager.machine:exit()
            return
        end
    end
    debugger.execution_state = "run"
end

emu.register_periodic(function()
    if debugger.execution_state == "stop" then
        stopped()
    end
end)
