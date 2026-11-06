"""Convert the supplied transparent artwork to centered PNG and multi-size Windows ICO.
Developer dependency: Pillow. The original image is never modified.
"""
from pathlib import Path
import argparse
import hashlib
import shutil
from PIL import Image

parser = argparse.ArgumentParser()
parser.add_argument('source', type=Path)
parser.add_argument('--assets', type=Path, default=Path(__file__).resolve().parents[1] / 'Assets')
args = parser.parse_args()
original = args.source.read_bytes()
args.assets.mkdir(parents=True, exist_ok=True)
art = Image.open(args.source).convert('RGBA')
alpha = art.getchannel('A')
bounds = alpha.point(lambda value: 255 if value > 8 else 0).getbbox()
if bounds is None:
    raise ValueError('The supplied icon is entirely transparent.')
art = art.crop(bounds)
art.thumbnail((472, 472), Image.Resampling.LANCZOS)
canvas = Image.new('RGBA', (512, 512), (0, 0, 0, 0))
canvas.alpha_composite(art, ((512 - art.width) // 2, (512 - art.height) // 2))
shutil.copyfile(args.source, args.assets / 'AppIcon-source.png')
canvas.save(args.assets / 'AppIcon.png')
canvas.save(args.assets / 'AppIcon.ico', format='ICO', sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
assert args.source.read_bytes() == original
assert (args.assets / 'AppIcon-source.png').read_bytes() == original
for name in ['AppIcon-source.png', 'AppIcon.png', 'AppIcon.ico']:
    path = args.assets / name
    print(f'{path.resolve()} SHA256={hashlib.sha256(path.read_bytes()).hexdigest()} bytes={path.stat().st_size}')
print('Original preserved; transparent PNG and native multi-size ICO created.')
