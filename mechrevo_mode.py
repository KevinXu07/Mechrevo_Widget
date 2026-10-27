# -*- coding: utf-8 -*-
"""
Mechrevo ControlCenter 性能档位切换脚本(免重启,免打开控制中心)

用法:
    python mechrevo_mode.py           # 显示当前档位
    python mechrevo_mode.py office    # 办公 (45W)
    python mechrevo_mode.py gaming    # 均衡 (75W)
    python mechrevo_mode.py turbo     # 狂暴 (145W)
    python mechrevo_mode.py custom    # 自定义
    python mechrevo_mode.py turbo-silent    # 狂暴·静音(狂暴子模式)
    python mechrevo_mode.py turbo-extreme   # 狂暴·高能(狂暴子模式)

原理:直接以本地 MQTT 客户端身份连接机械革命后台服务(GCUBridge)
监听在 127.0.0.1:13688 的 MQTT broker,向 Fan/Control 主题发布切档命令,
后台服务(GCUService)收到后会立即把 CPU/GPU 功耗墙和风扇曲线写入硬件,
效果与控制中心里点击完全一致,无需重启服务。

已抓到的真实切档命令(从控制中心 UI 流量观察所得):
    {"Action":"OPERATING_OFFICE_MODE","ProfileIndex":"0"}
    {"Action":"OPERATING_GAMING_MODE","ProfileIndex":"0"}
    {"Action":"OPERATING_TURBO_MODE","ProfileIndex":"0"}
    {"Action":"OPERATING_CUSTOM_MODE","ProfileIndex":"0"}
"""
import json
import sys
import time

# 统一 UTF-8 输出,避免中文在重定向/部分终端下乱码
if sys.platform == "win32":
    for _s in (sys.stdout, sys.stderr):
        try:
            _s.reconfigure(encoding="utf-8")
        except Exception:
            pass

try:
    import paho.mqtt.client as mqtt
except ImportError:
    print("缺少依赖 paho-mqtt,请先安装:  pip install paho-mqtt")
    sys.exit(1)

HOST, PORT = "127.0.0.1", 13688

# GCUBridge 本地 broker 接受的客户端身份(官方托盘程序使用的 MyTray 账户)。
# 用 MyTray 身份连接不会挤掉 GCUService(它的身份是 MyControlCenter)。
# 序号取较大的 19,避免与托盘/其他组件正在用的序号冲突。
CLIENT_ID = "MyTray_19"
USERNAME = "MyTray_User_19"
PASSWORD = "MyTray_Pwd888881772688_19"

MAIN_OPTION = (r"C:\Program Files\OEM\机械革命控制中心\AiStoneService\MyControlCenter"
               r"\UserPofiles\MainOption.json")

MODES = {
    # key: (Action, MainOption 中的 ProfileIndex 字段, 中文名)
    "office": ("OPERATING_OFFICE_MODE", "OfficeProfileIndex", "办公"),
    "gaming": ("OPERATING_GAMING_MODE", "GamingProfileIndex", "均衡"),
    "turbo": ("OPERATING_TURBO_MODE", "TurboProfileIndex", "狂暴"),
    "custom": ("OPERATING_CUSTOM_MODE", "CustomProfileIndex", "自定义"),
}

FAN_STATUS_TOPIC = "Fan/Status"


def profile_index_for(mode_key):
    """读取 MainOption.json,拿到该档位当前激活的子 Profile 序号(与 UI 一致)。

    读取失败时回退到 "0"(对应 *_Profile1)。
    """
    try:
        with open(MAIN_OPTION, "r", encoding="utf-8") as f:
            opts = json.load(f)
        field = MODES[mode_key][1]
        value = opts.get(field, 0)
        return str(int(value))
    except Exception:
        return "0"


def query_status(timeout=6):
    """向 Fan/Control 发 GETSTATUS 触发 GCUService 回发 Fan/Status,返回当前状态。

    这是 UI 的同样做法:Fan/Status 并非保留消息,只有查询或状态变化时才发布。
    """
    result = {}

    def on_message(client, userdata, msg):
        if msg.topic == FAN_STATUS_TOPIC:
            try:
                result["status"] = json.loads(msg.payload.decode("utf-8", "replace"))
            except Exception as e:
                result["error"] = repr(e)

    c = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=CLIENT_ID)
    c.username_pw_set(USERNAME, PASSWORD)
    c.on_message = on_message
    c.connect(HOST, PORT, keepalive=15)
    c.subscribe(FAN_STATUS_TOPIC, qos=0)
    c.loop_start()
    # 订阅就绪后发查询命令,等待 GCUService 回发状态
    time.sleep(0.2)
    c.publish("Fan/Control", json.dumps({"Action": "GETSTATUS"}), qos=1)
    deadline = time.time() + timeout
    while time.time() < deadline and "status" not in result:
        time.sleep(0.1)
    c.loop_stop()
    c.disconnect()
    return result.get("status")


def publish_payload(payload):
    """向 Fan/Control 发布一条命令。"""
    c = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=CLIENT_ID)
    c.username_pw_set(USERNAME, PASSWORD)
    c.connect(HOST, PORT, keepalive=15)
    c.publish("Fan/Control", json.dumps(payload), qos=1)
    c.loop_start()
    time.sleep(0.5)
    c.loop_stop()
    c.disconnect()


def switch_mode(action_name, profile_index="0"):
    """向 Fan/Control 发布切档命令。"""
    publish_payload({"Action": action_name, "ProfileIndex": profile_index})


def describe(status):
    if not status:
        return "未知(未获取到状态)"
    extra = ""
    if status.get("Extreme") == "1" and status.get("Silent") != "1":
        extra = " · 高能"
    elif status.get("Silent") == "1":
        extra = " · 静音"
    return (f"档位OperatingMode={status.get('OperatingMode')} | "
            f"Profile={status.get('ProfileName')} | "
            f"风扇表={status.get('FAN_TableName')} | "
            f"CPU PL1/PL2/PL4={status.get('CPU_PL1')}/{status.get('CPU_PL2')}/{status.get('CPU_PL4')}W | "
            f"GPU TGP={status.get('GPU_ConfigurableTGPTarget')}W{extra}")


def main():
    if len(sys.argv) < 2 or sys.argv[1] in ("status", "query", "get"):
        s = query_status()
        print("当前性能档位:", describe(s))
        return

    arg = sys.argv[1].lower()
    if arg in ("turbo-silent", "turbo-extreme"):
        # 狂暴子模式:先确保在狂暴档,再切子模式
        turbo_action, _, _ = MODES["turbo"]
        switch_mode(turbo_action, profile_index_for("turbo"))
        time.sleep(0.8)
        if arg == "turbo-silent":
            publish_payload({"Action": "SET_CPU_CORE_OFFSET_SILENT", "SILENT": 1})
            label = "狂暴·静音"
        else:
            publish_payload({"Action": "SET_CPU_CORE_OFFSET_EXTREME", "EXTREME": 1})
            label = "狂暴·高能"
        time.sleep(0.8)
        s = query_status()
        print(f"已切换 -> {label}")
        print("生效状态 :", describe(s))
        return

    if arg not in MODES:
        print("用法: python mechrevo_mode.py <office|gaming|turbo|custom|turbo-silent|turbo-extreme>")
        sys.exit(1)

    action, _, label = MODES[arg]
    pi = profile_index_for(arg)
    print(f"切到 {label} ... ProfileIndex={pi}")
    switch_mode(action, pi)
    time.sleep(1.0)  # 等 GCUService 应用并回发状态
    s = query_status()
    print(f"已切换 -> {label} ({action})")
    print("生效状态 :", describe(s))


if __name__ == "__main__":
    main()
