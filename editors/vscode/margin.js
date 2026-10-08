// Draws the loops of every routine as brackets in the margin, with each loop's trip count after
// its last line, and the branches and jumps of the routine at the caret as arrows beside them. The
// server works out the brackets, the arrows and the column each one's upright goes in
// (`nt65/margin`), and this file only draws them. VS Code cannot draw in its own gutter, so the
// margin is a prefix before the first character of every line of a routine, the same width on
// each line so that the code does not jog.
const vscode = require('vscode');

// How long the caret has to rest on a line before the server is asked about it.
const DELAY = 100;

// How long the text has to rest after an edit before the margin is asked for again. Until then
// the old margin stays, since VS Code moves it with the text, and taking it away on every
// keystroke would shift the code left and right while it is typed.
const EDIT_DELAY = 300;

// The values of `nt65.margin`, with how the quick pick names each, from least drawn to most.
const LEVELS = [
  { level: 'off', label: 'Nothing', detail: 'Draw nothing in front of the lines.' },
  { level: 'loops', label: 'Loops', detail: 'A bracket over each loop of every routine, with its trip count.' },
  { level: 'flow', label: 'Loops and flow', detail: 'The loops, and the branches and jumps of the routine at the caret as arrows.' },
];

// The styles a bracket or an arrow is drawn in. Where they meet, the part drawn in the style that
// comes first is drawn on top.
const STYLES = ['caret', 'bracketCaret', 'bracket', 'arrow', 'declared', 'faded'];

// Returns how thick lines are in a style, in pixels. Brackets and the caret's arrow are drawn
// twice as thick, so that they stand out from the other arrows by more than their colour.
function thicknessOf(style) {
  return style === 'caret' || style === 'bracket' || style === 'bracketCaret' ? 2 : 1;
}

// Returns the CSS colour of a style, which is the theme colour that VS Code exposes as a variable.
function colour(style) {
  if (style === 'bracket') return 'var(--vscode-nt65-loopBrackets-bracket)';
  if (style === 'bracketCaret') return 'var(--vscode-nt65-loopBrackets-caret)';
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

// Returns the drawn bracket of the innermost loop that holds the caret's line, or undefined where
// no drawn bracket holds it.
function innermostOf(routine, caret) {
  let found;
  for (const bracket of routine.brackets) {
    if (bracket.column == null || caret < bracket.top || caret > bracket.bottom) continue;
    if (!found || bracket.bottom - bracket.top < found.bottom - found.top) found = bracket;
  }
  return found;
}

// Returns the cells of a routine's prefix, one row per line of the routine. Each row holds a cell
// per column and two cells that join the columns to the code. A column's cell index counts from
// the left, and column 0 is the one nearest the code. A cell names the style of each part drawn in
// it: the half lines `up`, `down`, `left` and `right` from its centre, and the head `into`.
function cellsOf(routine, caret) {
  const width = routine.columns + 2;
  const rows = [];
  for (let line = routine.first; line <= routine.last; line++) {
    rows.push(Array.from({ length: width }, () => ({})));
  }
  const mark = (line, index, parts, style) => {
    if (line < routine.first || line > routine.last) return;
    const cell = rows[line - routine.first][index];
    for (const part of parts) {
      if (cell[part] === undefined || STYLES.indexOf(style) < STYLES.indexOf(cell[part])) cell[part] = style;
    }
  };

  // Draws a horizontal from a column's upright to the code, ending in a head where `head` is set.
  const across = (line, upright, style, head) => {
    for (let index = upright + 1; index < width - 1; index++) mark(line, index, ['left', 'right'], style);
    mark(line, width - 1, head ? ['left', 'into'] : ['left', 'right'], style);
  };

  // A bracket runs from its first line to its last, and joins the code at both ends and at each
  // tee. The arrowhead at the top says the bracket stands for the branches back to that line.
  const lit = innermostOf(routine, caret);
  for (const bracket of routine.brackets) {
    if (bracket.column == null) continue;
    const style = bracket === lit ? 'bracketCaret' : 'bracket';
    const upright = routine.columns - 1 - bracket.column;
    mark(bracket.top, upright, ['down', 'right'], style);
    mark(bracket.bottom, upright, ['up', 'right'], style);
    for (let line = bracket.top + 1; line < bracket.bottom; line++) mark(line, upright, ['up', 'down'], style);
    for (const tee of bracket.tees) mark(tee, upright, ['right'], style);
    across(bracket.top, upright, style, bracket.head);
    for (const line of [...bracket.tees, bracket.bottom]) across(line, upright, style, false);
  }

  // The server leaves out a field whose value is null, so an arrow without room has no `column`.
  const drawn = routine.arrows
    .filter(arrow => arrow.from >= routine.first && arrow.from <= routine.last && arrow.column != null);
  for (const arrow of drawn) {
    const style = styleOf(arrow, caret);
    const upright = routine.columns - 1 - arrow.column;
    const low = Math.min(arrow.from, arrow.to);
    const high = Math.max(arrow.from, arrow.to);
    mark(low, upright, ['down', 'right'], style);
    mark(high, upright, ['up', 'right'], style);
    for (let line = low + 1; line < high; line++) mark(line, upright, ['up', 'down'], style);
    across(arrow.from, upright, style, false);
    across(arrow.to, upright, style, true);
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
// whose background draws the brackets and arrows. An attachment has no option for the box's
// display or its background, so they go in through `textDecoration`, which is written into the
// style as it is.
// <p>
// The prefix comes before the line's indentation. Where a horizontal reaches the code on the
// line, it goes on across the `indent` columns of indentation, so that a head touches the label
// and a tail touches the branch. The box is widened by that much and a negative margin draws it
// under the indentation, so the code stays where it would be.
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
    contentText: ' ',
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

// Returns the text of a loop's trip count: `×16` for a loop that runs 16 times each time it is
// entered, and `×?` for one whose count the program does not say.
function tripsOf(bracket) {
  return `×${bracket.trips == null ? '?' : bracket.trips}`;
}

class Margin {
  constructor(client) {
    this.client = client;

    // Every line's prefix is drawn by this one type, each with a background of its own.
    this.prefix = vscode.window.createTextEditorDecorationType({});

    // The trip counts after the last line of each loop.
    const chip = new vscode.ThemeColor('nt65.loopBrackets.caret');
    this.trips = vscode.window.createTextEditorDecorationType({
      after: {
        color: chip,
        backgroundColor: new vscode.ThemeColor('nt65.loopBrackets.chipBackground'),
        border: '1px solid',
        borderColor: chip,
        margin: '0 0 0 0.6em',
        textDecoration: 'none; border-radius: 3px; padding: 0 3px; font-size: 90%',
      },
    });

    // The hover on the line of an arrow that had no room. It draws nothing.
    this.hover = vscode.window.createTextEditorDecorationType({});

    this.timer = undefined;
    this.cancel = undefined;

    // What is shown: the editor, the document version the answer is for, and the answer.
    this.shown = undefined;

    // The key of the last question asked, so that a caret that stays on its line asks nothing.
    this.asked = undefined;
  }

  // Gets what `nt65.margin` asks for: `off`, `loops`, or `flow`, which adds the caret's arrows.
  get level() {
    const level = vscode.workspace.getConfiguration('nt65').get('margin');
    return LEVELS.some(each => each.level === level) ? level : 'off';
  }

  get arrows() {
    return this.level === 'flow';
  }

  applies(editor) {
    return editor && editor.document.languageId === 'nt65'
      && (editor.document.uri.scheme === 'file' || editor.document.uri.scheme === 'untitled');
  }

  // Redraws what is shown for the caret's new line at once, then waits for the caret to rest and
  // asks again. Only the arrows depend on the caret's line, since it may be in another routine,
  // so with the arrows off a caret that moves asks nothing.
  schedule(editor, delay = DELAY) {
    clearTimeout(this.timer);
    if (this.level === 'off' || !this.applies(editor)) {
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
    const arrows = this.arrows;
    const key = `${document.uri}@${version}:${arrows ? position.line : ''}`;
    if (key === this.asked) return;
    this.asked = key;

    // A newer question makes any older one moot.
    if (this.cancel) this.cancel.cancel();
    const cancel = new vscode.CancellationTokenSource();
    this.cancel = cancel;
    let result;
    try {
      result = await this.client.sendRequest('nt65/margin', {
        textDocument: { uri: document.uri.toString() },
        position: { line: position.line, character: position.character },
        arrows,
      }, cancel.token);
    } catch {
      result = null;
    }

    // An answer that arrives after the text has changed, or for an editor no longer active, is
    // dropped. One for a caret that has since moved still holds the right margin.
    if (cancel.token.isCancellationRequested || document.version !== version
      || vscode.window.activeTextEditor !== editor) {
      return;
    }
    if (!result) {
      this.forget();
      return;
    }
    this.shown = { editor, version, result };
    this.render();
  }

  // Draws what is shown, with the caret's arrows and innermost bracket highlighted. Each line gets
  // one decoration, since VS Code does not keep several at one place in the order they are given.
  // Where one routine's text holds another's, the inner routine's prefix is drawn on its lines.
  render() {
    if (!this.shown) return;
    const { editor, result } = this.shown;
    const document = editor.document;
    const height = lineHeightOf(document);
    const caret = editor.selection.active.line;
    const tabSize = Number(editor.options.tabSize) || 4;
    const prefixes = new Map();
    const routines = [...result.routines].sort((a, b) => (b.last - b.first) - (a.last - a.first));
    for (const routine of routines) {
      if (routine.columns === 0) continue;
      const rows = cellsOf(routine, caret);
      for (let line = routine.first; line <= routine.last && line < document.lineCount; line++) {
        prefixes.set(line, {
          range: new vscode.Range(line, 0, line, 0),
          renderOptions: { before: prefixOf(rows[line - routine.first], height, indentOf(document.lineAt(line).text, tabSize)) },
        });
      }
    }
    editor.setDecorations(this.prefix, [...prefixes.values()]);

    // Loops that end on one line, as several can where a macro's call stands for its body, share
    // one chip there.
    const trips = new Map();
    for (const bracket of result.routines.flatMap(routine => routine.brackets)) {
      if (bracket.bottom >= document.lineCount) continue;
      trips.set(bracket.bottom, [...(trips.get(bracket.bottom) || []), tripsOf(bracket)]);
    }
    editor.setDecorations(this.trips, [...trips].map(([line, texts]) => {
      const end = document.lineAt(line).range.end;
      return { range: new vscode.Range(end, end), renderOptions: { after: { contentText: texts.join(' ') } } };
    }));
    editor.setDecorations(this.hover, result.routines
      .flatMap(routine => routine.arrows)
      .filter(arrow => arrow.column == null && arrow.from < document.lineCount)
      .map(arrow => ({ range: document.lineAt(arrow.from).range, hoverMessage: unshownOf(arrow) })));
  }

  // Takes away what is shown, but keeps the question asked, so that a caret that stays on its line
  // does not ask it again.
  forget() {
    if (!this.shown) return;
    const { editor } = this.shown;
    this.shown = undefined;
    editor.setDecorations(this.prefix, []);
    editor.setDecorations(this.trips, []);
    editor.setDecorations(this.hover, []);
  }

  clear() {
    clearTimeout(this.timer);
    if (this.cancel) this.cancel.cancel();
    this.cancel = undefined;
    this.asked = undefined;
    this.forget();
  }
}

// Everything the margin needs, registered once.
function register(context, client) {
  const margin = new Margin(client);
  context.subscriptions.push(
    margin.prefix,
    margin.trips,
    margin.hover,
    vscode.window.onDidChangeTextEditorSelection(event => margin.schedule(event.textEditor)),
    vscode.window.onDidChangeActiveTextEditor(editor => {
      margin.clear();
      if (editor) margin.schedule(editor);
    }),
    vscode.workspace.onDidChangeTextDocument(event => {
      const editor = vscode.window.activeTextEditor;
      if (editor && event.document === editor.document && event.contentChanges.length > 0) {
        margin.schedule(editor, EDIT_DELAY);
      }
    }),
    vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('editor.fontSize') || event.affectsConfiguration('editor.lineHeight')) {
        margin.render();
      }
      if (!event.affectsConfiguration('nt65.margin')) return;
      margin.clear();
      if (vscode.window.activeTextEditor) margin.schedule(vscode.window.activeTextEditor);
    }),
    vscode.commands.registerCommand('nt65.chooseMargin', async () => {
      const current = margin.level;
      const picked = await vscode.window.showQuickPick(
        LEVELS.map(each => ({ ...each, description: each.level === current ? 'current' : undefined })),
        { placeHolder: 'What the margin shows in front of each routine' });
      if (picked) {
        await vscode.workspace.getConfiguration('nt65').update('margin', picked.level, vscode.ConfigurationTarget.Global);
      }
    }),
    // The editor open at start-up gets its brackets once the server runs, without waiting for
    // the caret to move.
    client.onDidChangeState(() => {
      if (client.isRunning() && vscode.window.activeTextEditor) margin.schedule(vscode.window.activeTextEditor);
    }),
    { dispose: () => margin.clear() });
}

module.exports = { register };
