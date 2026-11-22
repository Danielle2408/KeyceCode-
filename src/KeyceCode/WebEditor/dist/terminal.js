(() => {
  "use strict";

  const host = window.chrome?.webview;
  const terminalRoot = document.getElementById("terminals");
  const themeDark = {
    background: "#181818", foreground: "#cccccc", cursor: "#aeafad", cursorAccent: "#181818",
    selectionBackground: "#264f78", black: "#000000", red: "#cd3131", green: "#0dbc79", yellow: "#e5e510",
    blue: "#2472c8", magenta: "#bc3fbc", cyan: "#11a8cd", white: "#e5e5e5", brightBlack: "#666666",
    brightRed: "#f14c4c", brightGreen: "#23d18b", brightYellow: "#f5f543", brightBlue: "#3b8eea",
    brightMagenta: "#d670d6", brightCyan: "#29b8db", brightWhite: "#ffffff"
  };
  const themeLight = {
    background: "#ffffff", foreground: "#333333", cursor: "#000000", cursorAccent: "#ffffff",
    selectionBackground: "#add6ff", black: "#000000", red: "#cd3131", green: "#008000", yellow: "#795e26",
    blue: "#0451a5", magenta: "#af00db", cyan: "#008080", white: "#e5e5e5", brightBlack: "#666666",
    brightRed: "#cd3131", brightGreen: "#14ce14", brightYellow: "#b5ba00", brightBlue: "#0451a5",
    brightMagenta: "#bc05bc", brightCyan: "#0598bc", brightWhite: "#a5a5a5"
  };
  const sessions = new Map();
  let activeId = null;
  let currentTheme = "dark";
  let currentOptions = {};
  let disposed = false;

  function post(message) {
    if (host) host.postMessage(message);
    else window.parent.postMessage({ source: "keycecode-terminal", ...message }, "*");
  }

  function fitAndReport(session) {
    if (!session || disposed || session.id !== activeId) return;
    window.clearTimeout(session.resizeTimer);
    session.resizeTimer = window.setTimeout(() => {
      if (disposed || session.id !== activeId) return;
      try {
        session.fitAddon.fit();
        post({ type: "resize", id: session.id, cols: session.terminal.cols, rows: session.terminal.rows });
      } catch (error) { post({ type: "terminalError", id: session.id, message: String(error?.message || error) }); }
    }, 40);
  }

  function setTheme(name) {
    currentTheme = String(name || "dark");
    const light = currentTheme === "light" || currentTheme === "vs" || currentTheme === "hc-light";
    const theme = light ? themeLight : themeDark;
    for (const session of sessions.values()) session.terminal.options.theme = theme;
    document.documentElement.style.colorScheme = light ? "light" : "dark";
    document.documentElement.style.setProperty("--terminal-bg", theme.background);
    document.documentElement.style.setProperty("--terminal-fg", theme.foreground);
    document.documentElement.style.setProperty("--terminal-cursor", theme.cursor);
  }

  function createSession(id, profile) {
    id = String(id || "");
    if (!id || id.length > 128) return null;
    if (sessions.has(id)) return sessions.get(id);
    const view = document.createElement("section");
    view.className = "terminal-view";
    view.setAttribute("aria-label", `Terminal ${String(profile || "")}`);
    terminalRoot.appendChild(view);
    const terminal = new Terminal({
      allowProposedApi: false, allowTransparency: false, convertEol: false, cursorBlink: true,
      cursorStyle: "bar", cursorWidth: 2, fontFamily: '"Cascadia Code", Consolas, monospace',
      fontSize: 13, lineHeight: 1.15, scrollback: 5000, smoothScrollDuration: 0,
      theme: currentTheme === "light" || currentTheme === "vs" || currentTheme === "hc-light" ? themeLight : themeDark,
      windowsMode: true, rightClickSelectsWord: true, overviewRulerWidth: 10, ...currentOptions
    });
    const fitAddon = new FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(view);
    const session = { id, profile: String(profile || ""), terminal, fitAddon, view, resizeTimer: 0, observer: null };
    terminal.onData((data) => post({ type: "input", id, data }));
    terminal.onResize(({ cols, rows }) => post({ type: "resize", id, cols, rows }));
    session.observer = new ResizeObserver(() => fitAndReport(session));
    session.observer.observe(view);
    sessions.set(id, session);
    if (activeId === null) switchSession(id);
    return session;
  }

  function switchSession(id) {
    const session = sessions.get(String(id));
    if (!session) return;
    activeId = session.id;
    for (const item of sessions.values()) item.view.classList.toggle("active", item === session);
    requestAnimationFrame(() => {
      fitAndReport(session);
      session.terminal.focus();
    });
  }

  function removeSession(id) {
    const session = sessions.get(String(id));
    if (!session) return;
    window.clearTimeout(session.resizeTimer);
    session.observer.disconnect();
    session.terminal.dispose();
    session.view.remove();
    sessions.delete(session.id);
    if (activeId === session.id) {
      activeId = null;
      const next = sessions.keys().next().value;
      if (next) switchSession(next);
    }
  }

  function dispatch(message) {
    if (!message || typeof message !== "object") return;
    const id = String(message.id || activeId || "");
    switch (message.type) {
      case "createSession": createSession(id, message.profile); break;
      case "switchSession": switchSession(id); break;
      case "removeSession": removeSession(id); break;
      case "write": sessions.get(id)?.terminal.write(String(message.data ?? "")); break;
      case "clear": sessions.get(id)?.terminal.clear(); break;
      case "resize": {
        const session = sessions.get(id);
        if (session && Number.isInteger(message.cols) && Number.isInteger(message.rows) && message.cols > 0 && message.rows > 0) {
          session.terminal.resize(message.cols, message.rows);
          fitAndReport(session);
        }
        break;
      }
      case "fit": fitAndReport(sessions.get(id)); break;
      case "setTheme": setTheme(message.theme); break;
      case "setOptions": {
        const options = message.options || {};
        currentOptions = {};
        if (Number.isFinite(options.fontSize)) currentOptions.fontSize = Math.max(8, Math.min(40, options.fontSize));
        if (typeof options.fontFamily === "string" && options.fontFamily.length < 256) currentOptions.fontFamily = options.fontFamily;
        if (Number.isFinite(options.scrollback)) currentOptions.scrollback = Math.max(0, Math.min(100000, options.scrollback));
        for (const session of sessions.values()) session.terminal.options = { ...session.terminal.options, ...currentOptions };
        fitAndReport(sessions.get(activeId));
        break;
      }
      case "focus": sessions.get(activeId)?.terminal.focus(); break;
      case "dispose": dispose(); break;
      default: post({ type: "terminalError", message: `Message hôte inconnu : ${String(message.type)}` });
    }
  }

  function dispose() {
    if (disposed) return;
    disposed = true;
    for (const id of [...sessions.keys()]) removeSession(id);
    post({ type: "closed" });
  }

  try {
    window.keyceTerminal = { dispatch, focus: () => sessions.get(activeId)?.terminal.focus(), get sessionCount() { return sessions.size; } };
    if (host) host.addEventListener("message", (event) => dispatch(event.data));
    else window.addEventListener("message", (event) => {
      if (event.data?.target === "keycecode-terminal") dispatch(event.data.message);
    });
    window.addEventListener("pagehide", dispose, { once: true });
    post({ type: "ready", capabilities: ["vt", "selection", "clipboard", "fit", "resize", "sessions"] });
  } catch (error) { post({ type: "terminalError", message: String(error?.stack || error?.message || error) }); }
})();
