// Draws the 65816's register widths beside the line numbers, as a two-stripe icon on every line
// whose widths are known: the left stripe for A and the right for X and Y, bright for 16 bits and
// dim for 8. Emulation mode draws both stripes in a color of its own. A line whose widths are not
// known gets no icon. The server works out the runs of lines (`nt65/widths`), and this file only
// draws them. The icons go in the glyph margin, so the code never moves. VS Code shows no hover
// for an icon there, so the status bar says in words what the caret line's stripes mean, with
// each stripe beside the register it stands for, and its tooltip gives the whole key.
const vscode = require('vscode');
const { showsSource, Requests } = require('./documents');

// How long the text has to rest after an edit before the widths are asked for again. Until then
// the old icons stay, since VS Code moves them with the text.
const EDIT_DELAY = 300;

// The colors of the stripes, which are the default colors input sources gives A and X, so that a
// register has one color everywhere and the two stripes differ in hue as well as place. An icon
// is an image, which cannot take a theme color, so each one is drawn once for dark themes and
// once for light ones. The 8-bit stripes are the 16-bit color at a third of its strength.
const COLORS = {
  dark: { a: '#4FC1FF', index: '#89D185', emulation: '#C586C0' },
  light: { a: '#0070C1', index: '#388A34', emulation: '#AF00DB' },
};
const DIM = 0.35;

// Returns one stripe of an icon, or nothing where its width is not known.
function stripeOf(x, color, bits) {
  if (bits == null) return '';
  return `<rect x="${x}" y="0" width="4" height="18" fill="${color}" fill-opacity="${bits === 16 ? 1 : DIM}"/>`;
}

// Returns the icon for a run's state in one theme's colors, as a data URI. It is stretched to the
// full height of the line, so that the stripes of one line meet those of the next.
function iconOf(run, colors) {
  const stripes = run.emulation
    ? stripeOf(4, colors.emulation, 16) + stripeOf(10, colors.emulation, 16)
    : stripeOf(4, colors.a, run.a) + stripeOf(10, colors.index, run.index);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 18 18" preserveAspectRatio="none">${stripes}</svg>`;
  return vscode.Uri.parse(`data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`);
}

// Returns the colors of the theme in use.
function themeColors() {
  const kind = vscode.window.activeColorTheme.kind;
  return kind === vscode.ColorThemeKind.Light || kind === vscode.ColorThemeKind.HighContrastLight
    ? COLORS.light
    : COLORS.dark;
}

// Returns a color at the strength its stripe is drawn with for a width, as a CSS hex color.
function strengthOf(color, bits) {
  return bits === 16 ? color : color + Math.round(DIM * 255).toString(16).padStart(2, '0');
}

// Returns the tooltip of the status bar items, which is the key to the stripes.
function keyTooltip() {
  return new vscode.MarkdownString([
    '**Width stripes** beside the line numbers show the 65816 register widths on each line.',
    '',
    '- The left stripe is A, and the right stripe is X and Y.',
    '- A bright stripe is 16 bits, and a dim one is 8 bits.',
    '- Two purple stripes are emulation mode, where every register is 8 bits.',
    '- A register with no stripe has a width nt65 does not know.',
    '',
    'A line shows the widths its own instruction runs with, so a `rep` or `sep` shows the widths before it.',
  ].join('\n'));
}

// Returns the key that names a run's state, which says which decoration type draws it.
function keyOf(run) {
  return run.emulation ? 'emulation' : `a${run.a ?? '?'}i${run.index ?? '?'}`;
}

class Widths {
  constructor(client) {
    this.requests = new Requests(client);

    // One decoration type per state, made the first time a state is drawn. There are at most
    // nine: each width 8, 16 or unknown, less both unknown, and emulation.
    this.types = new Map();

    this.timer = undefined;

    // The editor shown, the document version its icons are for, and the runs drawn.
    this.shown = undefined;

    // The caret line's state in the status bar, as a stripe and a label for A and then for X and
    // Y. Each stripe is an item of its own, since an item has one color and a dim 8-bit color
    // would make the label hard to read. The items sit just left of the caret's position.
    const tooltip = keyTooltip();
    this.status = [104, 103, 102, 101].map(priority => {
      const item = vscode.window.createStatusBarItem('nt65.widths.' + priority, vscode.StatusBarAlignment.Right, priority);
      item.name = 'nt65 Register Widths';
      item.tooltip = tooltip;
      return item;
    });
  }

  get enabled() {
    return vscode.workspace.getConfiguration('nt65').get('widths.enabled', true);
  }

  typeOf(run) {
    const key = keyOf(run);
    let type = this.types.get(key);
    if (!type) {
      type = vscode.window.createTextEditorDecorationType({
        dark: { gutterIconPath: iconOf(run, COLORS.dark), gutterIconSize: '100% 100%' },
        light: { gutterIconPath: iconOf(run, COLORS.light), gutterIconSize: '100% 100%' },
      });
      this.types.set(key, type);
    }
    return type;
  }

  // Asks for the widths of the editor's document after `delay`, and draws them when they come.
  schedule(editor, delay = 0) {
    clearTimeout(this.timer);
    if (!this.enabled || !showsSource(editor)) {
      this.clear();
      return;
    }
    if (this.shown && this.shown.editor !== editor) this.clear();
    this.timer = setTimeout(() => this.ask(editor), delay);
  }

  async ask(editor) {
    const document = editor.document;
    const version = document.version;
    if (this.shown && this.shown.editor === editor && this.shown.version === version) return;
    const result = await this.requests.send('nt65/widths', { textDocument: { uri: document.uri.toString() } });

    // An answer that arrives after the text has changed, or for an editor no longer active, is
    // dropped.
    if (result === undefined || document.version !== version || vscode.window.activeTextEditor !== editor) return;
    this.shown = { editor, version, runs: result ? result.runs : [] };
    this.render(editor, this.shown.runs);
    this.showCaret(editor);
  }

  // Draws each run with the decoration type of its state, and takes the icons of every other state
  // away.
  render(editor, runs) {
    const ranges = new Map();
    for (const run of runs) {
      const type = this.typeOf(run);
      if (!ranges.has(type)) ranges.set(type, []);
      const last = Math.min(run.last, editor.document.lineCount - 1);
      if (run.first > last) continue;
      ranges.get(type).push(new vscode.Range(run.first, 0, last, 0));
    }
    for (const type of this.types.values()) editor.setDecorations(type, ranges.get(type) || []);
  }

  // Shows the caret line's widths in the status bar, or hides them where the line has none.
  showCaret(editor) {
    const run = this.shown && this.shown.editor === editor
      ? this.shown.runs.find(each => each.first <= editor.selection.active.line && editor.selection.active.line <= each.last)
      : undefined;
    if (!run) {
      for (const item of this.status) item.hide();
      return;
    }
    const colors = themeColors();
    const [aStripe, aLabel, indexStripe, indexLabel] = this.status;
    const show = (stripe, label, color, text) => {
      stripe.text = '▌';
      stripe.color = color;
      label.text = text;
      stripe.show();
      label.show();
    };
    if (run.emulation) {
      show(aStripe, aLabel, colors.emulation, 'emulation');
      indexStripe.hide();
      indexLabel.hide();
      return;
    }

    // A register with no stripe is shown with none, so that the bar reads like the gutter.
    show(aStripe, aLabel, run.a == null ? 'transparent' : strengthOf(colors.a, run.a), `A ${run.a ?? '?'}`);
    show(indexStripe, indexLabel, run.index == null ? 'transparent' : strengthOf(colors.index, run.index), `XY ${run.index ?? '?'}`);
  }

  clear() {
    clearTimeout(this.timer);
    this.requests.cancel();
    if (this.shown) {
      for (const type of this.types.values()) this.shown.editor.setDecorations(type, []);
    }
    this.shown = undefined;
    for (const item of this.status) item.hide();
  }

  dispose() {
    this.clear();
    for (const type of this.types.values()) type.dispose();
    this.types.clear();
    for (const item of this.status) item.dispose();
  }
}

// Everything the width stripes need, registered once.
function register(context, client) {
  const widths = new Widths(client);
  context.subscriptions.push(
    vscode.window.onDidChangeActiveTextEditor(editor => {
      widths.clear();
      if (editor) widths.schedule(editor);
    }),
    vscode.window.onDidChangeTextEditorSelection(event => widths.showCaret(event.textEditor)),
    vscode.window.onDidChangeActiveColorTheme(() => {
      if (vscode.window.activeTextEditor) widths.showCaret(vscode.window.activeTextEditor);
    }),
    vscode.workspace.onDidChangeTextDocument(event => {
      const editor = vscode.window.activeTextEditor;
      if (editor && event.document === editor.document && event.contentChanges.length > 0) {
        widths.schedule(editor, EDIT_DELAY);
      }
    }),
    vscode.workspace.onDidChangeConfiguration(event => {
      if (!event.affectsConfiguration('nt65.widths.enabled')) return;
      widths.clear();
      if (vscode.window.activeTextEditor) widths.schedule(vscode.window.activeTextEditor);
    }),
    vscode.commands.registerCommand('nt65.toggleWidths', () => vscode.workspace.getConfiguration('nt65')
      .update('widths.enabled', !widths.enabled, vscode.ConfigurationTarget.Global)),

    // The editor open at start-up gets its stripes once the server runs.
    client.onDidChangeState(() => {
      if (client.isRunning() && vscode.window.activeTextEditor) widths.schedule(vscode.window.activeTextEditor);
    }),
    widths);
}

module.exports = { register };
