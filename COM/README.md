# COM Test Tool — 跨平台串口(COM)通信测试工具

基于 **Python + tkinter + pyserial** 的串口调试助手，Windows / Linux / macOS 通用，
零 GUI 依赖（tkinter 为 Python 内置），只需一个 `pyserial`。

## 功能

| 分类 | 能力 |
| --- | --- |
| 端口 | 自动枚举（COMx / ttyUSB* / cu.*）、手动输入任意端口、支持 `loop://`、`socket://host:port`、`rfc2217://` 等 URL 端口 |
| 参数 | 波特率（可自定义）、数据位 5-8、校验 N/E/O/M/S、停止位 1/1.5/2、流控（无 / RTS-CTS / XON-XOFF / DTR-DSR） |
| 接收 | 文本 / HEX / HEX Dump 三种视图、时间戳、自动滚动、暂停、自动合并串口分片、接收日志导出 |
| 发送 | 文本或 HEX 模式、行尾（无 / \n / \r / \r\n）、追加校验（SUM8 / XOR / CRC16-Modbus）、定时循环发送（可限定次数） |
| 快捷指令 | 表格化管理，双击即发，支持增删改与排序，随配置持久化 |
| 波形 | 自动解析接收数据中的数字并实时绘制多通道曲线（最多 8 通道） |
| 控制线 | DTR / RTS 输出控制，CTS / DSR / RI / CD 状态实时显示 |
| 主题 | 暗色 / 浅色 / 绚丽（霓虹）三套配色，菜单即时切换并记住选择 |
| 其它 | 收发字节统计、配置自动保存、Windows 高 DPI 适配 |

## 运行

```bash
# 方式一：一键脚本
run.bat        # Windows
./run.sh       # Linux / macOS（首次需 chmod +x run.sh）

# 方式二：手动
pip install -r requirements.txt
python main.py
```

无需硬件即可验证：`python main.py --selftest` 会跑一遍数据工具与 `loop://` 回环收发自检。

## 界面速览

```
┌──────────────────────────────────────────────────────────────────────┐
│ 串口 [COM3 — USB-SERIAL CH340  ▼] [刷新] [打开串口]            ● 已连接 │
│ 波特率 [115200] 数据位 [8] 校验位 [无 (None)] 停止位 [1] 流控 [无]        │
├──────────────────────────────────┬───────────────────────────────────┤
│ 显示 [文本▼] [utf-8▼]             │ 模式 [HEX▼] 行尾 [无▼] 校验 [CRC16▼]│
│ ☑时间戳 ☑自动滚动 ☑显示发送       │ ┌───────────────────────────────┐ │
│ [数据] [波形]      [暂停][保存日志]│ │ 01 03 00 00 00 02             │ │
│ ┌──────────────────────────────┐ │ └───────────────────────────────┘ │
│ │[12:00:01.234] OK              │ │ [发送 (Ctrl+Enter)] 定时[1000]ms │
│ │[TX] 01 03 00 00 00 02         │ │ ┌──── 快捷指令（双击发送）────┐ │
│ └──────────────────────────────┘ │ │ 查询版本 | AT+VER?  | 文本    │ │
│                                  │ │ 读寄存器 | 01 03 .. | HEX     │ │
├──────────────────────────────────┴───────────────────────────────────┤
│ 就绪                    RX 1.2 KB / TX 64 B   [清零]      12:00:01     │
└──────────────────────────────────────────────────────────────────────┘
```

## 快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+Enter` | 发送输入框内容 |
| `Ctrl+L` | 清空输入框 |
| `Ctrl+D` | 清空接收区 |
| `Ctrl+R` | 重发上一次数据 |
| 右键 | 文本区复制 / 粘贴 / 全选 / 清空菜单 |

## 界面主题

菜单「工具 → 界面主题」可在三套配色间即时切换，**无需重启**：串口连接状态、
接收区内容、输入框内容都不会丢失，选择会写入配置文件，下次启动直接生效。

| 主题 | 风格 |
| --- | --- |
| 暗色（默认） | 中性灰蓝底，长时间调试不刺眼 |
| 浅色 | 白底深字，适合明亮环境或投屏演示 |
| 绚丽（霓虹） | 紫黑底 + 品红/青色霓虹高亮，波形曲线与收发数据更醒目 |

## 常用输入格式（HEX 模式）

均受支持，可混用注释：

```
AA BB CC
AABBCC
0xAA,0xBB;0xCC
AA-BB-CC   // 前缀 0x 与各类分隔符都会被忽略
```

## 打包为 Windows exe

```bat
build_exe.bat
```

脚本会依次「生成图标 → 源码自检 → 调用 PyInstaller」，产物为
`dist\COM-Test-Tool.exe`：单文件、免安装、约 12 MB，已内嵌图标与版本信息。

手动打包：

```bat
python -m pip install pyinstaller
python tools\make_icon.py
python -m PyInstaller --noconfirm --clean COM-Test-Tool.spec
```

因为打包为 GUI 程序（无控制台黑框），自检结果需要写入文件查看：

```bat
dist\COM-Test-Tool.exe --selftest --log selftest.txt
```

说明：

- spec 中显式声明了 `serial.urlhandler.protocol_*` 隐藏导入，否则打包后
  `loop://`、`socket://` 等虚拟端口会失效（这是 pyserial 动态加载导致的坑）
- 图标由 `tools/make_icon.py` 用标准库绘制（无需 Pillow），含 16~256 共 7 种尺寸
- 版本信息来自 `packaging/version_info.txt`，可在 exe 属性页查看
- exe 与源码版共用配置：`%USERPROFILE%\.com_test_tool\config.json`
- `build/` 为打包中间产物，可随时删除

## 项目结构

```
COM/
├── main.py                     入口（含 --selftest 自检）
├── requirements.txt
├── run.bat / run.sh            一键启动脚本（源码运行）
├── build_exe.bat               一键打包 Windows exe
├── COM-Test-Tool.spec          PyInstaller 打包配置
├── tools/make_icon.py          生成多尺寸应用图标（纯标准库）
├── packaging/version_info.txt  exe 版本信息资源
├── assets/app.ico              由 make_icon.py 生成
├── app/
│   ├── core/                   与界面无关的核心逻辑
│   │   ├── data_utils.py       HEX/文本编解码、校验和、数值解析
│   │   ├── serial_engine.py    线程安全串口引擎（枚举/打开/收发/控制线）
│   │   └── config.py           配置持久化（用户目录 JSON，原子写入）
│   └── ui/
│       ├── theme.py            三套主题（暗色/浅色/霓虹）与跨平台字体探测
│       ├── widgets.py          文本区、状态灯、气泡提示
│       ├── receive_panel.py    接收区 + 实时波形
│       ├── send_panel.py       发送区 + 快捷指令
│       └── main_window.py      主窗口装配与事件调度
```

## 常见问题

- **没有可用串口？** 选择「虚拟回环 (loop://) —— 无硬件自测」即可完整验证收发、波形与定时功能。
- **Linux 提示权限不足（Permission denied）？** 将当前用户加入 `dialout` 组（Ubuntu：`sudo usermod -aG dialout $USER`）后重新登录；或临时用 `sudo` 运行。
- **Windows 提示端口被占用？** 关掉其他串口工具（含 IDE 串口监视器）后重试。
- **macOS 选哪个设备？** 优先选 `/dev/cu.*`（如 `/dev/cu.usbserial-1420`），避免 `/dev/tty.*` 的阻塞行为。
- **配置存在哪？** 用户目录下 `.com_test_tool/config.json`，菜单「文件 → 打开配置目录」可直接跳转；可用环境变量 `COM_TOOL_HOME` 改写路径。
