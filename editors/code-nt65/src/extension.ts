import * as fs from 'fs';
import * as path from 'path';

import { ExtensionContext, window, workspace } from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { Executable } from 'vscode-languageclient/node';

const serverPathSetting = 'nt65.server.path';
const bundledServerPath = 'server/nt65srv.dll';
const developmentServerPath = '../../src/Norristown.Server/bin/Debug/net10.0/nt65srv.dll';

let client: LanguageClient | undefined;

export async function activate(context: ExtensionContext) {
	// Locate the language server executable
	const program = resolveServerProgram(context);

	if (program === undefined) {
		window.showErrorMessage('No nt65 language server found.');
		return;
	}

	if (path.isAbsolute(program) && !fs.existsSync(program)) {
		window.showErrorMessage(`No nt65 language server at ${program}.`);
		return;
	}

	// Initialize the language server
	const server = resolveServerExecutable(program);
	const serverOptions: ServerOptions = { run: server, debug: server };

	// Initialize the language client
	const clientOptions: LanguageClientOptions = {
		outputChannelName: 'nt65 (Norristown)'
	};

	client = new LanguageClient('nt65', 'nt65 (Norristown)', serverOptions, clientOptions);
	context.subscriptions.push(client);

	// Start the server
	const description = [server.command, ...(server.args ?? [])].join(' ');
	client.outputChannel.appendLine(`[nt65] Starting server: ${description}`);

	try {
		await client.start();
	} catch (error) {
		const reason = error instanceof Error ? error.message : String(error);
		client.outputChannel.appendLine(`[nt65] Server failed to start: ${reason}`);
		window.showErrorMessage(
			`The nt65 language server failed to start: ${reason}. ` +
			`See the "nt65 (Norristown) output channel.`
		);
		return;
	}
	client.outputChannel.appendLine('[nt65] Server ready.');
}

export async function deactivate() {
	await client?.stop();
	client = undefined;
}

function resolveServerProgram(context: ExtensionContext): string | undefined {
	const configured = workspace.getConfiguration().get<string>(serverPathSetting)?.trim();
	if (configured !== undefined && configured.length > 0) {
		if (path.basename(configured) === configured) {
			return configured;
		}

		return path.isAbsolute(configured) ? configured : path.resolve(context.extensionPath, configured);
	}

	return [developmentServerPath, bundledServerPath]
		.map(candidate => path.resolve(context.extensionPath, candidate))
		.find(candidate => fs.existsSync(candidate));
}

function resolveServerExecutable(program: string): Executable {
	return path.extname(program).toLowerCase() === '.dll' ?
		{ command: 'dotnet', args: [program] } :
		{ command: program };
}