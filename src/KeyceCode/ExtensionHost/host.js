"use strict";

// Small, out-of-process compatibility host. This is process separation, not a security sandbox.
const fs = require("node:fs");
const path = require("node:path");
const readline = require("node:readline");
const Module = require("node:module");

let nextId = 0;
const extensions = new Map();
const pending = new Map();
let workspaceRoot = null;
const send = (message) => process.stdout.write(JSON.stringify(message) + "\n");
const log = (...args) => process.stderr.write(args.map(String).join(" ") + "\n");
console.log = console.info = console.warn = console.error = log;

const commandsApi = {
  registerCommand(id, callback, thisArg) {
    if (typeof id !== "string" || !id || typeof callback !== "function") throw new TypeError("registerCommand requires an id and callback");
    const extension = currentExtension;
    if (!extension) throw new Error("Commands can only be registered while an extension is activating");
    const previous = extension.commands.get(id);
    if (previous) throw new Error(`Command already registered by this extension: ${id}`);
    const registration = { callback: (...args) => callback.apply(thisArg, args) };
    extension.commands.set(id, registration);
    send({ type: "commandRegistered", extensionId: extension.id, command: id });
    return { dispose() { extension.commands.delete(id); } };
  },
  async executeCommand(id, ...args) {
    for (const extension of extensions.values()) {
      const registered = extension.commands.get(id);
      if (registered) return registered.callback(...args);
    }
    const requestId = `host-${++nextId}`;
    return new Promise((resolve) => {
      pending.set(requestId, resolve);
      send({ type: "executeHostCommand", requestId, command: id, args });
      setTimeout(() => { if (pending.delete(requestId)) resolve(undefined); }, 10000).unref();
    });
  },
  getCommands() { return Promise.resolve([...extensions.values()].flatMap(extension => [...extension.commands.keys()])); }
};

const showMessage = (level) => async (message) => {
  send({ type: "notification", level, message: String(message ?? "") });
  return undefined;
};

const vscodeApi = {
  version: "1.0.0-keycecode",
  env: { appName: "KeyceCode", language: "fr", uiKind: 1, appHost: "desktop" },
  commands: commandsApi,
  window: {
    showInformationMessage: showMessage("info"),
    showWarningMessage: showMessage("warning"),
    showErrorMessage: showMessage("error"),
    showTextDocument: async (document) => document
  },
  workspace: {
    get workspaceFolders() { return workspaceRoot ? [{ uri: { scheme: "file", fsPath: workspaceRoot, path: workspaceRoot }, name: path.basename(workspaceRoot), index: 0 }] : undefined; },
    get rootPath() { return workspaceRoot; },
    get name() { return workspaceRoot ? path.basename(workspaceRoot) : undefined; },
    getConfiguration(section = "") {
      return { get(key, defaultValue) { return defaultValue; }, has() { return false; }, update: async () => {}, inspect: () => undefined };
    },
    fs: {
      async readFile(uri) { return fs.promises.readFile(uri.fsPath || uri.path); },
      async writeFile(uri, content) { return fs.promises.writeFile(uri.fsPath || uri.path, content); },
      async stat(uri) { const info = await fs.promises.stat(uri.fsPath || uri.path); return { type: info.isDirectory() ? 2 : 1, ctime: info.ctimeMs, mtime: info.mtimeMs, size: info.size }; },
      async readDirectory(uri) { return (await fs.promises.readdir(uri.fsPath || uri.path, { withFileTypes: true })).map(entry => [entry.name, entry.isDirectory() ? 2 : 1]); },
      async createDirectory(uri) { await fs.promises.mkdir(uri.fsPath || uri.path, { recursive: true }); },
      async delete(uri, options = {}) { await fs.promises.rm(uri.fsPath || uri.path, { recursive: options.recursive === true, force: false }); },
      async rename(from, to, options = {}) { if (!options.overwrite && fs.existsSync(to.fsPath || to.path)) throw new Error("Target exists"); await fs.promises.rename(from.fsPath || from.path, to.fsPath || to.path); }
    }
  },
  Uri: {
    file(value) { const resolved = path.resolve(value); return { scheme: "file", fsPath: resolved, path: resolved, toString: () => `file://${resolved.replace(/\\/g, "/")}` }; },
    parse(value) { return { toString: () => String(value), fsPath: String(value).replace(/^file:\/\//, "") }; }
  },
  EventEmitter: class EventEmitter {
    constructor() { this.listeners = new Set(); this.event = (listener) => { this.listeners.add(listener); return { dispose: () => this.listeners.delete(listener) }; }; }
    fire(value) { for (const listener of this.listeners) { try { listener(value); } catch (error) { log(error?.stack || error); } } }
    dispose() { this.listeners.clear(); }
  },
  Disposable: class Disposable { constructor(call) { this.call = call; } dispose() { this.call?.(); this.call = undefined; } static from(...values) { return new vscodeApi.Disposable(() => values.forEach(value => value?.dispose?.())); } }
};

const originalLoad = Module._load;
Module._load = function(request, parent, isMain) {
  if (request === "vscode") return vscodeApi;
  return originalLoad.call(this, request, parent, isMain);
};
let currentExtension = null;

async function activate(message) {
  const directory = path.resolve(message.path);
  const manifestPath = path.join(directory, "extension", "package.json");
  const manifest = JSON.parse(await fs.promises.readFile(manifestPath, "utf8"));
  if (!manifest.main) throw new Error("L’extension ne déclare pas de point d’entrée CommonJS (champ main).");
  const extensionDirectory = path.join(directory, "extension");
  const entry = path.resolve(extensionDirectory, manifest.main);
  if (entry !== extensionDirectory && !entry.startsWith(extensionDirectory + path.sep)) throw new Error("Le point d’entrée sort du dossier de l’extension.");
  if (!fs.existsSync(entry)) throw new Error(`Point d’entrée absent : ${manifest.main}`);
  const id = `${manifest.publisher}.${manifest.name}`;
  if (extensions.has(id)) { send({ type: "activated", requestId: message.requestId, extensionId: id, commands: [] }); return; }
  const extension = { id, manifest, directory: extensionDirectory, commands: new Map(), exports: {}, subscriptions: [] };
  extensions.set(id, extension);
  workspaceRoot = message.workspaceRoot || null;
  try {
    currentExtension = extension;
    const storagePath = path.join(message.storagePath, id);
    await fs.promises.mkdir(storagePath, { recursive: true });
    const module = require(entry);
    extension.exports = module || {};
    if (typeof extension.exports.activate === "function") {
      await extension.exports.activate({
        subscriptions: extension.subscriptions, extensionPath: extensionDirectory,
        extensionUri: vscodeApi.Uri.file(extensionDirectory),
        globalStoragePath: storagePath,
        storagePath,
        asAbsolutePath: relative => path.resolve(extensionDirectory, relative),
        extensionMode: 1
      });
    }
    currentExtension = null;
    send({ type: "activated", requestId: message.requestId, extensionId: id, commands: [...extension.commands.keys()] });
  } catch (error) {
    currentExtension = null;
    extensions.delete(id);
    send({ type: "activationFailed", requestId: message.requestId, extensionId: id, error: error?.stack || String(error) });
  }
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", async line => {
  let message;
  try { message = JSON.parse(line); } catch (error) { log("Invalid host command", error); return; }
  if (message.type === "activate") { await activate(message); return; }
  if (message.type === "invokeCommand") {
    const extension = extensions.get(message.extensionId);
    const registered = extension?.commands.get(message.command);
    try {
      if (!registered) throw new Error(`Commande indisponible : ${message.command}`);
      const result = await registered.callback(...(Array.isArray(message.args) ? message.args : []));
      send({ type: "commandCompleted", requestId: message.requestId, result });
    } catch (error) {
      send({ type: "commandFailed", requestId: message.requestId, extensionId: message.extensionId, command: message.command, error: error?.stack || String(error) });
    }
    return;
  }
  if (message.type === "hostCommandResult") {
    const resolve = pending.get(message.requestId);
    if (resolve) { pending.delete(message.requestId); resolve(message.result); }
  }
});
input.on("close", async () => {
  for (const extension of [...extensions.values()].reverse()) {
    try { if (typeof extension.exports.deactivate === "function") await extension.exports.deactivate(); }
    catch (error) { log(`[${extension.id}] deactivate:`, error?.stack || error); }
    for (const subscription of extension.subscriptions) {
      try { subscription?.dispose?.(); } catch (error) { log(`[${extension.id}] dispose:`, error?.stack || error); }
    }
  }
  process.exit(0);
});

process.on("uncaughtException", error => { log(error?.stack || error); });
process.on("unhandledRejection", error => { log(error?.stack || error); });
send({ type: "ready" });
