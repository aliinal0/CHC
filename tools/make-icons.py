"""Renders the CHC logo from its SVG source into the files the app and the installer use.

  src/CHC.App/Assets/chc.svg        the logo, the same drawing at every size
  ->  src/CHC.App/Assets/chc.png    256 px, used in the app and the README
  ->  src/CHC.App/Assets/chc.ico    16 to 256 px, used by the exe, the window and the installer

Needs Python with Pillow and Microsoft Edge (it renders the SVGs headless, with a transparent background).
usage: python tools/make-icons.py"""
import os
import subprocess
import tempfile

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src", "CHC.App", "Assets")
EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
RENDER = 1024  # rendered large, then scaled down so every size is cleanly anti-aliased
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def render(svg, work):
    page = os.path.join(work, os.path.basename(svg) + ".html")
    with open(page, "w", encoding="utf-8") as f:
        f.write(f'<html><body style="margin:0;background:transparent">'
                f'<img src="file:///{svg.replace(os.sep, "/")}" style="width:{RENDER}px;height:{RENDER}px;display:block"></body></html>')
    png = os.path.join(work, os.path.basename(svg) + ".png")
    subprocess.run([EDGE, "--headless", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                    f"--user-data-dir={os.path.join(work, 'edge')}", "--default-background-color=00000000",
                    "--allow-file-access-from-files", f"--window-size={RENDER},{RENDER}",
                    f"--screenshot={png}", f"file:///{page.replace(os.sep, '/')}"],
                   check=True, capture_output=True)
    return Image.open(png).convert("RGBA")


def main():
    with tempfile.TemporaryDirectory() as work:
        logo = render(os.path.join(ASSETS, "chc.svg"), work)
    images = [logo.resize((s, s), Image.LANCZOS) for s in ICO_SIZES]
    images[-1].save(os.path.join(ASSETS, "chc.png"), optimize=True)
    # Pillow writes each size from the image closest to it, so hand it every hand-picked size.
    images[-1].save(os.path.join(ASSETS, "chc.ico"), sizes=[(s, s) for s in ICO_SIZES], append_images=images[:-1])
    print("chc.png and chc.ico written:", ", ".join(map(str, ICO_SIZES)))


if __name__ == "__main__":
    main()
