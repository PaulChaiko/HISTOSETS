import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, sep, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../', import.meta.url));
const maps = { '/specimens/': resolve(root, '../SPECIMENS'), '/fixtures/': resolve(root, '../artifacts/viewer-fixtures'), '/': resolve(root, 'dist') };
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.jpg': 'image/jpeg', '.png': 'image/png', '.dzi': 'application/xml' };
createServer(async (req, res) => {
  try {
    const path = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
    const prefix = Object.keys(maps).find(prefix => path.startsWith(prefix));
    const base = maps[prefix];
    const file = resolve(base, path.slice(prefix.length) || 'index.html');
    if (!file.startsWith(base + sep)) { res.writeHead(403).end(); return; }
    const content = await readFile(file);
    res.writeHead(200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream' });
    res.end(content);
  } catch { res.writeHead(404).end(); }
}).listen(4173, '127.0.0.1');
