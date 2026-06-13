// IME Stats C# 版 v1.0（功能对齐 Python 版 v0.9）
// =================================================
// 与 Python 版共享：stats.db（WAL）、commit_log.txt、单实例互斥锁
// （互斥锁同名 → 两个版本不会同时运行，不会重复计数）
// 分词/喂词仍复用 stats-app\IMEStats.exe --word-worker（或 pythonw word_worker.py）
// 编译：双击 scripts\build_csharp.bat（需 .NET 8 SDK）

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace IMEStatsSharp
{
    // ---------------- Win32 ----------------
    internal static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(string name);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        public const uint WM_IME_CONTROL = 0x0283, IMC_GETCONVERSIONMODE = 1, SMTO_ABORTIFHUNG = 2;
        public const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        // Ctrl/Alt/Win 任一按下 → 当前按键是快捷键组合，不算正常输入的字母
        public static bool ModifierDown() =>
            GetAsyncKeyState(VK_CONTROL) < 0 || GetAsyncKeyState(VK_MENU) < 0 ||
            GetAsyncKeyState(VK_LWIN) < 0 || GetAsyncKeyState(VK_RWIN) < 0;

        public static bool? ForegroundImeIsChinese()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;
                IntPtr ime = ImmGetDefaultIMEWnd(hwnd);
                if (ime == IntPtr.Zero) return null;
                if (SendMessageTimeoutW(ime, WM_IME_CONTROL, (UIntPtr)IMC_GETCONVERSIONMODE,
                        IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out UIntPtr r) == IntPtr.Zero)
                    return null;
                return ((ulong)r & 1) != 0;
            }
            catch { return null; }
        }

        public static string ForegroundProcessName()
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                using (var p = Process.GetProcessById((int)pid))
                    return (p.ProcessName + ".exe").ToLowerInvariant();
            }
            catch { return ""; }
        }
    }

    // ---------------- 数据层 ----------------
    internal sealed class Store : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly object _lock = new object();

        public Store(string dbPath)
        {
            _conn = new SqliteConnection("Data Source=" + dbPath);
            _conn.Open();
            Exec("PRAGMA journal_mode=WAL");
            Exec(@"CREATE TABLE IF NOT EXISTS daily(date TEXT PRIMARY KEY, keys INTEGER DEFAULT 0,
                   cn_chars INTEGER DEFAULT 0, en_chars INTEGER DEFAULT 0, en_words INTEGER DEFAULT 0)");
            Exec(@"CREATE TABLE IF NOT EXISTS hourly(date TEXT, hour INTEGER, keys INTEGER DEFAULT 0,
                   cn_chars INTEGER DEFAULT 0, PRIMARY KEY(date, hour))");
            Exec(@"CREATE TABLE IF NOT EXISTS word_freq(word TEXT PRIMARY KEY, count INTEGER DEFAULT 0)");
            Exec(@"CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT)");
        }

        private void Exec(string sql, params (string, object)[] args)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
                cmd.ExecuteNonQuery();
            }
        }

        private List<object[]> Query(string sql, params (string, object)[] args)
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
                var rows = new List<object[]>();
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var row = new object[rd.FieldCount];
                    rd.GetValues(row);
                    rows.Add(row);
                }
                return rows;
            }
        }

        public void Add(string date, long keys = 0, long cn = 0, long ec = 0, long ew = 0) =>
            Exec(@"INSERT INTO daily(date,keys,cn_chars,en_chars,en_words) VALUES($d,$k,$c,$e,$w)
                   ON CONFLICT(date) DO UPDATE SET keys=keys+excluded.keys,
                   cn_chars=cn_chars+excluded.cn_chars, en_chars=en_chars+excluded.en_chars,
                   en_words=en_words+excluded.en_words",
                ("$d", date), ("$k", keys), ("$c", cn), ("$e", ec), ("$w", ew));

        public void AddHourly(string date, int hour, long keys = 0, long cn = 0) =>
            Exec(@"INSERT INTO hourly(date,hour,keys,cn_chars) VALUES($d,$h,$k,$c)
                   ON CONFLICT(date,hour) DO UPDATE SET keys=keys+excluded.keys,
                   cn_chars=cn_chars+excluded.cn_chars",
                ("$d", date), ("$h", hour), ("$k", keys), ("$c", cn));

        public string GetMeta(string k, string def)
        {
            var r = Query("SELECT v FROM meta WHERE k=$k", ("$k", k));
            return r.Count > 0 ? Convert.ToString(r[0][0]) : def;
        }

        public void SetMeta(string k, string v) =>
            Exec("INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=excluded.v",
                ("$k", k), ("$v", v));

        public long[] Today()
        {
            var r = Query("SELECT keys,cn_chars,en_chars,en_words FROM daily WHERE date=$d",
                ("$d", DateTime.Today.ToString("yyyy-MM-dd")));
            return r.Count > 0 ? r[0].Select(Convert.ToInt64).ToArray() : new long[4];
        }

        public long[] Total()
        {
            var r = Query(@"SELECT COALESCE(SUM(keys),0),COALESCE(SUM(cn_chars),0),
                            COALESCE(SUM(en_chars),0),COALESCE(SUM(en_words),0) FROM daily");
            return r[0].Select(Convert.ToInt64).ToArray();
        }

        public List<(string date, long keys, long cn, long ew)> LastN(int n)
        {
            var r = Query("SELECT date,keys,cn_chars,en_words FROM daily ORDER BY date DESC LIMIT $n", ("$n", n));
            r.Reverse();
            return r.Select(x => (Convert.ToString(x[0]), Convert.ToInt64(x[1]),
                                  Convert.ToInt64(x[2]), Convert.ToInt64(x[3]))).ToList();
        }

        public Dictionary<int, long> TodayHours()
        {
            var r = Query("SELECT hour,keys FROM hourly WHERE date=$d",
                ("$d", DateTime.Today.ToString("yyyy-MM-dd")));
            return r.ToDictionary(x => Convert.ToInt32(x[0]), x => Convert.ToInt64(x[1]));
        }

        public List<(string w, long c)> TopWords(int n)
        {
            var r = Query("SELECT word,count FROM word_freq ORDER BY count DESC LIMIT $n", ("$n", n));
            return r.Select(x => (Convert.ToString(x[0]), Convert.ToInt64(x[1]))).ToList();
        }

        public List<(string date, long keys, long cn, long ew)> RangeDays(string d1, string d2)
        {
            var r = Query("SELECT date,keys,cn_chars,en_words FROM daily WHERE date BETWEEN $a AND $b ORDER BY date",
                ("$a", d1), ("$b", d2));
            return r.Select(x => (Convert.ToString(x[0]), Convert.ToInt64(x[1]),
                                  Convert.ToInt64(x[2]), Convert.ToInt64(x[3]))).ToList();
        }

        public void Dispose() => _conn.Dispose();
    }

    // ---------------- 按键统计（钩子回调只入队，逻辑在工作线程） ----------------
    internal sealed class KeyCounter
    {
        private readonly Store _store;
        private readonly HashSet<string> _skip;
        private IntPtr _hook = IntPtr.Zero;
        private Native.HookProc _proc;          // 防 GC
        private readonly BlockingCollection<(int vk, bool mod)> _events =
            new BlockingCollection<(int, bool)>();
        public volatile bool Paused;

        private readonly object _lock = new object();
        public long Keys, EnChars, EnWords;
        private long _letterRun;
        private readonly Dictionary<(string, int), long> _hourAcc = new Dictionary<(string, int), long>();

        private bool? _modeCache; private long _modeTick;
        private string _procCache = ""; private long _procTick;

        public KeyCounter(Store store, HashSet<string> skip)
        {
            _store = store; _skip = skip;
        }

        public void Start()
        {
            _proc = HookCallback;
            _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc,
                Native.GetModuleHandleW(null), 0);
            new Thread(Work) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !Paused &&
                ((long)wParam == Native.WM_KEYDOWN || (long)wParam == Native.WM_SYSKEYDOWN))
            {
                int vk = Marshal.ReadInt32(lParam);     // KBDLLHOOKSTRUCT.vkCode
                // 修饰键状态必须在回调内取（入队后再查就不是按键当时的状态了）
                _events.TryAdd((vk, Native.ModifierDown()));
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private bool? CachedImeChinese()
        {
            long now = Environment.TickCount64;
            if (now - _modeTick > 200) { _modeCache = Native.ForegroundImeIsChinese(); _modeTick = now; }
            return _modeCache;
        }

        private string CachedProc()
        {
            long now = Environment.TickCount64;
            if (now - _procTick > 1000) { _procCache = Native.ForegroundProcessName(); _procTick = now; }
            return _procCache;
        }

        private void Work()
        {
            foreach (var (vk, mod) in _events.GetConsumingEnumerable())
            {
                try
                {
                    if (_skip.Contains(CachedProc())) continue;
                    var now = DateTime.Now;
                    var hkey = (now.ToString("yyyy-MM-dd"), now.Hour);
                    lock (_lock)
                    {
                        Keys++;
                        _hourAcc[hkey] = _hourAcc.TryGetValue(hkey, out long v) ? v + 1 : 1;
                        // Ctrl+C 等快捷键不算英文输入（与 Python 版口径一致）
                        bool isLetter = !mod && vk >= 0x41 && vk <= 0x5A;    // A-Z
                        if (isLetter)
                        {
                            if (CachedImeChinese() != true) { EnChars++; _letterRun++; }
                        }
                        else if (_letterRun > 0) { EnWords++; _letterRun = 0; }
                    }
                }
                catch { }
            }
        }

        public void Flush()
        {
            long k, ec, ew; Dictionary<(string, int), long> hours;
            lock (_lock)
            {
                k = Keys; ec = EnChars; ew = EnWords;
                hours = new Dictionary<(string, int), long>(_hourAcc);
                Keys = EnChars = EnWords = 0; _hourAcc.Clear();
            }
            if (k > 0 || ec > 0 || ew > 0)
                _store.Add(DateTime.Today.ToString("yyyy-MM-dd"), k, 0, ec, ew);
            foreach (var kv in hours) _store.AddHourly(kv.Key.Item1, kv.Key.Item2, kv.Value, 0);
        }

        public (long k, long ec, long ew) Snapshot()
        {
            lock (_lock) return (Keys, EnChars, EnWords);
        }
    }

    // ---------------- Rime 上屏日志读取 ----------------
    internal sealed class CommitLogReader
    {
        private readonly Store _store;
        public long Offset;
        public readonly object Lock = new object();

        public CommitLogReader(Store store)
        {
            _store = store;
            Offset = long.Parse(store.GetMeta("commit_log_offset", "0"));
        }

        // 按 Unicode 码点统计 CJK 字数（含扩展 B 区生僻字，与 Python 版口径一致）
        public static long CountCjk(string s)
        {
            long n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int cp = s[i];
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    cp = char.ConvertToUtf32(s[i], s[++i]);
                if ((cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) ||
                    (cp >= 0x20000 && cp <= 0x2A6DF) || (cp >= 0xF900 && cp <= 0xFAFF)) n++;
            }
            return n;
        }

        public void Poll()
        {
            lock (Lock)
            {
                try { PollInner(); } catch { }
            }
        }

        // 调用方必须已持有 Lock（轮转归档前在锁内追平用）
        public void PollNoLock()
        {
            try { PollInner(); } catch { }
        }

        private void PollInner()
        {
            string path = Program.CommitLog;
            if (!File.Exists(path)) return;
            byte[] buf;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long size = fs.Length;
                if (size < Offset) Offset = 0;      // 日志被清空/轮转
                if (size == Offset) return;
                fs.Seek(Offset, SeekOrigin.Begin);
                buf = new byte[size - Offset];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < buf.Length) Array.Resize(ref buf, read);
            }
            // 只消费到最后一个换行符为止：Rime 可能正写到半行，
            // 留给下次读，offset 按消费的字节数推进（绝不跳过内容）
            int end = Array.LastIndexOf(buf, (byte)'\n');
            if (end < 0) return;

            var dayCn = new Dictionary<string, long>();
            var hourCn = new Dictionary<(string, int), long>();
            string chunk = Encoding.UTF8.GetString(buf, 0, end + 1);
            foreach (string raw in chunk.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                string ts = line.Substring(0, tab), text = line.Substring(tab + 1);
                long cn = CountCjk(text);
                if (cn > 0)
                {
                    string d = ts.Length >= 10 ? ts.Substring(0, 10) : DateTime.Today.ToString("yyyy-MM-dd");
                    int h = 0;
                    if (ts.Length >= 13) int.TryParse(ts.Substring(11, 2), out h);
                    dayCn[d] = dayCn.TryGetValue(d, out long v) ? v + cn : cn;
                    var hk = (d, h);
                    hourCn[hk] = hourCn.TryGetValue(hk, out long v2) ? v2 + cn : cn;
                }
            }
            Offset += end + 1;
            foreach (var kv in dayCn) _store.Add(kv.Key, 0, kv.Value, 0, 0);
            foreach (var kv in hourCn) _store.AddHourly(kv.Key.Item1, kv.Key.Item2, 0, kv.Value);
            _store.SetMeta("commit_log_offset", Offset.ToString());
        }
    }

    // ---------------- 统计面板（单 Form 自绘） ----------------
    internal sealed class PanelForm : Form
    {
        private readonly Store _store;
        private readonly KeyCounter _counter;
        private readonly CommitLogReader _reader;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();

        private long[] _today = new long[4], _total = new long[4];
        private (long, long, long) _live;
        private Dictionary<int, long> _hours = new Dictionary<int, long>();
        private List<(string, long, long, long)> _days = new List<(string, long, long, long)>();
        private List<(string, long)> _words = new List<(string, long)>();

        private static readonly Color BG = ColorTranslator.FromHtml("#FAFAFA");
        private static readonly Color CARD = Color.White;
        private static readonly Color FG = ColorTranslator.FromHtml("#3A3A3A");
        private static readonly Color SUB = ColorTranslator.FromHtml("#9B9B9B");
        private static readonly Color ACCENT = ColorTranslator.FromHtml("#3347D9");
        private static readonly Color BAR1 = ColorTranslator.FromHtml("#A9B8D6");
        private static readonly Color BAR2 = ColorTranslator.FromHtml("#7B8AE0");
        private static readonly Color BAR3 = ColorTranslator.FromHtml("#E8A87C");

        public PanelForm(Store s, KeyCounter c, CommitLogReader r)
        {
            _store = s; _counter = c; _reader = r;
            Text = "输入统计";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; TopMost = true;
            ClientSize = new Size(620, 640);
            BackColor = BG;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            _timer.Interval = 1000;
            _timer.Tick += (o, e) => { Refetch(); Invalidate(); };
            _timer.Start();
            Refetch();
        }

        private void Refetch()
        {
            try
            {
                _reader.Poll();
                _live = _counter.Snapshot();
                _today = _store.Today(); _total = _store.Total();
                _hours = _store.TodayHours();
                _days = _store.LastN(14);
                _words = _store.TopWords(10);
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop(); base.OnFormClosed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fTitle = new Font("微软雅黑", 12, FontStyle.Bold);
            using var fBig = new Font("微软雅黑", 18, FontStyle.Bold);
            using var fSm = new Font("微软雅黑", 9);
            using var fTiny = new Font("微软雅黑", 7);
            using var bFg = new SolidBrush(FG);
            using var bSub = new SolidBrush(SUB);
            using var bCard = new SolidBrush(CARD);

            g.DrawString("输入统计 (C#)", fTitle, bFg, 20, 14);
            g.DrawString(DateTime.Today.ToString("yyyy-MM-dd"), fSm, bSub, 520, 20);

            // 卡片
            string[] names = { "按键次数", "中文字数", "英文字符", "英文单词" };
            long[] todayV = { _today[0] + _live.Item1, _today[1], _today[2] + _live.Item2, _today[3] + _live.Item3 };
            long[] totalV = { _total[0] + _live.Item1, _total[1], _total[2] + _live.Item2, _total[3] + _live.Item3 };
            for (int i = 0; i < 4; i++)
            {
                var rc = new Rectangle(20 + i * 148, 50, 136, 86);
                g.FillRectangle(bCard, rc);
                using var bV = new SolidBrush(i == 1 ? ACCENT : FG);
                DrawCentered(g, todayV[i].ToString("N0"), fBig, bV, rc.X, rc.Y + 8, rc.Width);
                DrawCentered(g, names[i], fSm, bSub, rc.X, rc.Y + 44, rc.Width);
                DrawCentered(g, "累计 " + totalV[i].ToString("N0"), fTiny, bSub, rc.X, rc.Y + 64, rc.Width);
            }

            int y = 150;
            g.DrawString("今日 · 时段分布（按键）", fSm, bSub, 20, y);
            DrawHours(g, new Rectangle(20, y + 20, 580, 80), fTiny, bSub);
            y += 114;
            g.DrawString("最近 14 天 · 按键次数", fSm, bSub, 20, y);
            DrawDays(g, new Rectangle(20, y + 20, 580, 96), 1, BAR1, fTiny, bSub);
            y += 130;
            g.DrawString("最近 14 天 · 中文字数", fSm, bSub, 20, y);
            DrawDays(g, new Rectangle(20, y + 20, 580, 96), 2, BAR2, fTiny, bSub);
            y += 130;
            g.DrawString("常用词 Top 10", fSm, bSub, 20, y);
            var rcW = new Rectangle(20, y + 20, 580, 56);
            g.FillRectangle(bCard, rcW);
            string wtext = _words.Count == 0 ? "还没有数据，去打几个字吧"
                : string.Join("　", _words.Select((w, i) => $"{i + 1}.{w.Item1} ×{w.Item2}"));
            using (var fW = new Font("微软雅黑", 9.5f))
                g.DrawString(wtext, fW, bFg, new RectangleF(rcW.X + 10, rcW.Y + 8, rcW.Width - 20, rcW.Height - 12));
        }

        private static void DrawCentered(Graphics g, string s, Font f, Brush b, int x, int y, int w)
        {
            var sz = g.MeasureString(s, f);
            g.DrawString(s, f, b, x + (w - sz.Width) / 2, y);
        }

        private void DrawHours(Graphics g, Rectangle rc, Font fTiny, Brush bSub)
        {
            g.FillRectangle(Brushes.White, rc);
            long vmax = _hours.Count > 0 ? Math.Max(1, _hours.Values.Max()) : 1;
            float bw = (rc.Width - 16) / 24f;
            using var bar = new SolidBrush(BAR3);
            for (int h = 0; h < 24; h++)
            {
                _hours.TryGetValue(h, out long v);
                float x0 = rc.X + 8 + h * bw + bw * 0.2f;
                float wRect = bw * 0.6f;
                if (v > 0)
                {
                    float bh = (rc.Height - 26) * v / (float)vmax;
                    g.FillRectangle(bar, x0, rc.Bottom - 14 - bh, wRect, bh);
                }
                if (h % 3 == 0)
                    g.DrawString(h.ToString(), fTiny, bSub, x0 - 2, rc.Bottom - 12);
            }
        }

        private void DrawDays(Graphics g, Rectangle rc, int idx, Color color, Font fTiny, Brush bSub)
        {
            g.FillRectangle(Brushes.White, rc);
            if (_days.Count == 0) return;
            Func<(string, long, long, long), long> sel = d => idx == 1 ? d.Item2 : d.Item3;
            long lk = _live.Item1;
            long vmax = 1;
            for (int i = 0; i < _days.Count; i++)
            {
                long v = sel(_days[i]);
                if (idx == 1 && i == _days.Count - 1 &&
                    _days[i].Item1 == DateTime.Today.ToString("yyyy-MM-dd")) v += lk;
                vmax = Math.Max(vmax, v);
            }
            float bw = (rc.Width - 16) / (float)_days.Count;
            using var bar = new SolidBrush(color);
            for (int i = 0; i < _days.Count; i++)
            {
                long v = sel(_days[i]);
                if (idx == 1 && i == _days.Count - 1 &&
                    _days[i].Item1 == DateTime.Today.ToString("yyyy-MM-dd")) v += lk;
                float x0 = rc.X + 8 + i * bw + bw * 0.18f;
                float wRect = bw * 0.64f;
                if (v > 0)
                {
                    float bh = (rc.Height - 34) * v / (float)vmax;
                    g.FillRectangle(bar, x0, rc.Bottom - 16 - bh, wRect, bh);
                    string label = v >= 10000 ? (v / 1000.0).ToString("0.0") + "k" : v.ToString("N0");
                    g.DrawString(label, fTiny, bSub, x0 - 4, rc.Bottom - 28 - bh);
                }
                g.DrawString(_days[i].Item1.Substring(5), fTiny, bSub, x0 - 4, rc.Bottom - 13);
            }
        }
    }

    // ---------------- 主程序 ----------------
    internal static class Program
    {
        public static readonly string AppDir = AppContext.BaseDirectory.TrimEnd('\\');
        // 数据库固定在 %APPDATA%\IMEStats\stats.db（与 exe 位置无关，C#/Python 共用）
        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IMEStats");
        public static readonly string DbPath = Path.Combine(DataDir, "stats.db");
        public static readonly string RimeDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rime");
        public static readonly string CommitLog = Path.Combine(RimeDir, "commit_log.txt");
        public static readonly string ReportsDir =
            Path.Combine(Path.GetDirectoryName(AppDir) ?? AppDir, "docs", "reports");
        private const long RotateBytes = 2 * 1024 * 1024;

        private static Store _store;
        private static KeyCounter _counter;
        private static CommitLogReader _reader;
        private static NotifyIcon _tray;
        private static PanelForm _panel;

        public static readonly string CrashLog = Path.Combine(DataDir, "crash.log");

        // 全局兜底：WinExe 无控制台，任何未捕获异常都写进 crash.log 便于排查
        // （C# 单文件缺原生库那次就是静默崩溃，有此日志可一眼定位）
        public static void Log(string where, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(CrashLog,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}\n\n");
            }
            catch { }
        }

        [STAThread]
        private static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log("UnhandledException", e.ExceptionObject as Exception ?? new Exception("非异常对象: " + e.ExceptionObject));
            Application.ThreadException += (s, e) => Log("ThreadException", e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            using var mutex = new Mutex(false, "IMEStats_SingleInstance_Mutex", out bool createdNew);
            if (!createdNew)                // 已有实例（含 Python 版）在跑
            {
                MessageBox.Show(
                    "输入统计已经在运行了。\n\n很可能是另一个版本（Python 版）还在任务栏托盘里。\n" +
                    "两个版本共用同一份数据，不能同时开。\n请先右键那个托盘图标 → 退出，再启动本程序。",
                    "IME Stats（C# 版）", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();

            int flushSec;
            try
            {
                EnsureDatabase();           // 首次运行迁移旧数据库到统一位置
                _store = new Store(DbPath);
                HashSet<string> skip;
                (skip, flushSec) = LoadConfig();    // config.json 与 Python 版共用
                _counter = new KeyCounter(_store, skip);
                _reader = new CommitLogReader(_store);
                _counter.Start();
            }
            catch (Exception ex)
            {
                // 启动失败（如缺原生库、库被占用）：写日志 + 明确告知，而非静默退出
                Log("Startup", ex);
                MessageBox.Show(
                    "统计程序启动失败：\n" + ex.Message +
                    "\n\n详情见 " + CrashLog,
                    "IME Stats（C# 版）", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 落盘线程：每 flushSec 落盘；约每小时检查日志轮转 + 周报
            // （周报放循环里：长期不重启也能在跨周后正常生成，meta 标记防重复）
            new Thread(() =>
            {
                int loops = 0, hourly = Math.Max(1, 3600 / flushSec);
                WeeklyReport();
                while (true)
                {
                    Thread.Sleep(flushSec * 1000);
                    try
                    {
                        _counter.Flush(); _reader.Poll();
                        if (++loops % hourly == 0) { MaybeRotate(); WeeklyReport(); }
                    }
                    catch { }
                }
            }) { IsBackground = true }.Start();

            // 词频 worker：每 6 小时
            new Thread(() =>
            {
                while (true) { RunWordWorker(); Thread.Sleep(6 * 3600 * 1000); }
            }) { IsBackground = true }.Start();

            // 托盘
            _tray = new NotifyIcon
            {
                Icon = MakeIcon(false),
                Text = "输入统计 (C#)",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            _tray.ContextMenuStrip.Items.Add("统计面板", null, (o, e) => ShowPanel());
            _tray.ContextMenuStrip.Items.Add("立即更新词频/喂词", null, (o, e) => RunWordWorker());
            var pause = new ToolStripMenuItem("暂停统计");
            pause.Click += (o, e) =>
            {
                _counter.Paused = !_counter.Paused;
                pause.Checked = _counter.Paused;
                var old = _tray.Icon;
                _tray.Icon = MakeIcon(_counter.Paused);
                old?.Dispose();         // 释放被换下的图标句柄
            };
            _tray.ContextMenuStrip.Items.Add(pause);
            _tray.ContextMenuStrip.Items.Add("退出", null, (o, e) =>
            {
                _counter.Flush(); _reader.Poll();
                _counter.Stop(); _tray.Visible = false;
                Application.Exit();
            });
            _tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };
            _tray.DoubleClick += (o, e) => ShowPanel();
            ShowPanel();    // 启动即弹一次面板

            Application.Run();
        }

        private static void ShowPanel()
        {
            if (_panel != null && !_panel.IsDisposed) { _panel.Activate(); return; }
            _panel = new PanelForm(_store, _counter, _reader);
            _panel.Show();
        }

        private static Icon MakeIcon(bool paused)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var b = new SolidBrush(paused ? Color.Gray : Color.DodgerBlue);
                g.FillRectangle(b, 2, 2, 28, 28);
                using var f = new Font("微软雅黑", 16, FontStyle.Bold);
                g.DrawString("字", f, Brushes.White, 2, 2);
            }
            // GetHicon 的句柄 GDI 不会自动回收：Clone 出托管副本后立即 DestroyIcon
            IntPtr h = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(h);
                return (Icon)tmp.Clone();
            }
            finally { Native.DestroyIcon(h); }
        }

        // 读 stats-app\config.json（与 Python 版同一份）：skip_processes / flush_interval_sec
        private static (HashSet<string> skip, int flushSec) LoadConfig()
        {
            var skip = new HashSet<string>();
            int flushSec = 30;
            try
            {
                string p = Path.Combine(AppDir, "config.json");
                if (File.Exists(p))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                    if (doc.RootElement.TryGetProperty("skip_processes", out var sp))
                        foreach (var e in sp.EnumerateArray())
                            skip.Add((e.GetString() ?? "").ToLowerInvariant());
                    if (doc.RootElement.TryGetProperty("flush_interval_sec", out var fi))
                        flushSec = Math.Max(5, fi.GetInt32());
                }
            }
            catch { }
            return (skip, flushSec);
        }

        // 首次运行：把旧的 stats-app\stats.db 迁移到统一位置 %APPDATA%\IMEStats
        private static void EnsureDatabase()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if (File.Exists(DbPath)) return;
                string[] cands = {
                    Path.Combine(AppDir, "stats.db"),
                    Path.Combine(AppDir, "..", "stats-app", "stats.db"),
                    Path.Combine(AppDir, "stats-app", "stats.db"),
                };
                foreach (var c in cands)
                {
                    if (!File.Exists(c)) continue;
                    try
                    {
                        using var cc = new SqliteConnection("Data Source=" + c);
                        cc.Open();
                        using var cmd = cc.CreateCommand();
                        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                        cmd.ExecuteNonQuery();
                    }
                    catch { }
                    File.Copy(c, DbPath, true);
                    break;
                }
            }
            catch { }
        }

        // 分词 worker：优先打包版 IMEStats.exe --word-worker，其次 pythonw word_worker.py
        private static void RunWordWorker()
        {
            try
            {
                string exe = Path.Combine(AppDir, "IMEStats.exe");
                string py = new[] {
                    Path.Combine(AppDir, "word_worker.py"),
                    Path.Combine(AppDir, "..", "stats-app", "word_worker.py"),
                    Path.Combine(AppDir, "stats-app", "word_worker.py"),
                }.FirstOrDefault(File.Exists) ?? Path.Combine(AppDir, "word_worker.py");
                var psi = new ProcessStartInfo { CreateNoWindow = true, UseShellExecute = false };
                if (File.Exists(exe)) { psi.FileName = exe; psi.Arguments = "--word-worker"; }
                else if (File.Exists(py)) { psi.FileName = "pythonw"; psi.Arguments = "\"" + py + "\""; }
                else return;
                Process.Start(psi);
            }
            catch { }
        }

        private static void MaybeRotate()
        {
            try
            {
                if (!File.Exists(CommitLog)) return;
                if (new FileInfo(CommitLog).Length < RotateBytes) return;
                string arch = Path.Combine(RimeDir, "commit_log_archive");
                Directory.CreateDirectory(arch);
                string dst = Path.Combine(arch, "commit_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                // 追平字数 → 校验词频 → 改名归档全程持锁：
                // 锁内 Rime 新写入的行要么被追平统计到，要么留在新日志里，绝不丢
                lock (_reader.Lock)
                {
                    _reader.PollNoLock();
                    long size = new FileInfo(CommitLog).Length;
                    if (_reader.Offset < size) return;  // 还有半行没写完，下轮再说
                    if (long.Parse(_store.GetMeta("word_log_offset", "0")) < size)
                    {
                        RunWordWorker();                // 词频还没消化完，催一把，下轮再轮转
                        return;
                    }
                    File.Move(CommitLog, dst);
                    _reader.Offset = 0;
                    _store.SetMeta("commit_log_offset", "0");
                    _store.SetMeta("word_log_offset", "0");
                }
            }
            catch { }
        }

        private static void WeeklyReport()
        {
            try
            {
                var today = DateTime.Today;
                int wd = ((int)today.DayOfWeek + 6) % 7;            // 周一=0
                var lastMon = today.AddDays(-wd - 7);
                var lastSun = lastMon.AddDays(6);
                var cal = System.Globalization.ISOWeek.GetWeekOfYear(lastMon);
                string tag = $"{System.Globalization.ISOWeek.GetYear(lastMon)}-W{cal:D2}";
                if (_store.GetMeta("weekly_report_last", "") == tag) return;
                var rows = _store.RangeDays(lastMon.ToString("yyyy-MM-dd"), lastSun.ToString("yyyy-MM-dd"));
                _store.SetMeta("weekly_report_last", tag);
                if (rows.Count == 0) return;
                var prev = _store.RangeDays(lastMon.AddDays(-7).ToString("yyyy-MM-dd"),
                                            lastMon.AddDays(-1).ToString("yyyy-MM-dd"));
                long tk = rows.Sum(r => r.keys), tcn = rows.Sum(r => r.cn), tew = rows.Sum(r => r.ew);
                long pk = prev.Sum(r => r.keys);
                string pct = pk == 0 ? "—" : ((tk - pk) * 100.0 / pk).ToString("+0;-0") + "%";
                var sb = new StringBuilder();
                sb.AppendLine($"# 输入周报 {tag}（{lastMon:yyyy-MM-dd} ~ {lastSun:yyyy-MM-dd}）");
                sb.AppendLine();
                sb.AppendLine($"按键 **{tk:N0}** 次（环比上周 {pct}）；中文 **{tcn:N0}** 字；英文 **{tew:N0}** 词。");
                sb.AppendLine();
                sb.AppendLine("| 日期 | 按键 | 中文字 | 英文词 |");
                sb.AppendLine("|------|------|--------|--------|");
                foreach (var r in rows)
                    sb.AppendLine($"| {r.date} | {r.keys:N0} | {r.cn:N0} | {r.ew:N0} |");
                var top = _store.TopWords(10);
                if (top.Count > 0)
                    sb.AppendLine().AppendLine("本周为止的常用词：" +
                        string.Join("、", top.Select(t => $"{t.w}({t.c})")));
                Directory.CreateDirectory(ReportsDir);
                File.WriteAllText(Path.Combine(ReportsDir, $"输入周报-{tag}.md"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
