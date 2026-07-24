namespace DiscordBot;

internal static class SizeFormatter
{
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.00} MiB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.0} KiB";
        return $"{bytes} bytes";
    }
}
