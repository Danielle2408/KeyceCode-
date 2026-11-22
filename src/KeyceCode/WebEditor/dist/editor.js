(() => {
  "use strict";

  const vscode = window.chrome?.webview;
  const loading = document.getElementById("loading");
  const errorNode = document.getElementById("error");
  let monacoApi;
  let editor;
  let secondaryEditor = null;
  let activePane = 0;
  let splitOrientation = "vertical";
  let editorOptions;
  let activeDocument = { id: null, path: null, version: 0 };
  let suppressChanges = false;
  const contentTimers = new Map();
  const models = new Map();
  const modelPathsByUri = new Map();
  const modelIdsByUri = new Map();
  const documentMarkers = new Map();
  const pendingCompletions = new Map();
  const pendingNavigation = new Map();
  const pendingRefactorRequests = new Map();
  const pendingAiCompletions = new Map();
  let completionSequence = 0;
  let aiCompletionEnabled = false;
  let userSnippets = [];

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

  function documentForModel(model) {
    if (!model) return null;
    const key = model.uri.toString();
    return { id: modelIdsByUri.get(key), path: modelPathsByUri.get(key) || "" };
  }

  function activatePane(pane) {
    if (pane === 1 && !secondaryEditor) return;
    activePane = pane;
    editor = pane === 1 ? secondaryEditor : primaryEditor;
    const model = editor?.getModel();
    const doc = documentForModel(model);
    if (doc?.id) {
      activeDocument = { ...doc, version: model.getVersionId() };
      post({ type: "activePaneChanged", id: doc.id });
    }
    document.querySelectorAll(".editor-pane").forEach((node, index) => node.classList.toggle("active", index === pane));
  }

  let primaryEditor;
  function notifyContentChanged(source = editor) {
    const model = source?.getModel();
    const doc = documentForModel(model);
    if (suppressChanges || !source || !doc?.id) return;
    cancelAiCompletions();
    window.clearTimeout(contentTimers.get(doc.id));
    const timer = window.setTimeout(() => {
      contentTimers.delete(doc.id);
      if (!model) return;
      post({
        type: "contentChanged",
        id: doc.id,
        path: doc.path,
        text: model.getValue(),
        version: model.getVersionId()
      });
    }, 90);
    contentTimers.set(doc.id, timer);
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
    monaco.editor.defineTheme("keyce-brand-night", {
      base: "vs-dark",
      inherit: true,
      rules: [
        { token: "comment", foreground: "8190B3", fontStyle: "italic" },
        { token: "string", foreground: "F5A97F" },
        { token: "keyword", foreground: "C792EA" },
        { token: "number", foreground: "F9C66D" },
        { token: "type.identifier", foreground: "79D4C8" },
        { token: "function", foreground: "82AAFF" }
      ],
      colors: {
        "editor.background": "#080F22", "editor.foreground": "#E8EDF8",
        "editorLineNumber.foreground": "#7180A3", "editorLineNumber.activeForeground": "#E8EDF8",
        "editorCursor.foreground": "#FF982E", "editor.selectionBackground": "#234777",
        "editor.inactiveSelectionBackground": "#1B2A4D", "editor.lineHighlightBackground": "#FFFFFF08",
        "editorIndentGuide.background1": "#233459", "editorIndentGuide.activeBackground1": "#50658D",
        "editorGutter.background": "#080F22", "editorWidget.background": "#111B36",
        "editorWidget.border": "#344B75", "editorSuggestWidget.background": "#111B36",
        "editorSuggestWidget.border": "#344B75", "editorSuggestWidget.selectedBackground": "#234777",
        "editorOverviewRuler.border": "#7180A34D", "scrollbarSlider.background": "#7180A366",
        "scrollbarSlider.hoverBackground": "#9BAAC799", "scrollbarSlider.activeBackground": "#E8EDF866"
      }
    });

    editorOptions = {
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
      inlineSuggest: { enabled: true, mode: "subword" },
      insertSpaces: true,
      wordWrap: "off",
      padding: { top: 10, bottom: 10 },
      stickyScroll: { enabled: true },
      unicodeHighlight: { ambiguousCharacters: false }
    };
    editor = primaryEditor = monaco.editor.create(document.getElementById("editor"), editorOptions);

    editor.onDidChangeModelContent(() => notifyContentChanged(primaryEditor));
    editor.onDidChangeModel(() => {
      const model = primaryEditor.getModel();
      const path = model && modelPathsByUri.get(model.uri.toString());
      if (path) post({ type: "lspModelActivated", path });
    });
    editor.onDidChangeCursorSelection((event) => {
      cancelAiCompletions();
      const model = primaryEditor.getModel();
      const doc = documentForModel(model);
      if (!doc?.id) return;
      const selection = event.selection;
      post({
        type: "cursorChanged",
        id: doc.id,
        line: selection.positionLineNumber,
        column: selection.positionColumn,
        offset: model?.getOffsetAt(selection.getPosition()) || 0,
        version: model?.getVersionId() || 0,
        selection: {
          startLine: selection.startLineNumber,
          startColumn: selection.startColumn,
          endLine: selection.endLineNumber,
          endColumn: selection.endColumn
        },
        selectedText: model?.getValueInRange(selection) || ""
      });
    });
    editor.onDidFocusEditorWidget(() => activatePane(0));

    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => requestSave());
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyF, () => post({ type: "command", command: "find" }));
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyH, () => post({ type: "command", command: "replace" }));
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyP, () => post({ type: "command", command: "quickOpen" }));
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Space, () => post({ type: "command", command: "geminiComplete" }));
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Period, () => requestCodeActions());

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
      monaco.languages.registerCompletionItemProvider(language, {
        provideCompletionItems(model, position) {
          const word = model.getWordUntilPosition(position);
          const prefix = word.word || "";
          const languageId = model.getLanguageId();
          const suggestions = userSnippets
            .filter((snippet) => (!snippet.scopes.length || snippet.scopes.some((scope) => scope.toLowerCase() === languageId.toLowerCase())) &&
              snippet.prefix.toLowerCase().startsWith(prefix.toLowerCase()))
            .map((snippet) => ({
              label: snippet.label,
              kind: monaco.languages.CompletionItemKind.Snippet,
              detail: snippet.description || `Snippet · ${snippet.prefix}`,
              documentation: snippet.body,
              insertText: snippet.body,
              insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
              sortText: `0_${snippet.prefix}`,
              range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, position.column)
            }));
          return { suggestions };
        }
      });

      monaco.languages.registerDefinitionProvider(language, {
        provideDefinition(model, position, token) {
          return requestNavigation("definition", model, position, token).then((result) =>
            (result?.locations || []).map((location) => makeNavigationLocation(monaco, location)).filter(Boolean));
        }
      });

      monaco.languages.registerReferenceProvider(language, {
        provideReferences(model, position, context, token) {
          return requestNavigation("references", model, position, token).then((result) =>
            (result?.locations || []).map((location) => makeNavigationLocation(monaco, location)).filter(Boolean));
        }
      });

      monaco.languages.registerHoverProvider(language, {
        provideHover(model, position, token) {
          return requestNavigation("hover", model, position, token).then((result) => {
            const contentsValue = result?.hover?.contents;
            if (!contentsValue) return null;
            const rawContents = Array.isArray(contentsValue) ? contentsValue : [contentsValue];
            const contents = rawContents.map((entry) => {
              if (typeof entry === "string") return { value: entry };
              if (entry && typeof entry.value === "string") return { value: entry.value };
              return null;
            }).filter(Boolean);
            return contents.length ? { contents } : null;
          });
        }
      });

      monaco.languages.registerRenameProvider(language, {
        provideRenameEdits(model, position, newName, token) {
          const key = model.uri.toString();
          const path = modelPathsByUri.get(key);
          const id = modelIdsByUri.get(key);
          if (!vscode || !path || !id || typeof newName !== "string" || !newName.trim()) return null;
          return requestRefactor("rename", {
            id, path, line: position.lineNumber, column: position.column, newName: newName.trim(),
            text: model.getValue(), version: model.getVersionId()
          }, token).then((result) => result?.success ? { edits: [] } : null);
        }
      });
    }

    // Opt-in Gemini ghost text. LSP suggestions above remain a separate provider.
    monaco.languages.registerInlineCompletionsProvider(
      ["csharp", "javascript", "javascriptreact", "typescript", "typescriptreact", "json", "jsonc", "html", "css", "scss", "powershell", "python", "go", "rust", "java", "cpp", "markdown", "plaintext"],
      {
        async provideInlineCompletions(model, position, _context, token) {
          if (!aiCompletionEnabled || !vscode || !activeDocument.id || editor.getModel() !== model) return { items: [] };
          const selected = editor.getSelection();
          if (!selected || !selected.isEmpty()) return { items: [] };

          let debounceSubscription;
          const mayContinue = await new Promise((resolve) => {
            const timer = window.setTimeout(() => { debounceSubscription?.dispose(); resolve(true); }, 550);
            debounceSubscription = token.onCancellationRequested(() => {
              window.clearTimeout(timer);
              debounceSubscription?.dispose();
              resolve(false);
            });
          });
          debounceSubscription?.dispose();
          if (!mayContinue || token.isCancellationRequested) return { items: [] };

          const currentSelection = editor.getSelection();
          if (!aiCompletionEnabled || editor.getModel() !== model || !currentSelection?.isEmpty() ||
              currentSelection.positionLineNumber !== position.lineNumber || currentSelection.positionColumn !== position.column)
            return { items: [] };

          const version = model.getVersionId();
          const offset = model.getOffsetAt(position);
          let prefix = model.getValue().slice(0, offset);
          if (prefix.length > 4000) prefix = prefix.slice(-4000);
          if (!prefix.trim()) return { items: [] };
          const documentId = activeDocument.id;
          const requestId = `${documentId}:${++completionSequence}`;

          return await new Promise((resolve) => {
            let settled = false;
            let requestSubscription;
            let timeout = window.setTimeout(() => {
              post({ type: "geminiInlineCompletionCancel", requestId });
              finish({ items: [] });
            }, 12000);
            const finish = (result) => {
              if (settled) return;
              settled = true;
              window.clearTimeout(timeout);
              requestSubscription?.dispose();
              pendingAiCompletions.delete(requestId);
              resolve(result);
            };
            requestSubscription = token.onCancellationRequested(() => {
              post({ type: "geminiInlineCompletionCancel", requestId });
              finish({ items: [] });
            });
            pendingAiCompletions.set(requestId, (message) => {
              if (token.isCancellationRequested || !aiCompletionEnabled || editor.getModel() !== model ||
                  activeDocument.id !== documentId || model.getVersionId() !== version ||
                  !editor.getPosition() || model.getOffsetAt(editor.getPosition()) !== offset || message.version !== version ||
                  message.offset !== offset || message.id !== documentId || typeof message.text !== "string" || !message.text.trim()) {
                finish({ items: [] });
                return;
              }
              finish({ items: [{
                insertText: message.text,
                range: new monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column)
              }] });
            });
            post({ type: "geminiInlineCompletionRequest", requestId, id: documentId, version, offset,
              language: model.getLanguageId(), prefix, selectionEmpty: true });
          });
        },
        freeInlineCompletions() {}
      }
    );

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
      version: editor.getModel()?.getVersionId() || activeDocument.version
    });
  }

  function requestNavigation(operation, model, position, token) {
    if (!vscode) return Promise.resolve({ locations: [] });
    const key = model.uri.toString();
    const path = modelPathsByUri.get(key);
    const id = modelIdsByUri.get(key);
    if (!path || !id) return Promise.resolve({ locations: [] });
    const requestId = `lsp-nav-${++completionSequence}`;
    return new Promise((resolve) => {
      let cancellationSubscription;
      const timer = window.setTimeout(() => {
        pendingNavigation.delete(requestId);
        cancellationSubscription?.dispose();
        post({ type: "lspNavigationCancel", requestId });
        resolve({ locations: [] });
      }, 11000);
      cancellationSubscription = token.onCancellationRequested(() => {
        window.clearTimeout(timer);
        cancellationSubscription.dispose();
        pendingNavigation.delete(requestId);
        post({ type: "lspNavigationCancel", requestId });
        resolve({ locations: [] });
      });
      pendingNavigation.set(requestId, (message) => {
        window.clearTimeout(timer);
        cancellationSubscription.dispose();
        resolve(message);
      });
      post({
        type: "lspNavigationRequest",
        requestId,
        operation,
        id,
        path,
        line: position.lineNumber,
        column: position.column
      });
    });
  }

  function requestRefactor(operation, data, token) {
    const requestId = `lsp-refactor-${++completionSequence}`;
    return new Promise((resolve) => {
      let settled = false;
      let subscription;
      const finish = (value) => {
        if (settled) return;
        settled = true;
        window.clearTimeout(timer);
        subscription?.dispose();
        pendingRefactorRequests.delete(requestId);
        resolve(value);
      };
      const timer = window.setTimeout(() => {
        post({ type: "lspRefactorCancel", requestId });
        finish(null);
      }, 15000);
      if (token) subscription = token.onCancellationRequested(() => {
        post({ type: "lspRefactorCancel", requestId });
        finish(null);
      });
      pendingRefactorRequests.set(requestId, finish);
      post({ type: "lspRenameRequest", requestId, ...data });
    });
  }

  function requestCodeActions() {
    if (!vscode || !editor) return;
    const model = editor.getModel();
    const key = model?.uri.toString();
    const path = key && modelPathsByUri.get(key);
    const id = key && modelIdsByUri.get(key);
    if (!model || !path || !id) return;
    const selection = editor.getSelection();
    if (!selection) return;
    post({
      type: "lspCodeActionRequest", id, path,
      startLine: selection.startLineNumber, startColumn: selection.startColumn,
      endLine: selection.endLineNumber, endColumn: selection.endColumn,
      text: model.getValue(), version: model.getVersionId()
    });
  }

  function makeNavigationLocation(monaco, location) {
    if (!location || typeof location.path !== "string" || !location.path || typeof location.text !== "string") return null;
    const uri = modelUri(location.path, `lsp-${location.path}`);
    const key = uri.toString();
    let model = models.get(key);
    if (!model || model.isDisposed()) {
      model = monaco.editor.createModel(location.text, languageForPath(location.path), uri);
      models.set(key, model);
    }
    modelPathsByUri.set(key, location.workspacePath || location.path);
    modelIdsByUri.set(key, `lsp:${location.workspacePath || location.path}`);
    return {
      uri,
      range: {
        startLineNumber: Math.max(1, Number(location.startLine) + 1),
        startColumn: Math.max(1, Number(location.startCharacter) + 1),
        endLineNumber: Math.max(1, Number(location.endLine) + 1),
        endColumn: Math.max(1, Number(location.endCharacter) + 1)
      }
    };
  }

  function languageForPath(path) {
    const extension = String(path).split(".").pop()?.toLowerCase();
    return ({
      cs: "csharp", js: "javascript", jsx: "javascriptreact", ts: "typescript", tsx: "typescriptreact",
      json: "json", jsonc: "jsonc", html: "html", htm: "html", css: "css", scss: "scss",
      py: "python", ps1: "powershell", go: "go", rs: "rust", java: "java", cpp: "cpp", cc: "cpp", h: "cpp", hpp: "cpp"
    })[extension] || "plaintext";
  }

  function cancelAiCompletions() {
    for (const [requestId, resolve] of pendingAiCompletions) {
      post({ type: "geminiInlineCompletionCancel", requestId });
      resolve({ id: null, version: -1, offset: -1, text: "" });
    }
  }

  function setDocument(message) {
    if (!editor) return;
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
      modelPathsByUri.set(key, message.path || "");
      modelIdsByUri.set(key, id);
      editor.setModel(model);
      cancelAiCompletions();
      activeDocument = {
        id,
        path: message.path || null,
        version: model.getVersionId()
      };
      if (message.options) editor.updateOptions(message.options);
      if (message.theme) monaco.editor.setTheme(message.theme);
      monaco.editor.setModelMarkers(model, "keycecode-lsp", documentMarkers.get(id) || []);
      editor.focus();
      activatePane(activePane);
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
        case "toggleSplit": {
          const groups = document.getElementById("editor-groups");
          if (message.close === true) {
            if (secondaryEditor) {
              secondaryEditor.dispose(); secondaryEditor = null;
              document.getElementById("editor-secondary")?.remove();
              groups.classList.remove("horizontal");
              activatePane(0); primaryEditor.layout();
            }
            break;
          }
          if (secondaryEditor) {
            if (message.orientation) {
              splitOrientation = message.orientation === "horizontal" ? "horizontal" : "vertical";
              groups.classList.toggle("horizontal", splitOrientation === "horizontal");
              primaryEditor.layout(); secondaryEditor.layout();
            } else {
              secondaryEditor.dispose(); secondaryEditor = null;
              document.getElementById("editor-secondary")?.remove();
              groups.classList.remove("horizontal");
              activatePane(0); primaryEditor.layout();
            }
          } else {
            splitOrientation = message.orientation === "horizontal" ? "horizontal" : "vertical";
            groups.classList.toggle("horizontal", splitOrientation === "horizontal");
            const pane = document.createElement("section");
            pane.id = "editor-secondary"; pane.className = "editor-pane"; pane.setAttribute("aria-label", "Éditeur de code secondaire");
            groups.appendChild(pane);
            secondaryEditor = monacoApi.editor.create(pane, editorOptions);
            secondaryEditor.setModel(primaryEditor.getModel());
            secondaryEditor.onDidChangeModel(() => {
              const model = secondaryEditor.getModel();
              const path = model && modelPathsByUri.get(model.uri.toString());
              if (path) post({ type: "lspModelActivated", path });
            });
            secondaryEditor.onDidChangeModelContent(() => notifyContentChanged(secondaryEditor));
            secondaryEditor.onDidFocusEditorWidget(() => activatePane(1));
            secondaryEditor.onDidChangeCursorSelection((event) => {
              activatePane(1);
              const model = secondaryEditor.getModel();
              const doc = documentForModel(model);
              if (!doc?.id) return;
              const selection = event.selection;
              post({ type: "cursorChanged", id: doc.id, line: selection.positionLineNumber, column: selection.positionColumn,
                offset: model.getOffsetAt(selection.getPosition()), version: model.getVersionId(),
                selection: { startLine: selection.startLineNumber, startColumn: selection.startColumn, endLine: selection.endLineNumber, endColumn: selection.endColumn },
                selectedText: model.getValueInRange(selection) });
            });
            secondaryEditor.addCommand(monacoApi.KeyMod.CtrlCmd | monacoApi.KeyCode.KeyS, () => requestSave());
            secondaryEditor.addCommand(monacoApi.KeyMod.CtrlCmd | monacoApi.KeyCode.KeyF, () => post({ type: "command", command: "find" }));
            secondaryEditor.addCommand(monacoApi.KeyMod.CtrlCmd | monacoApi.KeyCode.KeyH, () => post({ type: "command", command: "replace" }));
            secondaryEditor.addCommand(monacoApi.KeyMod.CtrlCmd | monacoApi.KeyCode.KeyP, () => post({ type: "command", command: "quickOpen" }));
            secondaryEditor.addCommand(monacoApi.KeyMod.CtrlCmd | monacoApi.KeyCode.Space, () => post({ type: "command", command: "geminiComplete" }));
            secondaryEditor.addCommand(monacoApi.KeyMod.CtrlCmd | monacoApi.KeyCode.Period, () => requestCodeActions());
            activatePane(1); secondaryEditor.layout();
          }
          break;
        }
        case "setDocument": setDocument(message); break;
        case "setTheme": monacoApi?.editor.setTheme(message.theme || "keyce-night"); break;
        case "setLanguage": {
          const model = editor?.getModel();
          if (model && typeof message.language === "string") monacoApi.editor.setModelLanguage(model, message.language);
          break;
        }
        case "setOptions":
          primaryEditor?.updateOptions(message.options || {});
          secondaryEditor?.updateOptions(message.options || {});
          break;
        case "setUserSnippets": {
          userSnippets = Array.isArray(message.snippets) ? message.snippets.slice(0, 500).filter((snippet) =>
            snippet && typeof snippet.label === "string" && typeof snippet.prefix === "string" && snippet.prefix.length <= 256 &&
            typeof snippet.body === "string" && snippet.body.length <= 32768 && typeof snippet.description === "string" &&
            Array.isArray(snippet.scopes) && snippet.scopes.length <= 32 && snippet.scopes.every((scope) => typeof scope === "string" && scope.length <= 64)) : [];
          break;
        }
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
          const priorSuppression = suppressChanges;
          if (message.suppressChanges === true) suppressChanges = true;
          try {
            for (const edit of edits) {
              const range = {
                startLineNumber: model.getPositionAt(edit.start).lineNumber,
                startColumn: model.getPositionAt(edit.start).column,
                endLineNumber: model.getPositionAt(edit.end).lineNumber,
                endColumn: model.getPositionAt(edit.end).column
              };
              editor.executeEdits("lsp-format", [{ range, text: edit.text, forceMoveMarkers: true }]);
            }
          } finally {
            suppressChanges = priorSuppression;
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
        case "lspNavigationResult": {
          const resolve = pendingNavigation.get(message.requestId);
          if (resolve) {
            pendingNavigation.delete(message.requestId);
            resolve(message);
          }
          break;
        }
        case "lspRenameResult": {
          const finish = pendingRefactorRequests.get(message.requestId);
          if (finish) finish(message);
          break;
        }
        case "applyWorkspaceTexts": {
          if (!Array.isArray(message.documents)) break;
          for (const document of message.documents) {
            if (typeof document?.id !== "string" || typeof document.text !== "string") continue;
            const uri = [...modelIdsByUri.entries()].find(([, id]) => id === document.id)?.[0];
            const model = uri ? models.get(uri) : null;
            if (!model || model.isDisposed() || model.getValue() === document.text) continue;
            model.pushEditOperations([], [{ range: model.getFullModelRange(), text: document.text, forceMoveMarkers: true }], () => null);
          }
          editor?.focus();
          break;
        }
        case "setAiCompletionEnabled": {
          aiCompletionEnabled = message.value === true;
          if (!aiCompletionEnabled) cancelAiCompletions();
          editor?.updateOptions({ inlineSuggest: { enabled: aiCompletionEnabled } });
          break;
        }
        case "geminiInlineCompletionResult": {
          const resolve = pendingAiCompletions.get(message.requestId);
          if (resolve) resolve(message);
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
        case "layout": editor?.layout(); primaryEditor?.layout(); secondaryEditor?.layout(); break;
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
