namespace ChenLink.Core;

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };

    /// <summary>延迟值 → 展示文本；-1 表示尚未测得。</summary>
    public static string Latency(int ms) => ms < 0 ? "—" : $"{ms} ms";
}
