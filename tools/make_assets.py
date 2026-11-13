"""Builds ShinyForge's assets: the app icon (drawn here, original design) and the Pokemon artwork
used on the game tiles (downloaded once from the PokeAPI sprites repository)."""
import io, math, os, urllib.request
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.join(os.path.dirname(__file__), "..", "ShinyForge", "Assets")
os.makedirs(os.path.join(ROOT, "Art"), exist_ok=True)


def star(draw, cx, cy, r_out, r_in, points, fill):
    pts = []
    for i in range(points * 2):
        ang = math.pi / points * i - math.pi / 2
        r = r_out if i % 2 == 0 else r_in
        pts.append((cx + r * math.cos(ang), cy + r * math.sin(ang)))
    draw.polygon(pts, fill=fill)


def make_icon(size=512):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    # Rounded-square badge with a violet -> teal gradient.
    grad = Image.new("RGBA", (size, size))
    gd = ImageDraw.Draw(grad)
    for y in range(size):
        t = y / size
        c = (int(124 + (24 - 124) * t), int(58 + (190 - 58) * t), int(237 + (200 - 237) * t), 255)
        gd.line([(0, y), (size, y)], fill=c)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([8, 8, size - 8, size - 8], radius=size // 5, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    # Anvil-ish "forge" base.
    d.rounded_rectangle([size * .22, size * .70, size * .78, size * .80], radius=size // 40, fill=(20, 18, 40, 230))
    d.polygon([(size * .30, size * .70), (size * .70, size * .70), (size * .62, size * .62), (size * .38, size * .62)],
              fill=(20, 18, 40, 230))
    # Glow + big shiny sparkle.
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    star(ImageDraw.Draw(glow), size * .5, size * .38, size * .34, size * .09, 4, (255, 240, 150, 200))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(size / 40)))
    star(d, size * .5, size * .38, size * .30, size * .075, 4, (255, 249, 214, 255))
    star(d, size * .76, size * .20, size * .09, size * .025, 4, (255, 255, 255, 255))
    star(d, size * .25, size * .55, size * .06, size * .018, 4, (255, 255, 255, 230))
    return img


icon = make_icon()
icon.save(os.path.join(ROOT, "ShinyForge.png"))
icon.save(os.path.join(ROOT, "ShinyForge.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("icon done")

# National Dex numbers of each game's cover Pokemon (used on the game tiles).
MASCOTS = sorted({3, 6, 9, 25, 133, 245, 249, 250, 382, 383, 384, 483, 484, 487, 493, 643, 644, 646,
                  716, 717, 718, 791, 792, 800, 888, 889, 1007, 1008, 251, 151})
for n in MASCOTS:
    out = os.path.join(ROOT, "Art", f"{n}.png")
    if os.path.exists(out):
        continue
    url = f"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/other/home/{n}.png"
    try:
        data = urllib.request.urlopen(url, timeout=30).read()
        im = Image.open(io.BytesIO(data)).convert("RGBA")
        im.thumbnail((256, 256))
        im.save(out)
        print("art", n)
    except Exception as e:
        print("art FAILED", n, e)
