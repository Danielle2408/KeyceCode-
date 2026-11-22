import { cp, mkdir, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'dist');
const monaco = path.join(root, 'node_modules', 'monaco-editor', 'min');
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
await cp(path.join(root, 'src'), output, { recursive: true });
await cp(path.join(monaco, 'vs'), path.join(output, 'vs'), { recursive: true });
await cp(path.join(root, 'node_modules', 'monaco-editor', 'LICENSE'), path.join(output, 'MONACO-LICENSE.txt'));
console.log(`Offline Monaco editor assets copied to ${output}`);
