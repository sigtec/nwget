using System.Diagnostics;

namespace Nwget;

/// <summary>Progress line on the console plus log output, thread-safe.</summary>
sealed class Progress
{
    static readonly bool Interactive = !Console.IsOutputRedirected;

    readonly long? _total;
    readonly object _console = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();

    long _current;      // bytes present (finished blocks + bytes currently being received)
    long _transferred;  // bytes transferred in this run (for the speed)
    int _retries;

    double _lastTime;
    long _lastTransferred;
    double _speed;
    int _lineLength;

    public Progress(long? total, long alreadyDone)
    {
        _total = total;
        _current = alreadyDone;
    }

    public void Add(long bytes)
    {
        Interlocked.Add(ref _current, bytes);
        if (bytes > 0)
            Interlocked.Add(ref _transferred, bytes);
    }

    public void Reset() => Interlocked.Exchange(ref _current, 0);

    public void Retry(string message)
    {
        Interlocked.Increment(ref _retries);
        Log(message);
    }

    public void Log(string message)
    {
        lock (_console)
        {
            ClearLine();
            Console.WriteLine(message);
        }
    }

    public void Render()
    {
        lock (_console)
        {
            UpdateSpeed();
            if (!Interactive)
                return;
            string line = BuildLine();
            Console.Write("\r" + line.PadRight(_lineLength));
            _lineLength = line.Length;
        }
    }

    /// <summary>Final line with duration and average speed of this run.</summary>
    public void Finish()
    {
        lock (_console)
        {
            string line = BuildSummary();
            if (Interactive)
            {
                Console.Write("\r" + line.PadRight(_lineLength));
                Console.WriteLine();
                _lineLength = 0;
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }

    void ClearLine()
    {
        if (Interactive && _lineLength > 0)
        {
            Console.Write("\r" + new string(' ', _lineLength) + "\r");
            _lineLength = 0;
        }
    }

    void UpdateSpeed()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTime;
        if (dt < 1)
            return;
        long transferred = Interlocked.Read(ref _transferred);
        double current = (transferred - _lastTransferred) / dt;
        _speed = _lastTime == 0 ? current : 0.7 * _speed + 0.3 * current;
        _lastTime = now;
        _lastTransferred = transferred;
    }

    string BuildLine()
    {
        string speed = _lastTime > 0 ? $"{FormatBytes((long)_speed)}/s" : "-- B/s"; // meaningful only after 1 s
        if (_total is not long total || total == 0)
            return $"{Amount()}  {speed}{RetryText()}";

        string eta = _speed >= 1 ? FormatDuration((total - Interlocked.Read(ref _current)) / _speed) : "--:--";
        return $"{Amount()}  {speed}  ETA {eta}{RetryText()}";
    }

    string BuildSummary()
    {
        double seconds = _clock.Elapsed.TotalSeconds;
        long average = (long)(Interlocked.Read(ref _transferred) / Math.Max(seconds, 0.001));
        return $"{Amount()}  in {FormatDuration(seconds)}, avg {FormatBytes(average)}/s{RetryText()}";
    }

    string Amount()
    {
        long current = Interlocked.Read(ref _current);
        if (_total is not long total || total == 0)
            return FormatBytes(current);
        return $"{100.0 * current / total,5:0.0}%  {FormatBytes(current)} / {FormatBytes(total)}";
    }

    string RetryText() => _retries > 0 ? $"  retries: {_retries}" : "";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    static string FormatDuration(double seconds)
    {
        if (seconds > 99 * 3600)
            return ">99h";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
