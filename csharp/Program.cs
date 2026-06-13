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
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;   // Win11：窗口圆角偏好

        // 让无边框窗口可拖动：在任意客户区按下时模拟拖标题栏
        public static void DragWindow(IntPtr hwnd)
        {
            ReleaseCapture();
            SendMessageW(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        // Win11 圆角（旧系统自动忽略）
        public static void RoundCorners(IntPtr hwnd)
        {
            try { int v = 2 /*DWMWCP_ROUND*/; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4); }
            catch { }
        }

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

    // ---------------- 图标工厂（多分辨率 ICO，托盘/任务栏/Alt-Tab 都清晰） ----------------
    internal static class IconFactory
    {
        // 蓝色渐变圆角方块 + 白色"字"，paused 时转灰
        public static Bitmap Render(int s, bool paused)
        {
            var bmp = new Bitmap(s, s);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float m = s * 0.085f, r = s * 0.30f;             // 更柔的圆角方块
            var rect = new RectangleF(m, m, s - 2 * m, s - 2 * m);
            using var path = Round(rect, r);
            Color top = paused ? ColorTranslator.FromHtml("#BCC1CB") : ColorTranslator.FromHtml("#5B86FF");
            Color bot = paused ? ColorTranslator.FromHtml("#8B919D") : ColorTranslator.FromHtml("#2E45D4");
            using (var lg = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y - 1, rect.Width, rect.Height + 2), top, bot, 90f))
                g.FillPath(lg, path);
            // 顶部高光（柔和），增加立体感
            var glo = new RectangleF(m, m, s - 2 * m, (s - 2 * m) * 0.52f);
            using (var gp = Round(glo, r))
            using (var lg2 = new LinearGradientBrush(glo,
                Color.FromArgb(55, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                g.FillPath(lg2, gp);
            // "字"：路径墨迹居中，占方块约 56%
            using var fam = new FontFamily("微软雅黑");
            FillGlyphCentered(g, "字", fam, Brushes.White, rect, 0.56f);
            return bmp;
        }

        public static GraphicsPath Round(RectangleF rc, float r)
        {
            float d = r * 2;
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(rc); return p; }
            p.AddArc(rc.X, rc.Y, d, d, 180, 90);
            p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
            p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
            p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 把字形转成路径，按"墨迹包围盒"在 rect 内精确居中并缩放到占比 fill。
        // 比 DrawString 居中可靠：DrawString 用字体行高(含上下空白)定位，CJK/数字会偏。
        public static void FillGlyphCentered(Graphics g, string text, FontFamily fam,
                                             Brush brush, RectangleF rect, float fill)
        {
            using var path = new GraphicsPath();
            path.AddString(text, fam, (int)FontStyle.Bold, 100f, new PointF(0, 0),
                           StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            using (var m1 = new Matrix()) { m1.Translate(-b.X, -b.Y); path.Transform(m1); }   // 墨迹移到原点
            float target = Math.Min(rect.Width, rect.Height) * fill;
            float scale = target / Math.Max(b.Width, b.Height);
            float w = b.Width * scale, h = b.Height * scale;
            using (var m2 = new Matrix())
            {
                m2.Translate(rect.X + (rect.Width - w) / 2f, rect.Y + (rect.Height - h) / 2f);
                m2.Scale(scale, scale);
                path.Transform(m2);                                                          // 缩放后居中
            }
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
            g.SmoothingMode = old;
        }

        public static Icon Create(bool paused)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
            {
                using var bmp = Render(s, paused);
                using var ms0 = new MemoryStream();
                bmp.Save(ms0, System.Drawing.Imaging.ImageFormat.Png);
                pngs.Add(ms0.ToArray());
            }
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i];
                    bw.Write((byte)(s >= 256 ? 0 : s));
                    bw.Write((byte)(s >= 256 ? 0 : s));
                    bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((short)1); bw.Write((short)32);
                    bw.Write(pngs[i].Length);
                    bw.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) bw.Write(p);
                bw.Flush();
                var bytes = ms.ToArray();
                using var read = new MemoryStream(bytes);
                return new Icon(read);
            }
        }
    }

    // ---------------- 统计面板（无边框圆角自绘，Win11 风） ----------------
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

        private static readonly Color BG = ColorTranslator.FromHtml("#F4F5F7");
        private static readonly Color CARD = Color.White;
        private static readonly Color FG = ColorTranslator.FromHtml("#2B2F38");
        private static readonly Color SUB = ColorTranslator.FromHtml("#9AA0AB");
        private static readonly Color ACCENT = ColorTranslator.FromHtml("#4F6BFF");
        private static readonly Color ACCENT2 = ColorTranslator.FromHtml("#3344CC");
        // 三组柱状渐变（上浅下深）
        private static readonly Color BAR1A = ColorTranslator.FromHtml("#B7C2EC"), BAR1B = ColorTranslator.FromHtml("#8C9BE2");
        private static readonly Color BAR2A = ColorTranslator.FromHtml("#7E8CEC"), BAR2B = ColorTranslator.FromHtml("#4F5FD9");
        private static readonly Color BAR3A = ColorTranslator.FromHtml("#F6B98A"), BAR3B = ColorTranslator.FromHtml("#E8915A");

        private Rectangle _closeRect, _minRect, _pinRect;
        private bool _closeHover, _minHover, _pinHover;
        private static readonly int PAD = 24;
        private readonly Image _glyph = IconFactory.Render(30, false);   // 标题栏小图标

        public PanelForm(Store s, KeyCounter c, CommitLogReader r)
        {
            _store = s; _counter = c; _reader = r;
            Text = "输入统计";
            FormBorderStyle = FormBorderStyle.None;     // 无边框，自绘标题栏
            MaximizeBox = false; TopMost = true;
            ClientSize = new Size(660, 772);
            MinimumSize = new Size(660, 772);   // 可放大，不会缩到内容被裁
            ResizeRedraw = true;                // 拉伸时整窗重绘，避免残影
            BackColor = BG;
            DoubleBuffered = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = IconFactory.Create(false);           // Alt-Tab / 任务栏图标

            LayoutButtons();

            // Esc 关闭；标题区拖动；右上角 最小化 / × 关闭
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            MouseMove += (o, e) =>
            {
                bool ch = _closeRect.Contains(e.Location), mh = _minRect.Contains(e.Location),
                     ph = _pinRect.Contains(e.Location);
                if (ch != _closeHover || mh != _minHover || ph != _pinHover)
                { _closeHover = ch; _minHover = mh; _pinHover = ph; Invalidate(); }
            };
            MouseDown += (o, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (_closeRect.Contains(e.Location)) { Close(); return; }
                if (_minRect.Contains(e.Location)) { WindowState = FormWindowState.Minimized; return; }
                if (_pinRect.Contains(e.Location)) { TopMost = !TopMost; Invalidate(); return; }  // 置顶开关
                // 标题区拖动（避开 6px 缩放边缘和三个按钮）
                if (e.Y > 6 && e.Y < 60 && e.X > 6 && e.X < ClientSize.Width - 6)
                    Native.DragWindow(Handle);
            };

            _timer.Interval = 1000;
            _timer.Tick += (o, e) => { Refetch(); Invalidate(); };
            _timer.Start();
            Refetch();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);                // Win11 原生圆角 + 阴影
        }

        // 右上角按钮位置随宽度变化
        private void LayoutButtons()
        {
            int w = ClientSize.Width;
            _closeRect = new Rectangle(w - 42, 14, 28, 28);
            _minRect = new Rectangle(w - 76, 14, 28, 28);
            _pinRect = new Rectangle(w - 110, 14, 28, 28);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutButtons();
            Invalidate();
        }

        // 无边框窗口的边缘缩放：命中边/角时返回对应 HT 码，交给系统拖拽
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084, HTCLIENT = 1;
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT)
                {
                    int lp = m.LParam.ToInt32();
                    var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)(lp >> 16)));
                    int g = 6, w = ClientSize.Width, h = ClientSize.Height;
                    bool L = p.X <= g, R = p.X >= w - g, T = p.Y <= g, B = p.Y >= h - g;
                    int code =
                        T && L ? 13 : T && R ? 14 : B && L ? 16 : B && R ? 17 :
                        L ? 10 : R ? 11 : T ? 12 : B ? 15 : HTCLIENT;   // HTLEFT/RIGHT/TOP/BOTTOM/角
                    m.Result = (IntPtr)code;
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;            // CS_DROPSHADOW：柔和投影
                return cp;
            }
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
            _timer.Stop();
            Icon?.Dispose();
            _glyph?.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bgb = new SolidBrush(BG)) g.FillRectangle(bgb, ClientRectangle);

            using var fTitle = new Font("微软雅黑", 13, FontStyle.Bold);
            using var fBig = new Font("微软雅黑", 18.5f, FontStyle.Bold);
            using var fSm = new Font("微软雅黑", 9);
            using var fLbl = new Font("微软雅黑", 9.5f, FontStyle.Bold);
            using var fTiny = new Font("微软雅黑", 7.5f);
            using var bFg = new SolidBrush(FG);
            using var bSub = new SolidBrush(SUB);

            int W = ClientSize.Width;

            // ---- 标题栏 ----
            g.DrawImage(_glyph, PAD, 14, 30, 30);
            g.DrawString("输入统计", fTitle, bFg, PAD + 40, 17);
            // 日期胶囊
            string date = DateTime.Now.ToString("yyyy-MM-dd  HH:mm");
            float dw = g.MeasureString(date, fSm).Width + 22;
            var datePill = new RectangleF(_pinRect.X - 12 - dw, 16, dw, 26);   // 让位给置顶/最小化/关闭三键
            using (var p = IconFactory.Round(datePill, 13))
            using (var b = new SolidBrush(Color.FromArgb(235, 238, 244)))
                g.FillPath(b, p);
            g.DrawString(date, fSm, bSub, datePill.X + 11, datePill.Y + 5);
            DrawButtons(g);

            // ---- 指标卡片 ----
            string[] names = { "按键次数", "中文字数", "英文字符", "英文单词" };
            Color[] dots = { BAR1B, ACCENT, BAR2B, BAR3B };
            long[] todayV = { _today[0] + _live.Item1, _today[1], _today[2] + _live.Item2, _today[3] + _live.Item3 };
            long[] totalV = { _total[0] + _live.Item1, _total[1], _total[2] + _live.Item2, _total[3] + _live.Item3 };
            int gap = 12, cw = (W - 2 * PAD - 3 * gap) / 4, ch = 92, cy = 64;
            for (int i = 0; i < 4; i++)
            {
                var rc = new Rectangle(PAD + i * (cw + gap), cy, cw, ch);
                Card(g, rc, 14);
                using var bV = new SolidBrush(i == 1 ? ACCENT : FG);
                Centered(g, todayV[i].ToString("N0"), fBig, bV, rc.X, rc.Y + 14, rc.Width);
                // 名称 + 左侧色点
                float nameW = g.MeasureString(names[i], fSm).Width;
                float nx = rc.X + (rc.Width - nameW) / 2;
                using (var bd = new SolidBrush(dots[i]))
                    g.FillEllipse(bd, nx - 12, rc.Y + 50, 7, 7);
                g.DrawString(names[i], fSm, bSub, nx, rc.Y + 46);
                Centered(g, "累计 " + totalV[i].ToString("N0"), fTiny, bSub, rc.X, rc.Y + 68, rc.Width);
            }

            int cardW = W - 2 * PAD;
            int y = cy + ch + 20;
            y = Section(g, fLbl, "今日 · 时段分布", BAR3B, y, 104, rc => DrawHours(g, rc, fTiny));
            y = Section(g, fLbl, "最近 14 天 · 按键次数", BAR1B, y, 112, rc => DrawDays(g, rc, 1, BAR1A, BAR1B, fTiny));
            y = Section(g, fLbl, "最近 14 天 · 中文字数", BAR2B, y, 112, rc => DrawDays(g, rc, 2, BAR2A, BAR2B, fTiny));
            y = Section(g, fLbl, "常用词 Top 10", ACCENT, y, 104, rc => DrawWords(g, rc));
        }

        // ---- 标题色块 + 卡片容器，回调里画图表 ----
        private int Section(Graphics g, Font fLbl, string title, Color dot, int y, int h, Action<Rectangle> draw)
        {
            using (var bd = new SolidBrush(dot))
            using (var p = IconFactory.Round(new RectangleF(PAD, y + 1, 11, 11), 3))
                g.FillPath(bd, p);
            using (var bf = new SolidBrush(FG))
                g.DrawString(title, fLbl, bf, PAD + 18, y - 2);
            var rc = new Rectangle(PAD, y + 22, ClientSize.Width - 2 * PAD, h);
            Card(g, rc, 14);
            draw(rc);
            return y + 22 + h + 18;
        }

        // ---- 柔和投影 + 白卡片 + 细边 ----
        private static void Card(Graphics g, Rectangle rc, int r)
        {
            // 向下偏移的柔和投影（多层低透明度叠加，营造"悬浮"感）
            for (int k = 9; k >= 1; k--)
            {
                var sr = new RectangleF(rc.X - k + 0.5f, rc.Y - k + 6, rc.Width + 2 * k - 1, rc.Height + 2 * k);
                using var p = IconFactory.Round(sr, r + k);
                using var b = new SolidBrush(Color.FromArgb(7, 38, 50, 90));
                g.FillPath(b, p);
            }
            using (var p = IconFactory.Round(rc, r))
            using (var b = new SolidBrush(CARD)) g.FillPath(b, p);
            using (var p = IconFactory.Round(rc, r))
            using (var pen = new Pen(Color.FromArgb(232, 235, 240))) g.DrawPath(pen, p);
        }

        private void DrawButtons(Graphics g)
        {
            // 置顶大头针：钉住=蓝色实心竖立，取消=灰色斜放空心
            DrawPin(g, _pinRect, TopMost, _pinHover);
            // 最小化键：一条横线
            if (_minHover)
                using (var b = new SolidBrush(Color.FromArgb(232, 234, 240)))
                using (var p = IconFactory.Round(_minRect, 8)) g.FillPath(b, p);
            using (var pen = new Pen(_minHover ? FG : SUB, 1.6f))
            {
                int cy = _minRect.Y + _minRect.Height / 2;
                g.DrawLine(pen, _minRect.X + 8, cy, _minRect.Right - 8, cy);
            }
            // 关闭键：×
            if (_closeHover)
                using (var b = new SolidBrush(Color.FromArgb(240, 220, 222)))
                using (var p = IconFactory.Round(_closeRect, 8)) g.FillPath(b, p);
            using (var pen = new Pen(_closeHover ? ColorTranslator.FromHtml("#C04848") : SUB, 1.6f))
            {
                var r = _closeRect; int m = 9;
                g.DrawLine(pen, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
                g.DrawLine(pen, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
            }
        }

        // 大头针图标：active=置顶（竖立实心蓝），否则（斜放空心灰）
        private void DrawPin(Graphics g, Rectangle rect, bool active, bool hover)
        {
            if (hover)
                using (var b = new SolidBrush(Color.FromArgb(232, 234, 240)))
                using (var p = IconFactory.Round(rect, 8)) g.FillPath(b, p);

            using var path = new GraphicsPath();
            using (var cap = IconFactory.Round(new RectangleF(-5, -7, 10, 4), 1.6f))
                path.AddPath(cap, false);                       // 针帽（按压的平头）
            path.AddPolygon(new[] {                              // 针身 + 针尖
                new PointF(-1.7f, -3), new PointF(1.7f, -3),
                new PointF(1.7f, 2),  new PointF(0, 6), new PointF(-1.7f, 2) });

            using var m = new Matrix();
            m.Translate(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f - 1);
            if (!active) m.Rotate(40);                           // 取消置顶：斜放
            path.Transform(m);

            if (active)
            {
                using var b = new SolidBrush(ACCENT);
                g.FillPath(b, path);
            }
            else
            {
                using var pen = new Pen(SUB, 1.5f);
                g.DrawPath(pen, path);
            }
        }

        private static void Centered(Graphics g, string s, Font f, Brush b, int x, int y, int w)
        {
            var sz = g.MeasureString(s, f);
            g.DrawString(s, f, b, x + (w - sz.Width) / 2, y);
        }

        // 圆角顶的渐变柱
        private static void Bar(Graphics g, RectangleF rc, Color top, Color bot)
        {
            if (rc.Height < 0.5f) return;
            float r = Math.Min(rc.Width / 2f, Math.Min(rc.Height, 5f));
            float d = r * 2;
            using var p = new GraphicsPath();
            p.AddArc(rc.X, rc.Y, d, d, 180, 90);
            p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
            p.AddLine(rc.Right, rc.Y + r, rc.Right, rc.Bottom);
            p.AddLine(rc.Right, rc.Bottom, rc.X, rc.Bottom);
            p.CloseFigure();
            using var lg = new LinearGradientBrush(
                new RectangleF(rc.X, rc.Y, rc.Width, Math.Max(rc.Height, 1)), top, bot, 90f);
            g.FillPath(lg, p);
        }

        private void DrawHours(Graphics g, Rectangle rc, Font fTiny)
        {
            using var bSub = new SolidBrush(SUB);
            long vmax = _hours.Count > 0 ? Math.Max(1, _hours.Values.Max()) : 1;
            float bw = (rc.Width - 24) / 24f, baseY = rc.Bottom - 22, top = rc.Y + 14;
            for (int h = 0; h < 24; h++)
            {
                _hours.TryGetValue(h, out long v);
                float x0 = rc.X + 12 + h * bw + bw * 0.22f, wRect = bw * 0.56f;
                if (v > 0)
                {
                    float bh = (baseY - top) * v / (float)vmax;
                    Bar(g, new RectangleF(x0, baseY - bh, wRect, bh), BAR3A, BAR3B);
                }
                if (h % 3 == 0)
                    g.DrawString(h.ToString("D2"), fTiny, bSub, x0 - 4, baseY + 5);
            }
        }

        private void DrawDays(Graphics g, Rectangle rc, int idx, Color top, Color bot, Font fTiny)
        {
            using var bSub = new SolidBrush(SUB);
            if (_days.Count == 0) return;
            Func<(string, long, long, long), long> sel = d => idx == 1 ? d.Item2 : d.Item3;
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            long lk = _live.Item1;
            long Val(int i) => sel(_days[i]) + (idx == 1 && i == _days.Count - 1 && _days[i].Item1 == today ? lk : 0);
            long vmax = 1;
            for (int i = 0; i < _days.Count; i++) vmax = Math.Max(vmax, Val(i));
            float bw = (rc.Width - 24) / (float)_days.Count;
            float baseY = rc.Bottom - 22, top0 = rc.Y + 24;
            for (int i = 0; i < _days.Count; i++)
            {
                long v = Val(i);
                float x0 = rc.X + 12 + i * bw + bw * 0.16f, wRect = bw * 0.68f;
                if (v > 0)
                {
                    float bh = (baseY - top0) * v / (float)vmax;
                    Bar(g, new RectangleF(x0, baseY - bh, wRect, bh), top, bot);
                    string lab = v >= 10000 ? (v / 1000.0).ToString("0.0") + "k" : v.ToString("N0");
                    float lw = g.MeasureString(lab, fTiny).Width;
                    g.DrawString(lab, fTiny, bSub, x0 + wRect / 2 - lw / 2, baseY - bh - 14);
                }
                string md = _days[i].Item1.Substring(5);
                float mw = g.MeasureString(md, fTiny).Width;
                g.DrawString(md, fTiny, bSub, x0 + wRect / 2 - mw / 2, baseY + 5);
            }
        }

        // 常用词：胶囊标签自动换行，序号圆点内的数字按墨迹精确居中
        private void DrawWords(Graphics g, Rectangle rc)
        {
            using var fW = new Font("微软雅黑", 9.5f);
            using var fam = new FontFamily("微软雅黑");
            using var bSub = new SolidBrush(SUB);
            using var bf = new SolidBrush(FG);
            using var sfV = new StringFormat { LineAlignment = StringAlignment.Center };
            if (_words.Count == 0)
            {
                g.DrawString("还没有数据，去打几个字吧～", fW, bSub, rc.X + 16, rc.Y + 16);
                return;
            }
            const float ph = 26, circ = 18, padL = 5, gapTC = 7, padR = 13, lh = 33;
            float x = rc.X + 14, y = rc.Y + 15, maxX = rc.Right - 14;
            for (int i = 0; i < _words.Count; i++)
            {
                string label = $"{_words[i].Item1}  ×{_words[i].Item2}";
                float tw = g.MeasureString(label, fW).Width;
                float pillW = padL + circ + gapTC + tw + padR;
                if (x + pillW > maxX && x > rc.X + 14) { x = rc.X + 14; y += lh; }
                if (y + ph > rc.Bottom) break;
                var pill = new RectangleF(x, y, pillW, ph);
                using (var p = IconFactory.Round(pill, ph / 2))
                using (var b = new SolidBrush(Color.FromArgb(242, 244, 250))) g.FillPath(b, p);
                // 序号圆点（竖直居中）+ 数字（墨迹居中）
                var circle = new RectangleF(x + padL, y + (ph - circ) / 2f, circ, circ);
                using (var b = new SolidBrush(i < 3 ? ACCENT : Color.FromArgb(186, 192, 204)))
                    g.FillEllipse(b, circle);
                IconFactory.FillGlyphCentered(g, (i + 1).ToString(), fam, Brushes.White, circle, 0.5f);
                // 词条文字（竖直居中）
                g.DrawString(label, fW, bf,
                    new RectangleF(x + padL + circ + gapTC, y, tw + 6, ph), sfV);
                x += pillW + 9;
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
        // 周报存数据目录（与源码仓库位置无关，移动/删除仓库也不丢）
        public static readonly string ReportsDir = Path.Combine(DataDir, "reports");
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

        // 离屏渲染面板为 PNG（开发自检用，不影响正式运行）
        private static void RenderPreview(string outPath)
        {
            try
            {
                ApplicationConfiguration.Initialize();
                EnsureDatabase();
                using var store = new Store(DbPath);
                var counter = new KeyCounter(store, new HashSet<string>());   // 不 Start，不装钩子
                var reader = new CommitLogReader(store);
                using var f = new PanelForm(store, counter, reader);
                f.CreateControl();                          // 强制建句柄以便绘制
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) { Log("RenderPreview", ex); }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log("UnhandledException", e.ExceptionObject as Exception ?? new Exception("非异常对象: " + e.ExceptionObject));
            Application.ThreadException += (s, e) => Log("ThreadException", e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 开发自检：把面板离屏渲染成 PNG 后退出（不计数、不抢互斥锁）
            // 用法：IMEStatsSharp.exe --render-preview <输出png路径>
            if (args.Length >= 2 && args[0] == "--render-preview")
            {
                RenderPreview(args[1]);
                return;
            }

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
                Icon = IconFactory.Create(false),
                Text = "输入统计 (C#)",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            _tray.ContextMenuStrip.Items.Add("统计面板", null, (o, e) => ShowPanel());
            _tray.ContextMenuStrip.Items.Add("立即更新词频/喂词", null, (o, e) => RunWordWorker());
            _tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());

            // 开机自启开关（写/删启动文件夹里的 vbs，勾选反映当前状态）
            var auto = new ToolStripMenuItem("开机自启动") { Checked = AutostartEnabled() };
            auto.Click += (o, e) => { SetAutostart(!AutostartEnabled()); auto.Checked = AutostartEnabled(); };
            _tray.ContextMenuStrip.Items.Add(auto);
            _tray.ContextMenuStrip.Items.Add("导出 CSV", null, (o, e) => ExportCsv());
            _tray.ContextMenuStrip.Items.Add("打开数据文件夹", null, (o, e) =>
            { try { Process.Start("explorer.exe", DataDir); } catch (Exception ex) { Log("OpenFolder", ex); } });
            _tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());

            var pause = new ToolStripMenuItem("暂停统计");
            pause.Click += (o, e) =>
            {
                _counter.Paused = !_counter.Paused;
                pause.Checked = _counter.Paused;
                var old = _tray.Icon;
                _tray.Icon = IconFactory.Create(_counter.Paused);
                old?.Dispose();         // 释放被换下的图标句柄
            };
            _tray.ContextMenuStrip.Items.Add(pause);
            // 每次右键弹出前刷新"开机自启"勾选，避免外部改动后状态不同步
            _tray.ContextMenuStrip.Opening += (o, e) => auto.Checked = AutostartEnabled();
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

        // ---- 开机自启：写/删启动文件夹里的 vbs（与 enable_autostart.bat 同一文件名） ----
        private static string StartupVbs => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ime_stats_autostart.vbs");

        private static bool AutostartEnabled() => File.Exists(StartupVbs);

        private static void SetAutostart(bool on)
        {
            try
            {
                if (on)
                {
                    string exe = Environment.ProcessPath ?? Path.Combine(AppDir, "IMEStatsSharp.exe");
                    File.WriteAllText(StartupVbs,
                        "Set ws = CreateObject(\"WScript.Shell\")\r\n" +
                        "ws.Run \"\"\"" + exe + "\"\"\", 0, False\r\n");
                }
                else if (File.Exists(StartupVbs)) File.Delete(StartupVbs);
            }
            catch (Exception ex) { Log("Autostart", ex); }
        }

        // ---- 导出全部每日数据为 CSV（带 BOM，Excel 直接认中文）并在资源管理器选中 ----
        private static void ExportCsv()
        {
            try
            {
                var rows = _store.RangeDays("0000-01-01", "9999-12-31");
                var sb = new StringBuilder();
                sb.AppendLine("日期,按键次数,中文字数,英文单词");
                foreach (var r in rows)
                    sb.AppendLine($"{r.date},{r.keys},{r.cn},{r.ew}");
                string path = Path.Combine(DataDir, "导出统计_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex) { Log("ExportCsv", ex); }
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
