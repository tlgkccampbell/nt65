// Draws the branches and jumps of the routine at the caret as arrows in the margin. The server
// works out the arrows and the column each one's upright goes in (`nt65/flowArrows`), and this
// file only draws them. VS Code cannot draw in its own gutter, so the arrows are a prefix before
// the first character of every line of the routine, the same width on each line so that the code
// does not jog.
const vscode = require('vscode');

// How long the caret has to rest on a line before the server is asked about it.
const DELAY = 100;

// How long the text has to rest after an edit before the arrows are asked for again. Until then
// the old arrows stay, since VS Code moves them with the text, and taking them away on every
// keystroke would shift the code left and right while it is typed.
const EDIT_DELAY = 300;

// The styles an arrow is drawn in. Where arrows meet, the part drawn in the style that comes
// first is drawn on top.
const STYLES = ['caret', 'arrow', 'declared', 'faded'];

// Returns how thick an arrow's lines are in a style, in pixels. The caret's arrow is drawn twice
// as thick, so that it stands out from the others by more than its colour.
function thicknessOf(style) {
  return style === 'caret' ? 2 : 1;
}

// Returns the CSS colour of a style, which is the theme colour that VS Code exposes as a variable.
function colour(style) {
  return `var(--vscode-nt65-flowArrows-${style})`;
}

// Returns the style of an arrow. The arrow that starts or ends on the caret's line is highlighted,
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
// one nearest the code. A cell names the style of each part drawn in it: the half lines `up`,
// `down`, `left` and `right` from its centre, and the heads `into` and `out`.
function cellsOf(result, caret) {
  const width = 1 + result.columns + 2;
  const rows = [];
  for (let line = result.first; line <= result.last; line++) {
    rows.push(Array.from({ length: width }, () => ({})));
  }
  const mark = (line, index, parts, style) => {
    const cell = rows[line - result.first][index];
    for (const part of parts) {
      if (cell[part] === undefined || STYLES.indexOf(style) < STYLES.indexOf(cell[part])) cell[part] = style;
    }
  };

  // The server leaves out a field whose value is null, so an arrow that leaves the routine has
  // no `to`, and one without room has no `column`.
  const drawn = result.arrows
    .filter(arrow => arrow.from >= result.first && arrow.from <= result.last)
    .filter(arrow => arrow.to == null || arrow.column != null);
  for (const arrow of drawn) {
    const style = styleOf(arrow, caret);
    if (arrow.to == null) {
      mark(arrow.from, 0, ['out', 'right'], style);
      for (let index = 1; index < width; index++) mark(arrow.from, index, ['left', 'right'], style);
      continue;
    }
    const upright = 1 + (result.columns - 1 - arrow.column);
    const low = Math.min(arrow.from, arrow.to);
    const high = Math.max(arrow.from, arrow.to);
    mark(low, upright, ['down', 'right'], style);
    mark(high, upright, ['up', 'right'], style);
    for (let line = low + 1; line < high; line++) mark(line, upright, ['up', 'down'], style);
    for (const line of [arrow.from, arrow.to]) {
      for (let index = upright + 1; index < width - 1; index++) mark(line, index, ['left', 'right'], style);
    }
    mark(arrow.from, width - 1, ['left', 'right'], style);
    mark(arrow.to, width - 1, ['left', 'into'], style);
  }
  return rows;
}

// Returns the CSS background that draws a row of cells, in a box `height` pixels tall. Every part
// is a layer of its own, a gradient of one colour sized to the line or the half of a head it
// draws, so the uprights of one line meet those of the next with no gap.
function backgroundOf(row, height) {
  const middle = Math.floor(height / 2);
  const centre = middle + 0.5;
  const head = Math.max(3, Math.round(height * 0.22));
  const layers = [];
  const add = (style, image, x, y, width, tall) =>
    layers.push({ style, css: `${image} ${x} ${y} / ${width} ${tall} no-repeat` });
  const solid = style => `linear-gradient(${colour(style)}, ${colour(style)})`;

  // A half of a triangular head fills the half of its box on one side of the box's diagonal.
  const half = (style, towards) => `linear-gradient(to ${towards}, ${colour(style)} 50%, transparent 50%)`;
  row.forEach((cell, index) => {
    const upright = style => `calc(${index}ch + 0.5ch - ${thicknessOf(style) / 2}px)`;
    if (cell.up) {
      const thick = thicknessOf(cell.up);
      add(cell.up, solid(cell.up), upright(cell.up), '0px', `${thick}px`, `${middle + thick}px`);
    }
    if (cell.down) {
      const thick = thicknessOf(cell.down);
      add(cell.down, solid(cell.down), upright(cell.down), `${middle}px`, `${thick}px`, `${height - middle}px`);
    }
    const box = `calc(${index}ch + 0.1ch)`;
    if (cell.into) {
      add(cell.into, half(cell.into, 'top right'), box, `${centre - head}px`, '0.8ch', `${head}px`);
      add(cell.into, half(cell.into, 'bottom right'), box, `${centre}px`, '0.8ch', `${head}px`);
    }
    if (cell.out) {
      add(cell.out, half(cell.out, 'top left'), box, `${centre - head}px`, '0.8ch', `${head}px`);
      add(cell.out, half(cell.out, 'bottom left'), box, `${centre}px`, '0.8ch', `${head}px`);
    }
  });

  // A straight run of half lines in one style is one layer. Drawn as a layer per half, the halves'
  // edges round to different pixels at some font sizes and leave hairline gaps between them.
  const halves = row.flatMap(cell => [cell.left, cell.right]);
  for (let start = 0; start < halves.length;) {
    let end = start + 1;
    while (end < halves.length && halves[end] === halves[start]) end++;
    if (halves[start]) {
      const thick = thicknessOf(halves[start]);
      add(halves[start], solid(halves[start]), `calc(${start * 0.5}ch - ${thick / 2}px)`, `${middle}px`,
        `calc(${(end - start) * 0.5}ch + ${thick}px)`, `${thick}px`);
    }
    start = end;
  }

  // The first layer is drawn on top, so the parts in the style that comes first go first.
  layers.sort((a, b) => STYLES.indexOf(a.style) - STYLES.indexOf(b.style));
  return layers.map(layer => layer.css).join(', ');
}

// Returns the height of a line in the editor, in pixels, worked out from the settings the way
// VS Code works it out. A line height under 8 is a multiple of the font size, and 0 means the
// default ratio for the platform.
function lineHeightOf(document) {
  const settings = vscode.workspace.getConfiguration('editor', { uri: document.uri, languageId: document.languageId });
  const size = settings.get('fontSize') || 14;
  const height = settings.get('lineHeight') || 0;
  if (height === 0) return Math.round(size * (process.platform === 'darwin' ? 1.5 : 1.35));
  return Math.round(height < 8 ? size * height : height);
}

// Returns how one line's prefix is drawn: a box one line tall and as wide as the row's cells,
// whose background draws the arrows. An attachment has no option for the box's display or its
// background, so they go in through `textDecoration`, which is written into the style as it is.
// <p>
// The prefix comes before the line's indentation. Where an arrow ends on the line, its
// horizontal goes on across the `indent` columns of indentation, so that the head touches the
// label and the tail touches the branch. The box is widened by that much and a negative margin
// draws it under the indentation, so the code stays where it would be.
function prefixOf(row, height, indent) {
  const last = row[row.length - 1];
  const across = last.left ? indent : 0;
  const cells = across === 0 ? row : [
    ...row.slice(0, -1),
    ...Array.from({ length: across }, () => ({ left: last.left, right: last.left })),
    last,
  ];
  const background = backgroundOf(cells, height);
  const margin = across === 0 ? '' : `; margin-right: -${across}ch`;
  return {
    contentText: '\u00a0',
    color: 'transparent',
    textDecoration: `none; display: inline-block; vertical-align: top; width: ${cells.length}ch; height: ${height}px${margin}`
      + (background ? `; background: ${background}` : ''),
  };
}

// Returns how many columns the indentation of a line takes, with each tab reaching the next stop.
function indentOf(text, tabSize) {
  let columns = 0;
  for (const character of text) {
    if (character === ' ') columns++;
    else if (character === '\t') columns += tabSize - (columns % tabSize);
    else break;
  }
  return columns;
}

// Returns the hover for an arrow that had no room in the margin.
function unshownOf(arrow) {
  return new vscode.MarkdownString(`Goes to line ${arrow.to + 1}. The margin has no room for this arrow.`);
}

class FlowArrows {
  constructor(client) {
    this.client = client;

    // Every line's prefix is drawn by this one type, each with a background of its own.
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

  // Draws what is shown, with the arrows on the caret's line highlighted. Each line gets one
  // decoration, since VS Code does not keep several at one place in the order they are given.
  render() {
    if (!this.shown) return;
    const { editor, result } = this.shown;
    const document = editor.document;
    const height = lineHeightOf(document);
    const rows = cellsOf(result, editor.selection.active.line);
    const tabSize = Number(editor.options.tabSize) || 4;
    const prefixes = [];
    for (let line = result.first; line <= result.last && line < document.lineCount; line++) {
      prefixes.push({
        range: new vscode.Range(line, 0, line, 0),
        renderOptions: { before: prefixOf(rows[line - result.first], height, indentOf(document.lineAt(line).text, tabSize)) },
      });
    }
    editor.setDecorations(this.prefix, prefixes);
    editor.setDecorations(this.hover, result.arrows
      .filter(arrow => arrow.to != null && arrow.column == null && arrow.from < document.lineCount)
      .map(arrow => ({ range: document.lineAt(arrow.from).range, hoverMessage: unshownOf(arrow) })));
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
      if (event.affectsConfiguration('editor.fontSize') || event.affectsConfiguration('editor.lineHeight')) {
        arrows.render();
      }
      if (!event.affectsConfiguration('nt65.flowArrows.enabled')) return;
      if (arrows.enabled && vscode.window.activeTextEditor) arrows.schedule(vscode.window.activeTextEditor);
      else arrows.clear();
    }),
    vscode.commands.registerCommand('nt65.toggleFlowArrows', () => vscode.workspace.getConfiguration('nt65')
      .update('flowArrows.enabled', !arrows.enabled, vscode.ConfigurationTarget.Global)),
    { dispose: () => arrows.clear() });
}

module.exports = { register };
