// The VS Code client for nt65: it starts the Norristown language server over stdio, tells it
// what changes on disk, and lets the programmer choose the configuration it analyzes.
const fs = require('fs');
const path = require('path');
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

// The server: the configured executable, else the one packaged with the extension, which runs
// on the installed .NET, else the Debug build of this repository when the extension runs from it.
function serverOptions(context) {
  const configured = vscode.workspace.getConfiguration('nt65').get('server.path');
  if (configured) return { command: configured };
  const packaged = context.asAbsolutePath(path.join('server', 'Norristown.LanguageServer.dll'));
  if (fs.existsSync(packaged)) return { command: 'dotnet', args: [packaged] };
  const exe = process.platform === 'win32' ? 'Norristown.LanguageServer.exe' : 'Norristown.LanguageServer';
  return {
    command: context.asAbsolutePath(
      path.join('..', '..', 'src', 'Norristown.LanguageServer', 'bin', 'Debug', 'net10.0', exe)),
  };
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
  context.subscriptions.push(vscode.commands.registerCommand('nt65.selectConfiguration', selectConfiguration));
  statusItem(context);
  await client.start();
}

function deactivate() {
  return client?.stop();
}

module.exports = { activate, deactivate };
