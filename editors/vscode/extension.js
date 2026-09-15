// The VS Code client for nt65: it starts the Norristown language server over stdio and nothing else.
const path = require('path');
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

function serverCommand(context) {
  const configured = vscode.workspace.getConfiguration('nt65').get('server.path');
  if (configured) return configured;
  // Development default: the Debug build of the server in this repository.
  const exe = process.platform === 'win32' ? 'Norristown.LanguageServer.exe' : 'Norristown.LanguageServer';
  return context.asAbsolutePath(
    path.join('..', '..', 'src', 'Norristown.LanguageServer', 'bin', 'Debug', 'net10.0', exe));
}

async function activate(context) {
  client = new LanguageClient('nt65', 'nt65',
    { command: serverCommand(context) },
    { documentSelector: [{ language: 'nt65' }] });
  await client.start();
}

function deactivate() {
  return client?.stop();
}

module.exports = { activate, deactivate };
