"""托盘菜单构造（tray.py 薄封装）：菜单项存在性与顺序，不启动消息循环。"""
from zhulong.tray import Tray


def main():
    # v1.6：重启菜单存在且在「退出」之前
    t = Tray(on_pause=lambda: None, on_exit=lambda: None, on_restart=lambda: None,
             on_throttle_toggle=lambda: None, on_throttle_reset=lambda: None,
             on_open_config=lambda: None)
    texts = [str(i.text) for i in t.icon.menu.items]
    assert "重启烛龙" in texts and "退出" in texts, texts
    assert texts.index("重启烛龙") < texts.index("退出"), texts
    assert texts[0] == "打开面板" and "暂停 5 分钟" in texts, texts
    # 不传 on_restart：菜单无重启项（可选参数向后兼容）
    t2 = Tray(on_pause=lambda: None, on_exit=lambda: None)
    texts2 = [str(i.text) for i in t2.icon.menu.items]
    assert "重启烛龙" not in texts2 and texts2[-1] == "退出", texts2
    print("PASS")


if __name__ == "__main__":
    main()
