using System.Collections.Concurrent;
using System.Net;

using SkiaSharp;

namespace GeoJsonEditor.Map;

public readonly record struct TileKey(string SourceId, int Layer, int Z, int X, int Y);

/// <summary>
/// 瓦片下载与缓存：内存里保留最近使用的若干张解码后的图，磁盘缓存原始文件。
/// 下载按“后请求先下载”的顺序进行，快速平移时优先加载当前视野。
/// 解码后的图只在 UI 线程上淘汰和释放，绘制过程中不会被后台线程回收。
/// </summary>
public sealed class TileCache : IDisposable
{
    private const int MemoryCapacity = 600;
    private const int Workers = 6;
    private const int MaxPending = 200;

    private readonly string _diskRoot;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private readonly Dictionary<TileKey, LinkedListNode<(TileKey Key, SKImage Image)>> _memory = new();
    private readonly LinkedList<(TileKey Key, SKImage Image)> _lru = new();
    private readonly HashSet<TileKey> _inFlight = new();
    private readonly List<(TileKey Key, string Url)> _pending = new();
    private readonly ConcurrentDictionary<TileKey, DateTime> _failed = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>有新瓦片可用时触发（在后台线程上）。</summary>
    public event Action? TileArrived;

    public TileCache(string diskRoot)
    {
        _diskRoot = diskRoot;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = Workers,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GeoJsonEditor/1.0 (MewUI desktop app)");

        for (int i = 0; i < Workers; i++) _ = Task.Run(WorkerLoop);
    }

    public static string DefaultDiskRoot()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
        return Path.Combine(baseDir, "GeoJsonEditor", "tiles");
    }

    /// <summary>
    /// 每帧绘制前在 UI 线程上调用：清掉上一帧没来得及处理的请求，并淘汰超量的内存瓦片。
    /// </summary>
    public void BeginFrame()
    {
        List<SKImage>? evicted = null;
        lock (_lock)
        {
            _pending.Clear();
            while (_lru.Count > MemoryCapacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _memory.Remove(last.Value.Key);
                (evicted ??= new()).Add(last.Value.Image);
            }
        }
        if (evicted != null)
        {
            foreach (var img in evicted) img.Dispose();
        }
    }

    /// <summary>只查内存，不触发下载。</summary>
    public SKImage? Peek(TileKey key)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
        }
        return null;
    }

    /// <summary>查内存；没有时排队加载（先读磁盘，再走网络）。</summary>
    public SKImage? Get(TileKey key, string url)
    {
        var img = Peek(key);
        if (img != null) return img;

        if (_failed.TryGetValue(key, out var failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromSeconds(20))
        {
            return null;
        }

        lock (_lock)
        {
            if (_inFlight.Contains(key)) return null;
            _pending.Add((key, url));
            if (_pending.Count > MaxPending) _pending.RemoveAt(0);
        }
        _signal.Release();
        return null;
    }

    private async Task WorkerLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                (TileKey Key, string Url) job;
                lock (_lock)
                {
                    if (_pending.Count == 0) continue;
                    job = _pending[^1];
                    _pending.RemoveAt(_pending.Count - 1);
                    if (_memory.ContainsKey(job.Key) || !_inFlight.Add(job.Key)) continue;
                }

                try
                {
                    await Load(job.Key, job.Url, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_lock) _inFlight.Remove(job.Key);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Load(TileKey key, string url, CancellationToken ct)
    {
        try
        {
            var path = DiskPath(key);
            byte[]? bytes = null;
            if (File.Exists(path))
            {
                try
                {
                    bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    bytes = null;
                }
            }

            bool fromNetwork = false;
            if (bytes == null || bytes.Length == 0)
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _failed[key] = DateTime.UtcNow;
                    return;
                }
                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                fromNetwork = true;
            }

            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null)
            {
                _failed[key] = DateTime.UtcNow;
                if (!fromNetwork) TryDelete(path);
                return;
            }

            if (fromNetwork)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            var image = SKImage.FromBitmap(bitmap);
            if (image == null) return;

            lock (_lock)
            {
                if (_memory.ContainsKey(key))
                {
                    image.Dispose();
                    return;
                }
                _memory[key] = _lru.AddFirst((key, image));
            }
            _failed.TryRemove(key, out _);
            TileArrived?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _failed[key] = DateTime.UtcNow;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private string DiskPath(TileKey key)
        => Path.Combine(_diskRoot, key.SourceId, key.Layer.ToString(), key.Z.ToString(), key.X.ToString(), key.Y + ".tile");

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        lock (_lock)
        {
            foreach (var (_, img) in _lru) img.Dispose();
            _lru.Clear();
            _memory.Clear();
        }
    }
}
