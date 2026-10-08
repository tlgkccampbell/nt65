// Follows the caret for the direct page map. When the caret is inside a routine, the locations
// that routine reaches are marked in the tree and the grid, and the location named under the
// caret is marked more strongly. The map says which routines reach each location, so the only
// questions put to the editor are which routine holds the caret and what the name under it means.
const vscode = require('vscode');

// How long the caret has to rest before the routine and the name under it are looked up.
const DELAY = 120;

// The scheme of the URIs that tree rows carry so that the tree can colour them.
const SCHEME = 'nt65-direct-page';

// The colour of a location the caret's routine reaches, and of the one under the caret.
const COLOUR = new vscode.ThemeColor('nt65.directPages.caret');

// Returns the key a location goes by in the tree and the grid: its page and its name.
function keyOf(page, location) {
  return `${page.id}/${location.name}`;
}

// Returns the URI a location's row carries, which the tree's decorations are looked up by.
function uriOf(key) {
  return vscode.Uri.from({ scheme: SCHEME, path: `/${key}` });
}

// Checks whether a place is the given position in the given document.
function same(place, uri, position) {
  return !!place && place.uri === uri
    && place.range.start.line === position.line && place.range.start.character === position.character;
}

// Returns the innermost routine among document symbols that holds a position. A macro is a
// function in the outline as well, and is skipped by the map's own check of declarations.
function routineAt(symbols, position) {
  let found = null;
  const walk = list => {
    for (const symbol of list || []) {
      if (!symbol.range || !symbol.range.contains(position)) continue;
      if (symbol.kind === vscode.SymbolKind.Function) found = symbol;
      walk(symbol.children);
    }
  };
  walk(symbols);
  return found;
}

// Returns the keys of the locations a routine reaches, from the map. A routine reaches a location
// when it is in that location's call trees with a role of its own, or reaches it with D not known.
function reachedBy(result, uri, position) {
  const keys = new Set();
  const walk = (key, nodes) => {
    for (const node of nodes) {
      if (node.role && same(node.declaration, uri, position)) keys.add(key);
      walk(key, node.children || []);
    }
  };
  for (const page of result.pages) {
    for (const location of page.locations) walk(keyOf(page, location), location.routines);
    for (const group of page.groups) {
      for (const routine of group.routines) {
        if (!same(routine.declaration, uri, position)) continue;
        for (const use of routine.uses) if (use.home) keys.add(`${use.home}/${use.name}`);
      }
    }
  }
  return keys;
}

// Returns the key of the location a definition leads to, if it leads to one in the map.
function declaredAt(result, definitions) {
  for (const definition of definitions || []) {
    const uri = (definition.targetUri || definition.uri).toString();
    const range = definition.targetSelectionRange || definition.targetRange || definition.range;
    for (const page of result.pages) {
      for (const location of page.locations) {
        if (same(location.declaration, uri, range.start)) return keyOf(page, location);
      }
    }
  }
  return null;
}

// Represents what the caret marks: the locations its routine reaches, and the one under it.
class Caret {
  // `changed(caret)` is called whenever what the caret marks changes.
  constructor(changed) {
    this.changed = changed;
    this.keys = new Set();
    this.direct = null;
    this.routine = null;
    this.timer = undefined;
    this.asked = 0;
    this.decorated = new vscode.EventEmitter();
    this.onDidChangeFileDecorations = this.decorated.event;
  }

  // Looks the caret up again once it has rested, against the map `result`.
  schedule(result) {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.update(result), DELAY);
  }

  // Works out what the caret in the active editor marks. A later call wins over an earlier one
  // that is still waiting for the editor's answers.
  async update(result) {
    const asked = ++this.asked;
    const editor = vscode.window.activeTextEditor;
    let keys = new Set();
    let direct = null;
    let routine = null;
    if (result && editor && editor.document.languageId === 'nt65') {
      const uri = editor.document.uri;
      const position = editor.selection.active;
      try {
        const [symbols, definitions] = await Promise.all([
          vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', uri),
          vscode.commands.executeCommand('vscode.executeDefinitionProvider', uri, position),
        ]);
        if (asked !== this.asked) return;
        const found = routineAt(symbols, position);
        if (found) {
          keys = reachedBy(result, uri.toString(), found.selectionRange.start);
          if (keys.size > 0) routine = found.name;
        }
        direct = declaredAt(result, definitions);
      } catch {
        // The server may be restarting; nothing is marked until it answers.
      }
    }
    if (asked !== this.asked) return;
    this.set(keys, direct, routine);
  }

  // Replaces what is marked, and tells the tree and the grid when anything changed.
  set(keys, direct, routine) {
    const before = new Set([...this.keys, ...(this.direct ? [this.direct] : [])]);
    const same = before.size === new Set([...keys, ...(direct ? [direct] : [])]).size
      && [...keys].every(key => this.keys.has(key)) && direct === this.direct && routine === this.routine;
    if (same) return;
    this.keys = keys;
    this.direct = direct;
    this.routine = routine;
    const touched = new Set([...before, ...keys, ...(direct ? [direct] : [])]);
    this.decorated.fire([...touched].map(uriOf));
    this.changed(this);
  }

  // Returns how a location's row is decorated: a dot for one the caret's routine reaches, and a
  // diamond for the one under the caret.
  provideFileDecoration(uri) {
    if (uri.scheme !== SCHEME) return undefined;
    const key = uri.path.slice(1);
    if (key === this.direct) return { badge: '◆', color: COLOUR, tooltip: 'Under the caret' };
    if (this.keys.has(key)) return { badge: '•', color: COLOUR, tooltip: `Used by ${this.routine}` };
    return undefined;
  }

  dispose() {
    clearTimeout(this.timer);
    this.decorated.dispose();
  }
}

module.exports = { Caret, SCHEME, keyOf, uriOf, routineAt, reachedBy, declaredAt };
