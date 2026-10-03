"""Joins the pictures taken by make-gif.ps1 into docs/demo.gif: each scene on the same dark canvas with a caption."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

work, target = Path(sys.argv[1]), Path(sys.argv[2])
W, H = 760, 470
BACK, FORE, MUTED = (32, 32, 32), (235, 235, 235), (160, 160, 160)


def font(size, bold=False):
    name = "segoeuib.ttf" if bold else "segoeui.ttf"
    try:
        return ImageFont.truetype(name, size)
    except OSError:
        return ImageFont.load_default()


def canvas(caption, sub=None):
    img = Image.new("RGB", (W, H), BACK)
    d = ImageDraw.Draw(img)
    d.text((W // 2, 34), caption, fill=FORE, font=font(24, True), anchor="mm")
    if sub:
        d.text((W // 2, 66), sub, fill=MUTED, font=font(16), anchor="mm")
    return img


def scene(picture, caption, sub=None):
    img = canvas(caption, sub)
    pic = Image.open(work / picture).convert("RGB")
    room_w, room_h = W - 40, H - 110
    scale = min(1.0, room_w / pic.width, room_h / pic.height)
    if scale < 1.0:
        pic = pic.resize((int(pic.width * scale), int(pic.height * scale)), Image.LANCZOS)
    img.paste(pic, ((W - pic.width) // 2, 95 + (room_h - pic.height) // 2))
    return img


def title():
    img = canvas("Claude Code Traffic Light", "A tray traffic light for every Claude Code session on Windows")
    d = ImageDraw.Draw(img)
    rows = [("Working", "working"), ("Compacting", "compacting the conversation"),
            ("Waiting", "waiting for you"), ("Idle", "done, ready for the next task")]
    top = 140
    for i, (level, text) in enumerate(rows):
        lamp = Image.open(work / f"lamp-{level}.png").convert("RGBA")
        y = top + i * 72
        img.paste(lamp, (230, y), lamp)
        d.text((310, y + 32), text, fill=FORE, font=font(22), anchor="lm")
    return img


frames = [
    (title(), 2600),
    (scene("1-working.png", "Every session in the tray menu", "named after the chat, with its state and time"), 2600),
    (scene("2-panel.png", "Claude asks: answer from a panel", "no switching to its window; or from the phone, through ntfy"), 3800),
    (scene("2-menu-waiting.png", "The light turns red while it waits", "the tray menu says which session needs you"), 2400),
    (scene("3-done.png", "Answered: it goes on, then it is done", "a notification only when a session needs you"), 2800),
]
images = [f.quantize(colors=128, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE) for f, _ in frames]
images[0].save(target, save_all=True, append_images=images[1:], duration=[ms for _, ms in frames], loop=0, optimize=True)
print(f"{target}  {target.stat().st_size // 1024} KB, {len(frames)} frames")
