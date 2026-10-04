"""The picture GitHub shows when a link to the repository is shared (Settings > Social preview): 1280x640, the name,
what it does, three lamps and the request panel. Uses the pictures of a make-gif.ps1 run (work/gif)."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

work, target = Path(sys.argv[1]), Path(sys.argv[2])
W, H = 1280, 640
BACK, FORE, MUTED = (28, 28, 30), (240, 240, 240), (165, 165, 170)


def font(size, bold=False):
    try:
        return ImageFont.truetype("segoeuib.ttf" if bold else "segoeui.ttf", size)
    except OSError:
        return ImageFont.load_default()


img = Image.new("RGB", (W, H), BACK)
d = ImageDraw.Draw(img)
d.text((70, 92), "Claude Code", fill=FORE, font=font(64, True))
d.text((70, 166), "Traffic Light", fill=FORE, font=font(64, True))
d.text((72, 262), "Every Claude Code session in the Windows tray:", fill=MUTED, font=font(26))
d.text((72, 298), "see which one works, waits or is done,", fill=MUTED, font=font(26))
d.text((72, 334), "and answer its prompts without switching windows.", fill=MUTED, font=font(26))

rows = [("Working", "working"), ("Waiting", "waits for you"), ("Idle", "done")]
x = 72
for level, text in rows:
    lamp = Image.open(work / f"lamp-{level}.png").convert("RGBA").resize((52, 52), Image.LANCZOS)
    img.paste(lamp, (x, 420), lamp)
    d.text((x + 62, 446), text, fill=FORE, font=font(24), anchor="lm")
    x += 62 + int(d.textlength(text, font=font(24))) + 34

d.text((72, 540), "Portable .exe  ·  GPL-3.0  ·  phone pushes via ntfy", fill=MUTED, font=font(22))

panel = Image.open(work / "2-panel.png").convert("RGB")
scale = min(500 / panel.width, 520 / panel.height)
panel = panel.resize((int(panel.width * scale), int(panel.height * scale)), Image.LANCZOS)
img.paste(panel, (W - panel.width - 60, (H - panel.height) // 2))

img.save(target, optimize=True)
print(f"{target}  {target.stat().st_size // 1024} KB")
