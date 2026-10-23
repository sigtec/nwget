# nwget

Robust HTTP downloader for unstable connections (poor DSL, Wi-Fi), written in .NET 10.

Downloads a file in blocks over several parallel connections, retries failed blocks
automatically and resumes an interrupted download on the next invocation.

## Usage

```
nwget <url> [-o <path>] [-n <workers>] [-s <size>] [-f]
```

| Option | Meaning |
|---|---|
| `-o <path>` | Target file or target directory, see [Target path](#target-path). Default: current directory |
| `-n <workers>` | Number of parallel connections, 1–16. Default: 2 |
| `-s <size>` | Block size, 4k–1g. Default: 1m. Units `k`/`kb`/`kib`, `m`/`mb`/`mib`, `g`/`gb`/`gib` (case-insensitive) are always powers of two: `1k` = `1KB` = `1KiB` = 1024 bytes. Without a unit the value is in bytes |
| `-f` | Discard a partial download that cannot be resumed and start over (see [Resuming](#resuming)) |

Examples:

```
nwget https://example.com/files/image.iso
nwget https://example.com/download?id=42 -o D:\Downloads\
nwget https://example.com/files/image.iso -o image.iso -n 4 -s 4MiB
```

Cancel with **Ctrl+C** (or kill the process in any other way). Running the same command again
later resumes the download.

Larger blocks mean fewer requests and fsyncs, smaller blocks mean less data to re-download when
a connection drops in the middle of a block.

### Target path

- `-o` is an existing directory, or ends with a path separator (`D:\Downloads\`, `out/`): the
  file is created in that directory (created if necessary) under the remote file name.
- Otherwise `-o` is the target file name; it replaces the remote file name. Missing parent
  directories are created.
- Without `-o`, the remote file name is used in the current directory.

The remote file name is taken from the `Content-Disposition` header, otherwise from the last
segment of the URL (after redirects). If neither provides one (e.g. `https://example.com/`) and
`-o` names no file, nwget aborts and asks for `-o`.

## Building and installing

Requires the .NET 10 SDK. nwget builds into a single executable that can be started directly
(`nwget.exe` on Windows, `nwget` on Linux and macOS). There are three ways to publish it:

| Variant | Command (`<rid>` see below) | Needs .NET at runtime | Notes |
|---|---|---|---|
| Native AOT | `dotnet publish -c Release -r <rid> -p:PublishAot=true` | no | Smallest, fastest start. Must be built on the target OS, needs a native toolchain (see below) |
| Self-contained single file | `dotnet publish -c Release -r <rid> -p:PublishSingleFile=true --self-contained -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true` | no | ~13 MB (~70 MB without trimming and compression). Can be built on any OS for any target |
| Framework-dependent | `dotnet publish -c Release -r <rid> --self-contained false` | yes, .NET 10 runtime | Small. Can be built on any OS for any target |

Native AOT is recommended where possible, otherwise the self-contained single file.
The result is in `bin/Release/net10.0/<rid>/publish/`. nwget uses invariant globalization, so
no ICU libraries are needed, even on minimal Linux distributions.

| OS | `<rid>` | Toolchain for Native AOT |
|---|---|---|
| Windows | `win-x64`, `win-arm64` | Visual Studio or Build Tools with the "Desktop development with C++" workload |
| Linux | `linux-x64`, `linux-arm64` (e.g. Raspberry Pi 64-bit) | `clang` and `zlib1g-dev` (Debian/Ubuntu) or `zlib-devel` (Fedora) |
| macOS | `osx-arm64` (Apple Silicon), `osx-x64` (Intel) | Xcode command line tools: `xcode-select --install` |

### Windows

Copy `nwget.exe` to a directory of your choice and add it to the user `PATH` (PowerShell):

```powershell
$dir = "$env:LOCALAPPDATA\Programs\nwget"
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item bin\Release\net10.0\win-x64\publish\nwget.exe $dir
[Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "User") + ";$dir", "User")
```

Open a new terminal afterwards so the changed `PATH` takes effect.

### Linux

```sh
install -m 755 bin/Release/net10.0/linux-x64/publish/nwget ~/.local/bin/       # current user
sudo install -m 755 bin/Release/net10.0/linux-x64/publish/nwget /usr/local/bin/ # all users
```

`~/.local/bin` is in the `PATH` on most distributions. `install -m 755` also sets the executable
bit, which is missing if the file was built on Windows and copied over.

### macOS

```sh
sudo install -m 755 bin/Release/net10.0/osx-arm64/publish/nwget /usr/local/bin/
```

When built on the Mac itself, the binary is signed ad hoc automatically and runs right away. If it
was built on another OS or downloaded, macOS refuses to start it; then sign it ad hoc and remove
the quarantine flag:

```sh
codesign --force --sign - /usr/local/bin/nwget
xattr -d com.apple.quarantine /usr/local/bin/nwget   # only if downloaded
```

## How it works

1. **Probe:** a `GET` with `Range: bytes=0-0` determines size, range support, ETag,
   Last-Modified and the file name.
2. **Blocks:** the file is split into blocks (1 MiB by default, see `-s`). A queue hands them
   out in ascending order to n workers. Each worker streams its block directly to the block's
   offset in `<target>.part` using `RandomAccess.WriteAsync`, so memory use does not depend on
   the block size. A partially written block is harmless: it is not marked as done and is
   simply overwritten by the next attempt.
3. **Commit per block:** once the whole block is written, `.part` is flushed (fsync), then the block's bit is set
   in `<target>.state`, exactly that one byte is written and `.state` is flushed. `.state`
   therefore never lists a block that is not actually on disk – not even after Ctrl+C, a hard
   kill or a crash.
4. **Errors:** if a connection drops or no data arrives for 30 s, only that block is retried
   (exponential backoff up to 30 s, unlimited retries). Permanent errors abort the download
   immediately instead:
   - HTTP 4xx except 408/429, as well as 501 and 505
   - TLS / certificate errors, too many redirects
   - write errors on the local disk (e.g. disk full)
   - unknown host name, but only on the very first request – later in the download this
     usually just means the network is down, so it is retried
5. **Resuming:** see below.
6. **Completion:** `.part` is renamed to the target name, `.state` is deleted. The final line
   shows the duration and the average speed of this run.

An existing target file is never overwritten.

### Resuming

If `<target>.part` and `<target>.state` exist, the length of `.part` matches the size on the
server and ETag or Last-Modified are unchanged, all blocks marked as done are kept and the
download continues. A resumed download keeps the block size it was started with; `-s` only
applies to new downloads.

Every range request carries `If-Range`, so a change of the file on the server is also detected
during the download.

nwget **aborts** (exit code 1) and leaves existing files untouched if

- the file on the server has changed (size, ETag or Last-Modified), before or during the download,
- `.state` is unreadable or does not match `.part`,
- only one of `.part` and `.state` exists (e.g. a foreign `.part` file from a browser), or
- leftovers exist but the server no longer allows resuming.

With `-f` the leftovers are discarded instead and the download starts over. If the file changes
on the server again during that new attempt, nwget gives up.

### Servers without range support or without a size

Then only a simple sequential download is possible. On errors it starts over from the
beginning, and resuming after cancellation is not possible: the `.part` file is left behind, and
the next run aborts until it is called with `-f`. Without `Content-Length`,
completeness can only be verified with `Transfer-Encoding: chunked`; if the server signals the
end only by closing the connection, an interruption cannot be told apart from a regular end.

## `.state` file format

Binary, little endian:

| Offset | Size | Content |
|---|---|---|
| 0 | 4 | Magic `NWGS` |
| 4 | 1 | Format version (1) |
| 5 | 4 | Block size in bytes (Int32) |
| 9 | 8 | Last-Modified as UTC ticks (Int64, 0 = not present) |
| 17 | 2 | Length of the ETag in bytes (UInt16, 0 = not present) |
| 19 | n | ETag, UTF-8 |
| 19+n | m | Bitmap, 1 bit per block: block i = bit (i % 8) of byte (i / 8), 1 = done |

The file size is not stored in `.state` – it is the length of the `.part` file.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Error (including a partial download that cannot be resumed without `-f`) |
| 2 | Invalid arguments |
