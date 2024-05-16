using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AinoXE.HttpClientToolkit;

public static class HttpClientExtension
{
    public static int DefaultToFileMaxBufferSize { get; set; } = 1024 * 1024;
    public static ArrayPool<byte> DefaultToFileBufferPool { get; set; } = ArrayPool<byte>.Shared;


    #region 预热

    public static Task<HttpResponseMessage> PreheatAsync(this HttpClient self, string requestUri, CancellationToken cancellationToken = default)
        => self.SendAsync(new HttpRequestMessage(HttpMethod.Head, requestUri), cancellationToken);
    public static Task<HttpResponseMessage> PreheatAsync(this HttpClient self, CancellationToken cancellationToken = default)
        => self.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"), cancellationToken);

    #endregion


    #region 登录认证
    public static string? GetBearerToken(this HttpHeaders self)
    {
        if (self.TryGetValues("Authorization", out var values) == false)
            return null;
        var authStr = values.FirstOrDefault();
        if (authStr == null) return null;
        var idx = authStr.IndexOf("Bearer ", StringComparison.Ordinal);
        return idx == -1 ? null : authStr[(idx + 7)..];
    }

    public static string? GetAuthorizationHeader(this HttpHeaders self)
    {
        return self.TryGetValues("Authorization", out var values) == false
            ? null : values.FirstOrDefault();
    }

    public static HttpClient BasicAuthentication(this HttpClient self, string username, string password)
    {
        var basicAuthData = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        self.DefaultRequestHeaders.Add("Authorization", basicAuthData);
        return self;
    }

    public static HttpClient BearerAuthentication(this HttpClient self, string token)
    {
        self.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
        return self;
    }
    #endregion


    #region Api调用

    public static Task<ApiResult<TResp>> RequestApiAsync<TResp>(this HttpClient self, HttpMethod method,
        string url, object? json, CancellationToken cancellationToken = default)
    {
        var requestMessage = new HttpRequestMessage(method, url)
        {
            Content = json is null ? null : JsonContent.Create(json)
        };
        return self.RequestApiAsync<TResp>(requestMessage, default, cancellationToken);
    }


    public static Task<ApiResult<TResp>> RequestApiAsync<TResp>(this HttpClient self, HttpMethod method,
        string url, object json, JsonSerializerOptions? serializerOptions, CancellationToken cancellationToken = default)
    {
        var requestMessage = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(json, options: serializerOptions)
        };
        return self.RequestApiAsync<TResp>(requestMessage, serializerOptions, cancellationToken);
    }

    public static async Task<ApiResult<T>> RequestApiAsync<T>(this HttpClient self, HttpRequestMessage requestMessage, JsonSerializerOptions? serializerOptions, CancellationToken cancellationToken = default)
    {
        var resp = await self.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (resp.IsSuccessStatusCode == false)
            return new ApiResult<T>(resp, default, serializerOptions);
        var obj = await resp.Content.ReadFromJsonAsync<T>(serializerOptions, cancellationToken);
        return new ApiResult<T>(resp, obj, serializerOptions);
    }

    #endregion


    #region 文件下载

    public static async Task<ToFileResult> GetToFileAsync(
        this HttpClient self,
        string requestUri, string savePath,
        ToFileProgressNotifier? progressNotifier,
        CancellationToken cancellationToken = default)
    {
        var resp = await self.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (resp.IsSuccessStatusCode == false)
            return new ToFileResult(ToFileProgressInfo.Empty, resp);
        return new ToFileResult(
            await resp.Content.SaveToFileOffsetAsync(savePath, -1, -1, progressNotifier, cancellationToken),
            resp);
    }

    public static Task<ToFileResult> GetToFileAsync(
        this HttpClient self,
        string requestUri, string savePath,
        CancellationToken cancellationToken = default)
        => GetToFileAsync(self, requestUri, savePath, null, cancellationToken);

    public static async Task<ToFileResult> SendThenToFileAsync(
        this HttpClient self,
        HttpRequestMessage requestMessage,
        string savePath,
        ToFileProgressNotifier? progressNotifier, CancellationToken cancellationToken = default)
    {
        var resp = await self.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (resp.IsSuccessStatusCode == false)
            return new ToFileResult(ToFileProgressInfo.Empty, resp);
        return new ToFileResult(
            await resp.Content.SaveToFileAsync(savePath, progressNotifier, cancellationToken), resp);
    }

    public static Task<ToFileResult> SendThenToFileAsync(
        this HttpClient self,
        HttpRequestMessage requestMessage,
        string savePath,
        CancellationToken cancellationToken = default)
        => SendThenToFileAsync(self, requestMessage, savePath, null, cancellationToken);


    public static HttpDownloadTask CreateDownloadTask(this HttpRequestMessage requestMessage, string savePath, int taskCount,
        int blockCount)
        => new(requestMessage.RequestUri!.ToString(), savePath, taskCount, blockCount);
    public static HttpDownloadTask CreateDownloadTask(this HttpRequestMessage requestMessage, string savePath, int taskCount)
        => new(requestMessage.RequestUri!.ToString(), savePath, taskCount, taskCount);

    #endregion HttpClient文件下载
}



public static class HttpContentExtension
{
    public static ArrayPool<byte> DefaultToFileBufferPool
    {
        get => HttpClientExtension.DefaultToFileBufferPool;
        set => HttpClientExtension.DefaultToFileBufferPool = value;
    }
    public static int DefaultToFileMaxBufferSize
    {
        get => HttpClientExtension.DefaultToFileMaxBufferSize;
        set => HttpClientExtension.DefaultToFileMaxBufferSize = value;
    }

    #region 文件下载

    public static Task<ToFileProgressInfo> SaveToFileAsync(this HttpContent self, string savePath,
        ToFileProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default)
        => SaveToFileOffsetAsync(self, savePath, -1, -1, progressNotifier, cancellationToken);

    public static Task<ToFileProgressInfo> SaveToFileAsync(this HttpContent self, string savePath,
        CancellationToken cancellationToken)
        => SaveToFileOffsetAsync(self, savePath, -1, -1, null, cancellationToken);

    public static Task<ToFileProgressInfo> SaveToFileByContentRangeAsync(this HttpContent self,
        string savePath, CancellationToken cancellationToken = default)
        => SaveToFileByContentRangeAsync(self, savePath, null, cancellationToken);

    public static Task<ToFileProgressInfo> SaveToFileByContentRangeAsync(this HttpContent self,
        string savePath, ToFileProgressNotifier? progressNotifier, CancellationToken cancellationToken = default)
    {
        var contentRange = self.Headers.ContentRange ?? throw new ArgumentException("Not Found Content-Range Header!");
        var start = contentRange.From!.Value;
        var length = (contentRange.To - start) + 1 ?? -1;
        if (contentRange.Unit != "bytes")
            throw new ArgumentException($"Not Support Content Range Unit! {contentRange.Unit}");
        return SaveToFileOffsetAsync(self, savePath, start, length, progressNotifier, cancellationToken);
    }
    public static Task<ToFileProgressInfo> SaveToFileOffsetAsync(this HttpContent self, string savePath,
        long start, long length,
        CancellationToken cancellationToken = default)
        => SaveToFileOffsetAsync(self, savePath, start, length, null, cancellationToken);

    public static async Task<ToFileProgressInfo> SaveToFileOffsetAsync(this HttpContent self,
        string savePath, long start, long length, ToFileProgressNotifier? progressNotifier,
        CancellationToken cancellationToken = default)
    {
        var contentLen = self.Headers.ContentLength;
        var rentLen = (int)(contentLen is null
            ? DefaultToFileMaxBufferSize
            : contentLen.Value > DefaultToFileMaxBufferSize
                ? DefaultToFileMaxBufferSize
                : contentLen.Value);

        length = length == -1 ? contentLen ?? length : length;

        if (start == -1 && File.Exists(savePath))
            File.Delete(savePath);

        await using var fs = new FileStream(savePath,
            mode: start == -1 ? FileMode.Create : FileMode.OpenOrCreate,
            access: FileAccess.ReadWrite,
            share: start == -1 ? FileShare.Read : FileShare.ReadWrite);

        if (start != -1)
            fs.Seek(start, SeekOrigin.Begin);
        var buf = DefaultToFileBufferPool.Rent(rentLen);
        try
        {
            await using var stream = await self.ReadAsStreamAsync(cancellationToken);
            int readLen;
            long completedSize = 0;
            while ((readLen = await stream.ReadAsync(buf, cancellationToken)) != 0)
            {
                completedSize += readLen;
                // 范围溢出检查
                if (length != -1 && completedSize > length)
                {
                    readLen -= (int)(completedSize - length);
                    completedSize = length;
                    if (readLen <= 0) break;
                }
                await fs.WriteAsync(buf.AsMemory(0, readLen), cancellationToken);
#pragma warning disable CA2012
                progressNotifier?.Invoke(new ToFileProgressInfo(length, completedSize, readLen));
            }
            var result = new ToFileProgressInfo(length == -1 ? completedSize : length, completedSize, 0);
            progressNotifier?.Invoke(result);
#pragma warning restore CA2012
            return result;
        }
        finally
        {
            DefaultToFileBufferPool.Return(buf);
        }
    }

    #endregion HttpContent文件下载
}

public delegate ValueTask ToFileProgressNotifier(ToFileProgressInfo progressInfo);

public struct ToFileResult(ToFileProgressInfo progressInfo, HttpResponseMessage responseMessage) : IDisposable
{
    public ToFileProgressInfo ProgressInfo = progressInfo;
    public HttpResponseMessage ResponseMessage = responseMessage;
    public readonly bool IsSuccessStatusCode => ResponseMessage.IsSuccessStatusCode;
    public readonly bool IsToFileCompleted => ProgressInfo.IsCompleted;
    public readonly void Dispose() => ResponseMessage?.Dispose();
}
public readonly struct ToFileProgressInfo(long contentSize, long completedSize, int appendSize)
{
    public readonly long ContentSize = contentSize;
    public readonly long CompletedSize = completedSize;
    public readonly int AppendSize = appendSize;

    public float Progress => (float)((CompletedSize * 1.0 / ContentSize) * 100);
    public bool IsCompleted => ContentSize == CompletedSize;
    public double CompletedSizeOfKB => CompletedSize / 1024.0;
    public double CompletedSizeOfMB => CompletedSize / 1048576.0;
    public double CompletedSizeOfGB => CompletedSize / 1073741824.0;

    public double ContentSizeOfKB => ContentSize / 1024.0;
    public double ContentSizeOfMB => ContentSize / 1048576.0;
    public double ContentSizeOfGB => ContentSize / 1073741824.0;

    public static readonly ToFileProgressInfo Empty = new(0, 0, 0);
}
public struct ApiResult<T>(HttpResponseMessage resp, T? value, JsonSerializerOptions? serializerOptions) : IDisposable
{

    public HttpResponseMessage ResponseMessage = resp;
    public T? Value = value;
    public JsonSerializerOptions? SerializerOptions = serializerOptions;
    public readonly bool IsSuccess => ResponseMessage.IsSuccessStatusCode;

    public async ValueTask<T?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        if (Value != null)
            return Value;
        await using var stream = await ResponseMessage.Content.ReadAsStreamAsync(cancellationToken);
        Value = await JsonSerializer.DeserializeAsync<T?>(stream, SerializerOptions, cancellationToken);
        return Value;
    }

    public async ValueTask<TR?> GetValueAsync<TR>(CancellationToken cancellationToken = default)
    {
        await using var stream = await ResponseMessage.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<TR?>(stream, SerializerOptions, cancellationToken);
    }


    public readonly void Dispose()
    {
        ResponseMessage?.Dispose();
    }
}
