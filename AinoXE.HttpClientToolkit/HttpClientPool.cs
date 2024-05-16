namespace AinoXE.HttpClientToolkit;

public class HttpClientPool(
    Uri baseUri,
    int maxReverseCount,
    int maxCount,
    HttpMessageHandler? defaultHttpMessageHandler = null)
{
    public HttpMessageHandler? DefaultHttpMessageHandler { get; set; } = defaultHttpMessageHandler;
    public Uri BaseUri { get; } = baseUri;
    public int MaxReverseCount { get; set; } = maxReverseCount;
    public int MaxCount { get; set; } = maxCount;
    public int CurrentCount { get; private set; }


    private readonly Queue<HttpClient> _clients = new();

    public HttpClient Rent()
    {
        lock (_clients)
        {
            if (_clients.Count != 0)
                return _clients.Dequeue();
            CurrentCount++;
            if (CurrentCount > MaxCount)
                throw new OverflowException("HttpClientPool Not Has FreeClient!");
            return DefaultHttpMessageHandler is null
                ? new HttpClient() { BaseAddress = BaseUri }
                : new HttpClient(DefaultHttpMessageHandler) { BaseAddress = BaseUri };
        }
    }

    public void Return(HttpClient client)
    {
        lock (_clients)
        {
            if (_clients.Count < MaxReverseCount)
            {
                _clients.Enqueue(client);
                return;
            }
        }
        client.Dispose();
    }
}