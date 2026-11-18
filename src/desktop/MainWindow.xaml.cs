using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using desktop.Services;

namespace desktop;

public partial class MainWindow : Window
{
    private readonly NativeCapture capture = new();
    private readonly MediaSessions media = new();
    private RoomConnection? room;
    private AudioBroadcast? broadcast;
    private IReadOnlyList<MediaChoice> sessions = [];
    private Song song = new();
    private volatile bool closing;
    private bool allowClose, refreshingSessions;
    private Task captureStartTask = Task.CompletedTask;
    private Task roomCleanupTask = Task.CompletedTask;
    private bool updatingProcessFilter;
    private int processRefreshVersion;
    private DateTime lastLevel;
    private long lastSoundTicks;
    private bool silenceReported;
    private string captureLabel = "";
    private readonly DispatcherTimer healthTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    public MainWindow()
    {
        InitializeComponent();
        capture.Samples += pcm =>
        {
            if (closing) return;
            broadcast?.Push(pcm); double peak = 0; foreach (short sample in pcm) peak = Math.Max(peak, Math.Abs((double)sample) / 32768);
            if (peak > .0001) Interlocked.Exchange(ref lastSoundTicks, DateTime.UtcNow.Ticks);
            if ((DateTime.UtcNow - lastLevel).TotalMilliseconds >= 100) { lastLevel = DateTime.UtcNow; _ = Dispatcher.InvokeAsync(() => AudioMeter.Value = peak); }
        };
        healthTimer.Tick += (_, _) =>
        {
            if (!capture.IsRunning) return;
            bool silent = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref lastSoundTicks)) > TimeSpan.FromSeconds(10);
            if (silent && song.PlaybackStatus is "paused" or "stopped") { AudioMeter.Value = 0; return; }
            if (silent == silenceReported) return; silenceReported = silent;
            string warning = silent ? "未检测到目标程序音频。请确认 PID、播放状态及软件是否使用独立音频服务；受保护音频可能无法捕获。" : "";
            CaptureText.Text = silent ? warning : captureLabel; if (silent) { AudioMeter.Value = 0; Log(warning); }
            try { if (room?.Connected == true) room.Send("capture_state", new { active = true, error = warning }); } catch (Exception ex) { Log(ex.Message); }
        }; healthTimer.Start();
        capture.Failed += error => _ = Dispatcher.InvokeAsync(() => { if (!closing) { StopCapture(error); Log(error); } });
        media.Warning += Log;
        media.SongChanged += value => _ = Dispatcher.InvokeAsync(() => ShowSong(value));
        media.SessionsChanged += value => _ = Dispatcher.InvokeAsync(() =>
        {
            if (closing) return;
            var previous = SessionsCombo.SelectedItem as MediaChoice; sessions = value;
            refreshingSessions = true; SessionsCombo.ItemsSource = value; SessionsCombo.SelectedItem = value.FirstOrDefault(s => s.AppId == previous?.AppId); refreshingSessions = false;
            if (SessionsCombo.SelectedItem == null) MatchSession(); else media.Select(SessionsCombo.SelectedItem as MediaChoice);
        });
    }
    private void Log(string text)
    {
        if (closing) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(() => Log(text)); return; }
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n"); if (LogBox.Text.Length > 24000) LogBox.Text = LogBox.Text[^16000..]; LogBox.ScrollToEnd();
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) { await media.InitializeAsync(); if (closing) return; await RefreshProcesses(); Log("LMT 已就绪。选择目标进程后开始捕获，再创建房间。"); }
    private async Task RefreshProcesses()
    {
        int version = ++processRefreshVersion;
        bool all = ShowAllCheck.IsChecked == true;
        bool activeOnly = ActiveAudioCheck.IsChecked == true;
        RefreshButton.IsEnabled = false;
        try
        {
            var list = await Task.Run(() => NativeCapture.Enumerate(all, activeOnly));
            if (closing || version != processRefreshVersion) return;
            uint? pid = (ProcessesList.SelectedItem as AudioProcess)?.Pid;
            ProcessesList.ItemsSource = list;
            ProcessesList.SelectedItem = list.FirstOrDefault(p => p.Pid == pid);
            if (ProcessesList.SelectedItem != null) ProcessesList.ScrollIntoView(ProcessesList.SelectedItem);
            if (list.Count == 0) Log(activeOnly ? "未发现有音频活动的进程。请开始播放后刷新，或取消音频活动筛选。" : "未发现音频进程。请先启动音乐播放，或勾选显示全部进程。");
        }
        catch (Exception ex) { if (!closing && version == processRefreshVersion) Log("枚举进程失败：" + ex.Message); }
        finally { if (version == processRefreshVersion) RefreshButton.IsEnabled = true; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshProcesses();
    private async void ProcessFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || updatingProcessFilter) return;
        updatingProcessFilter = true;
        try
        {
            if (sender == ActiveAudioCheck && ActiveAudioCheck.IsChecked == true) ShowAllCheck.IsChecked = false;
            else if (sender == ShowAllCheck && ShowAllCheck.IsChecked == true) ActiveAudioCheck.IsChecked = false;
        }
        finally { updatingProcessFilter = false; }
        await RefreshProcesses();
    }
    private void Process_Changed(object sender, SelectionChangedEventArgs e) { if (!capture.IsRunning) MatchSession(); }
    private void MatchSession()
    {
        var matched = ProcessesList.SelectedItem is AudioProcess p ? MediaSessions.Match(p, sessions) : null;
        refreshingSessions = true; SessionsCombo.SelectedItem = matched; refreshingSessions = false; media.Select(matched);
        SessionHint.Text = matched == null ? "未匹配到媒体会话。可手动选择；音频仍可传输。" : "已根据应用标识匹配；可手动改选媒体会话。";
    }
    private void Session_Changed(object sender, SelectionChangedEventArgs e) { if (!refreshingSessions) media.Select(SessionsCombo.SelectedItem as MediaChoice); }
    private void ShowSong(Song value)
    {
        if (closing) return;
        song = value; TitleText.Text = value.Title; ArtistText.Text = value.Artist; AlbumText.Text = value.Album; PlaybackText.Text = value.PlaybackStatus switch { "playing" => "正在播放", "paused" => "已暂停", "stopped" => "已停止", _ => "播放状态未知" };
        CoverImage.Source = null; if (value.Cover != "") try { var bytes = Convert.FromBase64String(value.Cover[(value.Cover.IndexOf(',') + 1)..]); using var stream = new MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); CoverImage.Source = image; } catch { }
        try { if (room?.Connected == true) room.Send("metadata", song); } catch (Exception ex) { Log(ex.Message); }
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessesList.SelectedItem is not AudioProcess process) { Log("请先选择一个音乐进程。"); return; }
        if (closing || !captureStartTask.IsCompleted) return;
        StartButton.IsEnabled = false; ProcessesList.IsEnabled = false; CreateButton.IsEnabled = false;
        try
        {
            Interlocked.Exchange(ref lastSoundTicks, DateTime.UtcNow.Ticks); silenceReported = false;
            captureStartTask = Task.Run(() => capture.Start(process.Pid));
            await captureStartTask;
            if (closing) return;
            broadcast?.SetCapturing(true); StopButton.IsEnabled = true; captureLabel = $"正在捕获 {process.Name} · PID {process.Pid} · 包含子进程"; CaptureText.Text = captureLabel; Log(CaptureText.Text);
        }
        catch (Exception ex) { if (!closing) { Log(ex.Message); StartButton.IsEnabled = true; ProcessesList.IsEnabled = true; } }
        finally { if (!closing) CreateButton.IsEnabled = room == null; }
    }
    private void StopCapture(string error = "")
    {
        capture.Stop(); try { broadcast?.SetCapturing(false, error); } catch (Exception ex) { Log(ex.Message); }
        StartButton.IsEnabled = true; StopButton.IsEnabled = false; ProcessesList.IsEnabled = true; AudioMeter.Value = 0; CaptureText.Text = string.IsNullOrEmpty(error) ? "捕获已停止" : error;
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { StopCapture(); Log("已停止音频捕获。"); }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        try
        {
            var connection = new RoomConnection(); room = connection;
            broadcast = new AudioBroadcast(connection);
            broadcast.Status += text => _ = Dispatcher.InvokeAsync(() => { if (!closing) ConnectionText.Text = text; });
            broadcast.Failed += Log;
            connection.Closed += error => _ = Dispatcher.InvokeAsync(async () => { if (closing || room != connection) return; Log(error); ConnectionText.Text = error; await CloseRoomAsync(); });
            await connection.ConnectAsync(ServerUrl.Text, CreationKey.Password);
            if (closing || room != connection) return;
            connection.Send("metadata", song); broadcast.SetCapturing(capture.IsRunning);
            GuestLink.Text = connection.Room!.GuestUrl; CopyButton.IsEnabled = true; CloseRoomButton.IsEnabled = true;
            Log($"房间已创建，有效期至 {connection.Room.ExpiresAt.ToLocalTime():MM-dd HH:mm}，最多一位访客。");
        }
        catch (Exception ex) { if (!closing) { Log(ex.Message); ConnectionText.Text = "创建房间失败"; await CloseRoomAsync(); } }
    }
    private async Task CloseRoomAsync()
    {
        var oldRoom = room; var oldBroadcast = broadcast; room = null; broadcast = null;
        // Peer disposal can block; keep it off the dispatcher and finish signaling first.
        var cleanup = Task.Run(async () =>
        {
            try { if (oldRoom != null) await oldRoom.DisposeAsync(); }
            finally { if (oldBroadcast != null) await oldBroadcast.DisposeAsync(); }
        });
        roomCleanupTask = Task.WhenAll(roomCleanupTask, cleanup);
        await roomCleanupTask;
        if (!closing) { GuestLink.Text = ""; CopyButton.IsEnabled = false; CloseRoomButton.IsEnabled = false; CreateButton.IsEnabled = true; }
    }
    private async void CloseRoom_Click(object sender, RoutedEventArgs e) { await CloseRoomAsync(); ConnectionText.Text = "房间已关闭"; Log("房间已关闭，访问链接已失效。"); }
    private void Copy_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(GuestLink.Text); Log("已复制访客链接。"); } catch (Exception ex) { Log("复制失败：" + ex.Message); } }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return; e.Cancel = true; if (closing) return;
        closing = true;
        healthTimer.Stop(); Hide();
        // Let the original Closing event unwind even when cleanup completes synchronously.
        await Dispatcher.Yield(DispatcherPriority.Background);
        var stopCapture = Task.Run(async () =>
        {
            try { await captureStartTask.ConfigureAwait(false); } catch { }
            capture.Dispose();
        });
        try
        {
            try { media.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            await Task.WhenAll(stopCapture, CloseRoomAsync());
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { allowClose = true; Close(); }
    }
}
