using Nwget;

// nwget <url> [-o <path>] [-n <workers>] [-s <size>] [-f]

const int MinBlockSize = 4 * 1024;
const int MaxBlockSize = 1024 * 1024 * 1024; // blocks are streamed, their size costs no memory

string? url = null;
string? output = null;
int workers = 2;
int blockSize = 1024 * 1024;
bool force = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" or "--output" when i + 1 < args.Length:
            output = args[++i];
            break;

        case "-n" or "--workers" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out workers) || workers is < 1 or > 16)
                return Usage("-n expects a number between 1 and 16.");
            break;

        case "-s" or "--block-size" when i + 1 < args.Length:
            if (ParseSize(args[++i]) is not long size || size is < MinBlockSize or > MaxBlockSize)
                return Usage("-s expects a size between 4k and 1g, e.g. 512k or 4MiB.");
            blockSize = (int)size;
            break;

        case "-f" or "--force":
            force = true;
            break;

        case "-h" or "--help" or "-?" or "/?":
            return Usage(null);

        default:
            if (args[i].StartsWith('-'))
                return Usage($"Unknown or incomplete argument: {args[i]}");
            if (url is not null)
                return Usage("Only one URL can be given.");
            url = args[i];
            break;
    }
}

if (url is null)
    return Usage(null);

if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    return Usage($"Not a valid http(s) URL: {url}");

// No Ctrl+C handling needed: .state only ever lists blocks that were flushed to disk,
// so killing the process at any time leaves a resumable state.
try
{
    var downloader = new Downloader(uri, output, workers, blockSize, force);
    return await downloader.RunAsync(CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    if (ex is HttpRequestException { InnerException: { } inner } && !ex.Message.Contains(inner.Message)) // e.g. certificate errors
        Console.Error.WriteLine($"       {inner.Message}");
    return 1;
}

// Number with optional unit; k/kb/kib, m/mb/mib, g/gb/gib always mean powers of two.
static long? ParseSize(string text)
{
    text = text.Trim().ToLowerInvariant();
    int digits = 0;
    while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        digits++;
    if (digits == 0 || !long.TryParse(text.AsSpan(0, digits), out long value))
        return null;

    int shift = text[digits..].Trim() switch
    {
        "" or "b" => 0,
        "k" or "kb" or "kib" => 10,
        "m" or "mb" or "mib" => 20,
        "g" or "gb" or "gib" => 30,
        _ => -1,
    };
    if (shift < 0 || value > long.MaxValue >> shift)
        return null;
    return value << shift;
}

static int Usage(string? error)
{
    if (error is not null)
        Console.Error.WriteLine(error);

    Console.Error.WriteLine("""
        Usage: nwget <url> [-o <path>] [-n <workers>] [-s <size>] [-f]

          -o <path>     Target file or directory
                        (default: file name from the server or the URL, current directory)
          -n <workers>  Number of parallel connections, 1–16 (default: 2)
          -s <size>     Block size, 4k–1g, e.g. 512k or 4MiB (default: 1m).
                        k/kb/kib etc. always mean powers of two (1k = 1024).
          -f            Discard a partial download that cannot be resumed and start over
                        (file changed on the server, .part/.state broken or incomplete)

        An interrupted download is resumed automatically on the next run, as long as
        <target>.part and <target>.state exist. If they no longer match the file on the
        server, nwget aborts instead of silently discarding them.
        """);
    return error is null ? 0 : 2;
}
