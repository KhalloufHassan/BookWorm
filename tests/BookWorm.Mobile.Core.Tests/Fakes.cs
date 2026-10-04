using System.Net;
using System.Net.Http.Json;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Auth;
using BookWorm.Mobile.Core.Offline;

namespace BookWorm.Mobile.Core.Tests;

/// <summary>A server that answers with whatever the test says, and records what it was sent.</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public const string Address = "https://books.test/";

    public List<(HttpMethod Method, string Path, string Body, string Authorization)> Requests { get; } = [];

    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent);

    public bool Unreachable { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable)
        {
            throw new HttpRequestException("No route to host.");
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri.PathAndQuery.TrimStart('/'), body, request.Headers.Authorization?.ToString()));
        }

        var response = Respond(request, body);
        response.RequestMessage = request;
        return response;
    }

    public HttpClient Client(HttpMessageHandler first = null) => new(first ?? this, disposeHandler: false) { BaseAddress = new Uri(Address) };

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: BookWormJson.Options) };
}

internal sealed class FakeConnectivity : IConnectivity
{
    private bool _isOnline = true;

    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            _isOnline = value;
            Changed?.Invoke();
        }
    }

    public event Action Changed;
}

internal sealed class MemorySessionStorage : ISessionStorage
{
    public string Address { get; set; }

    public AppSession Session { get; set; }

    public Task<string> GetServerAddressAsync() => Task.FromResult(Address);

    public Task SetServerAddressAsync(string address)
    {
        Address = address;
        return Task.CompletedTask;
    }

    public Task<AppSession> LoadSessionAsync() => Task.FromResult(Session);

    public Task SaveSessionAsync(AppSession session)
    {
        Session = session;
        return Task.CompletedTask;
    }

    public Task ClearSessionAsync()
    {
        Session = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A signed-in app with one downloaded book, in a temporary folder.</summary>
internal sealed class OfflineFixture : IDisposable
{
    public static readonly Guid UserId = Guid.NewGuid();

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"bookworm-mobile-{Guid.NewGuid():N}");

    public FakeServer Server { get; } = new();

    public FakeConnectivity Connectivity { get; } = new();

    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public MemorySessionStorage Storage { get; } = new()
    {
        Address = FakeServer.Address,
        Session = new AppSession("access", "refresh", DateTimeOffset.MaxValue, new CurrentUser(UserId, "reader", null, false)),
    };

    public OfflineStore Store { get; }

    public OfflineQueue Queue { get; }

    public SessionManager Session { get; }

    public OfflineFixture()
    {
        Store = new OfflineStore(Root);
        Queue = new OfflineQueue(Store, Time);
        Session = new SessionManager(Storage, address => new HttpClient(Server, disposeHandler: false) { BaseAddress = new Uri(address) }, Time, "1.0.0");
        Session.InitializeAsync().GetAwaiter().GetResult();
    }

    /// <summary>The app's HttpClient: offline handler in front of the server.</summary>
    public HttpClient AppClient() => Server.Client(new OfflineHttpHandler(Store, Queue, Session, Connectivity, Time) { InnerHandler = Server });

    public static BookFileDetails EpubFile(Guid id) => new(id, BookFormat.Epub, "moby.epub", 10, "abc", DateTimeOffset.UnixEpoch, null, null);

    public (Guid BookId, BookFileDetails File) AddDownloadedBook(ReadDetails read = null)
    {
        var bookId = Guid.NewGuid();
        var file = EpubFile(Guid.NewGuid());
        var other = new BookFileDetails(Guid.NewGuid(), BookFormat.Pdf, "moby.pdf", 20, "def", DateTimeOffset.UnixEpoch, null, null);
        var book = new BookDetails(bookId, "Moby-Dick", BookStatus.CurrentlyReading, null, null, null, [], [], [], [], [file, other], null, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
        Store.Write(UserId, bookId, OfflineStore.BookJson, book);
        Store.Write(UserId, bookId, OfflineStore.HighlightsJson, new List<HighlightDetails>());
        Store.Write(UserId, bookId, OfflineStore.ReadJson, read);
        File.WriteAllText(Store.BookFilePath(UserId, bookId, BookFormat.Epub), "epub");
        Store.Write(UserId, bookId, OfflineStore.FileJson, new DownloadRecord(file, Time.Now));
        return (bookId, file);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
