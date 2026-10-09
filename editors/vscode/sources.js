// Shows where each value that the instruction at the caret reads was set, and where each value it
// writes is read. The server works out the answer (`nt65/sources`), and this file only draws it: a
// tint, a bar and a tag on each line that set a value or reads one, a dotted bar on each line a
// value passed through or leaves the routine by, and a chip per input and per output on the caret
// line. A reader's tag starts with `→` and an exit's ends with `↱`, so that a line that reads the
// caret's value is told apart from one that set a value the caret reads. A store into code and the
// instruction it patches are linked the same way, in the memory colour. Meaning is carried by colour and short glyphs, and anything longer goes in the
// hover, because lines crowded with inlays are hard to read.
const vscode = require('vscode');

// How long the caret has to rest on a line before the server is asked about it.
const DELAY = 100;

// The colour groups. Each register has its own colour, the flags share one and are told apart
// by their letter, the two widths share one, and memory has one.
const GROUPS = ['a', 'x', 'y', 'flags', 'widths', 'memory'];

// The context key that the next and previous keybindings depend on.
const ACTIVE = 'nt65.sources.active';

function groupOf(input) {
  switch (input.category) {
    case 'register': return input.name.toLowerCase();
    case 'flag': return 'flags';
    case 'width': return 'widths';
    default: return 'memory';
  }
}

function colour(group) {
  return new vscode.ThemeColor(`nt65.sources.${group}`);
}

function background(group) {
  return new vscode.ThemeColor(`nt65.sources.${group}Background`);
}

// The style of a small label after the code. A tag on a source line is filled; one on a through
// line is hollow and dotted; one on a best-effort source is dashed. Rounded corners are not an
// attachment option, so they go in through `textDecoration`, which is written into the style.
function label(group, border, filled) {
  return {
    color: colour(group),
    backgroundColor: filled ? background(group) : undefined,
    border: `1px ${border}`,
    borderColor: colour(group),
    fontWeight: 'bold',
    margin: '0 0 0 0.6em',
    textDecoration: 'none; border-radius: 3px; padding: 0 3px; font-size: 90%',
  };
}

// One decoration type per group and per style, made once.
function decorationTypes() {
  const types = {};
  for (const group of GROUPS) {
    types[group] = {
      source: vscode.window.createTextEditorDecorationType({
        isWholeLine: true,
        backgroundColor: background(group),
        borderWidth: '0 0 0 3px',
        borderStyle: 'solid',
        borderColor: colour(group),
        overviewRulerColor: colour(group),
        overviewRulerLane: vscode.OverviewRulerLane.Left,
        after: label(group, 'solid', true),
      }),
      through: vscode.window.createTextEditorDecorationType({
        isWholeLine: true,
        borderWidth: '0 0 0 3px',
        borderStyle: 'dotted',
        borderColor: colour(group),
        after: label(group, 'dotted', false),
      }),
      // A best-effort source is fainter than a proven one, with a tint of its own and dashes
      // where a proven source is solid. Its ruler mark goes in the centre lane instead of the
      // left one, since a ruler mark has no dashed form. Only memory has best-effort sources.
      guess: vscode.window.createTextEditorDecorationType({
        isWholeLine: true,
        backgroundColor: new vscode.ThemeColor(`nt65.sources.${group}FaintBackground`),
        borderWidth: '0 0 0 3px',
        borderStyle: 'dashed',
        borderColor: colour(group),
        overviewRulerColor: colour(group),
        overviewRulerLane: vscode.OverviewRulerLane.Center,
        after: label(group, 'dashed', false),
      }),
      // A line that might have changed a value in memory after its source set it. It is drawn
      // as a doubt and not as a source: a thinner dashed bar, no tint and no ruler mark, and a
      // faded, italic, hollow tag whose name ends in `?`.
      possible: vscode.window.createTextEditorDecorationType({
        isWholeLine: true,
        borderWidth: '0 0 0 2px',
        borderStyle: 'dashed',
        borderColor: colour(group),
        after: {
          ...label(group, 'dashed', false),
          fontStyle: 'italic',
          fontWeight: 'normal',
          textDecoration: 'none; border-radius: 3px; padding: 0 3px; font-size: 90%; opacity: 0.7',
        },
      }),
      chip: vscode.window.createTextEditorDecorationType({
        after: label(group, 'solid', true),
      }),
    };
  }
  // The hover on the caret line, which lists every input and source. It draws nothing.
  types.hover = vscode.window.createTextEditorDecorationType({});

  // The `+N` box that says how many chips did not fit. It is made last, so it comes after the
  // chips, and drawn in a neutral colour, because it stands for inputs of any colour.
  const muted = new vscode.ThemeColor('descriptionForeground');
  types.more = vscode.window.createTextEditorDecorationType({
    after: {
      color: muted,
      border: '1px dotted',
      borderColor: muted,
      margin: '0 0 0 0.6em',
      textDecoration: 'none; border-radius: 3px; padding: 0 3px; font-size: 90%',
    },
  });
  return types;
}

// Adds `name` to the label for `line` in `labels`, a map from line to the names shown there.
function addLabel(labels, line, name) {
  const names = labels.get(line) || [];
  if (!names.includes(name)) names.push(name);
  labels.set(line, names);
}

// Turns a map of labels into decorations, one per line, each with its names after the code.
function labelled(document, labels) {
  return [...labels].flatMap(([line, names]) => boxes(document.lineAt(line).range, names));
}

// One decoration per label, so that each input gets a box of its own rather than sharing one
// with the others of its colour.
function boxes(range, texts) {
  return texts.map(text => ({ range, renderOptions: { after: { contentText: text } } }));
}

// The chip for one input on the caret line. It says whether the value came from the caller,
// whether the analysis lost track of it, how far away its nearest source is when not every source
// is on screen, and how many sources there are when there is more than one.
function chipOf(input, caret, visible) {
  const name = input.name;
  if (input.sources.some(source => source.kind === 'unknown')) return `${name}?`;
  const times = input.sources.length > 1 ? ` ×${input.sources.length}` : '';
  const set = input.sources.filter(source => source.kind !== 'entry');
  if (set.length < input.sources.length) return `${name}↰${set.length > 0 ? times : ''}`;
  if (set.every(source => visible(source.range.start.line))) return `${name}${times}`;
  let nearest = set[0].range.start.line;
  for (const source of set) {
    const line = source.range.start.line;
    if (Math.abs(line - caret) < Math.abs(nearest - caret)) nearest = line;
  }
  const arrow = nearest < caret ? '↑' : '↓';
  return `${name}${arrow}${Math.abs(nearest - caret)}${times}`;
}

// Returns the chip for one output on the caret line, which says how many lines read the value, as
// `Y→3`. A value that only leaves the routine is `Y↱`.
function outputChipOf(output) {
  const read = output.readers.filter(reader => reader.kind !== 'exit').length;
  return read > 0 ? `${output.name}→${read}` : `${output.name}↱`;
}

// Merges the outputs that share a group, as `grouped` does for inputs.
function groupedOutputs(outputs) {
  const merged = new Map();
  for (const output of outputs) {
    const name = output.group || output.name;
    const known = merged.get(name);
    if (!known) {
      merged.set(name, { ...output, name, readers: [...output.readers], possibly: [...(output.possibly || [])] });
      continue;
    }
    for (const reader of output.readers) {
      if (!known.readers.some(other => other.kind === reader.kind && other.range.start.line === reader.range.start.line)) {
        known.readers.push(reader);
      }
    }
    known.possibly.push(...(output.possibly || []));
  }
  return [...merged.values()];
}

// Merges the inputs that share a group, such as the bytes of one pointer or the members of one
// struct in memory, into one input named by the group, so that they get one chip and one tag. A
// register or a flag has no group and stays as it is.
function grouped(inputs) {
  const merged = new Map();
  for (const input of inputs) {
    const name = input.group || input.name;
    const known = merged.get(name);
    if (!known) {
      merged.set(name, {
        ...input, name, sources: [...input.sources], through: [...input.through], possibly: [...(input.possibly || [])],
      });
      continue;
    }
    for (const source of input.sources) {
      if (!known.sources.some(other => other.kind === source.kind && other.range.start.line === source.range.start.line)) {
        known.sources.push(source);
      }
    }
    known.through.push(...input.through);
    known.possibly.push(...(input.possibly || []));
  }
  return [...merged.values()];
}

// Splits a line's chips, given as [group, text] pairs in the order they are drawn, into the ones
// that fit in `budget` characters and a count of the rest. A chip costs its text and a space.
// Where some do not fit, room is kept for the `+N` box that counts them. A budget of 0 means no
// limit.
function fit(chips, budget) {
  if (budget <= 0) return { shown: chips, hidden: 0 };
  const cost = chips.reduce((sum, [, text]) => sum + text.length + 1, 0);
  if (cost <= budget) return { shown: chips, hidden: 0 };
  const shown = [];
  let used = 0;
  for (const chip of chips) {
    const rest = chips.length - shown.length - 1;
    const more = rest > 0 ? `+${rest}`.length + 1 : 0;
    if (used + chip[1].length + 1 + more > budget) break;
    shown.push(chip);
    used += chip[1].length + 1;
  }
  return { shown, hidden: chips.length - shown.length };
}

// Writes a line of the document as inline code, for the hover.
function code(document, line) {
  const text = document.lineAt(line).text.trim();
  const fence = text.includes('`') ? '``' : '`';
  return `${fence}${text}${fence}`;
}

// What the hover adds after a best-effort source or reader: that it was inferred, and what else
// might have changed the value in between, where something might, as the server words it
// ("or possibly ...").
function guessed(source) {
  if (source.confidence !== 'bestEffort') return '';
  return source.reason ? ` (inferred), ${source.reason}` : ' (inferred)';
}

// Returns what a patched instruction can run as, given the instructions a `.patch` lists, as
// `runs as dex or inx`. A `.patch` that lists none says nothing about what is written.
function runsAs(document, link) {
  if (link.variants.length === 0) return 'rewritten';
  const written = document.lineAt(link.target.start.line).text.trim().split(/\s+/)[0].toLowerCase();
  return `runs as ${[written, ...link.variants.filter(name => name !== written)].join(' or ')}`;
}

// Returns whether the caret is on the instruction that every link patches, rather than on a store
// or a `.patch`.
function onPatched(result, line) {
  const patches = result.patches || [];
  return patches.length > 0 && patches.every(link => link.target.start.line === line);
}

// The hover on the caret line: each input, each line that set it with the code on that line, and
// for a value the analysis lost track of, the line that stopped it and why.
function hoverOf(document, result, line) {
  const hover = new vscode.MarkdownString();
  for (const input of result.inputs) {
    hover.appendMarkdown(`**${input.name}**\n\n`);
    for (const source of input.sources) {
      const line = source.range.start.line;
      const where = `line ${line + 1} ${code(document, line)}`;
      switch (source.kind) {
        case 'entry':
          hover.appendMarkdown(`- from the routine's caller${guessed(source)}\n`);
          break;
        case 'unknown':
          hover.appendMarkdown(`- unknown after ${where}: ${source.reason}\n`);
          break;
        case 'macro':
          hover.appendMarkdown(`- ${where} (macro)${guessed(source)}\n`);
          break;
        case 'call':
          hover.appendMarkdown(`- ${where} (call)${guessed(source)}\n`);
          break;
        default:
          hover.appendMarkdown(`- ${where}${guessed(source)}\n`);
      }
    }
    for (const through of input.through) {
      hover.appendMarkdown(`- through line ${through.start.line + 1} ${code(document, through.start.line)}\n`);
    }
    hover.appendMarkdown('\n');
  }
  for (const output of result.outputs || []) {
    hover.appendMarkdown(`**${output.name}** is read by\n\n`);
    for (const reader of output.readers) {
      const line = reader.range.start.line;
      const where = `line ${line + 1} ${code(document, line)}`;
      const inferred = guessed(reader);
      switch (reader.kind) {
        case 'exit':
          hover.appendMarkdown(`- whatever the routine returns to, after ${where}${inferred}\n`);
          break;
        case 'call':
          hover.appendMarkdown(`- ${where} (call)${inferred}\n`);
          break;
        case 'macro':
          hover.appendMarkdown(`- ${where} (macro)${inferred}\n`);
          break;
        default:
          hover.appendMarkdown(`- ${where}${inferred}\n`);
      }
    }
    hover.appendMarkdown('\n');
  }
  const patches = result.patches || [];
  if (onPatched(result, line)) {
    hover.appendMarkdown('**Patched** by\n\n');
    for (const link of patches) {
      const at = link.store.start.line;
      const what = link.variants.length > 0 ? `, so it ${runsAs(document, link)}` : '';
      hover.appendMarkdown(`- line ${at + 1} ${code(document, at)}${what}\n`);
    }
  } else {
    for (const link of patches) {
      const at = link.target.start.line;
      hover.appendMarkdown(`**Patches** ${link.name}, line ${at + 1} ${code(document, at)}, which then ${runsAs(document, link)}\n\n`);
    }
  }
  return hover;
}

class Sources {
  constructor(client) {
    this.client = client;
    this.types = decorationTypes();
    this.timer = undefined;
    this.cancel = undefined;

    // What is shown: the editor, the document version and the line asked about, and the answer.
    this.shown = undefined;

    // The key of the last question asked, so that a caret that stays on its line asks nothing.
    this.asked = undefined;

    // Where the last navigation command put the caret. A selection change to there is the
    // command's own and keeps what is shown, rather than asking about the new line.
    this.moved = undefined;
  }

  get enabled() {
    return vscode.workspace.getConfiguration('nt65').get('sources.enabled') !== false;
  }

  // Waits for the caret to rest, then asks about its line.
  schedule(editor) {
    clearTimeout(this.timer);
    if (!this.enabled || !this.applies(editor)) {
      this.clear();
      return;
    }
    const position = editor.selection.active;
    if (this.moved && this.shown && this.shown.editor === editor && position.isEqual(this.moved)) {
      this.render();
      return;
    }
    this.moved = undefined;
    if (this.shown && this.shown.line !== position.line) this.clear();
    this.timer = setTimeout(() => this.ask(editor, position), DELAY);
  }

  applies(editor) {
    return editor && editor.document.languageId === 'nt65'
      && (editor.document.uri.scheme === 'file' || editor.document.uri.scheme === 'untitled');
  }

  async ask(editor, position) {
    const document = editor.document;
    const version = document.version;
    const key = `${document.uri}@${version}:${position.line}`;
    if (key === this.asked && this.shown) return;
    this.asked = key;

    // A newer question makes any older one moot.
    if (this.cancel) this.cancel.cancel();
    const cancel = new vscode.CancellationTokenSource();
    this.cancel = cancel;
    let result;
    try {
      result = await this.client.sendRequest('nt65/sources', {
        textDocument: { uri: document.uri.toString() },
        position: { line: position.line, character: position.character },
      }, cancel.token);
    } catch {
      result = null;
    }

    // An answer that arrives after the caret has moved on, or the text has changed, is dropped.
    if (cancel.token.isCancellationRequested || document.version !== version
      || vscode.window.activeTextEditor !== editor || editor.selection.active.line !== position.line) {
      return;
    }
    if (!result || result.inputs.length + (result.outputs || []).length + (result.patches || []).length === 0) {
      this.clear();
      return;
    }
    this.shown = { editor, version, line: position.line, result };
    vscode.commands.executeCommand('setContext', ACTIVE, true);
    this.render();
  }

  // Draws what is shown. The chips depend on which lines are on screen, so this runs again
  // whenever the editor scrolls, without a new request.
  render() {
    if (!this.shown) return;
    const { editor, line, result } = this.shown;
    const document = editor.document;
    const visible = at => editor.visibleRanges.some(range => range.start.line <= at && at <= range.end.line);
    const caretEnd = document.lineAt(line).range.end;
    const opener = result.routine.start.line;
    const openerEnd = document.lineAt(opener).range.end;
    const chips = [];
    const entries = [];
    for (const group of GROUPS) {
      const sources = new Map();
      const guesses = new Map();
      const through = new Map();
      const possible = new Map();
      for (const input of grouped(result.inputs.filter(item => groupOf(item) === group))) {
        for (const source of input.sources) {
          if (source.kind === 'entry' && !entries.some(([, text]) => text === `${input.name}↰`)) {
            entries.push([group, `${input.name}↰`]);
          } else if (source.kind !== 'unknown' && source.kind !== 'entry') {
            addLabel(source.confidence === 'bestEffort' ? guesses : sources, source.range.start.line, input.name);
          }
        }
        for (const range of input.through) addLabel(through, range.start.line, input.name);
        for (const range of input.possibly || []) addLabel(possible, range.start.line, `${input.name}?`);
        chips.push([group, chipOf(input, line, visible)]);
      }

      // Where the caret's values go: a reader is tagged like a source, with a `→` in front, and an
      // exit like a through line, with a `↱` after. A line that might change a value in memory on
      // its way to a reader is drawn as the same doubt an input's is.
      for (const output of groupedOutputs((result.outputs || []).filter(item => groupOf(item) === group))) {
        for (const reader of output.readers) {
          const at = reader.range.start.line;
          if (reader.kind === 'exit') addLabel(through, at, `${output.name}↱`);
          else addLabel(reader.confidence === 'bestEffort' ? guesses : sources, at, `→${output.name}`);
        }
        for (const range of output.possibly) addLabel(possible, range.start.line, `${output.name}?`);
        chips.push([group, outputChipOf(output)]);
      }
      // A store into code and the instruction it patches are linked like a source and its reader.
      // The caret's chip names what it patches, or says how many stores patch it, and goes first
      // so that the length limit never hides it.
      if (group === 'memory') {
        const patches = result.patches || [];
        if (onPatched(result, line)) {
          for (const link of patches) addLabel(sources, link.store.start.line, `patches ${link.name}`);
          chips.unshift([group, patches.length > 1 ? `patched ×${patches.length}` : 'patched']);
        } else {
          for (const link of patches) {
            addLabel(sources, link.target.start.line, runsAs(document, link));
            const chip = `patches ${link.name}`;
            if (!chips.some(([, text]) => text === chip)) chips.unshift([group, chip]);
          }
        }
      }
      const types = this.types[group];
      editor.setDecorations(types.source, labelled(document, sources));
      editor.setDecorations(types.guess, labelled(document, guesses));
      editor.setDecorations(types.through, labelled(document, through));
      editor.setDecorations(types.possible, labelled(document, possible));
    }

    // The chips go after the code on the caret line, and the routine's opening line gets a `↰`
    // for each input that comes from the caller, which sticky scroll often keeps in sight. A call
    // that reads many locations in memory would fill the line, so each line's chips are held to a
    // length, registers first, and a `+N` box counts the rest, which the hover still lists.
    const budget = vscode.workspace.getConfiguration('nt65').get('sources.chipLength') ?? 60;
    const onCaret = fit(chips, budget);
    const onOpener = opener === line ? { shown: [], hidden: 0 } : fit(entries, budget);
    const at = end => new vscode.Range(end, end);
    for (const group of GROUPS) {
      const texts = shown => shown.filter(([owner]) => owner === group).map(([, text]) => text);
      editor.setDecorations(this.types[group].chip, [
        ...boxes(at(caretEnd), texts(onCaret.shown)),
        ...boxes(at(openerEnd), texts(onOpener.shown)),
      ]);
    }
    editor.setDecorations(this.types.more, [
      ...(onCaret.hidden > 0 ? boxes(at(caretEnd), [`+${onCaret.hidden}`]) : []),
      ...(onOpener.hidden > 0 ? boxes(at(openerEnd), [`+${onOpener.hidden}`]) : []),
    ]);
    editor.setDecorations(this.types.hover, [{ range: document.lineAt(line).range, hoverMessage: hoverOf(document, result, line) }]);
  }

  clear() {
    clearTimeout(this.timer);
    if (this.cancel) this.cancel.cancel();
    this.cancel = undefined;
    this.asked = undefined;
    this.moved = undefined;
    if (!this.shown) return;
    const { editor } = this.shown;
    this.shown = undefined;
    for (const group of GROUPS) {
      for (const type of Object.values(this.types[group])) editor.setDecorations(type, []);
    }
    editor.setDecorations(this.types.hover, []);
    editor.setDecorations(this.types.more, []);
    vscode.commands.executeCommand('setContext', ACTIVE, false);
  }

  // The lines the navigation commands visit, in the order they come in the file: every line that
  // set an input's value, the routine's opening line where a value came from the caller, and every
  // line that reads an output's value.
  targets() {
    if (!this.shown) return [];
    const lines = new Set();
    for (const input of this.shown.result.inputs) {
      for (const source of input.sources) {
        if (source.kind !== 'unknown') lines.add(source.range.start.line);
      }
    }
    for (const output of this.shown.result.outputs || []) {
      for (const reader of output.readers) lines.add(reader.range.start.line);
    }
    for (const link of this.shown.result.patches || []) {
      lines.add(link.store.start.line);
      lines.add(link.target.start.line);
    }
    lines.delete(this.shown.line);
    return [...lines].sort((a, b) => a - b);
  }

  // Moves the caret to the next source after it, or the previous one before it, wrapping round
  // at either end. What is shown stays the answer for the line the caret started on.
  step(forward) {
    const editor = vscode.window.activeTextEditor;
    const lines = this.targets();
    if (!this.shown || editor !== this.shown.editor || lines.length === 0) return;
    const at = editor.selection.active.line;
    const line = forward
      ? lines.find(target => target > at) ?? lines[0]
      : [...lines].reverse().find(target => target < at) ?? lines[lines.length - 1];
    const position = new vscode.Position(line, editor.document.lineAt(line).firstNonWhitespaceCharacterIndex);
    this.moved = position;
    editor.selection = new vscode.Selection(position, position);
    editor.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenterIfOutsideViewport);
  }

  // Opens the peek view on every source of every input on the caret line.
  peek() {
    if (!this.shown) return;
    const { editor, line } = this.shown;
    const uri = editor.document.uri;
    const locations = this.targets().map(target => new vscode.Location(uri, editor.document.lineAt(target).range));
    vscode.commands.executeCommand('editor.action.peekLocations', uri, new vscode.Position(line, 0), locations, 'peek');
  }
}

// Everything the source highlights need, registered once.
function register(context, client) {
  const sources = new Sources(client);
  context.subscriptions.push(
    ...GROUPS.flatMap(group => Object.values(sources.types[group])),
    sources.types.hover,
    sources.types.more,
    vscode.window.onDidChangeTextEditorSelection(event => sources.schedule(event.textEditor)),
    vscode.window.onDidChangeTextEditorVisibleRanges(event => {
      if (sources.shown && event.textEditor === sources.shown.editor) sources.render();
    }),
    vscode.window.onDidChangeActiveTextEditor(() => sources.clear()),
    vscode.workspace.onDidChangeTextDocument(event => {
      if (sources.shown && event.document === sources.shown.editor.document && event.contentChanges.length > 0) {
        sources.clear();
      }
    }),
    vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('nt65.sources.enabled') && !sources.enabled) sources.clear();
      if (event.affectsConfiguration('nt65.sources.chipLength')) sources.render();
    }),
    vscode.commands.registerCommand('nt65.nextSource', () => sources.step(true)),
    vscode.commands.registerCommand('nt65.previousSource', () => sources.step(false)),
    vscode.commands.registerCommand('nt65.peekSources', () => sources.peek()),
    vscode.commands.registerCommand('nt65.toggleSources', () => vscode.workspace.getConfiguration('nt65')
      .update('sources.enabled', !sources.enabled, vscode.ConfigurationTarget.Global)),
    { dispose: () => sources.clear() });
}

module.exports = { register };
