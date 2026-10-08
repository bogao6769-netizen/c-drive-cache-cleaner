using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Threading;
[assembly: AssemblyTitle("C盘缓存清理器")]
[assembly: AssemblyDescription("先扫描、后确认的安全型 Windows 缓存清理工具")]
[assembly: AssemblyCompany("Local Utility")]
[assembly: AssemblyProduct("C盘缓存清理器")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace CDriveCacheCleaner
{
    internal enum CleanupMode
    {
        AllFiles,
        OlderFiles,
        MatchingFiles
    }

    internal sealed class CleanupRule
    {
        public string RootPath;
        public CleanupMode Mode;
        public int RetentionHours;
        public string Pattern;
        public string ExcludedTopLevelName;

        public CleanupRule(string rootPath, CleanupMode mode)
        {
            RootPath = rootPath;
            Mode = mode;
            Pattern = "*";
            ExcludedTopLevelName = String.Empty;
        }
    }

    internal sealed class CleanupTarget
    {
        public string Id;
        public string Group;
        public string Name;
        public string Description;
        public string RuleSummary;
        public bool Selected;
        public long SizeBytes;
        public string Status;
        public int CandidateCount;
        public int ProtectedCount;
        public int ScanErrors;
        public List<FileEntry> Entries = new List<FileEntry>();
        public readonly List<CleanupRule> Rules = new List<CleanupRule>();
    }

    internal sealed class CleanResult
    {
        public long FreeBefore;
        public long FreeAfter;
        public int DeletedFiles;
        public int SkippedItems;
        public string ErrorMessage;
        public long EstimatedBytes;
        public long DeletedBytes;
        public bool Cancelled;
        public readonly List<Outcome> Outcomes = new List<Outcome>();
        public string SummaryStatus
        {
            get { return Cancelled ? "已停止" : (!String.IsNullOrEmpty(ErrorMessage) ? "失败" :
                (SkippedItems > 0 ? "部分完成" : "完成")); }
        }

    }

    internal static class FormatUtil
    {
        public static string Bytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return (bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1024L * 1024L)
                return (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        public static string Json(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal sealed class SafetyPolicy
    {
        private readonly string _localAppData;
        private readonly string _userTemp;
        private readonly string _windowsTemp;
        private readonly string _testRoot;
        private readonly HashSet<string> _exactRoots;

        public SafetyPolicy() : this(null) { }

        public SafetyPolicy(string testRoot)
        {
            _localAppData = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            // Never trust an arbitrary TEMP environment override as a deletion root.
            _userTemp = Normalize(Path.Combine(_localAppData, "Temp"));
            _windowsTemp = Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
            _testRoot = String.IsNullOrEmpty(testRoot) ? null : Normalize(testRoot);
            _exactRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddExact(_userTemp);
            AddExact(Path.Combine(_userTemp, "odis_download_dest"));
            AddExact(_windowsTemp);
            AddExact(Path.Combine(_localAppData, "D3DSCache"));
            AddExact(Path.Combine(_localAppData, "NVIDIA", "DXCache"));
            AddExact(Path.Combine(_localAppData, "NVIDIA", "GLCache"));
            AddExact(Path.Combine(_localAppData, "CrashDumps"));
            AddExact(Path.Combine(_localAppData, "Microsoft", "Windows", "Explorer"));
            AddExact(Path.Combine(_localAppData, "Microsoft", "Windows", "WER", "ReportArchive"));
            AddExact(Path.Combine(_localAppData, "Microsoft", "Windows", "WER", "ReportQueue"));
            if (_testRoot != null) AddExact(_testRoot);
        }

        private void AddExact(string path)
        {
            _exactRoots.Add(Normalize(path));
        }

        public static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            return full.Length == Path.GetPathRoot(full).Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public bool IsApprovedRoot(string path)
        {
            string full;
            try { full = Normalize(path); }
            catch { return false; }

            if (_exactRoots.Contains(full)) return true;
            if (_testRoot != null && String.Equals(full, _testRoot, StringComparison.OrdinalIgnoreCase)) return true;

            string chrome = Normalize(Path.Combine(_localAppData, "Google", "Chrome", "User Data"));
            string edge = Normalize(Path.Combine(_localAppData, "Microsoft", "Edge", "User Data"));
            return IsApprovedBrowserCache(full, chrome) || IsApprovedBrowserCache(full, edge);
        }

        private static bool IsApprovedBrowserCache(string full, string browserBase)
        {
            string prefix = browserBase + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string relative = full.Substring(prefix.Length);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Length != 2) return false;
            bool profileOk = String.Equals(parts[0], "Default", StringComparison.OrdinalIgnoreCase) ||
                             Regex.IsMatch(parts[0], "^Profile [0-9]+$", RegexOptions.IgnoreCase);
            bool leafOk = String.Equals(parts[1], "Cache", StringComparison.OrdinalIgnoreCase) ||
                          String.Equals(parts[1], "Code Cache", StringComparison.OrdinalIgnoreCase) ||
                          String.Equals(parts[1], "GPUCache", StringComparison.OrdinalIgnoreCase);
            return profileOk && leafOk;
        }

        public bool IsSafeDescendant(string root, string candidate)
        {
            string normalizedRoot;
            string normalizedCandidate;
            try
            {
                normalizedRoot = Normalize(root);
                normalizedCandidate = Normalize(candidate);
            }
            catch { return false; }

            return IsApprovedRoot(normalizedRoot) &&
                   normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        public bool HasSafeChain(string path)
        {
            try
            {
                string current = Normalize(path);
                while (!String.IsNullOrEmpty(current))
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    DirectoryInfo parent = Directory.GetParent(current);
                    current = parent == null ? null : parent.FullName;
                }
                return true;
            }
            catch { return false; }
        }
    }

    internal static class CleanerCatalog
    {
        public static List<CleanupTarget> Build(int retentionHours)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string temp = Path.Combine(local, "Temp");
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            List<CleanupTarget> targets = new List<CleanupTarget>();

            CleanupTarget odis = NewTarget("odis", "安装下载", "Adobe ODIS 旧下载包",
                "Adobe 安装器反复下载后留下的旧包。保留设定时间内的新包。",
                "仅清理早于保留时间的文件", true);
            odis.Rules.Add(Older(Path.Combine(temp, "odis_download_dest"), retentionHours, null));
            targets.Add(odis);

            CleanupTarget userTemp = NewTarget("user-temp", "临时文件", "用户临时文件",
                "应用运行过程中产生的临时文件。ODIS 目录单独计算，避免重复。",
                "保留最近使用的文件", true);
            userTemp.Rules.Add(Older(temp, retentionHours, "odis_download_dest"));
            targets.Add(userTemp);

            CleanupTarget winTemp = NewTarget("windows-temp", "临时文件", "Windows 临时文件",
                "系统与安装程序产生的临时内容；无权限或正在占用的项目会跳过。",
                "保留最近使用的文件", true);
            winTemp.Rules.Add(Older(Path.Combine(windows, "Temp"), retentionHours, null));
            targets.Add(winTemp);

            CleanupTarget chrome = NewTarget("chrome", "浏览器", "Chrome 缓存",
                "网页资源、代码与 GPU 缓存。不会删除书签、密码、历史记录或 Cookie。",
                "关闭浏览器后清理更彻底", true);
            AddBrowserRules(chrome, Path.Combine(local, "Google", "Chrome", "User Data"));
            targets.Add(chrome);

            CleanupTarget edge = NewTarget("edge", "浏览器", "Edge 缓存",
                "网页资源、代码与 GPU 缓存。不会删除收藏夹、密码、历史记录或 Cookie。",
                "关闭浏览器后清理更彻底", true);
            AddBrowserRules(edge, Path.Combine(local, "Microsoft", "Edge", "User Data"));
            targets.Add(edge);

            CleanupTarget shader = NewTarget("shader", "图形缓存", "DirectX / NVIDIA 着色器",
                "显卡驱动编译生成的着色器缓存，游戏和设计软件会按需重建。",
                "应用会按需重建", true);
            shader.Rules.Add(All(Path.Combine(local, "D3DSCache")));
            shader.Rules.Add(All(Path.Combine(local, "NVIDIA", "DXCache")));
            shader.Rules.Add(All(Path.Combine(local, "NVIDIA", "GLCache")));
            targets.Add(shader);

            CleanupTarget crash = NewTarget("crash", "诊断文件", "崩溃转储与错误报告",
                "原始崩溃信息不能重新生成，诊断文件默认保留。",
                "诊断记录，默认不清理", false);
            crash.Rules.Add(All(Path.Combine(local, "CrashDumps")));
            crash.Rules.Add(Older(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"), retentionHours, null));
            crash.Rules.Add(Older(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"), retentionHours, null));
            targets.Add(crash);

            CleanupTarget thumbs = NewTarget("thumbs", "系统缓存", "资源管理器缩略图",
                "图片、视频和文档预览缩略图。再次浏览文件夹时会自动重建。",
                "只匹配 thumbcache_*", true);
            CleanupRule thumbRule = new CleanupRule(Path.Combine(local, "Microsoft", "Windows", "Explorer"), CleanupMode.MatchingFiles);
            thumbRule.Pattern = "thumbcache_*";
            thumbs.Rules.Add(thumbRule);
            targets.Add(thumbs);

            return targets;
        }

        private static CleanupTarget NewTarget(string id, string group, string name, string description, string ruleSummary, bool selected)
        {
            CleanupTarget target = new CleanupTarget();
            target.Id = id;
            target.Group = group;
            target.Name = name;
            target.Description = description;
            target.RuleSummary = ruleSummary;
            target.Selected = selected;
            target.Status = "等待扫描";
            return target;
        }

        private static CleanupRule Older(string path, int hours, string excludedTopLevelName)
        {
            CleanupRule rule = new CleanupRule(path, CleanupMode.OlderFiles);
            rule.RetentionHours = hours;
            rule.ExcludedTopLevelName = excludedTopLevelName ?? String.Empty;
            return rule;
        }

        private static CleanupRule All(string path)
        {
            return new CleanupRule(path, CleanupMode.AllFiles);
        }

        private static void AddBrowserRules(CleanupTarget target, string userDataPath)
        {
            List<string> profiles = new List<string>();
            string defaultProfile = Path.Combine(userDataPath, "Default");
            if (Directory.Exists(defaultProfile)) profiles.Add(defaultProfile);
            try
            {
                if (Directory.Exists(userDataPath))
                {
                    foreach (string profile in Directory.GetDirectories(userDataPath, "Profile *", SearchOption.TopDirectoryOnly))
                    {
                        if (Regex.IsMatch(Path.GetFileName(profile), "^Profile [0-9]+$", RegexOptions.IgnoreCase))
                            profiles.Add(profile);
                    }
                }
            }
            catch { }

            foreach (string profile in profiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                target.Rules.Add(All(Path.Combine(profile, "Cache")));
                target.Rules.Add(All(Path.Combine(profile, "Code Cache")));
                target.Rules.Add(All(Path.Combine(profile, "GPUCache")));
            }
        }
    }


    internal static class AppLog
    {
        private static readonly object Gate = new object();

        public static string LogDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CDriveCacheCleaner", "Logs");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    string path = Path.Combine(LogDirectory, "cleaner-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    internal sealed class DiskMeterControl : Control
    {
        public long TotalBytes;
        public long FreeBytes;
        public long SelectedBytes;

        public DiskMeterControl()
        {
            DoubleBuffered = true;
            Height = 92;
            BackColor = Color.FromArgb(26, 26, 26);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int left = 28;
            int right = Width - 28;
            int barTop = 47;
            int barHeight = 15;
            int barWidth = Math.Max(20, right - left);
            double total = Math.Max(1d, TotalBytes);
            double usedRatio = Math.Max(0d, Math.Min(1d, (TotalBytes - FreeBytes) / total));
            double selectedRatio = Math.Max(0d, Math.Min(usedRatio, SelectedBytes / total));

            using (Font labelFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular))
            using (Font dataFont = new Font("Cascadia Mono", 10f, FontStyle.Bold))
            using (Brush ink = new SolidBrush(Color.FromArgb(245, 245, 242)))
            using (Brush muted = new SolidBrush(Color.FromArgb(145, 145, 140)))
            {
                g.DrawString("C: 磁盘刻度", labelFont, muted, left, 13);
                string rightLabel = FormatUtil.Bytes(FreeBytes) + " 可用";
                SizeF rightSize = g.MeasureString(rightLabel, dataFont);
                g.DrawString(rightLabel, dataFont, ink, right - rightSize.Width, 11);
            }

            using (GraphicsPath path = RoundedRectangle(new Rectangle(left, barTop, barWidth, barHeight), 7))
            using (Brush freeBrush = new SolidBrush(Color.FromArgb(75, 75, 72)))
            {
                g.FillPath(freeBrush, path);
            }

            int usedWidth = (int)Math.Round(barWidth * usedRatio);
            if (usedWidth > 0)
            {
                using (Region clip = new Region(RoundedRectangle(new Rectangle(left, barTop, barWidth, barHeight), 7)))
                using (Brush usedBrush = new SolidBrush(Color.FromArgb(154, 154, 150)))
                {
                    Region oldClip = g.Clip;
                    g.Clip = clip;
                    g.FillRectangle(usedBrush, left, barTop, usedWidth, barHeight);
                    g.Clip = oldClip;
                }
            }

            int selectedWidth = (int)Math.Round(barWidth * selectedRatio);
            if (selectedWidth > 1)
            {
                using (Pen selectedPen = new Pen(Color.FromArgb(112, 112, 108), 4f))
                {
                    selectedPen.StartCap = LineCap.Round;
                    selectedPen.EndCap = LineCap.Round;
                    int end = left + usedWidth;
                    g.DrawLine(selectedPen, Math.Max(left, end - selectedWidth), barTop + barHeight + 8, end, barTop + barHeight + 8);
                }
            }

            using (Pen tickPen = new Pen(Color.FromArgb(94, 94, 91), 1f))
            {
                for (int i = 0; i <= 10; i++)
                {
                    int x = left + (barWidth * i / 10);
                    g.DrawLine(tickPen, x, barTop + barHeight + 15, x, barTop + barHeight + (i % 5 == 0 ? 22 : 19));
                }
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Color Ink = Color.FromArgb(21, 21, 21);
        private readonly Color Teal = Color.FromArgb(184, 184, 178);
        private readonly Color Pale = Color.FromArgb(30, 30, 30);
        private readonly Color Muted = Color.FromArgb(156, 156, 151);
        private readonly Color Amber = Color.FromArgb(99, 99, 96);
        private readonly Color Highlight = Color.FromArgb(245, 245, 242);
        private DataGridView _grid;
        private DiskMeterControl _meter;
        private Label _reclaimValue;
        private Label _selectedValue;
        private Label _boundaryValue;
        private Label _status;
        private Label _lastRun;
        private Button _scanButton;
        private Button _cleanButton;
        private ComboBox _retention;
        private Panel _progress;
        private List<CleanupTarget> _targets = new List<CleanupTarget>();
        private bool _busy;
        private CancellationTokenSource _cancellation;
        private bool _closeWhenIdle;
        private Button _stopButton;
        private Button _detailsButton;
        private Button _exportButton;
        private CleanResult _lastResult;

        public MainForm()
        {
            Text = "C盘缓存清理器 · 1.1.0";
            MinimumSize = new Size(980, 720);
            Size = new Size(1200, 840);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ink;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = CreateAppIcon();
            BuildUi();
            Shown += delegate { BeginScan(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (_busy) { e.Cancel = true; _closeWhenIdle = true; StopOperation(); }
            };
        }

        private void BuildUi()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 94;
            header.BackColor = Ink;

            Label title = new Label();
            title.AutoSize = true;
            title.Text = "C 盘缓存清理器";
            title.ForeColor = Highlight;
            title.Font = new Font("Microsoft YaHei UI", 22f, FontStyle.Bold);
            title.Location = new Point(28, 17);
            header.Controls.Add(title);

            Label badge = new Label();
            badge.AutoSize = false;
            badge.Size = new Size(118, 29);
            badge.TextAlign = ContentAlignment.MiddleCenter;
            badge.Text = "确认后清理";
            badge.ForeColor = Teal;
            badge.BackColor = Color.FromArgb(61, 61, 59);
            badge.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            badge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            badge.Location = new Point(ClientSize.Width - 146, 29);
            header.Controls.Add(badge);
            header.Resize += delegate { badge.Left = header.ClientSize.Width - badge.Width - 28; };
            Controls.Add(header);

            _meter = new DiskMeterControl();
            _meter.Dock = DockStyle.Top;
            Controls.Add(_meter);

            Panel summary = new Panel();
            summary.Dock = DockStyle.Top;
            summary.Height = 86;
            summary.BackColor = Color.FromArgb(27, 27, 27);
            TableLayoutPanel cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Fill;
            cards.Padding = new Padding(24, 10, 24, 10);
            cards.ColumnCount = 3;
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
            _reclaimValue = AddSummaryCard(cards, 0, "扫描可释放", "—", Teal);
            _selectedValue = AddSummaryCard(cards, 1, "本次已选择", "—", Amber);
            _boundaryValue = AddSummaryCard(cards, 2, "临时文件策略", "保留最近 24 小时", Color.FromArgb(83, 83, 80));
            _reclaimValue.ForeColor = Highlight;
            _selectedValue.ForeColor = Highlight;
            _boundaryValue.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            summary.Controls.Add(cards);
            Controls.Add(summary);

            Panel actions = new Panel();
            actions.Dock = DockStyle.Bottom;
            actions.Height = 76;
            actions.BackColor = Color.FromArgb(24, 24, 24);
            actions.Padding = new Padding(26, 15, 26, 14);

            _status = new Label();
            _status.AutoSize = false;
            _status.Text = "准备扫描";
            _status.ForeColor = Muted;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Dock = DockStyle.Fill;
            actions.Controls.Add(_status);

            _progress = new Panel();
            _progress.BackColor = Color.FromArgb(126, 126, 121);
            _progress.Size = new Size(120, 5);
            _progress.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _progress.Location = new Point(28, 57);
            _progress.Visible = false;
            actions.Controls.Add(_progress);

            _cleanButton = MakeButton("清理所选缓存", Color.FromArgb(59, 59, 57), Highlight, 142);
            _cleanButton.FlatAppearance.BorderColor = Color.FromArgb(116, 116, 112);
            _cleanButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(76, 76, 73);
            _cleanButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(48, 48, 46);
            _cleanButton.Dock = DockStyle.Right;
            _cleanButton.Click += delegate { BeginClean(); };
            actions.Controls.Add(_cleanButton);

            Panel buttonGap = new Panel();
            buttonGap.Width = 10;
            buttonGap.Dock = DockStyle.Right;
            actions.Controls.Add(buttonGap);

            _scanButton = MakeButton("重新扫描", Pale, Teal, 112);
            _scanButton.Dock = DockStyle.Right;
            _scanButton.Click += delegate { BeginScan(); };
            actions.Controls.Add(_scanButton);
            _stopButton = MakeButton("停止操作", Pale, Teal, 104);
            _stopButton.Dock = DockStyle.Right;
            _stopButton.Enabled = false;
            _stopButton.Click += delegate { StopOperation(); };
            actions.Controls.Add(_stopButton);
            Controls.Add(actions);

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(24, 18, 24, 18);
            content.BackColor = Ink;

            Panel safety = new Panel();
            safety.Dock = DockStyle.Right;
            safety.Width = 244;
            safety.BackColor = Pale;
            safety.Padding = new Padding(0);
            safety.AutoScroll = true;

            Label safetyTitle = new Label();
            safetyTitle.AutoSize = true;
            safetyTitle.Text = "清理规则";
            safetyTitle.ForeColor = Teal;
            safetyTitle.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            safetyTitle.Location = new Point(20, 21);
            safety.Controls.Add(safetyTitle);

            Label safetyCopy = new Label();
            safetyCopy.AutoSize = false;
            safetyCopy.Text = "✓ 确认扫描清单后清理\r\n✓ 保留近期临时文件\r\n✓ 跳过占用及链接文件\r\n✓ 诊断文件默认保留\r\n✓ 清理前再次确认";
            safetyCopy.ForeColor = Muted;
            safetyCopy.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            safetyCopy.Location = new Point(20, 62);
            safetyCopy.Size = new Size(204, 90);
            safety.Controls.Add(safetyCopy);

            Label retentionLabel = new Label();
            retentionLabel.AutoSize = true;
            retentionLabel.Text = "临时文件保留时间";
            retentionLabel.ForeColor = Teal;
            retentionLabel.Location = new Point(20, 158);
            safety.Controls.Add(retentionLabel);

            _retention = new ComboBox();
            _retention.DropDownStyle = ComboBoxStyle.DropDownList;
            _retention.FlatStyle = FlatStyle.Flat;
            _retention.DrawMode = DrawMode.OwnerDrawFixed;
            _retention.BackColor = Color.FromArgb(43, 43, 43);
            _retention.ForeColor = Teal;
            _retention.Items.AddRange(new object[] { "24 小时", "72 小时", "7 天" });
            _retention.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                using (Brush background = new SolidBrush(Color.FromArgb(43, 43, 43))) e.Graphics.FillRectangle(background, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, _retention.Items[e.Index].ToString(), Font, e.Bounds, Teal, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            };
            _retention.SelectedIndex = 0;
            _retention.Location = new Point(20, 182);
            _retention.Width = 196;
            _retention.SelectedIndexChanged += delegate { UpdateRetentionCard(); };
            safety.Controls.Add(_retention);

            _lastRun = new Label();
            _lastRun.AutoSize = false;
            _lastRun.Text = "尚未执行清理";
            _lastRun.ForeColor = Muted;
            _lastRun.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            _lastRun.Location = new Point(20, 218);
            _lastRun.Size = new Size(198, 48);
            safety.Controls.Add(_lastRun);
            _detailsButton = MakeButton("查看选中项明细", Pale, Teal, 196);
            _detailsButton.Location = new Point(20, 270);
            _detailsButton.Click += delegate { ShowDetails(); };
            safety.Controls.Add(_detailsButton);
            _exportButton = MakeButton("导出上次结果", Pale, Teal, 196);
            _exportButton.Location = new Point(20, 320);
            _exportButton.Enabled = false;
            _exportButton.Click += delegate { ExportResult(); };
            safety.Controls.Add(_exportButton);
            Button feedback = MakeButton("反馈问题", Pale, Teal, 196);
            feedback.Location = new Point(20, 370);
            feedback.Click += delegate { Process.Start(new ProcessStartInfo("https://github.com/bogao6769-netizen/c-drive-cache-cleaner/issues/new/choose") { UseShellExecute = true }); };
            safety.Controls.Add(feedback);
            content.Controls.Add(safety);

            Panel gridHost = new Panel();
            gridHost.Dock = DockStyle.Fill;
            gridHost.Padding = new Padding(0, 0, 18, 0);
            _grid = CreateGrid();
            gridHost.Controls.Add(_grid);
            content.Controls.Add(gridHost);
            content.Controls.SetChildIndex(gridHost, 0);
            content.Controls.SetChildIndex(safety, 1);
            Controls.Add(content);

            // WinForms docks controls from the back of the collection. Keep the
            // fill panel at index zero so fixed top/bottom regions reserve space.
            Controls.SetChildIndex(content, 0);
            Controls.SetChildIndex(actions, 1);
            Controls.SetChildIndex(summary, 2);
            Controls.SetChildIndex(_meter, 3);
            Controls.SetChildIndex(header, 4);

            UpdateDriveMeter();
        }

        private Label AddSummaryCard(TableLayoutPanel parent, int column, string caption, string value, Color accent)
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(16, 8, 10, 5);
            panel.Margin = new Padding(column == 0 ? 0 : 8, 0, column == 2 ? 0 : 8, 0);
            Panel line = new Panel();
            line.BackColor = accent;
            line.Width = 4;
            line.Dock = DockStyle.Left;
            panel.Controls.Add(line);
            Label captionLabel = new Label();
            captionLabel.Text = caption;
            captionLabel.ForeColor = Muted;
            captionLabel.AutoSize = true;
            captionLabel.Location = new Point(17, 7);
            panel.Controls.Add(captionLabel);
            Label valueLabel = new Label();
            valueLabel.Text = value;
            valueLabel.ForeColor = Teal;
            valueLabel.Font = new Font("Cascadia Mono", 13f, FontStyle.Bold);
            valueLabel.AutoSize = true;
            valueLabel.Location = new Point(16, 29);
            panel.Controls.Add(valueLabel);
            parent.Controls.Add(panel, column, 0);
            return valueLabel;
        }

        private DataGridView CreateGrid()
        {
            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.FromArgb(21, 21, 21);
            grid.BorderStyle = BorderStyle.None;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoGenerateColumns = false;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeight = 38;
            grid.RowTemplate.Height = 46;
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Ink;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Teal;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Ink;
            grid.DefaultCellStyle.BackColor = Color.FromArgb(29, 29, 29);
            grid.DefaultCellStyle.ForeColor = Teal;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(59, 59, 57);
            grid.DefaultCellStyle.SelectionForeColor = Teal;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(34, 34, 34);
            grid.GridColor = Color.FromArgb(59, 59, 57);

            DataGridViewCheckBoxColumn selected = new DataGridViewCheckBoxColumn();
            selected.Name = "Selected";
            selected.HeaderText = "选";
            selected.Width = 54;
            selected.FlatStyle = FlatStyle.Standard;
            grid.Columns.Add(selected);
            grid.Columns.Add(TextColumn("Group", "类别", 80));
            grid.Columns.Add(TextColumn("Name", "缓存项目", 155));
            DataGridViewTextBoxColumn rule = TextColumn("Rule", "处理规则", 190);
            grid.Columns["Name"].MinimumWidth = 140;
            grid.Columns["Name"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns["Name"].FillWeight = 42;
            rule.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            rule.MinimumWidth = 150;
            rule.FillWeight = 58;
            grid.Columns.Add(rule);
            DataGridViewTextBoxColumn size = TextColumn("Size", "可释放", 102);
            size.DefaultCellStyle.Font = new Font("Cascadia Mono", 9f, FontStyle.Bold);
            size.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns.Add(size);
            grid.Columns.Add(TextColumn("Status", "状态", 82));

            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += GridCellValueChanged;
            grid.CellFormatting += GridCellFormatting;
            grid.CellPainting += GridCellPainting;
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) ShowDetails(); };
            grid.CellMouseEnter += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.RowIndex < _targets.Count)
                {
                    foreach (DataGridViewCell cell in grid.Rows[e.RowIndex].Cells)
                        cell.ToolTipText = _targets[e.RowIndex].Description;
                }
            };
            return grid;
        }

        private static DataGridViewTextBoxColumn TextColumn(string name, string header, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.Name = name;
            column.HeaderText = header;
            column.Width = width;
            column.ReadOnly = true;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            return column;
        }

        private Button MakeButton(string text, Color back, Color fore, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 44;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = back == Pale ? Color.FromArgb(78, 78, 75) : back;
            bool lightButton = fore == Ink;
            button.FlatAppearance.MouseOverBackColor = lightButton ? Color.FromArgb(145, 145, 140) : Color.FromArgb(48, 48, 47);
            button.FlatAppearance.MouseDownBackColor = lightButton ? Color.FromArgb(106, 106, 102) : Color.FromArgb(62, 62, 60);
            button.BackColor = back;
            button.ForeColor = fore;
            button.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private int RetentionHours
        {
            get
            {
                if (_retention.SelectedIndex == 1) return 72;
                if (_retention.SelectedIndex == 2) return 168;
                return 24;
            }
        }

        private void UpdateRetentionCard()
        {
            if (_busy) return;
            if (_boundaryValue != null)
                _boundaryValue.Text = RetentionHours == 24 ? "保留最近 24 小时" :
                                      (RetentionHours == 72 ? "保留最近 72 小时" : "保留最近 7 天");
            BeginScan();
        }

        private void BeginScan()
        {
            if (_busy) return;
            SetBusy(true, "正在扫描已批准的缓存目录…");
            int hours = RetentionHours;
            Dictionary<string, bool> previous = _targets.ToDictionary(t => t.Id, t => t.Selected);
            _targets = new List<CleanupTarget>(); BindTargets();
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                List<CleanupTarget> targets = CleanerCatalog.Build(hours);
                CleanerEngine engine = new CleanerEngine();
                engine.Scan(targets, null, token);
                foreach (CleanupTarget target in targets)
                    if (previous.ContainsKey(target.Id)) target.Selected = previous[target.Id];
                e.Result = targets;
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                if (e.Error != null)
                {
                    SetBusy(false, e.Error is OperationCanceledException ? "扫描已停止，请重新扫描" : "扫描失败：" + e.Error.Message);
                    EndOperation();
                    AppLog.Write("扫描失败：" + e.Error);
                    return;
                }
                _targets = (List<CleanupTarget>)e.Result;
                BindTargets();
                SetBusy(false, "扫描完成 · 勾选项目后开始清理");
                EndOperation();
                UpdateDriveMeter();
                AppLog.Write("扫描完成，可释放 " + FormatUtil.Bytes(_targets.Sum(t => t.SizeBytes)));
            };
            worker.RunWorkerAsync();
        }

        private void BindTargets()
        {
            _grid.Rows.Clear();
            foreach (CleanupTarget target in _targets)
            {
                int row = _grid.Rows.Add(target.Selected, target.Group, target.Name, target.RuleSummary,
                    FormatUtil.Bytes(target.SizeBytes), target.Status);
                _grid.Rows[row].Tag = target;
            }
            RecalculateSelection();
        }

        private void GridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Selected"].Index || e.RowIndex >= _targets.Count) return;
            object value = _grid.Rows[e.RowIndex].Cells["Selected"].Value;
            _targets[e.RowIndex].Selected = value != null && Convert.ToBoolean(value);
            RecalculateSelection();
        }

        private void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Status") return;
            string status = Convert.ToString(e.Value);
            e.CellStyle.ForeColor = status == "可清理" ? Highlight : Muted;
            e.CellStyle.Font = new Font("Microsoft YaHei UI", 8.5f, status == "可清理" ? FontStyle.Bold : FontStyle.Regular);
        }

        private void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Selected") return;
            e.PaintBackground(e.ClipBounds, true);
            bool isChecked = false;
            object raw = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            if (raw != null) isChecked = Convert.ToBoolean(raw);
            int boxSize = 17;
            Rectangle box = new Rectangle(e.CellBounds.Left + (e.CellBounds.Width - boxSize) / 2,
                e.CellBounds.Top + (e.CellBounds.Height - boxSize) / 2, boxSize, boxSize);
            using (Brush fill = new SolidBrush(isChecked ? Color.FromArgb(142, 142, 137) : Color.FromArgb(29, 29, 29)))
            using (Pen border = new Pen(isChecked ? Color.FromArgb(142, 142, 137) : Color.FromArgb(108, 108, 104), 1f))
            {
                e.Graphics.FillRectangle(fill, box);
                e.Graphics.DrawRectangle(border, box);
            }
            if (isChecked)
            {
                using (Pen check = new Pen(Ink, 2.2f))
                {
                    check.StartCap = LineCap.Round;
                    check.EndCap = LineCap.Round;
                    e.Graphics.DrawLine(check, box.Left + 4, box.Top + 9, box.Left + 7, box.Top + 12);
                    e.Graphics.DrawLine(check, box.Left + 7, box.Top + 12, box.Left + 13, box.Top + 5);
                }
            }
            e.Handled = true;
        }

        private void RecalculateSelection()
        {
            long total = _targets.Sum(t => t.SizeBytes);
            long selected = _targets.Where(t => t.Selected).Sum(t => t.SizeBytes);
            _reclaimValue.Text = FormatUtil.Bytes(total);
            _selectedValue.Text = FormatUtil.Bytes(selected);
            _cleanButton.Enabled = !_busy && selected > 0;
            _meter.SelectedBytes = selected;
            _meter.Invalidate();
        }

        private void BeginClean()
        {
            if (_busy) return;
            List<CleanupTarget> selected = _targets.Where(t => t.Selected && t.SizeBytes > 0).ToList();
            long estimated = selected.Sum(t => t.SizeBytes);
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "当前没有可清理的已选项目。", "无需清理", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string names = String.Join("、", selected.Select(t => t.Name).ToArray());
            DialogResult confirmation = MessageBox.Show(this,
                "即将清理 " + selected.Count.ToString(CultureInfo.InvariantCulture) + " 项缓存，预计 " + FormatUtil.Bytes(estimated) + "。\r\n\r\n" +
                names + "\r\n\r\n缓存不会进入回收站；占用、无权限或链接项目会自动跳过。",
                "确认清理所选缓存", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.OK) return;

            SetBusy(true, "正在清理所选缓存…");
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                CleanerEngine engine = new CleanerEngine();
                e.Result = engine.Clean(selected, delegate(string text) { AppLog.Write(text); }, token);
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                if (e.Error != null)
                {
                    SetBusy(false, "清理失败：" + e.Error.Message);
                    EndOperation();
                    AppLog.Write("清理失败：" + e.Error);
                    return;
                }
                CleanResult result = (CleanResult)e.Result;
                _lastResult = result;
                _exportButton.Enabled = true;
                _lastRun.Text = "上次清理  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n删除文件容量 " + FormatUtil.Bytes(result.DeletedBytes) +
                                " · 跳过 " + result.SkippedItems.ToString(CultureInfo.InvariantCulture) + " 项\r\n结果：" + result.SummaryStatus;
                AppLog.Write(result.SummaryStatus + "，删除文件容量 " + FormatUtil.Bytes(result.DeletedBytes) + "，删除文件 " + result.DeletedFiles + "，跳过 " + result.SkippedItems);
                if (!_closeWhenIdle) MessageBox.Show(this,
                    "预计容量：" + FormatUtil.Bytes(result.EstimatedBytes) + "\r\n已删除文件容量：" + FormatUtil.Bytes(result.DeletedBytes) +
                    "\r\nC盘可用空间变化：" + (result.FreeAfter >= result.FreeBefore ? "+" : "-") + FormatUtil.Bytes(Math.Abs(result.FreeAfter - result.FreeBefore)) +
                    "（受其他程序写入影响）\r\n删除 " + result.DeletedFiles + " 个文件，跳过 " + result.SkippedItems + " 项。\r\n" + result.ErrorMessage,
                    "清理结果：" + result.SummaryStatus, MessageBoxButtons.OK, String.IsNullOrEmpty(result.ErrorMessage) ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                SetBusy(false, "处理结束，正在复核…");
                EndOperation();
                if (_closeWhenIdle) return;
                BeginScan();
            };
            worker.RunWorkerAsync();
        }

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            _status.Text = text;
            _progress.Visible = busy;
            _scanButton.Enabled = !busy;
            _retention.Enabled = !busy;
            _grid.Enabled = !busy;
            _cleanButton.Enabled = !busy && _targets.Any(t => t.Selected && t.SizeBytes > 0);
            if (_stopButton != null) _stopButton.Enabled = busy;
            if (_detailsButton != null) _detailsButton.Enabled = !busy && _targets.Count > 0;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void StopOperation()
        {
            if (_cancellation != null) _cancellation.Cancel();
            _stopButton.Enabled = false;
            _status.Text = "正在停止，将保留已完成结果…";
        }

        private void EndOperation()
        {
            if (_cancellation != null) { _cancellation.Dispose(); _cancellation = null; }
            if (_closeWhenIdle) BeginInvoke(new Action(Close));
        }

        private void ExportResult()
        {
            if (_lastResult == null) return;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV 报告|*.csv", FileName = "清理结果-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { Report.Export(_lastResult, dialog.FileName); _status.Text = "结果已导出，用户目录已替换为 %USERPROFILE%"; }
                catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message); }
            }
        }

        private void ShowDetails()
        {
            if (_busy || _grid.CurrentRow == null) return;
            CleanupTarget target = _grid.CurrentRow.Tag as CleanupTarget;
            if (target == null) return;
            using (Form dialog = new Form())
            {
                dialog.Text = target.Name + " · 扫描明细"; dialog.Size = new Size(1100, 700);
                dialog.StartPosition = FormStartPosition.CenterParent; dialog.BackColor = Pale; dialog.ForeColor = Teal; dialog.Font = Font;
                DataGridView table = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                    RowHeadersVisible = false, BackgroundColor = Ink, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    EnableHeadersVisualStyles = false, BorderStyle = BorderStyle.None };
                table.ColumnHeadersDefaultCellStyle.BackColor = Pale; table.ColumnHeadersDefaultCellStyle.ForeColor = Highlight;
                table.DefaultCellStyle.BackColor = Pale; table.DefaultCellStyle.ForeColor = Teal;
                table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(59, 59, 57); table.DefaultCellStyle.SelectionForeColor = Highlight;
                table.Columns.Add("Path", "完整路径"); table.Columns.Add("Size", "大小"); table.Columns.Add("Decision", "处理规则 / 保留原因");
                table.Columns[0].FillWeight = 65; table.Columns[1].FillWeight = 12; table.Columns[2].FillWeight = 23;
                foreach (FileEntry entry in target.Entries.Take(5000)) table.Rows.Add(entry.Path, FormatUtil.Bytes(entry.Bytes), entry.Decision);
                Label note = new Label { Dock = DockStyle.Top, Height = 62, Padding = new Padding(14), Text = target.Description + "\r\n待清理 " + target.CandidateCount +
                    " 个 · 保留 " + target.ProtectedCount + " 项 · 读取问题 " + target.ScanErrors + " 项。界面最多展示 5000 条，可导出全部明细。" };
                Button export = MakeButton("导出全部明细", Pale, Highlight, 160); export.Dock = DockStyle.Bottom;
                export.Click += delegate
                {
                    using (SaveFileDialog save = new SaveFileDialog { Filter = "CSV 明细|*.csv", FileName = "扫描明细.csv" })
                    {
                        if (save.ShowDialog(dialog) != DialogResult.OK) return;
                        try
                        {
                            StringBuilder csv = new StringBuilder("路径,字节,处理规则\r\n");
                            foreach (FileEntry entry in target.Entries) csv.AppendLine(Report.Csv(Report.Redact(entry.Path)) + "," + entry.Bytes + "," + Report.Csv(entry.Decision));
                            File.WriteAllText(save.FileName, csv.ToString(), new UTF8Encoding(true));
                        }
                        catch (Exception ex) { MessageBox.Show(dialog, "导出失败：" + ex.Message); }
                    }
                };
                dialog.Controls.Add(table); dialog.Controls.Add(note); dialog.Controls.Add(export);
                dialog.ShowDialog(this);
            }
        }

        private void UpdateDriveMeter()
        {
            try
            {
                DriveInfo drive = new DriveInfo("C");
                _meter.TotalBytes = drive.TotalSize;
                _meter.FreeBytes = drive.AvailableFreeSpace;
            }
            catch
            {
                _meter.TotalBytes = 1;
                _meter.FreeBytes = 0;
            }
            _meter.Invalidate();
        }

        private static Icon CreateAppIcon()
        {
            Bitmap bitmap = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush back = new SolidBrush(Color.FromArgb(21, 21, 21))) g.FillEllipse(back, 1, 1, 30, 30);
                using (Pen ring = new Pen(Color.FromArgb(178, 178, 172), 3f)) g.DrawArc(ring, 6, 6, 20, 20, -75, 250);
                using (Brush accent = new SolidBrush(Color.FromArgb(119, 119, 115))) g.FillEllipse(accent, 22, 5, 5, 5);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 2 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                return CleanerEngine.RunSelfTest(Path.GetFullPath(args[1])) ? 0 : 1;

            if (args.Length >= 2 && String.Equals(args[0], "--scan-report", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    List<CleanupTarget> targets = CleanerCatalog.Build(24);
                    new CleanerEngine().Scan(targets, null);
                    StringBuilder json = new StringBuilder();
                    json.Append("{\r\n  \"generatedAt\": \"").Append(DateTime.Now.ToString("s")).Append("\",\r\n  \"targets\": [\r\n");
                    for (int i = 0; i < targets.Count; i++)
                    {
                        CleanupTarget target = targets[i];
                        json.Append("    { \"id\": \"").Append(FormatUtil.Json(target.Id)).Append("\", \"name\": \"")
                            .Append(FormatUtil.Json(target.Name)).Append("\", \"bytes\": ").Append(target.SizeBytes.ToString(CultureInfo.InvariantCulture))
                            .Append(", \"status\": \"").Append(FormatUtil.Json(target.Status)).Append("\" }");
                        if (i < targets.Count - 1) json.Append(",");
                        json.Append("\r\n");
                    }
                    json.Append("  ]\r\n}");
                    File.WriteAllText(Path.GetFullPath(args[1]), json.ToString(), new UTF8Encoding(false));
                    return 0;
                }
                catch { return 2; }
            }

            try { SetProcessDPIAware(); }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
