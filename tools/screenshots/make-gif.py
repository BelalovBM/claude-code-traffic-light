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
    # A picture with a transparent background (the round watch faces) lies on the canvas, not on black.
    pic = Image.open(work / picture).convert("RGBA")
    room_w, room_h = W - 40, H - 110
    scale = min(1.0, room_w / pic.width, room_h / pic.height)
    if scale < 1.0:
        pic = pic.resize((int(pic.width * scale), int(pic.height * scale)), Image.LANCZOS)
    img.paste(pic, ((W - pic.width) // 2, 95 + (room_h - pic.height) // 2), pic)
    return img


def title():
    img = canvas("Claude Code Traffic Light", "A tray traffic light for every Claude Code session on Windows")
    d = ImageDraw.Draw(img)
    rows = [("Working", "working"), ("Background", "waiting for its own build or tests"),
            ("Compacting", "compacting the conversation"), ("Waiting", "waiting for you, or stopped by an error"),
            ("Idle", "done, ready for the next task")]
    top, step, size = 112, 68, 52
    for i, (level, text) in enumerate(rows):
        lamp = Image.open(work / f"lamp-{level}.png").convert("RGBA").resize((size, size), Image.LANCZOS)
        y = top + i * step
        img.paste(lamp, (180, y), lamp)
        d.text((250, y + size // 2), text, fill=FORE, font=font(22), anchor="lm")
    return img


def languages():
    """The closing frame: the seven languages of the program, each written in itself, and how it fits Windows."""
    img = canvas("7 languages", "light, dark or system theme; follows Windows high contrast; display scaling, several monitors")
    d = ImageDraw.Draw(img)
    names = ["English", "Русский", "Español", "Deutsch", "Français", "Português", "中文"]
    try:
        cjk = ImageFont.truetype("msyh.ttc", 30)
    except OSError:
        cjk = font(30)
    col_x, top, step = (230, 450), 175, 64
    for i, name in enumerate(names):
        x = col_x[0] if i < 4 else col_x[1]
        y = top + (i if i < 4 else i - 4) * step
        f = cjk if name == "中文" else font(30)
        d.text((x, y), name, fill=FORE, font=f, anchor="lm")
    return img


def stack(names, gap=14):
    """Several pictures of the project page (docs) one above the other, as one picture."""
    docs = target.parent
    pics = [Image.open(docs / n).convert("RGBA") for n in names]
    out = Image.new("RGBA", (max(p.width for p in pics), sum(p.height for p in pics) + gap * (len(pics) - 1)), BACK + (255,))
    y = 0
    for p in pics:
        out.paste(p, (0, y), p)
        y += p.height + gap
    path = work / ("stack-" + "-".join(n.split(".")[0] for n in names) + ".png")
    out.convert("RGB").save(path)
    return path.name


panel = Image.open(target.parent / "panel-permission.png")
panel.save(work / "panel-permission.png")
toasts = stack(["toast-done.png", "toast-error.png"])
# The phone: the top of the screenshot, a permission prompt with its buttons and the start of a question.
phone = Image.open(target.parent / "phone-ntfy.jpg").convert("RGB")
phone.crop((0, 0, phone.width, 1060)).save(work / "phone.png")
# The watch: the same question in three steps, ending with the answer buttons on the wrist.
Image.open(target.parent / "watch-question.png").save(work / "watch-question.png")

watch = scene("watch-question.png", "On your wrist", "a smartwatch shows the same push; where it shows the buttons, answer from there")
frames = [
    (title(), 2600),
    (scene("1-working.png", "Every session in the tray menu", "with its state and time, and Claude's usage limits on top"), 3000),
    (scene("2-panel.png", "Claude asks: answer from a panel", "no switching to its window; or from the phone, through ntfy"), 3800),
    (scene("panel-permission.png", "A permission prompt, in the same panel", "allow once, allow and don't ask again, or open the session window first"), 3800),
    (scene("2-menu-waiting.png", "The light turns red while it waits", "the tray menu says which session needs you"), 2400),
    (scene("3-done.png", "Answered: it goes on, then it is done", "a notification only when a session needs you"), 2800),
    (scene(toasts, "Windows notifications", "what was done when it finishes; what happened when an error stops it"), 3600),
    (scene("phone.png", "On the phone, when you are away", "pushes through ntfy; answer a prompt or a question with a tap"), 3400),
    (watch, 3400),
    (scene("4-background.png", "Waiting for its own build: clock hands", "not \"done\" while the build runs; a notice if it takes half an hour"), 2800),
    (languages(), 2600),
]
# Screenshots need few colours; the photo of the watch gets the most a GIF frame can hold, so its red dot stays red.
images = [f.quantize(colors=256 if f is watch else 128, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
          for f, _ in frames]
images[0].save(target, save_all=True, append_images=images[1:], duration=[ms for _, ms in frames], loop=0, optimize=True)
print(f"{target}  {target.stat().st_size // 1024} KB, {len(frames)} frames")
