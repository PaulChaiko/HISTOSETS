import OpenSeadragon from 'openseadragon';
import { createOSDAnnotator } from '@annotorious/openseadragon';
import '@annotorious/openseadragon/annotorious-openseadragon.css';
import './viewer.css';

const $ = id => document.getElementById(id);
const notify = message => window.chrome?.webview?.postMessage(message);
const clone = value => JSON.parse(JSON.stringify(value));
window.addEventListener('error', () => {
  $('message').hidden = false;
  $('message').textContent = 'Не удалось запустить просмотрщик. Закройте окно и попробуйте снова.';
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
  if (elements.length === 0) $('elements').textContent = 'Для этого изображения нет списка элементов.';
}
function selectElement(id) {
  selectedElement = id;
  const element = elements.find(item => item.id === id);
  const count = anno.getAnnotations().filter(item => item.properties?.elementId === id).length;
  anno.cancelSelected();
  anno.setStyle(style);
  renderElements();
  $('selection').textContent = `${element?.name ?? 'Элемент'} — ${count ? `областей: ${count}` : 'разметка пока не добавлена'}.`;
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
  $('hint').textContent = tool === 'polygon'
    ? 'Щелчками добавьте вершины. Завершите полигон щелчком по первой вершине; Esc — отмена.'
    : tool === 'rectangle' ? 'Нажмите и протяните мышь для создания области. Esc — отмена.'
      : 'Колесо мыши — масштаб. Перетаскивание — перемещение.';
}
function status() {
  const item = viewer.world.getItemAt(0);
  if (!loaded || !item) return;
  const size = item.getContentSize();
  const zoom = Math.round(item.viewportToImageZoom(viewer.viewport.getZoom(true)) * 100);
  $('status').textContent = `${size.x} × ${size.y} пикселей · масштаб ${zoom}% · областей: ${anno.getAnnotations().length}`
    + (missingTiles ? ' · Часть тайлов недоступна: проверьте папку пирамиды.' : '');
}
function resetAnnotations() {
  setTool('navigate');
  selectedElement = null;
  anno.setAnnotations(clone(originalAnnotations));
  anno.setStyle(style);
  renderElements();
  $('selection').textContent = 'Выберите элемент для подсветки.';
  status();
}

async function load(payload) {
  const generation = ++request;
  loaded = false;
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
  $('title').textContent = payload.title ?? 'HISTOSETS';
  $('selection').textContent = 'Выберите элемент для подсветки.';
  $('message').hidden = false;
  $('message').textContent = 'Загрузка изображения…';
  $('status').textContent = 'Загрузка изображения…';
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
  setEnabled(false);
  $('message').hidden = false;
  $('message').textContent = 'Изображение недоступно. Проверьте файл и папку с тайлами.';
  $('status').textContent = 'Ошибка открытия изображения';
  notify({ type: 'error' });
  console.warn('HISTOSETS image load failed', error?.message ?? 'unknown error');
}
viewer.addHandler('open-failed', fail);
viewer.addHandler('tile-load-failed', () => { missingTiles = true; status(); notify({ type: 'error' }); });
viewer.addHandler('animation', status);
viewer.addHandler('resize', status);
anno.on('createAnnotation', () => { status(); $('selection').textContent = 'Пробная область создана. Изменения не сохраняются.'; });
anno.on('updateAnnotation', status);
anno.on('selectionChanged', annotations => {
  if (annotations.length) {
    const annotation = annotations[0];
    $('selection').textContent = annotation.bodies?.find(body => body.value)?.value ?? 'Пробная область';
  }
});
$('zoom-in').addEventListener('click', () => { viewer.viewport.zoomBy(1.5); viewer.viewport.applyConstraints(); });
$('zoom-out').addEventListener('click', () => { viewer.viewport.zoomBy(1 / 1.5); viewer.viewport.applyConstraints(); });
$('home').addEventListener('click', () => viewer.viewport.goHome());
$('opacity').addEventListener('input', () => anno.setStyle(style));
$('overlays').addEventListener('change', () => { setTool('navigate'); anno.setVisible($('overlays').checked); });
$('tool').addEventListener('change', event => setTool(event.target.value));
$('reset').addEventListener('click', resetAnnotations);
document.addEventListener('keydown', event => { if (event.key === 'Escape') setTool('navigate'); });
setEnabled(false);

// A small host boundary: no filesystem paths, database calls or network services.
window.histosetsPreview = Object.freeze({
  load,
  snapshot: () => ({ loaded, title: $('title').textContent, source: currentSource,
    annotations: clone(anno.getAnnotations()), selectedElement,
    zoom: loaded ? viewer.viewport.getZoom(true) : null }),
  restore: annotations => { if (loaded) { anno.setAnnotations(clone(annotations)); status(); } }
});
window.chrome?.webview?.addEventListener('message', event => {
  if (event.data?.type === 'load') load(event.data);
});
notify({ type: 'ready' });
