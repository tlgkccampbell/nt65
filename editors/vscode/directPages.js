// The direct page map: a tree of the direct pages a program reaches memory through, the
// locations in each and the routines that use them, with a legend beside it. The server works the
// map out (`nt65/directPages`), and this file only draws it. Selecting a row marks the lines it
// stands for in the source, and the grid panel in directPagesGrid.js shows one page byte by byte.
const vscode = require('vscode');
const { Grid } = require('./directPagesGrid');
const { Caret, keyOf, uriOf } = require('./directPagesCaret');

const VIEW = 'nt65.directPages';
const LEGEND = 'nt65.directPagesLegend';

// How long the map waits after the last reason to ask for it before it asks.
const DELAY = 150;

// The context key that says whether hazards are shown, which picks the toolbar button.
const HAZARDS = 'nt65.directPages.hazards';

// The context key that says why the tree is empty, which picks its welcome message.
const EMPTY = 'nt65.directPages.empty';

// The command a row runs when it is clicked, which only the tree invokes, so it is not contributed.
const SELECT = 'nt65.directPages.select';

// The locations a page may hold before its rows start collapsed rather than expanded.
const OPEN_LOCATIONS = 8;

// The line references a tooltip row lists before it counts the rest.
const REFERENCES = 3;

// The routines a location's tooltip lists before it counts the rest.
const ROUTINE_ROWS = 6;

const COLOUR = {
  shared: 'nt65.directPages.shared',
  nested: 'nt65.directPages.nested',
  irq: 'nt65.directPages.irq',
  own: 'nt65.directPages.own',
  unused: 'nt65.directPages.unused',
  referenced: 'nt65.directPages.referenced',
  hw: 'nt65.directPages.hw',
  unknown: 'nt65.directPages.unknown',
  access: 'nt65.directPages.access',
  inout: 'nt65.directPages.inout',
  dim: 'descriptionForeground',
  method: 'symbolIcon.methodForeground',
};

// What a location's tooltip adds to its address for each layout source; a fixed address needs nothing.
const LAYOUTS = {
  built: ' · built',
  configured: ' · predicted',
  guessed: ' · guessed',
};

const RELATIONS = {
  shared: 'shared',
  nested: 'nested temps',
  irq: 'interrupt',
  own: 'one owner',
  unused: 'unused',
  hw: 'hardware',
  unknown: 'not known',
};

const ROLES = {
  in: { glyph: '↓', word: 'in', colour: COLOUR.access },
  out: { glyph: '↑', word: 'out', colour: COLOUR.shared },
  inout: { glyph: '↕', word: 'in · out', colour: COLOUR.inout },
  temp: { glyph: '◦', word: 'temp', colour: COLOUR.dim },
  write: { glyph: 'w', word: 'write', colour: COLOUR.hw },
  read: { glyph: 'r', word: 'read', colour: COLOUR.hw },
};

const REASONS = {
  interrupted: 'interrupted code',
  unknown: 'not known',
};

// Returns an address as `$` and four hexadecimal digits.
function hex4(value) {
  return `$${(value & 0xFFFF).toString(16).toUpperCase().padStart(4, '0')}`;
}

// Returns a byte offset as `$` and two hexadecimal digits.
function hex2(value) {
  return `$${value.toString(16).toUpperCase().padStart(2, '0')}`;
}

// Returns a range of absolute addresses, written `$0080–87`, or one address alone.
function addresses(first, last) {
  if (first === last) return hex4(first);
  return `${hex4(first)}–${(last & 0xFFFF).toString(16).toUpperCase().padStart(2, '0')}`;
}

// Returns where a location lies in its page, written `+$00–01`, or an empty string when its
// offset is not known.
function offsets(location) {
  if (location.offset === null || location.offset === undefined) return '';
  const size = Math.max(1, location.size || 1);
  if (size === 1) return `+${hex2(location.offset)}`;
  return `+${hex2(location.offset)}–${(location.offset + size - 1).toString(16).toUpperCase().padStart(2, '0')}`;
}

// Returns the label of a page's row and of its grid tab.
function pageLabel(page) {
  return page.base === null ? 'D = ?' : `D = ${hex4(page.base)}`;
}

// Returns what a page holds, in a few words: its segments, or that it is hardware or not known.
function pageKind(page) {
  if (page.segments.length > 0) return page.segments.join(', ');
  if (page.hardware) return 'hardware';
  return page.base === null ? 'not known' : '';
}

// Checks whether no instruction reaches a location but the program takes its address, as
// `ldx #tmp` or `.addr tmp` does, so that it is used through a pointer or an index.
function addressTaken(location) {
  return location.relation === 'unused' && (location.references || []).length > 0;
}

// Checks whether a location is a hardware register that its page covers but no instruction
// reaches. The grid shows every such register, and the tree only those a note names.
function declaredOnly(location) {
  return location.relation === 'hw' && location.reached === false;
}

// Returns the names of the locations that the notes on where an access lands name, such as
// `OAMDATA` in "D = $2100 here reaches $2104, `OAMDATA`".
function landed(result) {
  const names = new Set();
  const take = notes => {
    for (const note of notes || []) {
      const match = /reaches \$[0-9A-Fa-f]+, `([^`]+)`/.exec(note.text);
      if (match) names.add(match[1]);
    }
  };
  for (const page of result ? result.pages : []) {
    for (const location of page.locations) flatten(location.routines).forEach(node => take(node.hazards));
    for (const group of page.groups || []) {
      for (const routine of group.routines) routine.uses.forEach(use => take(use.hazards));
    }
  }
  return names;
}

// Returns the page with an id from a map.
function pageOf(result, id) {
  return result.pages.find(page => page.id === id);
}

// Builds the tree's elements from the map. Every element records its parent, for `reveal`, and
// has an id that stays the same across refreshes, so that VS Code keeps what is expanded. The
// tree leaves out the hardware registers no instruction reaches, unless a note names one, to
// keep it short; the grid shows them all.
function build(result) {
  const named = landed(result);
  const byId = new Map();
  const make = (kind, id, parent, fields) => {
    let unique = id;
    for (let n = 2; byId.has(unique); n++) unique = `${id}#${n}`;
    const element = { kind, id: unique, parent, children: [], ...fields };
    byId.set(unique, element);
    if (parent) parent.children.push(element);
    return element;
  };
  const routine = (page, location, node, parent) => {
    const element = make('routine', `${parent.id}/r:${node.name}`, parent, { page, location, node });
    for (const child of node.children || []) routine(page, location, child, element);
  };
  const roots = [];
  for (const page of result ? result.pages : []) {
    const top = make('page', `p:${page.id}`, null, { page });
    roots.push(top);
    for (const location of page.locations) {
      if (declaredOnly(location) && !named.has(location.name)) continue;
      const row = make('location', `${top.id}/l:${location.name}`, top, { page, location });
      for (const node of location.routines) routine(page, location, node, row);
    }
    for (const group of page.groups || []) {
      const header = make('group', `${top.id}/g:${group.reason}`, top, { page, group });
      for (const caller of group.routines) {
        const row = make('unknownRoutine', `${header.id}/u:${caller.name}`, header, { page, group, routine: caller });
        for (const use of caller.uses) make('use', `${row.id}/${use.name}`, row, { page, group, routine: caller, use });
      }
    }
  }
  return { roots, byId };
}

// Returns a coloured codicon.
function icon(name, colour) {
  return new vscode.ThemeIcon(name, colour ? new vscode.ThemeColor(colour) : undefined);
}

// Escapes text for Markdown with HTML allowed, leaving code spans as they are, since nothing in a
// code span is read as markup.
function markdown(text) {
  return String(text).split(/(`[^`]*`)/).map((part, index) => index % 2 === 1
    ? part
    : part.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/([\\*_[\]#|])/g, '\\$1')).join('');
}

// Returns text wrapped in a span that colours it with a theme colour. Hover HTML accepts a colour
// only as a hexadecimal value or a `--vscode-` variable, and every theme colour has one.
function coloured(colour, text) {
  return `<span style="color:var(--vscode-${colour.replace(/\./g, '-')});">${text}</span>`;
}

// Returns a link to a line, written `L12`, that opens the file at that line.
function lineLink(place) {
  const line = place.range.start.line + 1;
  const target = place.uri.replace(/ /g, '%20').replace(/\(/g, '%28').replace(/\)/g, '%29');
  return `[L${line}](${target}#L${line})`;
}

// Returns links to the lines of some places, at most a few, followed by a count of the rest.
function lineLinks(places) {
  const seen = new Map();
  for (const place of places) {
    if (!place) continue;
    const key = `${place.uri}#${place.range.start.line}`;
    if (!seen.has(key)) seen.set(key, place);
  }
  const all = [...seen.values()].sort((a, b) => a.uri === b.uri
    ? a.range.start.line - b.range.start.line
    : a.uri.localeCompare(b.uri));
  const shown = all.slice(0, REFERENCES).map(lineLink);
  if (all.length > REFERENCES) shown.push(coloured(COLOUR.dim, `+${all.length - REFERENCES}`));
  return shown.join(' ');
}

// Builds a tooltip in the hover's shape: a header line with a name and a few words about it,
// then one row per fact, each a coloured glyph, a short phrase and where it is.
class Tip {
  constructor(name, meta) {
    this.parts = [`**${markdown(name)}**${meta ? ` &nbsp;${coloured(COLOUR.dim, markdown(meta))}` : ''}`, '\n\n---\n\n'];
    this.rows = [];
  }

  // Adds a row. `phrase` is Markdown, and `where` is Markdown or an empty string.
  row(glyph, colour, phrase, where) {
    this.rows.push(`${coloured(colour, glyph)}&nbsp;&nbsp;${phrase}${where ? `&nbsp;&nbsp;${where}` : ''}`);
    return this;
  }

  // Adds a block of Markdown of its own, such as a picture.
  block(text) {
    this.rows.push(text);
    return this;
  }

  // Returns the tooltip as a Markdown string.
  build() {
    const text = new vscode.MarkdownString(this.parts.join('') + (this.rows.length > 0 ? this.rows.join('  \n') : coloured(COLOUR.dim, 'nothing to show')));
    text.supportHtml = true;
    return text;
  }
}

// Returns a picture of one page's 256 bytes as an SVG data URI, in Markdown: the bytes the page's
// own locations take, the stretches other pages cover hatched, and the bytes both take amber.
// The picture is an image, so theme colours do not reach it, and its colours are ones that read
// on light and dark backgrounds alike.
function pageBar(page) {
  const clip = ([from, to]) => [Math.max(0, from), Math.min(256, to)];
  const spans = (ranges, fill) => ranges.map(clip).filter(([from, to]) => to > from)
    .map(([from, to]) => `<rect x="${from}" y="0" width="${to - from}" height="12" fill="${fill}"/>`).join('');
  const used = page.locations.filter(location => location.offset !== null)
    .map(location => [location.offset, location.offset + Math.max(1, location.size || 1)]);
  const other = page.overlaps.map(overlap => [overlap.first - page.base, overlap.last - page.base + 1]);
  const shared = page.overlaps.flatMap(overlap => overlap.shared)
    .map(bytes => [bytes.first - page.base, bytes.last - page.base + 1]);
  const text = (x, anchor, value) =>
    `<text x="${x}" y="23" font-family="monospace" font-size="9" fill="#8a8a8a" text-anchor="${anchor}">${value}</text>`;
  const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="26" viewBox="0 0 256 26">'
    + '<defs><pattern id="h" width="5" height="5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">'
    + '<rect width="2" height="5" fill="#8a8a8a"/></pattern></defs>'
    + '<rect width="256" height="12" rx="2" fill="#8080802e"/>'
    + spans(other, 'url(#h)') + spans(used, '#3794ff') + spans(shared, '#cca700')
    + text(0, 'start', hex4(page.base)) + text(128, 'middle', '+$80') + text(256, 'end', hex4(page.base + 0xFF))
    + '</svg>';
  const key = `${coloured(COLOUR.own, '■')} this page &nbsp;${coloured(COLOUR.hw, '▨')} other page`
    + (shared.length > 0 ? ` &nbsp;${coloured(COLOUR.nested, '■')} both` : '');
  return `![page](data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')})  \n${key}`;
}

// Returns every node of some call trees, parents before children.
function flatten(nodes) {
  return nodes.flatMap(node => [node, ...flatten(node.children || [])]);
}

// Returns the name a page goes by in a phrase: its segments, or its id.
function pageName(result, id) {
  const page = pageOf(result, id);
  return page && page.segments.length > 0 ? page.segments.join(', ') : id;
}

// Builds the tooltip of a page's row.
function pageTip(result, page, hazards) {
  if (page.base === null) {
    const count = page.groups.length;
    const tip = new Tip('D = ?', `${count} group${count === 1 ? '' : 's'}`);
    for (const group of page.groups) {
      const n = group.routines.length;
      tip.row(group.reason === 'interrupted' ? '⚡' : '?', group.reason === 'interrupted' ? COLOUR.irq : COLOUR.dim,
        markdown(REASONS[group.reason] || group.reason), coloured(COLOUR.dim, `${n} routine${n === 1 ? '' : 's'}`));
    }
    return tip.build();
  }
  const reached = page.locations.filter(location => !declaredOnly(location)).length;
  const meta = page.hardware
    ? `hardware · ${reached} of ${count(page.locations.length, 'register')} reached`
    : `${pageKind(page) || 'no segment'} · ${page.used} / 256`;
  const tip = new Tip(pageLabel(page), meta);
  if (page.overlaps.length > 0) tip.block(pageBar(page));
  if ((page.base & 0xFF) !== 0 && page.direct > 0) {
    tip.row('⚠', COLOUR.nested, `not page-aligned · +1 cycle on ${count(page.direct, 'instruction')}`, '');
  }
  for (const note of page.notes) tip.row(note.glyph, note.glyph === '⧉' ? COLOUR.nested : COLOUR.dim, markdown(note.text), '');
  for (const overlap of page.overlaps) {
    if (overlap.shared.length === 0) {
      tip.row('⧉', COLOUR.dim, `${markdown(pageName(result, overlap.page))} page, no bytes shared`,
        coloured(COLOUR.dim, addresses(overlap.first, overlap.last)));
    }
    for (const bytes of overlap.shared) {
      tip.row('⧉', COLOUR.nested, `${markdown(pageName(result, bytes.page))} \`${bytes.there}\``,
        coloured(COLOUR.dim, addresses(bytes.first, bytes.last)));
    }
  }
  for (const location of page.locations) {
    if (hazards && location.hazard) {
      tip.row('⚠', COLOUR.nested, `\`${location.name}\` ${RELATIONS[location.relation] || ''}`, '');
    } else if (location.relation === 'irq') {
      tip.row('⚡', COLOUR.irq, `\`${location.name}\` interrupt`, '');
    } else if (location.relation === 'nested') {
      tip.row('●', COLOUR.nested, `\`${location.name}\` nested temps`, '');
    }
  }
  return tip.build();
}

// Returns the glyph and colour that stand for a relation in a tooltip row.
function relationMark(relation) {
  switch (relation) {
    case 'unused': return ['○', COLOUR.unused];
    case 'irq': return ['⚡', COLOUR.irq];
    case 'hw': return ['w', COLOUR.hw];
    default: return ['●', COLOUR[relation] || COLOUR.dim];
  }
}

// Builds the tooltip of a location's row.
function locationTip(result, page, location, hazards) {
  const where = location.address === null
    ? '?'
    : addresses(location.address, location.address + Math.max(1, location.size || 1) - 1);
  const source = location.address !== null ? LAYOUTS[location.layout] || '' : '';
  const tip = new Tip(location.name, `${where}${location.type ? ` · ${location.type}` : ''}${source}`);
  const nodes = flatten(location.routines);
  const users = new Set(nodes.filter(node => node.role).map(node => node.name));
  if (addressTaken(location)) {
    tip.row('◎', COLOUR.referenced, 'address taken', lineLinks(location.references));
  } else if (declaredOnly(location)) {
    tip.row('○', COLOUR.hw, 'hardware · declared, not reached', '');
  } else {
    const [glyph, colour] = relationMark(location.relation);
    const relation = RELATIONS[location.relation] || location.relation;
    tip.row(glyph, colour, users.size > 1 ? `${relation} · ${users.size} routines` : relation, '');
  }
  if (location.accesses > 0) {
    tip.row('#', COLOUR.dim, `${count(location.accesses, 'instruction')} · ${times(location.perPass)} a pass`, '');
    if (location.uncounted > 0) tip.row('∞', COLOUR.dim, `${location.uncounted} in a loop of unknown count`, '');
  }
  const roles = nodes.filter(node => node.role);
  for (const node of roles.slice(0, ROUTINE_ROWS)) {
    const role = ROLES[node.role];
    tip.row(role.glyph, role.colour, `\`${node.name}\``, lineLinks(node.accesses.map(access => access.place)));
  }
  if (roles.length > ROUTINE_ROWS) tip.row('…', COLOUR.dim, `${roles.length - ROUTINE_ROWS} more`, '');
  if (hazards) {
    for (const node of nodes) {
      for (const note of node.hazards) tip.row(note.glyph || '⚠', COLOUR.nested, markdown(note.text), note.place ? lineLink(note.place) : '');
    }
  }
  for (const bytes of location.shared) {
    const at = location.address === null ? '' : `+${hex2(bytes.first - page.base)} `;
    // Two locations on this page that take one byte collide unless the source fixes both or the
    // linked config places both so.
    const here = bytes.kind === 'deliberate' || bytes.kind === 'authored' || bytes.kind === 'collision';
    const where = here ? `on this page${bytes.kind === 'authored' ? ' · by config' : ''}` : markdown(pageName(result, bytes.page));
    tip.row('⧉', bytes.kind === 'collision' || !here ? COLOUR.nested : COLOUR.dim,
      here ? `${at}= \`${bytes.there}\` ${where}` : `${at}= ${where} \`${bytes.there}\``,
      coloured(COLOUR.dim, addresses(bytes.first, bytes.last)));
  }
  return tip.build();
}

// Builds the tooltip of a routine's row in a location's call tree.
function routineTip(location, node, hazards) {
  const role = node.role ? ROLES[node.role] : null;
  if (node.unknown) {
    const tip = new Tip(node.name, `${location.name} · D unknown`);
    tip.row('?', COLOUR.dim, 'see `D = ?`', lineLinks(node.accesses.map(access => access.place)));
    if (hazards) {
      for (const note of node.hazards) tip.row(note.glyph || '⚠', COLOUR.nested, markdown(note.text), note.place ? lineLink(note.place) : '');
    }
    return tip.build();
  }
  const tip = new Tip(node.name, `${location.name}${role ? ` · ${role.word}` : ''}`);
  if (node.handler) tip.row('⚡', COLOUR.irq, 'interrupt handler', '');
  else if (node.interrupt) tip.row('⚡', COLOUR.irq, node.main ? 'runs in an interrupt too' : 'runs in an interrupt', '');
  if (role) {
    const writes = node.accesses.filter(access => access.writes).map(access => access.place);
    const reads = node.accesses.filter(access => !access.writes).map(access => access.place);
    if (writes.length > 0) tip.row(role.glyph, role.colour, 'writes', lineLinks(writes));
    if (reads.length > 0) tip.row(role.glyph, role.colour, 'reads', lineLinks(reads));
  }
  if (node.via.length > 0) tip.row('↳', COLOUR.dim, node.runs > 1 ? `called · runs ${times(node.runs)} a pass` : 'called', lineLinks(node.via));
  if (node.runsUncounted) tip.row('∞', COLOUR.dim, 'called in a loop of unknown count', '');
  const looped = node.accesses.filter(access => access.times > 1);
  if (looped.length > 0) tip.row('↻', COLOUR.dim, `${count(looped.length, 'instruction')} in counted loops`, lineLinks(looped.map(access => access.place)));
  const open = node.accesses.filter(access => access.uncounted);
  if (open.length > 0) tip.row('∞', COLOUR.dim, `${count(open.length, 'instruction')} in a loop of unknown count`, lineLinks(open.map(access => access.place)));
  for (const child of node.children || []) {
    if (child.via.length > 0) tip.row('↳', COLOUR.dim, `\`${child.name}\``, lineLinks(child.via));
  }
  if (hazards) {
    for (const note of node.hazards) tip.row(note.glyph || '⚠', COLOUR.nested, markdown(note.text), note.place ? lineLink(note.place) : '');
  }
  return tip.build();
}

// Builds the tooltip of a routine's row on the page whose D is not known.
function unknownRoutineTip(group, routine, hazards) {
  const tip = new Tip(routine.name, `D ${REASONS[group.reason] || group.reason}`);
  if (routine.handler) tip.row('⚡', COLOUR.irq, 'interrupt handler', '');
  else if (routine.interrupt) tip.row('⚡', COLOUR.irq, routine.main ? 'runs in an interrupt too' : 'runs in an interrupt', '');
  for (const use of routine.uses) {
    const role = ROLES[use.role];
    tip.row(role ? role.glyph : '?', role ? role.colour : COLOUR.dim, `\`${use.name}\``,
      lineLinks(use.accesses.map(access => access.place)));
    if (hazards) noteRows(tip, use.hazards || []);
  }
  return tip.build();
}

// Builds the tooltip of a location's row under a routine on the page whose D is not known.
function useTip(result, routine, use, hazards) {
  const at = use.offset === null ? '' : ` · +${hex2(use.offset)}`;
  const tip = new Tip(routine.name, `${use.name}${at}`);
  const role = ROLES[use.role];
  tip.row(role ? role.glyph : '?', role ? role.colour : COLOUR.dim, role ? role.word : '', lineLinks(use.accesses.map(access => access.place)));
  if (use.home) tip.row('●', COLOUR.dim, `home ${markdown(pageName(result, use.home))}`, coloured(COLOUR.dim, use.home));
  if (hazards) noteRows(tip, use.hazards || []);
  return tip.build();
}

// Adds a row for each of a use's notes on the page whose D is not known: that D is the
// interrupted code's, where the access lands on each page that code holds D at, and any hazard.
function noteRows(tip, notes) {
  for (const note of notes) {
    tip.row(note.glyph || '⚠', note.glyph === '◦' ? COLOUR.dim : COLOUR.nested, markdown(note.text), note.place ? lineLink(note.place) : '');
  }
}

// Returns the marks that end a description: ⧉ for an overlap and ⚠ for a hazard.
function marks(overlap, hazard, hazards) {
  return [overlap ? '⧉' : '', hazard && hazards ? '⚠' : ''].filter(Boolean).join(' ');
}

// Returns the parts of a description that are not empty, joined by spaces.
function joined(...parts) {
  return parts.filter(Boolean).join('  ');
}

// Returns the codicon of a routine's row.
function routineIcon(routine) {
  return routine.handler ? icon('zap', COLOUR.irq) : icon('symbol-method', COLOUR.method);
}

// Builds the tree item of an element.
function itemOf(result, element, hazards) {
  const { page } = element;
  const state = expanded => element.children.length === 0
    ? vscode.TreeItemCollapsibleState.None
    : expanded ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed;
  let item;
  switch (element.kind) {
    case 'page': {
      item = new vscode.TreeItem(pageLabel(page), state(!page.hardware));
      item.description = joined(pageKind(page), marks(page.overlaps.length > 0, page.hazard, hazards));
      if (page.base === null) item.iconPath = icon('question', COLOUR.unknown);
      else if (page.hardware) item.iconPath = icon('circuit-board', COLOUR.hw);
      else item.iconPath = icon('window', COLOUR[page.relation]);
      item.tooltip = pageTip(result, page, hazards);
      item.contextValue = page.base === null ? 'unknownPage' : 'page';
      break;
    }
    case 'location': {
      const { location } = element;
      item = new vscode.TreeItem(location.name, state(page.locations.length <= OPEN_LOCATIONS));
      item.description = joined(offsets(location), marks(location.shared.length > 0, location.hazard, hazards));
      item.iconPath = addressTaken(location) ? icon('target', COLOUR.referenced)
        : declaredOnly(location) ? icon('circle-outline', COLOUR.hw)
        : location.relation === 'unused' ? icon('circle-outline', COLOUR.unused)
          : icon('symbol-variable', COLOUR[location.relation]);
      item.tooltip = locationTip(result, page, location, hazards);
      item.contextValue = 'location';
      // The URI is only there so that the caret's decorations can colour the row.
      item.resourceUri = uriOf(keyOf(page, location));
      break;
    }
    case 'routine': {
      const { node, location } = element;
      item = new vscode.TreeItem(node.name, state(true));
      const glyph = node.unknown ? '?' : node.role ? ROLES[node.role].glyph : '';
      item.description = joined(glyph, marks(false, node.hazards.length > 0, hazards));
      item.iconPath = node.unknown && !node.handler ? icon('symbol-method', COLOUR.unknown) : routineIcon(node);
      item.tooltip = routineTip(location, node, hazards);
      item.contextValue = 'routine';
      break;
    }
    case 'group': {
      const { group } = element;
      item = new vscode.TreeItem(REASONS[group.reason] || group.reason, state(true));
      item.description = String(group.routines.length);
      item.iconPath = icon(group.reason === 'interrupted' ? 'zap' : 'folder', group.reason === 'interrupted' ? COLOUR.irq : COLOUR.unknown);
      const n = group.routines.length;
      item.tooltip = new Tip(item.label, `${n} routine${n === 1 ? '' : 's'}`).build();
      item.contextValue = 'group';
      break;
    }
    case 'unknownRoutine': {
      const { routine, group } = element;
      item = new vscode.TreeItem(routine.name, state(true));
      item.description = marks(false, routine.uses.some(use => use.hazard), hazards);
      item.iconPath = routineIcon(routine);
      item.tooltip = unknownRoutineTip(group, routine, hazards);
      item.contextValue = 'routine';
      break;
    }
    default: {
      const { use, routine } = element;
      item = new vscode.TreeItem(use.name, vscode.TreeItemCollapsibleState.None);
      const at = use.offset === null ? '' : `+${hex2(use.offset)}`;
      item.description = joined(at, ROLES[use.role] ? ROLES[use.role].glyph : '', marks(false, use.hazard, hazards));
      item.iconPath = icon('symbol-variable', routine.interrupt ? COLOUR.irq : COLOUR.unknown);
      item.tooltip = useTip(result, routine, use, hazards);
      item.contextValue = 'use';
    }
  }
  item.id = element.id;
  // A row that runs a command when clicked expands only from its arrow, so that clicking a
  // variable selects it without folding it. The command also runs when the row is already
  // selected, which a change of selection would not report.
  item.command = { command: SELECT, title: 'Select', arguments: [element] };
  return item;
}

// Collects the lines an element stands for, as a map from a document's URI to a map from a line
// to what happens there. `first` is the first access, which is the line to show.
class Marks {
  constructor(hazards) {
    this.hazards = hazards;
    this.byUri = new Map();
    // The line the editor is taken to: a variable's declaration, or a routine's first access.
    this.first = null;
    // What the selection is, in a few words, for the line above the tree.
    this.summary = '';
  }

  // Returns the record for a place's line, made when it is first asked for.
  at(place) {
    const uri = vscode.Uri.parse(place.uri).toString();
    if (!this.byUri.has(uri)) this.byUri.set(uri, new Map());
    const lines = this.byUri.get(uri);
    const line = place.range.start.line;
    if (!lines.has(line)) lines.set(line, { write: false, read: false, call: false, irq: false, declaration: false, reference: false, notes: [] });
    return lines.get(line);
  }

  // Adds an instruction that accesses a location.
  access(access, irq) {
    const mark = this.at(access.place);
    if (access.writes) mark.write = true;
    else mark.read = true;
    if (irq) mark.irq = true;
  }

  // Adds the line that declares a variable.
  declaration(place) {
    this.at(place).declaration = true;
  }

  // Adds a line that takes a location's address without reaching it.
  reference(place) {
    this.at(place).reference = true;
  }

  // Adds a call that leads to a routine that accesses a location.
  call(place) {
    this.at(place).call = true;
  }

  // Adds a note that makes a hazard, when hazards are shown.
  note(place, text) {
    if (!this.hazards || !place) return;
    const mark = this.at(place);
    if (!mark.notes.includes(text)) mark.notes.push(text);
  }

  // Adds a routine's accesses, the calls that reach it, and its hazards. With `deep`, the
  // routines it calls are added too. Without it, only the calls it makes down the tree are.
  node(node, deep) {
    for (const access of node.accesses) this.access(access, node.interrupt);
    for (const place of node.via) this.call(place);
    for (const note of node.hazards) this.note(note.place, note.text);
    for (const child of node.children || []) {
      if (deep) this.node(child, true);
      else for (const place of child.via) this.call(place);
    }
  }

  // Adds the accesses of one location reached with D not known.
  use(routine, use) {
    for (const access of use.accesses) {
      this.access(access, routine.interrupt);
      if (use.hazard) this.note(access.place, 'reached through the interrupted D');
    }
    for (const note of use.hazards || []) {
      if (note.glyph !== '◦') this.note(note.place, note.text);
    }
  }

  // Adds everything an element stands for, and says which line to show and what is selected.
  element(element) {
    this.add(element);
    switch (element.kind) {
      case 'page': {
        const page = element.page;
        const first = page.locations[0];
        this.first = (first && first.declaration) || this.earliestMark();
        this.summary = page.base === null
          ? `D = ? · ${count(page.groups.reduce((sum, group) => sum + group.routines.length, 0), 'routine')}`
          : `D = ${hex4(page.base)} · ${count(page.locations.length, 'variable')}`;
        break;
      }
      case 'location': {
        const location = element.location;
        const routines = new Set();
        const walk = nodes => nodes.forEach(node => {
          if (node.role) routines.add(node.name);
          walk(node.children || []);
        });
        walk(location.routines);
        this.first = location.declaration || this.earliestMark();
        this.summary = location.accesses > 0
          ? `${location.name} · ${count(location.accesses, 'access', 'accesses')} in ${count(routines.size, 'routine')}`
          : addressTaken(location)
            ? `${location.name} · address taken · ${count(location.references.length, 'line')}`
            : declaredOnly(location)
              ? `${location.name} · declared, not reached`
              : `${location.name} · never accessed`;
        break;
      }
      case 'routine': {
        // A routine that only calls its way to the variable is shown at those calls.
        const node = element.node;
        const calls = (node.children || []).flatMap(child => child.via);
        this.first = earliest(node.accesses.map(access => access.place)) || earliest(calls) || earliest(node.via);
        this.summary = node.role
          ? `${node.name} → ${element.location.name} · ${count(node.accesses.length, 'access', 'accesses')}`
          : `${node.name} → ${element.location.name} · ${count(calls.length, 'call')} on the way`;
        break;
      }
      case 'group':
        this.first = this.earliestMark();
        this.summary = `${element.group.reason === 'interrupted' ? 'interrupted code' : 'not known'} · ${count(element.group.routines.length, 'routine')}`;
        break;
      case 'unknownRoutine':
        this.first = this.earliestMark();
        this.summary = `${element.routine.name} · D = ?`;
        break;
      default:
        this.first = this.earliestMark();
        this.summary = `${element.routine.name} → ${element.use.name} · D = ?`;
    }
    return this;
  }

  // Returns the earliest line marked so far, in the first file that has one.
  earliestMark() {
    const [entry] = this.byUri;
    if (!entry) return null;
    const [uri, lines] = entry;
    return { uri, range: { start: { line: Math.min(...lines.keys()) } } };
  }

  // Adds the lines an element stands for.
  add(element) {
    switch (element.kind) {
      case 'page':
        for (const child of element.children) this.add(child);
        break;
      case 'location':
        if (element.location.declaration) this.declaration(element.location.declaration);
        for (const place of element.location.references || []) this.reference(place);
        for (const node of element.location.routines) this.node(node, true);
        break;
      case 'routine':
        this.node(element.node, false);
        break;
      case 'group':
      case 'unknownRoutine':
        for (const child of element.children) this.add(child);
        break;
      default:
        this.use(element.routine, element.use);
    }
  }
}

// Returns the first of some places in source order: the lowest line of the first place's file.
function earliest(places) {
  if (places.length === 0) return null;
  const uri = places[0].uri;
  return places.filter(place => place.uri === uri)
    .reduce((best, place) => (place.range.start.line < best.range.start.line ? place : best));
}

// Returns how many times something runs, such as `13×` or `4,096×`.
function times(n) {
  return `${n.toLocaleString('en-US')}×`;
}

// Returns a count with its noun, such as `1 routine` or `4 accesses`.
function count(n, one, many) {
  return `${n} ${n === 1 ? one : many || `${one}s`}`;
}

// The decoration types, made once. A line gets one bar and tint type and at most one tag type.
function decorationTypes() {
  const tints = {
    access: ['nt65.directPages.accessBackground', COLOUR.access],
    warning: ['nt65.directPages.warningBackground', COLOUR.nested],
    interrupt: ['nt65.directPages.interruptBackground', COLOUR.irq],
  };
  const bars = {
    write: { borderWidth: '0 0 0 3px', borderStyle: 'solid' },
    read: { borderWidth: '0 0 0 2px', borderStyle: 'dashed' },
    none: {},
  };
  const types = { lines: {} };
  for (const [tint, [background, colour]] of Object.entries(tints)) {
    for (const [bar, border] of Object.entries(bars)) {
      types.lines[`${bar}:${tint}`] = vscode.window.createTextEditorDecorationType({
        isWholeLine: true,
        backgroundColor: new vscode.ThemeColor(background),
        ...border,
        borderColor: new vscode.ThemeColor(colour),
        overviewRulerColor: new vscode.ThemeColor(colour),
        overviewRulerLane: vscode.OverviewRulerLane.Left,
      });
    }
  }
  const tag = (text, colour) => vscode.window.createTextEditorDecorationType({
    after: { contentText: text, color: new vscode.ThemeColor(colour), margin: '0 0 0 0.6em' },
  });
  types.call = tag('↳', COLOUR.dim);
  types.declaration = vscode.window.createTextEditorDecorationType({
    isWholeLine: true,
    backgroundColor: new vscode.ThemeColor('nt65.directPages.accessBackground'),
    overviewRulerColor: new vscode.ThemeColor(COLOUR.access),
    overviewRulerLane: vscode.OverviewRulerLane.Left,
    after: { contentText: '◆', color: new vscode.ThemeColor(COLOUR.access), margin: '0 0 0 0.6em' },
  });
  types.reference = vscode.window.createTextEditorDecorationType({
    isWholeLine: true,
    backgroundColor: new vscode.ThemeColor('nt65.directPages.accessBackground'),
    overviewRulerColor: new vscode.ThemeColor(COLOUR.referenced),
    overviewRulerLane: vscode.OverviewRulerLane.Left,
    after: { contentText: '◎', color: new vscode.ThemeColor(COLOUR.referenced), margin: '0 0 0 0.6em' },
  });
  types.hazard = tag('⚠', COLOUR.nested);
  return types;
}

// Returns every decoration type in a set of them.
function allTypes(types) {
  return [...Object.values(types.lines), types.call, types.hazard, types.declaration, types.reference];
}

// Draws the lines of the selected row in the editors that show them.
class Highlights {
  constructor() {
    this.types = decorationTypes();
    this.marks = null;
  }

  // Shows some marks, replacing any that were shown.
  set(marks) {
    this.marks = marks;
    this.paint();
  }

  // Removes every mark.
  clear() {
    this.marks = null;
    this.paint();
  }

  // Draws the marks in every visible editor, and removes them from editors that hold none.
  paint() {
    for (const editor of vscode.window.visibleTextEditors) {
      const lines = this.marks && this.marks.byUri.get(editor.document.uri.toString());
      const ranges = new Map(allTypes(this.types).map(type => [type, []]));
      for (const [line, mark] of lines || []) {
        if (line >= editor.document.lineCount) continue;
        const range = new vscode.Range(line, 0, line, 0);
        if (mark.declaration && !mark.write && !mark.read) {
          ranges.get(this.types.declaration).push(range);
          continue;
        }
        if (mark.reference && !mark.write && !mark.read) {
          ranges.get(this.types.reference).push(range);
          continue;
        }
        const bar = mark.write ? 'write' : mark.read ? 'read' : 'none';
        const tint = mark.notes.length > 0 ? 'warning' : mark.irq ? 'interrupt' : 'access';
        ranges.get(this.types.lines[`${bar}:${tint}`]).push(range);
        if (mark.notes.length > 0) {
          const hover = new vscode.MarkdownString(mark.notes.map(note => `⚠ ${markdown(note)}`).join('  \n'));
          ranges.get(this.types.hazard).push({ range, hoverMessage: hover });
        } else if (mark.call) {
          ranges.get(this.types.call).push(range);
        }
      }
      for (const [type, list] of ranges) editor.setDecorations(type, list);
    }
  }

  dispose() {
    for (const type of allTypes(this.types)) type.dispose();
  }
}

// The legend, a small fixed tree of what the colours, glyphs and marks mean.
const LEGEND_ITEMS = [
  {
    label: 'Sharing',
    children: [
      { label: 'shared', icon: icon('circle-filled', COLOUR.shared) },
      { label: 'nested temps', icon: icon('circle-filled', COLOUR.nested) },
      { label: 'interrupt', icon: icon('circle-filled', COLOUR.irq) },
      { label: 'one owner', icon: icon('circle-filled', COLOUR.own) },
      { label: 'unused', icon: icon('circle-outline', COLOUR.unused) },
      { label: 'address taken', icon: icon('target', COLOUR.referenced) },
      { label: 'hardware', icon: icon('circuit-board', COLOUR.hw) },
    ],
  },
  {
    label: 'Role',
    children: [
      { label: '↓', description: 'in, from the caller' },
      { label: '↑', description: 'out, to the caller' },
      { label: '↕', description: 'in · out' },
      { label: '◦', description: 'temp' },
      { label: 'w', description: 'hardware write' },
      { label: 'r', description: 'hardware read' },
    ],
  },
  {
    label: 'Marks',
    children: [
      { label: '⧉', description: 'pages overlap, or bytes shared' },
      { label: '◎', description: 'line that takes the address' },
      { label: '⚠', description: 'hazard' },
      { label: '⚡', description: 'interrupt handler', icon: icon('zap', COLOUR.irq) },
      { label: '┃', description: 'line that writes' },
      { label: '┆', description: 'line that only reads' },
      { label: '↳', description: 'call' },
      { label: '◆', description: 'declaration' },
    ],
  },
];

const legend = {
  getChildren: element => (element ? element.children || [] : LEGEND_ITEMS),
  getTreeItem: element => {
    const item = new vscode.TreeItem(element.label, element.children
      ? vscode.TreeItemCollapsibleState.Expanded
      : vscode.TreeItemCollapsibleState.None);
    item.description = element.description;
    item.iconPath = element.icon;
    return item;
  },
};

// The map, its tree and the grid, and what keeps them up to date.
class DirectPages {
  constructor(context, client) {
    this.client = client;
    this.result = null;
    this.text = 'null';
    this.tree = build(null);
    this.hazards = context.workspaceState.get(HAZARDS, true);
    this.context = context;
    this.changed = new vscode.EventEmitter();
    this.onDidChangeTreeData = this.changed.event;
    this.highlights = new Highlights();
    this.timer = undefined;
    this.asked = 0;
    this.stale = true;
    this.selected = null;
    this.source = vscode.window.activeTextEditor && applies(vscode.window.activeTextEditor.document)
      ? vscode.window.activeTextEditor.document.uri.toString()
      : null;
    this.column = vscode.ViewColumn.One;
    this.view = vscode.window.createTreeView(VIEW, { treeDataProvider: this, showCollapseAll: true });
    this.grid = new Grid(context, {
      select: (page, location) => this.selectLocation(page, location),
      visible: () => this.schedule(),
    });
    this.caret = new Caret(caret => this.grid.caret([...caret.keys], caret.direct));
    vscode.commands.executeCommand('setContext', HAZARDS, this.hazards);
  }

  getChildren(element) {
    return element ? element.children : this.tree.roots;
  }

  getParent(element) {
    return element.parent || undefined;
  }

  getTreeItem(element) {
    return itemOf(this.result, element, this.hazards);
  }

  // Gets a value indicating whether anything that shows the map is visible.
  get wanted() {
    return this.view.visible || this.grid.visible;
  }

  // Asks for the map again once nothing else has asked for a moment, or marks it out of date
  // when nothing shows it, so that it is asked for when something does.
  schedule() {
    clearTimeout(this.timer);
    if (!this.wanted) {
      this.stale = true;
      return;
    }
    this.timer = setTimeout(() => this.ask(), DELAY);
  }

  // Asks the server for the map of the program the current source belongs to.
  async ask() {
    this.stale = false;
    const asked = ++this.asked;
    if (!this.source) this.source = await anySource();
    if (asked !== this.asked) return;
    if (!this.source) {
      this.take(null);
      return;
    }
    let result;
    try {
      result = await this.client.sendRequest('nt65/directPages', { textDocument: { uri: this.source } });
    } catch {
      return;
    }
    if (asked === this.asked) this.take(result || null);
  }

  // Shows a new map. A map the same as the last one changes nothing, so that switching between
  // files of one program does not disturb the tree.
  take(result) {
    vscode.commands.executeCommand('setContext', EMPTY,
      !result ? 'noProgram' : result.pages.length === 0 ? 'noPages' : '');
    const text = JSON.stringify(result);
    if (text === this.text) return;
    this.text = text;
    this.result = result;
    this.tree = build(result);
    this.changed.fire(undefined);

    // The lines of the selected row have moved with the edit, so they are drawn again from the
    // new map, without revealing anything, or removed where the row is gone.
    const again = this.selected && this.tree.byId.get(this.selected);
    if (again) {
      const marks = new Marks(this.hazards).element(again);
      this.highlights.set(marks);
      this.view.message = marks.summary || undefined;
    } else {
      this.selected = null;
      this.highlights.clear();
      this.view.message = undefined;
    }
    this.grid.update(result, this.hazards);
    this.caret.schedule(result);
  }

  // Marks the lines a row stands for, names it above the tree, and shows its line: a variable's
  // declaration, or a routine's first access. The file is opened without focus when no editor
  // shows it, so the view that was clicked keeps the keyboard.
  async select(element) {
    // A routine that names the location while D is not known stands for its row under the
    // `D = ?` page, so selecting it selects that row instead.
    const known = element && element.kind === 'routine' && element.node.unknown && this.unknownRoutine(element.node.declaration);
    if (known) {
      element = known;
      try {
        await this.view.reveal(known, { select: true, focus: false, expand: true });
      } catch {
        // The row is still marked below.
      }
    }
    this.selected = element ? element.id : null;
    if (!element) {
      this.highlights.clear();
      this.view.message = undefined;
      return;
    }
    const marks = new Marks(this.hazards).element(element);
    this.highlights.set(marks);
    this.view.message = marks.summary || undefined;
    let top = element;
    while (top.parent) top = top.parent;
    if (top.page.base !== null) {
      const location = element.kind === 'page' ? null : element.location ? element.location.name : null;
      this.grid.focus(top.page.id, location);
    }
    if (!marks.first) return;
    const uri = vscode.Uri.parse(marks.first.uri);
    let editor = vscode.window.visibleTextEditors.find(shown => shown.document.uri.toString() === uri.toString());
    if (!editor) {
      try {
        editor = await vscode.window.showTextDocument(uri, { preserveFocus: true, preview: true, viewColumn: this.column });
      } catch {
        return;
      }
      this.highlights.paint();
    }
    const line = marks.first.range.start.line;
    editor.revealRange(new vscode.Range(line, 0, line, 0), vscode.TextEditorRevealType.InCenterIfOutsideViewport);
  }

  // Returns the row of a routine under the page whose D is not known, if it has one. Routines are
  // matched by where they are declared, because two routines in different scopes may share a name.
  unknownRoutine(declaration) {
    const key = place => `${place.uri}#${place.range.start.line}:${place.range.start.character}`;
    for (const element of this.tree.byId.values()) {
      if (element.kind === 'unknownRoutine' && key(element.routine.declaration) === key(declaration)) return element;
    }
    return undefined;
  }

  // Selects a location's row from the grid, which does what clicking the row does. A hardware
  // register that the tree leaves out has no row, so its declaration is marked and shown alone.
  async selectLocation(pageId, name) {
    const element = this.tree.byId.get(`p:${pageId}/l:${name}`);
    if (!element) {
      const top = this.tree.byId.get(`p:${pageId}`);
      const location = top && top.page.locations.find(item => item.name === name);
      if (location) await this.select({ kind: 'location', id: `${top.id}/l:${name}`, parent: top, children: [], page: top.page, location });
      return;
    }
    try {
      await this.view.reveal(element, { select: true, focus: false, expand: false });
    } catch {
      // The tree may not be resolved yet; the source is still marked below.
    }
    await this.select(element);
  }

  // Shows or hides hazards, in the tree, its tooltips, the marked lines and the grid.
  setHazards(on) {
    this.hazards = on;
    this.context.workspaceState.update(HAZARDS, on);
    vscode.commands.executeCommand('setContext', HAZARDS, on);
    this.changed.fire(undefined);
    const element = this.selected && this.tree.byId.get(this.selected);
    if (element) this.highlights.set(new Marks(on).element(element));
    this.grid.update(this.result, on);
  }

  // Opens the grid on a page: the one given, the one the selected row is under, or the first.
  showGrid(element) {
    const pages = (this.result ? this.result.pages : []).filter(page => page.base !== null);
    let top = element || (this.selected && this.tree.byId.get(this.selected));
    while (top && top.parent) top = top.parent;
    const page = top && top.page.base !== null ? top.page : pages[0];
    const location = element && element.kind === 'location' ? element.location.name : null;
    this.grid.show(this.result, this.hazards, page ? page.id : null, location);
    if (this.stale || !this.result) this.schedule();
  }

  // Follows the editor: a new nt65 source may be in another program, and is where the column for
  // opening files comes from.
  follow(editor) {
    if (!editor || !applies(editor.document)) return;
    if (editor.viewColumn) this.column = editor.viewColumn;
    const uri = editor.document.uri.toString();
    if (uri === this.source) return;
    this.source = uri;
    this.schedule();
  }

  dispose() {
    clearTimeout(this.timer);
    this.highlights.dispose();
    this.caret.dispose();
    this.view.dispose();
    this.grid.dispose();
    this.changed.dispose();
  }
}

// Returns an nt65 source to ask about when no editor has made one current, as after a restart
// that reopens the grid on its own: one that is open, or else any in the workspace.
async function anySource() {
  const open = vscode.workspace.textDocuments.find(applies);
  if (open) return open.uri.toString();
  const [found] = await vscode.workspace.findFiles('**/*.nt65', '**/node_modules/**', 1);
  return found ? found.toString() : null;
}

// Checks whether a document is an nt65 source the server analyzes.
function applies(document) {
  return document.languageId === 'nt65' && (document.uri.scheme === 'file' || document.uri.scheme === 'untitled');
}

// Registers the views, the grid and their commands. `outputChanged` fires whenever the server
// has published the whole program's analysis after an edit, which is when the map can change.
function register(context, client, outputChanged) {
  const pages = new DirectPages(context, client);
  if (!pages.source) vscode.commands.executeCommand('setContext', EMPTY, 'noProgram');
  if (pages.wanted) pages.schedule();
  context.subscriptions.push(
    pages,
    vscode.window.createTreeView(LEGEND, { treeDataProvider: legend }),
    pages.view.onDidChangeVisibility(() => {
      if (pages.view.visible && pages.stale) pages.schedule();
    }),
    vscode.commands.registerCommand(SELECT, element => pages.select(element)),
    vscode.window.onDidChangeActiveTextEditor(editor => {
      pages.follow(editor);
      pages.caret.schedule(pages.result);
    }),
    vscode.window.onDidChangeTextEditorSelection(event => {
      if (event.textEditor === vscode.window.activeTextEditor && applies(event.textEditor.document)) {
        pages.caret.schedule(pages.result);
      }
    }),
    vscode.window.registerFileDecorationProvider(pages.caret),
    vscode.window.registerWebviewPanelSerializer('nt65.directPageGrid', pages.grid),
    vscode.window.onDidChangeVisibleTextEditors(() => pages.highlights.paint()),
    outputChanged(() => pages.schedule()),
    // A grid VS Code restores after a restart can ask for the map before the server has started,
    // so the map is asked for again once it runs.
    client.onDidChangeState(() => {
      if (client.isRunning()) pages.schedule();
    }),
    vscode.commands.registerCommand('nt65.directPages.refresh', () => {
      pages.text = '';
      pages.schedule();
    }),
    vscode.commands.registerCommand('nt65.directPages.toggleHazards', () => pages.setHazards(!pages.hazards)),
    vscode.commands.registerCommand('nt65.directPages.hideHazards', () => pages.setHazards(false)),
    vscode.commands.registerCommand('nt65.directPages.showHazards', () => pages.setHazards(true)),
    vscode.commands.registerCommand('nt65.showDirectPageGrid', element => pages.showGrid(element)));
  return pages;
}

module.exports = { register, build, itemOf, Marks, hex4, hex2, addresses, offsets, pageLabel, pageKind };
