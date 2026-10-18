using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Dispatching;

namespace ChenLink.Services;

public enum LogLevel { Info, Success, Warning, Error }

public enum LogFilter { All, Warning, Error }

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = "";

    public string TimeText => Time.ToString("HH:mm:ss");
    public bool IsSuccess => Level == LogLevel.Success;
    public bool IsWarning => Level == LogLevel.Warning;
    public bool IsError => Level == LogLevel.Error;
}

/// <summary>
/// 统一日志出口：界面日志（分级、可筛选、上限 800 条）+ 按天滚动落盘。
/// 旧版 UI 用裸 string 集合、错误只写进最底部的日志框、用户看不到任何提示，这里把分级信息保留下来供界面着色。
/// </summary>
public sealed class LogService
{
    const int MaxEntries = 800;
    readonly DispatcherQueue _dq;
    readonly List<LogEntry> _all = new();
    readonly object _lock = new();
    string? _file;
    DateTime _fileDate;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogFilter Filter { get; private set; } = LogFilter.All;

    public string Directory { get; }

    public event Action? Changed;

    public LogService()
    {
        _dq = DispatcherQueue.GetForCurrentThread();
        Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChenLink", "logs");
    }

    public void Info(string message) => Add(LogLevel.Info, message);
    public void Success(string message) => Add(LogLevel.Success, message);
    public void Warn(string message) => Add(LogLevel.Warning, message);
    public void Error(string message) => Add(LogLevel.Error, message);

    public void Add(LogLevel level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message };
        WriteFile(level, message);

        if (_dq.HasThreadAccess)
        {
            Append(entry);
        }
        else
        {
            _dq.TryEnqueue(() => Append(entry));
        }
    }

    void Append(LogEntry entry)
    {
        lock (_lock)
        {
            _all.Add(entry);
            if (_all.Count > MaxEntries) _all.RemoveRange(0, _all.Count - MaxEntries);
        }
        if (Matches(entry)) AddVisible(entry);
        Changed?.Invoke();
    }

    void AddVisible(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    bool Matches(LogEntry e) => Filter switch
    {
        LogFilter.Warning => e.Level is LogLevel.Warning or LogLevel.Error,
        LogFilter.Error => e.Level == LogLevel.Error,
        _ => true,
    };

    public void SetFilter(LogFilter filter)
    {
        if (Filter == filter) return;
        Filter = filter;
        lock (_lock)
        {
            Entries.Clear();
            foreach (var e in _all.Where(Matches)) Entries.Add(e);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) _all.Clear();
        Entries.Clear();
        Changed?.Invoke();
    }

    /// <summary>导出全部日志文本，用于「复制日志」。</summary>
    public string Dump()
    {
        lock (_lock)
        {
            return string.Join(Environment.NewLine, _all.Select(e => $"[{e.Time:HH:mm:ss}] {Tag(e.Level)} {e.Message}"));
        }
    }

    static string Tag(LogLevel level) => level switch
    {
        LogLevel.Success => "OK  ",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERR ",
        _ => "INFO",
    };

    void WriteFile(LogLevel level, string message)
    {
        try
        {
            lock (_lock)
            {
                var today = DateTime.Today;
                if (_file is null || _fileDate != today)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    _file = Path.Combine(Directory, $"app-{today:yyyyMMdd}.log");
                    _fileDate = today;
                }
                File.AppendAllText(_file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Tag(level)} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志落盘失败不能影响主流程（可能处于只读目录）
        }
    }
}
