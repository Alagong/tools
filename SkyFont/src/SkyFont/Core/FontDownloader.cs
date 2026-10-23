using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace SkyFont.Core;

public enum DownloadStatus
{
    Downloaded,
    Skipped,
    Failed
}

public sealed class DownloadOutcome
{
    public required string Url { get; init; }
    public string? FilePath { get; set; }
    public DownloadStatus Status { get; set; }
    public string Message { get; set; } = "";
}

public sealed class DownloadReport
{
    public List<DownloadOutcome> Outcomes { get; } = new();
    public TimeSpan Elapsed { get; set; }
    public bool Cancelled { get; set; }

    public int Downloaded => Outcomes.Count(o => o.Status == DownloadStatus.Downloaded);
    public int Skipped => Outcomes.Count(o => o.Status == DownloadStatus.Skipped);
    public int Failed => Outcomes.Count(o => o.Status == DownloadStatus.Failed);
}

/// <summary>
/// 远程字体下载器。
///
/// 几条硬性约束，都是为了避免"把垃圾文件当字体存进字体库"：
///   · 只接受 http / https，拒绝 file:// 之类的本地协议；
///   · 单个文件上限 128 MB；
///   · 下载先落 .part，校验文件头（sfnt 魔数）与可选 SHA-256 通过后才改名入位；
///   · 扩展名以真实文件头为准，不信任 URL 后缀。
/// </summary>
public static class FontDownloader
{
    private const int MaxParallel = 4;
    private const int MaxAttempts = 3;
    private const long MaxBytes = 128L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        // .NET 在 Windows 上只读 IE/WinINET 的代理设置，不认 HTTP_PROXY 环境变量；
        // 而不少开发机 / CI 恰恰只配了环境变量，这里补上。
        if (ResolveEnvironmentProxy() is { } proxy)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SkyFont/1.0 (+font manifest import)");
        return client;
    }

    private static IWebProxy? ResolveEnvironmentProxy()
    {
        foreach (string name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;

            // 常见的 user:pass@host:port 写法本身就是合法 URI
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;

            return new WebProxy(uri) { BypassProxyOnLocal = true };
        }

        return null;
    }

    public static async Task<DownloadReport> DownloadAsync(
        IReadOnlyList<ManifestItem> items,
        string targetDirectory,
        IProgress<(int Done, int Total, string Detail)>? progress = null,
        CancellationToken ct = default)
    {
        var report = new DownloadReport();
        var started = DateTime.UtcNow;

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"无法创建下载目录 {targetDirectory}：{ex.Message}。程序目录可能需要写权限。", ex);
        }

        int total = items.Count;
        int done = 0;
        var slots = new SemaphoreSlim(MaxParallel, MaxParallel);
        var outcomes = new DownloadOutcome?[total];

        var tasks = items.Select(async (item, index) =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DownloadOutcome outcome;
                try
                {
                    outcome = await DownloadOneAsync(item, targetDirectory, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcome = new DownloadOutcome
                    {
                        Url = item.Url,
                        Status = DownloadStatus.Failed,
                        Message = "已取消"
                    };
                }
                catch (Exception ex)
                {
                    outcome = new DownloadOutcome
                    {
                        Url = item.Url,
                        Status = DownloadStatus.Failed,
                        Message = ex.Message
                    };
                }

                outcomes[index] = outcome;
                int current = Interlocked.Increment(ref done);
                progress?.Report((current, total, Describe(outcome)));
            }
            finally
            {
                slots.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
        }

        foreach (var outcome in outcomes)
        {
            if (outcome is not null) report.Outcomes.Add(outcome);
        }

        if (report.Outcomes.Any(o => o.Message == "已取消")) report.Cancelled = true;

        report.Elapsed = DateTime.UtcNow - started;
        return report;
    }

    private static string Describe(DownloadOutcome outcome) => outcome.Status switch
    {
        DownloadStatus.Downloaded => "下载完成：" + Path.GetFileName(outcome.FilePath ?? outcome.Url),
        DownloadStatus.Skipped => "已存在，跳过：" + Path.GetFileName(outcome.FilePath ?? outcome.Url),
        _ => "失败：" + Path.GetFileName(outcome.FilePath ?? outcome.Url) + "（" + outcome.Message + "）"
    };

    private static async Task<DownloadOutcome> DownloadOneAsync(
        ManifestItem item, string directory, CancellationToken ct)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
            return Failed(item, "地址无法解析");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Failed(item, "仅支持 http / https 地址");

        string proposed = ResolveFileName(item, uri);
        string existing = Path.Combine(directory, proposed);

        // ---- 已存在：无校验和时直接跳过，有校验和则比对 ----
        if (File.Exists(existing) && new FileInfo(existing).Length > 0)
        {
            if (string.IsNullOrWhiteSpace(item.Sha256) && !item.Overwrite)
            {
                return new DownloadOutcome
                {
                    Url = item.Url,
                    FilePath = existing,
                    Status = DownloadStatus.Skipped,
                    Message = "文件已存在"
                };
            }

            if (!item.Overwrite && !string.IsNullOrWhiteSpace(item.Sha256) &&
                string.Equals(await Sha256Async(existing, ct).ConfigureAwait(false), item.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new DownloadOutcome
                {
                    Url = item.Url,
                    FilePath = existing,
                    Status = DownloadStatus.Skipped,
                    Message = "文件已存在且校验通过"
                };
            }
        }

        return await DownloadWithRetryAsync(item, uri, directory, proposed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 带退避的重试下载。
    ///
    /// 代理、CDN 边缘节点偶发连接重置或超时是常态，一次抖动就判"下载失败"体验太差；
    /// 但 4xx 与内容不合法（非字体 / WOFF / 超限）重试也不会变好，直接失败。
    /// </summary>
    private static async Task<DownloadOutcome> DownloadWithRetryAsync(
        ManifestItem item, Uri uri, string directory, string proposed, CancellationToken ct)
    {
        string temp = Path.Combine(directory, proposed + ".part");
        Exception? last = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await DownloadAttemptAsync(item, uri, directory, proposed, temp, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                TryDelete(temp);

                if (attempt >= MaxAttempts || !IsRetryable(ex)) break;

                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }

        string message = last switch
        {
            TaskCanceledException => "连接或读取超时",
            null => "下载失败",
            _ => last.Message
        };

        return Failed(item, message);
    }

    private static async Task<DownloadOutcome> DownloadAttemptAsync(
        ManifestItem item, Uri uri, string directory, string proposed, string temp, CancellationToken ct)
    {
        long received = 0;

        // 建连 / TLS 握手单独限时：代理半死不活时握手会一直挂着，
        // 若交给 HttpClient 的整体超时（5 分钟）兜底，一个卡死的地址会把整批导入拖住。
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
                                        .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            if (IsRetryableStatus(response.StatusCode))
                throw new DownloadException(message, retryable: true);

            return Failed(item, message);
        }

        long? declared = response.Content.Headers.ContentLength;
        if (declared > MaxBytes)
            return Failed(item, $"文件过大（{declared / 1024 / 1024} MB，上限 {MaxBytes / 1024 / 1024} MB）");

        // 独立作用域：必须在读文件头之前关闭写入流
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var destination = File.Create(temp);

            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > MaxBytes)
                    throw new DownloadException($"文件超过 {MaxBytes / 1024 / 1024} MB 上限");

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        if (received == 0)
            throw new DownloadException("服务器返回了空文件");

        // ---- 校验文件头，扩展名以真实内容为准 ----
        string? sniffed = SniffExtension(temp);
        if (sniffed is null)
            throw new DownloadException("不是有效的字体文件（文件头无法识别）");
        if (sniffed is ".woff" or ".woff2")
            throw new DownloadException("不支持 WOFF / WOFF2 网页字体，请提供 TTF / OTF / TTC");

        if (!string.IsNullOrWhiteSpace(item.Sha256))
        {
            string actual = await Sha256Async(temp, ct).ConfigureAwait(false);
            if (!string.Equals(actual, item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DownloadException("SHA-256 校验不通过");
        }

        string finalName = Path.GetFileName(Path.ChangeExtension(proposed, sniffed));
        string finalPath = Path.Combine(directory, finalName);

        // 目标同名文件已存在时先比内容：同一份字体重跑清单不该堆出 (2)(3) 副本
        if (File.Exists(finalPath) && !item.Overwrite)
        {
            if (await SameContentAsync(finalPath, temp, ct).ConfigureAwait(false))
            {
                TryDelete(temp);
                return new DownloadOutcome
                {
                    Url = item.Url,
                    FilePath = finalPath,
                    Status = DownloadStatus.Skipped,
                    Message = "与已有文件内容相同"
                };
            }

            finalPath = UniquePath(finalPath, overwrite: false);
        }

        if (item.Overwrite && File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(temp, finalPath, overwrite: true);

        return new DownloadOutcome
        {
            Url = item.Url,
            FilePath = finalPath,
            Status = DownloadStatus.Downloaded,
            Message = $"{received / 1024.0:F0} KB"
        };
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static bool IsRetryable(Exception ex) => ex switch
    {
        DownloadException { Retryable: true } => true,
        HttpRequestException => true,   // DNS / 连接 / TLS 握手失败
        IOException => true,            // 传输中断、连接被重置
        TaskCanceledException => true,  // 超时（用户主动取消已在调用方单独处理）
        _ => false
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件清不掉不影响结果。
        }
    }

    /// <summary>下载阶段的错误。Retryable=false 表示"内容不对，重试也没用"。</summary>
    private sealed class DownloadException : Exception
    {
        public DownloadException(string message, bool retryable = false, Exception? inner = null)
            : base(message, inner) => Retryable = retryable;

        public bool Retryable { get; }
    }

    private static DownloadOutcome Failed(ManifestItem item, string message) => new()
    {
        Url = item.Url,
        Status = DownloadStatus.Failed,
        Message = message
    };

    /// <summary>
    /// 决定落盘文件名：显式 file 字段 &gt; URL 中的文件名 &gt; name 字段 &gt; font-序号。
    /// </summary>
    private static string ResolveFileName(ManifestItem item, Uri uri)
    {
        string? candidate = item.FileName;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            string fromUrl = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(fromUrl)) candidate = fromUrl;
        }

        if (string.IsNullOrWhiteSpace(candidate)) candidate = item.Name;
        if (string.IsNullOrWhiteSpace(candidate)) candidate = $"font-{item.Index:D3}";

        candidate = SanitizeFileName(candidate);

        // 没有可用扩展名时先按 .ttf 命名，入位前会依据文件头改正
        if (string.IsNullOrEmpty(Path.GetExtension(candidate))) candidate += ".ttf";

        return candidate;
    }

    /// <summary>文件名消毒：去路径、去非法字符、限长，杜绝 ../ 之类的路径穿越。</summary>
    private static string SanitizeFileName(string raw)
    {
        string name = Path.GetFileName(raw.Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) name = "font";

        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        name = name.Trim().Trim('.', ' ');
        if (name.Length == 0) name = "font";
        if (name.Length > 96)
        {
            string ext = Path.GetExtension(name);
            string stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length > 96 - ext.Length) stem = stem[..(96 - ext.Length)];
            name = stem + ext;
        }

        return name;
    }

    /// <summary>同名文件已存在且不允许覆盖时，追加 (2)(3)… 避免互相覆盖。</summary>
    private static string UniquePath(string path, bool overwrite)
    {
        if (overwrite || !File.Exists(path)) return path;

        string directory = Path.GetDirectoryName(path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>按 sfnt 魔数判断真实格式，同时挡掉 HTML 错误页、压缩包等伪字体。</summary>
    private static string? SniffExtension(string path)
    {
        Span<byte> head = stackalloc byte[4];

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Read(head) < 4) return null;
        }
        catch
        {
            return null;
        }

        byte b0 = head[0], b1 = head[1], b2 = head[2], b3 = head[3];

        if (b0 == 0x00 && b1 == 0x01 && b2 == 0x00 && b3 == 0x00) return ".ttf";
        if (b0 == 0x74 && b1 == 0x72 && b2 == 0x75 && b3 == 0x65) return ".ttf"; // 'true'
        if (b0 == 0x74 && b1 == 0x79 && b2 == 0x70 && b3 == 0x31) return ".ttf"; // 'typ1'
        if (b0 == 0x4F && b1 == 0x54 && b2 == 0x54 && b3 == 0x4F) return ".otf"; // 'OTTO'
        if (b0 == 0x74 && b1 == 0x74 && b2 == 0x63 && b3 == 0x66) return ".ttc"; // 'ttcf'
        if (b0 == 0x77 && b1 == 0x4F && b2 == 0x46 && b3 == 0x46) return ".woff";
        if (b0 == 0x77 && b1 == 0x4F && b2 == 0x46 && b3 == 0x32) return ".woff2";

        return null;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>比较两个文件内容是否一致（先比长度，再比 SHA-256）。</summary>
    private static async Task<bool> SameContentAsync(string left, string right, CancellationToken ct)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;

        return string.Equals(
            await Sha256Async(left, ct).ConfigureAwait(false),
            await Sha256Async(right, ct).ConfigureAwait(false),
            StringComparison.OrdinalIgnoreCase);
    }
}
