using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CodexResetRadar
{
    internal static class Program
    {
        private const string MutexName = "Local\\CodexResetRadar.SingleInstance";
        internal static readonly int ShowMessage = NativeMethods.RegisterWindowMessage("CodexResetRadar.ShowWindow.v1");

        [STAThread]
        private static void Main(string[] args)
        {
            bool selfTest = HasArg(args, "--self-test");
            bool checkNow = HasArg(args, "--check-now");
            bool background = HasArg(args, "--background");

            if (HasArg(args, "--apply-update"))
            {
                Environment.ExitCode = ReleaseUpdater.ApplyStagedUpdate(args);
                return;
            }

            if (selfTest)
            {
                AttachConsole();
                Environment.ExitCode = SelfTests.Run();
                return;
            }

            if (checkNow)
            {
                AttachConsole();
                try
                {
                    RadarSnapshot snapshot = RadarClient.FetchAsync(DateTimeOffset.UtcNow).GetAwaiter().GetResult();
                    Console.WriteLine("STATUS=" + snapshot.StatusTitle);
                    Console.WriteLine("COUNTDOWN=" + snapshot.GetCountdownText(DateTimeOffset.UtcNow));
                    Console.WriteLine("SOURCE=" + snapshot.SourceLabel);
                    Console.WriteLine("URL=" + snapshot.SourceUrl);
                    Console.WriteLine("RECENT_POSTS=" + (snapshot.RecentPosts == null ? "0" : snapshot.RecentPosts.Count.ToString(CultureInfo.InvariantCulture)));
                    if (snapshot.RecentPosts != null && snapshot.RecentPosts.Count > 0)
                    {
                        Console.WriteLine("NEWEST_POST=" + snapshot.RecentPosts[0].Id + "|" + snapshot.RecentPosts[0].PublishedUtc.ToString("o"));
                    }
                    if (snapshot.TargetUtc.HasValue)
                    {
                        Console.WriteLine("TARGET_UTC=" + snapshot.TargetUtc.Value.UtcDateTime.ToString("o"));
                        Console.WriteLine("TARGET_LOCAL=" + snapshot.TargetUtc.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
                    }
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("CHECK FAILED: " + ex.Message);
                    Environment.ExitCode = 2;
                }
                return;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    NativeMethods.PostMessage(new IntPtr(NativeMethods.HWND_BROADCAST), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                StartupRegistration.Ensure();
                Application.Run(new RadarForm(background));
                GC.KeepAlive(mutex);
            }
        }

        private static bool HasArg(string[] args, string wanted)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void AttachConsole()
        {
            try
            {
                if (NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
                {
                    Stream stdout = Console.OpenStandardOutput();
                    Stream stderr = Console.OpenStandardError();
                    Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
                    Console.SetError(new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true });
                }
            }
            catch { }
        }
    }

    internal static class NativeMethods
    {
        internal const int ATTACH_PARENT_PROCESS = -1;
        internal const int HWND_BROADCAST = 0xffff;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AttachConsole(int dwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);
    }

    internal static class StartupRegistration
    {
        internal static void Ensure()
        {
            try
            {
                string exe = Application.ExecutablePath;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (key != null) key.SetValue("CodexResetRadar", "\"" + exe + "\" --background", RegistryValueKind.String);
                }
            }
            catch { }
        }
    }

    internal sealed class RadarForm : Form
    {
        private readonly NotifyIcon tray;
        private readonly Label statusLabel;
        private readonly Label countdownLabel;
        private readonly Label sourceLabel;
        private readonly TextBox evidenceBox;
        private readonly Label checkedLabel;
        private readonly Button openButton;
        private readonly Button refreshButton;
        private readonly Button recentButton;
        private readonly Button translateButton;
        private readonly CheckBox pinCheck;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly System.Windows.Forms.Timer countdownTimer;
        private RadarSnapshot current;
        private string originalEvidence;
        private string translatedEvidence;
        private bool showingTranslation;
        private bool refreshing;
        private bool exiting;
        private readonly RadarState state;

        internal RadarForm(bool background)
        {
            Text = "Codex 重置雷达 v" + ReleaseUpdater.CurrentVersion;
            ClientSize = new Size(392, 500);
            MinimumSize = new Size(408, 539);
            MaximumSize = new Size(408, 539);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 17, 21);
            ForeColor = Color.FromArgb(239, 242, 247);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ShowInTaskbar = true;
            MaximizeBox = false;
            TopMost = false;

            Label brand = MakeLabel("CODEX RESET RADAR", 20, 18, 250, 22, 10F, FontStyle.Bold, Color.FromArgb(140, 151, 170));
            Label title = MakeLabel("重置预告", 20, 46, 250, 34, 20F, FontStyle.Bold, Color.White);
            Controls.Add(brand);
            Controls.Add(title);

            pinCheck = new CheckBox();
            pinCheck.Text = "置顶";
            pinCheck.Checked = false;
            pinCheck.AutoSize = true;
            pinCheck.Location = new Point(316, 24);
            pinCheck.ForeColor = Color.FromArgb(180, 188, 203);
            pinCheck.CheckedChanged += delegate { TopMost = pinCheck.Checked; };
            Controls.Add(pinCheck);

            Panel card = new Panel();
            card.Location = new Point(16, 92);
            card.Size = new Size(360, 150);
            card.BackColor = Color.FromArgb(25, 29, 36);
            Controls.Add(card);

            statusLabel = MakeLabel("正在获取预告…", 16, 15, 328, 26, 12F, FontStyle.Bold, Color.FromArgb(255, 188, 74));
            countdownLabel = MakeLabel("—", 16, 45, 328, 60, 18F, FontStyle.Bold, Color.White);
            // Keep the most important value readable even when a localized label is long.
            countdownLabel.AutoEllipsis = false;
            countdownLabel.UseCompatibleTextRendering = true;
            countdownLabel.TextAlign = ContentAlignment.MiddleLeft;
            sourceLabel = MakeLabel("首次刷新不会弹出历史提醒", 16, 111, 328, 24, 9F, FontStyle.Regular, Color.FromArgb(151, 161, 179));
            card.Controls.Add(statusLabel);
            card.Controls.Add(countdownLabel);
            card.Controls.Add(sourceLabel);

            Label evidenceTitle = MakeLabel("判断依据", 20, 260, 100, 22, 10F, FontStyle.Bold, Color.FromArgb(198, 205, 218));
            Controls.Add(evidenceTitle);

            translateButton = MakeButton("翻译中文", 272, 252, 100, 30, Color.FromArgb(40, 46, 57));
            translateButton.Click += async delegate { await ToggleTranslationAsync(); };
            translateButton.Enabled = false;
            Controls.Add(translateButton);

            evidenceBox = new TextBox();
            evidenceBox.Location = new Point(20, 290);
            evidenceBox.Size = new Size(352, 92);
            evidenceBox.Multiline = true;
            evidenceBox.ScrollBars = ScrollBars.Vertical;
            evidenceBox.ReadOnly = true;
            evidenceBox.BorderStyle = BorderStyle.None;
            evidenceBox.BackColor = Color.FromArgb(15, 17, 21);
            evidenceBox.ForeColor = Color.FromArgb(198, 205, 218);
            evidenceBox.Font = new Font("Microsoft YaHei UI", 9.5F);
            evidenceBox.Text = "优先检查公开重置预告；没有公开时间窗时，显示本机已登录 Codex 额度窗口的刷新时间。";
            Controls.Add(evidenceBox);

            checkedLabel = MakeLabel("尚未检查", 20, 397, 352, 20, 8.5F, FontStyle.Regular, Color.FromArgb(119, 130, 150));
            Controls.Add(checkedLabel);

            refreshButton = MakeButton("立即刷新", 20, 435, 82, 42, Color.FromArgb(40, 46, 57));
            refreshButton.Click += async delegate { await RefreshRadarAsync(true); };
            Controls.Add(refreshButton);

            recentButton = MakeButton("最近10条", 110, 435, 82, 42, Color.FromArgb(40, 46, 57));
            recentButton.Click += delegate { ShowRecentPosts(); };
            Controls.Add(recentButton);

            openButton = MakeButton("打开原帖", 200, 435, 82, 42, Color.FromArgb(43, 105, 211));
            openButton.Enabled = false;
            openButton.Click += delegate { OpenCurrentUrl(); };
            Controls.Add(openButton);

            Button hideButton = MakeButton("收进托盘", 290, 435, 82, 42, Color.FromArgb(40, 46, 57));
            hideButton.Click += delegate { HideToTray(); };
            Controls.Add(hideButton);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开重置雷达", null, delegate { ShowWindow(); });
            menu.Items.Add("立即刷新", null, async delegate { await RefreshRadarAsync(true); });
            menu.Items.Add("最近 10 条消息", null, delegate { ShowRecentPosts(); });
            menu.Items.Add("打开原帖", null, delegate { OpenCurrentUrl(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApplication(); });

            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Information;
            tray.Text = "Codex 重置雷达";
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWindow(); };

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 5 * 60 * 1000;
            refreshTimer.Tick += async delegate { await RefreshRadarAsync(false); };
            refreshTimer.Start();

            countdownTimer = new System.Windows.Forms.Timer();
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += delegate { UpdateCountdown(); };
            countdownTimer.Start();

            state = RadarState.Load();
            FormClosing += OnFormClosing;
            Shown += async delegate
            {
                if (background) HideToTray();
                await RefreshRadarAsync(false);
                string staged = await ReleaseUpdater.CheckAndPrepareAsync();
                if (!string.IsNullOrEmpty(staged) && ReleaseUpdater.StartApply(staged, Application.ExecutablePath)) ExitApplication();
            };
        }

        private static Label MakeLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(x, y);
            label.Size = new Size(width, height);
            label.Font = new Font("Microsoft YaHei UI", size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            return label;
        }

        private static Button MakeButton(string text, int x, int y, int width, int height, Color color)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(width, height);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = color;
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
            return button;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowMessage)
            {
                ShowWindow();
                return;
            }
            base.WndProc(ref m);
        }

        private async Task RefreshRadarAsync(bool userInitiated)
        {
            if (refreshing) return;
            refreshing = true;
            refreshButton.Enabled = false;
            string previousRefreshText = refreshButton.Text;
            refreshButton.Text = "刷新中…";
            try
            {
                Task<RadarSnapshot> fetchTask = RadarClient.FetchAsync(DateTimeOffset.UtcNow, state.LastGoodJson);
                Task finished = await Task.WhenAny(fetchTask, Task.Delay(45000));
                if (finished != fetchTask) throw new TimeoutException("刷新超过 45 秒，已停止本轮请求");
                RadarSnapshot next = await fetchTask;
                DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
                LocalQuotaSample quotaSample = LocalQuotaSample.FromInfo(next.LocalQuota, checkedAt);
                QuotaResetDetector.Apply(next, state, quotaSample, checkedAt);
                if (quotaSample != null) state.LastQuota = quotaSample;
                current = next;
                ApplySnapshot(next);

                bool firstSuccess = !state.Initialized;
                bool shouldNotify = NotificationRules.ShouldNotify(firstSuccess, state.LastNotifiedId, next.ActionableId, next.IsActionable);
                state.Initialized = true;
                if (!string.IsNullOrEmpty(next.ActionableId)) state.LastNotifiedId = next.ActionableId;
                if (!string.IsNullOrEmpty(next.CacheEnvelope)) state.LastGoodJson = next.CacheEnvelope;
                state.Save();

                // Deliberately silent: refresh state in the tray without stealing focus or showing balloons.
            }
            catch (Exception ex)
            {
                RadarSnapshot cached = RadarClient.FromCache(state.LastGoodJson, DateTimeOffset.UtcNow);
                if (cached != null)
                {
                    cached.SourceLabel = cached.SourceLabel + " · 离线缓存";
                    current = cached;
                    ApplySnapshot(cached);
                    checkedLabel.Text = "联网失败，显示上次结果 · " + ShortError(ex.Message);
                }
                else
                {
                    statusLabel.Text = "暂时无法获取数据";
                    statusLabel.ForeColor = Color.FromArgb(255, 108, 108);
                    countdownLabel.Text = "稍后自动重试";
                    sourceLabel.Text = "网络异常";
                    checkedLabel.Text = ShortError(ex.Message);
                }
                if (userInitiated) System.Media.SystemSounds.Exclamation.Play();
            }
            finally
            {
                refreshing = false;
                refreshButton.Enabled = true;
                refreshButton.Text = previousRefreshText;
            }
        }

        private void ApplySnapshot(RadarSnapshot snapshot)
        {
            statusLabel.Text = snapshot.StatusTitle;
            statusLabel.ForeColor = snapshot.StatusColor;
            countdownLabel.Text = snapshot.GetCountdownText(DateTimeOffset.UtcNow);
            sourceLabel.Text = snapshot.SourceLabel;
            originalEvidence = snapshot.Evidence;
            translatedEvidence = null;
            showingTranslation = false;
            evidenceBox.Text = originalEvidence;
            translateButton.Text = "翻译中文";
            translateButton.Enabled = !string.IsNullOrWhiteSpace(originalEvidence);
            checkedLabel.Text = "检查于 " + DateTime.Now.ToString("HH:mm:ss") + " · 每 5 分钟自动刷新";
            if (!string.IsNullOrEmpty(snapshot.PublicFeedWarning)) checkedLabel.Text = snapshot.PublicFeedWarning;
            openButton.Enabled = !string.IsNullOrEmpty(snapshot.SourceUrl);
            recentButton.Enabled = snapshot.RecentPosts != null && snapshot.RecentPosts.Count > 0;
            tray.Text = Truncate("Codex 重置雷达 · " + snapshot.GetCountdownText(DateTimeOffset.UtcNow), 63);
        }

        private void UpdateCountdown()
        {
            if (current == null) return;
            countdownLabel.Text = current.GetCountdownText(DateTimeOffset.UtcNow);
            tray.Text = Truncate("Codex 重置雷达 · " + countdownLabel.Text, 63);
        }

        private void OpenCurrentUrl()
        {
            if (current == null || string.IsNullOrEmpty(current.SourceUrl)) return;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(current.SourceUrl);
                start.UseShellExecute = true;
                Process.Start(start);
            }
            catch { System.Media.SystemSounds.Exclamation.Play(); }
        }

        private void ShowRecentPosts()
        {
            List<RecentPost> posts = current == null || current.RecentPosts == null ? new List<RecentPost>() : current.RecentPosts;
            using (RecentPostsForm dialog = new RecentPostsForm(posts, current == null ? null : current.PublicFeedWarning)) dialog.ShowDialog(this);
        }

        private async Task ToggleTranslationAsync()
        {
            if (showingTranslation)
            {
                evidenceBox.Text = originalEvidence;
                showingTranslation = false;
                translateButton.Text = "翻译中文";
                return;
            }
            if (string.IsNullOrWhiteSpace(originalEvidence)) return;
            translateButton.Enabled = false;
            translateButton.Text = "翻译中…";
            try
            {
                if (string.IsNullOrEmpty(translatedEvidence)) translatedEvidence = await TranslationClient.TranslateToChineseAsync(originalEvidence);
                evidenceBox.Text = translatedEvidence;
                showingTranslation = true;
                translateButton.Text = "查看原文";
            }
            catch (Exception ex)
            {
                translateButton.Text = "重试翻译";
                checkedLabel.Text = "翻译失败 · " + ShortError(ex.Message);
            }
            finally { translateButton.Enabled = true; }
        }

        private void ShowWindow()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        private void ExitApplication()
        {
            exiting = true;
            tray.Visible = false;
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
            else
            {
                tray.Visible = false;
                tray.Dispose();
            }
        }

        private static string ShortError(string text)
        {
            if (string.IsNullOrEmpty(text)) return "未知错误";
            return text.Length <= 90 ? text : text.Substring(0, 87) + "…";
        }

        private static string Truncate(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max);
        }
    }

    internal sealed class RecentPostsForm : Form
    {
        private sealed class PostRow
        {
            internal RecentPost Post;
            internal Panel Panel;
            internal Label Content;
            internal string Translation;
        }

        private readonly List<PostRow> rows = new List<PostRow>();
        private readonly Button translateButton;
        private bool showingTranslations;
        private bool translating;

        internal RecentPostsForm(List<RecentPost> posts, string sourceWarning)
        {
            Text = "最近 10 条公开消息";
            ClientSize = new Size(620, 620);
            MinimumSize = new Size(636, 659);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(15, 17, 21);
            ForeColor = Color.FromArgb(239, 242, 247);
            Font = new Font("Microsoft YaHei UI", 9F);

            Panel header = new Panel();
            header.Location = new Point(0, 0);
            header.Size = new Size(620, 48);
            header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            header.Height = 48;
            header.BackColor = Color.FromArgb(25, 29, 36);
            Controls.Add(header);

            Label hint = new Label();
            hint.Text = string.IsNullOrEmpty(sourceWarning)
                ? "公开原帖 · 最新 " + (posts == null || posts.Count == 0 ? "未知" : posts[0].PublishedUtc.LocalDateTime.ToString("MM-dd HH:mm"))
                : sourceWarning;
            hint.Location = new Point(16, 14);
            hint.Size = new Size(350, 22);
            hint.ForeColor = Color.FromArgb(180, 188, 203);
            header.Controls.Add(hint);

            translateButton = new Button();
            translateButton.Text = "翻译全部";
            translateButton.Location = new Point(492, 8);
            translateButton.Size = new Size(112, 32);
            translateButton.FlatStyle = FlatStyle.Flat;
            translateButton.FlatAppearance.BorderSize = 0;
            translateButton.BackColor = Color.FromArgb(43, 105, 211);
            translateButton.ForeColor = Color.White;
            translateButton.Click += async delegate { await ToggleTranslationsAsync(); };
            header.Controls.Add(translateButton);

            FlowLayoutPanel list = new FlowLayoutPanel();
            list.Location = new Point(0, 48);
            list.Size = new Size(620, 572);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            list.AutoScroll = true;
            list.FlowDirection = FlowDirection.TopDown;
            list.WrapContents = false;
            list.Padding = new Padding(14);
            list.BackColor = BackColor;
            Controls.Add(list);

            if (posts == null || posts.Count == 0)
            {
                translateButton.Enabled = false;
                Label empty = new Label();
                empty.Text = "还没有可记录的公开消息。请先点“立即刷新”。";
                empty.Size = new Size(560, 40);
                empty.ForeColor = Color.FromArgb(180, 188, 203);
                list.Controls.Add(empty);
                return;
            }

            int index = 0;
            foreach (RecentPost post in posts)
            {
                index++;
                Panel row = new Panel();
                row.Margin = new Padding(0, 0, 0, 10);
                row.BackColor = Color.FromArgb(25, 29, 36);
                list.Controls.Add(row);

                Label meta = new Label();
                meta.Location = new Point(12, 10);
                meta.Size = new Size(430, 20);
                meta.ForeColor = post.IsResetCard ? Color.FromArgb(255, 188, 74) : Color.FromArgb(151, 161, 179);
                meta.Text = "#" + index.ToString(CultureInfo.InvariantCulture) + " · " + post.KindLabel + " · " + post.PublishedUtc.LocalDateTime.ToString("MM-dd HH:mm");
                row.Controls.Add(meta);

                LinkLabel open = new LinkLabel();
                open.Text = "原帖";
                open.Location = new Point(510, 10);
                open.Size = new Size(42, 20);
                open.LinkColor = Color.FromArgb(103, 170, 255);
                open.Click += delegate
                {
                    try { Process.Start(new ProcessStartInfo(post.Url) { UseShellExecute = true }); }
                    catch { System.Media.SystemSounds.Exclamation.Play(); }
                };
                row.Controls.Add(open);

                Label content = new Label();
                content.Location = new Point(12, 34);
                content.ForeColor = Color.FromArgb(222, 226, 236);
                content.Text = post.Text;
                row.Controls.Add(content);

                PostRow postRow = new PostRow { Post = post, Panel = row, Content = content };
                rows.Add(postRow);
                ResizeRow(postRow, post.Text);
            }
        }

        private async Task ToggleTranslationsAsync()
        {
            if (translating) return;
            if (showingTranslations)
            {
                showingTranslations = false;
                foreach (PostRow row in rows) ApplyText(row, row.Post.Text);
                translateButton.Text = "翻译全部";
                return;
            }

            translating = true;
            showingTranslations = true;
            translateButton.Enabled = false;
            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    PostRow row = rows[i];
                    translateButton.Text = "翻译 " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + rows.Count.ToString(CultureInfo.InvariantCulture) + "…";
                    if (string.IsNullOrEmpty(row.Translation))
                    {
                        try { row.Translation = await TranslationClient.TranslateToChineseAsync(row.Post.Text); }
                        catch { row.Translation = row.Post.Text + Environment.NewLine + "（翻译失败，暂时显示原文）"; }
                    }
                    if (IsDisposed || Disposing) return;
                    ApplyText(row, row.Translation);
                }
                translateButton.Text = "查看原文";
            }
            finally
            {
                translating = false;
                if (!IsDisposed && !Disposing) translateButton.Enabled = true;
            }
        }

        private void ApplyText(PostRow row, string text)
        {
            row.Content.Text = text;
            ResizeRow(row, text);
        }

        private void ResizeRow(PostRow row, string text)
        {
            Size textSize = TextRenderer.MeasureText(text, Font, new Size(540, 0), TextFormatFlags.WordBreak);
            int rowHeight = Math.Max(94, textSize.Height + 48);
            row.Panel.Size = new Size(568, rowHeight);
            row.Content.Size = new Size(540, rowHeight - 42);
        }
    }

    internal enum RadarMode
    {
        OfficialWindow,
        WindowElapsed,
        Confirmed,
        Experimental,
        Quiet
    }

    internal static class CodexProcessMonitor
    {
        internal static HashSet<string> GetRunningKeys()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (Process process in Process.GetProcessesByName("ChatGPT"))
                {
                    try
                    {
                        string path = process.MainModule == null ? null : process.MainModule.FileName;
                        if (IsCodexDesktopPath(path)) keys.Add(process.Id.ToString(CultureInfo.InvariantCulture) + "|" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }
            return keys;
        }

        internal static bool IsDesktopRunning()
        {
            return GetRunningKeys().Count > 0;
        }

        internal static bool IsDesktopWindowVisible()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("ChatGPT"))
                {
                    try
                    {
                        string path = process.MainModule == null ? null : process.MainModule.FileName;
                        if (IsCodexDesktopPath(path) && process.MainWindowHandle != IntPtr.Zero && NativeMethods.IsWindowVisible(process.MainWindowHandle)) return true;
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }
            return false;
        }

        internal static bool IsCodexDesktopPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                path.IndexOf("WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                path.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class TranslationClient
    {
        // MyMemory rejects a decoded `q` value longer than 500 characters. Leave room
        // below that limit so long X posts can be translated piece by piece.
        private const int MaxQueryCharacters = 450;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        internal static async Task<string> TranslateToChineseAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            lock (Cache)
            {
                string cached;
                if (Cache.TryGetValue(text, out cached)) return cached;
            }

            StringBuilder translated = new StringBuilder();
            foreach (string chunk in SplitForQuery(text))
            {
                translated.Append(await TranslateChunkAsync(chunk));
            }
            string result = translated.ToString();
            lock (Cache) Cache[text] = result;
            return result;
        }

        private static async Task<string> TranslateChunkAsync(string text)
        {
            string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=en%7Czh-CN";
            using (TimeoutWebClient client = new TimeoutWebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers[HttpRequestHeader.UserAgent] = "CodexResetRadar/1.1";
                string json = await client.DownloadStringTaskAsync(new Uri(url));
                return ParseTranslation(json);
            }
        }

        private static List<string> SplitForQuery(string text)
        {
            List<string> chunks = new List<string>();
            for (int start = 0; start < text.Length;)
            {
                int end = Math.Min(text.Length, start + MaxQueryCharacters);
                if (end < text.Length)
                {
                    // Prefer a natural boundary, but do not allow one very long word/URL
                    // to exceed the provider's hard limit.
                    int minimum = start + MaxQueryCharacters / 2;
                    for (int candidate = end; candidate > minimum; candidate--)
                    {
                        char previous = text[candidate - 1];
                        if (previous == '\n' || previous == '\r' || char.IsWhiteSpace(previous)
                            || previous == '.' || previous == '!' || previous == '?' || previous == ';' || previous == ',')
                        {
                            end = candidate;
                            break;
                        }
                    }
                }
                chunks.Add(text.Substring(start, end - start));
                start = end;
            }
            return chunks;
        }

        internal static List<string> SplitForTests(string text)
        {
            return SplitForQuery(text);
        }

        internal static string ParseTranslation(string json)
        {
            Dictionary<string, object> root = Json.Deserialize<Dictionary<string, object>>(json);
            object responseObject;
            if (!root.TryGetValue("responseData", out responseObject)) throw new InvalidOperationException("翻译接口未返回结果");
            Dictionary<string, object> response = responseObject as Dictionary<string, object>;
            object translatedObject;
            if (response == null || !response.TryGetValue("translatedText", out translatedObject)) throw new InvalidOperationException("翻译接口未返回译文");
            string translated = WebUtility.HtmlDecode(Convert.ToString(translatedObject, CultureInfo.InvariantCulture));
            if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("译文为空");
            return translated;
        }
    }

    internal sealed class RecentPost
    {
        internal string Id;
        internal string Text;
        internal string Url;
        internal string ParentId;
        internal bool InheritedResetContext;
        internal bool IsOriginalPost;
        internal DateTimeOffset PublishedUtc;

        internal bool IsResetCard
        {
            get { return IsResetCardText(Text) || InheritedResetContext; }
        }

        internal string KindLabel
        {
            get
            {
                if (!IsOriginalPost && !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(Text)) return "公开记录";
                if (!string.IsNullOrEmpty(Text) && Text.ToLowerInvariant().Contains("banked reset")) return "重置卡";
                if (IsResetCard && RadarClient.LooksConfirmed(Text)) return "已重置";
                if (InheritedResetContext) return "重置预告";
                if (IsResetCard) return "重置信号";
                return string.IsNullOrWhiteSpace(Text) ? "公开消息" : "普通消息";
            }
        }

        internal static bool IsResetCardText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string lower = text.ToLowerInvariant();
            if (lower.Contains("banked reset")) return true;
            if (Regex.IsMatch(lower, @"\breset\s+day\b|\bday\s+\d+\s*/\s*(?:le\s+limit\s+)?reset\b|\bglobal\s+reset\b")) return true;
            if (Regex.IsMatch(lower, @"\band\s+reset\.\s*enjoy\b|\bside\s+of\s+reset\b|\breset\b.{0,60}\ball\s+propagated\b")) return true;
            return lower.Contains("reset") && (lower.Contains("codex") || lower.Contains("chatgpt") || lower.Contains("astra") || lower.Contains("usage") || lower.Contains("limit"));
        }
    }

    internal sealed class RadarSnapshot
    {
        internal RadarMode Mode;
        internal string StatusTitle;
        internal string SourceLabel;
        internal string Evidence;
        internal string SourceUrl;
        internal string ActionableId;
        internal DateTimeOffset? SignalUtc;
        internal DateTimeOffset? TargetUtc;
        internal bool IsDeadline;
        internal int Probability24;
        internal int Probability48;
        internal bool IsActionable;
        internal string CacheEnvelope;
        internal string PublicFeedWarning;
        internal LocalQuotaInfo LocalQuota;
        internal List<RecentPost> RecentPosts = new List<RecentPost>();

        internal Color StatusColor
        {
            get
            {
                if (Mode == RadarMode.OfficialWindow) return Color.FromArgb(255, 188, 74);
                if (Mode == RadarMode.WindowElapsed || Mode == RadarMode.Confirmed) return Color.FromArgb(91, 213, 151);
                if (Mode == RadarMode.Experimental) return Color.FromArgb(103, 170, 255);
                return Color.FromArgb(151, 161, 179);
            }
        }

        internal string GetCountdownText(DateTimeOffset now)
        {
            if (Mode == RadarMode.OfficialWindow && TargetUtc.HasValue)
            {
                TimeSpan remaining = TargetUtc.Value - now;
                if (remaining.TotalSeconds <= 0) return IsDeadline ? "最迟时间已到，等待确认" : "时间窗已到，等待确认";
                return IsDeadline ? DurationText.Deadline(remaining) : DurationText.Countdown(remaining);
            }
            if (Mode == RadarMode.WindowElapsed) return "时间窗已到，等待确认";
            if (Mode == RadarMode.Confirmed) return "本轮已重置";
            if (LocalQuota != null) return LocalQuota.GetRefreshText(now);
            if (Mode == RadarMode.Experimental) return "暂无可核实的倒计时";
            return "暂无可核实的倒计时";
        }
    }

    internal static class DurationText
    {
        internal static string Deadline(TimeSpan span)
        {
            double hours = Math.Max(0, span.TotalHours);
            if (hours < 1) return "距最迟时间：不足 1 小时";
            if (hours < 24) return "距最迟时间：约 " + hours.ToString("0.#", CultureInfo.InvariantCulture) + " 小时";
            return "距最迟时间：约 " + span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + " 天";
        }

        internal static string Countdown(TimeSpan span)
        {
            double hours = Math.Max(0, span.TotalHours);
            string hourText = hours < 1 ? "不足 1 小时" : "约 " + hours.ToString("0.#", CultureInfo.InvariantCulture) + " 小时";
            if (hours < 24) return "公开时间窗：" + hourText;
            return "公开时间窗：约 " + span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + " 天后";
        }

        internal static string LocalRefresh(TimeSpan span, string label)
        {
            double hours = Math.Max(0, span.TotalHours);
            if (hours < 1) return label + "：不足 1 小时后刷新";
            if (hours < 24) return label + "：约 " + hours.ToString("0.#", CultureInfo.InvariantCulture) + " 小时后刷新";
            return label + "：约 " + span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + " 天后刷新";
        }
    }

    internal static class NotificationRules
    {
        internal static bool ShouldNotify(bool firstSuccess, string lastId, string nextId, bool actionable)
        {
            if (firstSuccess || !actionable || string.IsNullOrEmpty(nextId)) return false;
            return !string.Equals(lastId, nextId, StringComparison.Ordinal);
        }
    }

    internal sealed class TimeoutWebClient : WebClient
    {
        internal const int TimeoutMilliseconds = 12000;

        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest request = base.GetWebRequest(address);
            request.Timeout = TimeoutMilliseconds;
            HttpWebRequest http = request as HttpWebRequest;
            if (http != null)
            {
                http.ReadWriteTimeout = TimeoutMilliseconds;
                http.KeepAlive = false;
            }
            return request;
        }
    }

    internal static class SafeHttp
    {
        internal static async Task<string> DownloadAsync(string url, string userAgent, string bearerToken, string accountId)
        {
            using (TimeoutWebClient client = new TimeoutWebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers[HttpRequestHeader.UserAgent] = userAgent;
                if (!string.IsNullOrEmpty(bearerToken)) client.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearerToken;
                if (!string.IsNullOrEmpty(accountId)) client.Headers["chatgpt-account-id"] = accountId;

                Task<string> downloadTask = client.DownloadStringTaskAsync(new Uri(url));
                Task finished = await Task.WhenAny(downloadTask, Task.Delay(TimeoutWebClient.TimeoutMilliseconds));
                if (finished != downloadTask)
                {
                    client.CancelAsync();
                    throw new TimeoutException("网络请求超过 " + (TimeoutWebClient.TimeoutMilliseconds / 1000).ToString(CultureInfo.InvariantCulture) + " 秒");
                }
                return await downloadTask;
            }
        }
    }

    internal sealed class LocalQuotaWindow
    {
        internal int WindowSeconds;
        internal double UsedPercent;
        internal DateTimeOffset? ResetUtc;
        internal string ScopeName;
        internal bool IsGeneral;

        internal string Label
        {
            get
            {
                if (WindowSeconds > 0 && WindowSeconds % 86400 == 0) return (WindowSeconds / 86400).ToString(CultureInfo.InvariantCulture) + " 天窗口";
                if (WindowSeconds > 0 && WindowSeconds % 3600 == 0) return (WindowSeconds / 3600).ToString(CultureInfo.InvariantCulture) + " 小时窗口";
                if (WindowSeconds > 0 && WindowSeconds % 60 == 0) return (WindowSeconds / 60).ToString(CultureInfo.InvariantCulture) + " 分钟窗口";
                return "额度窗口";
            }
        }
    }

    internal sealed class LocalQuotaInfo
    {
        private readonly List<LocalQuotaWindow> windows;
        internal string AccountId;

        internal LocalQuotaInfo(List<LocalQuotaWindow> values)
        {
            windows = values ?? new List<LocalQuotaWindow>();
        }

        internal LocalQuotaWindow GeneralWeeklyWindow()
        {
            foreach (LocalQuotaWindow window in windows)
            {
                if (window.IsGeneral && window.WindowSeconds >= 6 * 86400 && window.ResetUtc.HasValue) return window;
            }
            return null;
        }

        private LocalQuotaWindow Preferred(DateTimeOffset now)
        {
            LocalQuotaWindow selected = null;
            foreach (LocalQuotaWindow window in windows)
            {
                if (!window.IsGeneral || !window.ResetUtc.HasValue || window.ResetUtc.Value <= now) continue;
                if (selected == null || window.ResetUtc.Value < selected.ResetUtc.Value) selected = window;
            }
            if (selected != null) return selected;
            foreach (LocalQuotaWindow window in windows)
            {
                if (!window.ResetUtc.HasValue || window.ResetUtc.Value <= now) continue;
                if (selected == null || window.ResetUtc.Value < selected.ResetUtc.Value) selected = window;
            }
            return selected;
        }

        internal string GetRefreshText(DateTimeOffset now)
        {
            LocalQuotaWindow selected = Preferred(now);
            if (selected == null) return "本机额度：等待刷新时间同步";
            string label = selected.IsGeneral && selected.WindowSeconds >= 24 * 60 * 60 ? "本机每周额度" : "本机额度";
            return DurationText.LocalRefresh(selected.ResetUtc.Value - now, label);
        }

        internal string GetEvidence(DateTimeOffset now)
        {
            StringBuilder result = new StringBuilder("本机 Codex 通用使用限额（直接读取）：");
            AppendScopeEvidence(result, true, null);

            HashSet<string> scopes = new HashSet<string>(StringComparer.Ordinal);
            foreach (LocalQuotaWindow window in windows)
            {
                if (!window.IsGeneral && !string.IsNullOrWhiteSpace(window.ScopeName)) scopes.Add(window.ScopeName);
            }
            foreach (string scope in scopes)
            {
                result.Append(Environment.NewLine).Append(Environment.NewLine).Append(scope).Append(" 使用限额（模型专属，单独计）：");
                AppendScopeEvidence(result, false, scope);
            }
            return result.ToString();
        }

        private void AppendScopeEvidence(StringBuilder result, bool isGeneral, string scopeName)
        {
            List<LocalQuotaWindow> unique = new List<LocalQuotaWindow>();
            foreach (LocalQuotaWindow window in windows)
            {
                if (window.IsGeneral != isGeneral || (!isGeneral && !string.Equals(window.ScopeName, scopeName, StringComparison.Ordinal)) || !window.ResetUtc.HasValue) continue;
                LocalQuotaWindow same = null;
                foreach (LocalQuotaWindow present in unique)
                {
                    if (present.WindowSeconds == window.WindowSeconds) { same = present; break; }
                }
                if (same == null || window.UsedPercent > same.UsedPercent)
                {
                    if (same != null) unique.Remove(same);
                    unique.Add(window);
                }
            }
            unique.Sort(delegate (LocalQuotaWindow a, LocalQuotaWindow b) { return a.WindowSeconds.CompareTo(b.WindowSeconds); });
            foreach (LocalQuotaWindow window in unique)
            {
                result.Append(Environment.NewLine)
                    .Append(window.Label)
                    .Append("：已用 ")
                    .Append(window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture))
                    .Append("% · 刷新时间 ")
                    .Append(window.ResetUtc.Value.LocalDateTime.ToString("MM-dd HH:mm"));
            }
        }
    }

    internal static class LocalQuotaClient
    {
        private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 64 };

        internal static async Task<LocalQuotaInfo> TryFetchAsync(DateTimeOffset now)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                Dictionary<string, object> auth = Dict(Json.DeserializeObject(File.ReadAllText(AuthPath(), Encoding.UTF8)));
                Dictionary<string, object> tokens = DictOrNull(Get(auth, "tokens"));
                string token = Str(Get(tokens, "access_token"));
                if (string.IsNullOrWhiteSpace(token)) token = Str(Get(auth, "access_token"));
                if (string.IsNullOrWhiteSpace(token)) return null;
                string accountId = Str(Get(tokens, "account_id"));
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        string body = await SafeHttp.DownloadAsync(UsageUrl, "CodexResetRadar/1.3 (local quota view)", token, accountId);
                        LocalQuotaInfo result = Parse(body, now);
                        if (result != null) result.AccountId = accountId;
                        return result;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 1) LogFailure(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                LogFailure(ex);
            }
            return null;
        }

        private static void LogFailure(Exception ex)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexResetRadar");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "radar.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " 本机额度读取失败：" + ex.GetType().Name + " " + ex.Message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        internal static LocalQuotaInfo ParseForTests(string body, DateTimeOffset now)
        {
            return Parse(body, now);
        }

        private static string AuthPath()
        {
            string custom = Environment.GetEnvironmentVariable("CODEX_AUTH_JSON");
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(codexHome)) return Path.Combine(codexHome, "auth.json");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        }

        private static LocalQuotaInfo Parse(string body, DateTimeOffset now)
        {
            Dictionary<string, object> root = Dict(Json.DeserializeObject(body));
            List<LocalQuotaWindow> windows = new List<LocalQuotaWindow>();
            AddRateLimit(windows, Get(root, "rate_limit"), now, "通用使用限额", true);
            IEnumerable additional = Get(root, "additional_rate_limits") as IEnumerable;
            if (additional != null)
            {
                foreach (object entry in additional)
                {
                    Dictionary<string, object> item = DictOrNull(entry);
                    if (item == null) continue;
                    string scope = Str(Get(item, "limit_name"));
                    if (string.IsNullOrWhiteSpace(scope)) scope = Str(Get(item, "metered_feature"));
                    if (string.IsNullOrWhiteSpace(scope)) scope = "模型专属";
                    AddRateLimit(windows, Get(item, "rate_limit"), now, scope, false);
                }
            }
            return windows.Count == 0 ? null : new LocalQuotaInfo(windows);
        }

        private static void AddRateLimit(List<LocalQuotaWindow> windows, object rateLimitValue, DateTimeOffset now, string scopeName, bool isGeneral)
        {
            Dictionary<string, object> rateLimit = DictOrNull(rateLimitValue);
            if (rateLimit == null) return;
            AddWindow(windows, Get(rateLimit, "primary_window"), now, scopeName, isGeneral);
            AddWindow(windows, Get(rateLimit, "secondary_window"), now, scopeName, isGeneral);
        }

        private static void AddWindow(List<LocalQuotaWindow> windows, object windowValue, DateTimeOffset now, string scopeName, bool isGeneral)
        {
            Dictionary<string, object> window = DictOrNull(windowValue);
            if (window == null) return;
            long resetAt = Integer(Get(window, "reset_at"), 0);
            long afterSeconds = Integer(Get(window, "reset_after_seconds"), 0);
            DateTimeOffset? reset = resetAt > 0 ? UnixEpoch().AddSeconds(resetAt) : (afterSeconds > 0 ? (DateTimeOffset?)now.AddSeconds(afterSeconds) : null);
            if (!reset.HasValue) return;
            LocalQuotaWindow value = new LocalQuotaWindow();
            value.WindowSeconds = (int)Math.Max(0, Math.Min(int.MaxValue, Integer(Get(window, "limit_window_seconds"), 0)));
            value.UsedPercent = Number(Get(window, "used_percent"), 0);
            value.ResetUtc = reset;
            value.ScopeName = scopeName;
            value.IsGeneral = isGeneral;
            windows.Add(value);
        }

        private static DateTimeOffset UnixEpoch()
        {
            return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }

        private static Dictionary<string, object> Dict(object value)
        {
            Dictionary<string, object> result = value as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("本机额度数据格式无效");
            return result;
        }

        private static Dictionary<string, object> DictOrNull(object value) { return value as Dictionary<string, object>; }
        private static object Get(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict != null && dict.TryGetValue(key, out value) ? value : null;
        }
        private static string Str(object value) { return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static double Number(object value, double fallback)
        {
            double number;
            return double.TryParse(Str(value), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : fallback;
        }
        private static long Integer(object value, long fallback)
        {
            long number;
            return long.TryParse(Str(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : fallback;
        }
    }

    internal static class RadarClient
    {
        private const string FeedUrl = "https://codex-reset.com/api/feed";
        private const string ForecastUrl = "https://codex-reset.com/api/forecast";
        private const string DayclawUrl = "https://api.dayclaw.com/api/source/public/x/thsottiaux/items";
        private const string XProfileUrl = "https://x.com/thsottiaux";
        private const string FxTwitterStatusUrl = "https://api.fxtwitter.com/status/";
        private const string VxTwitterStatusUrl = "https://api.vxtwitter.com/i/status/";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 128 };
        private static readonly Dictionary<string, string> FullPostCache = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static async Task<RadarSnapshot> FetchAsync(DateTimeOffset now)
        {
            return await FetchAsync(now, null);
        }

        internal static async Task<RadarSnapshot> FetchAsync(DateTimeOffset now, string previousCacheJson)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            Task<LocalQuotaInfo> localQuotaTask = LocalQuotaClient.TryFetchAsync(now);
            RadarSnapshot publicSnapshot = null;
            Exception publicError = null;
            try
            {
                publicSnapshot = await FetchPublicAsync(now, previousCacheJson);
            }
            catch (Exception ex)
            {
                publicError = ex;
            }

            LocalQuotaInfo localQuota = await localQuotaTask;
            if (publicSnapshot == null)
            {
                if (localQuota == null) throw publicError ?? new InvalidOperationException("没有可用数据源");
                publicSnapshot = QuietSnapshot(null);
                publicSnapshot.SourceLabel = "本机 Codex 通用额度 · 直接读取";
                publicSnapshot.PublicFeedWarning = "公开消息暂不可用 · 仅本机额度已更新";
            }

            if (localQuota != null) publicSnapshot.LocalQuota = localQuota;
            if (localQuota != null && (publicSnapshot.Mode == RadarMode.Experimental || publicSnapshot.Mode == RadarMode.Quiet))
            {
                publicSnapshot.SourceLabel = "本机 Codex 通用额度 · 直接读取";
                publicSnapshot.Evidence = localQuota.GetEvidence(now) + Environment.NewLine + Environment.NewLine + publicSnapshot.Evidence;
            }
            else if (publicSnapshot.Mode == RadarMode.OfficialWindow || publicSnapshot.Mode == RadarMode.WindowElapsed)
            {
                LocalQuotaWindow weekly = localQuota == null ? null : localQuota.GeneralWeeklyWindow();
                publicSnapshot.Evidence += Environment.NewLine + Environment.NewLine
                    + (weekly == null ? "本机每周额度暂不可读，下一轮重试。"
                        : "本机每周额度持续核验：已用 " + weekly.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture)
                            + "% · 正常刷新 " + weekly.ResetUtc.Value.LocalDateTime.ToString("MM-dd HH:mm"));
            }
            return publicSnapshot;
        }

        private static async Task<RadarSnapshot> FetchPublicAsync(DateTimeOffset now, string previousCacheJson)
        {
            string feedJson = null;
            string forecastJson = null;
            string dayclawJson = null;
            string xProfileHtml = null;
            Exception feedError = null;

            Task<string> feedTask = DownloadAsync(FeedUrl);
            Task<string> forecastTask = DownloadAsync(ForecastUrl);
            Task<string> dayclawTask = DownloadAsync(DayclawUrl);
            Task<string> xProfileTask = DownloadXProfileAsync();
            try { feedJson = await feedTask; }
            catch (Exception ex) { feedError = ex; }

            try { forecastJson = await forecastTask; }
            catch { }
            try { dayclawJson = await dayclawTask; }
            catch { }
            try { xProfileHtml = await xProfileTask; }
            catch { }
            if (string.IsNullOrEmpty(feedJson))
            {
                try { feedJson = await DownloadAsync(FeedUrl); feedError = null; }
                catch (Exception ex) { feedError = ex; }
            }
            bool usingCachedFeed = false;
            if (string.IsNullOrEmpty(feedJson))
            {
                if (feedError == null) feedError = new InvalidDataException("主源返回空内容");
                feedJson = FeedFromCache(previousCacheJson);
                usingCachedFeed = !string.IsNullOrEmpty(feedJson);
            }
            feedJson = await EnrichLatestFeedPostAsync(feedJson, now);

            List<RecentPost> timelinePosts = MergeAllRecentPosts(ParseXProfilePosts(xProfileHtml), ParseDayclawPosts(dayclawJson));
            List<RecentPost> allPosts = MergeAllRecentPosts(ParseFeedPosts(feedJson), timelinePosts);
            List<RecentPost> posts = LimitRecentPosts(allPosts, 10);
            RadarSnapshot timeline = ParseRecentTimeline(allPosts, now);
            RadarSnapshot feedResult = null;
            if (!string.IsNullOrEmpty(feedJson)) feedResult = ParseFeed(feedJson, forecastJson, now);
            RadarSnapshot dayclawResult = null;
            if (feedResult == null && !string.IsNullOrEmpty(dayclawJson)) dayclawResult = ParseDayclaw(dayclawJson, forecastJson, now);

            RadarSnapshot result = ChooseRecentSignal(timeline, feedResult) ?? dayclawResult ?? ParseForecast(forecastJson, now);
            if (result == null && posts.Count > 0)
            {
                result = QuietSnapshot(null);
                result.SourceUrl = posts[0].Url;
                result.Evidence = "最近公开内容：" + CleanEvidence(posts[0].Text);
            }
            if (result == null) throw new InvalidOperationException("主源和备用源均不可用：" + (feedError == null ? "没有可解析的公开消息" : feedError.Message));
            result.RecentPosts = posts;
            if (feedError != null)
            {
                string newest = posts.Count == 0 ? "无可用原帖" : "最近原帖 " + posts[0].PublishedUtc.LocalDateTime.ToString("MM-dd HH:mm");
                result.PublicFeedWarning = (usingCachedFeed ? "主源暂不可用 · 已用上次数据" : "主源暂不可用 · 仅备用源") + " · " + newest;
            }
            result.CacheEnvelope = BuildCache(feedJson, forecastJson, dayclawJson, xProfileHtml);
            return result;
        }

        private static RadarSnapshot ChooseRecentSignal(RadarSnapshot timeline, RadarSnapshot feed)
        {
            if (timeline == null) return feed;
            if (feed != null && feed.IsActionable && feed.SignalUtc.HasValue
                && (!timeline.SignalUtc.HasValue || feed.SignalUtc.Value > timeline.SignalUtc.Value)) return feed;
            return timeline;
        }

        internal static string FeedFromCache(string envelopeJson)
        {
            if (string.IsNullOrEmpty(envelopeJson)) return null;
            try
            {
                Dictionary<string, object> envelope = Dict(Json.DeserializeObject(envelopeJson));
                string feed = Str(Get(envelope, "feed"));
                return string.IsNullOrWhiteSpace(feed) ? null : feed;
            }
            catch { return null; }
        }

        internal static RadarSnapshot FromCache(string envelopeJson, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(envelopeJson)) return null;
            try
            {
                Dictionary<string, object> envelope = Dict(Json.DeserializeObject(envelopeJson));
                string feed = Str(Get(envelope, "feed"));
                string forecast = Str(Get(envelope, "forecast"));
                string dayclaw = Str(Get(envelope, "dayclaw"));
                string xProfile = Str(Get(envelope, "x_profile"));
                List<RecentPost> timelinePosts = MergeAllRecentPosts(ParseXProfilePosts(xProfile), ParseDayclawPosts(dayclaw));
                List<RecentPost> allPosts = MergeAllRecentPosts(ParseFeedPosts(feed), timelinePosts);
                List<RecentPost> posts = LimitRecentPosts(allPosts, 10);
                RadarSnapshot timeline = ParseRecentTimeline(allPosts, now);
                RadarSnapshot feedResult = string.IsNullOrEmpty(feed) ? null : ParseFeed(feed, forecast, now);
                RadarSnapshot result = ChooseRecentSignal(timeline, feedResult);
                if (result == null && !string.IsNullOrEmpty(dayclaw)) result = ParseDayclaw(dayclaw, forecast, now);
                if (result == null) result = ParseForecast(forecast, now);
                if (result == null && posts.Count > 0)
                {
                    result = QuietSnapshot(null);
                    result.SourceUrl = posts[0].Url;
                    result.Evidence = "最近公开内容：" + CleanEvidence(posts[0].Text);
                }
                if (result != null)
                {
                    result.RecentPosts = posts;
                    result.PublicFeedWarning = "离线缓存 · " + (posts.Count == 0 ? "无可用原帖" : "最新原帖 " + posts[0].PublishedUtc.LocalDateTime.ToString("MM-dd HH:mm"));
                }
                return result;
            }
            catch { }
            return null;
        }

        private static async Task<string> DownloadAsync(string url)
        {
            return await SafeHttp.DownloadAsync(url, "CodexResetRadar/1.3 (+local Windows tray widget)", null, null);
        }

        private static async Task<string> DownloadXProfileAsync()
        {
            return await SafeHttp.DownloadAsync(XProfileUrl, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140 Safari/537.36", null, null);
        }

        private static async Task<string> EnrichLatestFeedPostAsync(string feedJson, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(feedJson)) return feedJson;
            try
            {
                Dictionary<string, object> root = Dict(Json.DeserializeObject(feedJson));
                List<Dictionary<string, object>> events = DictList(Get(root, "events"));
                SortNewestFirst(events, "announced_at");
                foreach (Dictionary<string, object> item in events)
                {
                    if (!string.Equals(Str(Get(item, "type")), "reset", StringComparison.OrdinalIgnoreCase)) continue;
                    DateTimeOffset announced;
                    if (!TryDate(Get(item, "announced_at"), out announced) || announced > now || (now - announced).TotalDays >= 3) continue;
                    string id = Str(Get(item, "id"));
                    if (!Regex.IsMatch(id ?? string.Empty, @"^\d+$")) continue;

                    string fullText = await DownloadFullPostTextAsync(id);
                    string summary = Str(Get(item, "summary"));
                    if (!string.IsNullOrWhiteSpace(fullText) && fullText.Length > summary.Length)
                    {
                        item["summary"] = fullText;
                        return Json.Serialize(root);
                    }
                    break;
                }
            }
            catch { }
            return feedJson;
        }

        private static async Task<string> DownloadFullPostTextAsync(string id)
        {
            lock (FullPostCache)
            {
                string cached;
                if (FullPostCache.TryGetValue(id, out cached)) return cached;
            }

            string text = await DownloadFullPostTextFromAsync(FxTwitterStatusUrl + id, true);
            if (string.IsNullOrWhiteSpace(text)) text = await DownloadFullPostTextFromAsync(VxTwitterStatusUrl + id, false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                lock (FullPostCache) FullPostCache[id] = text;
            }
            return text;
        }

        private static async Task<string> DownloadFullPostTextFromAsync(string url, bool nestedTweet)
        {
            try
            {
                string json = await SafeHttp.DownloadAsync(url, "CodexResetRadar/1.3 (+local Windows tray widget)", null, null);
                Dictionary<string, object> root = Dict(Json.DeserializeObject(json));
                Dictionary<string, object> source = nestedTweet ? DictOrNull(Get(root, "tweet")) : root;
                return Str(Get(source, "text"));
            }
            catch { return string.Empty; }
        }

        private static List<RecentPost> ParseDayclawPosts(string dayclawJson)
        {
            List<RecentPost> posts = new List<RecentPost>();
            if (string.IsNullOrEmpty(dayclawJson)) return posts;
            try
            {
                Dictionary<string, object> root = Dict(Json.DeserializeObject(dayclawJson));
                foreach (Dictionary<string, object> item in DictList(Get(root, "items")))
                {
                    DateTimeOffset published;
                    if (!TryDate(Get(item, "published_at"), out published)) continue;
                    string text = Str(Get(item, "content"));
                    if (string.IsNullOrWhiteSpace(text)) text = Str(Get(item, "title"));
                    string id = Str(Get(item, "external_id"));
                    string url = Str(Get(item, "url"));
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(text)) continue;
                    posts.Add(new RecentPost { Id = id, Text = text, Url = url, ParentId = Str(Get(item, "in_reply_to_tweet_id")), IsOriginalPost = true, PublishedUtc = published });
                }
            }
            catch { }
            return posts;
        }

        private static List<RecentPost> ParseFeedPosts(string feedJson)
        {
            List<RecentPost> posts = new List<RecentPost>();
            if (string.IsNullOrEmpty(feedJson)) return posts;
            try
            {
                Dictionary<string, object> root = Dict(Json.DeserializeObject(feedJson));
                foreach (Dictionary<string, object> item in DictList(Get(root, "events")))
                {
                    DateTimeOffset published;
                    if (!TryDate(Get(item, "announced_at"), out published)) continue;
                    string text = Str(Get(item, "summary"));
                    if (string.IsNullOrWhiteSpace(text)) text = Str(Get(item, "content"));
                    string id = Str(Get(item, "id"));
                    string url = Str(Get(item, "url"));
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(text)) continue;
                    posts.Add(new RecentPost { Id = id, Text = text, Url = url, PublishedUtc = published });
                }

                // `events` is a curated reset-signal stream, while the same fresh feed also
                // includes the author's latest public timeline in `tweets`.  Include both:
                // the later merge deduplicates shared IDs and keeps the fuller text.
                foreach (Dictionary<string, object> item in DictList(Get(root, "tweets")))
                {
                    DateTimeOffset published;
                    if (!TryDate(Get(item, "at"), out published)) continue;
                    string text = Str(Get(item, "text"));
                    string id = Str(Get(item, "id"));
                    string url = Str(Get(item, "url"));
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(text)) continue;
                    posts.Add(new RecentPost { Id = id, Text = text, Url = url, ParentId = Str(Get(item, "in_reply_to_tweet_id")), IsOriginalPost = true, PublishedUtc = published });
                }
            }
            catch { }
            return posts;
        }

        private static List<RecentPost> ParseXProfilePosts(string html)
        {
            List<RecentPost> posts = new List<RecentPost>();
            if (string.IsNullOrEmpty(html)) return posts;
            try
            {
                Dictionary<string, string> noteTexts = ParseXNoteTweetTexts(html);
                MatchCollection messages = Regex.Matches(html, @"full_text:\\?\""(?<text>(?:\\.|[^\""\\])*)\\?\"",.*?created_at_ms:(?<milliseconds>\d+)", RegexOptions.Singleline);
                foreach (Match message in messages)
                {
                    int prefixStart = Math.Max(0, message.Index - 2500);
                    string prefix = html.Substring(prefixStart, message.Index - prefixStart);
                    MatchCollection identifiers = Regex.Matches(prefix, @"VHdlZXQ6(?<encoded>[A-Za-z0-9+/=]+):details");
                    if (identifiers.Count == 0) continue;
                    string id = DecodeTweetId(identifiers[identifiers.Count - 1].Groups["encoded"].Value);
                    long milliseconds;
                    if (string.IsNullOrEmpty(id) || !long.TryParse(message.Groups["milliseconds"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds)) continue;
                    string text = UnescapeXText(message.Groups["text"].Value);
                    string noteText;
                    if (noteTexts.TryGetValue(id, out noteText) && noteText.Length > text.Length) text = noteText;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    posts.Add(new RecentPost
                    {
                        Id = id,
                        Text = text,
                        Url = "https://x.com/thsottiaux/status/" + id,
                        IsOriginalPost = true,
                        PublishedUtc = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds)
                    });
                }
            }
            catch { }
            return posts;
        }

        private static Dictionary<string, string> ParseXNoteTweetTexts(string html)
        {
            Dictionary<string, string> noteTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            MatchCollection links = Regex.Matches(html, @"client:VHdlZXQ6(?<encoded>[A-Za-z0-9+/=]+):note_tweet\""[^}]*?note_tweet_results:\$R\[\d+\]=\{__ref:\""(?<result>[^\""\\]+)\""\}", RegexOptions.Singleline);
            foreach (Match link in links)
            {
                string tweetId = DecodeTweetId(link.Groups["encoded"].Value);
                string resultId = link.Groups["result"].Value;
                if (string.IsNullOrEmpty(tweetId) || string.IsNullOrEmpty(resultId)) continue;

                Match result = Regex.Match(html, Regex.Escape(resultId) + @":\$R\[\d+\]=\{.*?result:\$R\[\d+\]=\{__ref:\""(?<note>[^\""\\]+)\""\}", RegexOptions.Singleline);
                if (!result.Success) continue;
                string noteId = result.Groups["note"].Value;
                Match note = Regex.Match(html, @"""?" + Regex.Escape(noteId) + @"""?:\$R\[\d+\]=\{.*?__typename:\""NoteTweet\"",text:\""(?<text>(?:\\.|[^\""\\])*)\""", RegexOptions.Singleline);
                if (!note.Success) continue;

                string text = UnescapeXText(note.Groups["text"].Value);
                if (!string.IsNullOrWhiteSpace(text)) noteTexts[tweetId] = text;
            }
            return noteTexts;
        }

        private static string DecodeTweetId(string encodedSuffix)
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String("VHdlZXQ6" + encodedSuffix));
                Match id = Regex.Match(decoded, @"^Tweet:(\d+)$");
                return id.Success ? id.Groups[1].Value : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string UnescapeXText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = Regex.Replace(value, @"\\u(?<hex>[0-9a-fA-F]{4})", delegate(Match match)
            {
                return ((char)int.Parse(match.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
            });
            return value.Replace("\\n", Environment.NewLine).Replace("\\r", "\r").Replace("\\\"", "\"").Replace("\\/", "/").Replace("\\\\", "\\");
        }

        private static List<RecentPost> MergeRecentPosts(List<RecentPost> primary, List<RecentPost> secondary)
        {
            return LimitRecentPosts(MergeAllRecentPosts(primary, secondary), 10);
        }

        private static List<RecentPost> MergeAllRecentPosts(List<RecentPost> primary, List<RecentPost> secondary)
        {
            Dictionary<string, RecentPost> unique = new Dictionary<string, RecentPost>(StringComparer.Ordinal);
            AddRecentPosts(unique, primary);
            AddRecentPosts(unique, secondary);
            List<RecentPost> merged = new List<RecentPost>(unique.Values);
            foreach (RecentPost post in merged)
            {
                post.InheritedResetContext = false;
                RecentPost parent;
                if (!string.IsNullOrEmpty(post.ParentId) && unique.TryGetValue(post.ParentId, out parent)
                    && post.PublishedUtc >= parent.PublishedUtc && (post.PublishedUtc - parent.PublishedUtc).TotalHours <= 24
                    && parent.IsResetCard && Regex.IsMatch(post.Text, @"\b(?:land|lands|landing|arrive|arriving|done|propagated)\b", RegexOptions.IgnoreCase))
                {
                    post.InheritedResetContext = true;
                }
            }
            merged.Sort(delegate(RecentPost left, RecentPost right) { return right.PublishedUtc.CompareTo(left.PublishedUtc); });
            return merged;
        }

        private static List<RecentPost> LimitRecentPosts(List<RecentPost> posts, int maxCount)
        {
            if (posts == null || posts.Count <= maxCount) return posts ?? new List<RecentPost>();
            return posts.GetRange(0, maxCount);
        }

        private static void AddRecentPosts(Dictionary<string, RecentPost> unique, List<RecentPost> posts)
        {
            if (posts == null) return;
            foreach (RecentPost post in posts)
            {
                if (post == null || string.IsNullOrWhiteSpace(post.Id) || string.IsNullOrWhiteSpace(post.Text) || string.IsNullOrWhiteSpace(post.Url)) continue;
                RecentPost existing;
                if (!unique.TryGetValue(post.Id, out existing)) { unique[post.Id] = post; continue; }
                if ((post.IsOriginalPost && !existing.IsOriginalPost)
                    || (post.IsOriginalPost == existing.IsOriginalPost && post.Text.Length > existing.Text.Length))
                {
                    if (string.IsNullOrEmpty(post.ParentId)) post.ParentId = existing.ParentId;
                    unique[post.Id] = post;
                }
                else if (string.IsNullOrEmpty(existing.ParentId)) existing.ParentId = post.ParentId;
            }
        }

        private static RadarSnapshot ParseRecentTimeline(List<RecentPost> posts, DateTimeOffset now)
        {
            if (posts == null) return null;
            foreach (RecentPost post in posts)
            {
                if (post.PublishedUtc > now || (now - post.PublishedUtc).TotalDays >= 3 || !post.IsResetCard) continue;
                if (LooksConfirmed(post.Text) && (now - post.PublishedUtc).TotalHours < 24)
                {
                    return new RadarSnapshot
                    {
                        Mode = RadarMode.Confirmed,
                        StatusTitle = "已确认完成重置",
                        SourceLabel = "X 公开原帖 · 完成确认",
                        Evidence = "确认帖发布时间：" + post.PublishedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm") + Environment.NewLine + CleanEvidence(post.Text),
                        SourceUrl = post.Url,
                        ActionableId = post.Id + "|confirmed",
                        SignalUtc = post.PublishedUtc,
                        IsActionable = true
                    };
                }
                string timeNote;
                DateTimeOffset? target = InferExplicitTarget(post.Text, post.PublishedUtc, false, post.InheritedResetContext, out timeNote);
                if (!target.HasValue || (target.Value <= now && (now - target.Value).TotalHours >= 24)) continue;
                bool future = target.Value > now;
                return new RadarSnapshot
                {
                    Mode = future ? RadarMode.OfficialWindow : RadarMode.WindowElapsed,
                    StatusTitle = future ? "检测到公开重置预告" : "公开时间窗已到，等待确认",
                    SourceLabel = post.InheritedResetContext ? "X 回复 · 承接上文重置预告" : "X 公开原帖",
                    Evidence = TimedEvidence(post.Text, timeNote, target.Value),
                    SourceUrl = post.Url,
                    ActionableId = post.Id + (future ? "|window" : "|elapsed"),
                    SignalUtc = post.PublishedUtc,
                    TargetUtc = target,
                    IsDeadline = IsDeadlinePhrase(post.Text),
                    IsActionable = true
                };
            }
            return null;
        }

        private static RadarSnapshot ParseFeed(string feedJson, string forecastJson, DateTimeOffset now)
        {
            Dictionary<string, object> root = Dict(Json.DeserializeObject(feedJson));
            List<Dictionary<string, object>> events = DictList(Get(root, "events"));
            SortNewestFirst(events, "announced_at");
            Dictionary<string, object> latestReset = null;

            foreach (Dictionary<string, object> evt in events)
            {
                if (!string.Equals(Str(Get(evt, "type")), "reset", StringComparison.OrdinalIgnoreCase)) continue;
                if (latestReset == null) latestReset = evt;
                DateTimeOffset announced;
                bool dated = TryDate(Get(evt, "announced_at"), out announced);
                bool recent = dated && announced <= now && (now - announced).TotalDays < 3;
                string verification = Str(Get(evt, "reset_verification_status"));
                string state = Str(Get(evt, "announcement_state"));
                string summary = Str(Get(evt, "summary"));
                bool archivedCompletion = string.Equals(Str(Get(evt, "source")), "archive", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Str(Get(evt, "reset_kind")), "hard", StringComparison.OrdinalIgnoreCase)
                    && Regex.IsMatch(summary, @"\b(?:landed|arrived|propagated|processed|confirmed)\b|\band\s+reset\.\s*enjoy\b", RegexOptions.IgnoreCase);
                bool confirmed = string.Equals(verification, "confirmed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "confirmed", StringComparison.OrdinalIgnoreCase) || archivedCompletion;
                if (confirmed && recent && (now - announced).TotalHours < 24)
                {
                    return new RadarSnapshot
                    {
                        Mode = RadarMode.Confirmed,
                        StatusTitle = "已确认完成重置",
                        SourceLabel = "公开记录 · 已确认",
                        Evidence = "公开确认/监测时间：" + announced.LocalDateTime.ToString("yyyy-MM-dd HH:mm") + Environment.NewLine + CleanEvidence(summary),
                        SourceUrl = Str(Get(evt, "url")),
                        ActionableId = Str(Get(evt, "id")) + "|confirmed",
                        SignalUtc = announced,
                        IsActionable = true
                    };
                }

                // A usable feed can still omit official_window. Resolve each event before
                // selecting it so an older structured window cannot hide a newer timed post.
                string timeNote = string.Empty;
                DateTimeOffset? target = null;
                Dictionary<string, object> window = DictOrNull(Get(evt, "official_window"));
                DateTimeOffset end;
                if (window != null && TryDate(Get(window, "end_at"), out end)) target = end;
                else if (dated && !confirmed) target = InferExplicitTarget(Str(Get(evt, "summary")), announced, false, out timeNote);
                if (!target.HasValue || (!recent && dated) || confirmed) continue;
                bool future = target.Value > now;
                if (!future && (!recent || (now - target.Value).TotalHours >= 24)) continue;

                return new RadarSnapshot
                {
                    Mode = future ? RadarMode.OfficialWindow : RadarMode.WindowElapsed,
                    StatusTitle = future ? "检测到公开重置预告" : "公开时间窗已到，等待确认",
                    SourceLabel = future ? (string.IsNullOrEmpty(timeNote) ? "公开预告时间窗 · 按原帖所述" : "原帖时间解析 · 已标注时区") : "公开预告 · 尚未确认落地",
                    Evidence = TimedEvidence(Str(Get(evt, "summary")), timeNote, target.Value),
                    SourceUrl = Str(Get(evt, "url")),
                    ActionableId = Str(Get(evt, "id")) + (future ? "|window" : "|elapsed"),
                    SignalUtc = announced,
                    TargetUtc = target,
                    IsDeadline = IsDeadlinePhrase(summary),
                    IsActionable = true
                };
            }

            RadarSnapshot experimental = ParseForecast(forecastJson, now);
            if (experimental != null)
            {
                if (latestReset != null)
                {
                    experimental.SourceUrl = Str(Get(latestReset, "url"));
                    experimental.Evidence = experimental.Evidence + Environment.NewLine + "最近信号：" + CleanEvidence(Str(Get(latestReset, "summary")));
                }
                return experimental;
            }

            return QuietSnapshot(latestReset);
        }

        private static RadarSnapshot ParseDayclaw(string dayclawJson, string forecastJson, DateTimeOffset now)
        {
            Dictionary<string, object> root = Dict(Json.DeserializeObject(dayclawJson));
            List<Dictionary<string, object>> items = DictList(Get(root, "items"));
            SortNewestFirst(items, "published_at");
            foreach (Dictionary<string, object> item in items)
            {
                string text = Str(Get(item, "content"));
                if (string.IsNullOrEmpty(text)) text = Str(Get(item, "title"));
                DateTimeOffset published;
                if (!TryDate(Get(item, "published_at"), out published)) continue;
                if (published > now || (now - published).TotalDays >= 3) continue;
                if (LooksConfirmed(text) && (now - published).TotalHours < 24)
                {
                    return new RadarSnapshot
                    {
                        Mode = RadarMode.Confirmed,
                        StatusTitle = "检测到重置公告",
                        SourceLabel = "@thsottiaux 公开信号 · 备用源",
                        Evidence = CleanEvidence(text),
                        SourceUrl = Str(Get(item, "url")),
                        ActionableId = Str(Get(item, "external_id")) + "|confirmed",
                        IsActionable = true
                    };
                }
                string timeNote;
                DateTimeOffset? target = InferExplicitTarget(text, published, true, out timeNote);
                if (target.HasValue && (target.Value > now || (now - target.Value).TotalHours < 24))
                {
                    bool future = target.Value > now;
                    return new RadarSnapshot
                    {
                        Mode = future ? RadarMode.OfficialWindow : RadarMode.WindowElapsed,
                        StatusTitle = future ? "发现官方重置预告" : "公开时间窗已到，等待确认",
                        SourceLabel = future ? "原帖时间解析 · @thsottiaux 备用源" : "公开预告 · 尚未确认落地",
                        Evidence = TimedEvidence(text, timeNote, target.Value),
                        SourceUrl = Str(Get(item, "url")),
                        ActionableId = Str(Get(item, "external_id")) + (future ? "|window" : "|elapsed"),
                        TargetUtc = target,
                        IsActionable = true
                    };
                }
            }

            RadarSnapshot experimental = ParseForecast(forecastJson, now);
            return experimental ?? QuietSnapshot(items.Count > 0 ? items[0] : null);
        }

        private static RadarSnapshot ParseForecast(string forecastJson, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(forecastJson)) return null;
            try
            {
                Dictionary<string, object> root = Dict(Json.DeserializeObject(forecastJson));
                Dictionary<string, object> officialSignal = DictOrNull(Get(root, "official_signal"));
                Dictionary<string, object> signalWindow = officialSignal == null ? null : DictOrNull(Get(officialSignal, "window"));
                DateTimeOffset announced;
                DateTimeOffset end;
                if (officialSignal != null && signalWindow != null
                    && TryDate(Get(officialSignal, "at"), out announced) && announced <= now && (now - announced).TotalDays < 3
                    && TryDate(Get(signalWindow, "end_at"), out end) && (end > now || (now - end).TotalHours < 24))
                {
                    bool future = end > now;
                    string summary = Str(Get(officialSignal, "summary"));
                    string sourceUrl = Str(Get(officialSignal, "url"));
                    string id = Str(Get(officialSignal, "tweet_id"));
                    return new RadarSnapshot
                    {
                        Mode = future ? RadarMode.OfficialWindow : RadarMode.WindowElapsed,
                        StatusTitle = future ? "检测到公开重置信号" : "公开时间窗已到，等待确认",
                        SourceLabel = future ? "公开信号时间窗 · 聚合源标注" : "公开信号 · 尚未确认落地",
                        Evidence = TimedEvidence(summary, "时间窗来自公开源的结构化标注。", end),
                        SourceUrl = sourceUrl,
                        ActionableId = id + (future ? "|signal-window" : "|signal-elapsed"),
                        TargetUtc = end,
                        IsActionable = true
                    };
                }
                Dictionary<string, object> cadence = DictOrNull(Get(root, "cadence"));
                Dictionary<string, object> probabilities = DictOrNull(Get(root, "probabilities"));
                if (cadence == null) return null;
                double median = Number(Get(cadence, "recent_median_days"), -1);
                if (median <= 0) median = Number(Get(cadence, "weighted_mean_days"), -1);
                if (median <= 0) return null;
                int p24 = probabilities == null ? 0 : (int)Math.Round(Number(Get(probabilities, "rounded_24h"), 0));
                int p48 = probabilities == null ? 0 : (int)Math.Round(Number(Get(probabilities, "rounded_48h"), 0));
                return new RadarSnapshot
                {
                    Mode = RadarMode.Experimental,
                    StatusTitle = "暂无公开预告",
                    SourceLabel = "历史统计 · 不构成预测",
                    Evidence = "历史统计：未来 24 小时事件概率 " + p24 + "% · 未来 48 小时事件概率 " + p48 + "% 。这只反映过往频率，不代表下一次重置的时间。",
                    SourceUrl = "https://codex-reset.com/",
                    Probability24 = p24,
                    Probability48 = p48,
                    IsActionable = false
                };
            }
            catch { return null; }
        }

        private static RadarSnapshot QuietSnapshot(Dictionary<string, object> latest)
        {
            return new RadarSnapshot
            {
                Mode = RadarMode.Quiet,
                StatusTitle = "暂无公开重置预告",
                SourceLabel = "每 5 分钟继续检查",
                Evidence = latest == null ? "目前没有可用的重置信号。" : "最近公开内容：" + CleanEvidence(Str(Get(latest, "summary")) + Str(Get(latest, "content"))),
                SourceUrl = latest == null ? "https://x.com/thsottiaux" : Str(Get(latest, "url")),
                IsActionable = false
            };
        }

        private static DateTimeOffset? InferExplicitTarget(string text, DateTimeOffset published, bool allowDateOnly, out string timeNote)
        {
            return InferExplicitTarget(text, published, allowDateOnly, false, out timeNote);
        }

        private static DateTimeOffset? InferExplicitTarget(string text, DateTimeOffset published, bool allowDateOnly, bool inheritedResetContext, out string timeNote)
        {
            timeNote = string.Empty;
            if (string.IsNullOrEmpty(text)) return null;
            string lower = text.ToLowerInvariant();
            if (!inheritedResetContext && !ContainsResetContext(lower)) return null;
            if (HasCancelledReset(lower)) return null;
            if (Regex.IsMatch(lower, @"\b(?:utc|gmt)\s*[+-]")) return null;

            Match eod = Regex.Match(text, @"\b(?:by\s+)?(?:EOD|end\s+of\s+day)\s+(?<zone>PST|PDT|PT)\b", RegexOptions.IgnoreCase);
            if (eod.Success)
            {
                string zone = eod.Groups["zone"].Value.ToUpperInvariant();
                TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                TimeSpan offset = TimeSpan.FromHours(zone == "PST" ? -8 : -7);
                DateTime sourceDate = zone == "PT" ? TimeZoneInfo.ConvertTime(published, pacific).Date : published.ToOffset(offset).Date;
                if (Regex.IsMatch(lower, @"\btomorrow\b")) sourceDate = sourceDate.AddDays(1);
                DateTime localClock = DateTime.SpecifyKind(sourceDate.AddHours(23).AddMinutes(59), DateTimeKind.Unspecified);
                DateTimeOffset? deadline = ClockToUtc(localClock, zone, offset, pacific);
                if (!deadline.HasValue) return null;
                timeNote = "原文截止：EOD " + zone + "（按该时区当日 23:59 估算）";
                if (zone == "PST" && pacific.IsDaylightSavingTime(localClock))
                    timeNote += Environment.NewLine + "若作者实际指 PDT 夏令时，截止时间提前 1 小时。";
                return deadline;
            }

            MatchCollection clocks = Regex.Matches(text, @"(?<![\w:])(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<period>a\.?m\.?|p\.?m\.?)?\s*(?<zone>PST|PDT|PT|UTC|GMT)\b", RegexOptions.IgnoreCase);
            if (clocks.Count > 0)
            {
                if (clocks.Count != 1) return null;
                string otherText = text.Remove(clocks[0].Index, clocks[0].Length);
                if (Regex.IsMatch(otherText, @"\b\d{1,2}(?::\d{2})?\s*(?:a\.?m\.?|p\.?m\.?)\b|\b\d{1,2}:\d{2}\b", RegexOptions.IgnoreCase)) return null;
                return ParseClockTarget(clocks[0], lower, published, out timeNote);
            }
            // Never turn an unqualified clock time into the legacy date-only estimate.
            if (Regex.IsMatch(lower, @"\d\s*(?:a\.?m\.?|p\.?m\.?)\b|\d:\d")) return null;
            Match hour = Regex.Match(lower, @"(?:next|within|in)\s+(?:(?:about|around|roughly|approximately|approx\.?|~)\s+)?(?:the\s+)?(\d+(?:\.\d+)?)\s*hours?");
            if (hour.Success)
            {
                double value;
                if (double.TryParse(hour.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && value <= 72) return published.AddHours(value);
            }
            if (Regex.IsMatch(lower, @"next\s+hour|within\s+(?:an?|the)\s+hour|in\s+an?\s+hour")) return published.AddHours(1);
            if (!allowDateOnly) return null;
            // Preserve the backup's legacy date-only estimate, without promoting it
            // into a precise main-feed announcement when no clock time is supplied.
            timeNote = "原文仅给出日期；以下为备用源的日期估算，并非官宣具体时刻。";
            DateTimeOffset utcDate = new DateTimeOffset(published.UtcDateTime.Date, TimeSpan.Zero);
            if (lower.Contains("tomorrow")) return utcDate.AddDays(1).AddHours(12);
            Match weekday = Regex.Match(lower, @"\bon\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b");
            if (weekday.Success)
            {
                DayOfWeek wanted = (DayOfWeek)Enum.Parse(typeof(DayOfWeek), CultureInfo.InvariantCulture.TextInfo.ToTitleCase(weekday.Groups[1].Value));
                int delta = ((int)wanted - (int)utcDate.DayOfWeek + 7) % 7;
                if (delta == 0) delta = 7;
                return utcDate.AddDays(delta).AddHours(12);
            }
            return null;
        }

        private static DateTimeOffset? ParseClockTarget(Match clock, string lower, DateTimeOffset published, out string timeNote)
        {
            timeNote = string.Empty;
            // Unsupported calendar dates and multiple deadlines must not silently
            // become today's deadline. Publication time, never polling time, anchors it.
            if (Regex.IsMatch(lower, @"\b(?:yesterday|last|next\s+week|day\s+after\s+tomorrow|january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec)\b|\b\d{4}-\d{1,2}-\d{1,2}\b|\b\d{1,2}/\d{1,2}\b")) return null;
            int hour = int.Parse(clock.Groups["hour"].Value, CultureInfo.InvariantCulture);
            int minute = clock.Groups["minute"].Success ? int.Parse(clock.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
            string period = clock.Groups["period"].Value.ToLowerInvariant().Replace(".", "");
            if (minute > 59 || (period.Length == 0 ? hour > 23 : hour < 1 || hour > 12)) return null;
            if (period.Length > 0) hour = hour % 12 + (period == "pm" ? 12 : 0);

            string zoneName = clock.Groups["zone"].Value.ToUpperInvariant();
            TimeSpan offset = TimeSpan.FromHours(zoneName == "PST" ? -8 : zoneName == "PDT" ? -7 : 0);
            TimeZoneInfo pacific = zoneName == "PT" || zoneName == "PST" ? TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time") : null;
            DateTime sourceDate = zoneName == "PT" ? TimeZoneInfo.ConvertTime(published, pacific).Date : published.ToOffset(offset).Date;
            bool tomorrow = Regex.IsMatch(lower, @"\btomorrow\b");
            bool explicitDay = tomorrow || Regex.IsMatch(lower, @"\b(?:today|tonight)\b");
            if (tomorrow) sourceDate = sourceDate.AddDays(1);
            Match weekday = Regex.Match(lower, @"\b(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b");
            if (weekday.Success)
            {
                if (tomorrow) return null;
                if (Regex.Matches(lower, @"\b(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b").Count != 1) return null;
                DayOfWeek wanted = (DayOfWeek)Enum.Parse(typeof(DayOfWeek), weekday.Groups[1].Value, true);
                int delta = ((int)wanted - (int)sourceDate.DayOfWeek + 7) % 7;
                if (delta == 0 && Regex.IsMatch(lower, @"\bnext\s+" + weekday.Groups[1].Value + @"\b")) delta = 7;
                sourceDate = sourceDate.AddDays(delta);
                explicitDay = true;
            }
            DateTime localClock = DateTime.SpecifyKind(sourceDate.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            DateTimeOffset? target = ClockToUtc(localClock, zoneName, offset, pacific);
            if (!target.HasValue) return null;
            if (!explicitDay && target.Value < published && Regex.IsMatch(lower, @"\b(?:will|landing|land|arriving|arrive|by)\b"))
            {
                localClock = localClock.AddDays(1);
                target = ClockToUtc(localClock, zoneName, offset, pacific);
                if (!target.HasValue) return null;
            }
            if (zoneName == "PT") offset = pacific.GetUtcOffset(localClock);
            string offsetText = "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            timeNote = (IsDeadlinePhrase(lower) ? "原文截止：" : "原文时间：") + clock.Value.Trim() + "（" + offsetText + "）";
            if (zoneName == "PST" && pacific.IsDaylightSavingTime(localClock))
            {
                timeNote += Environment.NewLine + "按原文 PST 标准时换算；若作者实际指 PDT 夏令时，则为 " + target.Value.AddHours(-1).LocalDateTime.ToString("MM-dd HH:mm") + "（本地）。";
            }
            return target;
        }

        private static DateTimeOffset? ClockToUtc(DateTime clock, string zoneName, TimeSpan offset, TimeZoneInfo pacific)
        {
            if (zoneName == "PT")
            {
                if (pacific.IsInvalidTime(clock) || pacific.IsAmbiguousTime(clock)) return null;
                offset = pacific.GetUtcOffset(clock);
            }
            return new DateTimeOffset(clock, offset).ToUniversalTime();
        }

        private static string TimedEvidence(string text, string timeNote, DateTimeOffset target)
        {
            return (timeNote.StartsWith("原文截止：", StringComparison.Ordinal) ? "最迟时间：" : "预告时间：")
                + target.LocalDateTime.ToString("yyyy-MM-dd HH:mm") + "（本地时间）" + Environment.NewLine
                + (string.IsNullOrEmpty(timeNote) ? string.Empty : timeNote + Environment.NewLine)
                + CleanEvidence(text);
        }

        private static bool IsDeadlinePhrase(string text)
        {
            return !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"\b(?:before|by|no\s+later\s+than|eod|end\s+of\s+day)\b", RegexOptions.IgnoreCase);
        }

        private static void SortNewestFirst(List<Dictionary<string, object>> items, string dateKey)
        {
            items.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
            {
                DateTimeOffset leftDate;
                DateTimeOffset rightDate;
                if (!TryDate(Get(left, dateKey), out leftDate)) leftDate = DateTimeOffset.MinValue;
                if (!TryDate(Get(right, dateKey), out rightDate)) rightDate = DateTimeOffset.MinValue;
                return rightDate.CompareTo(leftDate);
            });
        }

        private static bool ContainsResetContext(string lower)
        {
            return RecentPost.IsResetCardText(lower) || (lower.Contains("reset") && (lower.Contains("codex") || lower.Contains("usage") || lower.Contains("limit") || lower.Contains("surprise")));
        }

        internal static bool LooksConfirmed(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string lower = text.ToLowerInvariant();
            if (HasCancelledReset(lower)) return false;
            if (Regex.IsMatch(lower, @"\b(?:will|should|tomorrow|scheduled|expected|going\s+to)\b|\bby\s+(?:eod|end\s+of\s+day)\b")) return false;
            return Regex.IsMatch(lower, @"\b(?:have|has|been|is|are|was|were)\s+reset\b|\b(?:we|i)\s+reset\s+(usage|rate)\s+limits\b|it\s+is\s+done|enjoy\s+(a\s+)?(nice\s+)?reset|\band\s+reset\.\s*enjoy\b|\breset\b.{0,60}\ball\s+propagated\b|\bit['’]s\s+done\b|\bdone\s+and\s+dusted\b");
        }

        private static bool HasCancelledReset(string lower)
        {
            if (string.IsNullOrEmpty(lower)) return false;
            return Regex.IsMatch(lower, @"\b(?:no|never)\s+(?:\w+\s+){0,3}reset\b"
                + @"|\b(?:will\s+not|won['’]?t|not\s+going\s+to)\s+(?:\w+\s+){0,3}(?:reset|land|arriv(?:e|ing|al)|come)\b"
                + @"|\breset\b[^.!?\r\n]{0,60}\b(?:will\s+not|won['’]?t|not\s+going\s+to|cancelled|canceled|postponed)\b"
                + @"|\b(?:cancelled|canceled|postponed)\b[^.!?\r\n]{0,30}\breset\b");
        }

        private static string CleanEvidence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "没有附带摘要。";
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= 280 ? text : text.Substring(0, 277) + "…";
        }

        private static string BuildCache(string feed, string forecast)
        {
            return BuildCache(feed, forecast, null);
        }

        private static string BuildCache(string feed, string forecast, string dayclaw)
        {
            return BuildCache(feed, forecast, dayclaw, null);
        }

        private static string BuildCache(string feed, string forecast, string dayclaw, string xProfile)
        {
            Dictionary<string, object> envelope = new Dictionary<string, object>();
            envelope["feed"] = feed;
            envelope["forecast"] = forecast;
            envelope["dayclaw"] = dayclaw;
            envelope["x_profile"] = xProfile;
            envelope["saved_at"] = DateTimeOffset.UtcNow.ToString("o");
            return Json.Serialize(envelope);
        }

        private static Dictionary<string, object> Dict(object value)
        {
            Dictionary<string, object> result = value as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("JSON 对象格式无效");
            return result;
        }

        private static Dictionary<string, object> DictOrNull(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static List<Dictionary<string, object>> DictList(object value)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            IEnumerable sequence = value as IEnumerable;
            if (sequence == null || value is string) return result;
            foreach (object item in sequence)
            {
                Dictionary<string, object> dict = item as Dictionary<string, object>;
                if (dict != null) result.Add(dict);
            }
            return result;
        }

        private static object Get(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object value;
            return dict.TryGetValue(key, out value) ? value : null;
        }

        private static string Str(object value)
        {
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double Number(object value, double fallback)
        {
            if (value == null) return fallback;
            double number;
            return double.TryParse(Str(value), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : fallback;
        }

        private static bool TryDate(object value, out DateTimeOffset result)
        {
            return DateTimeOffset.TryParse(Str(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
        }

        internal static RadarSnapshot ParseFeedForTests(string feed, string forecast, DateTimeOffset now)
        {
            return ParseFeed(feed, forecast, now);
        }

        internal static RadarSnapshot ParseDayclawForTests(string dayclaw, string forecast, DateTimeOffset now)
        {
            return ParseDayclaw(dayclaw, forecast, now);
        }

        internal static List<RecentPost> ParseXProfileForTests(string html)
        {
            return ParseXProfilePosts(html);
        }

        internal static List<RecentPost> ParseFeedPostsForTests(string feed)
        {
            return ParseFeedPosts(feed);
        }

        internal static RadarSnapshot ParseRecentTimelineForTests(List<RecentPost> posts, DateTimeOffset now)
        {
            return ParseRecentTimeline(posts, now);
        }

        internal static List<RecentPost> MergeRecentPostsForTests(List<RecentPost> primary, List<RecentPost> secondary)
        {
            return MergeRecentPosts(primary, secondary);
        }

        internal static List<RecentPost> MergeAllRecentPostsForTests(List<RecentPost> primary, List<RecentPost> secondary)
        {
            return MergeAllRecentPosts(primary, secondary);
        }

        internal static string BuildCacheForTests(string feed, string forecast, string dayclaw, string xProfile)
        {
            return BuildCache(feed, forecast, dayclaw, xProfile);
        }
    }

    internal static class ReleaseUpdater
    {
        internal const string CurrentVersion = "1.0.1";
        private const string ManifestUrl = "https://raw.githubusercontent.com/987144016/codex-reset-radar/main/latest.json";
        private const string ReleasePrefix = "https://github.com/987144016/codex-reset-radar/releases/download/";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static async Task<string> CheckAndPrepareAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                string body = await SafeHttp.DownloadAsync(ManifestUrl, "CodexResetRadar/" + CurrentVersion, null, null);
                Dictionary<string, object> manifest = Json.Deserialize<Dictionary<string, object>>(body);
                Version latest;
                Version current;
                string version = Convert.ToString(manifest["version"], CultureInfo.InvariantCulture);
                if (!Version.TryParse(version.TrimStart('v', 'V'), out latest)
                    || !Version.TryParse(CurrentVersion, out current) || latest <= current) return null;
                string url = Convert.ToString(manifest["url"], CultureInfo.InvariantCulture);
                string expectedHash = Convert.ToString(manifest["sha256"], CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(url) || !url.StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase)
                    || !url.EndsWith("/CodexResetRadar.exe", StringComparison.OrdinalIgnoreCase)
                    || !Regex.IsMatch(expectedHash ?? string.Empty, @"^[0-9a-fA-F]{64}$"))
                    throw new InvalidDataException("更新清单的下载地址或校验值无效");

                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexResetRadar", "updates", "v" + latest.ToString());
                Directory.CreateDirectory(directory);
                string staged = Path.Combine(directory, "CodexResetRadar.exe");
                if (File.Exists(staged) && MatchesHash(File.ReadAllBytes(staged), expectedHash)) return staged;
                byte[] bytes;
                using (TimeoutWebClient client = new TimeoutWebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "CodexResetRadar/" + CurrentVersion;
                    Task<byte[]> download = client.DownloadDataTaskAsync(new Uri(url));
                    Task finished = await Task.WhenAny(download, Task.Delay(20000));
                    if (finished != download) { client.CancelAsync(); throw new TimeoutException("更新包下载超时"); }
                    bytes = await download;
                }
                if (bytes.Length < 30000 || bytes.Length > 10 * 1024 * 1024 || !MatchesHash(bytes, expectedHash))
                    throw new InvalidDataException("更新包 SHA-256 校验失败");
                string temporary = staged + ".download";
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(staged)) File.Delete(staged);
                File.Move(temporary, staged);
                Log("已下载 v" + latest.ToString() + "，等待替换");
                return staged;
            }
            catch (Exception ex)
            {
                Log("检查更新失败：" + ex.Message);
                return null;
            }
        }

        internal static bool StartApply(string staged, string target)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(staged);
                start.Arguments = "--apply-update " + Quote(target) + " " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(start);
                return true;
            }
            catch (Exception ex) { Log("启动更新失败：" + ex.Message); return false; }
        }

        internal static int ApplyStagedUpdate(string[] args)
        {
            if (args == null || args.Length < 3) return 2;
            string target;
            int oldPid;
            try { target = Path.GetFullPath(args[1]); }
            catch { return 2; }
            if (!string.Equals(Path.GetFileName(target), "CodexResetRadar.exe", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out oldPid)
                || oldPid <= 0 || string.Equals(target, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return 2;
            string restartArguments = args.Length >= 4 && string.Equals(args[3], "--self-test", StringComparison.OrdinalIgnoreCase)
                ? "--self-test" : "--background";
            try
            {
                try { using (Process old = Process.GetProcessById(oldPid)) old.WaitForExit(30000); }
                catch (ArgumentException) { }
                string backup = target + ".previous";
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        File.Copy(target, backup, true);
                        File.Copy(Application.ExecutablePath, target, true);
                        Process.Start(new ProcessStartInfo(target, restartArguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                        Log("更新完成，已重新启动");
                        return 0;
                    }
                    catch (IOException) { Thread.Sleep(500); }
                }
                throw new IOException("无法替换正在运行的程序");
            }
            catch (Exception ex)
            {
                Log("应用更新失败：" + ex.Message);
                try
                {
                    string backup = target + ".previous";
                    if (File.Exists(backup)) File.Copy(backup, target, true);
                    if (File.Exists(target)) Process.Start(new ProcessStartInfo(target, restartArguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                }
                catch { }
                return 3;
            }
        }

        private static bool MatchesHash(byte[] bytes, string expected)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Equals(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string argument)
        {
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }

        private static void Log(string message)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexResetRadar");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "update.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal sealed class LocalQuotaSample
    {
        internal string AccountId;
        internal int WindowSeconds;
        internal double UsedPercent;
        internal DateTimeOffset ResetUtc;
        internal DateTimeOffset CheckedUtc;

        internal static LocalQuotaSample FromInfo(LocalQuotaInfo info, DateTimeOffset now)
        {
            if (info == null) return null;
            LocalQuotaWindow window = info.GeneralWeeklyWindow();
            if (window == null || !window.ResetUtc.HasValue || window.UsedPercent < 0 || window.UsedPercent > 100) return null;
            return new LocalQuotaSample { AccountId = info.AccountId, WindowSeconds = window.WindowSeconds,
                UsedPercent = window.UsedPercent, ResetUtc = window.ResetUtc.Value, CheckedUtc = now };
        }
    }

    internal sealed class LocalResetReceipt
    {
        internal string AccountId;
        internal double BeforePercent;
        internal double AfterPercent;
        internal DateTimeOffset BeforeUtc;
        internal DateTimeOffset ObservedUtc;
    }

    internal static class QuotaResetDetector
    {
        internal static void Apply(RadarSnapshot snapshot, RadarState state, LocalQuotaSample current, DateTimeOffset now)
        {
            if (snapshot == null || state == null || current == null) return;
            LocalQuotaSample before = state.LastQuota;
            if (before != null && !string.IsNullOrEmpty(current.AccountId)
                && string.Equals(before.AccountId, current.AccountId, StringComparison.Ordinal)
                && before.WindowSeconds == current.WindowSeconds
                && current.CheckedUtc > before.CheckedUtc && (current.CheckedUtc - before.CheckedUtc).TotalHours <= 48
                && before.ResetUtc > current.CheckedUtc.AddHours(1)
                && before.UsedPercent >= 5 && before.UsedPercent - current.UsedPercent >= 5
                && current.UsedPercent <= 2)
            {
                state.LocalReset = new LocalResetReceipt { AccountId = current.AccountId,
                    BeforePercent = before.UsedPercent, AfterPercent = current.UsedPercent,
                    BeforeUtc = before.CheckedUtc, ObservedUtc = current.CheckedUtc };
            }

            LocalResetReceipt receipt = state.LocalReset;
            if (receipt == null || !string.Equals(receipt.AccountId, current.AccountId, StringComparison.Ordinal)
                || now < receipt.ObservedUtc || (now - receipt.ObservedUtc).TotalHours >= 24
                || (snapshot.SignalUtc.HasValue && snapshot.SignalUtc.Value > receipt.ObservedUtc)) return;

            string localEvidence = "本机每周额度：" + receipt.BeforePercent.ToString("0.#", CultureInfo.InvariantCulture) + "% → "
                + receipt.AfterPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% 。"
                + Environment.NewLine + "两次读取：" + receipt.BeforeUtc.LocalDateTime.ToString("MM-dd HH:mm") + " 至 "
                + receipt.ObservedUtc.LocalDateTime.ToString("MM-dd HH:mm") + "；实际变化发生在这段时间内。"
                + Environment.NewLine + "仅确认当前登录账号的额度变化。";
            if (snapshot.Mode == RadarMode.Confirmed)
            {
                snapshot.Evidence = localEvidence + Environment.NewLine + snapshot.Evidence;
                return;
            }
            snapshot.Mode = RadarMode.Confirmed;
            snapshot.StatusTitle = "本机每周额度已重置";
            snapshot.SourceLabel = "本机 Codex 额度 · 前后两次读取";
            snapshot.Evidence = localEvidence + (string.IsNullOrEmpty(snapshot.Evidence) ? string.Empty : Environment.NewLine + snapshot.Evidence);
            snapshot.ActionableId = "local-weekly-reset|" + receipt.ObservedUtc.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            snapshot.IsActionable = true;
        }
    }

    internal sealed class RadarState
    {
        internal bool Initialized;
        internal string LastNotifiedId;
        internal string LastGoodJson;
        internal LocalQuotaSample LastQuota;
        internal LocalResetReceipt LocalReset;

        private static string StatePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexResetRadar", "state.json");
            }
        }

        internal static RadarState Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return new RadarState();
                JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                Dictionary<string, object> data = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(StatePath, Encoding.UTF8));
                RadarState state = new RadarState();
                object value;
                if (data.TryGetValue("initialized", out value)) state.Initialized = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                if (data.TryGetValue("last_notified_id", out value)) state.LastNotifiedId = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (data.TryGetValue("last_good_json", out value)) state.LastGoodJson = Convert.ToString(value, CultureInfo.InvariantCulture);
                try
                {
                    Dictionary<string, object> quota = data.TryGetValue("last_quota", out value) ? value as Dictionary<string, object> : null;
                    if (quota != null) state.LastQuota = new LocalQuotaSample {
                        AccountId = Convert.ToString(quota["account_id"], CultureInfo.InvariantCulture),
                        WindowSeconds = Convert.ToInt32(quota["window_seconds"], CultureInfo.InvariantCulture),
                        UsedPercent = Convert.ToDouble(quota["used_percent"], CultureInfo.InvariantCulture),
                        ResetUtc = DateTimeOffset.Parse(Convert.ToString(quota["reset_utc"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                        CheckedUtc = DateTimeOffset.Parse(Convert.ToString(quota["checked_utc"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                    };
                }
                catch { }
                try
                {
                    Dictionary<string, object> receipt = data.TryGetValue("local_reset", out value) ? value as Dictionary<string, object> : null;
                    if (receipt != null) state.LocalReset = new LocalResetReceipt {
                        AccountId = Convert.ToString(receipt["account_id"], CultureInfo.InvariantCulture),
                        BeforePercent = Convert.ToDouble(receipt["before_percent"], CultureInfo.InvariantCulture),
                        AfterPercent = Convert.ToDouble(receipt["after_percent"], CultureInfo.InvariantCulture),
                        BeforeUtc = DateTimeOffset.Parse(Convert.ToString(receipt["before_utc"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                        ObservedUtc = DateTimeOffset.Parse(Convert.ToString(receipt["observed_utc"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                    };
                }
                catch { }
                return state;
            }
            catch { return new RadarState(); }
        }

        internal void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(StatePath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                Dictionary<string, object> data = new Dictionary<string, object>();
                data["initialized"] = Initialized;
                data["last_notified_id"] = LastNotifiedId;
                data["last_good_json"] = LastGoodJson;
                if (LastQuota != null) data["last_quota"] = new {
                    account_id = LastQuota.AccountId, window_seconds = LastQuota.WindowSeconds, used_percent = LastQuota.UsedPercent,
                    reset_utc = LastQuota.ResetUtc.ToString("o"), checked_utc = LastQuota.CheckedUtc.ToString("o")
                };
                if (LocalReset != null) data["local_reset"] = new {
                    account_id = LocalReset.AccountId, before_percent = LocalReset.BeforePercent, after_percent = LocalReset.AfterPercent,
                    before_utc = LocalReset.BeforeUtc.ToString("o"), observed_utc = LocalReset.ObservedUtc.ToString("o")
                };
                data["saved_at"] = DateTimeOffset.UtcNow.ToString("o");
                JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                string temporary = StatePath + ".tmp";
                File.WriteAllText(temporary, json.Serialize(data), new UTF8Encoding(false));
                if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null);
                else File.Move(temporary, StatePath);
            }
            catch { }
        }
    }

    internal static class SelfTests
    {
        private static int count;

        internal static int Run()
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.Parse("2026-08-13T01:30:00Z", CultureInfo.InvariantCulture);
                string feed = "{\"events\":[{\"id\":\"1\",\"type\":\"reset\",\"summary\":\"Landing in the next hour\",\"url\":\"https://x.test/1\",\"announced_at\":\"2026-08-13T01:00:00Z\",\"official_window\":{\"start_at\":\"2026-08-13T01:00:00Z\",\"end_at\":\"2026-08-13T02:00:00Z\"}}]}";
                RadarSnapshot officialResult = RadarClient.ParseFeedForTests(feed, null, now);
                Assert(officialResult.Mode == RadarMode.OfficialWindow, "official window mode");
                Assert(officialResult.GetCountdownText(now).Contains("不足 1 小时"), "official countdown");

                RadarSnapshot elapsedResult = RadarClient.ParseFeedForTests(feed, null, now.AddHours(1));
                Assert(elapsedResult.Mode == RadarMode.WindowElapsed, "elapsed window mode");
                Assert(elapsedResult.GetCountdownText(now.AddHours(1)).Contains("等待确认"), "elapsed window text");

                string forecast = "{\"age_days\":0.3,\"probabilities\":{\"rounded_24h\":30,\"rounded_48h\":50},\"cadence\":{\"recent_median_days\":2.3}}";
                RadarSnapshot experimental = RadarClient.ParseFeedForTests("{\"events\":[]}", forecast, now);
                Assert(experimental.Mode == RadarMode.Experimental, "experimental mode");
                Assert(experimental.GetCountdownText(now).Contains("暂无可核实"), "experimental has no ETA");

                string signalForecast = "{\"cadence\":{\"recent_median_days\":2.3},\"official_signal\":{\"tweet_id\":\"signal-1\",\"summary\":\"A global reset lands at 6pm PST today.\",\"url\":\"https://x.test/signal-1\",\"at\":\"2026-08-13T01:00:00Z\",\"window\":{\"end_at\":\"2026-08-13T02:00:00Z\"}}}";
                RadarSnapshot structuredSignal = RadarClient.ParseFeedForTests("{\"events\":[]}", signalForecast, now);
                Assert(structuredSignal.Mode == RadarMode.OfficialWindow && structuredSignal.TargetUtc == now.AddMinutes(30), "structured public signal window is actionable when full post text is unavailable");

                string localUsage = "{\"rate_limit\":{\"primary_window\":{\"used_percent\":1,\"limit_window_seconds\":604800,\"reset_at\":1787275800}},\"additional_rate_limits\":[{\"limit_name\":\"GPT-5.3-Codex-Spark\",\"rate_limit\":{\"primary_window\":{\"used_percent\":12,\"limit_window_seconds\":18000,\"reset_at\":1787241600},\"secondary_window\":{\"used_percent\":12,\"limit_window_seconds\":604800,\"reset_at\":1787275800}}}]}";
                LocalQuotaInfo localQuota = LocalQuotaClient.ParseForTests(localUsage, now);
                experimental.LocalQuota = localQuota;
                Assert(experimental.GetCountdownText(now).Contains("本机每周额度"), "general quota replaces experimental ETA");
                Assert(localQuota.GetEvidence(now).Contains("5 小时窗口"), "local quota evidence");
                Assert(localQuota.GetEvidence(now).Contains("GPT-5.3-Codex-Spark"), "model quota is separate");
                LocalQuotaSample beforeReset = new LocalQuotaSample { AccountId = "same-account", WindowSeconds = 604800,
                    UsedPercent = 11, ResetUtc = now.AddDays(7), CheckedUtc = now.AddMinutes(-5) };
                LocalQuotaSample afterReset = new LocalQuotaSample { AccountId = "same-account", WindowSeconds = 604800,
                    UsedPercent = 0, ResetUtc = now.AddDays(7), CheckedUtc = now };
                RadarState resetState = new RadarState { LastQuota = beforeReset };
                RadarSnapshot pendingReset = new RadarSnapshot { Mode = RadarMode.WindowElapsed, StatusTitle = "等待确认",
                    SignalUtc = now.AddHours(-1), Evidence = "公开预告", SourceUrl = "https://x.test/reset" };
                QuotaResetDetector.Apply(pendingReset, resetState, afterReset, now);
                Assert(pendingReset.Mode == RadarMode.Confirmed && resetState.LocalReset != null && pendingReset.Evidence.Contains("11% → 0%"), "early weekly quota drop confirms the local account reset");
                resetState.LastQuota = afterReset;
                RadarSnapshot nextPoll = new RadarSnapshot { Mode = RadarMode.WindowElapsed, SignalUtc = now.AddHours(-1), Evidence = "公开预告" };
                QuotaResetDetector.Apply(nextPoll, resetState, new LocalQuotaSample { AccountId = "same-account", WindowSeconds = 604800,
                    UsedPercent = 1, ResetUtc = now.AddDays(7), CheckedUtc = now.AddMinutes(5) }, now.AddMinutes(5));
                Assert(nextPoll.Mode == RadarMode.Confirmed, "local confirmation persists across later polling cycles");
                RadarState naturalState = new RadarState { LastQuota = new LocalQuotaSample { AccountId = "same-account", WindowSeconds = 604800,
                    UsedPercent = 11, ResetUtc = now.AddMinutes(-1), CheckedUtc = now.AddMinutes(-5) } };
                RadarSnapshot naturalWindow = new RadarSnapshot { Mode = RadarMode.WindowElapsed, SignalUtc = now.AddHours(-1) };
                QuotaResetDetector.Apply(naturalWindow, naturalState, afterReset, now);
                Assert(naturalWindow.Mode == RadarMode.WindowElapsed && naturalState.LocalReset == null, "normal scheduled weekly rollover is not mistaken for a special reset");
                RadarState otherAccount = new RadarState { LastQuota = beforeReset };
                RadarSnapshot otherWindow = new RadarSnapshot { Mode = RadarMode.WindowElapsed, SignalUtc = now.AddHours(-1) };
                QuotaResetDetector.Apply(otherWindow, otherAccount, new LocalQuotaSample { AccountId = "other-account", WindowSeconds = 604800,
                    UsedPercent = 0, ResetUtc = now.AddDays(7), CheckedUtc = now }, now);
                Assert(otherWindow.Mode == RadarMode.WindowElapsed, "switching accounts is not mistaken for a quota reset");

                string dayclaw = "{\"items\":[{\"external_id\":\"2\",\"content\":\"Codex usage reset surprise tomorrow\",\"url\":\"https://x.test/2\",\"published_at\":\"2026-08-13T01:00:00Z\"}]}";
                RadarSnapshot fallback = RadarClient.ParseDayclawForTests(dayclaw, null, now);
                Assert(fallback.Mode == RadarMode.OfficialWindow, "Dayclaw future fallback");
                Assert(!NotificationRules.ShouldNotify(true, null, "2", true), "first-run suppression");
                Assert(NotificationRules.ShouldNotify(false, "1", "2", true), "new signal notification");
                Assert(TranslationClient.ParseTranslation("{\"responseData\":{\"translatedText\":\"&quot;重置&quot;\"}}").Contains("重置"), "translation JSON parsing");
                string longTranslationInput = "Intro paragraph.\n\n" + new string('x', 1000);
                List<string> translationChunks = TranslationClient.SplitForTests(longTranslationInput);
                StringBuilder restoredTranslationInput = new StringBuilder();
                bool translationChunksAreSafe = translationChunks.Count == 3;
                foreach (string chunk in translationChunks)
                {
                    restoredTranslationInput.Append(chunk);
                    if (chunk.Length > 450) translationChunksAreSafe = false;
                }
                Assert(translationChunksAreSafe && restoredTranslationInput.ToString() == longTranslationInput, "long translation is split below provider query limit without losing text");
                Assert(CodexProcessMonitor.IsCodexDesktopPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_1.2.3_x64__abc\app\ChatGPT.exe"), "Codex desktop path");
                Assert(!CodexProcessMonitor.IsCodexDesktopPath(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.2.3_x64__abc\app\ChatGPT.exe"), "reject generic ChatGPT path");

                TestAnnouncementTimes();
                TestRecentMessages();

                Console.WriteLine("SELF-TEST PASS");
                Console.WriteLine("tests=" + count);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SELF-TEST FAIL: " + ex.Message);
                Console.Error.WriteLine("tests=" + count);
                return 1;
            }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            count++;
        }

        private static void TestAnnouncementTimes()
        {
            DateTimeOffset now = DateTimeOffset.Parse("2026-08-31T00:40:00Z", CultureInfo.InvariantCulture);
            DateTimeOffset target = DateTimeOffset.Parse("2026-08-31T02:00:00Z", CultureInfo.InvariantCulture);
            string announcement = "Your Codex and ChatGPT Work reset will land at 6pm PST.";
            string published = "2026-08-30T19:24:37Z";
            string latest = AnnouncementEvent("2094144275957350900", announcement, published, null);
            RadarSnapshot result = RadarClient.ParseFeedForTests("{\"events\":[" + latest + "]}", null, now);
            Assert(result.Mode == RadarMode.OfficialWindow && result.TargetUtc == target, "real 6pm PST feed announcement without official_window");
            Assert(result.Evidence.Contains("PST") && result.Evidence.Contains("UTC-08:00") && result.Evidence.Contains("PDT"), "literal PST and summer-time ambiguity are explained");
            Assert(result.Evidence.Contains(target.LocalDateTime.ToString("MM-dd HH:mm")), "announcement shows the converted local time");

            string fullLivePost = "Thanks for reading. We will do a global reset of the usage for all paid subscriptions so that you can keep enjoying Astra. Lands around 6pm PST today.";
            RadarSnapshot fullLiveResult = ParsedAnnouncement(fullLivePost, "2026-09-07T19:24:57Z", DateTimeOffset.Parse("2026-09-08T01:30:00Z", CultureInfo.InvariantCulture));
            Assert(fullLiveResult.Mode == RadarMode.OfficialWindow && fullLiveResult.TargetUtc == DateTimeOffset.Parse("2026-09-08T02:00:00Z", CultureInfo.InvariantCulture), "full live post with around 6pm PST today is recognized as an official window");

            string dayclaw = new JavaScriptSerializer().Serialize(new { items = new[] { new { external_id = "2094144275957350900", content = announcement, published_at = "2026-08-30T19:24:37", url = "https://x.com/thsottiaux/status/2094144275957350900" } } });
            RadarSnapshot backup = RadarClient.ParseDayclawForTests(dayclaw, null, now);
            Assert(backup.Mode == RadarMode.OfficialWindow && backup.TargetUtc == target, "same timed post through Dayclaw with a zone-less publication timestamp");
            RadarSnapshot cached = RadarClient.FromCache(new JavaScriptSerializer().Serialize(new { feed = "{\"events\":[" + latest + "]}", forecast = (string)null }), now);
            Assert(cached != null && cached.TargetUtc == target, "cached feed also recovers the announced time");

            RadarSnapshot elapsed = RadarClient.ParseFeedForTests("{\"events\":[" + latest + "]}", null, target.AddMinutes(1));
            Assert(elapsed.Mode == RadarMode.WindowElapsed && elapsed.GetCountdownText(target.AddMinutes(1)).Contains("等待确认"), "parsed deadline becomes waiting, never confirmed");
            Assert(RadarClient.ParseDayclawForTests(dayclaw, null, target.AddMinutes(1)).Mode == RadarMode.WindowElapsed, "backup preserves an elapsed announced deadline");
            Assert(ParsedAnnouncement(announcement, published, now.AddDays(4)).Mode == RadarMode.Quiet, "stale timed post is not recycled into a new countdown");
            Assert(ParsedAnnouncement(announcement.Replace("PST", "PDT"), published, now).TargetUtc == target.AddHours(-1), "PDT means UTC-07:00");
            Assert(ParsedAnnouncement(announcement.Replace("PST", "PT"), published, now).TargetUtc == target.AddHours(-1), "PT observes summer daylight saving time");
            Assert(ParsedAnnouncement("Codex reset will land at 18:30 UTC.", "2026-08-30T16:00:00Z", now.AddHours(-8)).TargetUtc == DateTimeOffset.Parse("2026-08-30T18:30:00Z"), "24-hour UTC with minutes");
            Assert(ParsedAnnouncement("Codex reset will land at 12am PST.", "2026-08-30T19:24:37Z", now).TargetUtc == DateTimeOffset.Parse("2026-08-31T08:00:00Z"), "12am and next-day rollover");
            Assert(ParsedAnnouncement("Codex reset will land tomorrow at 6pm PST.", "2026-08-30T02:00:00Z", now).TargetUtc == target, "tomorrow uses the source timezone date, not the UTC date");
            Assert(ParsedAnnouncement("Codex reset will land at 6pm PT.", "2026-12-01T19:00:00Z", DateTimeOffset.Parse("2026-12-02T00:00:00Z")).TargetUtc == DateTimeOffset.Parse("2026-12-02T02:00:00Z"), "PT observes winter standard time");
            Assert(ParsedAnnouncement("Codex reset will not land at 6pm PST.", published, now).Mode == RadarMode.Quiet, "negated reset timing is not an announcement");
            Assert(ParsedAnnouncement("Codex reset will land at 6pm.", published, now).Mode == RadarMode.Quiet, "missing timezone is not guessed");
            Assert(ParsedAnnouncement("Codex reset will land at 25:00 PST.", published, now).Mode == RadarMode.Quiet, "invalid clock time is rejected");
            Assert(ParsedAnnouncement("Codex reset will land at 6pm PST on September 2.", published, now).Mode == RadarMode.Quiet, "unsupported explicit calendar date is not silently treated as today");

            string structured = AnnouncementEvent("structured", announcement, published, "2026-08-31T03:00:00Z");
            Assert(RadarClient.ParseFeedForTests("{\"events\":[" + structured + "]}", null, now).TargetUtc == target.AddHours(1), "structured official window keeps priority over text inference");
            string older = AnnouncementEvent("older", "Codex reset will land at 5pm PST.", "2026-08-30T18:00:00Z", null);
            Assert(RadarClient.ParseFeedForTests("{\"events\":[" + older + "," + latest + "]}", null, now).TargetUtc == target, "latest dated announcement wins even when feed order changes");
            string olderStructured = AnnouncementEvent("older-window", "Codex reset within the hour.", "2026-08-30T18:00:00Z", "2026-08-31T01:00:00Z");
            Assert(RadarClient.ParseFeedForTests("{\"events\":[" + olderStructured + "," + latest + "]}", null, now).TargetUtc == target, "old structured window cannot hide newer text announcement");
            string confirmed = latest.Replace("\"pending\"", "\"confirmed\"");
            Assert(RadarClient.ParseFeedForTests("{\"events\":[" + olderStructured + "," + confirmed + "]}", null, now).Mode == RadarMode.Confirmed, "explicit latest confirmation supersedes an old future window");
            Assert(RadarClient.ParseDayclawForTests(dayclaw.Replace(announcement, "We will reset usage limits at 6pm PST."), null, target.AddMinutes(1)).Mode == RadarMode.WindowElapsed, "future-tense backup announcement never becomes confirmation at the deadline");
            Assert(ParsedAnnouncement("Codex reset tomorrow.", published, now).Mode == RadarMode.Quiet, "date-only hint is not promoted to a precise main-feed time");
            Assert(ParsedAnnouncement("Codex reset will land from 6pm PST to 8pm PST.", published, now).Mode == RadarMode.Quiet, "multiple clock times are not silently reduced to the first");
            Assert(ParsedAnnouncement("Codex reset will land at 2:30am PT tomorrow.", "2026-03-07T20:00:00Z", DateTimeOffset.Parse("2026-03-08T00:00:00Z")).Mode == RadarMode.Quiet, "nonexistent spring DST clock time is rejected");
            Assert(ParsedAnnouncement("Codex reset will land at 1:30am PT tomorrow.", "2026-10-31T20:00:00Z", DateTimeOffset.Parse("2026-11-01T00:00:00Z")).Mode == RadarMode.Quiet, "ambiguous autumn DST clock time is rejected");
            Assert(ParsedAnnouncement("Codex reset will land Monday at 6pm PST.", published, now).TargetUtc == DateTimeOffset.Parse("2026-09-01T02:00:00Z"), "weekday without on is not ignored");
            Assert(ParsedAnnouncement("Codex reset will land at 6pm or 8pm PST.", published, now).Mode == RadarMode.Quiet, "multiple candidate times with one timezone are ambiguous");
            Assert(ParsedAnnouncement("Codex reset will land at 6pm UTC+8.", published, now).Mode == RadarMode.Quiet, "unsupported numeric timezone offset is not truncated to UTC");
            string completedBackup = dayclaw.Replace(announcement, "Codex usage limits have been reset at 6pm PST.").Replace("2026-08-30T19:24:37", "2026-08-31T02:05:00");
            Assert(RadarClient.ParseDayclawForTests(completedBackup, null, target.AddMinutes(10)).Mode == RadarMode.Confirmed, "explicit completed backup announcement retains its confirmation");
        }

        private static void TestRecentMessages()
        {
            DateTimeOffset published = DateTimeOffset.Parse("2026-09-03T23:00:00Z", CultureInfo.InvariantCulture);
            DateTimeOffset now = published.AddHours(1);
            long milliseconds = (long)(published - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMilliseconds;
            string id = "2095651088502591861";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Tweet:" + id)).Substring("VHdlZXQ6".Length);
            string profile = "VHdlZXQ6" + encoded + ":details,full_text:\"We will give one banked reset for every day you do not have access to Astra.\\n\\nFirst one will land in ~ 3 hours.\",created_at_ms:" + milliseconds.ToString(CultureInfo.InvariantCulture);
            List<RecentPost> parsed = RadarClient.ParseXProfileForTests(profile);
            Assert(parsed.Count == 1 && parsed[0].Id == id && parsed[0].IsResetCard, "X profile banked reset is recorded as a reset card");
            Assert(parsed[0].Text.Contains(Environment.NewLine), "X profile escape sequences are decoded");
            RadarSnapshot card = RadarClient.ParseRecentTimelineForTests(parsed, now);
            Assert(card != null && card.Mode == RadarMode.OfficialWindow && card.TargetUtc == published.AddHours(3), "banked reset with about-hours countdown is detected");

            DateTimeOffset longPostPublished = DateTimeOffset.Parse("2026-12-01T20:00:00Z", CultureInfo.InvariantCulture);
            DateTimeOffset longPostNow = longPostPublished.AddHours(1);
            long longPostMilliseconds = (long)(longPostPublished - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMilliseconds;
            string longPostId = "2097043464538264003";
            string longPostEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Tweet:" + longPostId)).Substring("VHdlZXQ6".Length);
            string preview = "We will do a global reset of paid usage. " + new string('x', 320);
            string fullNote = preview + "\\n\\nLands around 6pm PST today.";
            string longProfile = "VHdlZXQ6" + longPostEncoded + ":details,full_text:\"" + preview + "\",created_at_ms:" + longPostMilliseconds.ToString(CultureInfo.InvariantCulture)
                + "},\"client:VHdlZXQ6" + longPostEncoded + ":note_tweet\":$R[1]={__typename:\"NoteTweetData\",note_tweet_results:$R[2]={__ref:\"NoteResultLong\"}}"
                + ",NoteResultLong:$R[3]={__typename:\"NoteTweetResults\",result:$R[4]={__ref:\"NoteLong=\"}}"
                + ",\"NoteLong=\":$R[5]={__typename:\"NoteTweet\",text:\"" + fullNote + "\",entity_set:$R[6]={}}";
            List<RecentPost> parsedLongPost = RadarClient.ParseXProfileForTests(longProfile);
            RadarSnapshot longPostCard = RadarClient.ParseRecentTimelineForTests(parsedLongPost, longPostNow);
            Assert(parsedLongPost.Count == 1 && parsedLongPost[0].Text.Length > preview.Length && parsedLongPost[0].Text.Contains("6pm PST"), "X NoteTweet stores the complete long-post body");
            Assert(longPostCard != null && longPostCard.TargetUtc == DateTimeOffset.Parse("2026-12-02T02:00:00Z", CultureInfo.InvariantCulture), "time near the end of an X long post is detected");

            string cache = RadarClient.BuildCacheForTests(null, null, null, profile);
            RadarSnapshot cached = RadarClient.FromCache(cache, now);
            Assert(cached != null && cached.RecentPosts.Count == 1 && cached.TargetUtc == published.AddHours(3), "recorded X messages survive the offline cache");

            string freshFeed = "{\"events\":[{\"id\":\"feed-fresh\",\"type\":\"boost\",\"summary\":\"A newer public post from the main feed.\",\"url\":\"https://x.test/feed-fresh\",\"announced_at\":\"2026-09-04T00:00:00Z\"}],\"tweets\":[{\"id\":\"timeline-newest\",\"text\":\"The actual newest public timeline post.\",\"url\":\"https://x.test/timeline-newest\",\"at\":\"2026-09-04T01:00:00Z\"}]}";
            List<RecentPost> parsedFeed = RadarClient.ParseFeedPostsForTests(freshFeed);
            Assert(parsedFeed.Count == 2 && parsedFeed[1].Id == "timeline-newest", "main feed timeline posts are available to recent messages");
            RadarSnapshot cachedFeed = RadarClient.FromCache(RadarClient.BuildCacheForTests(freshFeed, null, null, null), now);
            Assert(cachedFeed != null && cachedFeed.RecentPosts.Count == 2 && cachedFeed.RecentPosts[0].Id == "timeline-newest", "cached main feed timeline remains visible in recent messages");
            string savedFeed = RadarClient.BuildCacheForTests(freshFeed, null, null, null);
            Assert(RadarClient.FeedFromCache(savedFeed) == freshFeed, "temporary main-feed failure retains its last successful response");
            Assert(RadarClient.FeedFromCache("broken cache") == null, "unreadable cache cannot invent a feed");

            DateTimeOffset octoberNow = DateTimeOffset.Parse("2026-10-22T08:00:00Z", CultureInfo.InvariantCulture);
            string replyFeed = new JavaScriptSerializer().Serialize(new { tweets = new[] {
                new { id = "2113162277688070445", text = "Day 17/ Bet you thought we forgot, but no, it’s reset day today.", at = "2026-10-22T06:55:22Z", url = "https://x.test/parent", in_reply_to_tweet_id = (string)null },
                new { id = "2113162413008884099", text = "Lands tomorrow before 10am PST.", at = "2026-10-22T06:55:54Z", url = "https://x.test/reply", in_reply_to_tweet_id = "2113162277688070445" }
            }});
            List<RecentPost> replyPosts = RadarClient.MergeAllRecentPostsForTests(RadarClient.ParseFeedPostsForTests(replyFeed), null);
            RadarSnapshot replySignal = RadarClient.ParseRecentTimelineForTests(replyPosts, octoberNow);
            Assert(replyPosts[0].KindLabel == "重置预告" && replySignal != null && replySignal.Mode == RadarMode.OfficialWindow, "reply inherits reset context from the linked parent post");
            Assert(replySignal.TargetUtc == DateTimeOffset.Parse("2026-10-22T18:00:00Z", CultureInfo.InvariantCulture) && replySignal.IsDeadline && replySignal.Evidence.Contains("若作者实际指 PDT"), "before 10am PST is a deadline anchored to the author's Pacific date");
            string archiveAndTweet = new JavaScriptSerializer().Serialize(new {
                events = new[] { new { id = "day13", type = "reset", summary = "Archive summary: reset landed at 20:41 UTC on our checks.", announced_at = "2026-10-17T20:41:01Z", source = "archive", reset_kind = "hard", url = "https://x.test/day13" } },
                tweets = new[] { new { id = "day13", text = "Landing sometime today by EOD PST", at = "2026-10-17T16:21:13Z", url = "https://x.test/day13", in_reply_to_tweet_id = "parent13" },
                    new { id = "parent13", text = "Day 13/ Le limit reset", at = "2026-10-17T16:20:57Z", url = "https://x.test/parent13", in_reply_to_tweet_id = (string)null } }
            });
            List<RecentPost> day13Posts = RadarClient.MergeAllRecentPostsForTests(RadarClient.ParseFeedPostsForTests(archiveAndTweet), null);
            RadarSnapshot day13Signal = RadarClient.ParseRecentTimelineForTests(day13Posts, DateTimeOffset.Parse("2026-10-17T17:00:00Z", CultureInfo.InvariantCulture));
            Assert(day13Posts[0].Text.StartsWith("Landing sometime") && day13Posts[0].PublishedUtc == DateTimeOffset.Parse("2026-10-17T16:21:13Z", CultureInfo.InvariantCulture), "original post and publication time outrank later archive summary");
            Assert(day13Signal != null && day13Signal.IsDeadline && day13Signal.TargetUtc == DateTimeOffset.Parse("2026-10-18T07:59:00Z", CultureInfo.InvariantCulture), "EOD PST reply is a deadline, not an exact landing time");
            RadarSnapshot completedAfterDeadlinePreview = RadarClient.FromCache(RadarClient.BuildCacheForTests(archiveAndTweet, null, null, null), DateTimeOffset.Parse("2026-10-17T21:00:00Z", CultureInfo.InvariantCulture));
            Assert(completedAfterDeadlinePreview != null && completedAfterDeadlinePreview.Mode == RadarMode.Confirmed, "later verified completion supersedes an earlier unexpired deadline");
            List<RecentPost> orphanReply = RadarClient.MergeAllRecentPostsForTests(new List<RecentPost> { new RecentPost { Id = "orphan", Text = "Lands tomorrow before 10am PST.", ParentId = "absent", Url = "https://x.test/orphan", PublishedUtc = octoberNow.AddMinutes(-1) } }, null);
            Assert(RadarClient.ParseRecentTimelineForTests(orphanReply, octoberNow) == null, "unlinked landing reply is not promoted to a reset");
            RecentPost day15 = new RecentPost { Id = "day15", Text = "Day 15/ Nevermind, we're still cooking... and reset. Enjoy.", Url = "https://x.test/day15", PublishedUtc = DateTimeOffset.Parse("2026-10-20T06:33:48Z", CultureInfo.InvariantCulture) };
            Assert(RadarClient.ParseRecentTimelineForTests(new List<RecentPost> { day15 }, day15.PublishedUtc.AddMinutes(5)).Mode == RadarMode.Confirmed && day15.IsResetCard, "Day 15 completed reset is recognized without Codex keyword");
            RecentPost day8 = new RecentPost { Id = "day8", Text = "What would you like with your side of reset? It's done.", Url = "https://x.test/day8", PublishedUtc = DateTimeOffset.Parse("2026-10-13T05:06:06Z", CultureInfo.InvariantCulture) };
            Assert(RadarClient.ParseRecentTimelineForTests(new List<RecentPost> { day8 }, day8.PublishedUtc.AddMinutes(5)).Mode == RadarMode.Confirmed, "It's done reply is recognized as completed reset");
            RecentPost propagated = new RecentPost { Id = "propagated", Text = "Put the reset in the bag. All propagated.", Url = "https://x.test/propagated", PublishedUtc = DateTimeOffset.Parse("2026-10-11T04:01:09Z", CultureInfo.InvariantCulture) };
            Assert(RadarClient.ParseRecentTimelineForTests(new List<RecentPost> { propagated }, propagated.PublishedUtc.AddMinutes(5)).Mode == RadarMode.Confirmed, "all propagated is a completed reset");
            RecentPost demand = new RecentPost { Id = "demand", Text = "I demand a reset", Url = "https://x.test/demand", PublishedUtc = octoberNow.AddMinutes(-1) };
            Assert(RadarClient.ParseRecentTimelineForTests(new List<RecentPost> { demand }, octoberNow) == null, "a request for a reset is not a reset event");
            string archivedCompletion = new JavaScriptSerializer().Serialize(new { events = new[] { new { id = "day13", type = "reset", summary = "The global reset promised by EOD PST landed at 20:41 UTC on our checks; Tibo then posted Done.", announced_at = "2026-10-17T20:41:01Z", source = "archive", reset_kind = "hard", url = "https://x.test/day13" } } });
            RadarSnapshot archivedResult = RadarClient.ParseFeedForTests(archivedCompletion, null, DateTimeOffset.Parse("2026-10-17T21:00:00Z", CultureInfo.InvariantCulture));
            Assert(archivedResult.Mode == RadarMode.Confirmed && archivedResult.Evidence.Contains("公开确认/监测时间"), "verified archive completion shows observed time rather than an invented exact reset time");

            List<RecentPost> first = new List<RecentPost>();
            List<RecentPost> second = new List<RecentPost>();
            for (int i = 0; i < 11; i++)
            {
                first.Add(new RecentPost { Id = "post-" + i.ToString(CultureInfo.InvariantCulture), Text = "message " + i.ToString(CultureInfo.InvariantCulture), Url = "https://x.test/" + i.ToString(CultureInfo.InvariantCulture), PublishedUtc = now.AddMinutes(-i) });
            }
            second.Add(new RecentPost { Id = "post-0", Text = "message 0 with a fuller duplicate body", Url = "https://x.test/0", PublishedUtc = now });
            List<RecentPost> merged = RadarClient.MergeRecentPostsForTests(first, second);
            Assert(merged.Count == 10 && merged[0].Id == "post-0" && merged[0].Text.Contains("fuller") && merged[9].Id == "post-9", "recent records are deduplicated, sorted, and capped at ten");
            first[10].Text = "We will give one banked reset. It will land in 3 hours.";
            List<RecentPost> allRecent = RadarClient.MergeAllRecentPostsForTests(first, null);
            RadarSnapshot eleventhCard = RadarClient.ParseRecentTimelineForTests(allRecent, now);
            Assert(allRecent.Count == 11 && allRecent[10].Id == "post-10", "all recent posts remain available to the detector");
            Assert(eleventhCard != null && eleventhCard.TargetUtc == first[10].PublishedUtc.AddHours(3), "a reset card outside the displayed ten is still detected");
        }

        private static RadarSnapshot ParsedAnnouncement(string summary, string published, DateTimeOffset now)
        {
            return RadarClient.ParseFeedForTests("{\"events\":[" + AnnouncementEvent("test", summary, published, null) + "]}", null, now);
        }

        private static string AnnouncementEvent(string id, string summary, string published, string officialEnd)
        {
            return new JavaScriptSerializer().Serialize(new
            {
                id = id, type = "reset", summary = summary, announced_at = published,
                official_window = officialEnd == null ? null : new { end_at = officialEnd },
                announcement_state = "none", reset_verification_status = "pending",
                url = "https://x.test/" + id
            });
        }
    }
}
