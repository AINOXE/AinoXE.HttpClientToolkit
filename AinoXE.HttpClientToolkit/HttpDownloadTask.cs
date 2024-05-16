using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ReSharper disable PassStringInterpolation

namespace AinoXE.HttpClientToolkit;

public delegate void DownloadEvent(HttpDownloadTask task);
public delegate void DownloadErrorEvent(HttpDownloadTask task, int taskId, Exception exception);
public delegate void DownloadProgressUpdateEvent(HttpDownloadTask task, HttpResBlock? sender);
public delegate void DownloadBlockEvent(HttpDownloadTask task, HttpResBlock block);
public delegate void DownloadBlockErrorEvent(HttpDownloadTask task, HttpResBlock block, Exception exception);

public class HttpDownloadTask(string url, string savePath, int taskCount, int blockCount)
{
    [JsonConstructor]
    private HttpDownloadTask() : this(default(string)!, default!, default, default)
    {
        IsResumeTask = true;
    }

    public static byte[] DefaultDownloadConfigTagBytes { get; set; } = [0x02, 0x13, 0x20, 0x03];
    public static int DefaultDownloadConfigAppendLength { get; set; } = DefaultDownloadConfigTagBytes.Length * 2 + 8;
    public static int DefaultDownloadConfigAppendHalfLength { get; set; } = DefaultDownloadConfigTagBytes.Length + 4;
    public static string DefaultDownloadTempConfigFileSuffix { get; set; } = ".d-task-cfg";
    public static string DefaultDownloadTempFileSuffix { get; set; } = ".d-task";
    public static string DefaultDownloadTempFileNameSizeStartTag { get; set; } = ".^v^.";
    public static string DefaultDownloadTempFileNameSizeEndTag { get; set; } = ".";

    public HttpDownloadTask(HttpRequestMessage url, string savePath, int taskCount, int blockCount)
        : this(url.RequestUri!.ToString(), savePath, taskCount, blockCount) { }

    public event DownloadEvent? StartDownloading;
    public event DownloadEvent? EndDownload;
    public event DownloadErrorEvent? DownloadError;
    public event DownloadProgressUpdateEvent? ProgressUpdate;
    public event DownloadBlockEvent? BlockStartDownloading;
    public event DownloadBlockEvent? BlockCompleted;
    public event DownloadBlockErrorEvent? BlockError;

    private long _contentLength;
    private long _lastProgressUpdateNotifyTimeDownloadedSize;
    public static int SecondConfigInterval = 4096;
    private SemaphoreSlim _stateWriteLock = new(1);
    private readonly object _progressUpdateLock = new object();


    public string Url { get; set; } = url;
    public string SavePath { get; set; } = savePath;
    public string TempSavePath { get; set; } = null!;
    public int TaskCount { get; set; } = taskCount;
    public int BlockCount { get; set; } = blockCount;
    public long ContentLength
    {
        get => _contentLength;
        set
        {
            _contentLength = value;
            TempSavePath = $"{SavePath}.{TaskCount}.{BlockCount}{DefaultDownloadTempFileNameSizeStartTag}{value}{DefaultDownloadTempFileSuffix}";
            CalculateBlocks();
        }
    }
    public long DownloadedSize { get; set; }
    public HttpResBlock[] Blocks { get; set; } = [];
    public TimeSpan ProgressUpdateNotifyInterval { get; set; }
    public string? ErrorMessage => Error?.Message;
    public DateTime LastProgressUpdateNotifyTime { get; set; }

    [JsonIgnore]
    public Exception? Error { get; set; }

    internal FileStream? TaskFileStream;
    [JsonIgnore]
    public bool IsResumeTask { get; set; }

    #region 计算属性


    [JsonIgnore]
    public bool IsCompleted => DownloadedSize == ContentLength;
    [JsonIgnore]
    public bool IsBlocksHasError => Blocks.Any(t => t.Error != null);
    [JsonIgnore]
    public double Progress => (DownloadedSize / (ContentLength * 1.0));
    [JsonIgnore]
    public double CurrentSpeed => (DownloadedSize - _lastProgressUpdateNotifyTimeDownloadedSize) /
                                  (DateTime.Now - LastProgressUpdateNotifyTime).TotalSeconds;
    [JsonIgnore]
    public double CurrentSpeedOfKB => ((DownloadedSize - _lastProgressUpdateNotifyTimeDownloadedSize) /
                                       (DateTime.Now - LastProgressUpdateNotifyTime).TotalSeconds) / 1024;
    [JsonIgnore]
    public double CurrentSpeedOfMB => ((DownloadedSize - _lastProgressUpdateNotifyTimeDownloadedSize) /
                                       (DateTime.Now - LastProgressUpdateNotifyTime).TotalSeconds) / 1048576;
    [JsonIgnore]
    public double CurrentSpeedOfGB => ((DownloadedSize - _lastProgressUpdateNotifyTimeDownloadedSize) /
                                       (DateTime.Now - LastProgressUpdateNotifyTime).TotalSeconds) / 1073741824;
    [JsonIgnore]
    public double CurrentSpeedOfKbps => ((DownloadedSize - _lastProgressUpdateNotifyTimeDownloadedSize) /
                                         (DateTime.Now - LastProgressUpdateNotifyTime).TotalSeconds) / 128;
    [JsonIgnore]
    public double CurrentSpeedOfMbps => ((DownloadedSize - _lastProgressUpdateNotifyTimeDownloadedSize) /
                                         (DateTime.Now - LastProgressUpdateNotifyTime).TotalSeconds) / 131072;
    [JsonIgnore]
    public double CurrentSpeedOfGbps => ((DownloadedSize - _lastProgressUpdateNotifyTimeDownloadedSize) /
                                         (DateTime.Now - LastProgressUpdateNotifyTime).TotalSeconds) / 134217728;
    [JsonIgnore]
    public double DownloadedSizeOfKB => DownloadedSize / 1024.0;
    [JsonIgnore]
    public double DownloadedSizeOfMB => DownloadedSize / 1048576.0;
    [JsonIgnore]
    public double DownloadedSizeOfGB => DownloadedSize / 1073741824.0;
    [JsonIgnore]
    public double ContentLengthOfKB => ContentLength / 1024.0;
    [JsonIgnore]
    public double ContentLengthOfMB => ContentLength / 1048576.0;
    [JsonIgnore]
    public double ContentLengthOfGB => ContentLength / 1073741824.0;

    #endregion


    #region Blocks管理

    public HttpResBlock? GetUnStartedBlock()
    {
        lock (Blocks)
        {
            foreach (var item in Blocks)
            {
                if (item.IsTaskStarted || item.IsCompleted)
                    continue;
                item.IsTaskStarted = true;
                return item;
            }
            return null;
        }
    }
    public int CalculateBlocks()
    {
        if (_contentLength < BlockCount)
        {
            Blocks = [new HttpResBlock(0, 0, _contentLength)];
            BlockCount = 1;
            return 1;
        }

        var blockSize = _contentLength / BlockCount;
        Blocks = new HttpResBlock[BlockCount];
        long offset = -1;
        for (var i = 0; i < BlockCount; i++)
        {
            var end = offset + blockSize;
            Blocks[i] = new HttpResBlock(i, offset + 1, end);
            offset += blockSize;
        }
        if (_contentLength % BlockCount != 0)
            Blocks[BlockCount - 1].End = _contentLength - 1;
        return BlockCount;
    }

    #endregion


    #region 任务保存与恢复
    public enum ConfigSaveState
    {
        Undefined = 0,
        InSelfFile = 1,
        InConfigFile = 2,
        TaskCompleted = 3,
    }

    public async ValueTask<ConfigSaveState> SaveStateToFileAsync(string? stateSavePath = null)
    {
        if (IsCompleted)
            return ConfigSaveState.TaskCompleted;

        // 文件长度未知 只能写入到单独的配置文件 || 或者指定了单独的文件
        if (ContentLength == -1 || stateSavePath != null)
        {
            var configPath = stateSavePath ?? SavePath + DefaultDownloadTempConfigFileSuffix;
            var tempConfigPath = configPath + ".temp";
            await using var configFs = new FileStream(configPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            await JsonSerializer.SerializeAsync(configFs, this);
            configFs.SetLength(configFs.Position);
            File.Move(tempConfigPath, configPath, true);
            return ConfigSaveState.InConfigFile;
        }

        await _stateWriteLock.WaitAsync();
        try
        {
            TaskFileStream ??= new FileStream(TempSavePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            var configData = JsonSerializer.SerializeToUtf8Bytes(this);

            // 查看是否有前配置文件
            var frontCfgLen = GetTaskJsonDataLengthFromStream(TaskFileStream, true, ContentLength) + DefaultDownloadConfigAppendLength;
            // 没有就写前配置文件
            if (frontCfgLen == -1) // 这是第一次写入配置文件  写前配置文件
            {
                TaskFileStream.Seek(ContentLength, SeekOrigin.Begin);
                await WriteConfig(TaskFileStream, configData);
                return ConfigSaveState.InSelfFile;
            }
            // 获取当前配置文件大小
            var thisConfigLen = configData.Length + DefaultDownloadConfigAppendLength;
            // 计算偏移 计算后配置文件 与 前配置文件间隔
            var configOffset = frontCfgLen > thisConfigLen
                ? frontCfgLen
                : thisConfigLen;
            // 调整到前配置文件之后 + 指定间隔
            // 写入后配置文件
            TaskFileStream.Seek(ContentLength + configOffset, SeekOrigin.Begin);
            await WriteConfig(TaskFileStream, configData);
            // 写入前配置文件
            TaskFileStream.Seek(ContentLength, SeekOrigin.Begin);
            await WriteConfig(TaskFileStream, configData);
            // 保存文件长度
            TaskFileStream.SetLength(ContentLength + configOffset + thisConfigLen);
        }
        finally
        {
            _stateWriteLock.Release();
        }
        return ConfigSaveState.InSelfFile;

        static async ValueTask WriteConfig(Stream fs, byte[] data)
        {
            var len1 = (byte)(data.Length & 0xFF);
            var len2 = (byte)((data.Length >> 8) & 0xFF);
            var len3 = (byte)((data.Length >> 16) & 0xFF);
            var len4 = (byte)((data.Length >> 23) & 0xFF);
            // TAG - LEN - DATA - TAG - LEN
            // Write Tag
            fs.Write(DefaultDownloadConfigTagBytes);
            // Write Config Len
            fs.WriteByte(len1);
            fs.WriteByte(len2);
            fs.WriteByte(len3);
            fs.WriteByte(len4);
            // WriteData
            await fs.WriteAsync(data);
            // Write Tag
            fs.Write(DefaultDownloadConfigTagBytes);
            // Write Config Len
            fs.WriteByte(len1);
            fs.WriteByte(len2);
            fs.WriteByte(len3);
            fs.WriteByte(len4);
            await fs.FlushAsync();
        }
    }

    protected static int GetTaskJsonDataLengthFromStream(FileStream fs, bool isFront, long contentLen = -1)
    {
        if (isFront)
            fs.Seek(contentLen, SeekOrigin.Begin);
        else
            fs.Seek(-DefaultDownloadConfigAppendHalfLength, SeekOrigin.End);
        unsafe
        {
            Span<byte> endBuf = stackalloc byte[DefaultDownloadConfigAppendHalfLength];
            // First Check 
            var readLen = fs.Read(endBuf);
            if (readLen != DefaultDownloadConfigAppendHalfLength)
                return -1;
            for (var i = 0; i < DefaultDownloadConfigTagBytes.Length; i++)
            {
                if (endBuf[i] != DefaultDownloadConfigTagBytes[i])
                    return -1;
            }
            // Data Len
            var dataLen = (endBuf[4] | (endBuf[5] << 8) | (endBuf[6] << 16) | (endBuf[7] << 23));

            // Second Check
            fs.Seek(isFront ? dataLen : -(dataLen + DefaultDownloadConfigAppendLength), SeekOrigin.Current);
            readLen = fs.Read(endBuf);
            if (readLen != DefaultDownloadConfigAppendHalfLength)
                return -1;
            for (var i = 0; i < DefaultDownloadConfigTagBytes.Length; i++)
            {
                if (endBuf[i] != DefaultDownloadConfigTagBytes[i])
                    return -1;
            }
            return dataLen;
        }
    }

    public static async ValueTask<HttpDownloadTask?> ResumeFromSavePathAsync(string savePath)
    {
        var configPath = savePath + DefaultDownloadTempConfigFileSuffix;
        if (File.Exists(configPath))
        {
            await using var configFs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var task = await JsonSerializer.DeserializeAsync<HttpDownloadTask>(configFs);
            if (task != null)
                task.DownloadedSize = task.Blocks.Sum(t => t.CompletedSize > 0 ? t.CompletedSize : 0);
            return task;
        }

        var taskFilePath = savePath;
        if (savePath.Contains(DefaultDownloadTempFileSuffix) == false)
        {
            var baseFileName = Path.GetFileName(savePath);
            var files = Directory.GetFiles(Path.GetDirectoryName(savePath)!);
            taskFilePath = files.FirstOrDefault(t => t.Contains(baseFileName) && t.EndsWith(DefaultDownloadTempFileSuffix))?
                .Replace('\\', '/');
            if (taskFilePath == null)
                return null;
        }

        var sizeStartPos = taskFilePath.LastIndexOf(DefaultDownloadTempFileNameSizeStartTag, StringComparison.Ordinal);
        if (sizeStartPos == -1)
            return null;
        var sizeEndPos = taskFilePath.LastIndexOf(DefaultDownloadTempFileNameSizeEndTag, StringComparison.Ordinal);
        if (false == long.TryParse(taskFilePath.AsSpan(sizeStartPos + DefaultDownloadTempFileNameSizeStartTag.Length,
                sizeEndPos - sizeStartPos - DefaultDownloadTempFileNameSizeStartTag.Length), out var contentLength))
            return null;
        await using var fs = new FileStream(taskFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var cfgDataLen = GetTaskJsonDataLengthFromStream(fs, false);

        // Second Cfg
        if (cfgDataLen != -1)
            fs.Seek(-(cfgDataLen + DefaultDownloadConfigAppendHalfLength), SeekOrigin.End);
        // First Cfg
        else if ((cfgDataLen = GetTaskJsonDataLengthFromStream(fs, true, contentLength)) != -1)
            fs.Seek(contentLength + DefaultDownloadConfigAppendHalfLength, SeekOrigin.Begin);
        else
            // No Cfg
            return null;

        var offset = 0;
        var buf = ArrayPool<byte>.Shared.Rent(cfgDataLen);
        try
        {
            int readLen;
            while ((readLen = await fs.ReadAsync(buf, offset, buf.Length - offset)) != 0)
            {
                offset += readLen;
            }
            var task = JsonSerializer.Deserialize<HttpDownloadTask>(buf.AsSpan(0, cfgDataLen));
            if (task != null)
                task.DownloadedSize = task.Blocks.Sum(t => t.CompletedSize);
            return task;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

    }

    #endregion


    #region 事件触发器

    internal virtual void OnStartDownloading()
    {
        StartDownloading?.Invoke(this);
    }

    internal virtual void OnEndDownload()
    {
        lock (_progressUpdateLock)
        {
            if (DownloadedSize == ContentLength)
            {
                ProgressUpdate?.Invoke(this, null);
            }
            EndDownload?.Invoke(this);
        }
    }

    internal virtual void OnProgressUpdate(HttpResBlock? sender, int appendSize)
    {
        lock (_progressUpdateLock)
        {
            DownloadedSize += appendSize;
            var now = DateTime.Now;
            if (now - LastProgressUpdateNotifyTime < ProgressUpdateNotifyInterval)
                return;
            ProgressUpdate?.Invoke(this, sender);
            LastProgressUpdateNotifyTime = now;
            _lastProgressUpdateNotifyTimeDownloadedSize = DownloadedSize;
        }

    }

    internal virtual void OnBlockError(HttpResBlock block, Exception exception)
    {
        BlockError?.Invoke(this, block, exception);
    }

    internal virtual void OnBlockStartDownloading(HttpResBlock block)
    {
        BlockStartDownloading?.Invoke(this, block);
    }

    internal virtual void OnBlockCompleted(HttpResBlock block)
    {
        BlockCompleted?.Invoke(this, block);
    }

    internal virtual void OnDownloadError(int taskId, Exception exception)
    {
        DownloadError?.Invoke(this, taskId, exception);
    }

    #endregion



    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendFormat(
            "Url: {0}\nSavePath: {1}\nProgress: {2:P}\nSpeed: {3:0.00} MB/S\nContentLength: {4}\nDownloadedSize: {5}\nTaskCount: {6}\nBlocksCount: {7}\n",
            Url, SavePath, Progress, CurrentSpeedOfMB,ContentLength, DownloadedSize, TaskCount, BlockCount);
        foreach (var item in Blocks)
        {
            sb.AppendFormat("[ID:{0,-4}  {1}  {2,7:P}   LEN:{3} / {4}   RG:{5} - {6}]\n",
                item.Id, item.IsCompleted ? "END" : "ING", item.Progress, item.CompletedSize, item.ContentSize,
                item.Start, item.End);
        }
        return sb.ToString();
    }
}


public class HttpResBlock(int id, long start, long end, long completedSize = 0)
{
    public int Id { get; internal set; } = id;
    public long Start { get; internal set; } = start;
    public long End { get; internal set; } = end;
    public long CompletedSize { get; set; } = completedSize > 0 ? completedSize : 0;
    public long ContentSize { get; } = end - start + 1;
    [JsonIgnore]
    public Exception? Error { get; set; }

    public string? ErrorMessage => Error?.Message;

    [JsonIgnore]
    public bool IsTaskStarted { get; set; }
    [JsonIgnore]
    public bool IsCompleted => CompletedSize == ContentSize;
    [JsonIgnore]
    public double Progress => (CompletedSize / (ContentSize * 1.0));

    [JsonIgnore]
    public double CompletedSizeOfKB => CompletedSize / 1024.0;
    [JsonIgnore]
    public double CompletedSizeOfMB => CompletedSize / 1048576.0;
    [JsonIgnore]
    public double CompletedSizeOfGB => CompletedSize / 1073741824.0;
    [JsonIgnore]
    public double ContentSizeOfKB => ContentSize / 1024.0;
    [JsonIgnore]
    public double ContentSizeOfMB => ContentSize / 1048576.0;
    [JsonIgnore]
    public double ContentSizeOfGB => ContentSize / 1073741824.0;


    public override string ToString()
    {
        return $"[ID:{Id,-4}  {(IsCompleted ? "END" : "ING")}  {Progress,7:P}   LEN:{CompletedSize} / {ContentSize}   RG:{Start} - {End}]\n";
    }
}