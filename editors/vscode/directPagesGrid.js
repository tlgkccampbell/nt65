// The grid panel of the direct page map, which shows one direct page as 16 rows of 16 bytes with
// its locations, its budget and its checks beside it. This file opens the panel and passes it
// the map; the page itself is drawn by media/directPageGrid.js inside the webview.
const crypto = require('crypto');
const vscode = require('vscode');

const PANEL = 'nt65.directPageGrid';

// Returns the panel's HTML. It loads only its own script and stylesheet, under a policy that
// allows nothing else.
function html(webview, extensionUri) {
  const nonce = crypto.randomBytes(16).toString('base64');
  const media = name => webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', name));
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link rel="stylesheet" href="${media('directPageGrid.css')}">
<title>Direct Page</title>
</head>
<body>
<div class="tabs" role="tablist" id="tabs"></div>
<div class="body" id="body"><p class="empty" id="empty">No direct page to show.</p></div>
<div class="tip" id="tip" hidden></div>
<script nonce="${nonce}" src="${media('directPageGrid.js')}"></script>
</body>
</html>`;
}

// Represents the grid panel. There is at most one, and it keeps the page it shows when the map
// is refreshed.
class Grid {
  // `handlers.select(page, location)` is called when a cell or a list entry is clicked, and
  // `handlers.visible()` when the panel comes into view.
  constructor(context, handlers) {
    this.context = context;
    this.handlers = handlers;
    this.panel = undefined;
    this.ready = false;
    this.state = { result: null, hazards: true, page: null, location: null };
  }

  // Gets a value indicating whether the panel is open and in view.
  get visible() {
    return !!this.panel && this.panel.visible;
  }

  // Opens the panel beside the editor, or brings it forward, on a page and a location.
  show(result, hazards, page, location) {
    this.state = { result, hazards, page: page || this.state.page, location: location || null };
    if (this.panel) {
      this.panel.reveal(undefined, true);
      this.post();
      return;
    }
    const media = vscode.Uri.joinPath(this.context.extensionUri, 'media');
    this.panel = vscode.window.createWebviewPanel(PANEL, 'Direct Page',
      { viewColumn: vscode.ViewColumn.Beside, preserveFocus: true },
      { enableScripts: true, localResourceRoots: [media] });
    this.ready = false;
    this.panel.webview.html = html(this.panel.webview, this.context.extensionUri);
    this.panel.webview.onDidReceiveMessage(message => this.receive(message));
    this.panel.onDidChangeViewState(() => {
      if (this.panel && this.panel.visible) this.handlers.visible();
    });
    this.panel.onDidDispose(() => {
      this.panel = undefined;
      this.ready = false;
    });
  }

  // Passes a new map to the panel, which stays on the page it shows.
  update(result, hazards) {
    this.state = { ...this.state, result, hazards };
    this.post();
  }

  // Moves the panel to a page and marks a location in it, when the panel is open.
  focus(page, location) {
    if (!this.panel) return;
    this.state = { ...this.state, page, location };
    this.post();
  }

  // Handles a message from the webview.
  receive(message) {
    switch (message.type) {
      case 'ready':
        this.ready = true;
        this.post();
        break;
      case 'tab':
        this.state = { ...this.state, page: message.page, location: null };
        break;
      case 'select':
        this.state = { ...this.state, page: message.page, location: message.location };
        this.handlers.select(message.page, message.location);
        break;
      default:
    }
  }

  // Sends the panel what it shows, once its script is listening.
  post() {
    if (!this.panel || !this.ready) return;
    this.panel.webview.postMessage({ type: 'map', ...this.state });
  }

  dispose() {
    if (this.panel) this.panel.dispose();
  }
}

module.exports = { Grid, html };
