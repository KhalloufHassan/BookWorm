using System.Text.Json;
using BookWorm.Contracts;

namespace BookWorm.Mobile.Core.Offline;

/// <summary>
/// A change made offline, to send to the server later: an API request, replayed as it was made.
/// </summary>
/// <param name="Path">Relative to the server address, e.g. <c>api/books/{id}/reads/{readId}/progress</c>.</param>
/// <param name="Body">The JSON request body, or null.</param>
/// <param name="MergeKey">
/// Changes with the same key replace each other, e.g. reading positions of the same read and sitting:
/// only the latest matters.
/// </param>
public sealed record QueuedChange(Guid Id, DateTimeOffset QueuedAt, Guid BookId, string Method, string Path, string Body, string MergeKey);

/// <summary>
/// Changes made offline, per user, as one JSON file each in <c>offline/{userId}/queue/</c>, named so
/// that they sort in the order they were made.
/// </summary>
public sealed class OfflineQueue(OfflineStore store, TimeProvider timeProvider)
{
    private readonly object _lock = new();
    private long _lastTicks;

    public event Action Changed;

    public void Enqueue(Guid userId, Guid bookId, HttpMethod method, string path, object body, string mergeKey = null)
    {
        var folder = Folder(userId);
        lock (_lock)
        {
            Directory.CreateDirectory(folder);
            if (mergeKey is not null)
            {
                foreach (var (file, queued) in ReadAll(userId))
                {
                    if (queued.MergeKey == mergeKey)
                    {
                        File.Delete(file);
                    }
                }
            }

            // Strictly increasing, so changes made within the same tick keep their order.
            var now = timeProvider.GetUtcNow();
            _lastTicks = Math.Max(_lastTicks + 1, now.UtcTicks);
            var change = new QueuedChange(
                Guid.NewGuid(), now, bookId, method.Method, path,
                body is null ? null : JsonSerializer.Serialize(body, body.GetType(), BookWormJson.Options), mergeKey);
            var name = $"{_lastTicks:D20}-{change.Id:N}.json";
            var temp = Path.Combine(folder, name + ".tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(change, BookWormJson.Options));
            File.Move(temp, Path.Combine(folder, name));
        }

        Changed?.Invoke();
    }

    /// <summary>The user's queued changes, oldest first, with the file each is stored in.</summary>
    public IReadOnlyList<(string File, QueuedChange Change)> ReadAll(Guid userId)
    {
        var folder = Folder(userId);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var result = new List<(string, QueuedChange)>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<QueuedChange>(File.ReadAllText(file), BookWormJson.Options) is { } change)
                {
                    result.Add((file, change));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Unreadable (e.g. removed by a sync at the same moment): skip it.
            }
        }

        return result;
    }

    public int Count(Guid userId, Guid bookId) => ReadAll(userId).Count(entry => entry.Change.BookId == bookId);

    public void Remove(string file)
    {
        lock (_lock)
        {
            File.Delete(file);
        }

        Changed?.Invoke();
    }

    public void RemoveBook(Guid userId, Guid bookId)
    {
        lock (_lock)
        {
            foreach (var (file, change) in ReadAll(userId))
            {
                if (change.BookId == bookId)
                {
                    File.Delete(file);
                }
            }
        }

        Changed?.Invoke();
    }

    private string Folder(Guid userId) => Path.Combine(store.UserFolder(userId), "queue");
}
