# -*- coding: utf-8 -*-
"""
IME Stats —— 输入统计托盘程序 v1.2
================================
功能：
  1. 全局统计按键次数（被动低级钩子，对游戏无干扰）
  2. 读取 Rime 上屏日志（commit_log.txt）统计中文字数
  3. 英文模式下统计英文字符数 / 单词数
  4. 托盘面板实时刷新：指标卡片、今日时段分布、14天趋势、常用词Top10
  5. 每周一自动生成上周输入周报（docs/reports/*.md）
  6. 数据存 SQLite（stats.db，WAL 模式），所有数据仅保存在本机

v0.9：支持 pyinstaller 单文件打包（scripts\\build_exe.bat），打包后
`IMEStats.exe --word-worker` 复用同一 exe 跑分词，无需 Python 环境。

依赖（脚本方式运行时）：pip install -r requirements.txt
运行：pythonw ime_stats.py 或打包后的 IMEStats.exe
"""

import os
import sys
import json
import time
import queue
import sqlite3
import threading
import datetime
import subprocess
import ctypes
from ctypes import wintypes

import psutil
from pynput import keyboard
import pystray
from PIL import Image, ImageDraw
import tkinter as tk

FROZEN = getattr(sys, "frozen", False)
APP_DIR = (os.path.dirname(sys.executable) if FROZEN
           else os.path.dirname(os.path.abspath(__file__)))
DATA_DIR = os.path.join(os.environ.get("APPDATA", APP_DIR), "IMEStats")
DB_PATH = os.path.join(DATA_DIR, "stats.db")
CONFIG_PATH = os.path.join(APP_DIR, "config.json")
WORD_WORKER = os.path.join(APP_DIR, "word_worker.py")
# 周报存数据目录（与源码仓库位置无关，移动/删除仓库也不丢）
REPORTS_DIR = os.path.join(DATA_DIR, "reports")
RIME_USER_DIR = os.path.join(os.environ.get("APPDATA", ""), "Rime")
COMMIT_LOG = os.path.join(RIME_USER_DIR, "commit_log.txt")
LOG_ROTATE_BYTES = 2 * 1024 * 1024      # 超过 2MB 触发归档

DEFAULT_CONFIG = {
    # 在这些进程前台时完全跳过计数（一般不需要，钩子是被动的；
    # 若某反作弊较敏感，可把游戏 exe 名加进来，小写）
    "skip_processes": [],
    "flush_interval_sec": 30,
}

# ---------------- Win32: 前台窗口 IME 中/英 模式检测 ----------------
user32 = ctypes.windll.user32
imm32 = ctypes.windll.imm32

WM_IME_CONTROL = 0x0283
IMC_GETCONVERSIONMODE = 0x0001
IME_CMODE_NATIVE = 0x0001
SMTO_ABORTIFHUNG = 0x0002


def foreground_ime_is_chinese():
    """返回前台窗口输入法是否处于中文模式；检测失败返回 None"""
    try:
        hwnd = user32.GetForegroundWindow()
        if not hwnd:
            return None
        ime_wnd = imm32.ImmGetDefaultIMEWnd(hwnd)
        if not ime_wnd:
            return None
        result = ctypes.c_size_t(0)  # lpdwResult 是指针宽度，64位下必须用 size_t
        ok = user32.SendMessageTimeoutW(
            ime_wnd, WM_IME_CONTROL, IMC_GETCONVERSIONMODE, 0,
            SMTO_ABORTIFHUNG, 50, ctypes.byref(result))
        if not ok:
            return None
        return bool(result.value & IME_CMODE_NATIVE)
    except Exception:
        return None


def foreground_process_name():
    try:
        hwnd = user32.GetForegroundWindow()
        pid = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        return psutil.Process(pid.value).name().lower()
    except Exception:
        return ""


def is_cjk(ch):
    cp = ord(ch)
    return (0x4E00 <= cp <= 0x9FFF or 0x3400 <= cp <= 0x4DBF
            or 0x20000 <= cp <= 0x2A6DF or 0xF900 <= cp <= 0xFAFF)


# ---------------- 数据层 ----------------
class Store:
    def __init__(self):
        self.conn = sqlite3.connect(DB_PATH, check_same_thread=False,
                                    timeout=15)
        self.conn.execute("PRAGMA journal_mode=WAL")
        self.lock = threading.Lock()
        self.conn.execute("""CREATE TABLE IF NOT EXISTS daily(
            date TEXT PRIMARY KEY,
            keys INTEGER DEFAULT 0,
            cn_chars INTEGER DEFAULT 0,
            en_chars INTEGER DEFAULT 0,
            en_words INTEGER DEFAULT 0)""")
        self.conn.execute("""CREATE TABLE IF NOT EXISTS hourly(
            date TEXT, hour INTEGER,
            keys INTEGER DEFAULT 0, cn_chars INTEGER DEFAULT 0,
            PRIMARY KEY(date, hour))""")
        self.conn.execute("""CREATE TABLE IF NOT EXISTS word_freq(
            word TEXT PRIMARY KEY, count INTEGER DEFAULT 0)""")
        self.conn.execute("""CREATE TABLE IF NOT EXISTS meta(
            k TEXT PRIMARY KEY, v TEXT)""")
        self.conn.commit()

    def add(self, date, keys=0, cn=0, ec=0, ew=0):
        with self.lock:
            self.conn.execute(
                """INSERT INTO daily(date, keys, cn_chars, en_chars, en_words)
                   VALUES(?,?,?,?,?)
                   ON CONFLICT(date) DO UPDATE SET
                     keys=keys+excluded.keys,
                     cn_chars=cn_chars+excluded.cn_chars,
                     en_chars=en_chars+excluded.en_chars,
                     en_words=en_words+excluded.en_words""",
                (date, keys, cn, ec, ew))
            self.conn.commit()

    def add_hourly(self, date, hour, keys=0, cn=0):
        with self.lock:
            self.conn.execute(
                """INSERT INTO hourly(date, hour, keys, cn_chars)
                   VALUES(?,?,?,?)
                   ON CONFLICT(date, hour) DO UPDATE SET
                     keys=keys+excluded.keys,
                     cn_chars=cn_chars+excluded.cn_chars""",
                (date, hour, keys, cn))
            self.conn.commit()

    def get_meta(self, k, default="0"):
        with self.lock:
            row = self.conn.execute(
                "SELECT v FROM meta WHERE k=?", (k,)).fetchone()
        return row[0] if row else default

    def set_meta(self, k, v):
        with self.lock:
            self.conn.execute(
                "INSERT INTO meta(k,v) VALUES(?,?) "
                "ON CONFLICT(k) DO UPDATE SET v=excluded.v", (k, str(v)))
            self.conn.commit()

    def today(self):
        d = datetime.date.today().isoformat()
        with self.lock:
            row = self.conn.execute(
                "SELECT keys,cn_chars,en_chars,en_words FROM daily WHERE date=?",
                (d,)).fetchone()
        return row or (0, 0, 0, 0)

    def total(self):
        with self.lock:
            row = self.conn.execute(
                "SELECT COALESCE(SUM(keys),0),COALESCE(SUM(cn_chars),0),"
                "COALESCE(SUM(en_chars),0),COALESCE(SUM(en_words),0) FROM daily"
            ).fetchone()
        return row

    def last_n(self, n=14):
        """最近 n 天，按日期升序（供图表绘制）"""
        with self.lock:
            rows = self.conn.execute(
                "SELECT date,keys,cn_chars,en_words FROM daily "
                "ORDER BY date DESC LIMIT ?", (n,)).fetchall()
        return rows[::-1]

    def today_hours(self):
        """今日 24 小时按键分布，返回 {hour: keys}"""
        d = datetime.date.today().isoformat()
        with self.lock:
            rows = self.conn.execute(
                "SELECT hour,keys FROM hourly WHERE date=?", (d,)).fetchall()
        return dict(rows)

    def top_words(self, n=10):
        with self.lock:
            return self.conn.execute(
                "SELECT word,count FROM word_freq "
                "ORDER BY count DESC LIMIT ?", (n,)).fetchall()

    def range_days(self, d1, d2):
        with self.lock:
            return self.conn.execute(
                "SELECT date,keys,cn_chars,en_words FROM daily "
                "WHERE date BETWEEN ? AND ? ORDER BY date", (d1, d2)).fetchall()


# ---------------- 按键统计 ----------------
class KeyCounter:
    """钩子回调内只做入队（微秒级），所有 Win32 查询放到工作线程，
    确保不增加任何按键延迟——这是游戏不受影响的关键。"""

    def __init__(self, store, config):
        self.store = store
        self.config = config
        self.paused = False
        self.keys = 0
        self.en_chars = 0
        self.en_words = 0
        self.letter_run = 0
        self.hour_acc = {}               # (date, hour) -> keys
        self.lock = threading.Lock()
        self.events = queue.Queue()
        self._mode_cache = (0.0, None)   # (时间, 是否中文)
        self._proc_cache = (0.0, "")
        self.listener = keyboard.Listener(on_press=self.on_press)
        self.worker = threading.Thread(target=self._work, daemon=True)

    def start(self):
        self.listener.start()
        self.worker.start()

    def stop(self):
        self.listener.stop()

    # ---- 钩子回调：只入队，绝不阻塞 ----
    def on_press(self, key):
        if not self.paused:
            self.events.put(getattr(key, "char", None))

    # ---- 工作线程：真正的统计逻辑 ----
    def _cached_ime_chinese(self):
        t, v = self._mode_cache
        now = time.monotonic()
        if now - t > 0.2:
            v = foreground_ime_is_chinese()
            self._mode_cache = (now, v)
        return v

    def _cached_proc(self):
        t, v = self._proc_cache
        now = time.monotonic()
        if now - t > 1.0:
            v = foreground_process_name()
            self._proc_cache = (now, v)
        return v

    def _work(self):
        while True:
            ch = self.events.get()
            try:
                if self._cached_proc() in self.config["skip_processes"]:
                    continue
                now = datetime.datetime.now()
                hkey = (now.date().isoformat(), now.hour)
                with self.lock:
                    self.keys += 1
                    self.hour_acc[hkey] = self.hour_acc.get(hkey, 0) + 1
                    if ch and ch.isalpha() and ch.isascii():
                        # 中文模式下字母是拼音编码，不计入英文；
                        # 检测失败时按英文计（说明见文档）
                        if self._cached_ime_chinese() is not True:
                            self.en_chars += 1
                            self.letter_run += 1
                    elif self.letter_run > 0:
                        self.en_words += 1
                        self.letter_run = 0
            except Exception:
                pass

    def flush(self):
        with self.lock:
            k, ec, ew = self.keys, self.en_chars, self.en_words
            hours = dict(self.hour_acc)
            self.keys = self.en_chars = self.en_words = 0
            self.hour_acc.clear()
        if k or ec or ew:
            self.store.add(datetime.date.today().isoformat(),
                           keys=k, ec=ec, ew=ew)
        for (d, h), n in hours.items():
            self.store.add_hourly(d, h, keys=n)

    def snapshot(self):
        """未落盘的实时增量（供面板实时显示，不清零）"""
        with self.lock:
            return self.keys, self.en_chars, self.en_words


# ---------------- Rime 上屏日志读取 ----------------
class CommitLogReader:
    """只统计中文字数/时段；分词由独立的 word worker 进程负责"""

    def __init__(self, store):
        self.store = store
        self.offset = int(store.get_meta("commit_log_offset", "0"))
        self.lock = threading.Lock()  # 面板线程与落盘线程可能并发调用 poll

    def poll(self):
        with self.lock:
            self._poll()

    def _poll(self):
        if not os.path.exists(COMMIT_LOG):
            return
        size = os.path.getsize(COMMIT_LOG)
        if size < self.offset:          # 日志被清空/轮转
            self.offset = 0
        if size == self.offset:
            return
        with open(COMMIT_LOG, "rb") as f:
            f.seek(self.offset)
            data = f.read()
        # 只消费到最后一个换行符：Rime 可能正写到半行，留给下次读，
        # offset 按消费的字节数推进（不会跳过、也不会把半行当坏行丢掉）
        end = data.rfind(b"\n")
        if end < 0:
            return
        day_cn, hour_cn = {}, {}
        for line in data[:end + 1].decode("utf-8", "ignore").split("\n"):
            parts = line.rstrip("\r").split("\t", 1)
            if len(parts) != 2:
                continue
            ts, text = parts
            cn = sum(1 for c in text if is_cjk(c))
            if cn:
                try:                # 按日志自身时间归账，跨天也准确
                    d, h = ts[:10], int(ts[11:13])
                except ValueError:
                    d, h = datetime.date.today().isoformat(), 0
                day_cn[d] = day_cn.get(d, 0) + cn
                hour_cn[(d, h)] = hour_cn.get((d, h), 0) + cn
        self.offset += end + 1
        for d, cn in day_cn.items():
            self.store.add(d, cn=cn)
        for (d, h), cn in hour_cn.items():
            self.store.add_hourly(d, h, cn=cn)
        self.store.set_meta("commit_log_offset", self.offset)


# ---------------- 周报生成 ----------------
def maybe_weekly_report(store):
    """启动时检查：上一个自然周（周一~周日）的周报没生成过就补一份"""
    try:
        today = datetime.date.today()
        last_mon = today - datetime.timedelta(days=today.weekday() + 7)
        last_sun = last_mon + datetime.timedelta(days=6)
        iso = last_mon.isocalendar()
        tag = f"{iso[0]}-W{iso[1]:02d}"
        if store.get_meta("weekly_report_last", "") == tag:
            return
        rows = store.range_days(last_mon.isoformat(), last_sun.isoformat())
        store.set_meta("weekly_report_last", tag)
        if not rows:
            return                      # 上周无数据，不生成
        prev_mon = last_mon - datetime.timedelta(days=7)
        prev = store.range_days(prev_mon.isoformat(),
                                (last_mon - datetime.timedelta(days=1)).isoformat())
        tk_ = sum(r[1] for r in rows)
        tcn = sum(r[2] for r in rows)
        tew = sum(r[3] for r in rows)
        pk = sum(r[1] for r in prev)

        def pct(cur, old):
            if not old:
                return "—"
            return f"{(cur - old) / old * 100:+.0f}%"

        lines = [
            f"# 输入周报 {tag}（{last_mon} ~ {last_sun}）",
            "",
            f"按键 **{tk_:,}** 次（环比上周 {pct(tk_, pk)}）；"
            f"中文 **{tcn:,}** 字；英文 **{tew:,}** 词。",
            "",
            "| 日期 | 按键 | 中文字 | 英文词 |",
            "|------|------|--------|--------|",
        ]
        for d, k, cn, ew in rows:
            lines.append(f"| {d} | {k:,} | {cn:,} | {ew:,} |")
        top = store.top_words(10)
        if top:
            lines += ["", "本周为止的常用词：" +
                      "、".join(f"{w}({c})" for w, c in top)]
        os.makedirs(REPORTS_DIR, exist_ok=True)
        path = os.path.join(REPORTS_DIR, f"输入周报-{tag}.md")
        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
    except Exception:
        pass


# ---------------- 托盘 + 面板 ----------------
def make_icon_image(paused=False):
    from PIL import ImageFont
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    color = (120, 120, 120, 255) if paused else (30, 144, 255, 255)
    d.rounded_rectangle([4, 4, 60, 60], radius=12, fill=color)
    try:  # PIL 默认字体不含中文，需加载系统字体
        font = ImageFont.truetype("msyh.ttc", 40)
        d.text((12, 6), "字", fill=(255, 255, 255, 255), font=font)
    except Exception:
        d.text((20, 20), "Z", fill=(255, 255, 255, 255))
    return img


class App:
    def __init__(self):
        self.config = dict(DEFAULT_CONFIG)
        if os.path.exists(CONFIG_PATH):
            try:
                with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                    self.config.update(json.load(f))
            except Exception:
                pass
        else:
            with open(CONFIG_PATH, "w", encoding="utf-8") as f:
                json.dump(DEFAULT_CONFIG, f, ensure_ascii=False, indent=2)

        self.store = Store()
        self.counter = KeyCounter(self.store, self.config)
        self.reader = CommitLogReader(self.store)
        self.ui_queue = queue.Queue()
        self.panel = None

        self.root = tk.Tk()
        self.root.withdraw()
        self.root.title("IME Stats")

    # ---- 词频 Worker（独立进程，跑完即退；jieba 不占常驻内存）----
    def run_word_worker(self):
        try:
            if FROZEN:      # 单文件 exe：复用自身
                subprocess.Popen([sys.executable, "--word-worker"],
                                 creationflags=0x08000000)  # CREATE_NO_WINDOW
            elif os.path.exists(WORD_WORKER):
                subprocess.Popen([sys.executable, WORD_WORKER],
                                 creationflags=0x08000000)
        except Exception:
            pass

    def word_scheduler(self):
        while True:
            self.run_word_worker()
            time.sleep(6 * 3600)

    # ---- 日志轮转：超 2MB 且字数/词频均已消化完才归档 ----
    def maybe_rotate_log(self):
        try:
            if not os.path.exists(COMMIT_LOG):
                return
            if os.path.getsize(COMMIT_LOG) < LOG_ROTATE_BYTES:
                return
            arch_dir = os.path.join(RIME_USER_DIR, "commit_log_archive")
            os.makedirs(arch_dir, exist_ok=True)
            dst = os.path.join(arch_dir, "commit_log_%s.txt" %
                               datetime.datetime.now().strftime("%Y%m%d_%H%M%S"))
            # 追平字数 → 校验词频 → 改名归档全程持锁：
            # 锁内 Rime 新写入的行要么被追平统计到，要么留在新日志里，绝不丢
            with self.reader.lock:
                self.reader._poll()     # 字数先追平
                size = os.path.getsize(COMMIT_LOG)
                if self.reader.offset < size:
                    return              # 还有半行没写完，下轮再说
                if int(self.store.get_meta("word_log_offset", "0")) < size:
                    self.run_word_worker()  # 词频还没消化完，催一把，下轮再轮转
                    return
                os.rename(COMMIT_LOG, dst)
                self.reader.offset = 0
                self.store.set_meta("commit_log_offset", 0)
                self.store.set_meta("word_log_offset", 0)
        except Exception:
            pass

    # ---- 后台线程：定时落盘 ----
    def flusher(self):
        loops = 0
        hourly = max(1, 3600 // self.config["flush_interval_sec"])
        while True:
            time.sleep(self.config["flush_interval_sec"])
            try:
                self.counter.flush()
                self.reader.poll()
                loops += 1
                if loops % hourly == 0:     # 约每小时：轮转 + 周报
                    self.maybe_rotate_log()
                    # 周报放循环里：长期不重启也能在跨周后生成（meta 防重复）
                    maybe_weekly_report(self.store)
            except Exception:
                pass

    # ---- 托盘 ----
    def build_tray(self):
        def toggle_pause(icon, item):
            self.counter.paused = not self.counter.paused
            icon.icon = make_icon_image(self.counter.paused)

        self.tray = pystray.Icon(
            "ime_stats", make_icon_image(), "输入统计",
            menu=pystray.Menu(
                pystray.MenuItem("统计面板",
                                 lambda: self.ui_queue.put("panel"),
                                 default=True),
                pystray.MenuItem("立即更新词频/喂词",
                                 lambda: self.run_word_worker()),
                pystray.MenuItem("暂停统计", toggle_pause,
                                 checked=lambda i: self.counter.paused),
                pystray.MenuItem("退出", lambda: self.ui_queue.put("quit")),
            ))
        self.tray.run_detached()

    # ---- 统计面板（每秒实时刷新，卡片式浅色 UI） ----
    BG, CARD, FG, SUB, ACCENT = "#FAFAFA", "#FFFFFF", "#3A3A3A", "#9B9B9B", "#3347D9"

    def show_panel(self):
        if self.panel and self.panel.winfo_exists():
            self.panel.lift()
            self.panel.focus_force()
            return
        p = tk.Toplevel(self.root)
        self.panel = p
        p.title("输入统计")
        p.attributes("-topmost", True)
        p.resizable(False, False)
        p.configure(bg=self.BG)

        wrap = tk.Frame(p, bg=self.BG, padx=20, pady=16)
        wrap.pack()

        # 标题行
        head = tk.Frame(wrap, bg=self.BG)
        head.pack(fill="x", pady=(0, 12))
        tk.Label(head, text="输入统计", font=("微软雅黑", 13, "bold"),
                 bg=self.BG, fg=self.FG).pack(side="left")
        tk.Label(head, text=datetime.date.today().strftime("%Y-%m-%d"),
                 font=("微软雅黑", 10), bg=self.BG, fg=self.SUB).pack(
            side="right", pady=(4, 0))

        # 四张指标卡片：大数字（今日） + 名称 + 累计
        cards = tk.Frame(wrap, bg=self.BG)
        cards.pack()
        names = ("按键次数", "中文字数", "英文字符", "英文单词")
        self._today_vars, self._total_vars = [], []
        for i, name in enumerate(names):
            card = tk.Frame(cards, bg=self.CARD, padx=18, pady=12)
            card.grid(row=0, column=i, padx=6)
            tv = tk.StringVar(value="0")
            av = tk.StringVar(value="累计 0")
            self._today_vars.append(tv)
            self._total_vars.append(av)
            tk.Label(card, textvariable=tv, font=("微软雅黑", 20, "bold"),
                     bg=self.CARD, fg=self.ACCENT if i == 1 else self.FG
                     ).pack()
            tk.Label(card, text=name, font=("微软雅黑", 10),
                     bg=self.CARD, fg=self.SUB).pack(pady=(2, 0))
            tk.Label(card, textvariable=av, font=("微软雅黑", 9),
                     bg=self.CARD, fg=self.SUB).pack()

        # 今日时段分布
        tk.Label(wrap, text="今日 · 时段分布（按键）", font=("微软雅黑", 9),
                 bg=self.BG, fg=self.SUB, anchor="w").pack(fill="x",
                                                           pady=(14, 3))
        self._hours_cv = tk.Canvas(wrap, width=560, height=90, bg=self.CARD,
                                   highlightthickness=0)
        self._hours_cv.pack()
        self._hours_cache = None

        # 最近 14 天柱状图（按键 / 中文）
        self._charts = []
        for title, color, idx in (("最近 14 天 · 按键次数", "#A9B8D6", 1),
                                  ("最近 14 天 · 中文字数", "#7B8AE0", 2)):
            tk.Label(wrap, text=title, font=("微软雅黑", 9), bg=self.BG,
                     fg=self.SUB, anchor="w").pack(fill="x", pady=(14, 3))
            cv = tk.Canvas(wrap, width=560, height=110, bg=self.CARD,
                           highlightthickness=0)
            cv.pack()
            self._charts.append((cv, idx, color))
        self._chart_cache = None

        # 常用词 Top 10
        tk.Label(wrap,
                 text="常用词 Top 10（每 6 小时后台更新，托盘菜单可手动更新）",
                 font=("微软雅黑", 9), bg=self.BG, fg=self.SUB,
                 anchor="w").pack(fill="x", pady=(14, 3))
        self._words_var = tk.StringVar()
        tk.Label(wrap, textvariable=self._words_var, font=("微软雅黑", 10),
                 bg=self.CARD, fg=self.FG, justify="left", anchor="w",
                 padx=14, pady=10, width=64, wraplength=540).pack(fill="x")

        def refresh():
            if not p.winfo_exists():
                return
            self.reader.poll()                      # 实时拉取上屏日志 → 中文字数
            lk, lec, lew = self.counter.snapshot()  # 未落盘的按键/英文增量
            dk, dcn, dec, dew = self.store.today()
            ak, acn, aec, aew = self.store.total()
            pairs = ((dk + lk, ak + lk), (dcn, acn),
                     (dec + lec, aec + lec), (dew + lew, aew + lew))
            for i, (today_v, total_v) in enumerate(pairs):
                self._today_vars[i].set(f"{today_v:,}")
                self._total_vars[i].set(f"累计 {total_v:,}")

            hours = self.store.today_hours()
            if hours != self._hours_cache:
                self._hours_cache = hours
                self._draw_hours(self._hours_cv, hours)

            rows = self.store.last_n(14)
            if rows:    # 今日实时增量并入最后一根柱子
                d, k, cn, ew = rows[-1]
                if d == datetime.date.today().isoformat():
                    rows = rows[:-1] + [(d, k + lk, cn, ew + lew)]
            if rows != self._chart_cache:
                self._chart_cache = rows
                for cv, idx, color in self._charts:
                    self._draw_chart(cv, rows, idx, color)

            self._words_var.set("　".join(
                f"{i+1}.{w} ×{c}" for i, (w, c) in
                enumerate(self.store.top_words(10))) or "还没有数据，去打几个字吧")
            p.after(1000, refresh)

        refresh()

    def _draw_chart(self, cv, rows, idx, color):
        cv.delete("all")
        if not rows:
            return
        W, H = int(cv["width"]), int(cv["height"])
        pad, base = 10, H - 18
        vmax = max(r[idx] for r in rows) or 1
        bw = (W - 2 * pad) / len(rows)
        for i, r in enumerate(rows):
            v = r[idx]
            x0 = pad + i * bw + bw * 0.18
            x1 = pad + (i + 1) * bw - bw * 0.18
            h = (base - 26) * v / vmax
            if v:
                cv.create_rectangle(x0, base - h, x1, base,
                                    fill=color, width=0)
                label = f"{v/1000:.1f}k" if v >= 10000 else f"{v:,}"
                cv.create_text((x0 + x1) / 2, base - h - 8, text=label,
                               font=("微软雅黑", 8), fill=self.SUB)
            cv.create_text((x0 + x1) / 2, H - 8, text=r[0][5:],
                           font=("微软雅黑", 8), fill=self.SUB)

    def _draw_hours(self, cv, hours):
        cv.delete("all")
        W, H = int(cv["width"]), int(cv["height"])
        pad, base = 10, H - 16
        vmax = max(hours.values()) if hours else 1
        bw = (W - 2 * pad) / 24
        for h in range(24):
            v = hours.get(h, 0)
            x0 = pad + h * bw + bw * 0.2
            x1 = pad + (h + 1) * bw - bw * 0.2
            if v:
                bh = (base - 12) * v / vmax
                cv.create_rectangle(x0, base - bh, x1, base,
                                    fill="#E8A87C", width=0)
            if h % 3 == 0:
                cv.create_text((x0 + x1) / 2, H - 7, text=str(h),
                               font=("微软雅黑", 8), fill=self.SUB)

    # ---- 主循环 ----
    def poll_queue(self):
        try:
            while True:
                msg = self.ui_queue.get_nowait()
                if msg == "panel":
                    self.show_panel()
                elif msg == "quit":
                    self.shutdown()
                    return
        except queue.Empty:
            pass
        self.root.after(200, self.poll_queue)

    def shutdown(self):
        try:
            self.counter.flush()
            self.reader.poll()
            self.counter.stop()
            self.tray.stop()
        finally:
            self.root.destroy()

    def run(self):
        self.counter.start()
        threading.Thread(target=self.flusher, daemon=True).start()
        threading.Thread(target=self.word_scheduler, daemon=True).start()
        threading.Thread(target=maybe_weekly_report,
                         args=(self.store,), daemon=True).start()
        self.build_tray()
        self.root.after(200, self.poll_queue)
        self.root.mainloop()


def migrate_db():
    """首次运行：把旧的 stats-app\\stats.db 迁移到统一位置 %APPDATA%\\IMEStats，
    让 C# 版与 Python 版共用同一份历史数据。"""
    try:
        os.makedirs(DATA_DIR, exist_ok=True)
        if os.path.exists(DB_PATH):
            return
        import shutil
        for cand in (os.path.join(APP_DIR, "stats.db"),
                     os.path.join(APP_DIR, "..", "stats-app", "stats.db")):
            if os.path.exists(cand):
                try:
                    c = sqlite3.connect(cand)
                    c.execute("PRAGMA wal_checkpoint(TRUNCATE)")
                    c.close()
                except Exception:
                    pass
                shutil.copy(cand, DB_PATH)
                break
    except Exception:
        pass


def ensure_single_instance():
    """互斥锁防双开（双开会导致两份钩子重复计数）"""
    kernel32 = ctypes.windll.kernel32
    kernel32.CreateMutexW(None, False, "IMEStats_SingleInstance_Mutex")
    if kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
        sys.exit(0)


if __name__ == "__main__":
    if "--word-worker" in sys.argv:     # 单文件 exe 复用自身跑分词
        import word_worker
        word_worker.main()
        sys.exit(0)
    ensure_single_instance()
    migrate_db()
    App().run()
# v1.2
