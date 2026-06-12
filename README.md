# 我的输入法（Rime 定制 + 输入统计）

基于 [Rime / 小狼毫](https://rime.im) 定制的中英文输入法（雾凇拼音词库 + walter_light 主题），
外加输入统计托盘程序（按键/中英文字数、时段分布、趋势图、常用词、周报、高频词自动喂回输入法）。

## 项目结构

```
IME/
├── rime-config/          Rime 配置（部署到 %APPDATA%\Rime）
│   ├── default.custom.yaml        方案列表、切换键、候选数
│   ├── weasel.custom.yaml         walter_light 主题 + 游戏自动英文 + 关闭切换提示
│   ├── rime_ice.custom.yaml       雾凇拼音补丁（挂统计日志）
│   ├── rime.lua / lua/            上屏文字记录器（统计数据来源）
├── stats-app/            统计程序（Python 版，稳定）
│   ├── ime_stats.py               托盘主程序（v0.9）
│   ├── word_worker.py             分词/喂词独立进程（jieba+pypinyin）
│   └── requirements.txt
├── csharp/               统计程序（C# 版 v1.0，实验性，更省内存）
├── scripts/
│   ├── deploy_rime_config.ps1     部署 Rime 配置（带备份）
│   ├── install_rime_ice.ps1       安装/更新雾凇词库 + 自动重新部署
│   ├── build_exe.bat              Python 版打包成单文件 IMEStats.exe
│   ├── build_csharp.bat           编译 C# 版（需 .NET 8 SDK）
│   ├── enable_autostart.bat       开机自启 + 每小时看门狗（优先用 exe）
│   └── enable_weekly_dict_update.bat  每周日自动更新词库
├── docs/                 方案设计、安装指南、开发日志、输入周报
└── ime-repo.bundle       git 仓库备份（git clone ime-repo.bundle 可恢复历史）
```

## 快速开始（新机器）

1. 装小狼毫：https://rime.im
2. `scripts\install_rime_ice.ps1` —— 装雾凇拼音并自动部署
3. `scripts\deploy_rime_config.ps1` —— 部署本项目配置 → 右键小狼毫「重新部署」
4. 统计程序（二选一）：
   - `cd stats-app && pip install -r requirements.txt`，然后 `scripts\build_exe.bat` 打包
   - 或装 .NET 8 后 `scripts\build_csharp.bat` 用 C# 版
5. `scripts\enable_autostart.bat` —— 开机自启 + 看门狗

## 统计架构

Rime Lua 把每次上屏写入 commit_log.txt → 托盘程序实时统计中文字数/时段；
全局钩子（被动、绝不阻塞按键）计按键与英文；word_worker 每 6 小时 jieba 分词
更新词频，并把高频词注音写回 Rime custom_phrase 提权重（越用越懂你）。
日志超 2MB 自动归档；数据全部本地（stats.db, WAL）。
