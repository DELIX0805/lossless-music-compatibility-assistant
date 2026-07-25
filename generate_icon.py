import argparse
from pathlib import Path
from PIL import Image

parser = argparse.ArgumentParser(description="Create the multi-size Windows application icon.")
parser.add_argument("source", type=Path, help="Screenshot containing the title-bar icon.")
parser.add_argument("--output", type=Path, default=Path(__file__).parent / "Assets" / "app.ico")
args = parser.parse_args()

source = args.source
destination = args.output
destination.parent.mkdir(parents=True, exist_ok=True)

image = Image.open(source).convert("RGBA").crop((25, 13, 68, 57))
corner = image.getpixel((image.width - 1, image.height - 1))[:3]
pixels = image.load()

for y in range(image.height):
    for x in range(image.width):
        red, green, blue, alpha = pixels[x, y]
        distance = max(abs(red - corner[0]), abs(green - corner[1]), abs(blue - corner[2]))
        if distance <= 10:
            pixels[x, y] = (red, green, blue, 0)

bounds = image.getbbox()
if bounds is None:
    raise RuntimeError("The supplied icon image is empty.")

image = image.crop(bounds)
side = max(image.size)
canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
canvas.alpha_composite(image, ((side - image.width) // 2, (side - image.height) // 2))
master = canvas.resize((256, 256), Image.Resampling.LANCZOS)
master.save(destination, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

print(destination)
