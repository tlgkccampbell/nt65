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
      initializationOptions: { configuration: vscode.workspace.getConfiguration('nt65').get('configuration') },
      synchronize: {
        configurationSection: 'nt65',
        fileEvents: vscode.workspace.createFileSystemWatcher('**/*'),
      },
    });
  // `nt65.rename` is the server's to run and nobody's to type, so it is registered without
  // being contributed: the command palette has nothing to offer for it.
  context.subscriptions.push(
    vscode.commands.registerCommand('nt65.selectConfiguration', selectConfiguration),
    vscode.commands.registerCommand('nt65.rename', renameAt));
  statusItem(context);
  await client.start();
}

function deactivate() {
  return client?.stop();
}

module.exports = { activate, deactivate };
