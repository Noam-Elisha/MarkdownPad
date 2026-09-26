// Builds MarkdownPad.ico from assets/icon/*.svg, rendered by the Microsoft Edge that ships with Windows.
// Sizes that have a pixel-tuned icon-<size>.svg use it; the others are rendered from the master icon.svg.
//
//   node tools/build-icon.mjs              writes MarkdownPad.ico
//   node tools/build-icon.mjs --png <dir>  also writes every size as a PNG, for checking
//
// Needs Node 22+ (built-in fetch and WebSocket); no npm packages.
import { readFileSync, writeFileSync, mkdirSync, existsSync, rmSync, mkdtempSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');
const iconDir = join(repo, 'assets', 'icon');
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const pngArg = process.argv.indexOf('--png');
const pngDir = pngArg > 0 ? process.argv[pngArg + 1] : null;

const edge = ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe'].find(p => existsSync(p));
if (!edge) throw new Error('Microsoft Edge not found');

const delay = ms => new Promise(r => setTimeout(r, ms));
const withTimeout = (promise, what, ms = 15000) => Promise.race([promise,
  delay(ms).then(() => { throw new Error(`timed out ${what}`); })]);

// Port 0 lets Edge pick a free port; it writes the one it chose into the profile folder.
const profile = mkdtempSync(join(tmpdir(), 'mdpad-icon-'));
const browser = spawn(edge, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
  '--no-first-run', 'about:blank'], { stdio: 'ignore' });
const exited = new Promise(r => browser.once('exit', r));
const failedToStart = new Promise((_, fail) => browser.once('error', e => fail(new Error(`could not start Edge: ${e.message}`))));
failedToStart.catch(() => { });
let ws = null;

try {
  let port;
  for (let i = 0; i < 80 && !port; i++) {
    await Promise.race([delay(250), failedToStart]);
    const file = join(profile, 'DevToolsActivePort');
    if (existsSync(file)) port = readFileSync(file, 'utf8').split('\n')[0].trim();
  }
  if (!port) throw new Error('headless Edge did not start');
  const target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page');
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await withTimeout(new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; }), 'connecting to Edge');
  let id = 0, onLoad = null;
  const waiting = new Map();
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.method === 'Page.loadEventFired') onLoad?.();
    else if (waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); }
  };
  ws.onclose = () => { for (const done of waiting.values()) done({ error: { message: 'Edge closed the connection' } }); };
  const send = (method, params = {}) => withTimeout(new Promise((ok, fail) => {
    waiting.set(++id, m => m.error ? fail(new Error(`${method}: ${m.error.message}`)) : ok(m.result));
    ws.send(JSON.stringify({ id, method, params }));
  }), method);

  await send('Page.enable');
  await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } });
  const frames = [];
  for (const size of sizes) {
    const tuned = join(iconDir, `icon-${size}.svg`);
    const svg = readFileSync(existsSync(tuned) ? tuned : join(iconDir, 'icon.svg'), 'utf8')
      .replace(/<svg\b/, `<svg style="display:block;width:${size}px;height:${size}px"`);
    const html = `<!DOCTYPE html><html><body style="margin:0;background:transparent">${svg}</body></html>`;
    await send('Emulation.setDeviceMetricsOverride', { width: size, height: size, deviceScaleFactor: 1, mobile: false });
    const loaded = new Promise(r => { onLoad = r; });
    await send('Page.navigate', { url: 'data:text/html;base64,' + Buffer.from(html).toString('base64') });
    await withTimeout(loaded, `loading the ${size}px icon`);
    const { data } = await send('Page.captureScreenshot',
      { format: 'png', clip: { x: 0, y: 0, width: size, height: size, scale: 1 } });
    const png = Buffer.from(data, 'base64');
    frames.push({ size, png });
    if (pngDir) { mkdirSync(pngDir, { recursive: true }); writeFileSync(join(pngDir, `icon-${size}.png`), png); }
  }

  // ICO container: header, one 16-byte directory entry per frame, then the PNG-compressed frames.
  const header = Buffer.alloc(6);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(frames.length, 4);
  const entries = Buffer.alloc(16 * frames.length);
  let offset = header.length + entries.length;
  frames.forEach(({ size, png }, i) => {
    const e = 16 * i;
    entries.writeUInt8(size % 256, e);       // 0 means 256
    entries.writeUInt8(size % 256, e + 1);
    entries.writeUInt16LE(1, e + 4);          // color planes
    entries.writeUInt16LE(32, e + 6);         // bits per pixel
    entries.writeUInt32LE(png.length, e + 8);
    entries.writeUInt32LE(offset, e + 12);
    offset += png.length;
  });
  writeFileSync(join(repo, 'MarkdownPad.ico'), Buffer.concat([header, entries, ...frames.map(f => f.png)]));
  console.log(`MarkdownPad.ico: ${frames.map(f => f.size).join(', ')} px (${offset} bytes)`);
} finally {
  // Closing through DevTools shuts Edge's helper processes down too; kill it only if that doesn't work.
  if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ id: 0, method: 'Browser.close' }));
  const closed = await Promise.race([exited.then(() => true), delay(5000).then(() => false)]);
  if (!closed) { browser.kill(); await Promise.race([exited, delay(5000)]); }
  // Windows keeps the temporary profile locked for a moment after Edge exits (about a second).
  for (let attempt = 1; ; attempt++) {
    try { rmSync(profile, { recursive: true, force: true }); break; }
    catch (e) {
      if (attempt === 40) { console.warn(`could not remove ${profile}: ${e.message}`); break; }
      await delay(250);
    }
  }
}
