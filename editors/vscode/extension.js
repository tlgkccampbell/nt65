// The VS Code client for nt65: it starts the Norristown language server over stdio, tells it
// what changes on disk, and lets the programmer choose the configuration it analyzes.
const fs = require('fs');
const path = require('path');
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');
const views = require('./views');

let client;

// Which server to start: the executable `nt65.server.path` names; in a development host, this
// repository's Debug build, which the launch task has just built; otherwise the server packaged
// with the extension, run on the installed .NET. Running the package script also leaves a
// packaged server in a development checkout, and that copy is only as new as the last package,
// which is why the Debug build is preferred there.
function serverOptions(context) {
  const configured = vscode.workspace.getConfiguration('nt65').get('server.path');
  if (configured) return { command: configured };
  const exe = process.platform === 'win32' ? 'Norristown.LanguageServer.exe' : 'Norristown.LanguageServer';
  const debug = context.asAbsolutePath(
    path.join('..', '..', 'src', 'Norristown.LanguageServer', 'bin', 'Debug', 'net10.0', exe));
  const packaged = context.asAbsolutePath(path.join('server', 'Norristown.LanguageServer.dll'));
  if (context.extensionMode === vscode.ExtensionMode.Development && fs.existsSync(debug)) return { command: debug };
  if (fs.existsSync(packaged)) return { command: 'dotnet', args: [packaged] };
  return { command: debug };
}

// The nt65 command the build tasks run: the configured one; in a development host, the Debug
// build of this repository; else the command on the path, which is where `dotnet tool install`
// puts it. The extension bundles only the language server, not the command, so there is no
// packaged fallback.
function cliCommand(context) {
  const configured = vscode.workspace.getConfiguration('nt65').get('cli.path');
  if (configured) return configured;
  const exe = process.platform === 'win32' ? 'nt65.exe' : 'nt65';
  const debug = context.asAbsolutePath(
    path.join('..', '..', 'src', 'Norristown.Cli', 'bin', 'Debug', 'net10.0', exe));
  if (context.extensionMode === vscode.ExtensionMode.Development && fs.existsSync(debug)) return debug;
  return 'nt65';
}

// One build task. It runs in the workspace folder rather than beside the project file, because
// what the command prints is relative to where it ran and the `$nt65` matcher resolves those
// against the folder.
function buildTask(context, folder, definition, name) {
  const args = ['build'];
  if (definition.project) args.push('--project', definition.project);
  if (definition.config) args.push('--config', definition.config);
  const task = new vscode.Task(definition, folder, name, 'nt65',
    new vscode.ShellExecution(cliCommand(context), args, { cwd: folder.uri.fsPath }), '$nt65');
  task.group = vscode.TaskGroup.Build;
  return task;
}

// The configurations a project file names, or none when it cannot be read. nt65 allows comments
// and trailing commas where JSON.parse does not, so a file it accepts may not parse here; the
// project's own settings are still offered, and the file's own diagnostics say what is wrong.
function configurationsIn(text) {
  try {
    return Object.keys(JSON.parse(text).configurations || {});
  } catch {
    return [];
  }
}

// A task for each project file in the workspace: one for the project's own settings and one for
// each named configuration.
async function buildTasks(context) {
  const tasks = [];
  for (const folder of vscode.workspace.workspaceFolders || []) {
    const found = await vscode.workspace.findFiles(
      new vscode.RelativePattern(folder, '**/nt65.json'), '**/node_modules/**');
    for (const file of found.sort((a, b) => a.fsPath.localeCompare(b.fsPath))) {
      const directory = path.relative(folder.uri.fsPath, path.dirname(file.fsPath)).split(path.sep).join('/');
      const where = directory ? `${directory}: ` : '';
      const definition = directory ? { type: 'nt65', project: directory } : { type: 'nt65' };
      tasks.push(buildTask(context, folder, definition, `${where}build`));
      for (const name of configurationsIn(fs.readFileSync(file.fsPath, 'utf8'))) {
        tasks.push(buildTask(context, folder, { ...definition, config: name }, `${where}build ${name}`));
      }
    }
  }
  return tasks;
}

// Shows the active configuration, and chooses another when clicked.
function statusItem(context) {
  const item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left);
  item.command = 'nt65.selectConfiguration';
  item.tooltip = 'The nt65 configuration the editor analyzes the program as';
  const show = () => {
    const active = vscode.workspace.getConfiguration('nt65').get('configuration');
    item.text = `nt65: ${active || 'default'}`;
    item.show();
  };
  show();
  context.subscriptions.push(item, vscode.workspace.onDidChangeConfiguration(e => {
    if (e.affectsConfiguration('nt65.configuration')) show();
  }));
}

// The inlay-hint settings, in the shape the server reads. They are sent with the `initialize`
// request, and again, with the rest of the `nt65` settings, whenever they change.
function hintSettings() {
  const nt65 = vscode.workspace.getConfiguration('nt65');
  return {
    stateChanges: nt65.get('inlayHints.stateChanges'),
    longBranches: nt65.get('inlayHints.longBranches'),
    impliedValues: nt65.get('inlayHints.impliedValues'),
    parameterNames: nt65.get('inlayHints.parameterNames'),
    cycles: nt65.get('inlayHints.cycles'),
  };
}

// A status bar item that shows whether cycle-count inlay hints are on, and toggles them when
// clicked. The toggle lasts as long as the server runs and is not saved to settings, because
// cycle counts are wanted while a routine is being timed and not afterwards; the server holds
// the state and this item shows it. Once toggled, the item stops following the setting.
function cycleCounts(context) {
  const item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left);
  item.command = 'nt65.toggleCycleCounts';
  item.tooltip = 'Whether nt65 shows what each instruction costs, in the line';
  let on = vscode.workspace.getConfiguration('nt65').get('inlayHints.cycles') === true;
  let thrown = false;
  const show = () => {
    item.text = `nt65: cycles ${on ? 'on' : 'off'}`;
    item.show();
  };
  show();
  context.subscriptions.push(item,
    vscode.commands.registerCommand('nt65.toggleCycleCounts', async () => {
      on = await client.sendRequest('nt65/toggleCycleHints', {});
      thrown = true;
      show();
    }),
    vscode.workspace.onDidChangeConfiguration(e => {
      if (!thrown && e.affectsConfiguration('nt65.inlayHints.cycles')) {
        on = vscode.workspace.getConfiguration('nt65').get('inlayHints.cycles') === true;
        show();
      }
    }));
}

async function selectConfiguration() {
  const names = await client.sendRequest('nt65/configurations', {});
  const own = 'Each project\'s default';
  const picked = await vscode.window.showQuickPick([own, ...names], { placeHolder: 'Configuration to analyze as' });
  if (picked === undefined) return;
  await vscode.workspace.getConfiguration('nt65')
    .update('configuration', picked === own ? '' : picked, vscode.ConfigurationTarget.Workspace);
}

// Starts a rename on a name the server had to make up and the programmer should choose, such as
// the name of a routine that a refactoring has just extracted a few lines into. The server
// sends the name's position after its edit has been applied, so the position is in the
// document as the editor now holds it.
async function renameAt(uri, line, character) {
  const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(uri));
  const editor = await vscode.window.showTextDocument(document, { preserveFocus: false });
  const position = new vscode.Position(line, character);
  editor.selection = new vscode.Selection(position, position);
  editor.revealRange(new vscode.Range(position, position));
  await vscode.commands.executeCommand('editor.action.rename', [document.uri, position]);
}

// Moving a source rewrites the `files` entries of the nt65.json files that name it. Only nt65
// sources are synchronized with the server, so it computes those edits against nt65.json as saved.
// Offsets into a file with unsaved changes would land in the wrong place, so the edits to such a
// file are dropped, and the programmer is told to update it by hand.
async function keepUnsavedProjects(event, next) {
  const edit = await next(event);
  if (!edit) return edit;
  const unsaved = new Set(vscode.workspace.textDocuments
    .filter(document => document.isDirty && path.basename(document.uri.fsPath) === 'nt65.json')
    .map(document => document.uri.toString()));
  const kept = new vscode.WorkspaceEdit();
  for (const [uri, edits] of edit.entries()) {
    if (!unsaved.has(uri.toString())) {
      kept.set(uri, edits);
      continue;
    }
    vscode.window.showWarningMessage(
      `nt65: ${vscode.workspace.asRelativePath(uri)} has unsaved changes, so its \`files\` were left `
      + 'unchanged. Save it and edit `files` by hand if the moved file should still be built.');
  }
  return kept;
}

async function activate(context) {
  // The active configuration, the hints to show and the line length go to the server when it
  // starts, and again whenever the `nt65` settings change. The client watches the kinds of file every program is made of: the sources
  // and the project files. Which binaries an `.incbin` reads depends on the program, so the
  // server registers its own watch for those once it has read the program.
  client = new LanguageClient('nt65', 'nt65',
    serverOptions(context),
    {
      // Only files are the program's. A module that comes with nt65 is shown under an `nt65:`
      // URI, read-only, and is not a document the server is told about.
      documentSelector: [{ scheme: 'file', language: 'nt65' }, { scheme: 'untitled', language: 'nt65' }],
      middleware: { ...views.middleware, workspace: { willRenameFiles: keepUnsavedProjects } },
      initializationOptions: {
        configuration: vscode.workspace.getConfiguration('nt65').get('configuration'),
        inlayHints: hintSettings(),
        lineLength: vscode.workspace.getConfiguration('nt65').get('lineLength'),
      },
      synchronize: {
        configurationSection: 'nt65',
        fileEvents: [
          vscode.workspace.createFileSystemWatcher('**/*.nt65'),
          vscode.workspace.createFileSystemWatcher('**/nt65.json'),
          // A folder that is renamed or deleted is reported once, under the folder's own path,
          // which neither pattern above matches. Every path is watched for coming and going,
          // though not for changing, so that the server hears of such a folder.
          vscode.workspace.createFileSystemWatcher('**/*', false, true, false),
        ],
      },
    });
  // `nt65.rename` is invoked by the server, never typed by the user, so it is registered here
  // but not contributed in package.json, and the command palette does not list it.
  context.subscriptions.push(
    vscode.commands.registerCommand('nt65.selectConfiguration', selectConfiguration),
    vscode.commands.registerCommand('nt65.rename', renameAt),
    vscode.commands.registerCommand('nt65.restartServer', () => client.restart()),
    // A definition or reference that leads into a module that comes with nt65 opens its source,
    // which no file holds, so the server supplies the text.
    vscode.workspace.registerTextDocumentContentProvider('nt65', {
      provideTextDocumentContent: uri =>
        client.sendRequest('nt65/standardModule', { textDocument: { uri: uri.toString() } }),
    }),
    vscode.tasks.registerTaskProvider('nt65', {
      provideTasks: () => buildTasks(context),

      // A task written by hand in tasks.json arrives with its definition and nothing to run;
      // this gives it the same command the offered ones have.
      resolveTask(task) {
        const folder = task.scope && task.scope.uri ? task.scope : (vscode.workspace.workspaceFolders || [])[0];
        return folder ? buildTask(context, folder, task.definition, task.name) : undefined;
      },
    }));
  statusItem(context);
  cycleCounts(context);
  views.register(context, client);
  await client.start();
}

function deactivate() {
  return client?.stop();
}

module.exports = { activate, deactivate };
