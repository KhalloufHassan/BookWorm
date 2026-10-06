namespace BookWorm.Server.Backups;

/// <summary>Stops the app so a requested restore can run while it starts again.</summary>
public interface IAppRestarter
{
    /// <summary>Stops the app once <paramref name="response"/> has been sent.</summary>
    void RestartAfter(HttpResponse response);
}

internal sealed class HostAppRestarter(IHostApplicationLifetime lifetime, ILogger<HostAppRestarter> logger) : IAppRestarter
{
    public void RestartAfter(HttpResponse response) => response.OnCompleted(() =>
    {
        // Docker Compose ("restart: unless-stopped") starts the app again, and the restore runs then.
        logger.LogWarning("Stopping BookWorm to restore a backup. It restores while starting again.");
        lifetime.StopApplication();
        return Task.CompletedTask;
    });
}
