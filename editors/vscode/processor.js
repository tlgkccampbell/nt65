// The Processor view: what the analysis knows of the processor on the caret's line, which is the
// rows the instruction hover shows below its rule, kept in a view that follows the caret. That is
// the widths and the mode, D and B, each register, the flags and the stack. The server works the
// rows out (`nt65/processor`), and this file only lists them.
//
// The stack is known relative to the routine's entry. The view's last row chooses one call to the
// routine, and the stack rows then go on into that caller's pushes. That is true only along the
// path through the call, so it is a choice the reader makes for each routine, never the default.
const vscode = require('vscode');
const { showsSource, Requests } = require('./documents');

const VIEW = 'nt65.processor';

// How long the caret has to rest on a line before the server is asked about it.
const DELAY = 120;

// How long the text has to rest after an edit before the server is asked again.
const EDIT_DELAY = 300;

// Returns the key the chosen caller of a routine is kept under.
function routineKey(uri, routine) {
  return `${uri}#${routine}`;
}

// Returns a line number as people count them, from a zero-based one.
function lineOf(location) {
  return location.range.start.line + 1;
}

// Returns a row's location as VS Code takes it.
function asLocation(location) {
  const range = location.range;
  return {
    uri: vscode.Uri.parse(location.uri),
    range: new vscode.Range(range.start.line, range.start.character, range.end.line, range.end.character),
  };
}

class Processor {
  constructor(client) {
    this.requests = new Requests(client);
    this.changed = new vscode.EventEmitter();
    this.onDidChangeTreeData = this.changed.event;

    // The last answer, the editor and document it is for, and the caller chosen for each routine.
    this.result = null;
    this.uri = undefined;
    this.chosen = new Map();

    this.timer = undefined;
    this.view = vscode.window.createTreeView(VIEW, { treeDataProvider: this });
  }

  // Asks about the active editor's caret after `delay`, when the view can be seen.
  schedule(delay = DELAY) {
    clearTimeout(this.timer);
    if (!this.view.visible) return;
    const editor = vscode.window.activeTextEditor;
    if (!showsSource(editor)) {
      this.show(null, undefined);
      return;
    }
    this.timer = setTimeout(() => this.ask(editor), delay);
  }

  async ask(editor) {
    const document = editor.document;
    const version = document.version;
    const uri = document.uri.toString();
    const position = editor.selection.active;
    const result = await this.requests.send('nt65/processor', {
      textDocument: { uri },
      position: { line: position.line, character: position.character },
      callers: [...this.chosen.values()],
    });

    // An answer that arrives after the text has changed is dropped.
    if (result === undefined || document.version !== version) return;
    this.show(result, uri);
  }

  show(result, uri) {
    this.result = result;
    this.uri = uri;
    this.view.description = result ? `${result.routine} · line ${result.line + 1}` : undefined;
    this.changed.fire();
  }

  // Asks which call to see the caret's routine entered from, and asks the server again.
  async chooseCaller() {
    const result = this.result;
    if (!result || result.callers.length === 0) {
      vscode.window.showInformationMessage('nt65: The routine at the caret has no calls in the program to choose from.');
      return;
    }
    const any = { label: 'Any caller', description: 'the stack ends where the routine was entered', caller: null };
    const picks = [any, ...result.callers.map(caller => ({
      label: caller.name,
      description: `${vscode.workspace.asRelativePath(vscode.Uri.parse(caller.at.uri))}:${lineOf(caller.at)}`,
      caller: caller.at,
    }))];
    const picked = await vscode.window.showQuickPick(picks, { placeHolder: `Show ${result.routine} as called from` });
    if (!picked) return;
    const key = routineKey(this.uri, result.routine);
    if (picked.caller) this.chosen.set(key, picked.caller);
    else this.chosen.delete(key);
    const editor = vscode.window.activeTextEditor;
    if (showsSource(editor)) this.ask(editor);
  }

  getChildren(element) {
    if (element) return element.rows || [];
    if (!this.result) return [];
    const rows = [...this.result.rows];
    if (this.result.callers.length > 0) rows.push({ caller: true });
    return rows;
  }

  getTreeItem(element) {
    if (element.caller) {
      const chosen = this.result && this.result.caller;
      const name = chosen && this.result.callers.find(each => same(each.at, chosen));
      const item = new vscode.TreeItem('as called from', vscode.TreeItemCollapsibleState.None);
      item.description = name ? `${name.name} · line ${lineOf(chosen)}` : 'any caller';
      item.tooltip = 'Choose one call to the routine, and the stack goes on into what that caller had pushed.';
      item.iconPath = new vscode.ThemeIcon('call-incoming');
      item.command = { command: 'nt65.chooseCaller', title: 'Choose Caller' };
      return item;
    }
    const nested = element.rows && element.rows.length > 0;
    const item = new vscode.TreeItem(element.key,
      nested ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.None);
    item.description = element.detail ? `${element.value}  ·  ${element.detail}` : element.value;
    item.tooltip = element.detail ? `${element.key}  ${element.value}\n${element.detail}` : `${element.key}  ${element.value}`;
    if (element.target) {
      const target = asLocation(element.target);
      item.command = {
        command: 'vscode.open',
        title: 'Go to Line',
        arguments: [target.uri, { selection: target.range, preserveFocus: false }],
      };
    }
    return item;
  }

  dispose() {
    clearTimeout(this.timer);
    this.requests.cancel();
    this.view.dispose();
    this.changed.dispose();
  }
}

// Checks whether two locations name the same call, which is one line of one document.
function same(a, b) {
  return !!a && !!b && a.uri === b.uri && a.range.start.line === b.range.start.line;
}

// Everything the Processor view needs, registered once.
function register(context, client) {
  const processor = new Processor(client);
  context.subscriptions.push(
    processor,
    processor.view.onDidChangeVisibility(() => processor.schedule(0)),
    vscode.window.onDidChangeActiveTextEditor(() => processor.schedule(0)),
    vscode.window.onDidChangeTextEditorSelection(event => {
      if (event.textEditor === vscode.window.activeTextEditor) processor.schedule();
    }),
    vscode.workspace.onDidChangeTextDocument(event => {
      const editor = vscode.window.activeTextEditor;
      if (editor && event.document === editor.document && event.contentChanges.length > 0) processor.schedule(EDIT_DELAY);
    }),
    vscode.commands.registerCommand('nt65.chooseCaller', () => processor.chooseCaller()),

    // The view open at start-up fills once the server runs.
    client.onDidChangeState(() => {
      if (client.isRunning()) processor.schedule(0);
    }));
}

module.exports = { register };
