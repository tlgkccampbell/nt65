// Draws the grid panel of the direct page map inside its webview. The extension posts the map,
// whether hazards are shown, and the page and location to show; this script posts back the tab
// chosen and the location clicked. Everything is built as elements, and styles are set through
// the DOM, because the panel's content policy allows no inline styles.
(function () {
  const vscode = acquireVsCodeApi();
  const saved = vscode.getState() || {};
  let map = null;
  let hazards = true;
  let pageId = saved.page || null;
  let hot = null;

  // What the caret marks: the keys of the locations its routine reaches, and the key of the
  // location under it.
  let marked = { keys: [], direct: null };

  const tabs = document.getElementById('tabs');
  const body = document.getElementById('body');
  const tip = document.getElementById('tip');

  const RELATIONS = {
    shared: 'shared', nested: 'nested temps', irq: 'interrupt', own: 'one owner', unused: 'unused', hw: 'hardware',
  };

  // Returns an address as `$` and four hexadecimal digits.
  const hex4 = value => `$${(value & 0xFFFF).toString(16).toUpperCase().padStart(4, '0')}`;

  // Returns a byte offset as `$` and two hexadecimal digits.
  const hex2 = value => `$${value.toString(16).toUpperCase().padStart(2, '0')}`;

  // Returns a range of absolute addresses, written `$0080–87`, or one address alone.
  const addresses = (first, last) => first === last
    ? hex4(first)
    : `${hex4(first)}–${(last & 0xFFFF).toString(16).toUpperCase().padStart(2, '0')}`;

  // Returns a range of offsets in a page, written `+$00–01`, or one offset alone.
  const offsets = (first, last) => first === last
    ? `+${hex2(first)}`
    : `+${hex2(first)}–${last.toString(16).toUpperCase().padStart(2, '0')}`;

  // Returns the bytes a location takes, at least one.
  const sizeOf = location => Math.max(1, location.size || 1);

  // Checks whether no instruction reaches a location but the program takes its address, so that
  // it is used through a pointer or an index the map cannot follow.
  const taken = location => location.relation === 'unused' && (location.references || []).length > 0;

  // Checks whether no instruction reaches a location and nothing takes its address.
  const never = location => location.relation === 'unused' && !taken(location);

  // Returns the colour of a location's relation, with address-taken locations in their own.
  const colourOf = location => (taken(location) ? 'var(--referenced)'
    : `var(--${location.relation in RELATIONS ? location.relation : 'own'})`);

  // Returns how hot a location is: how many times its instructions run in a pass, counting the
  // loops whose counts are known. It is on a logarithmic scale, so that a location used once
  // still shows beside one used thousands of times.
  const heatOf = location => Math.log2(1 + location.perPass);

  // Returns a new element with a class and text.
  function h(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }

  // Returns a phrase as elements, with the parts in backticks set as code.
  function phrase(text) {
    const span = h('span');
    String(text).split(/(`[^`]*`)/).forEach((part, index) => {
      if (index % 2 === 1) span.append(h('code', '', part.slice(1, -1)));
      else if (part) span.append(document.createTextNode(part));
    });
    return span;
  }

  // Returns the name a page goes by: its segments, or its id.
  function pageName(page) {
    return page.segments.length > 0 ? page.segments.join(', ') : page.id;
  }

  // Returns the words that say where a page's layout comes from, from the strongest source among
  // its locations whose addresses the source does not fix, with a count of any weaker ones; or
  // null when the source fixes every address.
  function layoutBadge(page) {
    const counts = { built: 0, configured: 0, guessed: 0 };
    for (const location of page.locations) {
      if (location.layout in counts) counts[location.layout]++;
    }
    const sources = Object.keys(counts).filter(source => counts[source] > 0);
    if (sources.length === 0) return null;
    const words = { built: 'layout from last build', configured: 'layout predicted from config', guessed: 'layout guessed' };
    const weaker = { configured: 'predicted', guessed: 'guessed' };
    const parts = [words[sources[0]], ...sources.slice(1).map(source => `${counts[source]} ${weaker[source]}`)];
    // A build the sources have moved on from may no longer say where the data is.
    if (counts.built > 0 && map.build && map.build.stale) parts.push('sources changed since');
    return parts.join(' · ');
  }

  // Returns the location of a page that holds an absolute address, if any.
  function locationAt(page, address) {
    return page.locations.find(location => location.address !== null
      && address >= location.address && address < location.address + sizeOf(location)) || null;
  }

  // Works out what each of a page's 256 bytes holds: its own locations, if any, with the first
  // of them apart; how two of them share it, if they do; and the other page that covers it with
  // that page's location there, if any. Two own locations on one byte are `collision` unless the
  // source fixes both, which makes them `deliberate`.
  function model(page) {
    const own = new Array(256).fill(null);
    const owners = Array.from({ length: 256 }, () => []);
    const same = new Array(256).fill(null);
    for (const location of page.locations) {
      if (location.offset === null) continue;
      for (let i = 0; i < sizeOf(location); i++) {
        const at = location.offset + i;
        if (at >= 256) continue;
        if (!own[at]) own[at] = location;
        owners[at].push(location);
      }
      for (const bytes of location.shared || []) {
        if (bytes.kind !== 'collision' && bytes.kind !== 'deliberate') continue;
        for (let address = bytes.first; address <= bytes.last; address++) {
          const at = address - page.base;
          if (at >= 0 && at < 256 && same[at] !== 'collision') same[at] = bytes.kind;
        }
      }
    }
    const other = new Array(256).fill(null);
    for (const overlap of page.overlaps) {
      const there = map.pages.find(item => item.id === overlap.page);
      if (!there) continue;
      for (let address = overlap.first; address <= overlap.last; address++) {
        const at = address - page.base;
        if (at < 0 || at >= 256) continue;
        const location = locationAt(there, address);
        if (!other[at] || (!other[at].location && location)) other[at] = { page: there, location };
      }
    }
    return { own, owners, same, other };
  }

  // Shows a tooltip beside an element: a header with a name and a few words, then rows of a
  // coloured glyph and a phrase.
  function showTip(element, name, meta, rows) {
    tip.replaceChildren();
    const head = h('div', 'hd');
    head.append(h('span', '', name), h('span', 'm', meta));
    tip.append(head);
    for (const [glyph, colour, text] of rows) {
      const row = h('div', 'r');
      const key = h('span', 'k', glyph);
      key.style.color = colour;
      row.append(key, phrase(text));
      tip.append(row);
    }
    tip.hidden = false;
    const box = element.getBoundingClientRect();
    const width = tip.offsetWidth;
    let x = box.right + 8;
    let y = box.top;
    if (x + width > window.innerWidth - 8) {
      x = Math.max(8, Math.min(box.left, window.innerWidth - width - 8));
      y = box.bottom + 4;
    }
    tip.style.left = `${x}px`;
    tip.style.top = `${y}px`;
  }

  function hideTip() {
    tip.hidden = true;
  }

  // Outlines one location's cells and its entry in the list, from a hover on either.
  function peek(key) {
    for (const element of document.querySelectorAll('[data-key]')) {
      element.classList.toggle('peek', !!key && element.dataset.key === key);
    }
  }

  // Asks the extension to select a location in the tree and open its declaration.
  function select(page, location) {
    hot = page === pageId ? location : hot;
    vscode.postMessage({ type: 'select', page, location });
  }

  function render() {
    hideTip();
    tabs.replaceChildren();
    body.replaceChildren();
    const pages = map ? map.pages.filter(page => page.base !== null) : [];
    if (pages.length === 0) {
      body.append(h('p', 'empty', map ? 'This program reaches no memory through the direct page.' : 'No nt65 program to show.'));
      return;
    }
    let page = pages.find(item => item.id === pageId);
    if (!page) {
      page = pages[0];
      pageId = page.id;
    }
    vscode.setState({ page: pageId });
    for (const item of pages) {
      const tab = h('button', 'tab');
      tab.setAttribute('role', 'tab');
      tab.setAttribute('aria-selected', String(item === page));
      tab.title = item.segments.join(', ') || (item.hardware ? 'hardware' : '');
      tab.append(h('span', item.hardware ? 'ic hw' : 'ic'), document.createTextNode(`D = ${hex4(item.base)}`));
      tab.addEventListener('click', () => {
        pageId = item.id;
        hot = null;
        vscode.postMessage({ type: 'tab', page: item.id });
        render();
      });
      tabs.append(tab);
    }
    drawPage(page);
    mark();
  }

  function drawPage(page) {
    const { own, owners, same, other } = model(page);
    const ownLocations = page.locations.filter(location => location.offset !== null);
    const maxHeat = Math.max(1, ...ownLocations.map(heatOf));

    // Counts the page's bytes.
    let used = 0;
    let unreached = 0;
    let referenced = 0;
    let shared = 0;
    let foreign = 0;
    let run = 0;
    let best = [0, 0];
    let start = 0;
    for (let i = 0; i < 256; i++) {
      const theirs = other[i] && other[i].location;
      if (own[i]) {
        used++;
        if (owners[i].every(never)) unreached++;
        else if (owners[i].every(location => location.relation === 'unused')) referenced++;
        if (theirs) shared++;
      } else if (theirs) {
        foreign++;
      }
      if (!own[i] && !theirs) {
        if (run === 0) start = i;
        run++;
        if (run > best[0]) best = [run, start];
      } else {
        run = 0;
      }
    }
    const free = 256 - used - foreign;

    // The header.
    const head = h('div', 'head');
    head.append(h('span', 't', `D = ${hex4(page.base)}`));
    if (page.segments.length > 0 || page.hardware) head.append(h('span', 'm', page.segments.join(', ') || 'hardware'));
    head.append(h('span', '', `${used} / 256 used`), h('span', 'm', `${free} free · largest run ${best[0]}`));
    const badge = layoutBadge(page);
    if (badge) {
      const span = h('span', 'p', badge);
      if (map.build) span.title = `${map.build.path}\n${new Date(map.build.at).toLocaleString()}`;
      head.append(span);
    }
    body.append(head);

    // The grid, with its legend under it.
    const left = h('div');
    const wrap = h('div', 'gridwrap');
    const grid = h('div', 'grid');
    grid.append(h('span'));
    for (let c = 0; c < 16; c++) grid.append(h('span', 'colh', c.toString(16).toUpperCase()));

    // Returns the block a byte belongs to, and the colour of that block's outline.
    const blockOf = at => {
      if (at < 0 || at >= 256) return { key: 'out' };
      const mine = own[at];
      const theirs = other[at];
      if (mine && theirs && theirs.location) return { key: `o:${mine.name}`, edge: 'var(--edge-strong)' };
      if (mine) return { key: `o:${mine.name}`, edge: never(mine) ? 'var(--edge-unused)' : 'var(--edge-sym)' };
      if (theirs && theirs.location) return { key: `u:${theirs.page.id}:${theirs.location.name}`, edge: 'var(--edge-foreign)' };
      if (theirs) return { key: `p:${theirs.page.id}`, edge: 'var(--edge-page)' };
      return { key: `f:${at}`, edge: 'transparent' };
    };

    // Returns the shadows that cut a cell's outer sides away from its neighbours and draw its
    // block's edge there, `width` pixels wide. The cuts go first and the edges after, so each cut
    // covers all but the inner pixels of its edge.
    const sides = (at, column, width) => {
      const me = blockOf(at).key;
      const differs = (there, wraps) => wraps || blockOf(there).key !== me;
      const cuts = [];
      const edges = [];
      const cut = (x, y, x2, y2) => {
        cuts.push(`inset ${x}px ${y}px 0 var(--cut)`);
        edges.push(`inset ${x2}px ${y2}px 0 var(--edge)`);
      };
      const edge = 2 + width;
      if (differs(at - 1, column === 0)) cut(2, 0, edge, 0);
      if (differs(at + 1, column === 15)) cut(-2, 0, -edge, 0);
      if (differs(at - 16, false)) cut(0, 2, 0, edge);
      if (differs(at + 16, false)) cut(0, -2, 0, -edge);
      return cuts.concat(edges).join(', ');
    };

    const keyOf = (pageOf, location) => `${pageOf.id}/${location.name}`;
    for (let r = 0; r < 16; r++) {
      grid.append(h('span', 'rowh', hex4(page.base + r * 16)));
      for (let c = 0; c < 16; c++) {
        const at = r * 16 + c;
        const address = page.base + at;
        const mine = own[at];
        const theirs = other[at];
        const cell = h('span', 'cell');
        cell.style.setProperty('--edge', blockOf(at).edge);
        cell.style.boxShadow = sides(at, c, 1);
        // The locations the caret marks get wider edges, the one under it the widest.
        cell.edges = { plain: cell.style.boxShadow, used: sides(at, c, 2), direct: sides(at, c, 3) };
        if (mine && mine.relation !== 'unused') {
          const share = Math.round(30 + 70 * heatOf(mine) / maxHeat);
          cell.style.background = `color-mix(in srgb, ${colourOf(mine)} ${share}%, transparent)`;
        }
        if (mine && never(mine)) cell.classList.add('unused');
        else if (mine && taken(mine)) cell.classList.add('taken');
        if (same[at] === 'collision') cell.classList.add('twin');
        else if (same[at] === 'deliberate') cell.classList.add('alias');
        if (mine && theirs && theirs.location) cell.classList.add('clash');
        else if (!mine && theirs && theirs.location) cell.classList.add('otherused');
        else if (!mine && theirs) cell.classList.add('other');
        const target = mine ? { page, location: mine } : theirs && theirs.location ? { page: theirs.page, location: theirs.location } : null;
        if (target) {
          cell.classList.add('sym');
          cell.dataset.key = keyOf(target.page, target.location);
          if (target.page === page && hot === target.location.name) cell.classList.add('hot');
          cell.addEventListener('click', () => select(target.page.id, target.location.name));
        }
        const rows = [];
        // Every own location on the byte is listed, the first with how often it is used and the
        // others marked as taking the same byte.
        owners[at].forEach((location, index) => {
          const into = address - location.address;
          const name = `\`${location.name}\`${into > 0 ? ` +${into}` : ''}`;
          if (index === 0) {
            rows.push([glyphOf(location), never(location) ? 'var(--dim)' : colourOf(location), name]);
            rows.push(['#', 'var(--dim)', heatText(location)]);
          } else {
            rows.push(['=', same[at] === 'collision' ? 'var(--nested)' : 'var(--dim)', name]);
          }
        });
        if (theirs && theirs.location) {
          const into = address - theirs.location.address;
          rows.push(['⧉', mine ? 'var(--nested)' : 'var(--dim)', `${pageName(theirs.page)} \`${theirs.location.name}\`${into > 0 ? ` +${into}` : ''}`]);
        } else if (theirs) {
          rows.push(['⧉', 'var(--dim)', `${pageName(theirs.page)}'s page, unused here`]);
        }
        if (!mine && !(theirs && theirs.location)) rows.push(['·', 'var(--dim)', 'free']);
        cell.addEventListener('mouseenter', () => {
          showTip(cell, hex4(address), `+${hex2(at)}`, rows);
          peek(cell.dataset.key);
        });
        cell.addEventListener('mouseleave', () => {
          hideTip();
          peek(null);
        });
        grid.append(cell);
      }
    }
    wrap.append(grid);
    left.append(wrap, legend());
    body.append(left);

    // The side column: the locations, the budget and the checks.
    const side = h('div', 'side');
    side.append(symbols(page, maxHeat), budget(used, unreached, referenced, shared, foreign, free, best, page), checks(page));
    body.append(side);
  }

  // Returns the glyph that stands for a location's relation.
  function glyphOf(location) {
    if (taken(location)) return '◎';
    return location.relation === 'unused' ? '○' : '●';
  }

  // Returns how often a location is used, in words, or how many lines take its address.
  function heatText(location) {
    if (taken(location)) {
      const n = location.references.length;
      return `address taken on ${n} line${n === 1 ? '' : 's'}`;
    }
    const instructions = `${location.accesses} instruction${location.accesses === 1 ? '' : 's'}`;
    const open = location.uncounted > 0 ? ` · ${location.uncounted} in a loop of unknown count` : '';
    return `${instructions} · ${location.perPass.toLocaleString('en-US')}× a pass${open}`;
  }

  // Returns the list of the page's locations, and the other pages' locations that lie in it.
  function symbols(page, maxHeat) {
    const listed = page.locations.map(location => ({
      page, location, foreign: false,
      first: location.offset, last: location.offset === null ? null : location.offset + sizeOf(location) - 1,
    }));
    for (const overlap of page.overlaps) {
      const there = map.pages.find(item => item.id === overlap.page);
      if (!there) continue;
      for (const location of there.locations) {
        if (location.address === null) continue;
        const first = Math.max(location.address, page.base) - page.base;
        const last = Math.min(location.address + sizeOf(location), page.base + 256) - page.base - 1;
        if (last >= first) listed.push({ page: there, location, foreign: true, first, last });
      }
    }
    listed.sort((a, b) => (a.first === null) - (b.first === null) || (a.first || 0) - (b.first || 0));

    const section = h('div');
    section.append(h('h3', '', 'Locations'));
    const list = h('div', 'syms');

    // The bars are explained right under the heading, where they are read, rather than in a
    // legend or a hover.
    section.append(
      h('p', 'caption', 'Bar represents relative usage (log scale).'),
      h('p', 'caption', 'Dashes indicate an uncountable loop.'));
    for (const entry of listed) {
      const { location } = entry;
      const row = h('div', `s${entry.foreign ? ' foreign' : ''}${!entry.foreign && hot === location.name ? ' sel' : ''}`);
      row.tabIndex = 0;
      row.dataset.key = `${entry.page.id}/${location.name}`;
      const swatch = h('span', 'sw');
      if (entry.foreign) swatch.classList.add('foreign');
      else if (never(location)) swatch.classList.add('never');
      else swatch.style.background = colourOf(location);
      const name = h('span', 'nm', location.name);
      if (entry.foreign) name.append(h('span', 'rg', ` ${pageName(entry.page)}`));
      const range = entry.first === null ? '?' : offsets(entry.first, entry.last);
      const heat = h('span', 'ht');
      if (!entry.foreign) {
        const bar = h('span');
        bar.style.width = `${Math.round(heatOf(location) / maxHeat * 100)}%`;
        heat.append(bar);
        if (location.uncounted > 0) heat.classList.add('open');
      }
      row.append(swatch, name, h('span', 'rg', range), heat);
      const meta = entry.foreign
        ? `${pageName(entry.page)} · ${range}`
        : `${range} · ${sizeOf(location)} B${location.type ? ` · ${location.type}` : ''}`;
      const rows = entry.foreign
        ? [['⧉', 'var(--dim)', 'another page\'s bytes']]
        : [[glyphOf(location), 'var(--dim)', taken(location) ? 'address taken' : RELATIONS[location.relation] || location.relation],
          ['#', 'var(--dim)', heatText(location)],
          ...(location.shared || []).filter(bytes => bytes.kind === 'collision' || bytes.kind === 'deliberate')
            .map(bytes => ['=', bytes.kind === 'collision' ? 'var(--nested)' : 'var(--dim)',
              `\`${bytes.there}\` ${offsets(bytes.first - page.base, bytes.last - page.base)}`])];
      row.addEventListener('mouseenter', () => {
        peek(row.dataset.key);
        showTip(row, location.name, meta, rows);
      });
      row.addEventListener('mouseleave', () => {
        peek(null);
        hideTip();
      });
      row.addEventListener('click', () => select(entry.page.id, location.name));
      row.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          select(entry.page.id, location.name);
        }
      });
      list.append(row);
    }
    section.append(list);
    return section;
  }

  // Returns the budget: a stacked meter and what each part of the page's 256 bytes is. A byte
  // whose address the program takes is counted apart from one nothing uses, because it is used
  // through a pointer or an index.
  function budget(used, unreached, referenced, shared, foreign, free, best, page) {
    const section = h('div');
    section.append(h('h3', '', 'Budget'));
    const meter = h('div', 'meter');
    const parts = [[used - unreached - referenced - shared, 'own'], [referenced, 'taken'], [unreached, 'never'], [shared, 'shared'], [foreign, 'foreign']];
    for (const [count, className] of parts) {
      if (count <= 0) continue;
      const part = h('span', className);
      part.style.width = `${count / 256 * 100}%`;
      meter.append(part);
    }
    const values = h('div', 'kv');
    const pairs = [
      ['used', used],
      ['address taken', referenced],
      ['never accessed', unreached],
      ['shared with another page', shared],
      ['another page\'s only', foreign],
      ['free', free],
      ['largest free run', best[0] > 0 ? `${best[0]} · ${hex4(page.base + best[1])}` : '0'],
    ];
    for (const [key, value] of pairs) values.append(h('span', 'k', key), h('span', 'v', String(value)));
    section.append(meter, values);
    return section;
  }

  // Returns the checks, which say whether D is page-aligned, which pages overlap this one, what is
  // known about its layout and, when hazards are shown, which locations have one.
  function checks(page) {
    const items = [];
    // Only the 65816 has a D register to align; every other processor's one page is the zero page.
    if (map.cpu === '65816') {
      if ((page.base & 0xFF) === 0) {
        items.push(['✓', 'var(--shared)', 'D page-aligned', '']);
      } else {
        items.push(['⚠', 'var(--nested)', `D not page-aligned · +1 cycle on ${page.direct} instruction${page.direct === 1 ? '' : 's'}`, '']);
      }
    }
    for (const overlap of page.overlaps) {
      const there = map.pages.find(item => item.id === overlap.page);
      const name = there ? pageName(there) : overlap.page;
      if (overlap.shared.length > 0) {
        const count = overlap.shared.reduce((sum, bytes) => sum + bytes.last - bytes.first + 1, 0);
        const first = Math.min(...overlap.shared.map(bytes => bytes.first));
        const last = Math.max(...overlap.shared.map(bytes => bytes.last));
        items.push(['⧉', 'var(--nested)', `${count} byte${count === 1 ? '' : 's'} shared with ${name}`, addresses(first, last)]);
      } else {
        items.push(['⧉', 'var(--dim)', `${name} overlaps, no bytes shared`, addresses(overlap.first, overlap.last)]);
      }
    }
    // The header's badge already says the layout is guessed.
    for (const note of page.notes) {
      if (!note.text.endsWith('layout guessed')) items.push([note.glyph, note.glyph === '⧉' ? 'var(--nested)' : 'var(--dim)', note.text, '']);
    }
    if (hazards) {
      for (const location of page.locations) {
        if (location.hazard) items.push(['⚠', 'var(--nested)', `\`${location.name}\` ${RELATIONS[location.relation] || ''}`, '']);
      }
    }
    const section = h('div');
    section.append(h('h3', '', 'Checks'));
    const list = h('div', 'lst');
    for (const [glyph, colour, text, where] of items) {
      const item = h('div', 'it');
      const key = h('span', 'k', glyph);
      key.style.color = colour;
      item.append(key, phrase(text), h('span', 'ref', where));
      list.append(item);
    }
    section.append(list);
    return section;
  }

  // Returns the strip under the grid that says what its colours and shapes mean.
  function legend() {
    const strip = h('div', 'legend');
    const key = (text, setup) => {
      const span = h('span');
      const swatch = h('i');
      setup(swatch);
      span.append(swatch, document.createTextNode(text));
      strip.append(span);
    };
    for (const relation of ['shared', 'nested', 'irq', 'own']) {
      key(RELATIONS[relation], swatch => { swatch.style.background = `var(--${relation})`; });
    }
    key('address taken', swatch => { swatch.style.background = 'var(--referenced)'; });
    key('never accessed', swatch => swatch.classList.add('neverkey'));
    key('free', swatch => { swatch.style.background = 'var(--free)'; });
    key('another page', swatch => swatch.classList.add('cell', 'other'));
    key('both', swatch => {
      swatch.classList.add('clashkey');
      swatch.textContent = '⧉';
    });
    key('collide', swatch => {
      swatch.classList.add('twinkey');
      swatch.textContent = '=';
    });
    key('alias', swatch => {
      swatch.classList.add('aliaskey');
      swatch.textContent = '=';
    });
    key('colder → hotter', swatch => swatch.classList.add('heat'));
    key('one location', swatch => swatch.classList.add('shape'));
    return strip;
  }

  // Marks the cells and list entries of the locations the caret marks.
  function mark() {
    const keys = new Set(marked.keys);
    // While the caret marks anything, the cells it does not mark fade.
    document.body.classList.toggle('caret-on', keys.size > 0 || !!marked.direct);
    for (const element of document.querySelectorAll('[data-key]')) {
      const key = element.dataset.key;
      const direct = key === marked.direct;
      const used = keys.has(key) && !direct;
      element.classList.toggle('caret', used);
      element.classList.toggle('caret-direct', direct);
      if (element.edges) element.style.boxShadow = direct ? element.edges.direct : used ? element.edges.used : element.edges.plain;
    }
  }

  window.addEventListener('message', event => {
    const message = event.data;
    if (message && message.type === 'caret') {
      const moved = message.direct && message.direct !== marked.direct;
      marked = { keys: message.keys || [], direct: message.direct || null };
      // The location under the caret is shown even when it is on another page.
      const page = moved ? message.direct.slice(0, message.direct.lastIndexOf('/')) : null;
      if (map && page && page !== pageId && map.pages.some(item => item.id === page)) {
        pageId = page;
        render();
      } else {
        mark();
      }
      return;
    }
    if (!message || message.type !== 'map') return;
    map = message.result;
    hazards = message.hazards !== false;
    if (message.page) pageId = message.page;
    hot = message.location || null;
    render();
  });
  vscode.postMessage({ type: 'ready' });
}());
