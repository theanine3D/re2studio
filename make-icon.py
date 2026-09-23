"""Rebuilds Re2.Studio/app.ico from icon.png.

Run this after changing icon.png, then rebuild:

    python make-icon.py
    dotnet publish Re2.Studio -c Release -o dist

The build reads Re2.Studio/app.ico (the <ApplicationIcon> in Re2.Studio.csproj); icon.png is only the
source art and is not read at build time, so changing it alone has no effect.

Windows picks whichever entry is closest to the size it wants, so every size the shell asks for is
written rather than leaving it to rescale one large image badly: 16 and 32 for lists and title bars,
48 for the desktop, 256 for the large-icon views. 256 is the largest an .ico can hold.
"""

import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, 'icon.png')
TARGET = os.path.join(HERE, 'Re2.Studio', 'app.ico')

SIZES = [16, 24, 32, 48, 64, 128, 256]


def main() -> None:
    image = Image.open(SOURCE).convert('RGBA')
    print(f'source: {image.width}x{image.height}')

    # Square it off first if it is not already, so nothing is stretched on the way down.
    if image.width != image.height:
        side = max(image.size)
        square = Image.new('RGBA', (side, side), (0, 0, 0, 0))
        square.paste(image, ((side - image.width) // 2, (side - image.height) // 2))
        image = square
        print(f'padded to square: {side}x{side}')

    # Each size is resampled from the full-resolution original rather than from the previous step,
    # which keeps the small ones as sharp as they can be.
    frames = [image.resize((s, s), Image.LANCZOS) for s in SIZES]

    frames[-1].save(TARGET, format='ICO', sizes=[(s, s) for s in SIZES],
                    append_images=frames[:-1])

    print(f'wrote {TARGET} ({os.path.getsize(TARGET):,} bytes)')
    print('entries:', sorted(Image.open(TARGET).ico.sizes()))


if __name__ == '__main__':
    main()
