"""PZA's Meitou UI: pictures of every PZA Meitou window page, from PzaFitCheck's layout dumps (PZA_FIT_DUMP), in each language's real
Kenshi font (Kenshi_StandardFont_Medium of that locale, 14 px at 1080 lines as Meitou draws it). A plain, clean drawing of the controls'
real bounds and captions: not the game's skins. Author: PZA. Licence: GPLv3 or later.

    python -I fit_pictures.py <dump folder> <out folder> [<Kenshi folder>]

Writes one sheet per window: <window>.png, the tabs down, en / ru / zh / ja across.
"""
import json
import os
import re
import sys
from collections import defaultdict

from PIL import Image, ImageDraw, ImageFont

KENSHI = r"C:\Program Files (x86)\Steam\steamapps\common\Kenshi"
FONT_FILES = {
    "en": ("data/gui/fonts", "Exo2-SemiBold.ttf"),
    "ru": ("data/gui/fonts", "Exo2-SemiBold.ttf"),
    "zh": ("locale/zh_CN/gui/fonts", "TTZhongYuanJ.ttf"),
    "ja": ("locale/ja_JP/gui/fonts", "ume-pgs5.ttf"),
}
EM = 14  # px at 1080 lines (Meitou: StandardTextPixels 13.9, whole pixels)
BG, PANEL, FRAME, BTN, BTN_EDGE, TEXT, DIM, TAB_ON, LIST_BG = (
    (22, 20, 17), (38, 34, 29), (110, 96, 74), (58, 51, 42), (120, 104, 80), (233, 225, 210), (150, 142, 128), (92, 78, 56), (28, 25, 21))
COLOUR = re.compile(r"#[0-9A-Fa-f]{6}")
LANGS = ["en", "ru", "zh", "ja"]


def plain(s):
    return COLOUR.sub("", s.replace("##", "\0")).replace("\0", "#").replace("\\n", "\n")


def wrap(draw, text, font, width):
    lines = []
    for para in text.split("\n"):
        line = ""
        tokens = re.findall(r" *[\u2E80-\u9FFF\uAC00-\uD7AF\uF900-\uFAFF\uFF00-\uFFEF][、。，．：；！？）」』】》・]*| *[^ \u2E80-\u9FFF\uAC00-\uD7AF\uF900-\uFAFF\uFF00-\uFFEF]+", para)
        for tok in tokens:
            nxt = tok.lstrip() if not line else line + tok
            if line and draw.textlength(nxt.rstrip(), font=font) > width:
                lines.append(line.rstrip())
                line = tok.lstrip()
            else:
                line = nxt
        lines.append(line.rstrip())
    return lines


def render(doc, font):
    cs = doc["Controls"]
    root = next(c for c in cs if c["Type"] == "Window")
    ox, oy = root["X"] - 8, root["Y"] - 8
    img = Image.new("RGB", (int(root["Wd"]) + 16, int(root["Ht"]) + 16), BG)
    d = ImageDraw.Draw(img)
    line_h = int(EM * 1.25)

    def box(c):
        return (c["X"] - ox, c["Y"] - oy, c["X"] - ox + c["Wd"], c["Y"] - oy + c["Ht"])

    d.rectangle(box(root), fill=PANEL, outline=FRAME, width=2)
    for c in cs:
        if c is root or c["Wd"] <= 1 or c["Ht"] <= 1:
            continue
        x0, y0, x1, y1 = box(c)
        name, cap = c["Name"], plain(c["Caption"] or "")
        is_tab = "@tab" in name and "@row" not in name
        is_row = "@row" in name
        if c["Type"] == "ListBox":
            d.rectangle((x0, y0, x1, y1), fill=LIST_BG, outline=BTN_EDGE)
            continue
        if c["Type"] == "ScrollBar":
            d.rectangle((x0, y0 + (y1 - y0) * 0.35, x1, y1 - (y1 - y0) * 0.35), fill=BTN, outline=BTN_EDGE)
            continue
        if c["Skin"] == "Kenshi_TickBoxSkin":
            d.rectangle((x0, y0, x0 + (y1 - y0), y1), outline=TEXT, width=2)
            if c["Checked"]:
                d.rectangle((x0 + 4, y0 + 4, x0 + (y1 - y0) - 4, y1 - 4), fill=TEXT)
            continue
        if is_tab:
            on = name.endswith("@tab%d" % doc["Tab"])
            d.rectangle((x0, y0, x1, y1), fill=TAB_ON if on else BTN, outline=BTN_EDGE)
        elif c["Type"] == "Button" and not is_row and "Caption" not in name and "Header" not in c["Skin"]:
            if "Close" in c["Skin"] or "Minimize" in c["Skin"]:
                d.rectangle((x0, y0, x1, y1), outline=FRAME)
            else:
                d.rectangle((x0, y0, x1, y1), fill=BTN, outline=BTN_EDGE)
        if not cap.strip():
            continue
        col = TEXT if c["Enabled"] else DIM
        if c["Type"] == "Button" and not is_row:
            d.text(((x0 + x1) / 2, (y0 + y1) / 2), cap, font=font, fill=col, anchor="mm")
        elif is_row:
            d.text((x0 + 3, (y0 + y1) / 2), cap, font=font, fill=col, anchor="lm")
        else:
            lines = wrap(d, cap, font, x1 - x0) if c["Wrap"] else cap.split("\n")
            align = c["Align"] or ("Left Top" if c["Wrap"] else "Left VCenter")
            total = len(lines) * line_h
            y = y0 if "Top" in align else (y0 + y1 - total) / 2
            for ln in lines:
                if align.split()[0] in ("Center", "HCenter"):
                    d.text(((x0 + x1) / 2, y), ln, font=font, fill=col, anchor="ma")
                elif align.startswith("Right"):
                    d.text((x1, y), ln, font=font, fill=col, anchor="ra")
                else:
                    d.text((x0, y), ln, font=font, fill=col)
                y += line_h
    return img


def load_font(path):
    """The font at EM px. A font whose hinting program FreeType rejects (ume-pgs5: invalid opcode; Meitou then draws it auto-hinted) is
    loaded from memory with its fpgm / prep / cvt tables hidden (renamed in the table directory), so it draws unhinted."""
    try:
        f = ImageFont.truetype(path, EM)
        f.getmask("あ漢A")
        return f
    except OSError:
        import io
        data = bytearray(open(path, "rb").read())
        count = int.from_bytes(data[4:6], "big")
        for i in range(count):
            rec = 12 + 16 * i
            if bytes(data[rec:rec + 4]) in (b"fpgm", b"prep", b"cvt "):
                data[rec:rec + 4] = b"zz" + bytes(data[rec + 2:rec + 4])
        return ImageFont.truetype(io.BytesIO(bytes(data)), EM)


def main():
    dump, out = sys.argv[1], sys.argv[2]
    kenshi = sys.argv[3] if len(sys.argv) > 3 else KENSHI
    os.makedirs(out, exist_ok=True)
    fonts = {l: load_font(os.path.join(kenshi, *FONT_FILES[l][0].split("/"), FONT_FILES[l][1])) for l in LANGS}
    pages = defaultdict(dict)
    for f in sorted(os.listdir(dump)):
        if f.endswith(".json"):
            with open(os.path.join(dump, f), encoding="utf-8") as fh:
                doc = json.load(fh)
            pages[doc["Window"]][(doc["Tab"], doc["Lang"])] = doc
    for window, docs in pages.items():
        tabs = sorted({t for t, _ in docs})
        imgs = {k: render(v, fonts[v["Lang"]]) for k, v in docs.items()}
        cw = max(i.width for i in imgs.values()) + 12
        ch = max(i.height for i in imgs.values()) + 12
        sheet = Image.new("RGB", (cw * len(LANGS), ch * len(tabs) + 24), (12, 11, 9))
        d = ImageDraw.Draw(sheet)
        for li, lang in enumerate(LANGS):
            d.text((li * cw + 8, 4), lang.upper(), font=fonts["en"], fill=TEXT)
            for ti, tab in enumerate(tabs):
                if (tab, lang) in imgs:
                    sheet.paste(imgs[(tab, lang)], (li * cw + 6, 24 + ti * ch + 6))
        path = os.path.join(out, window.replace(".", "_") + ".png")
        sheet.save(path)
        print(path, sheet.size)


if __name__ == "__main__":
    main()
