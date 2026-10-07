"""托盘薄封装：隔离 pystray 依赖，将来可整体替换。
图标：烛龙品牌（龙身环琥珀 + 龙首 + 火焰），火焰随状态变色（绿/黄/红/灰）。"""
import webbrowser

import pystray
from PIL import Image, ImageDraw

from zhulong import config

_FLAME = {"green": (76, 175, 80, 255), "yellow": (255, 193, 7, 255),
          "red": (244, 67, 54, 255), "gray": (158, 158, 158, 255)}
_RING = (255, 159, 67, 255)   # 琥珀龙环


def _dragon(flame_rgb):
    """Pillow 手绘烛龙：环 + 双角 + 龙睛 + 水滴火焰。"""
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.arc((8, 8, 56, 56), start=185, end=355, fill=_RING, width=4)          # 龙身环（上开口）
    d.line([(24, 12), (21, 4)], fill=_RING, width=3)                        # 左角
    d.line([(29, 12), (33, 4)], fill=_RING, width=3)                        # 右角
    d.ellipse((25, 8, 28, 11), fill=_RING)                                  # 龙睛
    d.polygon([(32, 21), (38, 32), (32, 41), (26, 32)], fill=flame_rgb)     # 火焰（上尖）
    d.ellipse((27, 30, 37, 40), fill=flame_rgb)                             # 火焰（下圆）
    return img


class Tray:
    def __init__(self, on_pause, on_exit, on_throttle_toggle=None,
                 on_throttle_reset=None, on_open_config=None):
        self._imgs = {k: _dragon(v) for k, v in _FLAME.items()}
        items = [
            pystray.MenuItem("打开面板", lambda *_: webbrowser.open(config.PANEL_URL), default=True),
            pystray.MenuItem("暂停 5 分钟", lambda *_: on_pause()),
        ]
        if on_throttle_toggle:
            items.append(pystray.MenuItem("限流 开/关", lambda *_: on_throttle_toggle()))
        if on_throttle_reset:
            items.append(pystray.MenuItem("限流 恢复默认规则", lambda *_: on_throttle_reset()))
        if on_open_config:
            items.append(pystray.MenuItem("打开配置文件", lambda *_: on_open_config()))
        items.append(pystray.MenuItem("退出", lambda *_: on_exit()))
        self.icon = pystray.Icon("Zhulong", self._imgs["gray"], "烛龙 · 启动中…",
                                 menu=pystray.Menu(*items))

    def update(self, percent=None, used_gb=None, limit_gb=None, healthy=True):
        if not healthy or percent is None:
            state, tip = "gray", "烛龙 · 采集心跳异常"
        else:
            state = "green" if percent < 70 else "yellow" if percent < 85 else "red"
            tip = f"烛龙 · commit {percent:.0f}%"
            if used_gb and limit_gb:
                tip += f" ({used_gb:.0f}/{limit_gb:.0f}GB)"
        self.icon.icon = self._imgs[state]
        self.icon.title = tip[:127]

    def run(self):
        self.icon.run()

    def stop(self):
        self.icon.stop()
