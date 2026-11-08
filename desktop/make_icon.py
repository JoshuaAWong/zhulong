"""从同一组矢量控制点生成多尺寸应用图标；只在构建/设计时运行。"""
from pathlib import Path
from PIL import Image, ImageDraw

ASSETS = Path(__file__).resolve().parent / "Zhulong.Desktop" / "Assets"
SCALE = 10


def cubic(start, a, b, end):
    points = []
    for index in range(1, 41):
        t = index / 40
        u = 1 - t
        points.append((u ** 3 * start[0] + 3 * u * u * t * a[0] + 3 * u * t * t * b[0] + t ** 3 * end[0],
                       u ** 3 * start[1] + 3 * u * u * t * a[1] + 3 * u * t * t * b[1] + t ** 3 * end[1]))
    return points


def main():
    image = Image.new("RGBA", (100 * SCALE, 100 * SCALE))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((3 * SCALE, 3 * SCALE, 97 * SCALE, 97 * SCALE), radius=21 * SCALE, fill="#9e302b")
    points = [(73, 30)]
    for a, b, end in [((57,15),(29,23),(23,44)),((16,68),(36,84),(56,79)),((77,74),(82,52),(67,45)),
                      ((55,40),(44,48),(43,59)),((42,69),(54,74),(61,67)),((51,69),(49,62),(53,56)),
                      ((58,49),(69,55),(66,64)),((63,75),(48,76),(38,68)),((25,58),(30,39),(43,33)),
                      ((53,28),(63,30),(68,36))]:
        points.extend(cubic(points[-1], a, b, end))
    points.extend([(67,40),(84,34),(77,29),(79,23)])
    draw.polygon([(round(x*SCALE),round(y*SCALE)) for x,y in points], fill="#fff5db")
    for a,b in [((65,29),(61,19)),((72,28),(73,17))]:
        draw.line([(int(a[0]*SCALE),int(a[1]*SCALE)),(int(b[0]*SCALE),int(b[1]*SCALE))], fill="#fff5db", width=5*SCALE)
        for x,y in [a,b]: draw.ellipse(((x-2.5)*SCALE,(y-2.5)*SCALE,(x+2.5)*SCALE,(y+2.5)*SCALE),fill="#fff5db")
    draw.ellipse((73*SCALE,73*SCALE,87*SCALE,87*SCALE),fill="#edc778")
    image = image.resize((256,256), Image.Resampling.LANCZOS)
    image.save(ASSETS / "zhulong-next.png")
    image.save(ASSETS / "zhulong-next.ico", sizes=[(s,s) for s in (16,20,24,32,40,48,64,256)])
    preview = Image.new("RGB",(620,160),"#f4f2eb")
    panel = ImageDraw.Draw(preview);panel.rectangle((310,0,620,160),fill="#172229")
    for start in (0,310):
        for size,x in [(16,22),(24,68),(32,122),(64,188)]:
            stamp=image.resize((size,size),Image.Resampling.LANCZOS);preview.paste(stamp,(start+x,(160-size)//2),stamp)
    preview.save(ASSETS / "zhulong-next-preview.png")


if __name__ == "__main__":
    main()
