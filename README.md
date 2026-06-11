# 我的输入法（Rime 定制 + 输入统计）

基于 [Rime / 小狼毫](https://rime.im) 定制的中英文输入法，外加一个独立的输入统计托盘程序。

## 项目结构

```
IME/
├── rime-config/          Rime 配置（部署到 %APPDATA%\Rime）
│   ├── default.custom.yaml        方案列表、切换键、候选数
│   ├── weasel.custom.yaml         外观 + 游戏进程自动英文模式
│   ├── rime_ice.custom.yaml       雾凇拼音补丁（挂统计日志）
│   ├── luna_pinyin_simp.custom.yaml  朙月拼音补丁（兜底方案）
│   ├── rime.lua                   Lua 入口
│   └── lua/commit_logger.lua      上屏文字记录器（统计数据来源）
├── stats-app/            统计托盘程序（Python）
│   ├── ime_stats.py               主程序
│   ├── requirements.txt
│   └── start_stats.vbs            静默启动/开机自启用
├── scripts/              一键部署脚本（PowerShell）
└── docs/                 设计、安装、开发日志
```

## 快速开始

1. 安装小狼毫：https://rime.im → 下载 Weasel 安装包
2. `scripts\install_rime_ice.ps1` —— 安装雾凇拼音词库（智能度接近商业输入法）
3. `scripts\deploy_rime_config.ps1` —— 部署本项目配置
4. 右键任务栏小狼毫图标 →「重新部署」
5. 统计程序：`cd stats-app && pip install -r requirements.txt && pythonw ime_stats.py`

详细步骤见 `docs/02-安装指南.md`。

## 核心特性

- **中英文输入**：雾凇拼音方案（大词库、整句输入、词频学习），左 Shift 切中英
- **输入统计**：托盘图标点开即看今日/累计按键次数、中文字数、英文字符/单词数
- **游戏兼容**：常见游戏进程自动进入英文直通模式（零延迟、不弹候选框），统计钩子为被动监听不注入游戏，必要时可一键暂停
