"""Read unchanged atlas assets; create a clearly synthetic 201 MP tile pyramid."""
import json
import math
from pathlib import Path
from urllib.parse import quote
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[2]
out = root / 'artifacts' / 'viewer-fixtures'
out.mkdir(parents=True, exist_ok=True)
records = []
seed = json.loads((root / 'tools' / 'HistOSets.Demo' / 'catalog-seed.json').read_text(encoding='utf8'))
for locale in ('en', 'ru'):
    for card in seed:
        filename = card['File']
        with Image.open(root / 'SPECIMENS' / filename) as image:
            width, height = image.size
        assert (width, height) == (card['Metadata']['Width'], card['Metadata']['Height'])
        records.append(dict(
            title=card['Texts'][locale]['Name'],
            tileSource=dict(type='image', url='/specimens/' + quote(filename)),
            width=width, height=height,
            elements=[dict(id=f'element-{i}', name=element['Texts'][locale]['Name'],
                           polygons=element['Polygons'])
                      for i, element in enumerate(card['Elements'])]))
(out / 'atlas.json').write_text(json.dumps(records, ensure_ascii=False), encoding='utf8')

width, height, size = 16384, 12288, 256
(out / 'synthetic.dzi').write_text(
    f'<Image TileSize="{size}" Overlap="0" Format="jpg" xmlns="http://schemas.microsoft.com/deepzoom/2008">'
    f'<Size Width="{width}" Height="{height}"/></Image>', encoding='utf8')
maximum = math.ceil(math.log2(max(width, height)))
for level in range(maximum + 1):
    w, h = math.ceil(width / 2 ** (maximum - level)), math.ceil(height / 2 ** (maximum - level))
    folder = out / 'synthetic_files' / str(level)
    folder.mkdir(parents=True, exist_ok=True)
    for y in range(math.ceil(h / size)):
        for x in range(math.ceil(w / size)):
            path = folder / f'{x}_{y}.jpg'
            if path.exists():
                continue
            tile = Image.new('RGB', (min(size, w - x * size), min(size, h - y * size)),
                             (85 + (x * 17) % 110, 70 + (y * 23) % 110, 155))
            draw = ImageDraw.Draw(tile)
            draw.rectangle((0, 0, tile.width - 1, tile.height - 1), outline='white')
            draw.text((10, 12), f'SYNTHETIC / {level}\nTile {x}, {y}', fill='white')
            tile.save(path, quality=75)
print(f'Prepared {len(records)} atlas records and synthetic {width} x {height} DZI')
