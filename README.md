# QuickMenu 快启

> 双击一下，一键呼出。像手机桌面一样管理，像 Spotlight 一样搜索。

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Version](https://img.shields.io/badge/version-1.5.0-brightgreen.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue.svg)
[![Release](https://img.shields.io/badge/download-Releases-orange)](https://github.com/dhb861832993-star/QuickMenu/releases)

一个 **macOS 风格**的 Windows 快速启动器：**连续快速双击热键**（默认 Ctrl，可配置），立即呼出**手机桌面式的图标宫格菜单**，搜索并启动常用软件、网页、文件夹——比翻桌面、找开始菜单快得多。内置 **AI 对话**，搜不到的直接问 AI。

无需安装 .NET 运行时 —— 发布为**自包含单文件 exe**，复制到任何 Win10 / Win11 电脑即可运行；也提供 **Inno Setup 安装包**（免管理员）。

---

## ✨ 功能特性

| 功能 | 说明 |
| --- | --- |
| ⌨️ 双击热键呼出 | 默认双击 `Ctrl`，可在**设置窗口**一键更改 Alt / Shift / CapsLock / Win；间隔可调 |
| ⚙️ 图形化设置 | 菜单齿轮 / 托盘右键「设置…」打开 macOS 风格设置窗口：按键捕获、双击间隔滑块、主题切换、**AI 对话配置**，**保存后立即生效**，无需重启、无需改 JSON |
| 🤖 AI 对话 | 搜索框 **✨ 按钮**打开 AI 对话窗口（OpenAI 兼容接口，默认 DeepSeek 官方 API）：流式回复、多轮上下文、停止/清空；搜索无结果时出现「✨ 用 AI 回答」一键提问 |
| 🖱️ 拖拽添加 | 直接从**桌面 / 资源管理器拖文件、快捷方式、文件夹、网址**到设置窗口或弹出的菜单上，自动识别类型生成条目；拖到菜单上立即生效 |
| 🔍 实时搜索 | 输入即过滤，支持中文、拼音关键词、路径；`↑↓` 选择，`Enter` 打开 |
| 🍎 苹果风 UI | 圆角卡片 + 亚克力毛玻璃（Acrylic）、浅色/深色自动跟随系统 |
| 🧩 五种条目类型 | `app` 程序 / `url` 网页 / `folder` 文件夹 / `file` 文件 / `command` 命令 |
| 📦 自动提取图标 | 程序条目自动提取 exe 图标，无需手动配置 |
| 🖱️ 系统托盘 | 右键托盘：设置、编辑配置、重新加载、开机自启、退出 |
| 🚀 单实例 | 重复运行会唤起已运行的实例弹出菜单 |
| 📄 JSON 配置 | 高级选项仍可手改 JSON，热加载自动生效 |

---

## 📥 安装

> ⬇️ **直接下载**：[Releases 页面](https://github.com/dhb861832993-star/QuickMenu/releases) 提供 `QuickMenu-Setup.exe`（安装包）和 `QuickMenu.exe`（绿色版）。

### 方式一：安装包（推荐）
运行 `QuickMenu-Setup.exe`，一路下一步即可。可选“开机自动启动”和“桌面快捷方式”。

### 方式二：便携版
双击 `installer/安装QuickMenu.bat`：自动复制到 `%LOCALAPPDATA%\QuickMenu`，创建快捷方式并注册开机自启。卸载运行 `installer/卸载QuickMenu.bat`。

### 方式三：绿色直用
直接把 `QuickMenu.exe` 复制到任意位置双击运行（首次运行自动生成配置）。

---

## 🎮 使用方法

1. **双击 Ctrl**（快速按两下）→ 菜单从屏幕上方弹出，**手机桌面式图标宫格**展示
2. 输入关键字过滤（例：`git`、`记事`、`baidu`）
3. `↑` / `↓` / `←` / `→` 在图标间移动，`Enter` 打开；双击图标也可打开
4. **右键图标** → 操作菜单（移到开头/上移/下移/移到最后/编辑…/删除，删除需二次确认）；**按住图标拖到另一个图标上** → 交换位置，全部立即保存生效
5. **`Esc` 随时退出**（全局生效，无论焦点在哪）；点击其他窗口也会自动收起
6. 再次双击 Ctrl → 直接关闭菜单
7. 右键系统托盘图标 → 常用管理操作

## 🤖 AI 对话

默认**本机 Harness 模式**：直接和本机运行的 [DeepSeek Harness](https://github.com/deepseek-ai) agent 对话（零配置，点 ✨ 即聊，无需 API 密钥）。在 **设置 → AI 对话** 可切换：

- **接入方式**
  - **本机 Harness**（默认）：调用 `http://127.0.0.1:3080` 的 Harness，会话策略：留空=最近更新的会话、`new`=每次新建、或填具体 sessionId
  - **OpenAI API**：标准 OpenAI 兼容接口，默认 DeepSeek 官方（需填 API 密钥，DeepSeek 开放平台申请）
- **Harness 地址 / 会话策略**（Harness 模式）
- **接口地址 / 模型 / API 密钥 / 系统提示**（OpenAI 模式）

用法：
- 呼出菜单 → 点搜索框右侧 **✨** → 打开 AI 对话窗口
- 输入问题 Enter 发送，**流式输出**，可多轮追问；「停止」中断、「清空」重来
- 搜索**无结果**时，菜单里会出现「✨ 用 AI 回答：xxx」，点击直接把问题交给 AI
- Esc 关闭对话窗口

## ⚙️ 图形化设置

点击菜单**底部齿轮**⚙，或托盘右键 →「设置…」，打开 macOS 风格设置窗口：

- **呼出快捷键**：点击按钮后直接按下想用的键（Ctrl / Alt / Shift / CapsLock / Win），自动捕获
- **双击判定间隔**：滑块 200–600 毫秒，越小越灵敏
- **外观主题**：自动 / 浅色 / 深色，实时预览

点「保存」**立即生效**，无需重启程序，也不用改 JSON。

### 菜单项管理（增删改 + 排序）

设置窗口下半部分就是「菜单项」列表：

- **拖拽添加（最方便）**：直接从桌面 / 资源管理器把文件、快捷方式、文件夹拖进列表；或在浏览器里把网址文字拖进来。自动识别类型：
  - `.lnk` 快捷方式 → 解析目标：exe → 程序，文件夹 → 文件夹
  - `.url` 网页快捷方式 → 自动读取网址
  - `.exe` → 程序；文件夹 → 文件夹；其他文件 → 文件
  - 网页地址文字 → 网址
- 或者点「添加」手动填写（名称 / 类型 / 路径 / 参数 / 关键词），双击条目可直接编辑
- 「删除」「上移」「下移」调整列表，点「保存」生效

> 💡 也可以直接把文件**拖到弹出的快速菜单上**：立即添加并保存，不用进设置。

---

## ⚙️ 配置文件 `QuickMenu.config.json`

位于**程序同目录**（首次运行自动生成）。编辑保存后，下次呼出菜单即生效。

```json
{
  "hotkey": "Ctrl",              // Ctrl | Alt | Shift | CapsLock | Win
  "doubleTapIntervalMs": 380,    // 两次按键的最大间隔（毫秒）
  "theme": "auto",               // auto | light | dark
  "windowWidth": 680,
  "maxWindowHeight": 560,
  "items": [
    { "name": "记事本", "type": "app", "path": "notepad", "keywords": "文本 编辑" },
    { "name": "GitHub", "type": "url", "path": "https://github.com" },
    { "name": "工作目录", "type": "folder", "path": "D:\\work" },
    { "name": "系统设置", "type": "command", "path": "control" },
    { "name": "某文档", "type": "file", "path": "C:\\docs\\plan.docx" }
  ]
}
```

### 条目字段

| 字段 | 说明 |
| --- | --- |
| `name` | 显示名称 |
| `type` | `app` / `url` / `folder` / `file` / `command` |
| `path` | 程序名（自动搜索 PATH）或完整路径 / 网址 / 文件夹 / 文件 / 命令 |
| `args` | 附加参数（app / command 使用） |
| `keywords` | 额外搜索关键词，空格分隔 |
| `icon` | 可选自定义图标路径（.ico / .png / .exe），留空自动提取 |
| `hidden` | `true` 时隐藏该条目（配置保留） |

> 程序名搜索顺序：直接路径 → PATH 环境变量 → Program Files / LocalAppData\Programs。

---

## 🛠️ 自行构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows 10/11）。

```powershell
# 一键发布（自包含单文件 exe → dist\app）
.\build.ps1

# 制作安装包（需 Inno Setup 6）
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer\QuickMenu.iss
```

---

## ❓ 常见问题

**双击 Ctrl 没反应？**
可能与其他软件（输入法、快捷键工具）冲突。打开设置窗口，把快捷键改为 `CapsLock` 或 `Alt` 即可。

**提示找不到程序？**
把 `path` 写成完整路径，例如 `"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"`。

**开机自启勾选后没生效？**
自启写入当前用户注册表（`HKCU\...\Run`），无需管理员。若被杀软拦截请允许。

**换电脑 / 绿色分发？**
自包含 exe 无任何运行时依赖，复制 `QuickMenu.exe` + `QuickMenu.config.json` 即可。

---

## 🏗️ 技术说明

- .NET 8 / C# / WPF，WinForms 仅用于托盘图标
- 全局低级键盘钩子（`WH_KEYBOARD_LL`）实现双击检测，自动过滤按住重复
- `SetWindowCompositionAttribute` 实现 Win10/11 亚克力毛玻璃，失败时自动回退半透明
- 单实例通过命名 Mutex + EventWaitHandle 通信
- 自发布：`PublishSingleFile` + 压缩，约 63 MB（免装运行时）
