"""Windows toast 通知，三级降级：winotify → PowerShell → MessageBoxW。
任何失败都不抛异常，返回结果 dict。"""
import subprocess
import threading

from zhulong import config

CREATE_NO_WINDOW = 0x08000000


def _via_winotify(title, body):
    from winotify import Notification, audio
    n = Notification(app_id=config.TOAST_APP_ID, title=title, msg=body,
                     launch=config.PANEL_URL)
    n.set_audio(audio.Silent, loop=False)
    n.show()


_PSH = ("[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, "
        "Content = {Microsoft.Windows.Shell.RunFullTrust}, ContentType = WindowsRuntime] | Out-Null; "
        "$t=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); "
        "$x=$t.GetXml(); $t.GetElementsByTagName('text').Item(0).AppendChild($t.CreateTextNode('{TITLE}'))|Out-Null; "
        "$t.GetElementsByTagName('text').Item(1).AppendChild($t.CreateTextNode('{BODY}'))|Out-Null; "
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('烛龙').Show([Windows.UI.Notifications.ToastNotification]::new($t))")


def _ps_script(title, body):
    """拼 PowerShell 脚本。注入面：进程名/cmdline 可含单引号——
    PS 单引号字符串内 ' 必须双写（''），否则脚本被截断甚至注入。"""
    esc = lambda s: s.replace("'", "''")
    return _PSH.replace("{TITLE}", esc(title)).replace("{BODY}", esc(body))


def _via_powershell(title, body):
    subprocess.run(["powershell", "-NoProfile", "-Command", _ps_script(title, body)],
                   creationflags=CREATE_NO_WINDOW, timeout=15, check=True)


def _via_msgbox(title, body):
    import ctypes
    def show():
        ctypes.windll.user32.MessageBoxW(0, body, title, 0x40)
    t = threading.Thread(target=show, daemon=True)
    t.start()
    t.join(timeout=1)   # 不等用户点击，弹出来即算成功


DEFAULT_CHAIN = [_via_winotify, _via_powershell, _via_msgbox]


def run(title, body, chain=None):
    for i, sender in enumerate(chain if chain is not None else DEFAULT_CHAIN):
        try:
            sender(title, body)
            return {"status": "notified", "via": i}
        except Exception:
            continue
    return {"status": "failed"}


ACTION = {"name": "toast",
          "run": lambda params, cfg, state: run(params.get("toast_title", "烛龙"),
                                                params.get("toast_body", ""))}
