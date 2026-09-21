// The VS Code client for nt65: it starts the Norristown language server over stdio, tells it
// what changes on disk, and lets the programmer choose the configuration it analyzes.
const fs = require('fs');
const path = require('path');
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

// The server: the configured executable; in a development host, the Debug build of this
// repository, which the launch task has just built; else the one packaged with the extension,
// which runs on the installed .NET. A package script run leaves a packaged server beside a
// development copy too, and that one is only as new as the last package.
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
// puts it. The extension carries a language server, not a command, so there is nothing else.
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

// Which kinds of hint the editor shows, as the server reads them. They go over with the
// `initialize` request and again, with the rest of the `nt65` settings, whenever they change.
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

// Shows whether the cycle counts are in the lines, and switches them when clicked. They are
// switched for as long as the server runs rather than saved: they are wanted while a routine is
// being timed and not for the rest of the week, so the server holds which it is and this shows it.
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
  const own = 'The project\'s own settings';
  const picked = await vscode.window.showQuickPick([own, ...names], { placeHolder: 'Configuration to analyze as' });
  if (picked === undefined) return;
  await vscode.workspace.getConfiguration('nt65')
    .update('configuration', picked === own ? '' : picked, vscode.ConfigurationTarget.Workspace);
}

// Starts a rename on a name the server had to write and the programmer has to give, such as
// the routine a few lines were lifted into. The server sends where the name is once its change
// has been applied, which is the file as the editor now holds it.
async function renameAt(uri, line, character) {
  const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(uri));
  const editor = await vscode.window.showTextDocument(document, { preserveFocus: false });
  const position = new vscode.Position(line, character);
  editor.selection = new vscode.Selection(position, position);
  editor.revealRange(new vscode.Range(position, position));
  await vscode.commands.executeCommand('editor.action.rename', [document.uri, position]);
}

async function activate(context) {
  // The active configuration goes to the server when it starts, and again whenever the `nt65`
  // settings change. Every file change is sent: a project file, a source no one has open, or a
  // file an `.incbin` names may each change what is wrong, and the server knows which it reads.
  client = new LanguageClient('nt65', 'nt65',
    serverOptions(context),
    {
      documentSelector: [{ language: 'nt65' }],
      initializationOptions: {
        configuration: vscode.workspace.getConfiguration('nt65').get('configuration'),
        inlayHints: hintSettings(),
      },
      synchronize: {
        configurationSection: 'nt65',
        fileEvents: vscode.workspace.createFileSystemWatcher('**/*'),
      },
    });
  // `nt65.rename` is the server's to run and nobody's to type, so it is registered without
  // being contributed: the command palette has nothing to offer for it.
  context.subscriptions.push(
    vscode.commands.registerCommand('nt65.selectConfiguration', selectConfiguration),
    vscode.commands.registerCommand('nt65.rename', renameAt),
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
  await client.start();
}

function deactivate() {
  return client?.stop();
}

module.exports = { activate, deactivate };
