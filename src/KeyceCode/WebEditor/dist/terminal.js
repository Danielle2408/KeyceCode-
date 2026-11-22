(() => {
  "use strict";

  const host = window.chrome?.webview;
  const terminalRoot = document.getElementById("terminal");
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
  let terminal;
  let fitAddon;
  let resizeTimer;
  let disposed = false;

  function post(message) {
    if (host) host.postMessage(message);
    else window.parent.postMessage({ source: "keycecode-terminal", ...message }, "*");
  }

  function fitAndReport() {
    if (!terminal || !fitAddon || disposed) return;
    window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(() => {
      if (disposed) return;
      try {
        fitAddon.fit();
        post({ type: "resize", cols: terminal.cols, rows: terminal.rows });
      } catch (error) {
        post({ type: "terminalError", message: String(error?.message || error) });
      }
    }, 40);
  }

  function setTheme(name) {
    const light = name === "light" || name === "vs" || name === "hc-light";
    const theme = light ? themeLight : themeDark;
    terminal?.options && (terminal.options.theme = theme);
    document.documentElement.style.colorScheme = light ? "light" : "dark";
    document.documentElement.style.setProperty("--terminal-bg", theme.background);
    document.documentElement.style.setProperty("--terminal-fg", theme.foreground);
    document.documentElement.style.setProperty("--terminal-cursor", theme.cursor);
  }

  function dispatch(message) {
    if (!message || typeof message !== "object" || !terminal) return;
    switch (message.type) {
      case "write":
        terminal.write(String(message.data ?? ""));
        break;
      case "clear":
        terminal.clear();
        break;
      case "resize":
        if (Number.isInteger(message.cols) && Number.isInteger(message.rows) && message.cols > 0 && message.rows > 0) {
          terminal.resize(message.cols, message.rows);
          fitAndReport();
        }
        break;
      case "fit":
        fitAndReport();
        break;
      case "setTheme":
        setTheme(String(message.theme || "dark"));
        break;
      case "setOptions": {
        const options = message.options || {};
        if (Number.isFinite(options.fontSize)) {
          const size = Math.max(8, Math.min(40, options.fontSize));
          terminal.options.fontSize = size;
          document.documentElement.style.setProperty("--terminal-font-size", `${size}px`);
        }
        if (typeof options.fontFamily === "string" && options.fontFamily.length < 256) {
          terminal.options.fontFamily = options.fontFamily;
          document.documentElement.style.setProperty("--terminal-font", `${options.fontFamily}, Consolas, monospace`);
        }
        if (Number.isFinite(options.scrollback)) terminal.options.scrollback = Math.max(0, Math.min(100000, options.scrollback));
        if (options.theme) setTheme(options.theme);
        fitAndReport();
        break;
      }
      case "focus":
        terminal.focus();
        break;
      case "dispose":
        if (!disposed) {
          disposed = true;
          terminal.dispose();
          post({ type: "closed" });
        }
        break;
      default:
        post({ type: "terminalError", message: `Message hôte inconnu : ${String(message.type)}` });
    }
  }

  try {
    terminal = new Terminal({
      allowProposedApi: false,
      allowTransparency: false,
      convertEol: false,
      cursorBlink: true,
      cursorStyle: "bar",
      cursorWidth: 2,
      fontFamily: '"Cascadia Code", Consolas, monospace',
      fontSize: 13,
      lineHeight: 1.15,
      scrollback: 5000,
      smoothScrollDuration: 0,
      theme: themeDark,
      windowsMode: true,
      rightClickSelectsWord: true,
      overviewRulerWidth: 10
    });
    fitAddon = new FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(terminalRoot);
    terminal.onData((data) => post({ type: "input", data }));
    terminal.onResize(({ cols, rows }) => post({ type: "resize", cols, rows }));

    window.keyceTerminal = {
      dispatch,
      fit: fitAndReport,
      focus: () => terminal.focus(),
      get dimensions() { return { cols: terminal.cols, rows: terminal.rows }; }
    };

    if (host) host.addEventListener("message", (event) => dispatch(event.data));
    else window.addEventListener("message", (event) => {
      if (event.data?.target === "keycecode-terminal") dispatch(event.data.message);
    });

    const observer = new ResizeObserver(fitAndReport);
    observer.observe(terminalRoot);
    window.addEventListener("pagehide", () => {
      if (disposed) return;
      disposed = true;
      observer.disconnect();
      window.clearTimeout(resizeTimer);
      post({ type: "closed" });
      terminal.dispose();
    }, { once: true });

    fitAndReport();
    post({ type: "ready", cols: terminal.cols, rows: terminal.rows, capabilities: ["vt", "selection", "clipboard", "fit", "resize"] });
    terminal.focus();
  } catch (error) {
    post({ type: "terminalError", message: String(error?.stack || error?.message || error) });
  }
})();
