"""Side-by-side images (website left, Unity right) and the mean difference of each pair, for README/report use."""
import json, sys, os
from PIL import Image, ImageChops, ImageStat, ImageDraw
here = os.path.dirname(os.path.abspath(__file__))
cases = json.load(open(os.path.join(here, 'cases.json'), encoding='utf-8'))['cases']
os.makedirs(os.path.join(here, 'side'), exist_ok=True)
rows = []
for c in cases:
    a = Image.open(os.path.join(here, f"web_{c['name']}.png")).convert('RGB')
    b = Image.open(os.path.join(here, f"storm_cmp_{c['name']}.png")).convert('RGB').resize(a.size)
    d = ImageStat.Stat(ImageChops.difference(a, b)).mean
    mad = sum(d) / 3 / 255 * 100
    w, h = a.size
    s = Image.new('RGB', (w, h // 2))
    s.paste(a.resize((w // 2, h // 2)), (0, 0)); s.paste(b.resize((w // 2, h // 2)), (w // 2, 0))
    dr = ImageDraw.Draw(s); dr.text((10, 8), f"website  {c['name']}  {c['t']:.1f} min  {c['stage']}", fill=(255, 255, 255)); dr.text((w // 2 + 10, 8), "Unity (website lighting)", fill=(255, 255, 255))
    s.save(os.path.join(here, 'side', f"{c['name']}.jpg"), quality=88)
    rows.append((c['name'], c['stage'], mad))
for r in rows: print(f"{r[0]:18s} {r[1]:40s} mean abs diff {r[2]:.1f}%")
