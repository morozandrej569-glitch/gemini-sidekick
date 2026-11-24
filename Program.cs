using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace GeminiSidekick;

public sealed class AppLogger
{
    public string LogDirectory { get; }
    public string CurrentLogFile => Path.Combine(LogDirectory, $"sidekick-{DateTime.Now:yyyy-MM-dd}.log");

    public AppLogger()
    {
        LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeminiSidekick", "logs");
        Directory.CreateDirectory(LogDirectory);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            lock (this) { File.AppendAllText(CurrentLogFile, line); }
        }
        catch { }
    }
}

public sealed class AppRule
{
    public string ExecutableOrCommand { get; set; } = string.Empty;
    public bool AllowAdmin { get; set; }
}

public sealed class AppLauncher
{
    private readonly Dictionary<string, AppRule> _apps;
    private readonly AppLogger _logger;

    public AppLauncher(Dictionary<string, AppRule> apps, AppLogger logger)
    {
        _apps = new(apps, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public string Launch(string alias, bool requestAdmin = false)
    {
        if (!_apps.TryGetValue(alias, out var rule))
        {
            _logger.Warn($"Unknown app alias '{alias}'.");
            return $"Не знаю программу '{alias}'.";
        }
        if (requestAdmin && !rule.AllowAdmin)
        {
            _logger.Warn($"Admin launch blocked for '{alias}'.");
            return $"Запуск '{alias}' от имени администратора запрещён.";
        }
        try
        {
            _logger.Info($"Launching '{alias}' (cmd='{rule.ExecutableOrCommand}', admin={requestAdmin}).");
            Process.Start(new ProcessStartInfo
            {
                FileName = rule.ExecutableOrCommand,
                UseShellExecute = true,
                Verb = requestAdmin ? "runas" : string.Empty
            });
            return requestAdmin ? $"Запрашиваю запуск '{alias}' от имени администратора..." : $"Открываю '{alias}'.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logger.Warn($"UAC cancelled for '{alias}'.");
            return "Запрос UAC отменён.";
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to launch '{alias}'.", ex);
            return $"Ошибка запуска '{alias}': {ex.Message}";
        }
    }
}

internal sealed class SpeechBubbleForm : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _fallbackTimer;

    public SpeechBubbleForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(24, 26, 32);
        ForeColor = Color.White;
        Size = new Size(320, 90);
        StartPosition = FormStartPosition.Manual;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular),
            ForeColor = Color.White,
            Padding = new Padding(12, 10, 12, 10),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(_label);

        _fallbackTimer = new System.Windows.Forms.Timer { Interval = 6000 };
        _fallbackTimer.Tick += (_, _) => HideBubble();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, 14);
        using var fill = new SolidBrush(BackColor);
        using var pen = new Pen(Color.FromArgb(90, 145, 255), 1.5f);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }

    public void ShowAboveOrb(Point orbLocation, Size orbSize, string text, bool autoHideAfterFallback)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowAboveOrb(orbLocation, orbSize, text, autoHideAfterFallback));
            return;
        }

        _label.Text = text;
        var wa = Screen.FromPoint(orbLocation).WorkingArea;
        var maxBubbleW = Math.Clamp(wa.Width - 32, 160, 340);
        var measured = TextRenderer.MeasureText(text, _label.Font, new Size(maxBubbleW - 28, 260), TextFormatFlags.WordBreak);
        var w = Math.Clamp(measured.Width + 28, 150, maxBubbleW);
        var h = Math.Clamp(measured.Height + 24, 48, 280);
        Size = new Size(w, h);

        var x = Math.Clamp(orbLocation.X + orbSize.Width - w, wa.Left + 12, wa.Right - w - 12);
        var y = orbLocation.Y - h - 10;
        if (y < wa.Top + 12) y = Math.Clamp(orbLocation.Y + orbSize.Height + 10, wa.Top + 12, wa.Bottom - h - 12);

        Location = new Point(x, y);
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), 14);
        Region = new Region(path);

        if (!Visible) Show();
        TopMost = true;
        Invalidate();

        _fallbackTimer.Stop();
        if (autoHideAfterFallback) _fallbackTimer.Start();
    }

    public void HideBubble()
    {
        if (InvokeRequired)
        {
            BeginInvoke(HideBubble);
            return;
        }
        _fallbackTimer.Stop();
        Hide();
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public sealed class OrbForm : Form
{
    private readonly AppLauncher _launcher;
    private readonly AppLogger _logger;
    private readonly SpeechBubbleForm _bubble = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _slideTimer;

    private object? _synth;
    private readonly WavMicRecorder _micRecorder = new();
    private Vosk.Model? _voskModel;
    private bool _voskInitAttempted;
    private bool _voskMissingLogged;

    private string? ResolveVoskModelPath()
    {
        var envPath = Environment.GetEnvironmentVariable("VOSK_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
            return envPath;

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "models", "vosk-model-small-ru-0.22"),
            Path.Combine(Environment.CurrentDirectory, "models", "vosk-model-small-ru-0.22"),
            Path.Combine(Environment.CurrentDirectory, "src", "GeminiSidekick", "models", "vosk-model-small-ru-0.22"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "models", "vosk-model-small-ru-0.22")
        ];

        foreach (var cand in candidates)
        {
            var full = Path.GetFullPath(cand);
            if (Directory.Exists(full))
                return full;
        }

        string[] modelDirs =
        [
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(Environment.CurrentDirectory, "models"),
            Path.Combine(Environment.CurrentDirectory, "src", "GeminiSidekick", "models")
        ];

        foreach (var dir in modelDirs)
        {
            var fullDir = Path.GetFullPath(dir);
            if (!Directory.Exists(fullDir)) continue;
            foreach (var sub in Directory.GetDirectories(fullDir))
            {
                if (File.Exists(Path.Combine(sub, "am", "final.mdl")) || Directory.Exists(Path.Combine(sub, "conf")))
                    return sub;
            }
        }

        return null;
    }

    private Vosk.Model? EnsureVoskModel()
    {
        if (_voskModel != null)
            return _voskModel;

        if (_voskInitAttempted)
            return null;

        _voskInitAttempted = true;
        try
        {
            Vosk.Vosk.SetLogLevel(-1);
            var modelPath = ResolveVoskModelPath();
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                if (!_voskMissingLogged)
                {
                    _voskMissingLogged = true;
                    _logger.Warn("Vosk model not found (put vosk-model-small-ru-0.22 in models/ or set VOSK_MODEL_PATH). Wake word 'Окей, Google' is waiting for model.");
                }
                return null;
            }

            _voskModel = new Vosk.Model(modelPath);
            _logger.Info($"Vosk wake model loaded from '{modelPath}'.");
            return _voskModel;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Vosk model init failed: {ex.Message}");
            return null;
        }
    }

    private string? RecognizeWakePhraseLocally(string wavPath)
    {
        var model = EnsureVoskModel();
        if (model == null)
            return null;

        var bytes = File.ReadAllBytes(wavPath);
        if (bytes.Length <= 44)
            return null;

        const string grammar = "[\"окей гугл\", \"окей google\", \"ок гугл\", \"ok google\", \"хей гугл\", \"hey google\", \"[unk]\"]";
        using var rec = new Vosk.VoskRecognizer(model, 16000.0f, grammar);
        rec.SetMaxAlternatives(0);
        rec.SetWords(false);

        int offset = 44;
        int chunkSize = 4096;
        while (offset < bytes.Length)
        {
            int len = Math.Min(chunkSize, bytes.Length - offset);
            rec.AcceptWaveform(bytes, len);
            offset += len;
        }

        var json = rec.FinalResult();
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("text", out var textProp))
            {
                var raw = textProp.GetString() ?? string.Empty;
                var norm = NormalizeWakeText(raw);
                string[] wakePhrases =
                [
                    "окей гугл",
                    "окей google",
                    "ок гугл",
                    "ok google",
                    "хей гугл",
                    "hey google"
                ];
                return wakePhrases.FirstOrDefault(w => norm.Contains(w, StringComparison.Ordinal));
            }
        }
        catch
        {
            // Игнорируем ошибку парсинга JSON при тишине/шуме
        }

        return null;
    }
    private ToolStripMenuItem? _recordMenuItem;
    private NotifyIcon? _trayIcon;
    private bool _isTranscribing;
    private const int HotKeyIdVoice = 0x4756;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint VkV = 0x56;
    private const int WmHotKey = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string command, System.Text.StringBuilder? returnString, int returnLength, IntPtr hwndCallback);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private static void BringProcessWindowToFront(string? processHint)
    {
        try
        {
            var hint = (processHint ?? string.Empty).ToLowerInvariant();
            var procs = Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) return false;
                        if (string.IsNullOrWhiteSpace(hint)) return true;
                        var name = p.ProcessName.ToLowerInvariant();
                        var title = p.MainWindowTitle.ToLowerInvariant();
                        return name.Contains(hint) || title.Contains(hint) ||
                               (hint.Contains("yandex") && name.Contains("browser")) ||
                               (hint.Contains("браузер") && (name.Contains("browser") || name.Contains("chrome") || name.Contains("msedge")));
                    }
                    catch { return false; }
                })
                .ToList();

            foreach (var p in procs)
            {
                ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
            }
        }
        catch { }
    }
    private bool _wakeActive;
    private bool _speaking;
    private bool _wakeListenEnabled = true;
    private bool _isWakeChecking;
    private readonly System.Windows.Forms.Timer _wakePollTimer = new();
    private ToolStripMenuItem? _wakeToggleMenuItem;
    private const string WakeAlias = "GeminiSidekickWakeMic";

    private bool _wantsVisible;
    private int _targetX;
    private int _targetY;

    private const int OrbSize = 64;

    public OrbForm(AppLauncher launcher, AppLogger logger)
    {
        _launcher = launcher;
        _logger = logger;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(OrbSize, OrbSize);
        StartPosition = FormStartPosition.Manual;

        using var circle = new GraphicsPath();
        circle.AddEllipse(0, 0, Width, Height);
        Region = new Region(circle);

        BuildMenu();

        _slideTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _slideTimer.Tick += OnSlideTick;

        _wakePollTimer.Interval = 3600;
        _wakePollTimer.Tick += async (_, _) => await PollWakeWordAsync();
        _wakePollTimer.Start();

        ResetOffscreenPosition();
        Hide();

        _logger.Info($"GeminiSidekick orb started. Log file: {_logger.CurrentLogFile}");
        InitSpeechSafely();
        InitTrayAndHotKey();
    }

    protected override bool ShowWithoutActivation => true;

    private void ResetOffscreenPosition()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        _targetX = wa.Right;
        _targetY = wa.Bottom - Height - 24;
        Location = new Point(_targetX, _targetY);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HwndTopMost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private void EnsureTopMostNoActivate()
    {
        if (!IsHandleCreated) return;
        try
        {
            SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch { }
    }

    private void SetOrbActiveState(bool listeningOrSpeaking)
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        _wantsVisible = listeningOrSpeaking;
        _targetY = wa.Bottom - Height - 24;

        if (listeningOrSpeaking)
        {
            _targetX = wa.Right - Width - 24;
            if (!Visible)
            {
                Location = new Point(wa.Right, _targetY);
                Show();
                EnsureTopMostNoActivate();
            }
            Size = new Size(OrbSize, OrbSize);
            using var circle = new GraphicsPath();
            circle.AddEllipse(0, 0, Width, Height);
            Region = new Region(circle);
        }
        else
        {
            _targetX = wa.Right + 4;
            if (!Visible)
            {
        _slideTimer.Stop();
                return;
            }
        }
        _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        var dx = _targetX - Location.X;
        var dy = _targetY - Location.Y;
        if (Math.Abs(dx) <= 4 && Math.Abs(dy) <= 4)
        {
            Location = new Point(_targetX, _targetY);
        _slideTimer.Stop();
            if (!_wantsVisible && Visible)
            {
                Hide();
            }
            return;
        }

        var stepX = Math.Sign(dx) * Math.Max(4, Math.Abs(dx) / 4);
        var stepY = Math.Sign(dy) * Math.Max(2, Math.Abs(dy) / 4);
        Location = new Point(Location.X + stepX, Location.Y + stepY);
    }

    private void BuildMenu()
    {
        var testCmdItem = new ToolStripMenuItem("Тест: открыть блокнот");
        testCmdItem.Click += (_, _) =>
        {
            _wakeActive = true;
            SetOrbActiveState(true);
            Invalidate();
            _ = ExecuteCommandAsync("открой блокнот", fromVoice: false);
        };

        var settingsItem = new ToolStripMenuItem("Настройки...");
        settingsItem.Click += (_, _) =>
        {
            var current = SidekickSettingsStore.Load();
            using var dlg = new SettingsDialog(current);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                current.ExtraFolders = dlg.SelectedFolders;
                current.CustomAppsFolder = dlg.SelectedFolders.FirstOrDefault() ?? string.Empty;
                current.SilenceMsToSend = dlg.SilenceMs;
                SidekickSettingsStore.Save(current);
                _logger.Info($"Settings saved. Folders count={current.GetAllCustomFolders().Count}, silenceMs={current.SilenceMsToSend}");
                SpeakWithBubble("Настройки сохранены.");
            }
        };

        var logsItem = new ToolStripMenuItem("Открыть папку логов");
        logsItem.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = _logger.LogDirectory, UseShellExecute = true }); }
            catch (Exception ex) { _logger.Error("Failed to open logs folder.", ex); }
        };

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => Close();

        _recordMenuItem = new ToolStripMenuItem("Голос: начать запись");
        _recordMenuItem.Click += async (_, _) => await ToggleVoiceRecordingAsync();

        _menu.Items.Add(_recordMenuItem);
        _menu.Items.Add(testCmdItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(logsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
        ContextMenuStrip = _menu;
    }

    private static GraphicsPath CreateGeminiSparklePath(RectangleF bounds)
    {
        var cx = bounds.X + bounds.Width / 2f;
        var cy = bounds.Y + bounds.Height / 2f;
        var rx = bounds.Width / 2f;
        var ry = bounds.Height / 2f;
        var pinchX = rx * 0.22f;
        var pinchY = ry * 0.22f;

        var top = new PointF(cx, bounds.Top);
        var right = new PointF(bounds.Right, cy);
        var bottom = new PointF(cx, bounds.Bottom);
        var left = new PointF(bounds.Left, cy);

        var path = new GraphicsPath();
        path.AddBezier(top, new PointF(cx + pinchX, cy - pinchY), new PointF(cx + pinchX, cy - pinchY), right);
        path.AddBezier(right, new PointF(cx + pinchX, cy + pinchY), new PointF(cx + pinchX, cy + pinchY), bottom);
        path.AddBezier(bottom, new PointF(cx - pinchX, cy + pinchY), new PointF(cx - pinchX, cy + pinchY), left);
        path.AddBezier(left, new PointF(cx - pinchX, cy - pinchY), new PointF(cx - pinchX, cy - pinchY), top);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var bg1 = _speaking ? Color.FromArgb(24, 34, 58) : Color.FromArgb(18, 22, 36);
        var bg2 = _speaking ? Color.FromArgb(44, 66, 112) : Color.FromArgb(30, 38, 62);
        using var bgBrush = new LinearGradientBrush(ClientRectangle, bg1, bg2, LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillEllipse(bgBrush, 1, 1, Width - 2, Height - 2);

        var ringColor = _speaking ? Color.FromArgb(110, 225, 255)
                      : _wakeActive ? Color.FromArgb(130, 175, 255)
                      : Color.FromArgb(190, 210, 255);
        using var ringPen = new Pen(ringColor, _speaking ? 2.6f : 2f);
        e.Graphics.DrawEllipse(ringPen, 2, 2, Width - 4, Height - 4);

        var starRect = new RectangleF(13f, 13f, Width - 26f, Height - 26f);
        using var starPath = CreateGeminiSparklePath(starRect);

        var s1 = _speaking ? Color.FromArgb(72, 220, 255) : Color.FromArgb(110, 165, 255);
        var s2 = _speaking ? Color.FromArgb(138, 120, 255) : Color.FromArgb(165, 110, 255);
        using var starBrush = new LinearGradientBrush(starRect, s1, s2, LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillPath(starBrush, starPath);
    }

    private Assembly? LoadSpeechAssembly()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "System.Speech.dll");
        if (!File.Exists(dllPath))
        {
            _logger.Warn($"System.Speech.dll not found at '{dllPath}'.");
            return null;
        }
        return Assembly.LoadFrom(dllPath);
    }

    private void InitSpeechSafely()
    {
        Assembly? speechAsm = null;
        try { speechAsm = LoadSpeechAssembly(); }
        catch (Exception ex) { _logger.Error("Failed to load System.Speech.dll.", ex); }

        if (speechAsm == null) return;

        try
        {
            var synthType = speechAsm.GetType("System.Speech.Synthesis.SpeechSynthesizer");
            if (synthType != null)
            {
                var synth = Activator.CreateInstance(synthType);
                if (synth != null)
                {
                    synthType.GetProperty("Rate")?.SetValue(synth, 1);
                    synthType.GetProperty("Volume")?.SetValue(synth, 100);
                    synthType.GetMethod("SetOutputToDefaultAudioDevice", Type.EmptyTypes)?.Invoke(synth, null);

                    var completedEv = synthType.GetEvent("SpeakCompleted");
                    if (completedEv != null)
                    {
                        var h = Delegate.CreateDelegate(completedEv.EventHandlerType!, this, nameof(OnSpeakCompleted));
                        completedEv.AddEventHandler(synth, h);
                    }

                    _synth = synth;
                    _logger.Info("SpeechSynthesizer initialized.");
                }
            }
        }
        catch (Exception ex)
        {
            _synth = null;
            _logger.Error("SpeechSynthesizer initialization failed.", ex);
        }

        _logger.Info("Cloud voice mode active (SpeechRecognizer disabled).");
    }

    private async Task ToggleVoiceRecordingAsync()
    {
        if (_isTranscribing) return;

        try
        {
            if (!_micRecorder.IsRecording)
            {
                _micRecorder.Start();
                _wakeActive = true;
                SetOrbActiveState(true);
                Invalidate();
                if (_recordMenuItem != null) _recordMenuItem.Text = "Голос: остановить и распознать";
                _logger.Info("Cloud voice recording started.");
                return;
            }

            if (_recordMenuItem != null)
            {
                _recordMenuItem.Text = "Голос: распознаю...";
                _recordMenuItem.Enabled = false;
            }
            _isTranscribing = true;

            var wavPath = _micRecorder.StopAndSaveWav();
            _logger.Info($"Voice WAV saved: '{wavPath}'. Sending to Gemini API...");

            string heard;
            try
            {
                heard = await GeminiAudioTranscriber.TranscribeRussianWavAsync(wavPath);
            }
            finally
            {
                try { File.Delete(wavPath); } catch { }
            }

            _logger.Info($"Cloud speech recognized: '{heard}'");
            if (!string.IsNullOrWhiteSpace(heard))
            {
                if (InvokeRequired) Invoke(() => HandleHeardText(heard, directFromHotkeyOrTray: true));
                else HandleHeardText(heard, directFromHotkeyOrTray: true);
            }
            else
            {
                SpeakWithBubble("Не расслышал.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Cloud voice recognition failed.", ex);
            SpeakWithBubble($"Ошибка голоса: {ex.Message}");
        }
        finally
        {
            _isTranscribing = false;
            if (!_wakeActive && !_speaking)
            {
                SetOrbActiveState(false);
            }
            else
            {
                SetOrbActiveState(true);
            }
            Invalidate();
            if (_recordMenuItem != null)
            {
                _recordMenuItem.Text = "Голос: начать запись";
                _recordMenuItem.Enabled = true;
            }
        }
    }

    public void OnSpeakCompleted(object? sender, object? e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnSpeakCompleted(sender, e));
            return;
        }
        _speaking = false;
        _wakeActive = false;
        _speaking = false;
        Invalidate();
        _bubble.HideBubble();
        if (!_micRecorder.IsRecording && !_isTranscribing && !_wakeActive)
        {
            SetOrbActiveState(false);
        }
    }

    private static string NormalizeWakeText(string input)
    {
        var chars = input.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ')
            .ToArray();
        return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private DateTime _lastSpeechTriggerUtc = DateTime.MinValue;

    private static (double Rms, double Peak, double SpeechRatio) AnalyzeWavSpeech(string wavPath)
    {
        try
        {
            var data = File.ReadAllBytes(wavPath);
            if (data.Length <= 44) return (0.0, 0.0, 0.0);
            double sumSquares = 0;
            double peak = 0;
            int totalSamples = 0;
            int activeFrames = 0;
            int totalFrames = 0;
            const int frameSamples = 320;
            double frameSumSq = 0;
            int frameCount = 0;

            for (int i = 44; i + 1 < data.Length; i += 2)
            {
                short sample = (short)(data[i] | (data[i + 1] << 8));
                double absNorm = Math.Abs(sample) / 32768.0;
                if (absNorm > peak) peak = absNorm;
                double sNorm = sample / 32768.0;
                sumSquares += sNorm * sNorm;
                totalSamples++;

                frameSumSq += sNorm * sNorm;
                frameCount++;
                if (frameCount == frameSamples)
                {
                    double fRms = Math.Sqrt(frameSumSq / frameCount);
                    totalFrames++;
                    if (fRms > 0.022) activeFrames++;
                    frameSumSq = 0;
                    frameCount = 0;
                }
            }

            double rms = totalSamples == 0 ? 0.0 : Math.Sqrt(sumSquares / totalSamples);
            double speechRatio = totalFrames == 0 ? 0.0 : (double)activeFrames / totalFrames;
            return (rms, peak, speechRatio);
        }
        catch { return (0.0, 0.0, 0.0); }
    }

    private async Task PollWakeWordAsync()
    {
        if (!_wakeListenEnabled || _isWakeChecking || _isTranscribing || _micRecorder.IsRecording)
            return;

        if ((DateTime.UtcNow - _lastSpeechTriggerUtc).TotalSeconds < 12)
            return;

        _isWakeChecking = true;
        string? wavPath = null;
        try
        {
            mciSendStringW($"close {WakeAlias}", null, 0, IntPtr.Zero);
            if (mciSendStringW($"open new type waveaudio alias {WakeAlias}", null, 0, IntPtr.Zero) != 0)
                return;
            mciSendStringW($"set {WakeAlias} time format ms bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2", null, 0, IntPtr.Zero);
            if (mciSendStringW($"record {WakeAlias}", null, 0, IntPtr.Zero) != 0)
            {
                mciSendStringW($"close {WakeAlias}", null, 0, IntPtr.Zero);
                return;
            }

            await Task.Delay(1600);

            if (_isTranscribing || _micRecorder.IsRecording || !_wakeListenEnabled)
            {
                mciSendStringW($"close {WakeAlias}", null, 0, IntPtr.Zero);
                return;
            }

            wavPath = Path.Combine(Path.GetTempPath(), $"geminisidekick_wake_{DateTime.Now:HHmmss_fff}.wav");
            mciSendStringW($"stop {WakeAlias}", null, 0, IntPtr.Zero);
            mciSendStringW($"save {WakeAlias} \"{wavPath}\"", null, 0, IntPtr.Zero);
            mciSendStringW($"close {WakeAlias}", null, 0, IntPtr.Zero);

            if (!File.Exists(wavPath) || new FileInfo(wavPath).Length < 2048)
                return;

            var (rms, peak, speechRatio) = AnalyzeWavSpeech(wavPath);
            bool speechDetected = rms >= 0.012 && peak >= 0.06 && speechRatio >= 0.12;
            if (!speechDetected)
                return;

            _lastSpeechTriggerUtc = DateTime.UtcNow;
            _logger.Info($"Local speech trigger activated (rms={rms:F4}, peak={peak:F4}, ratio={speechRatio:F2}). Starting Gemini voice capture...");

            if (InvokeRequired)
                Invoke(async () => await ToggleVoiceRecordingAsync());
            else
                await ToggleVoiceRecordingAsync();
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("429") || ex.Message.Contains("503"))
            {
                _lastSpeechTriggerUtc = DateTime.UtcNow.AddSeconds(25);
            }
            _logger.Warn($"Local speech poll skipped: {ex.Message}");
        }
        finally
        {
            try { mciSendStringW($"close {WakeAlias}", null, 0, IntPtr.Zero); } catch { }
            if (!string.IsNullOrWhiteSpace(wavPath))
            {
                try { File.Delete(wavPath); } catch { }
            }
            _isWakeChecking = false;
        }
    }

    private void HandleHeardText(string heard, bool directFromHotkeyOrTray = false)
    {
        var normalized = NormalizeWakeText(heard);
        string[] wakePhrases =
        {
            "окей гугл",
            "окей google",
            "ок гугл",
            "ok google",
            "хей гугл",
            "hey google"
        };

        string? matchedWake = wakePhrases.FirstOrDefault(w => normalized.Contains(w, StringComparison.Ordinal));
        if (matchedWake != null)
        {
            _wakeActive = true;
            SetOrbActiveState(true);
            Invalidate();

            var idx = normalized.IndexOf(matchedWake, StringComparison.Ordinal);
            var cmd = normalized[(idx + matchedWake.Length)..].Trim();

            if (!string.IsNullOrWhiteSpace(cmd))
                _ = ExecuteCommandAsync(cmd, fromVoice: true);
            else
                SpeakWithBubble("Слушаю.");
            return;
        }

        if (directFromHotkeyOrTray || _wakeActive)
            _ = ExecuteCommandAsync(heard, fromVoice: true);
    }

    private void HideOrbAfterCommand()
    {
        _wakeActive = false;
        _speaking = false;
        if (!_micRecorder.IsRecording && !_isTranscribing)
        {
            _bubble.HideBubble();
            SetOrbActiveState(false);
            Invalidate();
        }
    }

    private List<string> CollectLaunchTargets()
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddItem(string item)
        {
            var trimmed = item.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed))
                targets.Add(trimmed);
        }

        AddItem("notepad.exe");
        AddItem("cmd.exe");
        AddItem("calc.exe");
        AddItem("firefox.exe");

        var yandexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Yandex\YandexBrowser\Application\browser.exe");
        if (File.Exists(yandexPath))
            AddItem(yandexPath);

        string[] knownExes =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Mozilla Firefox\firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Mozilla Firefox\firefox.exe")
        ];
        foreach (var exe in knownExes)
        {
            if (File.Exists(exe))
                AddItem(exe);
        }

        var settings = SidekickSettingsStore.Load();
        foreach (var customEntry in settings.GetAllCustomFolders())
        {
            if (string.IsNullOrWhiteSpace(customEntry))
                continue;

            var clean = customEntry.Trim().Trim('"');
            if (File.Exists(clean))
            {
                AddItem(clean);
                continue;
            }

            if (Directory.Exists(clean))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(clean, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .Take(200))
                    {
                        AddItem(file);
                    }
                }
                catch
                {
                    // Пропускаем недоступные каталоги
                }
            }
        }

        string[] shortcutRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")
        ];

        foreach (var root in shortcutRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).Take(150))
                {
                    AddItem(lnk);
                }
            }
            catch
            {
                // Пропускаем недоступные каталоги
            }
        }

        return targets;
    }

    private string LaunchDynamicTarget(string? target)
    {
        var trimmed = (target ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
            return "Не указана цель для запуска.";

        var targets = CollectLaunchTargets();
        var match = targets.FirstOrDefault(t =>
            Path.GetFileNameWithoutExtension(t).Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(t).Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(t).Contains(trimmed, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(match))
        {
            try
            {
                _logger.Info($"AI matched target '{match}' for '{trimmed}'.");
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = match,
                    UseShellExecute = true
                });
                if (proc != null)
                    _ = Task.Run(() => BringProcessWindowToFront(Path.GetFileNameWithoutExtension(match)));
                return $"Открываю '{Path.GetFileNameWithoutExtension(match)}'.";
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to launch matched target '{match}'.", ex);
                return $"Не вышло запустить '{match}': {ex.Message}";
            }
        }

        try
        {
            _logger.Info($"AI direct launch '{trimmed}'.");
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = trimmed,
                UseShellExecute = true
            });
            if (proc != null)
                _ = Task.Run(() => BringProcessWindowToFront(Path.GetFileNameWithoutExtension(trimmed)));
            return $"Запущено: {trimmed}";
        }
        catch (Exception ex)
        {
            _logger.Warn($"Direct launch failed for '{trimmed}': {ex.Message}");
            return _launcher.Launch(trimmed, requestAdmin: false);
        }
    }
    private async Task ExecuteCommandAsync(string cmdText, bool fromVoice)
    {
        _logger.Info($"Command ({(fromVoice ? "voice" : "text")}): '{cmdText}'");

        var targets = CollectLaunchTargets();
        var lowerCmd = cmdText.Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "открой ", "запусти ", "включи ", "open ", "launch ", "start " })
        {
            if (lowerCmd.StartsWith(prefix, StringComparison.Ordinal))
            {
                var appQuery = cmdText.Trim().Substring(prefix.Length).Trim().TrimEnd('.', '!', '?');
                if (!string.IsNullOrWhiteSpace(appQuery))
                {
                    var directMatch = targets.FirstOrDefault(t =>
                        Path.GetFileNameWithoutExtension(t).Equals(appQuery, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileNameWithoutExtension(t).Contains(appQuery, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(directMatch))
                    {
                        var fastStatus = LaunchDynamicTarget(directMatch);
                        _logger.Info($"Fast local launch: '{fastStatus}'");
                        SpeakWithBubble(fastStatus);
                        var fastHide = new System.Windows.Forms.Timer { Interval = 1600 };
                        fastHide.Tick += (_, _) => { fastHide.Stop(); fastHide.Dispose(); HideOrbAfterCommand(); };
                        fastHide.Start();
                        return;
                    }
                }
            }
        }

        var shortTargets = targets
            .Select(t => Path.GetFileNameWithoutExtension(t))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();
        var decision = await GeminiAudioTranscriber.DecideAndReplyAsync(cmdText, shortTargets);

        string reply = decision.Reply;
        if (string.Equals(decision.Action, "launch", StringComparison.OrdinalIgnoreCase))
        {
            var launchStatus = LaunchDynamicTarget(decision.Target);
            _logger.Info($"Launch status: '{launchStatus}'");
            if (string.IsNullOrWhiteSpace(reply))
                reply = launchStatus;
        }
        else if (string.Equals(decision.Action, "paste", StringComparison.OrdinalIgnoreCase))
        {
            var textToPaste = string.IsNullOrWhiteSpace(decision.Target) ? cmdText : decision.Target;
            TryPasteIntoActiveWindow(textToPaste);
            if (string.IsNullOrWhiteSpace(reply))
                reply = textToPaste;
        }

        if (string.IsNullOrWhiteSpace(reply))
            reply = "Готово.";

        _logger.Info($"Reply: '{reply}'");
        SpeakWithBubble(reply);

        var hideTimer = new System.Windows.Forms.Timer { Interval = 2200 };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();
            hideTimer.Dispose();
            HideOrbAfterCommand();
        };
        hideTimer.Start();
    }
    private bool TryPasteIntoActiveWindow(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            IDataObject? previousData = null;
            try { previousData = Clipboard.GetDataObject(); } catch { }

            Clipboard.SetText(text);
            Thread.Sleep(60);
            SendKeys.SendWait("^v");
            Thread.Sleep(80);

            if (previousData != null)
            {
                try { Clipboard.SetDataObject(previousData, true); } catch { }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to paste recognized text into active window.", ex);
            return false;
        }
    }
    private void SpeakWithBubble(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        bool startedAsync = false;
        if (_synth != null)
        {
            try
            {
                var t = _synth.GetType();
                t.GetMethod("SpeakAsyncCancelAll", Type.EmptyTypes)?.Invoke(_synth, null);
                t.GetMethod("SpeakAsync", new[] { typeof(string) })?.Invoke(_synth, new object[] { text });
                startedAsync = true;
            }
            catch (Exception ex)
            {
                _logger.Error("TTS error.", ex);
            }
        }

        _speaking = true;
        SetOrbActiveState(true);
        Invalidate();
        _bubble.ShowAboveOrb(new Point(_targetX, _targetY), Size, text, autoHideAfterFallback: !startedAsync);
    }


    private void InitTrayAndHotKey()
    {
        try
        {
            if (RegisterHotKey(Handle, HotKeyIdVoice, ModControl | ModAlt, VkV))
                _logger.Info("Global hotkey registered: Ctrl+Alt+V");
            else
                _logger.Warn("Failed to register global hotkey Ctrl+Alt+V.");
        }
        catch (Exception ex)
        {
            _logger.Error("Error registering hotkey.", ex);
        }

        try
        {
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "GeminiSidekick (Ctrl+Alt+V — голос)",
                Visible = true,
                ContextMenuStrip = _menu
            };
            _trayIcon.MouseClick += async (_, args) =>
            {
                if (args.Button == MouseButtons.Left)
                    await ToggleVoiceRecordingAsync();
            };
        }
        catch (Exception ex)
        {
            _logger.Error("Error creating tray icon.", ex);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey && m.WParam.ToInt32() == HotKeyIdVoice)
        {
            _ = ToggleVoiceRecordingAsync();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _logger.Info("OrbForm closing.");
        _slideTimer.Stop();
        _bubble.Close();
        try { _micRecorder.Dispose(); } catch { }
        try { UnregisterHotKey(Handle, HotKeyIdVoice); } catch { }
        try { if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); } } catch { }
        try { (_synth as IDisposable)?.Dispose(); } catch { }
        base.OnFormClosed(e);
    }

}
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var logger = new AppLogger();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => logger.Error("Unhandled UI thread exception.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.Error("Unhandled domain exception.", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        var yandexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Yandex\YandexBrowser\Application\browser.exe");
        var launcher = new AppLauncher(new Dictionary<string, AppRule>
        {
            ["notepad"] = new() { ExecutableOrCommand = "notepad.exe", AllowAdmin = false },
            ["cmd"] = new() { ExecutableOrCommand = "cmd.exe", AllowAdmin = false },
            ["yandex"] = new() { ExecutableOrCommand = File.Exists(yandexPath) ? yandexPath : "https://ya.ru", AllowAdmin = false }
        }, logger);
        Application.Run(new OrbForm(launcher, logger));
    }
}















