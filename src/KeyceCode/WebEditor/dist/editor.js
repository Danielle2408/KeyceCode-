(() => {
  "use strict";

  const vscode = window.chrome?.webview;
  const loading = document.getElementById("loading");
  const errorNode = document.getElementById("error");
  let monacoApi;
  let editor;
  let activeDocument = { id: null, path: null, version: 0 };
  let suppressChanges = false;
  let contentTimer = 0;
  const models = new Map();
  const documentMarkers = new Map();
  const pendingCompletions = new Map();
  let completionSequence = 0;

  const post = (message) => {
    if (vscode) vscode.postMessage(message);
    else window.parent.postMessage({ source: "keycecode-editor", ...message }, "*");
  };

  function reportError(error) {
    errorNode.hidden = false;
    errorNode.textContent = `Impossible d’initialiser l’éditeur Monaco.\n${error?.stack || error?.message || error}`;
    post({ type: "editorError", message: String(error?.message || error) });
  }

  function modelUri(path, id) {
    const safePath = String(path || `untitled-${id || "1"}`).replace(/\\/g, "/").replace(/^\/+/, "");
    return monaco.Uri.parse(`file:///workspace/${safePath.split("/").map(encodeURIComponent).join("/")}`);
  }

  function notifyContentChanged() {
    if (suppressChanges || !editor || !activeDocument.id) return;
    window.clearTimeout(contentTimer);
    contentTimer = window.setTimeout(() => {
      const model = editor.getModel();
      if (!model) return;
      activeDocument.version += 1;
      post({
        type: "contentChanged",
        id: activeDocument.id,
        path: activeDocument.path,
        text: model.getValue(),
        version: activeDocument.version
      });
    }, 90);
  }

  function initialize(monaco) {
    monacoApi = monaco;
    window.monaco = monaco;
    monaco.editor.defineTheme("keyce-night", {
      base: "vs-dark",
      inherit: true,
      rules: [
        { token: "comment", foreground: "6A9955", fontStyle: "italic" },
        { token: "string", foreground: "CE9178" },
        { token: "keyword", foreground: "C586C0" },
        { token: "number", foreground: "B5CEA8" },
        { token: "type.identifier", foreground: "4EC9B0" }
      ],
      colors: {
        "editor.background": "#1E1E1E",
        "editor.foreground": "#D4D4D4",
        "editorLineNumber.foreground": "#858585",
        "editorLineNumber.activeForeground": "#C6C6C6",
        "editorCursor.foreground": "#AEAFAD",
        "editor.selectionBackground": "#264F78",
        "editor.inactiveSelectionBackground": "#3A3D41",
        "editor.lineHighlightBackground": "#FFFFFF0A",
        "editorIndentGuide.background1": "#404040",
        "editorIndentGuide.activeBackground1": "#707070",
        "editorGutter.background": "#1E1E1E",
        "editorWidget.background": "#252526",
        "editorWidget.border": "#454545",
        "editorSuggestWidget.background": "#252526",
        "editorSuggestWidget.border": "#454545",
        "editorSuggestWidget.selectedBackground": "#04395E",
        "editorOverviewRuler.border": "#7F7F7F4D",
        "scrollbarSlider.background": "#79797966",
        "scrollbarSlider.hoverBackground": "#646464B3",
        "scrollbarSlider.activeBackground": "#BFBFBF66"
      }
    });

    editor = monaco.editor.create(document.getElementById("editor"), {
      value: "",
      language: "plaintext",
      theme: "keyce-night",
      automaticLayout: true,
      fontFamily: "Cascadia Code, Consolas, monospace",
      fontSize: 14,
      lineHeight: 21,
      fontLigatures: false,
      minimap: { enabled: true, side: "right", renderCharacters: false, maxColumn: 120 },
      lineNumbers: "on",
      renderLineHighlight: "all",
      renderWhitespace: "selection",
      guides: { indentation: true, highlightActiveIndentation: true, bracketPairs: true },
      bracketPairColorization: { enabled: true, independentColorPoolPerBracketType: true },
      smoothScrolling: true,
      cursorBlinking: "smooth",
      cursorSmoothCaretAnimation: "on",
      scrollBeyondLastLine: false,
      tabSize: 4,
      insertSpaces: true,
      wordWrap: "off",
      padding: { top: 10, bottom: 10 },
      stickyScroll: { enabled: true },
      unicodeHighlight: { ambiguousCharacters: false }
    });

    editor.onDidChangeModelContent(notifyContentChanged);
    editor.onDidChangeCursorSelection((event) => {
      if (!activeDocument.id) return;
      const selection = event.selection;
      post({
        type: "cursorChanged",
        id: activeDocument.id,
        line: selection.positionLineNumber,
        column: selection.positionColumn,
        offset: editor.getModel()?.getOffsetAt(selection.getPosition()) || 0,
        selection: {
          startLine: selection.startLineNumber,
          startColumn: selection.startColumn,
          endLine: selection.endLineNumber,
          endColumn: selection.endColumn
        },
        selectedText: editor.getModel()?.getValueInRange(selection) || ""
      });
    });

    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => requestSave());
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyF, () => post({ type: "command", command: "find" }));
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyH, () => post({ type: "command", command: "replace" }));
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyP, () => post({ type: "command", command: "quickOpen" }));
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Space, () => post({ type: "command", command: "geminiComplete" }));

    // Real language-aware completion comes from the host's configured LSP process.
    for (const language of ["csharp", "javascript", "javascriptreact", "typescript", "typescriptreact", "json", "jsonc", "html", "css", "scss", "powershell", "python", "go", "rust", "java", "cpp"]) {
      monaco.languages.registerCompletionItemProvider(language, {
        triggerCharacters: ["."],
        provideCompletionItems(model, position, context, token) {
          if (!vscode || !activeDocument.id || !activeDocument.path) return { suggestions: [] };
          const requestId = `lsp-${++completionSequence}`;
          return new Promise((resolve) => {
            const timer = window.setTimeout(() => {
              pendingCompletions.delete(requestId);
              resolve({ suggestions: [] });
            }, 15000);
            const subscription = token.onCancellationRequested(() => {
              window.clearTimeout(timer);
              subscription.dispose();
              pendingCompletions.delete(requestId);
              resolve({ suggestions: [] });
            });
            pendingCompletions.set(requestId, (message) => {
              window.clearTimeout(timer);
              subscription.dispose();
              const suggestions = (message.items || []).map((item) => ({
                label: item.label,
                kind: completionKind(monaco, item.kind),
                detail: item.detail || undefined,
                documentation: item.documentation || undefined,
                insertText: item.insertText || item.label,
                insertTextRules: item.insertTextRules || undefined,
                sortText: item.sortText || undefined,
                range: undefined
              }));
              resolve({ suggestions });
            });
            post({
              type: "lspCompletionRequest",
              requestId,
              id: activeDocument.id,
              path: activeDocument.path,
              line: position.lineNumber,
              column: position.column,
              triggerKind: context.triggerKind === monaco.languages.CompletionTriggerKind.TriggerCharacter ? 2 : 1,
              triggerCharacter: context.triggerCharacter || null
            });
          });
        }
      });
    }

    loading.classList.add("ready");
    post({ type: "ready", capabilities: ["monaco", "models", "multicursor", "format-request", "theme", "options"] });
    editor.focus();
  }

  function requestSave() {
    if (!editor || !activeDocument.id) return;
    post({
      type: "requestSave",
      id: activeDocument.id,
      path: activeDocument.path,
      text: editor.getValue(),
      version: activeDocument.version
    });
  }

  function setDocument(message) {
    if (!editor) return;
    window.clearTimeout(contentTimer);
    suppressChanges = true;
    try {
      const id = String(message.id || message.path || "untitled");
      const uri = modelUri(message.path, id);
      const key = uri.toString();
      let model = models.get(key);
      if (!model || model.isDisposed()) {
        model = monaco.editor.createModel(String(message.text || ""), message.language || "plaintext", uri);
        models.set(key, model);
      } else if (message.text !== undefined && model.getValue() !== message.text) {
        model.setValue(String(message.text));
      }
      monaco.editor.setModelLanguage(model, message.language || "plaintext");
      editor.setModel(model);
      activeDocument = {
        id,
        path: message.path || null,
        version: Number.isFinite(message.version) ? message.version : 0
      };
      if (message.options) editor.updateOptions(message.options);
      if (message.theme) monaco.editor.setTheme(message.theme);
      monaco.editor.setModelMarkers(model, "keycecode-lsp", documentMarkers.get(id) || []);
      editor.focus();
    } finally {
      suppressChanges = false;
    }
  }

  async function executeCommand(message) {
    const command = String(message.command || "");
    const builtIn = {
      undo: "undo",
      redo: "redo",
      find: "actions.find",
      replace: "editor.action.startFindReplaceAction",
      formatDocument: "editor.action.formatDocument",
      formatSelection: "editor.action.formatSelection",
      commentLine: "editor.action.commentLine",
      uncommentLine: "editor.action.removeCommentLine",
      fold: "editor.fold",
      unfold: "editor.unfold",
      selectAll: "editor.action.selectAll"
    }[command];
    const action = editor.getAction(builtIn || command);
    if (action) { await action.run(message.args); return; }
    post({ type: "command", command, args: message.args || null });
  }

  window.keyce = {
    dispatch(message) {
      if (!message || typeof message !== "object") return;
      switch (message.type) {
        case "setDocument": setDocument(message); break;
        case "setTheme": monacoApi?.editor.setTheme(message.theme || "keyce-night"); break;
        case "setLanguage": {
          const model = editor?.getModel();
          if (model && typeof message.language === "string") monacoApi.editor.setModelLanguage(model, message.language);
          break;
        }
        case "setOptions": editor?.updateOptions(message.options || {}); break;
        case "executeCommand": void executeCommand(message); break;
        case "requestSave": requestSave(); break;
        case "requestFormat": post({
          type: "requestFormat",
          id: activeDocument.id,
          path: activeDocument.path,
          language: editor?.getModel()?.getLanguageId() || "plaintext",
          text: editor?.getValue() || ""
        }); break;
        case "applyEdit": {
          if (!editor || typeof message.text !== "string") break;
          const model = editor.getModel();
          const selections = message.select;
          model.pushEditOperations([], [{ range: model.getFullModelRange(), text: message.text }], () => null);
          if (selections) editor.setSelection(selections);
          editor.focus();
          break;
        }
        case "applyTextEdits": {
          if (!editor || !Array.isArray(message.edits)) break;
          const model = editor.getModel();
          if (!model) break;
          const edits = message.edits.map((edit) => {
            const range = edit.range;
            if (!range?.start || !range?.end || typeof edit.newText !== "string") return null;
            return {
              start: model.getOffsetAt({ lineNumber: range.start.line + 1, column: range.start.character + 1 }),
              end: model.getOffsetAt({ lineNumber: range.end.line + 1, column: range.end.character + 1 }),
              text: edit.newText
            };
          }).filter(Boolean).sort((a, b) => b.start - a.start);
          editor.pushUndoStop();
          for (const edit of edits) {
            const range = {
              startLineNumber: model.getPositionAt(edit.start).lineNumber,
              startColumn: model.getPositionAt(edit.start).column,
              endLineNumber: model.getPositionAt(edit.end).lineNumber,
              endColumn: model.getPositionAt(edit.end).column
            };
            editor.executeEdits("lsp-format", [{ range, text: edit.text, forceMoveMarkers: true }]);
          }
          editor.pushUndoStop();
          editor.focus();
          break;
        }
        case "setDiagnostics": {
          if (typeof message.id !== "string") break;
          const markers = Array.isArray(message.markers) ? message.markers : [];
          documentMarkers.set(message.id, markers);
          const model = editor?.getModel();
          if (model && activeDocument.id === message.id) monacoApi.editor.setModelMarkers(model, "keycecode-lsp", markers);
          break;
        }
        case "lspCompletionResult": {
          const resolve = pendingCompletions.get(message.requestId);
          if (resolve) {
            pendingCompletions.delete(message.requestId);
            resolve(message);
          }
          break;
        }
        case "setReadOnly": editor?.updateOptions({ readOnly: Boolean(message.value) }); break;
        case "insertTextAtCursor": {
          if (!editor || typeof message.text !== "string") break;
          editor.pushUndoStop();
          editor.executeEdits("keyceia", [{ range: editor.getSelection(), text: message.text, forceMoveMarkers: true }]);
          editor.pushUndoStop();
          editor.focus();
          break;
        }
        case "focus": editor?.focus(); break;
        case "layout": editor?.layout(); break;
        default: post({ type: "editorError", message: `Message hôte inconnu : ${message.type}` });
      }
    },
    getValue() { return editor?.getValue() || ""; },
    getSelection() { return editor?.getSelection() || null; }
  };

  function completionKind(monaco, kind) {
    const value = Number(kind);
    const map = {
      1: monaco.languages.CompletionItemKind.Text,
      2: monaco.languages.CompletionItemKind.Method,
      3: monaco.languages.CompletionItemKind.Function,
      4: monaco.languages.CompletionItemKind.Constructor,
      5: monaco.languages.CompletionItemKind.Field,
      6: monaco.languages.CompletionItemKind.Variable,
      7: monaco.languages.CompletionItemKind.Class,
      8: monaco.languages.CompletionItemKind.Interface,
      9: monaco.languages.CompletionItemKind.Module,
      10: monaco.languages.CompletionItemKind.Property,
      12: monaco.languages.CompletionItemKind.Value,
      13: monaco.languages.CompletionItemKind.Enum,
      14: monaco.languages.CompletionItemKind.Keyword,
      15: monaco.languages.CompletionItemKind.Snippet,
      16: monaco.languages.CompletionItemKind.Color,
      17: monaco.languages.CompletionItemKind.File,
      18: monaco.languages.CompletionItemKind.Reference
    };
    return map[value] || monaco.languages.CompletionItemKind.Text;
  }

  if (vscode) vscode.addEventListener("message", (event) => window.keyce.dispatch(event.data));
  else window.addEventListener("message", (event) => {
    if (event.data?.target === "keycecode-editor") window.keyce.dispatch(event.data.message);
  });

  require.config({ paths: { vs: "./vs" } });
  require(["vs/editor/editor.main"], initialize, reportError);
})();
