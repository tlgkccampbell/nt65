// The two read-only views that open beside a source: the ca65 output the file compiles to, and
// the expansion of a macro call. Both show the server's response unchanged; nothing here works
// anything out about the program, so VS Code and any other client show the same thing.
const vscode = require('vscode');

const OUTPUT = 'nt65-output';
const EXPANSION = 'nt65-expansion';

// What each open view is showing, keyed by the URI of the view's own document. An output view
// records its source file, so that after an edit anywhere its output can be requested again.
const shown = new Map();

// Highlights the lines that match the caret's line, from source to output or output to source.
// It is a line highlight and not a selection, because moving the caret in the source window
// from the view would interfere with typing there.
const marked = vscode.window.createTextEditorDecorationType({
  isWholeLine: true,
  backgroundColor: new vscode.ThemeColor('editor.rangeHighlightBackground'),
});

// A virtual document that holds text and nothing else. The text is replaced in place, so the
// window keeps its scroll position and its place in the history when the view is refreshed.
class Held {
  constructor() {
    this.changed = new vscode.EventEmitter();
    this.onDidChange = this.changed.event;
  }

  provideTextDocumentContent(uri) {
    const view = shown.get(uri.toString());
    return view ? view.text : '';
  }

  refresh(uri) {
    this.changed.fire(vscode.Uri.parse(uri));
  }
}

const held = new Held();

// The URI of a view's document: the view's scheme, a path that gives the window its title, and
// the source's URI as the query, so that one source has one view and reopening it reuses the
// window.
function addressed(scheme, path, source) {
  return vscode.Uri.from({ scheme, path: path.startsWith('/') ? path : `/${path}`, query: source });
}

// The runs of output lines that one source line produced, as the server reports them.
function runsFor(view, line) {
  return (view.lines || []).filter(run => run.source === line);
}

function sourceLineOf(view, line) {
  const run = (view.lines || []).find(item => line >= item.first && line <= item.last);
  return run ? run.source : undefined;
}

// Marks lines in an editor, and reveals them where it was asked to.
function mark(editor, ranges, reveal) {
  if (!editor) return;
  editor.setDecorations(marked, ranges);
  if (reveal && ranges.length > 0) {
    editor.revealRange(ranges[0], vscode.TextEditorRevealType.InCenterIfOutsideViewport);
  }
}

function editorsOf(uri) {
  return vscode.window.visibleTextEditors.filter(editor => editor.document.uri.toString() === uri);
}

// Asks the server for a file's output, and stores the answer. A file that is not part of the
// program has no output; this returns undefined then, so that the caller can tell the user.
async function askForOutput(client, source) {
  const answer = await client.sendRequest('nt65/output', { textDocument: { uri: source } });
  if (!answer) return undefined;
  const uri = addressed(OUTPUT, answer.path, source).toString();
  shown.set(uri, { kind: OUTPUT, source, ...answer });
  held.refresh(uri);
  return uri;
}

async function showOutputBeside(client) {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== 'nt65') {
    vscode.window.showInformationMessage('nt65: open an nt65 file to see the ca65 source it compiles to.');
    return;
  }
  const source = editor.document.uri.toString();
  const uri = await askForOutput(client, source);
  if (!uri) {
    vscode.window.showWarningMessage('nt65: this file is not part of a program, so nothing is written for it.');
    return;
  }
  const view = shown.get(uri);
  const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(uri));
  const opened = await vscode.window.showTextDocument(document, {
    viewColumn: vscode.ViewColumn.Beside,
    preserveFocus: true,
    preview: false,
  });

  // It opens on the lines the caret is already in, or past the header, which is there for ca65
  // rather than for the reader.
  const runs = runsFor(view, editor.selection.active.line);
  const at = runs.length > 0 ? runs[0].first : view.header;
  opened.revealRange(new vscode.Range(at, 0, at, 0), vscode.TextEditorRevealType.AtTop);
  followSource(editor);
}

// When the caret moves in a source, its line's output is highlighted and scrolled to in any
// output view of that source.
function followSource(editor) {
  const source = editor.document.uri.toString();
  for (const [uri, view] of shown) {
    if (view.kind !== OUTPUT || view.source !== source) continue;
    const ranges = runsFor(view, editor.selection.active.line)
      .map(run => new vscode.Range(run.first, 0, run.last, 0));
    for (const opened of editorsOf(uri)) mark(opened, ranges, true);
  }
}

// When the caret moves in an output view, the source line it came from is highlighted in any
// visible editor on that source. The source is not scrolled, because that is where the typing
// happens.
function followView(editor) {
  const view = shown.get(editor.document.uri.toString());
  if (!view || view.kind !== OUTPUT) return;
  const line = sourceLineOf(view, editor.selection.active.line);
  const ranges = line === undefined ? [] : [new vscode.Range(line, 0, line, 0)];
  for (const opened of editorsOf(view.source)) mark(opened, ranges, false);
}

// Shows the expansion of a macro call in a view of its own. `into` says which nested call to
// expand further at each level; expansion goes one level at a time, because a fully expanded
// nest of macros is unreadable. `all` expands every level at once.
async function showExpansion(client, uri, line, character, into, all) {
  const editor = vscode.window.activeTextEditor;
  const where = uri
    ? { uri, position: { line, character } }
    : editor && { uri: editor.document.uri.toString(), position: {
      line: editor.selection.active.line, character: editor.selection.active.character } };
  if (!where) return;

  const answer = await client.sendRequest('nt65/expansion', {
    textDocument: { uri: where.uri },
    position: where.position,
    into: into || [],
    all: all === true,
  });
  if (!answer) {
    vscode.window.showInformationMessage('nt65: the caret is not on a macro call.');
    return;
  }
  const address = addressed(EXPANSION, answer.title, where.uri);
  shown.set(address.toString(), { kind: EXPANSION, at: where, ...answer });
  held.refresh(address.toString());
  const document = await vscode.workspace.openTextDocument(address);
  await vscode.window.showTextDocument(document, {
    viewColumn: vscode.ViewColumn.Beside,
    preserveFocus: false,
    preview: false,
  });
  if (answer.note) vscode.window.showWarningMessage(`nt65: ${answer.note}`);
}

// The code lenses of an expansion view: on each macro call left unexpanded, one that expands
// that call too, and at the top one that expands them all. A code lens is the only clickable
// thing a read-only document can carry, so the summary line at the top is a lens as well.
function lenses(document) {
  const view = shown.get(document.uri.toString());
  if (!view || view.kind !== EXPANSION) return [];
  const at = view.at;
  const opens = (title, into, all) => ({
    title,
    command: 'nt65.showExpansion',
    arguments: [at.uri, at.position.line, at.position.character, into, all],
  });
  const top = new vscode.Range(0, 0, 0, 0);
  const found = [new vscode.CodeLens(top, { title: view.summary, command: '' })];
  if (view.links.length > 0) {
    found.push(new vscode.CodeLens(top, opens('Expand all', [], true)));
  }
  for (const link of view.links) {
    found.push(new vscode.CodeLens(
      new vscode.Range(link.line, 0, link.line, 0),
      opens(`Show expansion of ${link.text}`, link.into, false)));
  }
  return found;
}

// Everything the two views need, registered once. `client` is the language client; the views
// only request text from it and send it no notifications.
function register(context, client) {
  context.subscriptions.push(
    marked,
    vscode.workspace.registerTextDocumentContentProvider(OUTPUT, held),
    vscode.workspace.registerTextDocumentContentProvider(EXPANSION, held),
    vscode.languages.registerCodeLensProvider({ scheme: EXPANSION }, { provideCodeLenses: lenses }),
    vscode.commands.registerCommand('nt65.showOutputBeside', () => showOutputBeside(client)),
    vscode.commands.registerCommand('nt65.showExpansion',
      (uri, line, character, into, all) => showExpansion(client, uri, line, character, into, all)),
    vscode.window.onDidChangeTextEditorSelection(event => {
      if (event.textEditor.document.languageId === 'nt65') followSource(event.textEditor);
      else followView(event.textEditor);
    }),

    // An output view follows the program, not the caret, so it requests its text again when the
    // server says analysis has caught up after an edit, the same point diagnostics are published.
    client.onNotification('nt65/outputChanged', async () => {
      for (const view of [...shown.values()]) {
        if (view.kind === OUTPUT) await askForOutput(client, view.source);
      }
      for (const editor of vscode.window.visibleTextEditors) {
        if (editor.document.languageId === 'nt65') followSource(editor);
      }
    }),

    // A view nobody has open is forgotten, so that the next one opens on a fresh answer.
    vscode.workspace.onDidCloseTextDocument(document => shown.delete(document.uri.toString())));
}

// A hover's *Show expansion* is a command link, and VS Code runs a command link from a hover
// only when the hover's Markdown names that command as trusted. Only `nt65.showExpansion` is
// named, so any other command link a server writes into a hover will not run.
const middleware = {
  async provideHover(document, position, token, next) {
    const hover = await next(document, position, token);
    for (const part of (hover && hover.contents) || []) {
      if (part instanceof vscode.MarkdownString) {
        part.isTrusted = { enabledCommands: ['nt65.showExpansion'] };
      }
    }
    return hover;
  },
};

module.exports = { register, middleware };
