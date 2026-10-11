import OpenSeadragon from 'openseadragon';
import { createOSDAnnotator } from '@annotorious/openseadragon';
import '@annotorious/openseadragon/annotorious-openseadragon.css';
import './viewer.css';

const $ = id => document.getElementById(id);
const notify = message => window.chrome?.webview?.postMessage(message);
const clone = value => JSON.parse(JSON.stringify(value));
let locale = 'ru';
const t = (ru, en) => locale === 'en' ? en : ru;
const localizedTexts = new Map();
function setText(id, ru, en) {
  localizedTexts.set(id, { ru, en });
  $(id).textContent = t(ru, en);
}
function setLocale(value) {
  locale = value === 'en' ? 'en' : 'ru';
  document.documentElement.lang = locale;
  document.title = t('HISTOSETS — просмотр изображения', 'HISTOSETS — image viewer');
  for (const node of document.querySelectorAll('[data-en]')) {
    node.dataset.ru ??= node.textContent;
    node.textContent = t(node.dataset.ru, node.dataset.en);
  }
  for (const attribute of ['title', 'aria-label']) {
    for (const node of document.querySelectorAll(`[data-${attribute}-en]`)) {
      const ruAttribute = `data-${attribute}-ru`;
      if (!node.hasAttribute(ruAttribute)) node.setAttribute(ruAttribute, node.getAttribute(attribute));
      node.setAttribute(attribute, t(node.getAttribute(ruAttribute), node.getAttribute(`data-${attribute}-en`)));
    }
  }
  for (const [id, text] of localizedTexts) $(id).textContent = t(text.ru, text.en);
  // Relabel the existing view without reloading the image or changing drawing state.
  renderElements();
  status();
}
window.addEventListener('error', () => {
  $('message').hidden = false;
  setText('message', 'Не удалось запустить просмотрщик. Закройте окно и попробуйте снова.',
    'Could not start the viewer. Close this window and try again.');
  notify({ type: 'error' });
});
const viewer = OpenSeadragon({
  id: 'viewer', showNavigationControl: false, showNavigator: true,
  animationTime: .25, blendTime: .1, visibilityRatio: .5,
  maxZoomPixelRatio: 4, crossOriginPolicy: 'Anonymous',
  gestureSettingsMouse: { clickToZoom: false },
  drawer: 'canvas'
});
const anno = createOSDAnnotator(viewer, { drawingEnabled: false, drawingMode: 'drag', autoSave: true, userSelectAction: 'SELECT' });
// Annotorious' POLYGON selector has no hole representation. Keep imported
// compound regions in a read-only SVG layer, with the same image transform.
const svgNamespace = 'http://www.w3.org/2000/svg';
const compoundLayer = document.createElementNS(svgNamespace, 'svg');
compoundLayer.id = 'compound-regions';
compoundLayer.setAttribute('aria-hidden', 'true');
Object.assign(compoundLayer.style, { position: 'absolute', inset: '0', width: '100%', height: '100%', pointerEvents: 'none', overflow: 'hidden' });
const compoundGroup = document.createElementNS(svgNamespace, 'g');
compoundLayer.append(compoundGroup);
viewer.canvas.append(compoundLayer);
let compoundRegions = [];
let elements = [];
let originalAnnotations = [];
let selectedElement = null;
let loaded = false;
let request = 0;
let currentSource = null;
let missingTiles = false;

function setEnabled(enabled) {
  for (const id of ['zoom-in', 'zoom-out', 'home', 'tool', 'reset', 'overlays', 'opacity']) $(id).disabled = !enabled;
}
function style(annotation, state) {
  const active = annotation.properties?.elementId === selectedElement || state?.selected;
  return { fill: active ? '#ffd166' : '#5ee3d9', fillOpacity: Number($('opacity').value) / 100,
    stroke: active ? '#ffd166' : '#5ee3d9', strokeWidth: active ? 3 : 1.5 };
}
anno.setStyle(style);
function toCompoundRegions(items) {
  return items.flatMap(element => (element.regions ?? [])
    .filter(region => region.kind === 'compound')
    .map((region, index) => ({ id: region.id ?? `${element.id}-compound-${index}`,
      elementId: element.id, rings: clone(region.rings) })));
}
function renderCompoundRegions() {
  compoundGroup.replaceChildren();
  for (const region of compoundRegions) {
    const path = document.createElementNS(svgNamespace, 'path');
    path.dataset.regionId = region.id;
    path.dataset.elementId = region.elementId;
    path.setAttribute('fill-rule', 'evenodd');
    path.setAttribute('vector-effect', 'non-scaling-stroke');
    path.setAttribute('d', region.rings.map(ring => ring.map(([x, y], index) => `${index ? 'L' : 'M'}${x} ${y}`).join(' ') + ' Z').join(' '));
    compoundGroup.append(path);
  }
  updateCompoundStyle();
  updateCompoundTransform();
}
function updateCompoundStyle() {
  compoundLayer.style.display = loaded && $('overlays').checked ? '' : 'none';
  for (const path of compoundGroup.children) {
    const active = path.dataset.elementId === selectedElement;
    path.setAttribute('fill', active ? '#ffd166' : '#5ee3d9');
    path.setAttribute('fill-opacity', Number($('opacity').value) / 100);
    path.setAttribute('stroke', active ? '#ffd166' : '#5ee3d9');
    path.setAttribute('stroke-width', active ? '3' : '1.5');
  }
}
function updateCompoundTransform() {
  if (!loaded || !viewer.world.getItemAt(0)) return;
  const origin = viewer.viewport.imageToViewerElementCoordinates(new OpenSeadragon.Point(0, 0));
  const x = viewer.viewport.imageToViewerElementCoordinates(new OpenSeadragon.Point(1, 0));
  const y = viewer.viewport.imageToViewerElementCoordinates(new OpenSeadragon.Point(0, 1));
  compoundGroup.setAttribute('transform', `matrix(${x.x - origin.x} ${x.y - origin.y} ${y.x - origin.x} ${y.y - origin.y} ${origin.x} ${origin.y})`);
}
const regionCount = () => anno.getAnnotations().length + compoundRegions.length;
function toAnnotations(items) {
  return items.flatMap(element => (element.polygons ?? []).map((points, index) => {
    const id = `${element.id}-region-${index}`;
    const xs = points.map(p => p[0]);
    const ys = points.map(p => p[1]);
    return { id, bodies: [{ id: `${id}-label`, annotation: id, purpose: 'describing', value: element.name }],
      properties: { elementId: element.id },
      target: { annotation: id, selector: { type: 'POLYGON', geometry: { points: clone(points),
        bounds: { minX: Math.min(...xs), minY: Math.min(...ys), maxX: Math.max(...xs), maxY: Math.max(...ys) } } } } };
  }));
}
function renderElements() {
  $('elements').replaceChildren();
  for (const element of elements) {
    const button = document.createElement('button');
    button.textContent = element.name;
    button.dataset.elementId = element.id;
    button.setAttribute('aria-pressed', String(element.id === selectedElement));
    button.disabled = !loaded;
    button.addEventListener('click', () => selectElement(element.id));
    $('elements').append(button);
  }
  if (elements.length === 0) $('elements').textContent = t('Для этого изображения нет списка элементов.', 'This image has no element list.');
}
function selectElement(id) {
  selectedElement = id;
  const element = elements.find(item => item.id === id);
  const count = anno.getAnnotations().filter(item => item.properties?.elementId === id).length
    + compoundRegions.filter(item => item.elementId === id).length;
  anno.cancelSelected();
  anno.setStyle(style);
  updateCompoundStyle();
  renderElements();
  setText('selection', `${element?.name ?? 'Элемент'} — ${count ? `областей: ${count}` : 'разметка пока не добавлена'}.`,
    `${element?.name ?? 'Element'} — ${count ? `regions: ${count}` : 'no annotations yet'}.`);
}
function setTool(tool) {
  $('tool').value = tool;
  anno.cancelDrawing();
  anno.cancelSelected();
  if (tool === 'navigate') anno.setDrawingEnabled(false);
  else {
    $('overlays').checked = true;
    anno.setVisible(true);
    anno.setDrawingTool(tool);
    anno.setDrawingMode(tool === 'rectangle' ? 'drag' : 'click');
    anno.setDrawingEnabled(true);
  }
  updateCompoundStyle();
  if (tool === 'polygon') setText('hint',
    'Щелчками добавьте вершины. Завершите полигон щелчком по первой вершине; Esc — отмена.',
    'Click to add vertices. Click the first vertex to finish the polygon; Esc cancels.');
  else if (tool === 'rectangle') setText('hint', 'Нажмите и протяните мышь для создания области. Esc — отмена.',
    'Click and drag to create a region. Esc cancels.');
  else setText('hint', 'Колесо мыши — масштаб. Перетаскивание — перемещение.', 'Use the mouse wheel to zoom and drag to pan.');
}
function status() {
  const item = viewer.world.getItemAt(0);
  if (!loaded || !item) return;
  const size = item.getContentSize();
  const zoom = Math.round(item.viewportToImageZoom(viewer.viewport.getZoom(true)) * 100);
  setText('status', `${size.x} × ${size.y} пикселей · масштаб ${zoom}% · областей: ${regionCount()}`
    + (missingTiles ? ' · Часть тайлов недоступна: проверьте папку пирамиды.' : ''),
    `${size.x} × ${size.y} pixels · zoom ${zoom}% · regions: ${regionCount()}`
    + (missingTiles ? ' · Some tiles are unavailable: check the pyramid folder.' : ''));
}
function resetAnnotations() {
  setTool('navigate');
  selectedElement = null;
  anno.setAnnotations(clone(originalAnnotations));
  anno.setStyle(style);
  updateCompoundStyle();
  renderElements();
  setText('selection', 'Выберите элемент для подсветки.', 'Select an element to highlight it.');
  status();
}

async function load(payload) {
  const generation = ++request;
  loaded = false;
  compoundRegions = [];
  compoundGroup.replaceChildren();
  updateCompoundStyle();
  missingTiles = false;
  setEnabled(false);
  setTool('navigate');
  viewer.close();
  // Loading another image is an import, not an undoable user edit.
  // setAnnotations also avoids Annotorious' empty-history race during early startup.
  anno.setAnnotations([]);
  selectedElement = null;
  elements = payload.elements ?? [];
  currentSource = payload.tileSource;
  originalAnnotations = toAnnotations(elements);
  compoundRegions = toCompoundRegions(elements);
  setText('title', payload.title ?? 'HISTOSETS', payload.title ?? 'HISTOSETS');
  setText('selection', 'Выберите элемент для подсветки.', 'Select an element to highlight it.');
  $('message').hidden = false;
  setText('message', 'Загрузка изображения…', 'Loading image…');
  setText('status', 'Загрузка изображения…', 'Loading image…');
  renderElements();
  try {
    // Resolve before opening so an earlier, slower source cannot replace a newer one.
    const { source } = await viewer.instantiateTileSourceClass({ tileSource: payload.tileSource });
    if (generation !== request) return;
    viewer.addOnceHandler('open', () => {
      if (generation !== request) return;
      loaded = true;
      $('message').hidden = true;
      setEnabled(true);
      anno.setVisible($('overlays').checked);
      resetAnnotations();
      renderCompoundRegions();
      notify({ type: 'loaded' });
    });
    viewer.open(source);
  } catch (error) {
    if (generation !== request) return;
    fail(error);
  }
}
function fail(error) {
  loaded = false;
  updateCompoundStyle();
  setEnabled(false);
  $('message').hidden = false;
  setText('message', 'Изображение недоступно. Проверьте файл и папку с тайлами.', 'The image is unavailable. Check the image file and tile folder.');
  setText('status', 'Ошибка открытия изображения', 'Could not open the image');
  notify({ type: 'error' });
  console.warn('HISTOSETS image load failed', error?.message ?? 'unknown error');
}
viewer.addHandler('open-failed', fail);
viewer.addHandler('tile-load-failed', () => { missingTiles = true; status(); notify({ type: 'error' }); });
viewer.addHandler('animation', status);
viewer.addHandler('resize', status);
viewer.addHandler('animation', updateCompoundTransform);
viewer.addHandler('resize', updateCompoundTransform);
viewer.addHandler('viewport-change', updateCompoundTransform);
anno.on('createAnnotation', () => { status(); setText('selection', 'Пробная область создана. Изменения не сохраняются.',
  'Trial region created. Changes are not saved.'); });
anno.on('updateAnnotation', status);
anno.on('selectionChanged', annotations => {
  if (annotations.length) {
    const annotation = annotations[0];
    const label = annotation.bodies?.find(body => body.value)?.value;
    setText('selection', label ?? 'Пробная область', label ?? 'Trial region');
  }
});
$('zoom-in').addEventListener('click', () => { viewer.viewport.zoomBy(1.5); viewer.viewport.applyConstraints(); });
$('zoom-out').addEventListener('click', () => { viewer.viewport.zoomBy(1 / 1.5); viewer.viewport.applyConstraints(); });
$('home').addEventListener('click', () => viewer.viewport.goHome());
$('opacity').addEventListener('input', () => { anno.setStyle(style); updateCompoundStyle(); });
$('overlays').addEventListener('change', () => { setTool('navigate'); anno.setVisible($('overlays').checked); updateCompoundStyle(); });
$('tool').addEventListener('change', event => setTool(event.target.value));
$('reset').addEventListener('click', resetAnnotations);
document.addEventListener('keydown', event => { if (event.key === 'Escape') setTool('navigate'); });
setEnabled(false);
setText('title', 'Просмотр изображения', 'Image viewer');
setText('message', 'Загрузка изображения…', 'Loading image…');
setText('selection', 'Выберите элемент для подсветки.', 'Select an element to highlight it.');
setText('hint', 'Колесо мыши — масштаб. Перетаскивание — перемещение.', 'Use the mouse wheel to zoom and drag to pan.');
setText('status', 'Ожидание изображения', 'Waiting for an image');

// A small host boundary: no filesystem paths, database calls or network services.
window.histosetsPreview = Object.freeze({
  load,
  setLocale,
  snapshot: () => ({ loaded, title: $('title').textContent, source: currentSource,
    annotations: clone(anno.getAnnotations()), compoundRegions: clone(compoundRegions), selectedElement,
    zoom: loaded ? viewer.viewport.getZoom(true) : null }),
  restore: annotations => { if (loaded) { anno.setAnnotations(clone(annotations)); status(); } }
});
window.chrome?.webview?.addEventListener('message', event => {
  if (event.data?.type === 'load') load(event.data);
  else if (event.data?.type === 'locale') setLocale(event.data.locale);
});
notify({ type: 'ready' });
