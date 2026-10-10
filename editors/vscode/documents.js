// What the views that follow the caret share: the test of which documents are nt65 sources the
// server analyzes, and the sending of a request that a later request makes moot.
const vscode = require('vscode');

// Checks whether a document is an nt65 source the server analyzes. A module that comes with nt65
// is shown under an `nt65:` URI, read-only, and is not one.
function isSource(document) {
  return document.languageId === 'nt65' && (document.uri.scheme === 'file' || document.uri.scheme === 'untitled');
}

// Checks whether an editor shows an nt65 source the server analyzes.
function showsSource(editor) {
  return !!editor && isSource(editor.document);
}

// Sends the requests of a view that follows the caret, one at a time. A newer question makes any
// older one moot, so sending a request cancels the one before it.
class Requests {
  constructor(client) {
    this.client = client;
    this.pending = undefined;
  }

  // Sends a request and returns its answer. The answer is null for a request that failed, and
  // undefined for one that a later request or `cancel` superseded while it waited, which the
  // caller drops.
  async send(method, params) {
    if (this.pending) this.pending.cancel();
    const pending = new vscode.CancellationTokenSource();
    this.pending = pending;
    let result;
    try {
      result = await this.client.sendRequest(method, params, pending.token);
    } catch {
      result = null;
    }
    return pending.token.isCancellationRequested ? undefined : result;
  }

  // Cancels the request that is waiting, if any.
  cancel() {
    if (this.pending) this.pending.cancel();
    this.pending = undefined;
  }
}

module.exports = { isSource, showsSource, Requests };
