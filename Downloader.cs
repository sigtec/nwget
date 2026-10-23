using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using Microsoft.Win32.SafeHandles;

namespace Nwget;

/// <summary>Error where retrying makes no sense (404, write error, ...).</summary>
sealed class DownloadException(string message) : Exception(message);

/// <summary>The file on the server is no longer the one the download started with.</summary>
sealed class RemoteChangedException() : Exception("The file on the server changed during the download.");

/// <summary>Result of the probe. <see cref="FileName"/> is null if neither header nor URL provide one.</summary>
sealed record RemoteInfo(long? Length, bool RangesSupported, string? ETag, DateTimeOffset? LastModified, string? FileName);

sealed class Downloader
{
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);     // no data for this long = abort + retry

    readonly Uri _url;
    readonly string? _output;
    readonly int _workers;
    readonly int _blockSize;    // only for new downloads; a resumed download keeps the block size from its .state
    readonly bool _force;       // -f: discard non-resumable leftovers and start over instead of aborting
    readonly HttpClient _http;
    readonly object _commitLock = new();
    Exception? _fatal;

    public Downloader(Uri url, string? output, int workers, int blockSize, bool force)
    {
        _url = url;
        _output = output;
        _workers = workers;
        _blockSize = blockSize;
        _force = force;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        // No global timeout: large files may take a long time, hanging connections are
        // detected via StallTimeout.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("nwget/1.0");
        _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
    }

    public async Task<int> RunAsync(CancellationToken ct, bool restarted = false)
    {
        var info = await ProbeAsync(ct);
        string target = ResolveTargetPath(info.FileName);
        string partPath = target + ".part";
        string statePath = target + ".state";

        if (File.Exists(target))
            throw new DownloadException($"Target file already exists: {target}");

        bool chunked = info.RangesSupported && info.Length > 0;

        if (!restarted)
        {
            Console.WriteLine($"Target: {target}");
            Console.WriteLine($"Size:   {(info.Length is long len ? Progress.FormatBytes(len) : "unknown")}");
            Console.WriteLine("Mode:   " + info.Length switch
            {
                0 => "empty file",
                null => "server reports no size – simple download, resuming not possible",
                _ when chunked => $"{_workers} workers, blocks of {Progress.FormatBytes(_blockSize)}, resumable",
                _ => "server does not support range requests – simple download, resuming not possible",
            });
        }

        if (info.Length == 0)
        {
            DiscardLeftovers(partPath, statePath);
            File.Create(target).Dispose();
        }
        else if (chunked)
        {
            try
            {
                await ChunkedAsync(info, info.Length!.Value, target, partPath, statePath, ct);
            }
            catch (RemoteChangedException) when (_force && !restarted)
            {
                Console.WriteLine("The file on the server has changed – discarding partial download and starting over.");
                TryDelete(partPath);
                TryDelete(statePath);
                return await RunAsync(ct, restarted: true);
            }
            catch (RemoteChangedException) when (!_force)
            {
                throw Unresumable("The file on the server changed during the download");
            }
        }
        else
        {
            DiscardLeftovers(partPath, statePath);
            await SingleStreamAsync(info, target, partPath, ct);
        }

        Console.WriteLine($"Done:   {target}");
        return 0;
    }

    // ------------------------------------------------------------------
    // Probe: determine size, range support, validators and file name
    // ------------------------------------------------------------------

    async Task<RemoteInfo> ProbeAsync(CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(StallTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                string? name = FileNameFrom(response);
                string? etag = response.Headers.ETag?.ToString();
                var lastModified = response.Content.Headers.LastModified;
                var contentRange = response.Content.Headers.ContentRange;

                // Empty file: the server rejects "bytes=0-0" with 416 and reports "bytes */0".
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    long? length = contentRange is { HasLength: true } ? contentRange.Length : null;
                    return new RemoteInfo(length == 0 ? 0 : null, false, etag, lastModified, name);
                }

                ThrowIfFatal(response);
                response.EnsureSuccessStatusCode();

                // With 206, Content-Length is the length of the range (1 byte), not of the file.
                // "bytes 0-0/*" = ranges work, but the total size is unknown -> simple download.
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    long? length = contentRange is { HasLength: true } ? contentRange.Length : null;
                    return new RemoteInfo(length, length is not null, etag, lastModified, name);
                }

                return new RemoteInfo(response.Content.Headers.ContentLength, false, etag, lastModified, name);
            }
            catch (Exception ex) when (IsTransient(ex, ct, firstContact: true))
            {
                var delay = Backoff(attempt);
                Console.WriteLine($"Server not reachable ({Describe(ex)}) – retrying in {delay.TotalSeconds:0}s");
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>File name from Content-Disposition, else from the last URL segment (after redirects); null if none.</summary>
    string? FileNameFrom(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        string? name = disposition?.FileNameStar ?? disposition?.FileName;

        if (string.IsNullOrWhiteSpace(name))
            name = LastSegment(response.RequestMessage?.RequestUri) ?? LastSegment(_url);

        name = Path.GetFileName((name ?? "").Trim().Trim('"'));
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? null : name;

        static string? LastSegment(Uri? uri)
        {
            if (uri is null)
                return null;
            string segment = Uri.UnescapeDataString(uri.Segments[^1]).Trim('/');
            return segment.Length > 0 ? segment : null;
        }
    }

    /// <summary>
    /// -o is a directory if it exists as one or ends with a path separator (created if necessary);
    /// the remote file name is then used inside it. Otherwise -o is the target file.
    /// </summary>
    string ResolveTargetPath(string? remoteName)
    {
        bool isDirectory = string.IsNullOrWhiteSpace(_output)
            || Directory.Exists(_output)
            || _output.EndsWith(Path.DirectorySeparatorChar)
            || _output.EndsWith(Path.AltDirectorySeparatorChar);

        if (!isDirectory)
        {
            string full = Path.GetFullPath(_output!);
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            return full;
        }

        if (remoteName is null)
            throw new DownloadException("Neither the server nor the URL provide a file name. Use -o to specify the target file.");

        string directory = string.IsNullOrWhiteSpace(_output) ? "." : _output;
        Directory.CreateDirectory(directory);
        return Path.GetFullPath(Path.Combine(directory, remoteName));
    }

    // ------------------------------------------------------------------
    // Block download with parallel workers and state file
    // ------------------------------------------------------------------

    async Task ChunkedAsync(RemoteInfo info, long total, string target, string partPath, string statePath, CancellationToken ct)
    {
        _fatal = null;

        StateFile? state = TryResume(info, total, partPath, statePath);
        FileStream? part = null;
        bool complete = false;

        try
        {
            if (state is not null)
            {
                part = OpenPart(partPath, FileMode.Open);
                Console.WriteLine($"Resuming: {state.DoneCount} of {state.BlockCount} blocks already present.");
                if (state.BlockSize != _blockSize)
                    Console.WriteLine($"Note: the partial download uses blocks of {Progress.FormatBytes(state.BlockSize)}, keeping that size.");
            }
            else
            {
                // Order: first create .part at its final size, then .state. If interrupted in between,
                // .state is missing; the next run then aborts (or starts over with -f).
                TryDelete(statePath);
                part = OpenPart(partPath, FileMode.Create);
                part.SetLength(total);
                part.Flush(flushToDisk: true);
                int blockCount = checked((int)((total + _blockSize - 1) / _blockSize));
                state = StateFile.Create(statePath, _blockSize, blockCount, info.ETag, info.LastModified);
            }

            StateFile st = state;
            FileStream partFile = part;
            SafeFileHandle handle = partFile.SafeFileHandle;

            var progress = new Progress(total, DoneBytes(st, total));
            var queue = new ConcurrentQueue<int>(Enumerable.Range(0, st.BlockCount).Where(i => !st.IsDone(i)));
            var ifRange = BuildIfRange(info);
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Committing a block: flush the .part data to disk, then set the bit in .state
            // (writes 1 byte + fsync). This way .state never lists a block that is not on disk.
            void Commit(int index)
            {
                lock (_commitLock)
                {
                    partFile.Flush(flushToDisk: true);
                    st.MarkDone(index);
                }
            }

            int workerCount = Math.Max(1, Math.Min(_workers, queue.Count));
            var allWorkers = Task.WhenAll(Enumerable.Range(0, workerCount)
                .Select(_ => Task.Run(() => WorkerAsync(queue, handle, total, st.BlockSize, ifRange, progress, Commit, runCts))));

            // The main thread only renders progress – the state is persisted after every block.
            while (!allWorkers.IsCompleted)
            {
                await Task.WhenAny(allWorkers, Task.Delay(500, CancellationToken.None));
                progress.Render();
            }
            await allWorkers; // workers catch their own exceptions
            progress.Finish();

            complete = st.AllDone;
            if (!complete)
            {
                ct.ThrowIfCancellationRequested();
                if (_fatal is not null)
                    ExceptionDispatchInfo.Throw(_fatal);
                throw new DownloadException("Download incomplete.");
            }
        }
        finally
        {
            state?.Dispose();
            part?.Dispose();
        }

        File.Move(partPath, target);
        TryDelete(statePath);
    }

    async Task WorkerAsync(ConcurrentQueue<int> queue, SafeFileHandle handle, long total, int blockSize,
        RangeConditionHeaderValue? ifRange, Progress progress, Action<int> commit, CancellationTokenSource runCts)
    {
        var buffer = new byte[64 * 1024];
        var ct = runCts.Token;

        try
        {
            while (!ct.IsCancellationRequested && queue.TryDequeue(out int index))
            {
                long from = (long)index * blockSize;
                int length = (int)Math.Min(blockSize, total - from);

                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        await FetchBlockAsync(from, length, total, handle, buffer, ifRange, progress, ct);
                        break;
                    }
                    catch (Exception ex) when (IsTransient(ex, ct))
                    {
                        var delay = Backoff(attempt);
                        progress.Retry($"Block {index}: {Describe(ex)} – retrying in {delay.TotalSeconds:0}s");
                        await Task.Delay(delay, ct);
                    }
                }

                // The block is completely in .part – commit it even if cancellation was requested meanwhile.
                commit(index);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Cancelled by the caller or by a fatal error of another worker
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _fatal, ex, null);
            runCts.Cancel();
        }
    }

    async Task FetchBlockAsync(long from, int length, long total, SafeFileHandle handle, byte[] buffer,
        RangeConditionHeaderValue? ifRange, Progress progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new RangeHeaderValue(from, from + length - 1);
        if (ifRange is not null)
            request.Headers.IfRange = ifRange;

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);

        // 200 to a range request with If-Range = file changed; 416 = file got shorter
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new RemoteChangedException();
        ThrowIfFatal(response);
        response.EnsureSuccessStatusCode();

        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.From != from)
            throw new IOException("Unexpected response to range request");
        if (range.HasLength && range.Length != total)
            throw new RemoteChangedException();

        // Stream directly to the block's offset in .part. A partially written block is harmless:
        // its bit is only set after the last byte, and a new attempt overwrites it.
        await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
        int received = 0;
        try
        {
            while (received < length)
            {
                stall.CancelAfter(StallTimeout); // restart the timer on every read
                int n = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length - received)), stall.Token);
                if (n == 0)
                    throw new IOException("Connection closed prematurely");
                await WritePartAsync(handle, buffer.AsMemory(0, n), from + received);
                received += n;
                progress.Add(n);
            }
        }
        catch
        {
            progress.Add(-received);
            throw;
        }
    }

    /// <summary>
    /// Deliberately without CancellationToken: received data is always written. Write errors
    /// (disk full, ...) are not transient and must not end up in the retry loop.
    /// </summary>
    static async ValueTask WritePartAsync(SafeFileHandle handle, ReadOnlyMemory<byte> data, long offset)
    {
        try
        {
            await RandomAccess.WriteAsync(handle, data, offset, CancellationToken.None);
        }
        catch (IOException ex)
        {
            throw new DownloadException($"Write error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the state file of a resumable download, or null if there are no leftovers.
    /// If existing leftovers don't fit (source changed, file missing or broken), the download is
    /// aborted – with -f, null is returned instead, i.e. the download starts over.
    /// </summary>
    StateFile? TryResume(RemoteInfo info, long total, string partPath, string statePath)
    {
        bool hasPart = File.Exists(partPath);
        bool hasState = File.Exists(statePath);
        if (!hasPart && !hasState)
            return null;

        StateFile? state = null;
        string? reason = null;
        if (!hasState)
            reason = $"{Path.GetFileName(partPath)} exists, but its .state file is missing";
        else if (!hasPart)
            reason = $"{Path.GetFileName(statePath)} exists, but its .part file is missing";
        // The size is not stored in .state – it is the length of the .part file.
        else if (new FileInfo(partPath).Length != total)
            reason = "Size of the .part file does not match the file on the server";
        else if ((state = StateFile.TryOpen(statePath, total)) is null)
            reason = "State file is unreadable or does not match the .part file";
        else if (state.ETag is not null && info.ETag is not null && state.ETag != info.ETag)
            reason = "ETag of the file on the server has changed";
        else if ((state.ETag is null || info.ETag is null)
                 && state.LastModified is not null && info.LastModified is not null
                 && state.LastModified != info.LastModified)
            reason = "Last-Modified of the file on the server has changed";

        if (reason is null)
            return state;

        state?.Dispose();
        if (!_force)
            throw Unresumable(reason);
        Console.WriteLine($"{reason} – discarding partial download and starting over.");
        return null;
    }

    /// <summary>Leftovers of a previous download when resuming is not possible this time: abort, or delete with -f.</summary>
    void DiscardLeftovers(string partPath, string statePath)
    {
        if (!File.Exists(partPath) && !File.Exists(statePath))
            return;
        if (!_force)
            throw Unresumable("Leftovers of a previous download exist, but resuming is not possible");
        Console.WriteLine("Discarding leftovers of a previous download.");
        TryDelete(partPath);
        TryDelete(statePath);
    }

    static DownloadException Unresumable(string reason) =>
        new($"{reason}.\nUse -f to discard the partial download and start over.");

    static long DoneBytes(StateFile state, long total)
    {
        long sum = 0;
        for (int i = 0; i < state.BlockCount; i++)
            if (state.IsDone(i))
                sum += Math.Min(state.BlockSize, total - (long)i * state.BlockSize);
        return sum;
    }

    static FileStream OpenPart(string path, FileMode mode) =>
        new(path, mode, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous);

    static RangeConditionHeaderValue? BuildIfRange(RemoteInfo info)
    {
        // If-Range only allows strong ETags
        if (info.ETag is { } tag && !tag.StartsWith("W/", StringComparison.Ordinal))
            return new RangeConditionHeaderValue(tag);
        if (info.LastModified is { } lastModified)
            return new RangeConditionHeaderValue(lastModified);
        return null;
    }

    // ------------------------------------------------------------------
    // Fallback: no range support or unknown size
    // -> a single stream, starting over on errors
    // ------------------------------------------------------------------

    async Task SingleStreamAsync(RemoteInfo info, string target, string partPath, CancellationToken ct)
    {
        var progress = new Progress(info.Length, 0);
        var buffer = new byte[81920];

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                                 bufferSize: 1 << 16, FileOptions.Asynchronous))
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(StallTimeout);

                    using var response = await _http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                    ThrowIfFatal(response);
                    response.EnsureSuccessStatusCode();

                    // With "Transfer-Encoding: chunked", HttpClient reports a dropped connection as an error.
                    // If the end is only signalled by closing the connection, an interruption cannot be
                    // told apart from a regular end.
                    long? expected = info.Length ?? response.Content.Headers.ContentLength;

                    await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
                    var lastRender = Stopwatch.StartNew();
                    long received = 0;
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        int n = await body.ReadAsync(buffer, stall.Token);
                        if (n == 0)
                            break;
                        try
                        {
                            await file.WriteAsync(buffer.AsMemory(0, n), CancellationToken.None);
                        }
                        catch (IOException ex)
                        {
                            throw new DownloadException($"Write error: {ex.Message}");
                        }
                        received += n;
                        progress.Add(n);
                        if (lastRender.ElapsedMilliseconds >= 500)
                        {
                            progress.Render();
                            lastRender.Restart();
                        }
                    }

                    if (expected is long e && received != e)
                        throw new IOException($"Incomplete: {received} of {e} bytes");
                    await file.FlushAsync(CancellationToken.None);
                }

                progress.Finish();
                File.Move(partPath, target);
                return;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                progress.Reset();
                var delay = Backoff(attempt);
                progress.Retry($"{Describe(ex)} – starting over in {delay.TotalSeconds:0}s");
                await Task.Delay(delay, ct);
            }
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    static void ThrowIfFatal(HttpResponseMessage response)
    {
        int code = (int)response.StatusCode;
        if (code is (>= 400 and < 500 and not 408 and not 429) or 501 or 505)
            throw new DownloadException($"Server responded with {code} {response.ReasonPhrase}.");
    }

    /// <summary>
    /// Is a retry worthwhile? An unknown host is only permanent on first contact – in the middle
    /// of a download on an unstable line it usually just means the network is gone.
    /// </summary>
    static bool IsTransient(Exception ex, CancellationToken ct, bool firstContact = false) =>
        !ct.IsCancellationRequested && ex switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => !firstContact,
            HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded } => false, // too many redirects
            HttpRequestException { InnerException: AuthenticationException } => false,                     // TLS/certificate
            HttpRequestException or IOException or OperationCanceledException or TimeoutException => true,
            _ => false,
        };

    static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(attempt - 1, 5)), 30) + Random.Shared.NextDouble());

    static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "timeout",
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
        _ => ex.InnerException?.Message ?? ex.Message,
    };

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
