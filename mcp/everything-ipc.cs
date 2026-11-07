// everything-ipc.cs — zero-dependency Everything IPC bridge.
//
// Everything (voidtools) exposes its full index over a documented WM_COPYDATA
// IPC protocol. Its command line has no "search and print" switch, so this
// helper talks the IPC protocol directly instead of scraping the GUI.
//
// Protocol source of truth (values below were read from it, not guessed):
//   Everything-SDK\ipc\everything_ipc.h  (voidtools, Everything SDK)
//
// Usage (line-delimited JSON on stdin, one JSON object per line on stdout):
//   {"cmd":"search","search":"*.log","max":50,"offset":0,"sort":"name_asc",
//    "matchCase":false,"matchPath":false,"regex":false,"wholeWord":false,
//    "diacritics":false,"fields":["name","path","fullpath","size","ext",
//    "dateModified","dateCreated","dateAccessed","attributes","runcount"]}
//   {"cmd":"count","search":"*.log"}
//   {"cmd":"version"}
//
// Compile:
//   csc.exe /nologo /target:exe /platform:anycpu /optimize+ /out:everything-ipc.exe everything-ipc.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class EverythingIpc
{
    // ---- Everything IPC constants (everything_ipc.h) ----------------------

    private const int WM_USER = 0x0400;
    private const int WM_COPYDATA = 0x004A;
    private const int EVERYTHING_WM_IPC = WM_USER;

    private const int IPC_GET_MAJOR_VERSION = 0;
    private const int IPC_GET_MINOR_VERSION = 1;
    private const int IPC_GET_REVISION = 2;
    private const int IPC_GET_BUILD_NUMBER = 3;

    // HAZARD, measured on Everything 1.4.1.1032 (x86, Windows 11): the SDK header
    // declares code 4 as EVERYTHING_IPC_GET_TOTRESULTS, but sending it to the
    // taskbar-notification window terminates Everything -- the process is gone
    // within seconds and the reply is a meaningless 1. Everything 1.5 evidently
    // repurposed this slot to disable the HTTP server.
    //
    // Nothing here uses code 4. Total hit counts come from the LIST2 reply header
    // of a real query, which is both accurate and safe.
    //   0..3  version / build                 safe
    //   4     DESTRUCTIVE on 1.4.1.1032       never send
    //   401   db loaded                       safe
    //   402   db busy                         safe
    //   403   is admin                        safe
    //   500+  search-client settings/mutators  not used (state-changing)
    private const int IPC_IS_DB_LOADED = 401;
    private const int IPC_IS_DB_BUSY = 402;
    private const int IPC_IS_ADMIN = 403;
    private const int IPC_GET_NUMRESULTS = 9;

    private const string IPC_WNDCLASS = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const string SEARCH_CLIENT_WNDCLASS = "EVERYTHING";

    // dwData discriminator for the WM_COPYDATA request. These are NOT 1 and 2:
    //   1 / 2  = EVERYTHING_IPC_COPYDATAQUERYA/W   -> old QUERY struct (name/path only)
    //   17/18  = EVERYTHING_IPC_COPYDATA_QUERY2A/W -> QUERY2 struct used here
    // Sending QUERY2 fields under dwData=2 makes Everything parse the buffer as the
    // old QUERY layout, which silently yields a reply with an unrelated header.
    private const int COPYDATA_QUERYW = 2;    // old QUERY (A/W)
    private const int COPYDATA_QUERY2W = 18;  // QUERY2 (Unicode)
    private const uint ALLRESULTS = 0xFFFFFFFF;

    private const uint MATCHCASE = 0x00000001;
    private const uint MATCHWHOLEWORD = 0x00000002;
    private const uint MATCHPATH = 0x00000004;
    private const uint REGEX = 0x00000008;
    private const uint MATCHDIACRITICS = 0x00000010;

    private const uint FOLDER = 0x00000001;
    private const uint DRIVE = 0x00000002;

    private const uint R_NAME = 0x00000001;
    private const uint R_PATH = 0x00000002;
    private const uint R_FULLPATH = 0x00000004;
    private const uint R_EXTENSION = 0x00000008;
    private const uint R_SIZE = 0x00000010;
    private const uint R_DATE_CREATED = 0x00000020;
    private const uint R_DATE_MODIFIED = 0x00000040;
    private const uint R_DATE_ACCESSED = 0x00000080;
    private const uint R_ATTRIBUTES = 0x00000100;
    private const uint R_FILELIST_FILENAME = 0x00000200;
    private const uint R_RUN_COUNT = 0x00000400;
    private const uint R_DATE_RUN = 0x00000800;
    private const uint R_DATE_RECENTLY_CHANGED = 0x00001000;

    // ---- Win32 ------------------------------------------------------------

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter,
        string lpszClass, string lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    // ---- Reply plumbing ---------------------------------------------------

    private const int WM_TIMER = 0x0113;
    private const uint TIMER_ID_FLUSH = 1;

    /// <summary>HWND_MESSAGE: a message-only window, invisible and never painted.</summary>
    private static readonly IntPtr HwndMessage = new IntPtr(-3);

    private static IntPtr _listenerHwnd = IntPtr.Zero;
    private static readonly object Gate = new object();
    private static volatile bool _replyReady;
    private static long _deadlineMs;   // not volatile: only a timer-driven hint, never a gate
    private static volatile uint _pendingReplyId;

    private static byte[] LastList;
    private static int LastListLen;
    private static string LastReplyDiag = "";

    private static byte[] CopyOut(IntPtr src, int len)
    {
        if (len <= 0) throw new InvalidOperationException("reply had cbData=" + len);
        byte[] buf = new byte[len];
        Marshal.Copy(src, buf, 0, len);
        return buf;
    }

    // ---- Reply window (WinForms owns the class registration and the pump) ---

    /// <summary>
    /// Hidden window that receives Everything's WM_COPYDATA reply.
    ///
    /// The window class is registered by WinForms rather than by hand: a manual
    /// RegisterClassEx/CreateWindowEx pair failed here with error 1407
    /// (ERROR_CANNOT_FIND_WND_CLASS) because the class atom is per-module and the
    /// managed entry point does not own the module the class name resolved against.
    /// WinForms also runs the message pump for us, so the reply is actually
    /// delivered instead of sitting in a queue nobody drains.
    /// </summary>
    private sealed class ReplyForm : Form
    {
        public ReplyForm()
        {
            // Message-only: never shown, never in the taskbar or Alt+Tab.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-2000, -2000);
            Size = new System.Drawing.Size(1, 1);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // A message-only child gives the same delivery semantics as the
            // manual HwndMessage window without depending on class atoms.
            NativeMethods.SetParent(Handle, HwndMessage);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_COPYDATA)
            {
                COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(
                    m.LParam, typeof(COPYDATASTRUCT));
                try
                {
                    if ((uint)cds.dwData == _pendingReplyId)
                    {
                        LastList = CopyOut(cds.lpData, cds.cbData);
                        LastListLen = cds.cbData;
                        lock (Gate)
                        {
                            _replyReady = true;
                            Monitor.PulseAll(Gate);
                        }
                        m.Result = new IntPtr(1);
                        return;
                    }
                    LastReplyDiag = "ignored WM_COPYDATA dwData=" + cds.dwData.ToInt64() +
                                    " cbData=" + cds.cbData + " expectedId=" + _pendingReplyId;
                }
                catch (Exception ex)
                {
                    LastReplyDiag = "copy failed: " + ex.GetType().Name + ": " + ex.Message +
                                    " | dwData=" + cds.dwData.ToInt64() +
                                    " cbData=" + cds.cbData +
                                    " lpData=0x" + cds.lpData.ToInt64().ToString("X");
                    lock (Gate)
                    {
                        _replyReady = true;
                        Monitor.PulseAll(Gate);
                    }
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    }

    private static readonly ManualResetEventSlim ListenerReady = new ManualResetEventSlim(false);
    private static ReplyForm _replyForm;
    private static Thread _uiThread;

    /// <summary>Start the reply window and its pump on a dedicated STA thread.</summary>
    private static void EnsureListener()
    {
        if (_listenerHwnd != IntPtr.Zero) return;

        lock (Gate)
        {
            if (_listenerHwnd != IntPtr.Zero) return;

            _uiThread = new Thread(delegate()
            {
                try
                {
                    _replyForm = new ReplyForm();
                    _replyForm.CreateControl();
                    IntPtr h = _replyForm.Handle;   // forces handle creation
                    _listenerHwnd = h;
                }
                catch (Exception ex)
                {
                    ListenerError = ex;
                    ListenerReady.Set();
                    return;
                }

                ListenerReady.Set();
                try { Application.Run(new ApplicationContext(_replyForm)); }
                catch { /* shutting down */ }
            });
            _uiThread.IsBackground = true;
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();

            if (!ListenerReady.Wait(10000))
                throw new TimeoutException("reply window did not start within 10 s");
            if (ListenerError != null)
                throw new InvalidOperationException("reply window failed: " + ListenerError.Message);
        }
    }

    private static Exception ListenerError;

    // ---- Everything handle discovery --------------------------------------

    /// <summary>
    /// Everything exposes more than one IPC endpoint: the taskbar-notification
    /// window serves the version/state commands, while QUERY is documented against
    /// the search-client window. Both are probed, with "auto" falling through to
    /// the other when the first rejects the query.
    /// </summary>
    private static IntPtr FindEverythingNotification()
    {
        return FindWindow(IPC_WNDCLASS, null);
    }

    private static IntPtr FindEverythingSearchClient()
    {
        return FindWindow(SEARCH_CLIENT_WNDCLASS, null);
    }

    /// <summary>Query endpoints in the order they should be tried.</summary>
    private static List<IntPtr> QueryTargets(string preference)
    {
        IntPtr notif = FindEverythingNotification();
        IntPtr client = FindEverythingSearchClient();

        List<IntPtr> order = new List<IntPtr>();
        if (preference == "notification") { AddIf(order, notif); AddIf(order, client); }
        else if (preference == "client") { AddIf(order, client); AddIf(order, notif); }
        else { AddIf(order, client); AddIf(order, notif); }
        return order;
    }

    private static void AddIf(List<IntPtr> l, IntPtr h)
    {
        if (h != IntPtr.Zero && !l.Contains(h)) l.Add(h);
    }

    // ---- Query ------------------------------------------------------------

    private static uint BuildFlags(bool matchCase, bool matchPath, bool wholeWord,
        bool regex, bool diacritics)
    {
        uint f = 0;
        if (matchCase) f |= MATCHCASE;
        if (matchPath) f |= MATCHPATH;
        if (wholeWord) f |= MATCHWHOLEWORD;
        if (regex) f |= REGEX;
        if (diacritics) f |= MATCHDIACRITICS;
        return f;
    }

    private static uint ParseSort(string s)
    {
        if (string.IsNullOrEmpty(s)) return 1; // name ascending: always fast in Everything
        switch (s.Trim().ToLowerInvariant())
        {
            case "name_asc": return 1;
            case "name_desc": return 2;
            case "path_asc": return 3;
            case "path_desc": return 4;
            case "size_asc": return 5;
            case "size_desc": return 6;
            case "ext_asc": return 7;
            case "ext_desc": return 8;
            case "date_modified_asc": return 13;
            case "date_modified_desc": return 14;
            case "date_created_asc": return 11;
            case "date_created_desc": return 12;
            case "date_accessed_asc": return 23;
            case "date_accessed_desc": return 24;
            case "runcount_asc": return 19;
            case "runcount_desc": return 20;
            default:
                throw new ArgumentException("unknown sort: " + s);
        }
    }

    private static byte[] QueryRaw(string search, uint maxResults, uint offset, uint sort,
        uint searchFlags, uint requestFlags, int timeoutMs, string targetPreference)
    {
        IntPtr ignored;
        return QueryRaw(search, maxResults, offset, sort, searchFlags, requestFlags, timeoutMs,
            targetPreference, out ignored);
    }

    private static byte[] QueryRaw(string search, uint maxResults, uint offset, uint sort,
        uint searchFlags, uint requestFlags, int timeoutMs, string targetPreference,
        out IntPtr answeredBy)
    {
        answeredBy = IntPtr.Zero;
        List<IntPtr> targets = QueryTargets(targetPreference);
        if (targets.Count == 0)
            throw new InvalidOperationException(
                "Everything is not running (no window of class '" + SEARCH_CLIENT_WNDCLASS +
                "' or '" + IPC_WNDCLASS + "').");

        EnsureListener();

        byte[] searchBytes = Encoding.Unicode.GetBytes(search ?? "");
        int fixedPart = 7 * 4;                 // reply_hwnd .. sort_type
        int total = fixedPart + searchBytes.Length + 2;
        byte[] req = new byte[total];

        uint replyId = (uint)(Environment.TickCount & 0x7FFFFFFF);
        if (replyId < 1000) replyId += 1000;

        // EVERYTHING_IPC_QUERY2 layout (pack=1): 7 DWORDs then a UTF-16 search string.
        BitConverter.GetBytes((uint)_listenerHwnd.ToInt64()).CopyTo(req, 0);
        BitConverter.GetBytes(replyId).CopyTo(req, 4);
        BitConverter.GetBytes(searchFlags).CopyTo(req, 8);
        BitConverter.GetBytes(offset).CopyTo(req, 12);
        BitConverter.GetBytes(maxResults).CopyTo(req, 16);
        BitConverter.GetBytes(requestFlags).CopyTo(req, 20);
        BitConverter.GetBytes(sort).CopyTo(req, 24);
        searchBytes.CopyTo(req, 28);

        IntPtr pinned = Marshal.AllocHGlobal(total);
        try
        {
            Marshal.Copy(req, 0, pinned, total);
            COPYDATASTRUCT cds = new COPYDATASTRUCT();
            cds.dwData = new IntPtr(COPYDATA_QUERY2W);
            cds.cbData = total;
            cds.lpData = pinned;

            string lastError = "no endpoint accepted the query";
            foreach (IntPtr target in targets)
            {
                lock (Gate)
                {
                    _replyReady = false;
                    _deadlineMs = NowMs() + timeoutMs;
                    _pendingReplyId = replyId;
                    LastList = null;
                    LastListLen = 0;
                    LastReplyDiag = "";
                }

                IntPtr ok = SendMessage(target, WM_COPYDATA, _listenerHwnd, ref cds);
                if (ok == IntPtr.Zero)
                {
                    lastError = "endpoint 0x" + ((long)target).ToString("X") +
                                " rejected the query (WM_COPYDATA returned FALSE)";
                    continue;
                }

                lock (Gate)
                {
                    while (!_replyReady)
                    {
                        // Bounded wait, then re-check the wall clock: a lost reply
                        // must surface as a timeout, not an indefinite block.
                        if (Monitor.Wait(Gate, 100)) continue;
                        if (NowMs() > _deadlineMs) break;
                    }
                }

                if (LastList != null)
                {
                    answeredBy = target;
                    return LastList;
                }
                lastError = "endpoint 0x" + ((long)target).ToString("X") +
                            " returned no reply within " + timeoutMs + " ms";
                if (LastReplyDiag.Length > 0) lastError += " (" + LastReplyDiag + ")";
            }

            throw new InvalidOperationException("Everything query failed: " + lastError);
        }
        finally
        {
            Marshal.FreeHGlobal(pinned);
        }
    }

    // ---- LIST2 decoding ---------------------------------------------------

    private sealed class Row
    {
        public uint Flags;
        public string Name, Path, FullPath, Extension, FileListName;
        public long Size = -1;
        public long? DateCreated, DateModified, DateAccessed, DateRecentlyChanged;
        public uint Attributes;
        public uint RunCount;
        public bool HasSize, HasAttrs, HasRunCount;
    }

    private sealed class List2
    {
        public uint Total, Num, Offset, RequestFlags, Sort;
        public readonly List<Row> Rows = new List<Row>();
    }

    private static List2 DecodeList2(byte[] b, uint requestFlags)
    {
        List2 l = new List2();
        l.Total = BitConverter.ToUInt32(b, 0);
        l.Num = BitConverter.ToUInt32(b, 4);
        l.Offset = BitConverter.ToUInt32(b, 8);
        l.RequestFlags = BitConverter.ToUInt32(b, 12);
        l.Sort = BitConverter.ToUInt32(b, 16);

        int itemBase = 20;
        for (uint i = 0; i < l.Num; i++)
        {
            int ip = itemBase + (int)i * 8;
            if (ip + 8 > b.Length) break;

            Row row = new Row();
            row.Flags = BitConverter.ToUInt32(b, ip);
            uint dataOffset = BitConverter.ToUInt32(b, ip + 4);
            int p = (int)dataOffset;

            // Data block order is fixed by the SDK header and matches the
            // ascending bit order of the QUERY2_REQUEST_* flags.
            if ((requestFlags & R_NAME) != 0) row.Name = ReadString(b, ref p);
            if ((requestFlags & R_PATH) != 0) row.Path = ReadString(b, ref p);
            if ((requestFlags & R_FULLPATH) != 0) row.FullPath = ReadString(b, ref p);
            if ((requestFlags & R_EXTENSION) != 0) row.Extension = ReadString(b, ref p);
            if ((requestFlags & R_SIZE) != 0)
            {
                row.Size = BitConverter.ToInt64(b, p);
                row.HasSize = true;
                p += 8;
            }
            if ((requestFlags & R_DATE_CREATED) != 0) { row.DateCreated = ReadFileTime(b, ref p); }
            if ((requestFlags & R_DATE_MODIFIED) != 0) { row.DateModified = ReadFileTime(b, ref p); }
            if ((requestFlags & R_DATE_ACCESSED) != 0) { row.DateAccessed = ReadFileTime(b, ref p); }
            if ((requestFlags & R_ATTRIBUTES) != 0)
            {
                row.Attributes = BitConverter.ToUInt32(b, p);
                row.HasAttrs = true;
                p += 4;
            }
            if ((requestFlags & R_FILELIST_FILENAME) != 0) row.FileListName = ReadString(b, ref p);
            if ((requestFlags & R_RUN_COUNT) != 0)
            {
                row.RunCount = BitConverter.ToUInt32(b, p);
                row.HasRunCount = true;
                p += 4;
            }
            if ((requestFlags & R_DATE_RUN) != 0) { ReadFileTime(b, ref p); }
            if ((requestFlags & R_DATE_RECENTLY_CHANGED) != 0) { row.DateRecentlyChanged = ReadFileTime(b, ref p); }

            if (row.FullPath == null)
            {
                if (row.Path != null && row.Name != null)
                    row.FullPath = row.Path.TrimEnd('\\') + "\\" + row.Name;
                else if (row.Name != null)
                    row.FullPath = row.Name;
            }
            l.Rows.Add(row);
        }
        return l;
    }

    private static string ReadString(byte[] b, ref int p)
    {
        uint len = BitConverter.ToUInt32(b, p);
        p += 4;
        string s = Encoding.Unicode.GetString(b, p, (int)len * 2);
        p += (int)len * 2 + 2;   // skip the UTF-16 null terminator
        return s;
    }

    private static long? ReadFileTime(byte[] b, ref int p)
    {
        long ft = BitConverter.ToInt64(b, p);
        p += 8;
        if (ft <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ft).Ticks; }
        catch { return null; }
    }

    // ---- JSON output ------------------------------------------------------

    private static readonly DateTime Epoch = DateTime.UtcNow;

    /// <summary>
    /// Monotonic-enough millisecond clock. csc from the .NET Framework 4.0
    /// directory is the only compiler guaranteed present, and it predates
    /// Environment.TickCount64.
    /// </summary>
    private static long NowMs()
    {
        return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
    }

    private static string J(string s)
    {
        if (s == null) return "null";
        StringBuilder sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string IsoFromTicks(long? ticks)
    {
        if (ticks == null || ticks.Value <= 0) return "null";
        try
        {
            return "\"" + new DateTime(ticks.Value, DateTimeKind.Utc)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "\"";
        }
        catch { return "null"; }
    }

    private static string AttributesToString(uint a)
    {
        StringBuilder sb = new StringBuilder();
        if ((a & 0x0001) != 0) sb.Append('R');
        if ((a & 0x0002) != 0) sb.Append('H');
        if ((a & 0x0004) != 0) sb.Append('S');
        if ((a & 0x0010) != 0) sb.Append('D');
        if ((a & 0x0020) != 0) sb.Append('A');
        if ((a & 0x0800) != 0) sb.Append('C');
        if ((a & 0x0400) != 0) sb.Append('L');
        return sb.ToString();
    }

    // ---- Commands ---------------------------------------------------------

    private static readonly uint DEFAULT_FIELDS = R_NAME | R_PATH | R_FULLPATH | R_EXTENSION |
                                                  R_SIZE | R_DATE_MODIFIED | R_ATTRIBUTES;

    private static uint FieldsToFlags(List<string> fields)
    {
        if (fields == null || fields.Count == 0) return DEFAULT_FIELDS;
        uint f = 0;
        foreach (string raw in fields)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "name": f |= R_NAME; break;
                case "path": f |= R_PATH; break;
                case "fullpath": case "full_path": case "full": f |= R_FULLPATH; break;
                case "ext": case "extension": f |= R_EXTENSION; break;
                case "size": f |= R_SIZE; break;
                case "datecreated": case "date_created": f |= R_DATE_CREATED; break;
                case "datemodified": case "date_modified": case "modified": f |= R_DATE_MODIFIED; break;
                case "dateaccessed": case "date_accessed": case "accessed": f |= R_DATE_ACCESSED; break;
                case "attributes": case "attrs": f |= R_ATTRIBUTES; break;
                case "filelist": case "filelist_filename": f |= R_FILELIST_FILENAME; break;
                case "runcount": case "run_count": f |= R_RUN_COUNT; break;
                case "daterun": case "date_run": f |= R_DATE_RUN; break;
                case "daterecentlychanged": case "date_recently_changed": f |= R_DATE_RECENTLY_CHANGED; break;
                case "all":
                    f |= R_NAME | R_PATH | R_FULLPATH | R_EXTENSION | R_SIZE |
                         R_DATE_CREATED | R_DATE_MODIFIED | R_DATE_ACCESSED | R_ATTRIBUTES |
                         R_RUN_COUNT;
                    break;
                default: throw new ArgumentException("unknown field: " + raw);
            }
        }
        // Full path is the model-facing identity; name/path come along cheaply.
        return f | R_NAME | R_PATH | R_FULLPATH;
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); }
        catch { /* older console hosts */ }

        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
        {
            Console.WriteLine(VersionJson());
            return 0;
        }

        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            string response;
            try
            {
                response = Handle(line);
            }
            catch (Exception ex)
            {
                response = "{\"ok\":false,\"error\":" + J(ex.GetType().Name + ": " + ex.Message) +
                           ",\"stack\":" + J(ex.StackTrace) + "}";
            }
            Console.Out.WriteLine(response);
            Console.Out.Flush();
        }

        // Exit on stdin EOF. Queries already pump their own messages, so there is
        // nothing left to drain here; looping on GetMessage would block forever
        // because the heartbeat timer keeps the queue non-empty.
        return 0;
    }

    private static string VersionJson()
    {
        EnsureListener();

        // The taskbar-notification window exists only in some launch modes (it is
        // absent when Everything starts straight into the background). The search
        // window answers the same version/state commands, so either endpoint will
        // do -- probing both avoids reporting "not running" for a live instance.
        IntPtr h = FindEverythingNotification();
        string servedBy = "notification";
        if (h == IntPtr.Zero)
        {
            h = FindEverythingSearchClient();
            servedBy = "search-client";
        }
        if (h == IntPtr.Zero)
            return "{\"ok\":false,\"error\":\"Everything is not running\"}";

        int major = SendMessage(h, EVERYTHING_WM_IPC, new IntPtr(IPC_GET_MAJOR_VERSION), IntPtr.Zero).ToInt32();
        int minor = SendMessage(h, EVERYTHING_WM_IPC, new IntPtr(IPC_GET_MINOR_VERSION), IntPtr.Zero).ToInt32();
        int rev = SendMessage(h, EVERYTHING_WM_IPC, new IntPtr(IPC_GET_REVISION), IntPtr.Zero).ToInt32();
        int build = SendMessage(h, EVERYTHING_WM_IPC, new IntPtr(IPC_GET_BUILD_NUMBER), IntPtr.Zero).ToInt32();
        bool dbLoaded = SendMessage(h, EVERYTHING_WM_IPC, new IntPtr(IPC_IS_DB_LOADED), IntPtr.Zero) != IntPtr.Zero;
        bool busy = SendMessage(h, EVERYTHING_WM_IPC, new IntPtr(IPC_IS_DB_BUSY), IntPtr.Zero) != IntPtr.Zero;
        bool isAdmin = SendMessage(h, EVERYTHING_WM_IPC, new IntPtr(IPC_IS_ADMIN), IntPtr.Zero) != IntPtr.Zero;

        StringBuilder sb = new StringBuilder();
        sb.Append("{\"ok\":true");
        sb.Append(",\"version\":\"").Append(major).Append('.').Append(minor).Append('.')
          .Append(rev).Append('.').Append(build).Append('"');
        sb.Append(",\"major\":").Append(major);
        sb.Append(",\"minor\":").Append(minor);
        sb.Append(",\"revision\":").Append(rev);
        sb.Append(",\"build\":").Append(build);
        sb.Append(",\"dbLoaded\":").Append(dbLoaded ? "true" : "false");
        sb.Append(",\"dbBusy\":").Append(busy ? "true" : "false");
        sb.Append(",\"runningAsAdmin\":").Append(isAdmin ? "true" : "false");
        sb.Append(",\"servedBy\":").Append(J(servedBy));
        sb.Append(",\"queryable\":").Append(FindEverythingSearchClient() != IntPtr.Zero ? "true" : "false");
        sb.Append('}');
        return sb.ToString();
    }

    private static string Handle(string json)
    {
        Dictionary<string, object> o = MiniJson.ParseObject(json);
        string cmd = MiniJson.GetString(o, "cmd", "");
        switch (cmd)
        {
            case "version": return VersionJson();
            case "count": return Count(o);
            case "search": return Search(o);
            default: throw new ArgumentException("unknown cmd: " + cmd);
        }
    }

    private static string Count(Dictionary<string, object> o)
    {
        string search = MiniJson.GetString(o, "search", "");
        uint flags = BuildFlags(MiniJson.GetBool(o, "matchCase"), MiniJson.GetBool(o, "matchPath"),
            MiniJson.GetBool(o, "wholeWord"), MiniJson.GetBool(o, "regex"), MiniJson.GetBool(o, "diacritics"));
        string pref = MiniJson.GetString(o, "target", null);

        // max_results=1: this build rejects max_results=0. The authoritative hit
        // count is the LIST2 reply's own totitems field -- never IPC code 4 (see
        // the hazard note at the top of this file).
        IntPtr answered;
        byte[] raw = QueryRaw(search, 1, 0, ParseSort(MiniJson.GetString(o, "sort", null)),
            flags, R_NAME, 30000, pref, out answered);

        if (raw.Length < 20)
            return "{\"ok\":false,\"error\":\"short LIST2 reply (" + raw.Length + " bytes)\"}";
        uint tot = BitConverter.ToUInt32(raw, 0);

        return "{\"ok\":true,\"search\":" + J(search) + ",\"total\":" + tot + "}";
    }

    private static string Search(Dictionary<string, object> o)
    {
        string search = MiniJson.GetString(o, "search", "");
        uint max = (uint)MiniJson.GetInt(o, "max", 100);
        if (max == 0) max = ALLRESULTS;
        uint offset = (uint)MiniJson.GetInt(o, "offset", 0);
        int timeout = MiniJson.GetInt(o, "timeoutMs", 30000);

        uint flags = BuildFlags(MiniJson.GetBool(o, "matchCase"), MiniJson.GetBool(o, "matchPath"),
            MiniJson.GetBool(o, "wholeWord"), MiniJson.GetBool(o, "regex"), MiniJson.GetBool(o, "diacritics"));

        uint reqFlags = FieldsToFlags(MiniJson.GetStringList(o, "fields"));
        uint sort = ParseSort(MiniJson.GetString(o, "sort", null));

        byte[] raw = QueryRaw(search, max, offset, sort, flags, reqFlags, timeout,
            MiniJson.GetString(o, "target", null));
        List2 list = DecodeList2(raw, reqFlags);

        StringBuilder sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"search\":").Append(J(search));
        sb.Append(",\"total\":").Append(list.Total);
        sb.Append(",\"offset\":").Append(list.Offset);
        sb.Append(",\"returned\":").Append(list.Rows.Count);
        sb.Append(",\"results\":[");
        for (int i = 0; i < list.Rows.Count; i++)
        {
            Row r = list.Rows[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append("\"name\":").Append(J(r.Name));
            sb.Append(",\"path\":").Append(J(r.Path));
            sb.Append(",\"fullPath\":").Append(J(r.FullPath));
            sb.Append(",\"extension\":").Append(J(r.Extension));
            sb.Append(",\"isFolder\":").Append((r.Flags & FOLDER) != 0 ? "true" : "false");
            sb.Append(",\"isDrive\":").Append((r.Flags & DRIVE) != 0 ? "true" : "false");
            sb.Append(",\"size\":").Append(r.HasSize ? r.Size.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append(",\"dateCreated\":").Append(IsoFromTicks(r.DateCreated));
            sb.Append(",\"dateModified\":").Append(IsoFromTicks(r.DateModified));
            sb.Append(",\"dateAccessed\":").Append(IsoFromTicks(r.DateAccessed));
            sb.Append(",\"attributes\":").Append(r.HasAttrs ? J(AttributesToString(r.Attributes)) : "null");
            sb.Append(",\"runCount\":").Append(r.HasRunCount ? r.RunCount.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }
}

// ---- Minimal JSON reader (no external dependencies) ------------------------

internal static class MiniJson
{
    public static Dictionary<string, object> ParseObject(string s)
    {
        int i = 0;
        object v = ParseValue(s, ref i);
        Dictionary<string, object> d = v as Dictionary<string, object>;
        if (d == null) throw new ArgumentException("expected a JSON object");
        return d;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    private static object ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new ArgumentException("unexpected end of JSON");
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObj(s, ref i);
            case '[': return ParseArr(s, ref i);
            case '"': return ParseStr(s, ref i);
            case 't': Expect(s, ref i, "true"); return true;
            case 'f': Expect(s, ref i, "false"); return false;
            case 'n': Expect(s, ref i, "null"); return null;
            default: return ParseNum(s, ref i);
        }
    }

    private static void Expect(string s, ref int i, string lit)
    {
        if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
            throw new ArgumentException("invalid JSON literal at " + i);
        i += lit.Length;
    }

    private static Dictionary<string, object> ParseObj(string s, ref int i)
    {
        Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
        i++; // {
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return d; }
        while (true)
        {
            SkipWs(s, ref i);
            string k = ParseStr(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new ArgumentException("expected ':' at " + i);
            i++;
            d[k] = ParseValue(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length) throw new ArgumentException("unterminated object");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return d; }
            throw new ArgumentException("expected ',' or '}' at " + i);
        }
    }

    private static List<object> ParseArr(string s, ref int i)
    {
        List<object> a = new List<object>();
        i++; // [
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return a; }
        while (true)
        {
            a.Add(ParseValue(s, ref i));
            SkipWs(s, ref i);
            if (i >= s.Length) throw new ArgumentException("unterminated array");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return a; }
            throw new ArgumentException("expected ',' or ']' at " + i);
        }
    }

    private static string ParseStr(string s, ref int i)
    {
        if (i >= s.Length || s[i] != '"') throw new ArgumentException("expected string at " + i);
        i++;
        StringBuilder sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 <= s.Length)
                    {
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                    }
                    break;
                default: sb.Append(e); break;
            }
        }
        throw new ArgumentException("unterminated string");
    }

    private static object ParseNum(string s, ref int i)
    {
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                               s[i] == '-' || s[i] == '+')) i++;
        string t = s.Substring(start, i - start);
        double d;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            throw new ArgumentException("invalid number: " + t);
        return d;
    }

    public static string GetString(Dictionary<string, object> o, string key, string dflt)
    {
        object v;
        if (o.TryGetValue(key, out v) && v is string) return (string)v;
        return dflt;
    }

    public static bool GetBool(Dictionary<string, object> o, string key)
    {
        object v;
        if (o.TryGetValue(key, out v) && v is bool) return (bool)v;
        return false;
    }

    public static int GetInt(Dictionary<string, object> o, string key, int dflt)
    {
        object v;
        if (o.TryGetValue(key, out v))
        {
            if (v is double) return (int)(double)v;
            if (v is string)
            {
                int r;
                if (int.TryParse((string)v, out r)) return r;
            }
        }
        return dflt;
    }

    public static List<string> GetStringList(Dictionary<string, object> o, string key)
    {
        object v;
        if (!o.TryGetValue(key, out v)) return null;
        List<object> arr = v as List<object>;
        if (arr == null) return null;
        List<string> r = new List<string>();
        foreach (object item in arr)
        {
            if (item is string) r.Add((string)item);
        }
        return r;
    }
}
