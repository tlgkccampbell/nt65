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

  // Returns how hot a location is: its accesses, with those in loops counted three times.
  const heatOf = location => location.accesses + 2 * location.loops;

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

  // Returns the location of a page that holds an absolute address, if any.
  function locationAt(page, address) {
    return page.locations.find(location => location.address !== null
      && address >= location.address && address < location.address + sizeOf(location)) || null;
  }

  // Works out what each of a page's 256 bytes holds: its own location, if any, and the other page
  // that covers it with that page's location there, if any.
  function model(page) {
    const own = new Array(256).fill(null);
    for (const location of page.locations) {
      if (location.offset === null) continue;
      for (let i = 0; i < sizeOf(location); i++) {
        const at = location.offset + i;
        if (at < 256 && !own[at]) own[at] = location;
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
    return { own, other };
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
  }

  function drawPage(page) {
    const { own, other } = model(page);
    const ownLocations = page.locations.filter(location => location.offset !== null);
    const maxHeat = Math.max(1, ...ownLocations.map(heatOf));

    // Counts the page's bytes.
    let used = 0;
    let never = 0;
    let shared = 0;
    let foreign = 0;
    let run = 0;
    let best = [0, 0];
    let start = 0;
    for (let i = 0; i < 256; i++) {
      const theirs = other[i] && other[i].location;
      if (own[i]) {
        used++;
        if (own[i].relation === 'unused') never++;
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
    if (map.predicted && page.locations.some(location => !location.fixed)) head.append(h('span', 'p', 'predicted layout'));
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
      if (mine) return { key: `o:${mine.name}`, edge: mine.relation === 'unused' ? 'var(--edge-unused)' : 'var(--edge-sym)' };
      if (theirs && theirs.location) return { key: `u:${theirs.page.id}:${theirs.location.name}`, edge: 'var(--edge-foreign)' };
      if (theirs) return { key: `p:${theirs.page.id}`, edge: 'var(--edge-page)' };
      return { key: `f:${at}`, edge: 'transparent' };
    };

    // Returns the shadows that cut a cell's outer sides away from its neighbours and draw its
    // block's edge there. The cuts go first and the edges after, so each cut covers all but the
    // inner pixel of its edge.
    const sides = (at, column) => {
      const me = blockOf(at).key;
      const differs = (there, wraps) => wraps || blockOf(there).key !== me;
      const cuts = [];
      const edges = [];
      const cut = (x, y, x2, y2) => {
        cuts.push(`inset ${x}px ${y}px 0 var(--cut)`);
        edges.push(`inset ${x2}px ${y2}px 0 var(--edge)`);
      };
      if (differs(at - 1, column === 0)) cut(2, 0, 3, 0);
      if (differs(at + 1, column === 15)) cut(-2, 0, -3, 0);
      if (differs(at - 16, false)) cut(0, 2, 0, 3);
      if (differs(at + 16, false)) cut(0, -2, 0, -3);
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
        cell.style.boxShadow = sides(at, c);
        if (mine && mine.relation !== 'unused') {
          const share = Math.round(30 + 70 * heatOf(mine) / maxHeat);
          cell.style.background = `color-mix(in srgb, var(--${mine.relation in RELATIONS ? mine.relation : 'own'}) ${share}%, transparent)`;
        }
        if (mine && mine.relation === 'unused') cell.classList.add('unused');
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
        if (mine) {
          const colour = mine.relation === 'unused' ? 'var(--dim)' : `var(--${mine.relation in RELATIONS ? mine.relation : 'own'})`;
          const into = address - mine.address;
          rows.push([mine.relation === 'unused' ? '○' : '●', colour, `\`${mine.name}\`${into > 0 ? ` +${into}` : ''}`]);
          rows.push(['#', 'var(--dim)', heatText(mine)]);
        }
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
    side.append(symbols(page, maxHeat), budget(used, never, shared, foreign, free, best, page), checks(page));
    body.append(side);
  }

  // Returns how often a location is accessed, in words.
  function heatText(location) {
    const loops = location.loops > 0 ? ` · ${location.loops} in loops` : '';
    return `${location.accesses} access${location.accesses === 1 ? '' : 'es'}${loops}`;
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
    for (const entry of listed) {
      const { location } = entry;
      const row = h('div', `s${entry.foreign ? ' foreign' : ''}${!entry.foreign && hot === location.name ? ' sel' : ''}`);
      row.tabIndex = 0;
      row.dataset.key = `${entry.page.id}/${location.name}`;
      const swatch = h('span', 'sw');
      if (entry.foreign) swatch.classList.add('foreign');
      else if (location.relation === 'unused') swatch.classList.add('never');
      else swatch.style.background = `var(--${location.relation in RELATIONS ? location.relation : 'own'})`;
      const name = h('span', 'nm', location.name);
      if (entry.foreign) name.append(h('span', 'rg', ` ${pageName(entry.page)}`));
      const range = entry.first === null ? '?' : offsets(entry.first, entry.last);
      const heat = h('span', 'ht');
      if (!entry.foreign) {
        const bar = h('span');
        bar.style.width = `${Math.round(heatOf(location) / maxHeat * 100)}%`;
        heat.append(bar);
      }
      row.append(swatch, name, h('span', 'rg', range), heat);
      const meta = entry.foreign
        ? `${pageName(entry.page)} · ${range}`
        : `${range} · ${sizeOf(location)} B${location.type ? ` · ${location.type}` : ''}`;
      const rows = entry.foreign
        ? [['⧉', 'var(--dim)', 'another page\'s bytes']]
        : [[location.relation === 'unused' ? '○' : '●', 'var(--dim)', RELATIONS[location.relation] || location.relation],
          ['#', 'var(--dim)', heatText(location)]];
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

  // Returns the budget: a stacked meter and what each part of the page's 256 bytes is.
  function budget(used, never, shared, foreign, free, best, page) {
    const section = h('div');
    section.append(h('h3', '', 'Budget'));
    const meter = h('div', 'meter');
    for (const [count, className] of [[used - never - shared, 'own'], [never, 'never'], [shared, 'shared'], [foreign, 'foreign']]) {
      if (count <= 0) continue;
      const part = h('span', className);
      part.style.width = `${count / 256 * 100}%`;
      meter.append(part);
    }
    const values = h('div', 'kv');
    const pairs = [
      ['used', used],
      ['never accessed', never],
      ['shared with another page', shared],
      ['another page\'s only', foreign],
      ['free', free],
      ['largest free run', best[0] > 0 ? `${best[0]} · ${hex4(page.base + best[1])}` : '0'],
    ];
    for (const [key, value] of pairs) values.append(h('span', 'k', key), h('span', 'v', String(value)));
    section.append(meter, values);
    return section;
  }

  // Returns the checks: whether D is page-aligned, which pages overlap this one, and, when hazards
  // are shown, which locations have one.
  function checks(page) {
    const items = [];
    if ((page.base & 0xFF) === 0) {
      items.push(['✓', 'var(--shared)', 'D page-aligned', '']);
    } else {
      items.push(['⚠', 'var(--nested)', `D not page-aligned · +1 cycle × ${page.direct}`, '']);
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
    key('never accessed', swatch => swatch.classList.add('neverkey'));
    key('free', swatch => { swatch.style.background = 'var(--free)'; });
    key('another page', swatch => swatch.classList.add('cell', 'other'));
    key('both', swatch => {
      swatch.classList.add('clashkey');
      swatch.textContent = '⧉';
    });
    key('colder → hotter', swatch => swatch.classList.add('heat'));
    key('one location', swatch => swatch.classList.add('shape'));
    return strip;
  }

  window.addEventListener('message', event => {
    const message = event.data;
    if (!message || message.type !== 'map') return;
    map = message.result;
    hazards = message.hazards !== false;
    if (message.page) pageId = message.page;
    hot = message.location || null;
    render();
  });
  vscode.postMessage({ type: 'ready' });
}());
