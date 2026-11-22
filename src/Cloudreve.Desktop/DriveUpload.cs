using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed partial class DriveClient
{
    internal async Task UploadAsync(string path, string target, DriveEntry? previous, CancellationToken cancel, SyncActivity? activity = null,
        TransferFiles? files = null, UploadCheckpoint? saved = null)
    {
        if (previous != null && (previous.IsFolder || previous.Uri != target || string.IsNullOrEmpty(previous.Version)))
        {
            throw new InvalidDataException(UiText.Get("无法确认远端文件版本，未覆盖。请刷新目录。"));
        }

        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous))
        {
            long size = input.Length;
            if (size == 0 && previous == null)
            {
                await JsonAsync(HttpMethod.Post, "file/create", new { uri = target, type = "file", err_on_conflict = true }, cancel);
                return;
            }

            var parameters = new Dictionary<string, object>
            {
                ["uri"] = target, ["size"] = size, ["policy_id"] = "", ["encryption_supported"] = new[] { UploadCipher.Algorithm },
                ["last_modified"] = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds()
            };
            if (previous != null)
            {
                parameters.Add("entity_type", "version");
                parameters.Add("previous", previous.Version!);
            }

            var ticket = saved?.Ticket ?? await JsonAsync(HttpMethod.Put, "file/upload", parameters, cancel);
            string id = DesktopLogin.Required(ticket, "session_id");
            bool canResume = saved != null;
            try
            {
                var checkpoint = saved ?? new UploadCheckpoint(ticket, 0, []);
                if (files != null && saved == null)
                {
                    files.StartUpload(ticket);
                    canResume = true;
                }

                using (var cipher = UploadCipher.FromTicket(ticket))
                {
                    try
                    {
                        await TransferAsync(input, size, checkpoint, files, cipher, cancel, activity);
                    }
                    finally
                    {
                        activity?.EndTransfer();
                    }
                }
            }
            catch (Exception failure) when (!canResume || RejectsUploadSession(failure))
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    try
                    {
                        await CancelUploadAsync(target, id, timeout.Token);
                        if (files != null)
                        {
                            File.Delete(files.UploadPath);
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException)
                    {
                        // Keep the original failure; the server expires abandoned sessions.
                        Log.Logger.WriteFailure(LogEventLevel.Debug, "Clean up abandoned upload", ex);
                    }
                }

                throw;
            }
        }
    }

    private static bool RejectsUploadSession(Exception failure)
    {
        return failure is InvalidDataException or NotSupportedException || failure.Data["CloudreveCode"] is 40005 or 40011 or 40012 or 40030 or 40075 ||
               failure is HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                   HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Conflict or HttpStatusCode.RequestedRangeNotSatisfiable };
    }

    internal async Task CancelUploadAsync(string target, string id, CancellationToken cancel)
    {
        try
        {
            var current = await StatAsync(target, cancel);
            if (current.UploadSessionId != id || !PairInventory.SameUri(target, current.Uri))
            {
                return;
            }

            await JsonAsync(HttpMethod.Delete, "file/upload", new { id, uri = target }, cancel);
        }
        catch (Exception ex) when (ex.Data["CloudreveCode"] is 404 or 40011 or 40044 || ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
        {
            // The server already discarded this session.
        }
    }

    private async Task TransferAsync(FileStream input, long size, UploadCheckpoint checkpoint, TransferFiles? files, UploadCipher? cipher,
        CancellationToken cancel, SyncActivity? activity)
    {
        var ticket = checkpoint.Ticket;
        string id = checkpoint.SessionId;
        string kind = checkpoint.StorageKind;

        string[] urls = ticket.TryGetProperty("upload_urls", out var addresses) && addresses.ValueKind == JsonValueKind.Array
            ? addresses.EnumerateArray().Select(address => address.GetString() ?? "").ToArray()
            : [];
        long chunkSize = ticket.GetProperty("chunk_size").GetInt64();
        if (chunkSize < 0)
        {
            throw new InvalidDataException(UiText.Get("站点返回了无效的分片大小。"));
        }

        long step = chunkSize == 0 || kind == "upyun" ? Math.Max(1, size) : chunkSize;
        long count = size == 0 ? 1 : (size - 1) / step + 1;
        if (count > int.MaxValue || (kind is "s3" or "ks3" or "cos" or "obs" or "oss" && urls.Length != count))
        {
            throw new InvalidDataException(UiText.Get("上传分片与站点返回的地址不匹配。"));
        }

        bool needsTags = kind is "s3" or "cos" or "ks3" or "obs" or "qiniu";
        if (checkpoint.NextPart > count || checkpoint.Tags.Count != (needsTags ? checkpoint.NextPart : 0) || checkpoint.Merged && checkpoint.NextPart != count)
        {
            throw new InvalidDataException(UiText.Get("上传分片断点无效。"));
        }

        long initialOffset = checkpoint.NextPart == count ? size : checkpoint.NextPart * step;
        activity?.BeginTransfer(UiText.Get("上传"), size, initialOffset);
        for (int i = checkpoint.NextPart; i < count; i++)
        {
            long offset = i * step;
            string? tag = await UploadPartAsync(input, size, ticket, id, kind, urls, i, offset, Math.Min(step, size - offset), cipher, activity, cancel);
            if (tag != null)
            {
                checkpoint.Tags.Add(tag);
            }

            checkpoint = checkpoint with { NextPart = i + 1 };
            if (files != null)
            {
                files.ConfirmPart(checkpoint.NextPart, tag);
            }
        }

        if (!checkpoint.Merged && kind is ("s3" or "cos" or "ks3" or "obs" or "oss" or "qiniu"))
        {
            await MergePartsAsync(kind, ticket, urls, checkpoint.Tags, cancel);
            if (files != null)
            {
                files.ConfirmPart(checkpoint.NextPart, null, merged: true);
            }
        }

        if (kind is "s3" or "ks3" or "cos" or "onedrive")
        {
            string secret = DesktopLogin.Required(ticket, "callback_secret");
            await JsonAsync(kind == "onedrive" ? HttpMethod.Post : HttpMethod.Get,
                $"callback/{kind}/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(secret)}", null, cancel);
        }
    }

    private async Task<string?> UploadPartAsync(FileStream input, long size, JsonElement ticket, string id, string kind, string[] urls, int i,
        long offset, long length, UploadCipher? cipher, SyncActivity? activity, CancellationToken cancel)
    {
        if (kind is "local" or "remote" or "load_balance" && (urls.Length == 0 || string.IsNullOrEmpty(urls[0]) || urls[0].StartsWith('/')))
        {
            using (var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiUri($"file/upload/{Uri.EscapeDataString(id)}/{i}"))
                       { Content = new FileSliceContent(input.SafeFileHandle, offset, length, cipher, activity) }, cancel))
            {
                response.EnsureSuccessStatusCode();
                DesktopLogin.Envelope(await response.Content.ReadFromJsonAsync<JsonElement>(cancel));
            }

            return null;
        }

        if (urls.Length == 0)
        {
            throw new InvalidDataException(UiText.Get("站点未返回上传地址。"));
        }

        using (var request = new HttpRequestMessage())
        {
            request.Content = new FileSliceContent(input.SafeFileHandle, offset, length, cipher, activity);
            request.RequestUri = StorageUri(urls[0]);
            switch (kind)
            {
                case "local":
                case "remote":
                case "load_balance":
                    var slave = new UriBuilder(request.RequestUri);
                    slave.Query = (slave.Query.Length > 1 ? slave.Query[1..] + "&" : "") + "chunk=" + i;
                    request.RequestUri = slave.Uri;
                    request.Method = HttpMethod.Post;
                    request.Headers.Add("Authorization", DesktopLogin.Required(ticket, "credential"));
                    break;
                case "s3":
                case "cos":
                case "ks3":
                case "obs":
                case "oss":
                    request.Method = HttpMethod.Put;
                    request.RequestUri = StorageUri(urls[i]);
                    break;
                case "onedrive" when size > 0:
                    request.Method = HttpMethod.Put;
                    request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, size);
                    break;
                case "qiniu":
                    request.Method = HttpMethod.Put;
                    var part = new UriBuilder(request.RequestUri);
                    part.Path = part.Path.TrimEnd('/') + "/" + (i + 1);
                    request.RequestUri = part.Uri;
                    request.Headers.Authorization = new AuthenticationHeaderValue("UpToken", DesktopLogin.Required(ticket, "credential"));
                    break;
                case "upyun":
                    request.Method = HttpMethod.Post;
                    var form = new MultipartFormDataContent();
                    form.Add(new StringContent(DesktopLogin.Required(ticket, "upload_policy")), "policy");
                    form.Add(new StringContent(DesktopLogin.Required(ticket, "credential")), "authorization");
                    form.Add(request.Content, "file", "file");
                    request.Content = form;
                    break;
                default:
                    throw new NotSupportedException(UiText.Format($"该文件不支持使用 {kind} 策略上传。"));
            }

            using (var response = await storage.SendAsync(request, cancel))
            {
                response.EnsureSuccessStatusCode();
                if (kind is "s3" or "cos" or "ks3" or "obs")
                {
                    return response.Headers.ETag?.Tag.Trim('"') ?? throw new InvalidDataException(UiText.Get("存储服务缺少分片 ETag。"));
                }
                else if (kind == "qiniu")
                {
                    return DesktopLogin.Required(await response.Content.ReadFromJsonAsync<JsonElement>(cancel), "etag");
                }
                else if (kind is "local" or "remote" or "load_balance")
                {
                    DesktopLogin.Envelope(await response.Content.ReadFromJsonAsync<JsonElement>(cancel));
                }
            }
        }

        return null;
    }

    private Uri StorageUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && new Uri(session.Site).Scheme == "http"))
        {
            throw new InvalidDataException(UiText.Get("不安全或无效的存储地址。"));
        }

        return uri;
    }

    private async Task MergePartsAsync(string kind, JsonElement ticket, string[] urls, List<string> tags, CancellationToken cancel)
    {
        Uri destination = StorageUri(kind == "qiniu" ? urls[0] : DesktopLogin.Required(ticket, "completeURL"));
        using (var request = new HttpRequestMessage(HttpMethod.Post, destination))
        {
            if (kind == "qiniu")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("UpToken", DesktopLogin.Required(ticket, "credential"));
                request.Content = JsonContent.Create(new { parts = tags.Select((etag, index) => new { etag, partNumber = index + 1 }) });
            }
            else
            {
                var xml = new XElement("CompleteMultipartUpload", tags.Select((tag, index) => new XElement("Part", new XElement("PartNumber", index + 1), new XElement("ETag", tag))));
                request.Content = new StringContent(kind == "oss" ? "" : xml.ToString(SaveOptions.DisableFormatting));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (kind == "oss")
                {
                    request.Headers.Add("x-oss-forbid-overwrite", "true");
                    request.Headers.Add("x-oss-complete-all", "yes");
                }
                else if (kind == "cos")
                {
                    request.Headers.Add("x-cos-forbid-overwrite", "true");
                }
            }

            using (var response = await storage.SendAsync(request, cancel))
            {
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync(cancel);
                if (body.AsSpan().TrimStart().StartsWith("<") && XElement.Parse(body).Name.LocalName == "Error")
                {
                    throw new IOException(UiText.Get("存储服务未能合并分片。"));
                }
            }
        }
    }
}
