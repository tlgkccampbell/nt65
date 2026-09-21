// The two read-only views that open beside a source: what the file became, and what a macro
// call becomes. Both are the server's answer shown as it came; nothing here works anything out
// about the program, so the editor and any other client see the same thing.
const vscode = require('vscode');

const OUTPUT = 'nt65-output';
const EXPANSION = 'nt65-expansion';

// What each open view is showing, by the URI of the view's own document. An output view keeps
// the source it is of, so that an edit anywhere can have it ask again.
const shown = new Map();

// The lines a caret points at, in either direction. It is a line highlight and not a selection:
// a view that moved the caret would fight the typing in the window it was opened from.
const marked = vscode.window.createTextEditorDecorationType({
  isWholeLine: true,
  backgroundColor: new vscode.ThemeColor('editor.rangeHighlightBackground'),
});

// A virtual document that holds text and nothing else. The text is replaced in place, so the
// window keeps its scroll and its place in the history when the program settles.
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

// The view's own document URI: the scheme, a path that names the window, and the source it is
// of, so that one source has one view and reopening it reuses the window.
function addressed(scheme, path, source) {
  return vscode.Uri.from({ scheme, path, query: source });
}

// The runs of output lines one source line became, as the server sends them.
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

// Asks the server what a file became, and holds the answer. A file the program does not hold
// has no output, which is the one thing worth saying out loud.
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
    vscode.window.showInformationMessage('nt65: open an nt65 file to see what it becomes.');
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

// The caret moved in a source: its lines are marked in the view of it and scrolled to.
function followSource(editor) {
  const source = editor.document.uri.toString();
  for (const [uri, view] of shown) {
    if (view.kind !== OUTPUT || view.source !== source) continue;
    const ranges = runsFor(view, editor.selection.active.line)
      .map(run => new vscode.Range(run.first, 0, run.last, 0));
    for (const opened of editorsOf(uri)) mark(opened, ranges, true);
  }
}

// The caret moved in a view: the line it came from is marked in the source, where the source is
// on the screen. It is not scrolled to, because the source is where the typing happens.
function followView(editor) {
  const view = shown.get(editor.document.uri.toString());
  if (!view || view.kind !== OUTPUT) return;
  const line = sourceLineOf(view, editor.selection.active.line);
  const ranges = line === undefined ? [] : [new vscode.Range(line, 0, line, 0)];
  for (const opened of editorsOf(view.source)) mark(opened, ranges, false);
}

// What a macro call becomes, in a view of its own. `into` says which call to write out further
// at each level: one level at a time, because a fully written out nest of macros is unreadable.
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

// A view of an expansion offers, on each call left as a call, the way to see that one too, and
// once at the top the way to see all of them. A lens is the only thing a read-only document can
// carry, and it is what says the summary as well.
function lenses(document) {
  const view = shown.get(document.uri.toString());
  if (!view || view.kind !== EXPANSION) return [];
  const top = new vscode.Range(0, 0, 0, 0);
  const found = [{ range: top, command: { title: view.summary, command: '' } }];
  if (view.links.length > 0) {
    found.push({
      range: top,
      command: {
        title: 'Expand all',
        command: 'nt65.showExpansion',
        arguments: [view.at.uri, view.at.position.line, view.at.position.character, [], true],
      },
    });
  }
  for (const link of view.links) {
    found.push({
      range: new vscode.Range(link.line, 0, link.line, 0),
      command: {
        title: `Show expansion of ${link.text}`,
        command: 'nt65.showExpansion',
        arguments: [view.at.uri, view.at.position.line, view.at.position.character, link.into, false],
      },
    });
  }
  return found;
}

// Everything the two views need, registered once. `client` is the language client, which is
// asked for the text and told nothing.
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

    // A view of what a file became follows the program and not the caret, so it asks again when
    // the server says the program has settled, which is the wait the squiggles come on.
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

// A hover's *Show expansion* is a link to a command, and VS Code runs one from a hover only
// where the hover says which commands it means. It is the one command named, so a server
// writing anything else into a hover cannot have the editor run it.
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
