// Parses every Mermaid diagram in the given Markdown files with the pinned Mermaid release, which reports the syntax
// errors GitHub's renderer would show in place of a diagram. Usage: node check-mermaid.mjs <file.md>...
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';

// Mermaid sanitizes labels with DOMPurify, which needs a DOM; parsing lays nothing out.
const { window } = new JSDOM('<!doctype html><html><body></body></html>');
for (const name of ['window', 'document', 'navigator', 'Element', 'HTMLElement', 'SVGElement', 'DOMParser']) {
  if (!(name in globalThis)) {
    Object.defineProperty(globalThis, name, { value: name === 'window' ? window : window[name], configurable: true });
  }
}

const { default: mermaid } = await import('mermaid');
mermaid.initialize({ startOnLoad: false });

/** Yields each fenced `mermaid` block of a CommonMark document with the line of its first body line. */
function* mermaidBlocks(text) {
  const lines = text.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const open = /^ {0,3}(`{3,}|~{3,})\s*([^\s`]*)/.exec(lines[i]);
    if (!open) {
      continue;
    }

    const [, fence, info] = open;
    const close = new RegExp(`^ {0,3}${fence[0]}{${fence.length},}\\s*$`);
    const start = i + 1;
    const body = [];
    for (i++; i < lines.length && !close.test(lines[i]); i++) {
      body.push(lines[i]);
    }

    if (info.toLowerCase() === 'mermaid') {
      yield { line: start + 1, source: body.join('\n') };
    }
  }
}

let diagrams = 0;
let failures = 0;
for (const file of process.argv.slice(2)) {
  for (const { line, source } of mermaidBlocks(await readFile(file, 'utf8'))) {
    diagrams++;
    try {
      await mermaid.parse(source);
    } catch (error) {
      failures++;
      const message = String(error?.message ?? error).split('\n').filter(Boolean).slice(0, 3).join(' ');
      console.error(`${file}:${line}: ${message}`);
    }
  }
}

console.log(`Mermaid: ${diagrams} diagrams, ${failures} failed to parse.`);
process.exitCode = failures === 0 ? 0 : 1;
