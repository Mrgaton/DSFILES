using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DSFiles_Server.Routes
{
    internal sealed class ShortLinkExpiryQueue : BackgroundService
    {
        internal sealed record Item(string Key, string Token, long ExpiresAt);
        private readonly string path;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly ILogger<ShortLinkExpiryQueue> logger;
        private List<Item> items;

        public ShortLinkExpiryQueue(ILogger<ShortLinkExpiryQueue> logger) : this(logger, Path.Combine(AppContext.BaseDirectory, "Data", "shortlink-expiry.json"))
        {
        }

        internal ShortLinkExpiryQueue(ILogger<ShortLinkExpiryQueue> logger, string storagePath)
        {
            this.logger = logger;
            path = storagePath;
            items = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.PreserveNames.ListItem)
                    ?? throw new InvalidDataException("Invalid short-link expiry queue.")
                : new List<Item>();
            if (items.Count > 10000 || items.Any(item => item.ExpiresAt <= 0
                || !ShortLinkHandler.IsValidKey(item.Key)
                || !item.Token.StartsWith("jsp:" + item.Key + ":", StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Invalid short-link expiry queue.");
            }
        }

        private async Task Save(List<Item> next)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, AppJsonContext.PreserveNames.ListItem));
            File.Move(temporary, path, true);
            items = next;
        }

        public async Task Add(string key, string token, long expiresAt)
        {
            await gate.WaitAsync();
            try
            {
                if (items.Count >= 10000)
                {
                    throw new InvalidOperationException("Expiry queue is full.");
                }

                var next = items.Where(i => i.Key != key).ToList();
                next.Add(new(key, token, expiresAt));
                await Save(next);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<bool> Expired(string key)
        {
            await gate.WaitAsync();
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return items.Any(i => i.Key == key && i.ExpiresAt <= now);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task Forget(string key)
        {
            await gate.WaitAsync();
            try
            {
                var next = items.Where(i => i.Key != key).ToList();
                if (next.Count != items.Count)
                {
                    await Save(next);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await gate.WaitAsync(stoppingToken);
                Item[] due;
                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    due = items.Where(i => i.ExpiresAt <= now).ToArray();
                }
                finally
                {
                    gate.Release();
                }

                foreach (var item in due)
                {
                    try
                    {
                        await ShortLinkHandler.DeleteDocument(item.Token, stoppingToken);
                        await Forget(item.Key);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("JSPaste scheduled deletion failed ({Type}); will retry.", ex.GetType().Name);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
