# -*- coding: utf-8 -*-
"""从 assets/logo.svg 生成 assets/lecoofan.ico —— 窗口标题栏 / 任务栏 / exe / 安装器共用的应用图标。

    python assets/make_icon.py              # 生成 assets/lecoofan.ico
    python assets/make_icon.py --preview    # 顺便留一份 assets/lecoofan-preview.png（256，看效果用）

为什么用无头浏览器渲染而不是用 GDI+ 重画一遍：
    logo 是 SVG（线性/径向渐变 + 三次贝塞尔扇叶 + 圆弧表盘 + 投影滤镜），在 C# 里照抄一遍
    必然走样，而且以后 logo 一改就得同步两处。系统自带 Edge/Chrome，无头截一张就是"同一个 logo"。

小尺寸的光学简化（SMALL 表）：
    16~24 px 下细刻度线宽不足 0.5 px，只会糊成灰雾；投影滤镜同理。所以小尺寸按图标设计惯例
    裁掉刻度、去掉投影/光晕，并把 viewBox 收紧（= 放大主体），保证在任务栏上依然认得出。
    ≥64 px 用完整 logo（含刻度与投影）。

ICO 里全部写成传统 DIB（BMP）条目，不用 PNG 压缩条目：
    .NET 的 System.Drawing.Icon 不认 PNG 条目（老版 csc 的 /win32icon 同理），混着写会
    在取小图标时抛"参数无效"。
"""
import os
import re
import struct
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SVG = os.path.join(ROOT, "assets", "logo.svg")
ICO = os.path.join(ROOT, "assets", "lecoofan.ico")
PREVIEW = os.path.join(ROOT, "assets", "lecoofan-preview.png")

# 方形徽标取景：logo 画布 512x200，风扇表盘圆心 (110,100)、刻度最外沿 r≈85、光晕 r=96。
# 取 viewBox 半宽 half 即以 (110,100) 为中心的正方形。
PLAN = [
    # size, half(取景半宽), arc 线宽, 隐藏/调整项
    #   ticks    = 两圈刻度全不要      dimticks = 刻度压到 45%（浅灰刻度在深色任务栏上会亮成"太阳光"）
    #   glow     = 去掉环境光晕        hl       = 去掉扇叶高光        shadow = 去掉投影滤镜
    (16,   80, 14.0, ("ticks", "glow", "hl", "shadow")),
    (20,   80, 14.0, ("ticks", "glow", "hl", "shadow")),
    (24,   80, 14.0, ("ticks", "glow", "hl", "shadow")),
    (32,   82, 13.0, ("ticks", "glow", "hl", "shadow")),
    (40,   82, 13.0, ("ticks", "glow", "hl", "shadow")),
    (48,   82, 13.0, ("ticks", "glow", "hl", "shadow")),
    (64,  100, 11.0, ("hl", "dimticks")),
    (128, 100, 11.0, ("dimticks",)),
    (256, 100, 11.0, ("dimticks",)),
]

BROWSERS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
]


def find_browser():
    for b in BROWSERS:
        if os.path.exists(b):
            return b
    raise SystemExit("找不到 Edge/Chrome，无法渲染 SVG。装一个或者手工导出 PNG 再改本脚本。")


def svg_inner():
    with open(SVG, encoding="utf-8") as f:
        src = f.read()
    inner = src[src.index(">") + 1: src.rindex("</svg>")]

    # 给需要按尺寸隐藏/调参的元素打标记（找不到就报错：logo 改了这里必须跟着改）
    def sub(pat, rep, what):
        nonlocal inner
        new, n = re.subn(pat, rep, inner, count=1)
        if n != 1:
            raise SystemExit("logo.svg 里找不到" + what + "（logo 结构变了，请同步 assets/make_icon.py）")
        inner = new

    sub(r'<g stroke="#C7D1DE" stroke-width="3">',
        '<g class="lf-tick-major" stroke="#C7D1DE" stroke-width="3">', "主刻度组")
    sub(r'<g stroke="#DAE1EA" stroke-width="1.8">',
        '<g class="lf-tick-minor" stroke="#DAE1EA" stroke-width="1.8">', "细刻度组")
    sub(r'<circle cx="110" cy="100" r="96" fill="url\(#lf-glow\)"/>',
        '<circle class="lf-glow" cx="110" cy="100" r="96" fill="url(#lf-glow)"/>', "环境光晕")
    sub(r'<g fill="none" stroke="#FFFFFF" stroke-opacity="0.4"',
        '<g class="lf-hl" fill="none" stroke="#FFFFFF" stroke-opacity="0.4"', "扇叶高光")
    return inner


def html_for(inner, size, half, arc_w, hide):
    css = ["html,body{margin:0;padding:0;background:transparent;overflow:hidden}", "svg{display:block}"]
    for h in hide:
        if h == "ticks":
            css.append(".lf-tick-major,.lf-tick-minor{display:none}")
        elif h == "dimticks":
            css.append(".lf-tick-major,.lf-tick-minor{opacity:.45}")
        elif h == "glow":
            css.append(".lf-glow{display:none}")
        elif h == "hl":
            css.append(".lf-hl{display:none}")
        elif h == "shadow":
            css.append("[filter]{filter:none}")          # 小尺寸投影只会糊边
    body = inner.replace('stroke-width="11"', 'stroke-width="%g"' % arc_w, 1)   # 表盘弧线宽
    vb = "%d %d %d %d" % (110 - half, 100 - half, half * 2, half * 2)
    return ('<!doctype html><html><head><meta charset="utf-8"><style>%s</style></head><body>'
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="%s" width="%d" height="%d">%s</svg>'
            '</body></html>') % ("\n".join(css), vb, size, size, body)


def render(browser, tmp, inner, size, half, arc_w, hide):
    html = os.path.join(tmp, "s%d.html" % size)
    png = os.path.join(tmp, "s%d.png" % size)
    with open(html, "w", encoding="utf-8") as f:
        f.write(html_for(inner, size, half, arc_w, hide))
    cmd = [browser, "--headless=new", "--disable-gpu", "--hide-scrollbars",
           "--force-device-scale-factor=1", "--default-background-color=00000000",
           "--user-data-dir=" + os.path.join(tmp, "prof"),
           "--window-size=%d,%d" % (size, size),
           "--screenshot=" + png, "file:///" + html.replace("\\", "/")]
    subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
    if not os.path.exists(png):
        raise SystemExit("渲染 %dpx 失败（无头浏览器没写出截图）" % size)
    return png


def dib(img):
    """PNG → ICO 里的 DIB 数据块：BITMAPINFOHEADER + 自下而上的 BGRA + AND 掩码。"""
    from PIL import Image
    img = img.convert("RGBA")
    w, h = img.size
    px = img.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))
    stride = ((w + 31) // 32) * 4                      # AND 掩码每行按 4 字节对齐
    andm = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(stride)
        for x in range(w):
            if px[x, y][3] < 128:                      # 1 = 透明（32bpp 下仅供老渲染器参考）
                row[x >> 3] |= 0x80 >> (x & 7)
        andm += row
    hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(andm), 0, 0, 0, 0)
    return hdr + bytes(xor) + bytes(andm)


def main():
    from PIL import Image
    browser = find_browser()
    inner = svg_inner()
    tmp = tempfile.mkdtemp(prefix="lecoofan_ico_")
    entries = []
    for size, half, arc_w, hide in PLAN:
        png = render(browser, tmp, inner, size, half, arc_w, hide)
        with Image.open(png) as im:
            entries.append((size, dib(im)))
            if size == 256 and "--preview" in sys.argv:
                im.convert("RGBA").save(PREVIEW)

    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        b = 0 if size >= 256 else size                 # 256 在目录里记 0
        out += struct.pack("<BBBBHHII", b, b, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    with open(ICO, "wb") as f:
        f.write(out)

    print("wrote %s  (%d 尺寸: %s, %d KB)" % (
        ICO, len(entries), " ".join(str(s) for s, _ in entries), len(out) // 1024))


if __name__ == "__main__":
    main()
