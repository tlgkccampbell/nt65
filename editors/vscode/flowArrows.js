// Draws the branches and jumps of the routine at the caret as arrows in the margin. The server
// works out the arrows and the column each one's upright goes in (`nt65/flowArrows`), and this
// file only draws them. VS Code cannot put text in its own gutter, so the arrows are a prefix
// before the first character of every line of the routine, the same width on each line so that
// the code does not jog.
const vscode = require('vscode');

// How long the caret has to rest on a line before the server is asked about it.
const DELAY = 100;

// How long the text has to rest after an edit before the arrows are asked for again. Until then
// the old arrows stay, since VS Code moves them with the text, and taking them away on every
// keystroke would shift the code left and right while it is typed.
const EDIT_DELAY = 300;

// The directions a cell's lines go in, combined into the key of the glyph that draws them.
const UP = 1;
const DOWN = 2;
const LEFT = 4;
const RIGHT = 8;

// The glyph for each combination of directions. These are box-drawing characters that Consolas,
// Cascadia and Menlo all have, so that each cell is one column wide in the editor's own font.
const GLYPHS = {
  [UP]: '│',
  [DOWN]: '│',
  [UP | DOWN]: '│',
  [LEFT]: '─',
  [RIGHT]: '─',
  [LEFT | RIGHT]: '─',
  [DOWN | RIGHT]: '┌',
  [UP | RIGHT]: '└',
  [DOWN | LEFT]: '┐',
  [UP | LEFT]: '┘',
  [UP | DOWN | RIGHT]: '├',
  [UP | DOWN | LEFT]: '┤',
  [LEFT | RIGHT | DOWN]: '┬',
  [LEFT | RIGHT | UP]: '┴',
  [UP | DOWN | LEFT | RIGHT]: '┼',
};

// The heads, drawn where an arrow lands and where one leaves the routine.
const INTO = '►';
const OUT = '◄';

// A space that the editor does not collapse.
const SPACE = ' ';

// The styles an arrow is drawn in. Where arrows cross, the cell takes the style that comes first.
const STYLES = ['caret', 'arrow', 'declared', 'faded'];

function colour(style) {
  return new vscode.ThemeColor(`nt65.flowArrows.${style}`);
}

// Returns the style of an arrow. The arrow that starts or ends on the caret's line is brightened,
// like a matched bracket. One the flags prove always or never taken, or that starts on a line
// nothing reaches, is faded, and one a `.next` declared has a colour of its own.
function styleOf(arrow, caret) {
  if (arrow.from === caret || arrow.to === caret) return 'caret';
  if (arrow.proved || !arrow.reached) return 'faded';
  return arrow.declared ? 'declared' : 'arrow';
}

// Returns the cells of the prefix, one row per line of the routine. Each row holds a lead cell,
// where an arrow that leaves the routine has its head, a cell per column, and two cells that
// join the columns to the code. A column's cell index counts from the left, and column 0 is the
// one nearest the code.
function cellsOf(result, caret) {
  const width = 1 + result.columns + 2;
  const rows = [];
  for (let line = result.first; line <= result.last; line++) {
    rows.push(Array.from({ length: width }, () => ({ mask: 0, head: undefined, style: undefined })));
  }
  const cell = (line, index) => rows[line - result.first][index];
  const mark = (line, index, mask, style) => {
    const target = cell(line, index);
    target.mask |= mask;
    if (target.style === undefined || STYLES.indexOf(style) < STYLES.indexOf(target.style)) target.style = style;
  };
  const head = (line, index, glyph, style) => {
    mark(line, index, 0, style);
    cell(line, index).head = glyph;
  };

  // The server leaves out a field whose value is null, so an arrow that leaves the routine has
  // no `to`, and one without room has no `column`.
  const drawn = result.arrows
    .filter(arrow => arrow.from >= result.first && arrow.from <= result.last)
    .filter(arrow => arrow.to == null || arrow.column != null);
  for (const arrow of drawn) {
    const style = styleOf(arrow, caret);
    if (arrow.to == null) {
      head(arrow.from, 0, OUT, style);
      for (let index = 1; index < width; index++) mark(arrow.from, index, LEFT | RIGHT, style);
      continue;
    }
    const upright = 1 + (result.columns - 1 - arrow.column);
    const low = Math.min(arrow.from, arrow.to);
    const high = Math.max(arrow.from, arrow.to);
    mark(low, upright, DOWN | RIGHT, style);
    mark(high, upright, UP | RIGHT, style);
    for (let line = low + 1; line < high; line++) mark(line, upright, UP | DOWN, style);
    for (const line of [arrow.from, arrow.to]) {
      for (let index = upright + 1; index < width; index++) mark(line, index, LEFT | RIGHT, style);
    }
    head(arrow.to, width - 1, INTO, style);
  }
  return rows;
}

// Returns a row of cells as runs of text that share a style. A blank cell joins the run before
// it, since a space has no colour to keep.
function runsOf(row) {
  const runs = [];
  for (const cell of row) {
    const text = cell.head || GLYPHS[cell.mask] || SPACE;
    const style = cell.style || (runs.length > 0 ? runs[runs.length - 1].style : 'arrow');
    if (runs.length > 0 && runs[runs.length - 1].style === style) runs[runs.length - 1].text += text;
    else runs.push({ style, text });
  }
  return runs;
}

// Returns the hover for an arrow that had no room in the margin.
function unshownOf(arrow) {
  return new vscode.MarkdownString(`Goes to line ${arrow.to + 1}. The margin has no room for this arrow.`);
}

class FlowArrows {
  constructor(client) {
    this.client = client;

    // Every run is drawn by this one type, each with its own text and colour. Decorations of one
    // type at one place are drawn in the order they are given, which keeps a row's runs in order.
    this.prefix = vscode.window.createTextEditorDecorationType({});

    // The hover on the line of an arrow that had no room. It draws nothing.
    this.hover = vscode.window.createTextEditorDecorationType({});

    this.timer = undefined;
    this.cancel = undefined;

    // What is shown: the editor, the document version the answer is for, and the answer.
    this.shown = undefined;

    // The key of the last question asked, so that a caret that stays on its line asks nothing.
    this.asked = undefined;
  }

  get enabled() {
    return vscode.workspace.getConfiguration('nt65').get('flowArrows.enabled') !== false;
  }

  applies(editor) {
    return editor && editor.document.languageId === 'nt65'
      && (editor.document.uri.scheme === 'file' || editor.document.uri.scheme === 'untitled');
  }

  // Redraws what is shown for the caret's new line at once, then waits for the caret to rest and
  // asks about that line, which may be in another routine.
  schedule(editor, delay = DELAY) {
    clearTimeout(this.timer);
    if (!this.enabled || !this.applies(editor)) {
      this.clear();
      return;
    }
    if (this.shown && this.shown.editor !== editor) this.clear();
    if (this.shown && this.shown.version === editor.document.version) this.render();
    const position = editor.selection.active;
    this.timer = setTimeout(() => this.ask(editor, position), delay);
  }

  async ask(editor, position) {
    const document = editor.document;
    const version = document.version;
    const key = `${document.uri}@${version}:${position.line}`;
    if (key === this.asked) return;
    this.asked = key;

    // A newer question makes any older one moot.
    if (this.cancel) this.cancel.cancel();
    const cancel = new vscode.CancellationTokenSource();
    this.cancel = cancel;
    let result;
    try {
      result = await this.client.sendRequest('nt65/flowArrows', {
        textDocument: { uri: document.uri.toString() },
        position: { line: position.line, character: position.character },
      }, cancel.token);
    } catch {
      result = null;
    }

    // An answer that arrives after the text has changed, or for an editor no longer active, is
    // dropped. One for a caret that has since moved still holds the right arrows.
    if (cancel.token.isCancellationRequested || document.version !== version
      || vscode.window.activeTextEditor !== editor) {
      return;
    }
    if (!result) {
      this.clear();
      return;
    }
    this.shown = { editor, version, result };
    this.render();
  }

  // Draws what is shown, with the arrows on the caret's line brightened.
  render() {
    if (!this.shown) return;
    const { editor, result } = this.shown;
    const caret = editor.selection.active.line;
    const prefixes = [];
    const rows = cellsOf(result, caret);
    for (let line = result.first; line <= result.last && line < editor.document.lineCount; line++) {
      const at = new vscode.Range(line, 0, line, 0);
      for (const run of runsOf(rows[line - result.first])) {
        prefixes.push({ range: at, renderOptions: { before: { contentText: run.text, color: colour(run.style) } } });
      }
    }
    editor.setDecorations(this.prefix, prefixes);
    editor.setDecorations(this.hover, result.arrows
      .filter(arrow => arrow.to != null && arrow.column == null && arrow.from < editor.document.lineCount)
      .map(arrow => ({ range: editor.document.lineAt(arrow.from).range, hoverMessage: unshownOf(arrow) })));
  }

  clear() {
    clearTimeout(this.timer);
    if (this.cancel) this.cancel.cancel();
    this.cancel = undefined;
    this.asked = undefined;
    if (!this.shown) return;
    const { editor } = this.shown;
    this.shown = undefined;
    editor.setDecorations(this.prefix, []);
    editor.setDecorations(this.hover, []);
  }
}

// Everything the flow arrows need, registered once.
function register(context, client) {
  const arrows = new FlowArrows(client);
  context.subscriptions.push(
    arrows.prefix,
    arrows.hover,
    vscode.window.onDidChangeTextEditorSelection(event => arrows.schedule(event.textEditor)),
    vscode.window.onDidChangeActiveTextEditor(editor => {
      arrows.clear();
      if (editor) arrows.schedule(editor);
    }),
    vscode.workspace.onDidChangeTextDocument(event => {
      const editor = vscode.window.activeTextEditor;
      if (editor && event.document === editor.document && event.contentChanges.length > 0) {
        arrows.schedule(editor, EDIT_DELAY);
      }
    }),
    vscode.workspace.onDidChangeConfiguration(event => {
      if (!event.affectsConfiguration('nt65.flowArrows.enabled')) return;
      if (arrows.enabled && vscode.window.activeTextEditor) arrows.schedule(vscode.window.activeTextEditor);
      else arrows.clear();
    }),
    vscode.commands.registerCommand('nt65.toggleFlowArrows', () => vscode.workspace.getConfiguration('nt65')
      .update('flowArrows.enabled', !arrows.enabled, vscode.ConfigurationTarget.Global)),
    { dispose: () => arrows.clear() });
}

module.exports = { register };
