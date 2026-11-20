from argparse import ArgumentParser
from pathlib import Path

from PIL import Image


parser = ArgumentParser(description="Rebuild the app icons from the Kindle plugin's launcher-icon.png.")
parser.add_argument("source", type=Path)
source = parser.parse_args().source
root = Path(__file__).resolve().parent
destination = root / "App" / "Assets"
destination.mkdir(exist_ok=True)
with Image.open(source) as artwork:
    mark = artwork.convert("RGB").crop((0, 90, 600, 690)).resize((256, 256), Image.Resampling.LANCZOS)
    mark.save(destination / "Mark.png")
    sizes = (16, 20, 24, 32, 40, 48, 64, 128, 256)
    mark.convert("RGBA").save(destination / "ReadingTimeManager.ico", sizes=[(size, size) for size in sizes], bitmap_format="bmp")
