using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cloudreve.Sync;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed record DriveEntry(string Name, string Uri, bool IsFolder, long Size, string? Version = null, bool SkipSync = false, string? Id = null, string? UploadSessionId = null)
{
    public override string ToString()
    {
        return IsFolder ? $"{Name} /" : $"{Name}   ·   {Size:N0} B";
    }
}

internal sealed partial class DriveClient(HttpClient api, HttpClient storage, DriveSession initial, Action<DriveSession> persist)
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly Dictionary<(string Directory, CancellationToken Batch), Task<List<DriveEntry>>> uploadListings = [];
    private DriveSession session = initial;

    private Uri ApiUri(string path)
    {
        return new Uri(new Uri(session.Site), "/api/v4/" + path);
    }

    private async Task<DriveSession> CredentialsAsync(string? rejected, CancellationToken cancel)
    {
        await refreshLock.WaitAsync(cancel);
        try
        {
            if (session.AccessExpires > DateTimeOffset.UtcNow.AddSeconds(30) && session.AccessToken != rejected)
            {
                return session;
            }

            if (session.RefreshExpires <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException(UiText.Get("api.authorizationExpired"));
            }

            // Once sent, finish token rotation even if directory navigation cancels its original caller.
            var request = new RefreshTokenRequest(session.RefreshToken);
            using (var response = await api.PostAsJsonAsync(ApiUri("session/token/refresh"), request, DriveApiJson.Default.RefreshTokenRequest, CancellationToken.None))
            {
                response.EnsureSuccessStatusCode();
                JsonElement data = DesktopLogin.Envelope(await response.Content.ReadFromJsonAsync(DriveApiJson.Default.JsonElement, CancellationToken.None));
                session = session with
                {
                    AccessToken = DesktopLogin.Required(data, "access_token"), RefreshToken = DesktopLogin.Required(data, "refresh_token"),
                    AccessExpires = data.GetProperty("access_expires").GetDateTimeOffset(), RefreshExpires = data.GetProperty("refresh_expires").GetDateTimeOffset()
                };
                persist(session);
                return session;
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancel)
    {
        string? rejected = null;
        for (int attempt = 0;; attempt++)
        {
            DriveSession credentials = await CredentialsAsync(rejected, cancel);
            using (var request = create())
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                request.Headers.Add("X-Cr-Client-Id", credentials.DriveId);
                var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    response.Dispose();
                    rejected = credentials.AccessToken;
                    continue;
                }

                return response;
            }
        }
    }

    private Task<JsonElement> JsonAsync<T>(HttpMethod method, string path, T body, JsonTypeInfo<T> typeInfo, CancellationToken cancel)
    {
        return JsonAsync(method, path, cancel, () => JsonContent.Create(body, typeInfo));
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string path, CancellationToken cancel, Func<JsonContent>? createContent = null)
    {
        using (var response = await SendAsync(() => new HttpRequestMessage(method, ApiUri(path))
                   { Content = createContent?.Invoke() }, cancel))
        {
            response.EnsureSuccessStatusCode();
            return DesktopLogin.Envelope(await response.Content.ReadFromJsonAsync(DriveApiJson.Default.JsonElement, cancel));
        }
    }

    internal async Task<string> UserIdAsync(CancellationToken cancel)
    {
        return DesktopLogin.Required(await JsonAsync(HttpMethod.Get, "user/me", cancel), "id");
    }

    internal async Task<(long Used, long Total)> CapacityAsync(CancellationToken cancel)
    {
        var data = await JsonAsync(HttpMethod.Get, "user/capacity", cancel);
        return (data.GetProperty("used").GetInt64(), data.GetProperty("total").GetInt64());
    }

    internal async Task<List<DriveEntry>> ListForUploadAsync(string directory, CancellationToken cancel)
    {
        // Workers in one transfer batch share only overlapping requests. Later checks must see new placeholders and collisions.
        var key = (directory, cancel);
        Task<List<DriveEntry>> request;
        lock (uploadListings)
        {
            if (!uploadListings.TryGetValue(key, out var active) || active.IsCompleted)
            {
                active = ListAsync(directory, cancel);
                uploadListings[key] = active;
            }
            request = active;
        }
        try
        {
            return await request;
        }
        finally
        {
            lock (uploadListings)
            {
                if (uploadListings.TryGetValue(key, out var active) && active == request)
                {
                    uploadListings.Remove(key);
                }
            }
        }
    }

    internal async Task<List<DriveEntry>> ListAsync(string directory, CancellationToken cancel)
    {
        string path = "file?uri=" + Uri.EscapeDataString(directory) + "&page_size=100";
        string continuation = "";
        var requested = new HashSet<string>();
        var entries = new List<DriveEntry>();
        long? expectedCount = null;
        while (requested.Add(continuation))
        {
            JsonElement data = await JsonAsync(HttpMethod.Get, path + continuation, cancel);
            if (data.TryGetProperty("recursion_limit_reached", out var limited) && limited.ValueKind == JsonValueKind.True)
            {
                throw new InvalidDataException(UiText.Get("api.listingIncomplete"));
            }

            foreach (var file in data.GetProperty("files").EnumerateArray())
            {
                entries.Add(ParseEntry(file));
            }

            var pagination = data.GetProperty("pagination");
            if (pagination.TryGetProperty("next_token", out var token) && !string.IsNullOrEmpty(token.GetString()))
            {
                continuation = "&next_page_token=" + Uri.EscapeDataString(token.GetString()!);
                continue;
            }

            if (pagination.TryGetProperty("total_items", out var total) && total.ValueKind == JsonValueKind.Number)
            {
                long count = total.GetInt64();
                if (count < 0 || expectedCount.HasValue && count != expectedCount)
                {
                    throw new InvalidDataException(UiText.Get("api.folderChangedWhileListing"));
                }

                expectedCount = count;
                long next = (long)pagination.GetProperty("page").GetInt32() + 1;
                int size = pagination.GetProperty("page_size").GetInt32();
                if (next > 0 && size > 0 && next * size < total.GetInt64())
                {
                    continuation = "&page=" + next;
                    continue;
                }
            }

            if (expectedCount.HasValue && expectedCount != entries.Count)
            {
                throw new InvalidDataException(UiText.Get("api.listingIncomplete"));
            }

            return entries;
        }

        throw new InvalidDataException(UiText.Get("api.repeatedPage"));
    }

    private static DriveEntry ParseEntry(JsonElement file)
    {
        int type = file.GetProperty("type").GetInt32();
        return new DriveEntry(DesktopLogin.Required(file, "name"), DesktopLogin.Required(file, "path"),
            type == 1, file.GetProperty("size").GetInt64(),
            file.TryGetProperty("primary_entity", out var version) ? version.GetString() : null, type is not (0 or 1) || HasSyncExclusion(file),
            file.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null, UploadPlaceholder(file, type));
    }

    private static string? UploadPlaceholder(JsonElement file, int type)
    {
        if (type == 0 && file.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
            !metadata.TryGetProperty("sys:shared_redirect", out _) && metadata.TryGetProperty("sys:upload_session_id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            return id.GetString();
        }

        return null;
    }

    private static bool HasSyncExclusion(JsonElement file)
    {
        return file.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
               (metadata.TryGetProperty("sys:shared_redirect", out _) || metadata.TryGetProperty("sys:upload_session_id", out _));
    }

    internal async Task<string> FolderIdAsync(string uri, CancellationToken cancel)
    {
        var data = await JsonAsync(HttpMethod.Get, "file/info?uri=" + Uri.EscapeDataString(uri), cancel);
        if (data.GetProperty("type").GetInt32() != 1 || HasSyncExclusion(data) ||
            !PairInventory.SameUri(uri.TrimEnd('/'), DesktopLogin.Required(data, "path").TrimEnd('/')))
        {
            throw new InvalidDataException(UiText.Get("api.pairedFolderUnverified"));
        }

        return DesktopLogin.Required(data, "id");
    }

    internal async Task<DriveEntry> StatAsync(string uri, CancellationToken cancel)
    {
        return ParseEntry(await JsonAsync(HttpMethod.Get, "file/info?uri=" + Uri.EscapeDataString(uri), cancel));
    }

    internal async Task DownloadAsync(DriveEntry file, string destination, CancellationToken cancel, SyncActivity? activity = null)
    {
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, $".cloudreve-{Guid.NewGuid():N}.part");
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            try
            {
                await ReadDownloadAsync(file, output, null, cancel, activity);
                output.Close();
                cancel.ThrowIfCancellationRequested();
                File.Move(temp, destination, true);
            }
            finally
            {
                File.Delete(temp);
            }
        }
    }

    internal async Task<string> StageDownloadAsync(DriveEntry file, TransferFiles files, string destination, CancellationToken cancel, SyncActivity? activity)
    {
        using (var partial = new DownloadPartial(files))
        {
            await partial.RestoreAsync(file.Size, cancel);
            if (!partial.Complete)
            {
                await ReadDownloadAsync(file, partial.Stream, partial.Hash, cancel, activity, partial);
            }

            // Stage on the target volume so the final install is an atomic rename even on an external disk.
            cancel.ThrowIfCancellationRequested();
            if (!LocalFile.TryClone(partial.Stream, destination))
            {
                partial.Stream.Position = 0;
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await partial.Stream.CopyToAsync(output, cancel);
                    output.Flush(true);
                }
            }

            return Convert.ToHexString(partial.Hash.GetCurrentHash());
        }
    }

    internal async Task<string> HashDownloadAsync(DriveEntry file, CancellationToken cancel, SyncActivity? activity = null)
    {
        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await ReadDownloadAsync(file, null, digest, cancel, activity);
            return Convert.ToHexString(digest.GetHashAndReset());
        }
    }

    private async Task<Uri> DownloadUriAsync(DriveEntry file, CancellationToken cancel)
    {
        string? entity = string.IsNullOrEmpty(file.Version) ? null : file.Version;
        var request = new DownloadUrlRequest([file.Uri], Download: true, Entity: entity);
        var data = await JsonAsync(HttpMethod.Post, "file/url", request, DriveApiJson.Default.DownloadUrlRequest, cancel);
        var urls = data.GetProperty("urls");
        var site = new Uri(session.Site);
        if (urls.GetArrayLength() != 1 || !Uri.TryCreate(site, DesktopLogin.Required(urls[0], "url"), out var url) || url.UserInfo.Length > 0 ||
            (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && (site.Scheme == Uri.UriSchemeHttp || url.IsLoopback))))
        {
            throw new InvalidDataException(UiText.Get("download.noUrl"));
        }

        return url;
    }

    private async Task<HttpResponseMessage> GetDownloadAsync(Uri url, long offset, CancellationToken cancel)
    {
        // The signed URL can point at a different host. Never attach site credentials.
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            return await storage.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        }
    }

    private async Task ReadDownloadAsync(DriveEntry file, Stream? output, IncrementalHash? digest, CancellationToken cancel, SyncActivity? activity,
        DownloadPartial? partial = null)
    {
        var url = await DownloadUriAsync(file, cancel);
        long offset = partial?.Stream.Position ?? 0;
        var download = await GetDownloadAsync(url, offset, cancel);
        if (offset > 0 && download.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            download.Dispose();
            partial!.Reset();
            offset = 0;
            download = await GetDownloadAsync(url, offset, cancel);
        }

        using (var response = download)
        {
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.Unit != "bytes" || range.From != offset || range.To != file.Size - 1 || range.Length != file.Size ||
                    response.Content.Headers.ContentLength is long length && length != file.Size - offset)
                {
                    partial?.Reset();
                    throw new InvalidDataException(UiText.Get("download.rangeMismatch"));
                }
            }
            else if (response.StatusCode == HttpStatusCode.OK && offset > 0)
            {
                partial!.Reset();
                offset = 0;
            }
            else if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidDataException(UiText.Get("download.rangeMismatch"));
            }

            using (var input = await response.Content.ReadAsStreamAsync(cancel))
            {
                const int bufferSize = 81920;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                long received = offset;
                try
                {
                    activity?.BeginTransfer(UiText.Get("activity.readCloudContents"), file.Size, offset);
                    while (true)
                    {
                        if (activity != null)
                        {
                            await activity.WaitForResumeAsync(cancel);
                        }

                        int read = await input.ReadAsync(buffer.AsMemory(0, bufferSize), cancel);
                        if (read == 0)
                        {
                            break;
                        }

                        if (read > file.Size - received)
                        {
                            partial?.Reset();
                            throw new InvalidDataException(UiText.Get("download.tooLarge"));
                        }

                        if (output != null)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                        }

                        digest?.AppendData(buffer, 0, read);
                        received += read;
                        if (received < file.Size)
                        {
                            partial?.Save();
                        }
                        activity?.ReportBytes(received);
                    }

                    if (received != file.Size)
                    {
                        throw new InvalidDataException(UiText.Get("download.incomplete"));
                    }

                    partial?.Save(complete: true);
                    cancel.ThrowIfCancellationRequested();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    activity?.EndTransfer();
                    if (received < file.Size)
                    {
                        partial?.Save(force: true);
                    }
                }
            }
        }
    }

    internal async Task WatchAsync(string directory, Action<string, string> notify, CancellationToken cancel, ILogger? logger = null)
    {
        logger ??= Log.Logger;
        TimeSpan delay = TimeSpan.FromSeconds(1);
        var url = ApiUri("file/events?uri=" + Uri.EscapeDataString(directory));
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            string stage = "Connect";
            string reason = "EndOfStream";
            Exception? failure = null;
            try
            {
                using (var response = await SendAsync(() =>
                       {
                           var request = new HttpRequestMessage(HttpMethod.Get, url);
                           request.Headers.Accept.ParseAdd("text/event-stream");
                           return request;
                       }, cancel))
                {
                    response.EnsureSuccessStatusCode();
                    string? mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        cancel.ThrowIfCancellationRequested();
                        string received = mediaType?.ToLowerInvariant() switch
                        {
                            null => "missing",
                            "text/html" => "text/html",
                            "application/json" => "application/json",
                            _ => "other"
                        };
                        logger.Warning("Remote event push unavailable: HTTP {StatusCode}, expected text/event-stream, received {ContentType}; folder polling remains enabled",
                            (int)response.StatusCode, received);
                        notify("disconnected", "");
                        return;
                    }

                    stage = "ReadStream";
                    using (var stream = await response.Content.ReadAsStreamAsync(cancel))
                    {
                        await foreach (var message in SseParser.Create(stream).EnumerateAsync(cancel))
                        {
                            if (message.EventType is "subscribed" or "connected" or "resumed" or "event")
                            {
                                notify(message.EventType, message.Data);
                            }

                            if (message.EventType == "event")
                            {
                                delay = TimeSpan.FromSeconds(1);
                            }

                            if (message.EventType == "reconnect-required")
                            {
                                reason = "ServerRequestedReconnect";
                                break;
                            }
                        }
                    }
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == null || (int)ex.StatusCode >= 500)
            {
                failure = ex;
            }
            catch (IOException ex)
            {
                failure = ex;
            }
            catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
            {
                failure = ex;
            }

            cancel.ThrowIfCancellationRequested();
            if (failure != null)
            {
                logger.ForContext("Stage", stage).ForContext("RetryInSeconds", delay.TotalSeconds)
                    .WriteFailure(LogEventLevel.Warning, "Remote event push", failure);
            }
            else
            {
                var level = reason == "ServerRequestedReconnect" ? LogEventLevel.Information : LogEventLevel.Warning;
                logger.Write(level, "Remote event stream disconnected: {Reason}; reconnecting in {RetryInSeconds} s", reason, delay.TotalSeconds);
            }

            notify("disconnected", "");
            await Task.Delay(delay, cancel);
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
    }
}
