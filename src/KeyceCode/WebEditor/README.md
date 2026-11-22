# Monaco editor client for the WPF/WebView2 workbench

This project packages the pinned Monaco Editor **0.57.0** browser distribution into `dist/`. The WebView page uses only local assets; it does not fetch scripts, styles, fonts, or editor code from a CDN at runtime. The npm dependency is pinned in `package.json` and `package-lock.json`.

## Build local editor assets

Requirements: Node.js and npm.

```powershell
npm ci --prefix src/KeyceCode/WebEditor
npm run --prefix src/KeyceCode/WebEditor build
```

The build copies `src/` and Monaco's `min/vs/` AMD loader, language workers, styles and fonts into `dist/`, and preserves Monaco's license in `dist/MONACO-LICENSE.txt`. The WPF project should copy `WebEditor/dist/**` to its output and map that directory to a virtual WebView2 host such as `https://keyce.local/` before navigating to `https://keyce.local/index.html`. Do not navigate the editor using `file:`; Monaco workers require a secure local origin.

## Host/WebView message contract

The C# host sends one JSON object with a `type` discriminator using WebView2's `PostWebMessageAsJson`. The page sends JSON objects through `window.chrome.webview.postMessage`. The host must wait for `ready` before sending editor commands.

Host to page:

- `setDocument`: `{ "type":"setDocument", "id":"doc-id", "path":"relative/path.cs", "text":"...", "language":"csharp", "version":0, "theme":"keyce-night", "options":{} }`
- `setTheme`: `{ "type":"setTheme", "theme":"vs-dark|vs|hc-black|keyce-night" }`
- `setOptions`: `{ "type":"setOptions", "options":{ "fontSize":15, "fontFamily":"Cascadia Code", "tabSize":4, "minimap":{"enabled":false} } }`
- `executeCommand`: `{ "type":"executeCommand", "command":"undo|redo|find|replace|formatDocument|formatSelection|commentLine|uncommentLine|fold|unfold|selectAll", "args":{} }`
- `requestSave`, `requestFormat`, `focus`, `layout`, `setReadOnly:{value:true}`, `setDiagnostics:{markers:[MonacoMarker...]}`
- `applyEdit`: `{ "type":"applyEdit", "text":"replacement full document", "select":null }`; use only after user approval for assistant changes.

Page to host:

- `ready`: editor loaded and editor capabilities available.
- `contentChanged`: `{ "type":"contentChanged", "id":"doc-id", "path":"...", "text":"...", "version":1 }` after a short debounce. Host tracks dirty state and should not re-send this content back as a new document.
- `cursorChanged`: `{ "type":"cursorChanged", "id":"doc-id", "line":1, "column":1, "selection":{"startLine":1,"startColumn":1,"endLine":1,"endColumn":1}, "selectedText":"" }`. `selectedText` enables explicit selected-text context for KeyceIA.
- `requestSave`: includes current `id`, `path`, `text` and `version`; host performs the actual safe file write and replies with save state if needed.
- `requestFormat`: includes current document text and language; host delegates formatting to a configured language service and returns the formatted text with `applyEdit` only after success.
- `command` for commands managed by the workbench; `editorError` for page/runtime initialization errors.

## Security and limitations

The bridge only accepts the documented message types and does not expose arbitrary file or process access to JavaScript. File I/O, command execution, LSP, and formatting remain host responsibilities. Treat all messages as untrusted input and validate IDs, sizes, paths, ranges and command names in C#.

Monaco supplies the browser editor, built-in syntax tokenization and selected browser language tooling. It is not the full VS Code Workbench and does not provide C# IntelliSense, external language servers, formatting, debugging, tasks, Git, or VS Code extension-host compatibility by itself. Add these through explicit LSP/DAP/host integrations.
