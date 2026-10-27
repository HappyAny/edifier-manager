from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
assets = root / "src/EdifierManager/Assets"
assets.mkdir(parents=True, exist_ok=True)
# Original vector geometry: a white headset with capsule-shaped earpieces.
size, scale = 1024, 4
image = Image.new("RGBA", (size, size))
mask = Image.new("L", (size, size))
ImageDraw.Draw(mask).rounded_rectangle((16*scale,16*scale,240*scale,240*scale), radius=52*scale, fill=255)
gradient = Image.new("RGBA", (size, size))
draw = ImageDraw.Draw(gradient)
for y in range(size):
    t = y / (size - 1)
    color = tuple(round(a*(1-t)+b*t) for a,b in zip((31,169,157),(17,97,107)))+(255,)
    draw.line((0,y,size,y), fill=color)
image.paste(gradient,(0,0),mask)
draw = ImageDraw.Draw(image)
white=(255,255,255,255)
draw.arc((61*scale,53*scale,195*scale,187*scale),180,360,fill=white,width=18*scale)
for x in (70,186):
    draw.line((x*scale,120*scale,x*scale,143*scale),fill=white,width=18*scale)
for left in (53,165):
    draw.rounded_rectangle((left*scale,116*scale,(left+38)*scale,190*scale),radius=19*scale,fill=white)
icon=image.resize((256,256),Image.Resampling.LANCZOS)
icon.save(assets/"app.png")
icon.save(assets/"app.ico",format="ICO",sizes=[(s,s) for s in (16,20,24,32,40,48,64,128,256)])
(assets/"app.svg").write_text('''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><defs><linearGradient id="bg" x2="0" y2="1"><stop stop-color="#1fa99d"/><stop offset="1" stop-color="#11616b"/></linearGradient></defs><rect x="16" y="16" width="224" height="224" rx="52" fill="url(#bg)"/><path d="M70 143v-23a58 58 0 0 1 116 0v23" fill="none" stroke="white" stroke-width="18"/><rect x="53" y="116" width="38" height="74" rx="19" fill="white"/><rect x="165" y="116" width="38" height="74" rx="19" fill="white"/></svg>''',encoding="utf-8")
print("Created app.svg, app.png and nine-size app.ico")
