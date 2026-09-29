import { test, expect } from '@playwright/test';
const snapshot = page => page.evaluate(() => window.histosetsPreview.snapshot());
async function load(page, payload) {
  await page.evaluate(payload => window.histosetsPreview.load(payload), payload);
  await expect.poll(async () => (await snapshot(page)).loaded).toBe(true);
}
test.beforeEach(async ({ page }) => {
  await page.goto('/');
  await page.waitForFunction(() => window.histosetsPreview);
});

test('all atlas records: names, regions, DPI geometry, zoom and switching', async ({ page, request }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const external = [];
  page.on('request', req => { if (!req.url().startsWith('http://127.0.0.1:4173') && !req.url().startsWith('data:')) external.push(req.url()); });
  const atlas = await (await request.get('/fixtures/atlas.json')).json();
  let total = 0;
  for (const specimen of atlas) {
    await load(page, specimen);
    const before = await snapshot(page);
    const expected = specimen.elements.flatMap(e => e.polygons);
    expect(before.annotations.map(a => a.target.selector.geometry.points)).toEqual(expected);
    total += before.annotations.length;
    await expect(page.locator('#elements button')).toHaveCount(specimen.elements.length);
    if (specimen.elements.length) {
      await page.locator('#elements button').first().click();
      await expect(page.locator('#elements button').first()).toHaveAttribute('aria-pressed', 'true');
    }
    await page.getByRole('button', { name: 'Приблизить', exact: true }).click();
    await expect.poll(async () => (await snapshot(page)).zoom).toBeGreaterThan(before.zoom);
    expect((await snapshot(page)).annotations).toEqual(before.annotations);
  }
  expect(total).toBe(66);
  expect(errors).toEqual([]);
  expect(external).toEqual([]);
  // The high-DPI cornea must occupy source pixels rather than a 103 x 77 corner.
  const cornea = atlas.find(s => s.width === 2592);
  await load(page, cornea);
  expect(Math.max(...(await snapshot(page)).annotations.flatMap(a => a.target.selector.geometry.points.map(p => p[0])))).toBeGreaterThan(2500);
  await page.screenshot({ path: '../artifacts/viewer-cornea.png' });
});

test('draw rectangle and polygon, round-trip temporary annotations, reset', async ({ page, request }) => {
  const atlas = await (await request.get('/fixtures/atlas.json')).json();
  await load(page, { ...atlas[0], elements: [] });
  const box = await page.locator('#viewer').boundingBox();
  await page.locator('#tool').selectOption('rectangle');
  await page.mouse.move(box.x + box.width * .3, box.y + box.height * .3);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * .5, box.y + box.height * .5, { steps: 10 });
  await page.mouse.up();
  await page.locator('#tool').selectOption('navigate');
  await expect.poll(async () => (await snapshot(page)).annotations.length).toBe(1);
  await page.locator('#tool').selectOption('polygon');
  for (const [x, y] of [[.6, .3], [.8, .35], [.7, .55], [.6, .3]])
    await page.mouse.click(box.x + box.width * x, box.y + box.height * y);
  await page.locator('#tool').selectOption('navigate');
  await expect.poll(async () => (await snapshot(page)).annotations.length).toBe(2);
  const saved = (await snapshot(page)).annotations;
  expect(saved.map(a => a.target.selector.type).sort()).toEqual(['POLYGON', 'RECTANGLE']);
  await page.evaluate(() => window.histosetsPreview.restore([]));
  await page.evaluate(data => window.histosetsPreview.restore(data), saved);
  expect((await snapshot(page)).annotations).toEqual(saved);
  await page.getByRole('button', { name: 'Восстановить исходные контуры' }).click();
  expect((await snapshot(page)).annotations).toEqual([]);
});

test('201 MP DZI loads tiles, zooms, and shows no stale regions', async ({ page, request }) => {
  const atlas = await (await request.get('/fixtures/atlas.json')).json();
  await load(page, atlas[0]);
  const tiles = [];
  page.on('response', res => { if (res.url().includes('synthetic_files/') && res.ok()) tiles.push(res.url()); });
  await load(page, { title: 'Синтетическая пирамида 201 Мп', tileSource: '/fixtures/synthetic.dzi', elements: [] });
  await expect(page.locator('#status')).toContainText('16384 × 12288');
  expect((await snapshot(page)).annotations).toEqual([]);
  await expect.poll(() => tiles.length).toBeGreaterThan(0);
  const initialLevels = new Set(tiles.map(url => url.split('/').at(-2)));
  for (let i = 0; i < 5; i++) await page.getByRole('button', { name: 'Приблизить', exact: true }).click();
  await expect.poll(() => tiles.some(url => !initialLevels.has(url.split('/').at(-2)))).toBe(true);
  await page.screenshot({ path: '../artifacts/viewer-dzi.png' });
});

test('missing image reports an error and a subsequent image recovers', async ({ page, request }) => {
  await page.evaluate(() => window.histosetsPreview.load({ title: 'Missing', tileSource: { type: 'image', url: '/missing.jpg' }, elements: [] }));
  await expect(page.locator('#message')).toContainText('Изображение недоступно');
  await expect(page.locator('#zoom-in')).toBeDisabled();
  const atlas = await (await request.get('/fixtures/atlas.json')).json();
  await load(page, atlas[0]);
  await expect(page.locator('#message')).toBeHidden();
});

test('a slow previous image cannot replace the latest selection', async ({ page, request }) => {
  const atlas = await (await request.get('/fixtures/atlas.json')).json();
  let resume;
  const paused = new Promise(resolve => { resume = resolve; });
  await page.route('**/slow.jpg', async route => { await paused; await route.fulfill({ response: await request.get(atlas[0].tileSource.url) }); });
  await page.evaluate(p => { window.slowLoad = window.histosetsPreview.load(p); }, { ...atlas[0], tileSource: { type: 'image', url: '/slow.jpg' } });
  await load(page, atlas[2]);
  resume();
  await page.evaluate(() => window.slowLoad);
  await expect.poll(async () => (await snapshot(page)).title).toBe(atlas[2].title);
  expect((await snapshot(page)).annotations.length).toBe(atlas[2].elements.flatMap(e => e.polygons).length);
});
