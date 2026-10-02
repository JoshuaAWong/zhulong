"""托盘薄封装：隔离 pystray 依赖，将来可整体替换。"""
import webbrowser

import pystray
from PIL import Image, ImageDraw

from zhulong import config

_COLORS = {"green": (76, 175, 80, 255), "yellow": (255, 193, 7, 255),
           "red": (244, 67, 54, 255), "gray": (158, 158, 158, 255)}


def _disc(color):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    ImageDraw.Draw(img).ellipse((8, 8, 56, 56), fill=_COLORS[color])
    return img


class Tray:
    def __init__(self, on_pause, on_exit):
        self._imgs = {k: _disc(k) for k in _COLORS}
        self.icon = pystray.Icon(
            "Zhulong", self._imgs["gray"], "烛龙 · 启动中…",
            menu=pystray.Menu(
                pystray.MenuItem("打开面板", lambda *_: webbrowser.open(config.PANEL_URL), default=True),
                pystray.MenuItem("暂停 5 分钟", lambda *_: on_pause()),
                pystray.MenuItem("退出", lambda *_: on_exit()),
            ))

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
