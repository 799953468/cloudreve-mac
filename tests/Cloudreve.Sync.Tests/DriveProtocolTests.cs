using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class DriveProtocolTests
{
    private static DriveSession Session()
    {
        return new DriveSession("https://site.example/", Guid.NewGuid().ToString(), "Work", "cloudreve://my/", "alice", "access", "refresh",
            DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return send(request);
        }
    }

    private static HttpResponseMessage Json(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    [Fact]
    public async Task RestartsEncryptedMultipartUploadAndRetriesOnlyTheOutstandingCallback()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var cancel = TestContext.Current.CancellationToken;
            var session = Session();
            var ticket = JsonSerializer.SerializeToElement(new
            {
                session_id = "upload", chunk_size = 3, storage_policy = new { type = "s3" },
                upload_urls = new[] { "https://storage.example/0", "https://storage.example/1" },
                completeURL = "https://storage.example/merge", callback_secret = "callback",
                encrypt_metadata = new { algorithm = UploadCipher.Algorithm, key_plain_text = Convert.ToBase64String(new byte[32]), iv = Convert.ToBase64String(new byte[16]) }
            });
            string path = Path.Combine(directory.FullName, "source");
            byte[] expected = [1, 2, 3, 4, 5];
            File.WriteAllBytes(path, expected);
            using (var cipher = UploadCipher.FromTicket(ticket))
            {
                cipher!.Apply(expected, 0);
            }

            int sessions = 0, callbacks = 0, merges = 0;
            var parts = new List<int>();
            using (var api = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/v4/file/upload" && request.Method == HttpMethod.Put)
                {
                    sessions++;
                    return Task.FromResult(Json(new { code = 0, data = ticket }));
                }

                Assert.Equal("/api/v4/callback/s3/upload/callback", request.RequestUri.AbsolutePath);
                callbacks++;
                return Task.FromResult(callbacks == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(new { code = 0 }));
            })))
            using (var storage = new HttpClient(new Handler(async request =>
            {
                Assert.Null(request.Headers.Authorization);
                string key = request.RequestUri!.AbsolutePath[1..];
                if (key == "merge")
                {
                    merges++;
                    var xml = System.Xml.Linq.XElement.Parse(await request.Content!.ReadAsStringAsync(cancel));
                    Assert.Equal(["tag0", "tag1"], xml.Descendants("ETag").Select(tag => tag.Value));
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                int index = int.Parse(key);
                parts.Add(index);
                Assert.Equal(index == 0 ? expected[..3] : expected[3..], await request.Content!.ReadAsByteArrayAsync(cancel));
                if (parts.Count == 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"tag{index}\"");
                return response;
            })))
            {
                Task RestartAsync()
                {
                    var files = new TransferFiles(Path.Combine(directory.FullName, "job"));
                    var client = new DriveClient(api, storage, session, _ => { });
                    return client.UploadAsync(path, session.Root + "source", null, cancel, files: files, saved: files.ReadUpload());
                }

                await Assert.ThrowsAsync<HttpRequestException>(RestartAsync);
                await Assert.ThrowsAsync<HttpRequestException>(RestartAsync);
                await RestartAsync();
                Assert.Equal([0, 1, 1], parts);
                Assert.Equal(1, sessions);
                Assert.Equal(1, merges);
                Assert.Equal(2, callbacks);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("https://site.example")]
    [InlineData("http://192.168.1.10:5212")]
    public async Task ExchangesOnlyTheMatchingCallbackAndChecksPkce(string site)
    {
        string? verifier = null;
        var exchanges = 0;
        using (var http = new HttpClient(new Handler(async request =>
               {
                   Assert.Equal(site, request.RequestUri!.GetLeftPart(UriPartial.Authority));
                   if (request.RequestUri.AbsolutePath == "/api/v4/site/ping")
                   {
                       return Json(new { code = 0, data = "4.12.0-pro" });
                   }

                   Assert.Equal("/api/v4/session/oauth/token", request.RequestUri.AbsolutePath);
                   Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
                   var form = HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync());
                   Assert.Equal("/callback/desktop", form["redirect_uri"]);
                   Assert.Equal("authorization_code", form["grant_type"]);
                   Assert.Equal("approved-code", form["code"]);
                   verifier = form["code_verifier"];
                   exchanges++;
                   return Json(new { access_token = "new-access", refresh_token = "new-refresh", expires_in = 600, refresh_token_expires_in = 3600 });
               })))
        {
            var login = new DesktopLogin(http, site);
            var cancellationToken = TestContext.Current.CancellationToken;
            var authorize = await login.BeginAsync(cancellationToken);
            Assert.Equal(site, authorize.GetLeftPart(UriPartial.Authority));
            Assert.Equal("/session/authorize", authorize.AbsolutePath);
            var query = HttpUtility.ParseQueryString(authorize.Query);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("/callback/desktop", query["redirect_uri"]);
            string prefix = "cloudreve://mount?code=approved-code&path=cloudreve%3A%2F%2Fmy%2F&state=";
            await Assert.ThrowsAsync<InvalidDataException>(() => login.CompleteAsync(new Uri(prefix + "wrong"), cancellationToken));
            var correct = new Uri(prefix + query["state"]);
            Assert.Equal("cloudreve://my/", (await login.CompleteAsync(correct, cancellationToken)).Root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => login.CompleteAsync(correct, cancellationToken));
            Assert.Equal(1, exchanges);
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier!))).TrimEnd('=').Replace('+', '-').Replace('/', '_'), query["code_challenge"]);
        }
    }

    [Fact]
    public void ReauthorizationChecksServerIdentityAndKeepsDriveId()
    {
        var previous = Session();
        var candidate = Session();
        Assert.Equal(previous.DriveId, DesktopLogin.VerifyBinding(candidate, "alice", previous).DriveId);
        Assert.Throws<InvalidDataException>(() => DesktopLogin.VerifyBinding(candidate, "bob", previous));
        Assert.Throws<InvalidDataException>(() => DesktopLogin.VerifyBinding(candidate with { UserId = "bob" }, "bob", previous));
        Assert.Throws<InvalidDataException>(() => DesktopLogin.VerifyBinding(candidate with { Site = "https://elsewhere.example" }, "alice", previous));
    }

    [Fact]
    public async Task ConcurrentRequestsRotateExpiredTokenOnce()
    {
        var session = Session() with { AccessExpires = DateTimeOffset.UtcNow };
        var saves = new List<DriveSession>();
        int refreshes = 0;
        using (var http = new HttpClient(new Handler(async request =>
               {
                   if (request.RequestUri!.AbsolutePath == "/api/v4/session/token/refresh")
                   {
                       Interlocked.Increment(ref refreshes);
                       Assert.Null(request.Headers.Authorization);
                       Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                       Assert.Equal("refresh", (await request.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString());
                       await Task.Yield();
                       return Json(new
                       {
                           code = 0,
                           data = new
                           {
                               access_token = "rotated-access", refresh_token = "rotated-refresh", access_expires = DateTimeOffset.UtcNow.AddHours(1),
                               refresh_expires = DateTimeOffset.UtcNow.AddDays(1)
                           }
                       });
                   }

                   Assert.Equal("Bearer rotated-access", request.Headers.Authorization!.ToString());
                   Assert.Equal(session.DriveId, Assert.Single(request.Headers.GetValues("X-Cr-Client-Id")));
                   return Json(new { code = 0, data = new { id = "alice" } });
               })))
        {
            var drive = new DriveClient(http, http, session, saves.Add);
            var cancellationToken = TestContext.Current.CancellationToken;
            await Task.WhenAll(drive.UserIdAsync(cancellationToken), drive.UserIdAsync(cancellationToken));
            Assert.Equal(1, refreshes);
            Assert.Equal("rotated-refresh", Assert.Single(saves).RefreshToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsNumberedAndCursorPages(bool cursor)
    {
        int pages = 0;
        string folder = "cloudreve://my/文稿";
        using (var http = new HttpClient(new Handler(request =>
               {
                   var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
                   Assert.Equal(folder, query["uri"]);
                   if (pages == 1)
                   {
                       Assert.Equal(cursor ? "a+/=" : "1", query[cursor ? "next_page_token" : "page"]);
                   }

                   object pagination = cursor ? new { next_token = pages == 0 ? "a+/=" : null } : new { page = pages, page_size = 1, total_items = 2 };
                   pages++;
                   return Task.FromResult(Json(new { code = 0, data = new { pagination, files = new[] { new { name = $"file{pages}", path = folder + "/file" + pages, type = 0, size = 4 } } } }));
               })))
        {
            var result = await new DriveClient(http, http, Session(), _ => { }).ListAsync(folder, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "file1", "file2" }, result.Select(f => f.Name));
        }
    }

    [Theory]
    [InlineData("https://site.example", "https://cdn.example/file?sig=x", true)]
    [InlineData("https://site.example", "https://cdn.example/file?sig=x", false)]
    [InlineData("http://site.example:5212", "/file?sig=x", true)]
    [InlineData("http://site.example:5212", "http://cdn.example/file?sig=x", true)]
    public async Task SignedDownloadIsAnonymousAndAtomic(string site, string downloadUrl, bool complete)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string destination = Path.Combine(dir.FullName, "saved.txt");
            var cancellationToken = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(destination, "original", cancellationToken);
            var file = new DriveEntry("saved.txt", "cloudreve://my/saved.txt", false, 4);
            using (var api = new HttpClient(new Handler(async request =>
                   {
                       Assert.Equal("/api/v4/file/url", request.RequestUri!.AbsolutePath);
                       Assert.Equal(file.Uri, (await request.Content!.ReadFromJsonAsync<JsonElement>()).GetProperty("uris")[0].GetString());
                       Assert.NotNull(request.Headers.Authorization);
                       return Json(new { code = 0, data = new { urls = new[] { new { url = downloadUrl } } } });
                   })))
            using (var storage = new HttpClient(new Handler(request =>
                   {
                       Assert.Equal(new Uri(new Uri(site), downloadUrl), request.RequestUri);
                       Assert.Null(request.Headers.Authorization);
                       Assert.False(request.Headers.Contains("X-Cr-Client-Id"));
                       return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(complete ? "data" : "da") });
                   })))
            {
                var drive = new DriveClient(api, storage, Session() with { Site = site }, _ => { });
                if (complete)
                {
                    await drive.DownloadAsync(file, destination, cancellationToken);
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => drive.DownloadAsync(file, destination, cancellationToken));
                }

                Assert.Equal(complete ? "data" : "original", await File.ReadAllTextAsync(destination, cancellationToken));
                Assert.Single(dir.GetFiles());
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task StreamsVerificationDigestAcrossChunksIncludingEmptyContent()
    {
        byte[] data = new byte[200_003];
        new Random(41).NextBytes(data);
        using (var api = new HttpClient(new Handler(_ => Task.FromResult(Json(new { code = 0, data = new { urls = new[] { new { url = "https://cdn.example/file" } } } })))))
        using (var storage = new HttpClient(new Handler(request =>
               {
                   Assert.Null(request.Headers.Authorization);
                   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
               })))
        {
            var drive = new DriveClient(api, storage, Session(), _ => { });
            var cancel = TestContext.Current.CancellationToken;
            var file = new DriveEntry("doc", "cloudreve://my/doc", false, data.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), await drive.HashDownloadAsync(file, cancel));
            await Assert.ThrowsAsync<InvalidDataException>(() => drive.HashDownloadAsync(file with { Size = data.Length - 1 }, cancel));
            await Assert.ThrowsAsync<InvalidDataException>(() => drive.HashDownloadAsync(file with { Size = data.Length + 1 }, cancel));
            data = [];
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), await drive.HashDownloadAsync(file with { Size = 0 }, cancel));
        }
    }

    [Fact]
    public async Task HttpsSiteRejectsRemoteHttpDownload()
    {
        using (var api = new HttpClient(new Handler(_ => Task.FromResult(Json(new { code = 0, data = new { urls = new[] { new { url = "http://cdn.example/file" } } } })))))
        using (var storage = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected storage request"))))
        {
            var drive = new DriveClient(api, storage, Session(), _ => { });
            var file = new DriveEntry("saved.txt", "cloudreve://my/saved.txt", false, 4);
            await Assert.ThrowsAsync<InvalidDataException>(() => drive.DownloadAsync(file, "unused", TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("event: reconnect-required\ndata: <nil>\n\n")]
    public async Task EventsReconnectOnEofOrServerRequestAndStopOnCancellation(string ending)
    {
        int subscriptions = 0;
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        using (var http = new HttpClient(new Handler(request =>
               {
                   Assert.Equal("/api/v4/file/events", request.RequestUri!.AbsolutePath);
                   Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
                   subscriptions++;
                   string payload = subscriptions == 1
                       ? "event: subscribed\ndata: <nil>\n\nevent: keep-alive\ndata: <nil>\n\n" + ending
                       : "event: resumed\ndata: <nil>\n\nevent: event\ndata: [{}]\n\n";
                   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "text/event-stream") });
               })))
        {
            var seen = new List<string>();
            var drive = new DriveClient(http, http, Session(), _ => { });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drive.WatchAsync(Session().Root, signal =>
            {
                seen.Add(signal);
                if (signal == "event")
                {
                    cancel.Cancel();
                }
            }, cancel.Token));
            Assert.Equal(2, subscriptions);
            Assert.Equal(new[] { "subscribed", "disconnected", "resumed", "event" }, seen.Take(4));
        }
    }
}
