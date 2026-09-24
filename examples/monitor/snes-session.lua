-- This script runs the monitor on MAME's Super NES for test.ps1. test.ps1 writes a script that
-- sets `session` and then runs this one. `session` holds these fields:
--
--   typed    The text to type, with a carriage return at the end of each line.
--   keys     The address of the keyboard's table, `platform::keyboard::keys`, which says what
--            each of its places types.
--   pad      The address of `platform::keyboard::pad`, the buttons the monitor last read.
--   screen   The address of `platform::screen::screen`, the text screen.
--   halt     The address of `platform::halt`, where the monitor stops after `X`.
--   output   The file the text screen is saved to, once the monitor stops.
--
-- The script types on the on-screen keyboard as someone with a joypad would. For each character
-- it finds the shortest way there over the keyboard, moving the cursor with the d-pad the way the
-- monitor does, and presses A. It presses START at the end of each line. The joypad holds each
-- state until the monitor has read it, so nothing is lost while the monitor is busy.

local KEY_COLUMNS = 16
local KEYS = 48
local COLUMNS = 32
local TEXT_ROWS = 24

-- The buttons, as MAME names them, and as the S-CPU reads them.
local BUTTONS = {
    { name = "P1 B", bit = 0x8000 },
    { name = "P1 Y", bit = 0x4000 },
    { name = "P1 Select", bit = 0x2000 },
    { name = "P1 Start", bit = 0x1000 },
    { name = "P1 Up", bit = 0x0800 },
    { name = "P1 Down", bit = 0x0400 },
    { name = "P1 Left", bit = 0x0200 },
    { name = "P1 Right", bit = 0x0100 },
    { name = "P1 A", bit = 0x0080 },
}
local B, START, UP, DOWN, LEFT, RIGHT, A = 0x8000, 0x1000, 0x0800, 0x0400, 0x0200, 0x0100, 0x0080

local cpu = manager.machine.devices[":maincpu"]
local memory = cpu.spaces["program"]
local fields = manager.machine.ioport.ports[":ctrl1:joypad:JOYPAD"].fields

-- What each place on the keyboard types, from 0.
local keys = {}

-- Returns the place the cursor moves to from `at` for the d-pad buttons in `buttons`. It moves
-- across and then up or down, as the monitor's `move` does.
local function move(at, buttons)
    local was = keys[at]
    if buttons & RIGHT ~= 0 then
        repeat
            at = at + 1
            if at % KEY_COLUMNS == 0 then
                at = at - KEY_COLUMNS
            end
        until keys[at] ~= was
    elseif buttons & LEFT ~= 0 then
        repeat
            if at % KEY_COLUMNS == 0 then
                at = at + KEY_COLUMNS
            end
            at = at - 1
        until keys[at] ~= was
    end
    if buttons & UP ~= 0 then
        at = (at - KEY_COLUMNS) % KEYS
    elseif buttons & DOWN ~= 0 then
        at = (at + KEY_COLUMNS) % KEYS
    end
    return at
end

local MOVES = {
    RIGHT, LEFT, UP, DOWN,
    RIGHT | UP, RIGHT | DOWN, LEFT | UP, LEFT | DOWN,
}

-- Returns the shortest list of d-pad states that moves the cursor from `from` to a place that
-- types `code`, and the place it ends on.
local function path(from, code)
    local came = { [from] = false }
    local queue = { from }
    local head = 1
    while head <= #queue do
        local at = queue[head]
        head = head + 1
        if keys[at] == code then
            local moves = {}
            local to = at
            while came[at] do
                table.insert(moves, 1, came[at].buttons)
                at = came[at].from
            end
            return moves, to
        end
        for _, buttons in ipairs(MOVES) do
            local to = move(at, buttons)
            if came[to] == nil then
                came[to] = { from = at, buttons = buttons }
                queue[#queue + 1] = to
            end
        end
    end
    error(string.format("the keyboard has no key for %q", string.char(code)))
end

-- Returns the joypad states that type `typed`, each a set of buttons. A button held in two states
-- in a row counts as pressed only in the first, so a state with no buttons comes between them.
local function plan(typed)
    local states = {}
    local function press(buttons)
        local last = states[#states] or 0
        if last & buttons ~= 0 then
            states[#states + 1] = 0
        end
        states[#states + 1] = buttons
    end
    local at = 0
    for i = 1, #typed do
        local code = typed:byte(i)
        if code == 13 then
            press(START)
        else
            local moves
            moves, at = path(at, code)
            -- The monitor moves the cursor before it types, so A goes with the last move.
            for m = 1, #moves - 1 do
                press(moves[m])
            end
            press((moves[#moves] or 0) | A)
        end
    end
    return states
end

-- Returns the text screen, a row to a line.
local function text()
    local rows = {}
    for row = 0, TEXT_ROWS - 1 do
        local line = {}
        for column = 0, COLUMNS - 1 do
            local code = memory:read_u8(session.screen + row * COLUMNS + column)
            line[#line + 1] = (code >= 0x20 and code < 0x60) and string.char(code) or "#"
        end
        rows[#rows + 1] = (table.concat(line):gsub("%s+$", ""))
    end
    return table.concat(rows, "\n") .. "\n"
end

-- Holds the buttons in `buttons` down, and lets the rest up.
local function hold(buttons)
    for _, button in ipairs(BUTTONS) do
        fields[button.name]:set_value((buttons & button.bit ~= 0) and 1 or 0)
    end
end

local states
local next_state = 1

-- The subscription is kept by the callback that ends it, or it would be collected.
local frames
frames = emu.add_machine_frame_notifier(function()
    if not states then
        for place = 0, KEYS - 1 do
            keys[place] = memory:read_u8(session.keys + place)
        end
        states = plan(session.typed)
        hold(states[1])
        return
    end
    if next_state <= #states then
        -- The monitor has read the state held, so the next one goes down.
        if memory:read_u16(session.pad) == states[next_state] then
            next_state = next_state + 1
            hold(states[next_state] or 0)
        end
        return
    end
    local pc = cpu.state["PC"].value
    if pc >= session.halt and pc < session.halt + 3 then
        frames:unsubscribe()
        local file = io.open(session.output, "wb")
        file:write(text())
        file:close()
        manager.machine:exit()
    end
end)
