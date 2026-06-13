# -*- coding: utf-8 -*-
"""
词频批处理 Worker —— 独立进程，跑完即退
========================================
被 ime_stats.py 定时（每 6 小时）或托盘手动拉起。独立进程的意义：
jieba 词典加载后约占 60MB+ 内存，放在常驻托盘里太重；放这里用完就释放。

打包为单文件 exe 后以 `IMEStats.exe --word-worker` 方式复用同一个可执行文件。

做两件事：
1. 增量读取 Rime 上屏日志（word_log_offset 之后的部分），jieba 分词
   更新 word_freq 表（≥2 字纯汉字词）
2. 把 count≥3 的 Top 200 高频词用 pypinyin 注音，写入
   %APPDATA%\\Rime\\custom_phrase.txt 的自动区块 —— 你的高频词在
   输入法里权重更高（下次「重新部署」后生效；每周词库更新会自动部署）

区块外的内容（你手动加的短语）原样保留。
"""

import os
import sys
import sqlite3

FROZEN = getattr(sys, "frozen", False)
APP_DIR = (os.path.dirname(sys.executable) if FROZEN
           else os.path.dirname(os.path.abspath(__file__)))
DATA_DIR = os.path.join(os.environ.get("APPDATA", APP_DIR), "IMEStats")
DB_PATH = os.path.join(DATA_DIR, "stats.db")
RIME_USER_DIR = os.path.join(os.environ.get("APPDATA", ""), "Rime")
COMMIT_LOG = os.path.join(RIME_USER_DIR, "commit_log.txt")
PHRASE_PATH = os.path.join(RIME_USER_DIR, "custom_phrase.txt")

BEGIN_MARK = "# === IME-Stats 高频词自动区块 开始（手动短语请写在区块外）==="
END_MARK = "# === IME-Stats 高频词自动区块 结束 ==="


def is_cjk(ch):
    cp = ord(ch)
    return (0x4E00 <= cp <= 0x9FFF or 0x3400 <= cp <= 0x4DBF
            or 0x20000 <= cp <= 0x2A6DF or 0xF900 <= cp <= 0xFAFF)


def update_word_freq(conn, jieba):
    cur = conn.execute("SELECT v FROM meta WHERE k='word_seg_ver'").fetchone()
    ver = cur[0] if cur else "1"
    cur = conn.execute("SELECT v FROM meta WHERE k='word_log_offset'").fetchone()
    offset = int(cur[0]) if cur else -1
    if ver != "3" or offset < 0:
        # 算法升级或首次运行：清空重建（v0.7 之前的口径不同）
        conn.execute("DELETE FROM word_freq")
        offset = 0
    words = {}
    if os.path.exists(COMMIT_LOG):
        size = os.path.getsize(COMMIT_LOG)
        if size < offset:               # 日志被轮转
            offset = 0
        if size > offset:
            with open(COMMIT_LOG, "r", encoding="utf-8",
                      errors="ignore") as f:
                f.seek(offset)
                for line in f:
                    parts = line.rstrip("\n").split("\t", 1)
                    if len(parts) != 2:
                        continue
                    for w in jieba.lcut(parts[1]):
                        if len(w) >= 2 and all(is_cjk(c) for c in w):
                            words[w] = words.get(w, 0) + 1
                offset = f.tell()
    if words:
        conn.executemany(
            """INSERT INTO word_freq(word, count) VALUES(?,?)
               ON CONFLICT(word) DO UPDATE SET count=count+excluded.count""",
            list(words.items()))
    for k, v in (("word_seg_ver", "3"), ("word_log_offset", str(offset))):
        conn.execute("INSERT INTO meta(k,v) VALUES(?,?) "
                     "ON CONFLICT(k) DO UPDATE SET v=excluded.v", (k, v))
    conn.commit()


def update_custom_phrase(top):
    """把高频词写入 custom_phrase.txt 的自动区块，保留区块外手动内容"""
    try:
        from pypinyin import lazy_pinyin
    except ImportError:
        return
    if not top:
        return
    keep, inside = [], False
    if os.path.exists(PHRASE_PATH):
        with open(PHRASE_PATH, "r", encoding="utf-8", errors="ignore") as f:
            for raw in f:
                line = raw.rstrip("\n")
                if line.strip() == BEGIN_MARK:
                    inside = True
                    continue
                if line.strip() == END_MARK:
                    inside = False
                    continue
                if not inside:
                    keep.append(line)
    while keep and not keep[-1].strip():
        keep.pop()
    block = [BEGIN_MARK]
    for w, c in top:
        code = "".join(lazy_pinyin(w))
        if code.isascii() and code.isalpha():
            block.append(f"{w}\t{code}\t{min(c, 1000)}")
    block.append(END_MARK)
    lines = keep + ["", ""] + block if keep else block
    with open(PHRASE_PATH, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def migrate_db():
    """与 ime_stats.py 一致：首次把旧库迁移到 %APPDATA%\\IMEStats"""
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


def main():
    try:
        import jieba
        jieba.setLogLevel(60)
    except ImportError:
        return                          # 没装 jieba 就什么都不做
    migrate_db()
    conn = sqlite3.connect(DB_PATH, timeout=15)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("""CREATE TABLE IF NOT EXISTS word_freq(
        word TEXT PRIMARY KEY, count INTEGER DEFAULT 0)""")
    conn.execute("""CREATE TABLE IF NOT EXISTS meta(
        k TEXT PRIMARY KEY, v TEXT)""")
    conn.commit()
    update_word_freq(conn, jieba)
    top = conn.execute(
        "SELECT word,count FROM word_freq WHERE count>=3 "
        "ORDER BY count DESC LIMIT 200").fetchall()
    conn.close()
    try:
        update_custom_phrase(top)
    except Exception:
        pass


if __name__ == "__main__":
    main()
