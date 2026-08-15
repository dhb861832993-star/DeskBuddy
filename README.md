# 🤖 DeskBuddy

> 双击一下，一键呼出。像手机桌面一样管理，像 Spotlight 一样搜索，问不到的交给 AI。

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Version](https://img.shields.io/badge/version-2.0.0-brightgreen.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue.svg)
[![Release](https://img.shields.io/badge/download-Releases-orange)](https://github.com/dhb861832993-star/DeskBuddy/releases)

**DeskBuddy** 是一个 **macOS 风格**的 Windows 桌面伙伴：连续快速双击热键（默认 `Ctrl`），立即呼出**手机桌面式的图标宫格菜单**，搜索并启动常用软件、网页、文件夹——比翻桌面、找开始菜单快得多。内置 **AI 对话**，搜不到的直接问 AI；还支持 **MCP**，让任何 AI 工具都能帮你把常用工具收进菜单。

无需安装 .NET 运行时 —— 发布为**自包含单文件 exe**，复制到任何 Win10 / Win11 电脑即可运行；也提供 **Inno Setup 安装包**（免管理员）。

---

## 📸 界面预览

### 主菜单
双击热键呼出，图标宫格 + 实时搜索，右下角彩色 ✨ 一键进入 AI 对话：

![主菜单](screenshots/menu.png)

### 实时搜索
输入即过滤，支持中文、拼音关键词、路径；**还能同时搜电脑文件**（可选，结果以「文件」分区显示在下方，不干扰菜单条目）：

![搜索](screenshots/search.png)

### 文件搜索（可选）
在设置里指定目录范围，文件搜索走**内存索引毫秒级返回**，且**实时自动更新**（新建/删除/改名几秒内同步）；右键文件可打开、定位、复制路径、重命名、删除：

![文件搜索](screenshots/filesearch.png)

### 设置窗口
macOS 系统设置风格的**左侧目录栏**：通用 / AI 对话 / AI 快捷添加（MCP） / 菜单项，紧凑清晰：

![设置](screenshots/settings.png)

### AI 对话
和本机 DeepSeek Harness 的 agent 零配置对话：流式输出、工具状态、多问题问答：

![AI 对话](screenshots/chat.png)

---

## ✨ 功能特性

| 功能 | 说明 |
| --- | --- |
| ⌨️ 双击热键呼出 | 默认双击 `Ctrl`，可在设置窗口一键更改 Alt / Shift / CapsLock / Win；间隔可调 |
| 🖱️ **单击即开** | 点击任意图标直接启动，不再区分单击/双击；已打开的程序（含微信/QQ 等托盘程序）**只弹出不重复启动** |
| 🤖 AI 对话 | 默认**本机 Harness** 零配置对话（流式输出、工具状态、授权/提问处理）；也支持 OpenAI 兼容 API |
| 🔧 MCP 接入 | 内置 MCP 服务，Claude Desktop / Cursor / Cherry Studio 等 AI 工具可直接查看/添加/删除你的快捷菜单 |
| 🖱️ 拖拽添加 | 从桌面/资源管理器拖文件、快捷方式、文件夹、网址到菜单或设置窗口，自动识别类型、立即生效 |
| 🔍 实时搜索 | 输入即过滤，支持中文、拼音关键词、路径；`↑↓←→` 选择，`Enter` 打开 |
| 🗂️ 文件搜索 | 可选：在设置里指定目录范围（支持多盘/环境变量），**内存索引毫秒级**返回，**实时自动更新**（新建/删除/改名自动同步）；结果以「文件」分区显示在菜单下方，不冲突 |
| 🍎 苹果风 UI | 圆角卡片 + 亚克力毛玻璃、浅色/深色自动跟随系统、彩色品牌图标 |
| ⚙️ 图形化设置 | 左侧目录栏设置窗口：快捷键捕获、双击间隔、主题、AI 配置、菜单项管理，保存即生效 |
| 🧩 五种条目类型 | `app` 程序 / `url` 网页 / `folder` 文件夹 / `file` 文件 / `command` 命令 |
| 📦 自动提取图标 | 程序条目自动提取 exe 图标；AI 智能启动（本机服务在跑→直接开页面，没跑→脚本拉起） |
| 🖱️ 系统托盘 | 右键托盘：设置、编辑配置、重新加载、开机自启、退出 |
| 📄 JSON 配置 | 高级选项仍可手改 JSON，热加载自动生效 |

---

## 📥 安装

> ⬇️ **直接下载**：[Releases 页面](https://github.com/dhb861832993-star/DeskBuddy/releases) 提供 `DeskBuddy-Setup.exe`（安装包）和 `DeskBuddy.exe`（绿色版）。

### 方式一：安装包（推荐）
运行 `DeskBuddy-Setup.exe`，一路下一步即可。可选“开机自动启动”和“桌面快捷方式”。

### 方式二：便携版
双击 `installer/安装DeskBuddy.bat`：自动复制到 `%LOCALAPPDATA%\DeskBuddy`，创建快捷方式并注册开机自启。卸载运行 `installer/卸载DeskBuddy.bat`。

### 方式三：绿色直用
直接把 `DeskBuddy.exe` 复制到任意位置双击运行（首次运行自动生成配置；旧版 QuickMenu 配置会自动迁移）。

---

## 🎮 使用方法

1. **双击 Ctrl**（快速按两下）→ 菜单从屏幕上方弹出，手机桌面式图标宫格展示
2. 输入关键字过滤（例：`微信`、`WPS`、`baidu`），或 **单击图标直接打开**
3. `↑` / `↓` / `←` / `→` 在图标间移动，`Enter` 打开
4. **右键图标** → 操作菜单（移到开头/上移/下移/移到最后/编辑…/删除）；**按住图标拖到另一个图标上** → 交换位置，立即保存生效
5. **程序已打开？** 单击菜单项只会把它**弹出到前台**（微信最小化/关到托盘也能弹回来），不会二次启动
6. **`Esc` 随时退出**；点击其他窗口自动收起；再次双击 Ctrl 关闭
7. 右键系统托盘图标 → 设置 / 重新加载 / 开机自启 / 退出

## 🗂️ 文件搜索（可选）

想让 DeskBuddy 也能搜电脑上的文件？在 **设置 → 搜索** 里开启并指定范围即可：

- **搜索范围**：每行一个目录路径（支持环境变量，如 `%USERPROFILE%\Desktop`、`D:\`），可跨多个盘
- **与菜单搜索不冲突**：菜单条目永远排在前面；文件结果以「文件」分区显示在下方，键盘继续往下选、Enter 用默认程序打开
- **毫秒级返回**：首次搜索在后台建立文件名内存索引（低优先级，不卡电脑），之后每次搜索都是内存匹配
- **实时自动更新**：监听搜索范围，文件新建/删除/改名后**几秒内自动同步**索引，新文件立即可搜到；增量更新不做全盘重建
- **文件右键菜单**：打开 / 打开文件所在目录 / 复制文件路径 / 重命名 / 删除（删除有二次确认）
- 自动跳过隐藏、系统、构建产物（`node_modules`/`bin`/`obj` 等）与链接目录

## 🤖 AI 对话

默认**本机 Harness 模式**：直接和本机运行的 [DeepSeek Harness](https://github.com/deepseek-ai) agent 对话（零配置，点 ✨ 即聊，无需 API 密钥）。在 **设置 → AI 对话** 可切换：

- **接入方式**
  - **本机 Harness**（默认）：调用 `http://127.0.0.1:3080` 的 Harness，会话策略：留空=最近更新的会话、`new`=每次新建、或填具体 sessionId
  - **OpenAI API**：标准 OpenAI 兼容接口，默认 DeepSeek 官方（需填 API 密钥）
- **按模式联动**：设置里 Harness 模式只显示 Harness 相关项，OpenAI 模式只显示 API 相关项，不相关设置自动隐藏

用法：
- 呼出菜单 → 点搜索框右侧彩色 **✨** → 打开 AI 对话窗口
- **会话管理**：标题栏可选择/刷新会话，历史对话自动加载；与 web 页面一致——**已归档的会话自动隐藏**
- 输入问题 Enter 发送，**流式输出**，实时显示 agent 状态（🧠 思考、🔧 工具调用、⏳ 步骤、✅ 完成）
- agent 需要**授权/提问**时出现操作条（允许/拒绝 / 输入回答；**一次多个问题会逐个列出，全部答完再提交**）
- 搜索**无结果**时，菜单里出现「✨ 用 AI 回答：xxx」，点击直接把问题交给 AI

## 🔧 MCP（AI 快捷添加菜单）

DeskBuddy 内置 **MCP 服务**（默认开启），任何支持 MCP 的 AI 工具都可以直接查看 / 添加 / 删除你的快捷菜单——让 AI 帮你把常用工具收进菜单，静默生效，无需重启。

- **开关**：设置 → AI 快捷添加（MCP）；默认开启，仅本机可访问
- **接入方式**：`DeskBuddy.exe --mcp`（stdio 传输）。设置里有「复制接入配置」按钮，一键复制客户端配置
- **工具**：
  - `list_menu_items` — 列出所有条目（只读）
  - `add_menu_item` — 添加条目：`name` / `type`(app/url/folder/file/command) / `path` / `args?` / `keywords?` / `icon?`
  - `remove_menu_item` — 按名称删除条目
- **实现原理**：`--mcp` 子进程通过命名管道把请求转发给已运行的主进程，由主进程统一读写配置并刷新菜单，避免并发写配置冲突

客户端配置示例（Claude Desktop 的 `claude_desktop_config.json`）：

```json
{
  "mcpServers": {
    "deskbuddy": {
      "command": "C:\\Users\\<你的用户名>\\AppData\\Local\\DeskBuddy\\DeskBuddy.exe",
      "args": ["--mcp"]
    }
  }
}
```

## ⚙️ 图形化设置

**左侧目录栏**设置窗口（菜单底部齿轮 / 托盘右键「设置…」）：

- **通用**：呼出快捷键捕获、双击间隔滑块、外观主题（自动/浅色/深色）
- **AI 对话**：接入方式（Harness / OpenAI）、会话策略、接口/模型/密钥/系统提示（按模式联动显示）
- **AI 快捷添加（MCP）**：开关 + 一键复制接入配置
- **菜单项**：拖拽添加 / 列表管理 / 上移下移，点「保存」**立即生效**

### 菜单项管理（增删改 + 排序）

- **拖拽添加（最方便）**：直接从桌面 / 资源管理器把文件、快捷方式、文件夹拖进列表；或在浏览器里把网址文字拖进来。自动识别类型：
  - `.lnk` 快捷方式 → 解析目标：exe → 程序，文件夹 → 文件夹（**带参数的快捷方式保留本体，按原始语义启动**）
  - `.url` 网页快捷方式 → 自动读取网址；`.exe` → 程序；文件夹 → 文件夹；其他文件 → 文件
  - 网页地址文字 → 网址
- 或者点「添加」手动填写（名称 / 类型 / 路径 / 参数 / 关键词），双击条目可直接编辑

> 💡 也可以直接把文件**拖到弹出的菜单上**：立即添加并保存，不用进设置。

---

## ⚙️ 配置文件 `DeskBuddy.config.json`

位于**程序同目录**（首次运行自动生成；旧版 QuickMenu 配置自动迁移）。编辑保存后，下次呼出菜单即生效。

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
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer\DeskBuddy.iss
```

---

## ❓ 常见问题

**双击 Ctrl 没反应？**
可能与其他软件（输入法、快捷键工具）冲突。打开设置窗口，把快捷键改为 `CapsLock` 或 `Alt` 即可。

**提示找不到程序？**
把 `path` 写成完整路径，例如 `"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"`。

**点菜单项弹出了新登录界面（如微信）？**
不会——DeskBuddy 检测到程序已在运行（含托盘隐藏），只会把它弹出到前台，不重复启动。

**开机自启勾选后没生效？**
自启写入当前用户注册表（`HKCU\...\Run`），无需管理员。若被杀软拦截请允许。

**换电脑 / 绿色分发？**
自包含 exe 无任何运行时依赖，复制 `DeskBuddy.exe` + `DeskBuddy.config.json` 即可。

---

## 🏗️ 技术说明

- .NET 8 / C# / WPF，WinForms 仅用于托盘图标
- 全局低级键盘钩子（`WH_KEYBOARD_LL`）实现双击检测，自动过滤按住重复
- `SetWindowCompositionAttribute` 实现 Win10/11 亚克力毛玻璃，失败时自动回退半透明
- 单实例通过命名 Mutex + EventWaitHandle 通信
- MCP 服务基于官方 `ModelContextProtocol` SDK，stdio 传输 + 命名管道 IPC
- 自发布：`PublishSingleFile` + 压缩，约 66 MB（免装运行时）
