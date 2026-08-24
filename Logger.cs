namespace WinNetFix;

/// <summary>滚动日志：按天分文件，保留 retentionDays 天。</summary>
public sealed class Logger : IDisposable
{
    private readonly string _dir;
    private readonly int _retentionDays;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _currentDate;
    private bool _writable;

    public LogLevel Level { get; set; } = LogLevel.Info;

    public Logger(string dir, int retentionDays, LogLevel level)
    {
        _dir = dir;
        _retentionDays = retentionDays;
        Level = level;
        try
        {
            Directory.CreateDirectory(_dir);
            _writable = true;
        }
        catch
        {
            // 目录不可创建（如 Program Files 下非管理员运行）时禁用落盘，不影响主流程
            _writable = false;
        }
    }

    public static LogLevel ParseLevel(string s) => s?.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    public void Debug(string msg) => Write(LogLevel.Debug, msg);
    public void Info(string msg) => Write(LogLevel.Info, msg);
    public void Warn(string msg) => Write(LogLevel.Warn, msg);
    public void Error(string msg) => Write(LogLevel.Error, msg);

    private void Write(LogLevel level, string msg)
    {
        if (level < Level) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        Console.WriteLine(line); // 前台模式（--status/--once）可见；后台隐藏窗口时无害
        if (!_writable) return;
        try
        {
            lock (_lock)
            {
                EnsureWriter();
                _writer!.WriteLine(line);
                _writer!.Flush();
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        if (_currentDate == today && _writer != null) return;

        _writer?.Dispose();
        _writer = null;
        var file = Path.Combine(_dir, $"winnetfix-{today}.log");
        _writer = new StreamWriter(file, append: true) { AutoFlush = true };
        _currentDate = today;

        // 顺手清理过期日志
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "winnetfix-*.log"))
            {
                var info = new FileInfo(f);
                if ((DateTime.Now - info.LastWriteTime).TotalDays > _retentionDays)
                    File.Delete(f);
            }
        }
        catch { /* 清理失败忽略 */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}
