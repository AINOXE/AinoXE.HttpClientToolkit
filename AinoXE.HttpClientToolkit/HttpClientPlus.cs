using System.Net.Http.Headers;
using AinoXE.HttpClientToolkit;

namespace Ainosoft.Net.Http;

public class HttpClientPlus : HttpClient
{
    private readonly Queue<HttpClient> _freeClients = new();

    #region 构造方法

    public HttpClientPlus(int poolMaxReverseClientCount = 8, int poolMaxClientCount = 10,
        bool enableSyncPropertiesToPool = false)
    {
        PoolMaxReverseClientCount = poolMaxReverseClientCount;
        PoolMaxClientCount = poolMaxClientCount;
        EnableSyncPropertiesToPool = enableSyncPropertiesToPool;
    }

    public HttpClientPlus(HttpMessageHandler handler, int poolMaxReverseClientCount, int poolMaxClientCount,
        bool enableSyncPropertiesToPool) : base(handler)
    {
        PoolMaxReverseClientCount = poolMaxReverseClientCount;
        PoolMaxClientCount = poolMaxClientCount;
        EnableSyncPropertiesToPool = enableSyncPropertiesToPool;
        DefaultHttpMessageHandler = handler;
    }

    public HttpClientPlus(HttpMessageHandler handler, bool disposeHandler, int poolMaxReverseClientCount,
        int poolMaxClientCount, bool enableSyncPropertiesToPool) : base(handler, disposeHandler)
    {
        PoolMaxReverseClientCount = poolMaxReverseClientCount;
        PoolMaxClientCount = poolMaxClientCount;
        EnableSyncPropertiesToPool = enableSyncPropertiesToPool;
        DefaultHttpMessageHandler = handler;
    }

    #endregion


    #region 属性

    private HttpMessageHandler? DefaultHttpMessageHandler { get; set; }
    public bool EnableSyncPropertiesToPool { get; set; }

    public int PoolMaxReverseClientCount { get; set; }
    public int PoolMaxClientCount { get; set; }
    public int PoolCurrentClientCount { get; private set; }

    /// <summary>Gets or sets the base address of Uniform Resource Identifier (URI) of the Internet resource used when sending requests.</summary>
    /// <returns>The base address of Uniform Resource Identifier (URI) of the Internet resource used when sending requests.</returns>
    public new Uri? BaseAddress
    {
        get => base.BaseAddress;
        set
        {
            base.BaseAddress = value;
            if (!EnableSyncPropertiesToPool) return;
            lock (_freeClients)
            {
                foreach (var httpClient in _freeClients)
                {
                    if (httpClient.BaseAddress != value)
                        httpClient.BaseAddress = value;
                }
            }
        }
    }
    /// <summary>Gets or sets the maximum number of bytes to buffer when reading the response content.</summary>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The size specified is less than or equal to zero.</exception>
    /// <exception cref="T:System.InvalidOperationException">An operation has already been started on the current instance.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The current instance has been disposed.</exception>
    /// <returns>The maximum number of bytes to buffer when reading the response content. The default value for this property is 2 gigabytes.</returns>
    public new long MaxResponseContentBufferSize
    {
        get => base.MaxResponseContentBufferSize;
        set
        {
            base.MaxResponseContentBufferSize = value;
            if (!EnableSyncPropertiesToPool) return;
            lock (_freeClients)
            {
                foreach (var httpClient in _freeClients)
                {
                    if (httpClient.MaxResponseContentBufferSize != value)
                        httpClient.MaxResponseContentBufferSize = value;
                }
            }
        }
    }
    /// <summary>Gets or sets the default HTTP version used on subsequent requests made by this <see cref="T:System.Net.Http.HttpClient" /> instance.</summary>
    /// <exception cref="T:System.ArgumentNullException">In a set operation, <see langword="DefaultRequestVersion" /> is <see langword="null" />.</exception>
    /// <exception cref="T:System.InvalidOperationException">The <see cref="T:System.Net.Http.HttpClient" /> instance has already started one or more requests.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The <see cref="T:System.Net.Http.HttpClient" /> instance has already been disposed.</exception>
    /// <returns>The default version to use for any requests made with this <see cref="T:System.Net.Http.HttpClient" /> instance.</returns>
    public new Version DefaultRequestVersion
    {
        get => base.DefaultRequestVersion;
        set
        {
            base.DefaultRequestVersion = value;
            if (!EnableSyncPropertiesToPool) return;
            lock (_freeClients)
            {
                foreach (var httpClient in _freeClients)
                {
                    if (httpClient.DefaultRequestVersion != value)
                        httpClient.DefaultRequestVersion = value;
                }
            }
        }
    }
    /// <summary>Gets or sets the timespan to wait before the request times out.</summary>
    /// <exception cref="T:System.ArgumentOutOfRangeException">The timeout specified is less than or equal to zero and is not <see cref="F:System.Threading.Timeout.InfiniteTimeSpan" />.</exception>
    /// <exception cref="T:System.InvalidOperationException">An operation has already been started on the current instance.</exception>
    /// <exception cref="T:System.ObjectDisposedException">The current instance has been disposed.</exception>
    /// <returns>The timespan to wait before the request times out.</returns>
    public new TimeSpan Timeout
    {
        get => base.Timeout;
        set
        {
            base.Timeout = value;
            if (!EnableSyncPropertiesToPool) return;
            lock (_freeClients)
            {
                foreach (var httpClient in _freeClients)
                {
                    if (httpClient.Timeout != value)
                        httpClient.Timeout = value;
                }
            }
        }
    }
    /// <summary>Gets or sets the default version policy for implicitly created requests in convenience methods, for example, <see cref="M:System.Net.Http.HttpClient.GetAsync(System.String)" /> and <see cref="M:System.Net.Http.HttpClient.PostAsync(System.String,System.Net.Http.HttpContent)" />.</summary>
    /// <returns>The HttpVersionPolicy used when the HTTP connection is established.</returns>
    public new HttpVersionPolicy DefaultVersionPolicy
    {
        get => base.DefaultVersionPolicy;
        set
        {
            base.DefaultVersionPolicy = value;
            if (!EnableSyncPropertiesToPool) return;
            lock (_freeClients)
            {
                foreach (var httpClient in _freeClients)
                {
                    if (httpClient.DefaultVersionPolicy != value)
                        httpClient.DefaultVersionPolicy = value;
                }
            }
        }
    }

    #endregion


    #region Pool


    public HttpClient PoolRent()
    {
        lock (_freeClients)
        {
            if (_freeClients.Count != 0)
                return _freeClients.Dequeue();
            PoolCurrentClientCount++;
            if (PoolCurrentClientCount > PoolMaxClientCount)
                throw new OverflowException("HttpClientPool Not Has FreeClient!");
            var newClient = DefaultHttpMessageHandler is null
                ? new HttpClient() { BaseAddress = BaseAddress }
                : new HttpClient(DefaultHttpMessageHandler) { BaseAddress = BaseAddress };
            if (EnableSyncPropertiesToPool)
                SyncPropertiesTo(newClient);
            return newClient;
        }
    }

    public void PoolReturn(HttpClient client)
    {
        lock (_freeClients)
        {
            if (_freeClients.Count < PoolMaxReverseClientCount)
            {
                _freeClients.Enqueue(client);
                return;
            }
        }

        client.Dispose();
    }

    public int SyncPropertiesToPoolFreeClient()
    {
        lock (_freeClients)
        {
            foreach (var freeClient in _freeClients)
            {
                SyncPropertiesTo(freeClient);
            }

            return _freeClients.Count;
        }
    }

    public void SyncPropertiesTo(HttpClient httpClient)
    {
        httpClient.DefaultRequestHeaders.Clear();
        foreach (var kv in DefaultRequestHeaders)
        {
            httpClient.DefaultRequestHeaders.Add(kv.Key, kv.Value);
        }

        if (httpClient.BaseAddress != base.BaseAddress)
            httpClient.BaseAddress = base.BaseAddress;
        if (httpClient.Timeout != base.Timeout)
            httpClient.Timeout = base.Timeout;
        if (httpClient.DefaultRequestVersion != base.DefaultRequestVersion)
            httpClient.DefaultRequestVersion = base.DefaultRequestVersion;
        if (httpClient.DefaultVersionPolicy != base.DefaultVersionPolicy)
            httpClient.DefaultVersionPolicy = base.DefaultVersionPolicy;
        if (httpClient.MaxResponseContentBufferSize != base.MaxResponseContentBufferSize)
            httpClient.MaxResponseContentBufferSize = base.MaxResponseContentBufferSize;
    }
    #endregion


    #region 多任务、分块、断点恢复 下载

    public Task<ToFileTaskResult> GetToFileAsync(string url, string savePath, int taskCount,
    CancellationToken cancellationToken = default)
    => SendThenToFileAsync(null, new HttpDownloadTask(url, savePath, taskCount, taskCount), cancellationToken);

    public Task<ToFileTaskResult> GetToFileAsync(string url, string savePath, int taskCount, int blockCount,
        CancellationToken cancellationToken = default)
        => SendThenToFileAsync(null, new HttpDownloadTask(url, savePath, taskCount, blockCount), cancellationToken);

    public Task<ToFileTaskResult> GetToFileAsync(HttpDownloadTask downloadTask,
        CancellationToken cancellationToken = default)
        => SendThenToFileAsync(null, downloadTask, cancellationToken);

    public async Task<ToFileTaskResult> SendThenToFileAsync(
        HttpRequestMessage? requestMessage, HttpDownloadTask downloadTask,
        CancellationToken cancellationToken = default)
    {
        if (requestMessage != null)
            downloadTask.Url = requestMessage.RequestUri!.ToString();
        else
            requestMessage = new HttpRequestMessage(HttpMethod.Get, downloadTask.Url);
        var baseMethod = requestMessage.Method;
        requestMessage.Method = HttpMethod.Head;
        var resp = await SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (resp.IsSuccessStatusCode == false)
            return new ToFileTaskResult(downloadTask, resp);
        requestMessage.Method = baseMethod;
        downloadTask.OnStartDownloading();
        var contentLen = resp.Content.Headers.ContentLength;
        // 不支持Range 就算是恢复的任务任务也要重新下载
        if (resp.Headers.AcceptRanges.Count == 0 || contentLen.HasValue == false)
        {
            downloadTask.BlockCount = 1;
            downloadTask.TaskCount = 1;
            downloadTask.ContentLength = contentLen ?? -1;

            var reqMsg = new HttpRequestMessage
            {
                Content = requestMessage.Content,
                Method = requestMessage.Method,
                RequestUri = requestMessage.RequestUri,
                Version = requestMessage.Version,
                VersionPolicy = requestMessage.VersionPolicy
            };
            foreach (var kv in requestMessage.Headers)
                reqMsg.Headers.Add(kv.Key, kv.Value);
            try
            {
                await this.SendThenToFileAsync(reqMsg, downloadTask.TempSavePath, (progressInfo) =>
                {
                    downloadTask.Blocks[0].CompletedSize = progressInfo.CompletedSize;
                    return ValueTask.CompletedTask;
                }, cancellationToken);
                File.Move(downloadTask.TempSavePath, downloadTask.SavePath, true);
            }
            catch (Exception e)
            {
                downloadTask.OnBlockError(downloadTask.Blocks[0], e);
                downloadTask.OnDownloadError(0, e);
            }

            downloadTask.OnEndDownload();
            return new ToFileTaskResult(downloadTask, resp);
        }

        // 给downloadTask.ContentLength赋值会触发重新分配任务
        if (downloadTask.IsResumeTask == false || downloadTask.ContentLength != contentLen.Value)
            downloadTask.ContentLength = contentLen.Value;
        var clients = new HttpClient[downloadTask.TaskCount];
        var tasks = new Task[downloadTask.TaskCount];
        tasks[0] = CreateBlockDownloadTask(downloadTask, this, requestMessage, cancellationToken);
        for (var i = 1; i < clients.Length; i++)
        {
            tasks[i] = CreateBlockDownloadTask(downloadTask, clients[i], requestMessage, cancellationToken);
        }

        for (var i = 0; i < tasks.Length; i++)
        {
            try
            {
                await tasks[i];
            }
            catch (Exception e)
            {
                downloadTask.OnDownloadError(i, e);
            }
        }

        downloadTask.TaskFileStream ??= new FileStream(downloadTask.TempSavePath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite);
        downloadTask.TaskFileStream.SetLength(downloadTask.ContentLength);
        downloadTask.TaskFileStream.Close();
        File.Move(downloadTask.TempSavePath, downloadTask.SavePath, true);
        downloadTask.OnEndDownload();
        return new ToFileTaskResult(downloadTask, resp);
    }

    protected async Task CreateBlockDownloadTask(HttpDownloadTask downloadTask, HttpClient? client,
        HttpRequestMessage msgTemplate, CancellationToken cancellationToken)
    {
        client ??= PoolRent();
        try
        {
            while (downloadTask.GetUnStartedBlock() is { } block)
            {
                try
                {
                    var reqMsg = new HttpRequestMessage
                    {
                        Content = msgTemplate.Content,
                        Method = msgTemplate.Method,
                        RequestUri = msgTemplate.RequestUri,
                        Version = msgTemplate.Version,
                        VersionPolicy = msgTemplate.VersionPolicy
                    };
                    foreach (var kv in msgTemplate.Headers)
                        reqMsg.Headers.Add(kv.Key, kv.Value);

                    var blockLocal = block;

                    var start = block.CompletedSize > 0 ? block.Start + blockLocal.CompletedSize : block.Start;
                    reqMsg.Headers.Range = new RangeHeaderValue(start, block.End == -1 ? null : block.End);
                    downloadTask.OnBlockStartDownloading(blockLocal);
                    var baseCompletedSize = blockLocal.CompletedSize;
                    var resp = await client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    var progressInfo = await resp.Content.SaveToFileByContentRangeAsync(downloadTask.TempSavePath,
                        info =>
                        {
                            blockLocal.CompletedSize = baseCompletedSize + info.CompletedSize;
                            downloadTask.OnProgressUpdate(blockLocal, info.AppendSize);
                            return ValueTask.CompletedTask;
                        }, cancellationToken);
                    blockLocal.CompletedSize = baseCompletedSize + progressInfo.CompletedSize;
                    if (progressInfo.IsCompleted)
                        downloadTask.OnBlockCompleted(blockLocal);
                    else
                        downloadTask.OnBlockError(block, new NotImplementedException());
                }
                catch (Exception e)
                {
                    block.Error = e;
                    downloadTask.OnBlockError(block, e);
                }
            }
        }
        finally
        {
            if (client != this)
                PoolReturn(client);
        }
    }

    #endregion

}

public readonly struct ToFileTaskResult(HttpDownloadTask downloadTask, HttpResponseMessage responseMessage) : IDisposable
{
    public readonly HttpDownloadTask DownloadTask = downloadTask;
    public readonly HttpResponseMessage ResponseMessage = responseMessage;
    public readonly bool IsSuccessStatusCode => ResponseMessage.IsSuccessStatusCode;
    public readonly void Dispose() => ResponseMessage?.Dispose();
}