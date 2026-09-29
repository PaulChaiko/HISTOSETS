import { build } from 'esbuild';
import { mkdir, copyFile, rm, readFile, readdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
process.chdir(fileURLToPath(new URL('.', import.meta.url)));
await rm('dist', { recursive: true, force: true });
await mkdir('dist');
const result = await build({ entryPoints: ['src/viewer.js'], bundle: true, minify: true,
  sourcemap: false, metafile: true, outfile: 'dist/viewer.js', target: ['es2022'], legalComments: 'linked',
  loader: { '.png': 'file' }, assetNames: 'branding/[name]' });
await copyFile('src/index.html', 'dist/index.html');
await mkdir('dist/branding', { recursive: true });
await copyFile('../LOGO/LOGO2.png', 'dist/branding/LOGO2.png');
let notices = await readFile('THIRD-PARTY-NOTICES.txt', 'utf8');
const packages = new Set(Object.keys(result.metafile.inputs).flatMap(path => {
  const match = path.match(/node_modules\/((?:@[^/]+\/)?[^/]+)\//);
  return match ? [match[1]] : [];
}));
// Annotorious ships prebundled dependencies, invisible to esbuild's input graph.
const lock = JSON.parse(await readFile('package-lock.json', 'utf8'));
for (const [path, pkg] of Object.entries(lock.packages)) {
  if (path.startsWith('node_modules/') && !pkg.dev) packages.add(path.slice('node_modules/'.length));
}
notices += '\n\nSvelte (included in Annotorious)\n' + await readFile('licenses/svelte.txt', 'utf8');
for (const name of [...packages].sort()) {
  if (name === 'openseadragon' || name.startsWith('@annotorious/')) continue;
  const root = `node_modules/${name}`;
  const pkg = JSON.parse(await readFile(`${root}/package.json`, 'utf8'));
  if (name === '@pixi/colord') {
    notices += `\n\n${name} ${pkg.version}\n` + await readFile('licenses/colord.txt', 'utf8');
    continue;
  }
  const files = (await readdir(root)).filter(file => /^(license|licence|copying)([.-].*)?$/i.test(file));
  if (!files.length && name.startsWith('@pixi/')) {
    notices += `\n\n${name} ${pkg.version} (PixiJS MIT)\n` + await readFile('node_modules/pixi.js/LICENSE', 'utf8');
    continue;
  }
  if (!files.length) throw new Error(`Missing license notice for bundled package ${name}`);
  notices += `\n\n${name} ${pkg.version} (${pkg.license})\n`;
  for (const file of files) notices += await readFile(`${root}/${file}`, 'utf8');
}
await writeFile('dist/THIRD-PARTY-NOTICES.txt', notices);
