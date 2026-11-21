using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;

namespace Cloudreve.Desktop;

internal sealed record DriveEntry(string Name, string Uri, bool IsFolder, long Size, string? Version = null, bool SkipSync = false)
{
    public override string ToString()
    {
        return IsFolder ? $"{Name} /" : $"{Name}   ·   {Size:N0} B";
    }
}

internal sealed partial class DriveClient(HttpClient api, HttpClient storage, DriveSession initial, Action<DriveSession> persist)
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
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
                throw new InvalidOperationException("授权已过期，请重新授权。");
            }

            // Once sent, finish token rotation even if directory navigation cancels its original caller.
            using (var response = await api.PostAsJsonAsync(ApiUri("session/token/refresh"), new { refresh_token = session.RefreshToken }, CancellationToken.None))
            {
                response.EnsureSuccessStatusCode();
                JsonElement data = DesktopLogin.Envelope(await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None));
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

    private async Task<JsonElement> JsonAsync(HttpMethod method, string path, object? body, CancellationToken cancel)
    {
        using (var response = await SendAsync(() => new HttpRequestMessage(method, ApiUri(path))
                   { Content = body == null ? null : JsonContent.Create(body) }, cancel))
        {
            response.EnsureSuccessStatusCode();
            return DesktopLogin.Envelope(await response.Content.ReadFromJsonAsync<JsonElement>(cancel));
        }
    }

    internal async Task<string> UserIdAsync(CancellationToken cancel)
    {
        return DesktopLogin.Required(await JsonAsync(HttpMethod.Get, "user/me", null, cancel), "id");
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
            JsonElement data = await JsonAsync(HttpMethod.Get, path + continuation, null, cancel);
            if (data.TryGetProperty("recursion_limit_reached", out var limited) && limited.ValueKind == JsonValueKind.True)
            {
                throw new InvalidDataException("目录列表不完整，请稍后重试。");
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
                    throw new InvalidDataException("读取期间目录已变化。");
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
                throw new InvalidDataException("目录列表不完整，请稍后重试。");
            }

            return entries;
        }

        throw new InvalidDataException("服务端重复返回同一分页位置。");
    }

    private static DriveEntry ParseEntry(JsonElement file)
    {
        int type = file.GetProperty("type").GetInt32();
        return new DriveEntry(DesktopLogin.Required(file, "name"), DesktopLogin.Required(file, "path"),
            type == 1, file.GetProperty("size").GetInt64(),
            file.TryGetProperty("primary_entity", out var version) ? version.GetString() : null, type is not (0 or 1) || HasSyncExclusion(file));
    }

    private static bool HasSyncExclusion(JsonElement file)
    {
        return file.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
            (metadata.TryGetProperty("sys:shared_redirect", out _) || metadata.TryGetProperty("sys:upload_session_id", out _));
    }

    internal async Task<string> FolderIdAsync(string uri, CancellationToken cancel)
    {
        var data = await JsonAsync(HttpMethod.Get, "file/info?uri=" + Uri.EscapeDataString(uri), null, cancel);
        if (data.GetProperty("type").GetInt32() != 1 || HasSyncExclusion(data) ||
            !PairInventory.SameUri(uri.TrimEnd('/'), DesktopLogin.Required(data, "path").TrimEnd('/')))
        {
            throw new InvalidDataException("无法确认配对的云端目录。");
        }

        return DesktopLogin.Required(data, "id");
    }

    internal async Task<DriveEntry> StatAsync(string uri, CancellationToken cancel)
    {
        return ParseEntry(await JsonAsync(HttpMethod.Get, "file/info?uri=" + Uri.EscapeDataString(uri), null, cancel));
    }

    internal async Task DownloadAsync(DriveEntry file, string destination, CancellationToken cancel)
    {
        var parameters = new Dictionary<string, object> { ["uris"] = new[] { file.Uri }, ["download"] = true };
        if (!string.IsNullOrEmpty(file.Version))
        {
            parameters.Add("entity", file.Version);
        }

        var data = await JsonAsync(HttpMethod.Post, "file/url", parameters, cancel);
        var urls = data.GetProperty("urls");
        var site = new Uri(session.Site);
        if (urls.GetArrayLength() != 1 || !Uri.TryCreate(site, DesktopLogin.Required(urls[0], "url"), out var url) || url.UserInfo.Length > 0 ||
            (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && (site.Scheme == Uri.UriSchemeHttp || url.IsLoopback))))
        {
            throw new InvalidDataException("站点未返回可用的下载地址。");
        }

        // The signed URL can point at a different host. Never attach site credentials.
        using (var response = await storage.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            string temp = Path.Combine(Path.GetDirectoryName(destination)!, $".cloudreve-{Guid.NewGuid():N}.part");
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                try
                {
                    await response.Content.CopyToAsync(output, cancel);
                    if (output.Length != file.Size)
                    {
                        throw new InvalidDataException("文件大小已变化或下载不完整，请刷新后重试。");
                    }

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
    }

    internal async Task WatchAsync(string directory, Action<string> notify, CancellationToken cancel)
    {
        TimeSpan delay = TimeSpan.FromSeconds(1);
        var url = ApiUri("file/events?uri=" + Uri.EscapeDataString(directory));
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
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
                    if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    {
                        throw new InvalidDataException("站点未提供目录事件流，可手动刷新。");
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync(cancel))
                    {
                        await foreach (var message in SseParser.Create(stream).EnumerateAsync(cancel))
                        {
                            if (message.EventType is "subscribed" or "resumed" or "event")
                            {
                                notify(message.EventType);
                            }

                            if (message.EventType == "event")
                            {
                                delay = TimeSpan.FromSeconds(1);
                            }

                            if (message.EventType == "reconnect-required")
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == null || (int)ex.StatusCode >= 500)
            {
                // Transient transport or service failure; reconnect below.
            }
            catch (IOException)
            {
                // A reset can occur while reading the event stream body.
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                // HTTP header timeout, not a user cancellation.
            }

            notify("disconnected");
            await Task.Delay(delay, cancel);
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
    }
}
