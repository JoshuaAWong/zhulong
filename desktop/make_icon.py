"""从朱印原稿和共用玉龙遮罩生成 Windows 多尺寸图标。"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ASSETS = Path(__file__).resolve().parent / "Zhulong.Desktop" / "Assets"
SIZES = [(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 256)]


def jade_preview(mask, size):
    # 用于预览玉龙在深浅背景和不同尺寸下的轮廓。
    scale = 4
    frame = Image.new("L", (size * scale, size * scale))
    height = (size - 3 if size <= 24 else round(size * 0.875)) * scale
    width = round(height * mask.width / mask.height)
    frame.paste(mask.resize((width, height), Image.Resampling.LANCZOS),
                ((frame.width - width) // 2, (frame.height - height) // 2))
    stroke = max(2, round(size * 0.035 * scale))
    rim = Image.new("RGBA", frame.size, "#e4f2e8")
    rim.putalpha(frame.filter(ImageFilter.MaxFilter(stroke * 2 + 1)))
    jade = Image.new("RGBA", frame.size, "#246c58")
    jade.putalpha(frame)
    rim.alpha_composite(jade)
    return rim.resize((size, size), Image.Resampling.LANCZOS)


def main():
    with Image.open(ASSETS / "zhulong-next.png") as png:
        seal = png.convert("RGBA")
    seal.save(ASSETS / "zhulong-next.ico", sizes=SIZES)

    with Image.open(ASSETS / "zhulong-jade-mask.png") as png:
        mask = png.getchannel("A")
    jade = [jade_preview(mask, size) for size, _ in SIZES]

    preview = Image.new("RGB", (620, 160), "#f4f2eb")
    ImageDraw.Draw(preview).rectangle((310, 0, 620, 160), fill="#172229")
    for offset in (0, 310):
        for index, position in enumerate((22, 66, 110, 156, 202, 250)):
            sample = jade[index]
            icon = seal.resize(sample.size, Image.Resampling.LANCZOS)
            preview.paste(icon, (offset + position, 15), icon)
            preview.paste(sample, (offset + position, 90), sample)
    preview.save(ASSETS / "zhulong-next-preview.png")


if __name__ == "__main__":
    main()
