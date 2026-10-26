// Captures reference images from the web build (live site by default) with headless Chrome and WebGPU, through the
// page's own test hook (window.__stormlab: setPreset, setSeed, setTime, viewCam, captureImage). Writes web_<name>.png
// and cases.json (preset, seed, storm time and the exact camera of each image), which the Unity harness renders with
// the same cameras: StormLabTests -slCompare validation/website/cases.json.
//   node validation/website/capture.mjs [url]
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';

const here = dirname(fileURLToPath(import.meta.url));
const url = process.argv[2] ?? 'https://jonnyglessner-jpg.github.io/StormLab/#storm';
const chrome = process.env.CHROME ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const port = 9333, W = 1600, H = 1000;
const profile = join(tmpdir(), 'stormlab-capture-chrome'); mkdirSync(profile, { recursive: true });
const proc = spawn(chrome, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--enable-unsafe-webgpu',
  '--enable-gpu', '--ignore-gpu-blocklist', '--window-size=1600,1000', '--no-first-run', 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let ws;
try {
  let target;
  for (let i = 0; i < 50 && !target; i++) {
    try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page'); } catch { await sleep(200); }
  }
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise(r => ws.addEventListener('open', r, { once: true }));
  let id = 0; const pending = new Map();
  ws.addEventListener('message', ev => { const m = JSON.parse(ev.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } });
  const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  const evalJs = async expr => {
    const m = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
    if (m.result?.exceptionDetails) throw new Error(JSON.stringify(m.result.exceptionDetails));
    return m.result.result.value;
  };
  await send('Page.navigate', { url });
  for (let i = 0; i < 100; i++) { if (await evalJs('!!window.__stormlab && !!window.__stormlab.kind()')) break; await sleep(300); }
  const kind = await evalJs('window.__stormlab.kind()');
  const adapter = await evalJs("navigator.gpu ? navigator.gpu.requestAdapter().then(a => a ? (a.info ? a.info.vendor + ' ' + a.info.architecture + ' ' + a.info.description : 'adapter') : 'none') : 'no webgpu'");
  console.log('renderer', kind, '| adapter', adapter);
  const wanted = [
    ['plains', 42, 'plains_012', 12], ['plains', 42, 'plains_022', 22], ['plains', 42, 'plains_036', 36],
    ['plains', 42, 'plains_tornado1', c => c[0].times[3]], ['plains', 42, 'plains_tornado2', c => c[1].times[3]],
    ['lp', 42, 'lp_mature', c => c ? c[0].times[3] : 50], ['dixie', 42, 'dixie_mature', c => c ? c[0].times[3] : 50],
  ];
  const cases = [];
  for (const [preset, seed, name, tSpec] of wanted) {
    await evalJs(`(async () => { const s = window.__stormlab; s.setPreset('${preset}'); s.setSeed(${seed}); await new Promise(r => setTimeout(r, 800)); })()`);
    const cycles = await evalJs('window.__stormlab.state().cycles');
    const t = typeof tSpec === 'function' ? tSpec(cycles) : tSpec;
    // set the time twice: the camera rig eases toward the storm; settle it before reading the camera
    const res = await evalJs(`(async () => {
      const s = window.__stormlab; s.setTime(${t}); await new Promise(r => setTimeout(r, 2500)); s.setTime(${t});
      await new Promise(r => setTimeout(r, 300));
      const cam = s.viewCam(); const img = await s.captureImage(${W}, ${H});
      const c = document.createElement('canvas'); c.width = img.width; c.height = img.height; c.getContext('2d').putImageData(img, 0, 0);
      return { cam, stage: s.state().stage, png: c.toDataURL('image/png') };
    })()`);
    writeFileSync(join(here, `web_${name}.png`), Buffer.from(res.png.split(',')[1], 'base64'));
    const cam = { az: res.cam.az, dist: res.cam.dist, h: res.cam.h, tz: res.cam.tz, tx: res.cam.tx, ty: res.cam.ty };
    cases.push({ name, preset, seed, varied: true, t, cam, w: W, h: H, stage: res.stage });
    console.log(name, t.toFixed(2), res.stage);
  }
  writeFileSync(join(here, 'cases.json'), JSON.stringify({ source: url, renderer: kind, adapter, cases }, null, 1));
} finally {
  try { ws?.close(); } catch {}
  proc.kill();
}
