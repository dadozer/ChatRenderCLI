using System.Buffers;
using System.Collections.Concurrent;
using SkiaSharp;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderCore.TwitchObjects.Api;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore
{
    public static partial class TwitchHelper
    {
        private static readonly HttpClient httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private static readonly JsonSerializerOptions JsonSerializerOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowTrailingCommas = true
        };

        private static readonly string[] BttvZeroWidth = ["SoSnowy", "IceCold", "SantaHat", "TopHat", "ReinDeer", "CandyCane", "cvMask", "cvHazmat"];
        // Giphy has no bulk search, so one page fetch per title. Concurrent, but not so many that Giphy throttles us.
        private const int GIPHY_SEARCH_THREADS = 8;

        // Hardware-adaptive I/O and codec header concurrency: scales from 4 threads on laptops up to 16 threads on 8C/16T+ desktops.
        private static int AssetFetchParallelism => Math.Clamp(Environment.ProcessorCount, 4, 16);

        private static readonly SearchValues<char> WhiteSpaceChars = SearchValues.Create("\t\n\v\f\r\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000");

        private const string SEVEN_TV_PROXY_HOST = "7tv-imageproxy.twitcharchives.workers.dev";

        public static async Task<GqlVideoResponse> GetVideoInfo(long videoId)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{video(id:\\\"" + videoId + "\\\"){title,thumbnailURLs(height:180,width:320),createdAt,lengthSeconds,owner{id,displayName,login},viewCount,game{id,displayName,boxArtURL},description,status}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GqlVideoResponse>();
        }

        public static async Task<GqlVideoTokenResponse> GetVideoToken(long videoId, string authToken)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"operationName\":\"PlaybackAccessToken_Template\",\"query\":\"query PlaybackAccessToken_Template($login: String!, $isLive: Boolean!, $vodID: ID!, $isVod: Boolean!, $playerType: String!) {  streamPlaybackAccessToken(channelName: $login, params: {platform: \\\"web\\\", playerBackend: \\\"mediaplayer\\\", playerType: $playerType}) @include(if: $isLive) {    value    signature    __typename  }  videoPlaybackAccessToken(id: $vodID, params: {platform: \\\"web\\\", playerBackend: \\\"mediaplayer\\\", playerType: $playerType}) @include(if: $isVod) {    value    signature    __typename  }}\",\"variables\":{\"isLive\":false,\"login\":\"\",\"isVod\":true,\"vodID\":\"" + videoId + "\",\"playerType\":\"embed\"}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            if (!string.IsNullOrWhiteSpace(authToken))
                request.Headers.Add("Authorization", $"OAuth {authToken}");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GqlVideoTokenResponse>();
        }

        public static async Task<string> GetVideoPlaylist(long videoId, string token, string sig)
        {
            HttpRequestMessage request;
            HttpResponseMessage response;
            try
            {
                request = new HttpRequestMessage()
                {
                    RequestUri = new Uri($"https://usher.ttvnw.net/vod/v2/{videoId}.m3u8?sig={sig}&token={token}&allow_source=true&allow_audio_only=true&include_unavailable=true&platform=web&player_backend=mediaplayer&playlist_include_framerate=true&supported_codecs=av1,h265,h264"),
                    Method = HttpMethod.Get
                };
                response = await httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                if (IsAuthException(ex))
                {
                    request = new HttpRequestMessage()
                    {
                        RequestUri = new Uri($"https://twitch-downloader-proxy.twitcharchives.workers.dev/v2/{videoId}.m3u8?sig={sig}&token={token}&allow_source=true&allow_audio_only=true&include_unavailable=true&platform=web&player_backend=mediaplayer&playlist_include_framerate=true&supported_codecs=av1,h265,h264"),
                        Method = HttpMethod.Get
                    };
                    response = await httpClient.SendAsync(request);
                }
                else
                {
                    throw;
                }
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Twitch returns 403 Forbidden for (some? all?) sub-only VODs when correct authorization is not provided
                var forbiddenResponse = await response.Content.ReadAsStringAsync();
                if (forbiddenResponse.Contains("vod_manifest_restricted") || forbiddenResponse.Contains("unauthorized_entitlements"))
                {
                    // Return the error string so the caller can choose their error strategy
                    // TODO: We may want to eventually return all 403 responses so the error messages can be parsed and/or logged since more potential errors exist
                    return forbiddenResponse;
                }
            }

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }

        private static bool IsAuthException(Exception ex)
        {
            while (ex != null)
            {
                if (ex is System.Security.Authentication.AuthenticationException)
                {
                    return true;
                }
                ex = ex.InnerException;
            }
            return false;
        }

        public static async Task<GqlClipResponse> GetClipInfo(object clipId)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{clip(slug:\\\"" + clipId + "\\\"){title,thumbnailURL,createdAt,curator{id,displayName,login},durationSeconds,broadcaster{id,displayName,login},videoOffsetSeconds,video{id},viewCount,game{id,displayName,boxArtURL}}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GqlClipResponse>();
        }

        public static async Task<GqlClipTokenResponse> GetClipLinks(string clipId)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"operationName\":\"VideoAccessToken_Clip\",\"variables\":{\"slug\":\"" + clipId + "\"},\"extensions\":{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"36b89d2507fce29e5ca551df756d27c1cfe079e2609642b4390aa4c35796eb11\"}}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var gqlClipTokenResponses = await response.Content.ReadFromJsonAsync<GqlClipTokenResponse>();
            if (gqlClipTokenResponses.data.clip.videoQualities is { Length: > 0 })
            {
                Array.Sort(gqlClipTokenResponses.data.clip.videoQualities, new ClipQualityComparer());
            }

            return gqlClipTokenResponses;
        }

        public static async Task<GqlShareClipRenderStatusResponse> GetShareClipRenderStatus(string clipId)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"operationName\":\"ShareClipRenderStatus\",\"variables\":{\"slug\":\"" + clipId + "\"},\"extensions\":{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"66038e29eb00d8fd115b0ce1a1382dd9d41168739b08bc87dc042af6a730541f\"}}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var renderStatusResponse = await response.Content.ReadFromJsonAsync<GqlShareClipRenderStatusResponse>();
            if (renderStatusResponse.data.clip.assets is not null)
            {
                foreach (var asset in renderStatusResponse.data.clip.assets)
                {
                    Array.Sort(asset.videoQualities, new ClipVideoQualityComparer());
                }
            }

            return renderStatusResponse;
        }

        public static async Task<GqlVideoSearchResponse> GetGqlVideos(string channelName, string cursor = "", int limit = 50, string type = "")
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{user(login:\\\"" + channelName + "\\\"){videos(first: " + limit + "" + (cursor == "" ? "" : ",after:\\\"" + cursor + "\\\"") + (type == "" ? "" : ",type:" + type) + ") { edges { node { title, id, lengthSeconds, previewThumbnailURL(height: 180, width: 320), createdAt, viewCount, game { id, displayName } }, cursor }, pageInfo { hasNextPage, hasPreviousPage }, totalCount }}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kd1unb4b3q4t58fwlpcbzcbnm76a8fp");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GqlVideoSearchResponse>();
        }

        public static async Task<GqlClipSearchResponse> GetGqlClips(string channelName, string period = "LAST_WEEK", string cursor = "", int limit = 50)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{user(login:\\\"" + channelName + "\\\"){clips(first: " + limit + (cursor == "" ? "" : ", after: \\\"" + cursor + "\\\"") + ", criteria: { period: " + period + " }) {  edges { cursor, node { id, slug, title, createdAt, curator, { id, displayName }, durationSeconds, thumbnailURL, viewCount, game { id, displayName } } }, pageInfo { hasNextPage, hasPreviousPage } }}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kd1unb4b3q4t58fwlpcbzcbnm76a8fp");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GqlClipSearchResponse>();
        }

        public static async Task<EmoteResponse> GetThirdPartyEmotesMetadata(int streamerId, bool getBttv, bool getFfz, bool getStv, bool allowUnlistedEmotes, string cacheFolder, bool offline, ITaskLogger logger,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EmoteResponse emoteResponse = new();

            Task bttvTask = getBttv ? LoadBttvAsync() : Task.CompletedTask;
            Task ffzTask = getFfz ? LoadFfzAsync() : Task.CompletedTask;
            Task stvTask = getStv ? LoadStvAsync() : Task.CompletedTask;

            await Task.WhenAll(bttvTask, ffzTask, stvTask);
            return emoteResponse;

            async Task LoadBttvAsync()
            {
                try
                {
                    if (!offline)
                    {
                        emoteResponse.BTTV = await GetBttvEmotesMetadata(streamerId, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProviderException(ex, "BetterTTV", logger);
                }

                var bttvDir = new DirectoryInfo(Path.Combine(cacheFolder, "bttv"));
                await RefreshOrLoadProviderMetadata(emoteResponse.BTTV, bttvDir, "BetterTTV", streamerId, offline, logger, cancellationToken);
            }

            async Task LoadFfzAsync()
            {
                try
                {
                    if (!offline)
                    {
                        emoteResponse.FFZ = await GetFfzEmotesMetadata(streamerId, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProviderException(ex, "FFZ", logger);
                }

                var ffzDir = new DirectoryInfo(Path.Combine(cacheFolder, "ffz"));
                await RefreshOrLoadProviderMetadata(emoteResponse.FFZ, ffzDir, "FFZ", streamerId, offline, logger, cancellationToken);
            }

            async Task LoadStvAsync()
            {
                try
                {
                    if (!offline)
                    {
                        emoteResponse.STV = await GetStvEmotesMetadata(streamerId, allowUnlistedEmotes, logger, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProviderException(ex, "7TV", logger);
                }

                var stvDir = new DirectoryInfo(Path.Combine(cacheFolder, "stv"));
                await RefreshOrLoadProviderMetadata(emoteResponse.STV, stvDir, "7TV", streamerId, offline, logger, cancellationToken);
            }

            static void LogProviderException(Exception ex, string providerName, ITaskLogger logger)
            {
                var message = ex switch
                {
                    HttpRequestException { StatusCode: not null } hre => $"{providerName} returned {(int)hre.StatusCode}: {hre.StatusCode}.",
                    HttpRequestException { InnerException: not null } when ex.Message.Contains("SSL") => $"{ex.Message.Remove(ex.Message.Length - 1)}: {ex.InnerException.Message}",
                    TaskCanceledException when ex.Message.Contains("HttpClient.Timeout") => $"{providerName} timed out.",
                    _ => ex.Message
                };

                logger.LogError($"{message} {providerName} emotes may not be present for this session.");
            }
        }

        private static async Task<List<EmoteResponseItem>> GetBttvEmotesMetadata(int streamerId, CancellationToken cancellationToken)
        {
            var globalEmoteRequest = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.betterttv.net/3/cached/emotes/global", UriKind.Absolute));
            var channelEmoteRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.betterttv.net/3/cached/users/twitch/{streamerId}", UriKind.Absolute));

            using var globalEmoteResponseTask = httpClient.SendAsync(globalEmoteRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            using var channelEmoteResponseTask = httpClient.SendAsync(channelEmoteRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            using var globalEmoteResponse = await globalEmoteResponseTask;
            globalEmoteResponse.EnsureSuccessStatusCode();
            var BTTV = await globalEmoteResponse.Content.ReadFromJsonAsync<List<BTTVEmote>>(cancellationToken: cancellationToken);

            // Channel might not have BTTV emotes
            try
            {
                using var channelEmoteResponse = await channelEmoteResponseTask;
                channelEmoteResponse.EnsureSuccessStatusCode();

                var bttvChannel = await channelEmoteResponse.Content.ReadFromJsonAsync<BTTVChannelEmoteResponse>(cancellationToken: cancellationToken);
                BTTV.AddRange(bttvChannel.channelEmotes);
                BTTV.AddRange(bttvChannel.sharedEmotes);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }

            var returnList = new List<EmoteResponseItem>(BTTV.Count);
            foreach (var emote in BTTV)
            {
                string id = emote.id;
                string name = emote.code;
                string mime = emote.imageType;
                string url = $"https://cdn.betterttv.net/emote/{id}/[scale]x";
                returnList.Add(new EmoteResponseItem() { Id = id, Code = name, ImageType = mime, ImageUrl = url, IsZeroWidth = BttvZeroWidth.Contains(name) });
            }

            return returnList;
        }

        private static async Task<List<EmoteResponseItem>> GetFfzEmotesMetadata(int streamerId, CancellationToken cancellationToken)
        {
            var globalEmoteRequest = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.betterttv.net/3/cached/frankerfacez/emotes/global", UriKind.Absolute));
            var channelEmoteRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.betterttv.net/3/cached/frankerfacez/users/twitch/{streamerId}", UriKind.Absolute));

            using var globalEmoteResponseTask = httpClient.SendAsync(globalEmoteRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            using var channelEmoteResponseTask = httpClient.SendAsync(channelEmoteRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            using var globalEmoteResponse = await globalEmoteResponseTask;
            globalEmoteResponse.EnsureSuccessStatusCode();
            var FFZ = await globalEmoteResponse.Content.ReadFromJsonAsync<List<FFZEmote>>(cancellationToken: cancellationToken);

            // Channel might not have FFZ emotes
            try
            {
                using var channelEmoteResponse = await channelEmoteResponseTask;
                channelEmoteResponse.EnsureSuccessStatusCode();

                var channelEmotes = await channelEmoteResponse.Content.ReadFromJsonAsync<List<FFZEmote>>(cancellationToken: cancellationToken);
                FFZ.AddRange(channelEmotes);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }

            var returnList = new List<EmoteResponseItem>(FFZ.Count);
            foreach (var emote in FFZ)
            {
                string id = emote.id.ToString();
                string name = emote.code;
                string mime = emote.imageType;
                string url = emote.animated
                    ? $"https://cdn.betterttv.net/frankerfacez_emote/{id}/animated/[scale]"
                    : $"https://cdn.betterttv.net/frankerfacez_emote/{id}/[scale]";
                returnList.Add(new EmoteResponseItem() { Id = id, Code = name, ImageType = mime, ImageUrl = url });
            }

            return returnList;
        }

        private static async Task<List<EmoteResponseItem>> GetStvEmotesMetadata(int streamerId, bool allowUnlistedEmotes, ITaskLogger logger, CancellationToken cancellationToken)
        {
            var globalEmoteRequest = new HttpRequestMessage(HttpMethod.Get, new Uri("https://7tv.io/v3/emote-sets/global", UriKind.Absolute));
            var streamerInfoRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://7tv.io/v3/users/twitch/{streamerId}", UriKind.Absolute));

            using var globalEmoteResponseTask = httpClient.SendAsync(globalEmoteRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            using var streamerInfoResponseTask = httpClient.SendAsync(streamerInfoRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            using var globalEmoteResponse = await globalEmoteResponseTask;
            globalEmoteResponse.EnsureSuccessStatusCode();
            var globalEmoteObject = await globalEmoteResponse.Content.ReadFromJsonAsync<STVGlobalEmoteResponse>(cancellationToken: cancellationToken);
            var stvEmotes = globalEmoteObject.emotes;

            // Channel might not be registered on 7tv
            try
            {
                using var streamerInfoResponse = await streamerInfoResponseTask;
                streamerInfoResponse.EnsureSuccessStatusCode();

                var streamerInfoObject = await streamerInfoResponse.Content.ReadFromJsonAsync<STVChannelEmoteResponse>(cancellationToken: cancellationToken);
                var streamerEmoteRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://7tv.io/v3/emote-sets/{streamerInfoObject.emote_set_id}", UriKind.Absolute));
                using var streamerEmoteResponse = await httpClient.SendAsync(streamerEmoteRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                streamerEmoteResponse.EnsureSuccessStatusCode();

                var streamerEmoteObject = await streamerEmoteResponse.Content.ReadFromJsonAsync<STVEmoteSet>(cancellationToken: cancellationToken);
                // Channel might not have emotes setup
                if (streamerEmoteObject?.emotes != null)
                {
                    stvEmotes.AddRange(streamerEmoteObject.emotes);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }

            var returnList = new List<EmoteResponseItem>(stvEmotes.Count);
            foreach (var stvEmote in stvEmotes)
            {
                STVData emoteData = stvEmote.data;
                STVHost emoteHost = emoteData.host;
                List<STVFile> emoteFiles = emoteHost.files;
                if (emoteFiles.Count == 0) // Sometimes there are no hosted files for the emote
                {
                    logger.LogVerbose($"{stvEmote.name} has no hosted files, skipping.");
                    continue;
                }

                // TODO: Allow and prefer avif when SkiaSharp properly supports it
                string emoteFormat = "";
                foreach (var fileItem in emoteFiles)
                {
                    if (fileItem.format.Equals("webp", StringComparison.OrdinalIgnoreCase)) // Is the emote offered in webp?
                    {
                        emoteFormat = "webp";
                        break;
                    }
                }

                if (emoteFormat is "") // SkiaSharp does not yet properly support avif, only allow webp - see issue lay295#426
                {
                    logger.LogVerbose($"{stvEmote.name} is not available in webp, skipping. Available formats: {string.Join(", ", emoteFiles.Select(x => x.format))}");
                    continue;
                }

                var emoteFlags = emoteData.flags;
                if ((emoteFlags & StvEmoteFlags.ContentTwitchDisallowed) != 0 || (emoteFlags & StvEmoteFlags.Private) != 0)
                {
                    logger.LogVerbose($"{stvEmote.name} has disallowed flags, skipping. Flags: {emoteFlags}.");
                    continue;
                }

                var emoteUrl = $"https:{emoteHost.url}/[scale]x.{emoteFormat}";
                var emoteResponse = new EmoteResponseItem { Id = stvEmote.id, Code = stvEmote.name, ImageType = emoteFormat, ImageUrl = emoteUrl };
                if ((emoteFlags & StvEmoteFlags.ZeroWidth) != 0)
                {
                    emoteResponse.IsZeroWidth = true;
                }

                if (allowUnlistedEmotes || emoteData.listed)
                {
                    returnList.Add(emoteResponse);
                }
            }

            return returnList;
        }

        /// <summary>
        /// Persists fresh metadata to disk so future sessions can fall back to it when the API is temporarily unavailable.
        /// </summary>
        private static async Task RefreshOrLoadProviderMetadata(List<EmoteResponseItem> emotes, DirectoryInfo cacheFolder, string providerName, int streamerId, bool offline, ITaskLogger logger, CancellationToken ct)
        {
            var cacheFile = new FileInfo(Path.Combine(cacheFolder.FullName, $"emotes_{streamerId}.json.gz"));

            if (emotes is { Count: > 0 })
            {
                // Persist fresh metadata so future sessions can fall back to it.
                try
                {
                    if (!cacheFolder.Exists)
                        CreateDirectory(cacheFolder.FullName);

                    await using var fs = cacheFile.OpenWrite();
                    await using var gs = new GZipStream(fs, CompressionLevel.SmallestSize);
                    await JsonSerializer.SerializeAsync(gs, emotes, JsonSerializerOptions, ct);
                }
                catch { /* best-effort - never block rendering on a metadata write failure */ }

                return;
            }

            // API failed - try falling back to a previously cached emote list.
            try
            {
                if (!cacheFile.Exists)
                    return;

                var timeSinceLastWrite = DateTime.UtcNow - cacheFile.LastWriteTimeUtc;
                var maxAge = TimeSpan.FromHours(48);
                if (timeSinceLastWrite > maxAge)
                    return;

                await using var fs = cacheFile.OpenRead();
                await using var gs = new GZipStream(fs, CompressionMode.Decompress);
                var cached = await JsonSerializer.DeserializeAsync<EmoteResponseItem[]>(gs, JsonSerializerOptions, ct);
                if (cached is { Length: > 0 })
                {
                    var ageHours = timeSinceLastWrite.TotalHours;
                    if (offline)
                        logger.LogInfo($"Using cached {providerName} emote list from {ageHours:F1}h ago ({cached.Length} emotes).");
                    else
                        logger.LogInfo($"{providerName} API unavailable. Using cached emote list from {ageHours:F1}h ago ({cached.Length} emotes).");
                    emotes.AddRange(cached);
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to load cached {providerName} emote metadata: {ex.Message}");
            }
        }

        /// <summary>
        /// Tokenizes all comment bodies in a single O(Comments) pass into a HashSet of whitespace-delimited tokens,
        /// replacing O(Emotes * Comments) regex evaluations.
        /// </summary>
        private static HashSet<string> BuildCommentWordSet([AllowNull] List<Comment> comments)
        {
            if (comments is null)
            {
                return null;
            }

            var wordSet = new HashSet<string>(StringComparer.Ordinal);
            var lookup = wordSet.GetAlternateLookup<ReadOnlySpan<char>>();

            foreach (var comment in comments)
            {
                var body = comment.message?.body;
                if (string.IsNullOrEmpty(body))
                {
                    continue;
                }

                var span = body.AsSpan();
                foreach (var range in span.SplitAny(WhiteSpaceChars))
                {
                    var token = span[range];
                    if (!token.IsEmpty)
                    {
                        lookup.Add(token);
                    }
                }
            }

            return wordSet;
        }

        public static async Task<List<TwitchEmote>> GetThirdPartyEmotes(List<Comment> comments, int streamerId, string cacheFolder, ITaskLogger logger, EmbeddedData embeddedData = null, bool bttv = true, bool ffz = true, bool stv = true, bool allowUnlistedEmotes = true, bool offline = false, CancellationToken cancellationToken = default)
        {
            // No 3rd party emotes are wanted
            if (!bttv && !ffz && !stv)
            {
                return new List<TwitchEmote>();
            }

            var emotes = new Dictionary<string, TwitchEmote>();

            // Load our embedded data from file
            if (embeddedData?.thirdParty != null)
            {
                emotes.EnsureCapacity(embeddedData.thirdParty.Count);
                foreach (EmbedEmoteData emoteData in embeddedData.thirdParty)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var newEmote = new TwitchEmote(emoteData.data, null, EmoteProvider.ThirdParty, emoteData.imageScale, emoteData.id, emoteData.name, emoteData.isZeroWidth.GetValueOrDefault());

                        if (!emotes.TryAdd(emoteData.name, newEmote))
                        {
                            newEmote.Dispose();
                            logger.LogVerbose($"Tried to add duplicate emote from embedded data: {emoteData.name}.");
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogVerbose($"An exception occurred while loading embedded emote '{emoteData.name}': {e.Message}.");
                    }
                }
            }

            var metadataTask = GetThirdPartyEmotesMetadata(streamerId, bttv, ffz, stv, allowUnlistedEmotes, cacheFolder, offline, logger, cancellationToken);
            var commentWords = BuildCommentWordSet(comments);
            EmoteResponse emoteDataResponse = await metadataTask;

            DirectoryInfo bttvFolder = new DirectoryInfo(Path.Combine(cacheFolder, "bttv"));
            DirectoryInfo ffzFolder = new DirectoryInfo(Path.Combine(cacheFolder, "ffz"));
            DirectoryInfo stvFolder = new DirectoryInfo(Path.Combine(cacheFolder, "stv"));

            if (bttv)
            {
                try
                {
                    await FetchEmoteImages(comments, commentWords, emoteDataResponse.BTTV, emotes, bttvFolder, "BetterTTV", offline, logger, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProviderException(ex, "BetterTTV", logger);
                }
            }

            if (ffz)
            {
                try
                {
                    await FetchEmoteImages(comments, commentWords, emoteDataResponse.FFZ, emotes, ffzFolder, "FFZ", offline, logger, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProviderException(ex, "FFZ", logger);
                }
            }

            if (stv)
            {
                try
                {
                    await FetchEmoteImages(comments, commentWords, emoteDataResponse.STV, emotes, stvFolder, "7TV", offline, logger, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProviderException(ex, "7TV", logger);
                }
            }

            return emotes.Values.ToList();

            static async Task FetchEmoteImages([AllowNull] List<Comment> comments, [AllowNull] HashSet<string> commentWords, IEnumerable<EmoteResponseItem> emoteResponse, Dictionary<string, TwitchEmote> emotes,
                DirectoryInfo cacheFolder, string providerName, bool offline, ITaskLogger logger, CancellationToken cancellationToken)
            {
                if (!cacheFolder.Exists)
                    cacheFolder = CreateDirectory(cacheFolder.FullName);

                var candidates = new List<EmoteResponseItem>();
                var seenCodes = new HashSet<string>(StringComparer.Ordinal);

                foreach (var emote in emoteResponse)
                {
                    if (emotes.ContainsKey(emote.Code) || !seenCodes.Add(emote.Code))
                    {
                        continue;
                    }

                    if (comments is null)
                    {
                        candidates.Add(emote);
                        continue;
                    }

                    if (!emote.Code.AsSpan().ContainsAny(WhiteSpaceChars))
                    {
                        if (commentWords.Contains(emote.Code))
                        {
                            candidates.Add(emote);
                        }
                    }
                    else
                    {
                        var regex = new Regex($@"(?<=^|\s){Regex.Escape(emote.Code)}(?=$|\s)");
                        if (comments.Any(comment => regex.IsMatch(comment.message.body)))
                        {
                            candidates.Add(emote);
                        }
                    }
                }

                if (candidates.Count == 0)
                {
                    return;
                }

                var loadedResults = new (TwitchEmote Emote, bool WasFromCache)[candidates.Count];

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, candidates.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = AssetFetchParallelism, CancellationToken = cancellationToken },
                    async (index, ct) =>
                    {
                        var emote = candidates[index];
                        var emoteUrl = emote.ImageUrl.Replace("[scale]", "2");

                        try
                        {
                            var (bytes, codec, wasInCache) = await GetImageInternalAsync(cacheFolder, emoteUrl, emote.Id, 2, emote.ImageType, offline, logger, ct);
                            if (bytes is null)
                            {
                                return;
                            }

                            var newEmote = new TwitchEmote(bytes, codec, EmoteProvider.ThirdParty, 2, emote.Id, emote.Code, emote.IsZeroWidth);
                            loadedResults[index] = (newEmote, wasInCache);
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                        {
                            logger.LogWarning($"Got {(int)ex.StatusCode}: {ex.StatusCode} when fetching {emote.Code} ({emoteUrl}).");
                        }
                    });

                int fromCache = 0, fromDownload = 0;
                for (int i = 0; i < loadedResults.Length; i++)
                {
                    var (newEmote, wasInCache) = loadedResults[i];
                    if (newEmote is null)
                    {
                        continue;
                    }

                    if (!emotes.TryAdd(newEmote.Name, newEmote))
                    {
                        newEmote.Dispose();
                    }
                    else if (wasInCache)
                    {
                        fromCache++;
                    }
                    else
                    {
                        fromDownload++;
                    }
                }

                var total = fromCache + fromDownload;
                if (total > 0)
                {
                    logger.LogInfo($"Loaded {total} {providerName} emotes from cache/download - {fromCache} cached, {fromDownload} newly downloaded.");
                }
            }

            static void LogProviderException(Exception ex, string providerName, ITaskLogger logger)
            {
                var message = ex switch
                {
                    HttpRequestException { StatusCode: not null } hre => $"{providerName} returned {(int)hre.StatusCode}: {hre.StatusCode}.",
                    HttpRequestException { InnerException: not null } when ex.Message.Contains("SSL") => $"{ex.Message.Remove(ex.Message.Length - 1)}: {ex.InnerException.Message}",
                    TaskCanceledException when ex.Message.Contains("HttpClient.Timeout") => $"{providerName} timed out.",
                    _ => ex.Message
                };

                logger.LogError($"{message} Some {providerName} emotes may not be present for this session.");
            }
        }

        /// <summary>Resolves the Giphy titles posted across <paramref name="comments"/> to their GIF, without downloading any images.</summary>
        /// <remarks>Prefers what is already recorded in <paramref name="embeddedData"/>, then the on disk resolution cache, then a Giphy search.</remarks>
        public static async Task<Dictionary<string, GiphyResolver.GiphyGif>> ResolveGiphyGifs(List<Comment> comments, string cacheFolder, ITaskLogger logger,
            EmbeddedData embeddedData = null, bool offline = false, Action<int> reportProgress = null, CancellationToken cancellationToken = default)
        {
            var gifFolder = new DirectoryInfo(Path.Combine(cacheFolder, "giphy"));
            if (!gifFolder.Exists)
                gifFolder = CreateDirectory(gifFolder.FullName);

            // Which GIF a title refers to does not change, so resolutions are remembered between runs. Failures are
            // remembered too, as a null, so a re-run does not search again for every title Giphy could not answer.
            var remembered = LoadResolvedGifs(gifFolder, logger);
            var resolutions = new Dictionary<string, GiphyResolver.GiphyGif>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<string>();

            foreach (var title in GiphyResolver.GetTitles(comments))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var known = embeddedData?.gifs?.FirstOrDefault(x => title.Equals(x.name, StringComparison.OrdinalIgnoreCase));
                var knownUrl = known?.url ?? (known?.id is null ? null : GiphyResolver.MediaUrl(known.id));
                if (knownUrl is not null)
                {
                    resolutions[title] = new GiphyResolver.GiphyGif(known.id, knownUrl, known.width, known.height);
                }
                else if (remembered.TryGetValue(title, out var cached))
                {
                    if (cached is not null)
                    {
                        resolutions[title] = cached.Value;
                    }
                }
                else
                {
                    pending.Add(title);
                }
            }

            // Searching Giphy is one page fetch per title and is by far the slow part, so they run concurrently.
            // A busy VOD can post well over a thousand distinct GIFs.
            if (pending.Count > 0 && !offline)
            {
                var searchResults = new ConcurrentDictionary<string, GiphyResolver.GiphyGif?>(StringComparer.OrdinalIgnoreCase);
                var completed = 0;

                await Parallel.ForEachAsync(pending,
                    new ParallelOptions { MaxDegreeOfParallelism = GIPHY_SEARCH_THREADS, CancellationToken = cancellationToken },
                    async (title, token) =>
                    {
                        try
                        {
                            searchResults[title] = await GiphyResolver.ResolveAsync(title, logger, token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogVerbose($"An exception occurred while searching Giphy for '{title}': {ex.Message}.");
                        }

                        reportProgress?.Invoke(Interlocked.Increment(ref completed) * 100 / pending.Count);
                    });

                foreach (var (title, result) in searchResults)
                {
                    remembered[title] = result;
                    if (result is not null)
                    {
                        resolutions[title] = result.Value;
                    }
                }

                SaveResolvedGifs(gifFolder, remembered, logger);
            }

            return resolutions;
        }

        /// <summary>Fetches the images of the Giphy GIFs posted across <paramref name="comments"/>, keyed by the Giphy title.</summary>
        /// <remarks>The GIFs come back undecoded, so that a caller only pays for the frames of the ones it actually draws.</remarks>
        public static async Task<List<TwitchEmote>> GetGiphyGifs(List<Comment> comments, string cacheFolder, ITaskLogger logger, EmbeddedData embeddedData = null, bool offline = false,
            Action<int> reportProgress = null, CancellationToken cancellationToken = default)
        {
            var gifs = new Dictionary<string, TwitchEmote>(StringComparer.OrdinalIgnoreCase);

            // Load our embedded GIFs
            foreach (var gifData in embeddedData?.gifs ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (gifData.data is not { Length: > 0 })
                {
                    continue;
                }

                try
                {
                    var embeddedGif = new TwitchEmote(gifData.data, null, EmoteProvider.Giphy, 1, gifData.id, gifData.name) { Url = gifData.url };
                    if (!gifs.TryAdd(gifData.name, embeddedGif))
                    {
                        embeddedGif.Dispose();
                        logger.LogVerbose($"Tried to add duplicate GIF from embedded data: {gifData.name}.");
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogVerbose($"An exception occurred while loading embedded GIF '{gifData.name}': {e.Message}.");
                }
            }

            // The loop below reports over every title, so letting the resolve report too would run the bar twice
            var resolutions = await ResolveGiphyGifs(comments, cacheFolder, logger, embeddedData, offline, null, cancellationToken);
            var gifFolder = new DirectoryInfo(Path.Combine(cacheFolder, "giphy"));
            if (!gifFolder.Exists)
                gifFolder = CreateDirectory(gifFolder.FullName);

            var titles = GiphyResolver.GetTitles(comments).ToList();
            int fromEmbed = 0, fromCache = 0, fromDownload = 0, unresolved = 0, processed = 0;

            var fetchIndices = new List<int>(titles.Count);
            for (int i = 0; i < titles.Count; i++)
            {
                var title = titles[i];
                if (gifs.ContainsKey(title))
                {
                    fromEmbed++;
                    reportProgress?.Invoke(Interlocked.Increment(ref processed) * 100 / titles.Count);
                    continue;
                }

                if (!resolutions.ContainsKey(title))
                {
                    unresolved++;
                    reportProgress?.Invoke(Interlocked.Increment(ref processed) * 100 / titles.Count);
                    continue;
                }

                fetchIndices.Add(i);
            }

            if (fetchIndices.Count > 0)
            {
                var fetchedResults = new (TwitchEmote Gif, bool WasInCache, bool Failed)[titles.Count];

                await Parallel.ForEachAsync(
                    fetchIndices,
                    new ParallelOptions { MaxDegreeOfParallelism = AssetFetchParallelism, CancellationToken = cancellationToken },
                    async (titleIndex, ct) =>
                    {
                        var title = titles[titleIndex];
                        var resolution = resolutions[title];

                        try
                        {
                            var (bytes, codec, wasInCache) = await GetImageInternalAsync(gifFolder, resolution.Url, resolution.Id, 1, "gif", offline, logger, ct);
                            if (bytes is null)
                            {
                                fetchedResults[titleIndex] = (null, false, true);
                                return;
                            }

                            // Constructing reads the header only, so this does not pay for the frames
                            var newGif = new TwitchEmote(bytes, codec, EmoteProvider.Giphy, 1, resolution.Id, title) { Url = resolution.Url };

                            // A url can outlive the GIF it points at: resolutions are cached between runs and archived into
                            // chat files, so this may be fetching one recorded long ago. Giphy answers those with a
                            // "content is not available" image under a 200, which only the size gives away.
                            if (resolution.Width > 0 && resolution.Height > 0 && (newGif.Width != resolution.Width || newGif.Height != resolution.Height))
                            {
                                logger.LogVerbose(
                                    $"Giphy served {newGif.Width}x{newGif.Height} for '{title}' but advertised {resolution.Width}x{resolution.Height}, discarding it.");
                                newGif.Dispose();
                                DeleteCachedImage(gifFolder, resolution.Id, 1, "gif", logger);
                                fetchedResults[titleIndex] = (null, false, true);
                                return;
                            }

                            fetchedResults[titleIndex] = (newGif, wasInCache, false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            fetchedResults[titleIndex] = (null, false, true);
                            logger.LogVerbose($"An exception occurred while fetching GIF '{title}': {ex.Message}.");
                        }
                        finally
                        {
                            reportProgress?.Invoke(Interlocked.Increment(ref processed) * 100 / titles.Count);
                        }
                    });

                foreach (var titleIndex in fetchIndices)
                {
                    var (newGif, wasInCache, failed) = fetchedResults[titleIndex];
                    if (failed || newGif is null)
                    {
                        unresolved++;
                        continue;
                    }

                    if (!gifs.TryAdd(titles[titleIndex], newGif))
                    {
                        newGif.Dispose();
                    }
                    else if (wasInCache)
                    {
                        fromCache++;
                    }
                    else
                    {
                        fromDownload++;
                    }
                }
            }

            if (titles.Count > 0)
            {
                logger.LogInfo(
                    $"Loaded {gifs.Count} of {titles.Count} chat GIFs from cache/download - {fromEmbed} embedded, {fromCache} cached, " +
                    $"{fromDownload} newly downloaded, {unresolved} unresolved.");
            }

            return gifs.Values.ToList();
        }

        // Remembers which GIF each title resolved to. Searching Giphy is the slow part and the answer does not change,
        // so a re-render of a chat with no embedded GIF data reuses this instead of searching for every title again.
        private static Dictionary<string, GiphyResolver.GiphyGif?> LoadResolvedGifs(DirectoryInfo cacheDir, ITaskLogger logger)
        {
            var resolved = new Dictionary<string, GiphyResolver.GiphyGif?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var file = new FileInfo(Path.Combine(cacheDir.FullName, "resolved.json.gz"));
                if (!file.Exists)
                {
                    return resolved;
                }

                using var fs = file.OpenRead();
                using var gs = new GZipStream(fs, CompressionMode.Decompress);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, GiphyResolver.GiphyGif?>>(gs, JsonSerializerOptions);
                foreach (var (title, gif) in loaded ?? [])
                {
                    resolved[title] = gif;
                }
            }
            catch (Exception e)
            {
                logger.LogVerbose($"Failed to read the Giphy resolution cache: {e.Message}");
            }

            return resolved;
        }

        private static void SaveResolvedGifs(DirectoryInfo cacheDir, Dictionary<string, GiphyResolver.GiphyGif?> resolved, ITaskLogger logger)
        {
            try
            {
                using var fs = File.Create(Path.Combine(cacheDir.FullName, "resolved.json.gz"));
                using var gs = new GZipStream(fs, CompressionLevel.SmallestSize);
                JsonSerializer.Serialize(gs, resolved, JsonSerializerOptions);
            }
            catch (Exception e)
            {
                logger.LogVerbose($"Failed to write the Giphy resolution cache: {e.Message}");
            }
        }

        // Removes a cached image so a rejected download is not reused next run
        private static void DeleteCachedImage(DirectoryInfo cacheDir, string imageId, int imageScale, string imageType, ITaskLogger logger)
        {
            try
            {
                var file = new FileInfo(Path.Combine(cacheDir.FullName, $"{imageId}_{imageScale}.{imageType}"));
                if (file.Exists)
                {
                    file.Delete();
                }
            }
            catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException)
            {
                logger.LogVerbose($"Failed to delete cached {imageId}: {e.Message}");
            }
        }

        public static async Task<List<TwitchEmote>> GetEmotes(List<Comment> comments, string cacheFolder, ITaskLogger logger, EmbeddedData embeddedData = null, bool offline = false, CancellationToken cancellationToken = default)
        {
            var emotes = new Dictionary<string, TwitchEmote>();

            DirectoryInfo emoteFolder = new DirectoryInfo(Path.Combine(cacheFolder, "emotes"));
            if (!emoteFolder.Exists)
                emoteFolder = CreateDirectory(emoteFolder.FullName);

            // Load our embedded emotes
            if (embeddedData?.firstParty != null)
            {
                emotes.EnsureCapacity(embeddedData.firstParty.Count);
                foreach (EmbedEmoteData emoteData in embeddedData.firstParty)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var newEmote = new TwitchEmote(emoteData.data, null, EmoteProvider.FirstParty, emoteData.imageScale, emoteData.id, emoteData.name);

                        if (!emotes.TryAdd(emoteData.id, newEmote))
                        {
                            newEmote.Dispose();
                            logger.LogVerbose($"Tried to add duplicate emote from embedded data: {emoteData.name}.");
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogVerbose($"An exception occurred while loading embedded emote '{emoteData.name}': {e.Message}.");
                    }
                }
            }

            var toFetch = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var comment in comments)
            {
                if (comment.message?.fragments is null)
                    continue;

                foreach (var fragment in comment.message.fragments)
                {
                    var id = fragment.emoticon?.emoticon_id;
                    if (id is not null && !emotes.ContainsKey(id) && seenIds.Add(id))
                    {
                        toFetch.Add(id);
                    }
                }
            }

            if (toFetch.Count > 0)
            {
                var loadedEmotes = new TwitchEmote[toFetch.Count];

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, toFetch.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = AssetFetchParallelism, CancellationToken = cancellationToken },
                    async (index, ct) =>
                    {
                        var id = toFetch[index];
                        try
                        {
                            var (bytes, codec, _) = await GetImageInternalAsync(emoteFolder, $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/2.0", id, 2, "png", offline, logger, ct);
                            if (bytes is null)
                            {
                                return;
                            }

                            loadedEmotes[index] = new TwitchEmote(bytes, codec, EmoteProvider.FirstParty, 2, id, id);
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
                    });

                for (int i = 0; i < loadedEmotes.Length; i++)
                {
                    var newEmote = loadedEmotes[i];
                    if (newEmote is not null && !emotes.TryAdd(newEmote.Id, newEmote))
                    {
                        newEmote.Dispose();
                    }
                }
            }

            return emotes.Values.ToList();
        }

        public static async Task<List<EmbedChatBadge>> GetChatBadgesData(List<Comment> comments, int streamerId, CancellationToken cancellationToken = new())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // TODO: this currently only does twitch badges, but we could also support FFZ, BTTV, 7TV, etc badges!
            // TODO: would want to make this configurable as we do for emotes though...
            var globalBadgeRequest = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{badges{imageURL(size:DOUBLE),description,title,setID,version}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            globalBadgeRequest.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");

            var subBadgeRequest = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{user(id: " + streamerId + "){broadcastBadges{imageURL(size:DOUBLE),description,title,setID,version}}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            subBadgeRequest.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");

            using var globalBadgeResponseTask = httpClient.SendAsync(globalBadgeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            using var subBadgeResponseTask = httpClient.SendAsync(subBadgeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            using var globalBadgeResponse = await globalBadgeResponseTask;
            globalBadgeResponse.EnsureSuccessStatusCode();
            var globalBadges = (await globalBadgeResponse.Content.ReadFromJsonAsync<GqlGlobalBadgeResponse>(cancellationToken: cancellationToken)).data.badges.GroupBy(x => x.name).ToDictionary(x => x.Key, x => x.ToList());

            using var subBadgeResponse = await subBadgeResponseTask;
            subBadgeResponse.EnsureSuccessStatusCode();
            var subBadges = (await subBadgeResponse.Content.ReadFromJsonAsync<GqlSubBadgeResponse>(cancellationToken: cancellationToken)).data.user.badges.GroupBy(x => x.name).ToDictionary(x => x.Key, x => x.ToList());

            List<EmbedChatBadge> badges = new List<EmbedChatBadge>();

            // Track exact (badge._id, badge.version) pairs used in comments so we only fetch and decode versions that are actually drawn
            var usedVersionsByBadge = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var comment in comments)
            {
                if (comment.message?.user_badges is null)
                    continue;

                foreach (var badge in comment.message.user_badges)
                {
                    if (string.IsNullOrWhiteSpace(badge._id) || string.IsNullOrWhiteSpace(badge.version))
                        continue;

                    if (!globalBadges.ContainsKey(badge._id) && !subBadges.ContainsKey(badge._id))
                        continue;

                    if (!usedVersionsByBadge.TryGetValue(badge._id, out var versionSet))
                    {
                        versionSet = new HashSet<string>(StringComparer.Ordinal);
                        usedVersionsByBadge[badge._id] = versionSet;
                    }
                    versionSet.Add(badge.version);
                }
            }

            foreach (var (name, usedVersions) in usedVersionsByBadge)
            {
                Dictionary<string, ChatBadgeData> versions = new();
                if (globalBadges.TryGetValue(name, out var globalBadge))
                {
                    foreach (var badge in globalBadge)
                    {
                        if (usedVersions.Contains(badge.version))
                        {
                            versions[badge.version] = new()
                            {
                                title = badge.title,
                                description = badge.description,
                                url = badge.image_url_2x
                            };
                        }
                    }
                }

                // Prefer channel specific badges over global ones
                if (subBadges.TryGetValue(name, out var subBadge))
                {
                    foreach (var badge in subBadge)
                    {
                        if (usedVersions.Contains(badge.version))
                        {
                            versions[badge.version] = new()
                            {
                                title = badge.title,
                                description = badge.description,
                                url = badge.image_url_2x
                            };
                        }
                    }
                }

                if (versions.Count > 0)
                {
                    badges.Add(new EmbedChatBadge() { name = name, versions = versions });
                }
            }

            return badges;
        }

        public static async Task<List<ChatBadge>> GetChatBadges(List<Comment> comments, int streamerId, string cacheFolder, ITaskLogger logger, EmbeddedData embeddedData = null, bool offline = false, CancellationToken cancellationToken = default)
        {
            var badges = new Dictionary<string, ChatBadge>();

            // Load our embedded data from file
            if (embeddedData?.twitchBadges != null)
            {
                badges.EnsureCapacity(embeddedData.twitchBadges.Count);
                foreach (EmbedChatBadge data in embeddedData.twitchBadges)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        ChatBadge newBadge = new ChatBadge(data.name, data.versions);

                        if (!badges.TryAdd(data.name, newBadge))
                        {
                            newBadge.Dispose();
                            logger.LogVerbose($"Tried to add duplicate badge from embedded data: {data.name}.");
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogVerbose($"An exception occurred while loading embedded badge '{data.name}': {e.Message}.");
                    }
                }
            }

            // Directly return if we are in offline, no need for a network request
            if (offline)
            {
                return badges.Values.ToList();
            }

            List<EmbedChatBadge> badgesData = await GetChatBadgesData(comments, streamerId, cancellationToken);

            DirectoryInfo badgeFolder = new DirectoryInfo(Path.Combine(cacheFolder, "badges"));
            if (!badgeFolder.Exists)
                badgeFolder = CreateDirectory(badgeFolder.FullName);

            var pendingBadges = badgesData.Where(b => !badges.ContainsKey(b.name)).ToList();
            if (pendingBadges.Count > 0)
            {
                var loadedBadges = new ChatBadge[pendingBadges.Count];

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, pendingBadges.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = AssetFetchParallelism, CancellationToken = cancellationToken },
                    async (index, ct) =>
                    {
                        var badge = pendingBadges[index];
                        try
                        {
                            Dictionary<string, ChatBadgeData> versions = new(badge.versions.Count);
                            foreach (var (version, data) in badge.versions)
                            {
                                string id = data.url.GetNthOccurrence('/', ^2).ToString();
                                var (bytes, codec, _) = await GetImageInternalAsync(badgeFolder, data.url, id, 2, "png", false, logger, ct);
                                if (bytes is null)
                                {
                                    continue;
                                }

                                versions.Add(version, new ChatBadgeData
                                {
                                    title = data.title,
                                    description = data.description,
                                    bytes = bytes,
                                    Codec = codec,
                                });
                            }

                            if (versions.Count > 0)
                            {
                                loadedBadges[index] = new ChatBadge(badge.name, versions);
                            }
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
                    });

                for (int i = 0; i < loadedBadges.Length; i++)
                {
                    var newBadge = loadedBadges[i];
                    if (newBadge is not null && !badges.TryAdd(newBadge.Name, newBadge))
                    {
                        newBadge.Dispose();
                    }
                }
            }

            return badges.Values.ToList();
        }

        /// <summary>
        /// Ensures that the requested emoji vendor archive is extracted to disk and returns the paths of all emoji PNG files
        /// without eagerly decoding all ~3,689 bitmaps into memory.
        /// </summary>
        public static async Task<string[]> GetEmojiFilesAsync(string cacheFolder, EmojiVendor emojiVendor, CancellationToken cancellationToken = default)
        {
            if (emojiVendor == EmojiVendor.None)
                return [];

            var emojiFolder = Path.Combine(cacheFolder, "emojis", emojiVendor.EmojiFolder());
            if (!Directory.Exists(emojiFolder))
                CreateDirectory(emojiFolder);

            var enumerationOptions = new EnumerationOptions { MatchType = MatchType.Simple, MatchCasing = MatchCasing.CaseInsensitive };
            var emojiFiles = Directory.GetFiles(emojiFolder, "*.png", enumerationOptions);

            if (emojiFiles.Length < emojiVendor.EmojiCount())
            {
                var emojiZipPath = Path.Combine(emojiFolder, Path.GetRandomFileName());
                try
                {
                    using (var ms = emojiVendor.MemoryStream())
                    {
                        await using var fs = File.OpenWrite(emojiZipPath);
                        await ms.CopyToAsync(fs, cancellationToken);
                    }

                    await using var archive = await ZipFile.OpenReadAsync(emojiZipPath, cancellationToken);
                    var emojiAssetsPath = emojiVendor.AssetPath();
                    var sep = emojiVendor.UnicodeSequenceSeparator();
                    var emojis = archive.Entries
                        .Where(x => !string.IsNullOrWhiteSpace(x.Name)
                                    && Path.GetDirectoryName(x.FullName) is { } dir
                                    && dir.EndsWith(emojiAssetsPath));

                    foreach (var emoji in emojis)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var filePath = Path.Combine(emojiFolder, emoji.Name.ToUpper().Replace(sep, ' '));
                        if (!File.Exists(filePath))
                        {
                            try
                            {
                                // ReSharper disable once MethodHasAsyncOverloadWithCancellation
                                // ExtractToFileAsync is extremely slow
                                emoji.ExtractToFile(filePath);
                            }
                            catch { /* Being written by a parallel process? */ }
                        }
                    }

                    emojiFiles = Directory.GetFiles(emojiFolder, "*.png", enumerationOptions);
                }
                finally
                {
                    if (File.Exists(emojiZipPath))
                    {
                        File.Delete(emojiZipPath);
                    }
                }
            }

            return emojiFiles;
        }

        public static async Task<Dictionary<string, SKBitmap>> GetEmojis(string cacheFolder, EmojiVendor emojiVendor, ITaskLogger logger, CancellationToken cancellationToken = default)
        {
            var returnCache = new Dictionary<string, SKBitmap>();

            if (emojiVendor == EmojiVendor.None)
                return returnCache;

            var emojiFiles = await GetEmojiFilesAsync(cacheFolder, emojiVendor, cancellationToken);
            var failedToDecode = 0;

            foreach (var emojiPath in emojiFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var fs = File.OpenRead(emojiPath);
                var emojiImage = SKBitmap.Decode(fs);

                if (emojiImage is null)
                {
                    failedToDecode++;
                    logger.LogVerbose($"Failed to decode emoji {Path.GetFileName(emojiPath)}, skipping.");
                    continue;
                }

                returnCache.Add(Path.GetFileNameWithoutExtension(emojiPath), emojiImage);
            }

            if (failedToDecode > 0)
            {
                logger.LogWarning($"{failedToDecode} emojis failed to decode.");
            }

            return returnCache;
        }

        public static async Task<List<CheerEmote>> GetBits(List<Comment> comments, string cacheFolder, string channelId, ITaskLogger logger, EmbeddedData embeddedData = null, bool offline = false, CancellationToken cancellationToken = default)
        {
            var bits = new Dictionary<string, CheerEmote>();

            // Load our embedded data from file
            if (embeddedData?.twitchBits != null)
            {
                bits.EnsureCapacity(embeddedData.twitchBits.Count);
                foreach (EmbedCheerEmote data in embeddedData.twitchBits)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        List<KeyValuePair<int, TwitchEmote>> tierList = new List<KeyValuePair<int, TwitchEmote>>();
                        CheerEmote newEmote = new CheerEmote() { prefix = data.prefix, tierList = tierList };
                        foreach (var (cost, cheermote) in data.tierList)
                        {
                            var tierEmote = new TwitchEmote(cheermote.data, null, EmoteProvider.FirstParty, cheermote.imageScale, cheermote.id, cheermote.name);
                            tierList.Add(new KeyValuePair<int, TwitchEmote>(cost, tierEmote));
                        }

                        if (!bits.TryAdd(data.prefix, newEmote))
                        {
                            newEmote.Dispose();
                            logger.LogVerbose($"Tried to add duplicate cheermote from embedded data: {data.prefix}.");
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogVerbose($"An exception occurred while loading embedded cheermote '{data.prefix}': {e.Message}.");
                    }
                }
            }

            // Directly return if we are in offline, no need for a network request
            if (offline)
            {
                return bits.Values.ToList();
            }

            // Pre-filter comments that actually spent bits. If none exist in the chat log, skip the network GQL call and regex scanning entirely.
            List<Comment> bitComments = null;
            if (comments is not null)
            {
                bitComments = new List<Comment>();
                foreach (var c in comments)
                {
                    if (c.message is { bits_spent: > 0, body.Length: > 0 })
                    {
                        bitComments.Add(c);
                    }
                }

                if (bitComments.Count == 0)
                {
                    return bits.Values.ToList();
                }
            }

            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{cheerConfig{groups{nodes{id, prefix, tiers{bits}}, templateURL}},user(id:\\\"" + channelId + "\\\"){cheer{cheerGroups{nodes{id,prefix,tiers{bits}},templateURL}}}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var cheerResponseMessage = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            cheerResponseMessage.EnsureSuccessStatusCode();
            var cheerResponse = await cheerResponseMessage.Content.ReadFromJsonAsync<GqlCheerResponse>(cancellationToken: cancellationToken);

            DirectoryInfo bitFolder = new DirectoryInfo(Path.Combine(cacheFolder, "bits"));
            if (!bitFolder.Exists)
                bitFolder = CreateDirectory(bitFolder.FullName);

            if (cheerResponse?.data != null)
            {
                List<CheerGroup> groupList = new List<CheerGroup>();

                foreach (CheerGroup group in cheerResponse.data.cheerConfig.groups)
                {
                    groupList.Add(group);
                }

                if (cheerResponse.data.user?.cheer?.cheerGroups != null)
                {
                    foreach (var group in cheerResponse.data.user.cheer.cheerGroups)
                    {
                        groupList.Add(group);
                    }
                }

                var searchTarget = bitComments ?? comments;
                foreach (CheerGroup cheerGroup in groupList)
                {
                    string templateURL = cheerGroup.templateURL;

                    var cheerNodesQuery = from node in cheerGroup.nodes
                        where !bits.ContainsKey(node.prefix)
                        let regex = new Regex($@"(?<=^|\s){Regex.Escape(node.prefix)}(?=[1-9])")
                        where searchTarget == null || searchTarget.Any(comment => regex.IsMatch(comment.message.body))
                        select node;

                    foreach (CheerNode node in cheerNodesQuery)
                    {
                        string prefix = node.prefix;
                        try
                        {
                            List<KeyValuePair<int, TwitchEmote>> tierList = new List<KeyValuePair<int, TwitchEmote>>(node.tiers.Count);
                            CheerEmote newEmote = new CheerEmote() { prefix = prefix, tierList = tierList };
                            foreach (Tier tier in node.tiers)
                            {
                                int minBits = tier.bits;
                                var url = new StringBuilder(templateURL)
                                    .Replace("PREFIX", node.prefix.ToLower())
                                    .Replace("BACKGROUND", "dark")
                                    .Replace("ANIMATION", "animated")
                                    .Replace("TIER", tier.bits.ToString())
                                    .Replace("SCALE.EXTENSION", "2.gif")
                                    .ToString();

                                var (bytes, codec, _) = await GetImageInternalAsync(bitFolder, url, node.id + tier.bits, 2, "gif", false, logger, cancellationToken);
                                if (bytes is null)
                                {
                                    continue;
                                }

                                var emote = new TwitchEmote(bytes, codec, EmoteProvider.FirstParty, 2, prefix + minBits, prefix + minBits);
                                tierList.Add(new KeyValuePair<int, TwitchEmote>(minBits, emote));
                            }

                            if (!bits.TryAdd(node.prefix, newEmote))
                            {
                                newEmote.Dispose();
                            }
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
                    }
                }
            }

            return bits.Values.ToList();
        }

        public static async Task<Dictionary<string, SKBitmap>> GetAvatars(List<Comment> comments, string[] defaultAvatars, string cacheFolder, ITaskLogger logger, bool offline = false, CancellationToken cancellationToken = default)
        {
            var urls = new HashSet<string>(defaultAvatars ?? []);
            foreach (var comment in comments)
            {
                var logo = comment.commenter.logo;
                if (string.IsNullOrWhiteSpace(logo))
                    continue;

                urls.Add(logo);
            }

            var avatarFolder = new DirectoryInfo(Path.Combine(cacheFolder, "avatars"));
            if (!avatarFolder.Exists)
                avatarFolder = CreateDirectory(avatarFolder.FullName);

            var urlList = urls.ToList();
            var decodedAvatars = new SKBitmap[urlList.Count];

            await Parallel.ForEachAsync(
                Enumerable.Range(0, urlList.Count),
                new ParallelOptions { MaxDegreeOfParallelism = AssetFetchParallelism, CancellationToken = cancellationToken },
                async (index, ct) =>
                {
                    var url = urlList[index];
                    var avatarId = Path.GetFileNameWithoutExtension(url);

                    byte[] bytes;
                    SKCodec codec;
                    try
                    {
                        (bytes, codec, _) = await GetImageInternalAsync(avatarFolder, url, avatarId, 2, "jpg", offline, logger, ct);
                    }
                    catch (HttpRequestException ex)
                    {
                        logger.LogVerbose($"Error while fetching {url}: {ex.Message}");
                        return;
                    }

                    if (bytes is null)
                    {
                        return;
                    }

                    try
                    {
                        var bitmap = codec is null
                            ? SKBitmap.Decode(bytes)
                            : SKBitmap.Decode(codec);

                        if (bitmap is null)
                        {
                            logger.LogWarning($"Skia was unable to decode {avatarId}.");
                            return;
                        }

                        decodedAvatars[index] = bitmap;
                    }
                    finally
                    {
                        codec?.Dispose();
                    }
                });

            var avatars = new Dictionary<string, SKBitmap>(urlList.Count);
            for (int i = 0; i < urlList.Count; i++)
            {
                if (decodedAvatars[i] is not null)
                {
                    avatars[urlList[i]] = decodedAvatars[i];
                }
            }

            return avatars;
        }

        public static FileInfo ClaimFile(string path, Func<FileInfo, FileInfo> fileAlreadyExistsCallback, ITaskLogger logger)
        {
            var fullPath = Path.GetFullPath(path);
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Exists)
            {
                if (fileAlreadyExistsCallback is null)
                {
                    logger.LogWarning($"{nameof(fileAlreadyExistsCallback)} was null.");
                }
                else
                {
                    fileInfo = fileAlreadyExistsCallback(fileInfo);

                    if (fileInfo is null)
                    {
                        // I would prefer to not throw here, but the alternative is refactoring the task queue :/
                        throw new FileNotFoundException("No destination file was provided, aborting.");
                    }

                    if (fullPath != fileInfo.FullName)
                    {
                        logger.LogInfo($"'{fullPath}' will be renamed to '{fileInfo.FullName}'");
                    }
                }
            }

            var directory = fileInfo.Directory;
            if (directory is not null && !directory.Exists)
            {
                CreateDirectory(directory.FullName);
            }

            return fileInfo;
        }

        public static void CleanUpClaimedFile([AllowNull] FileInfo fileInfo, [AllowNull] FileStream fileStream, ITaskLogger logger)
        {
            if (fileInfo is null)
            {
                return;
            }

            fileInfo.Refresh();
            if (fileInfo.Exists && fileInfo.Length == 0)
            {
                try
                {
                    fileStream?.Dispose();
                }
                catch
                {
                    // Ignored
                }

                try
                {
                    fileInfo.Delete();
                }
                catch (Exception e)
                {
                    logger.LogWarning($"Failed to clean up {fileInfo.FullName}: {e.Message}");
                }
            }
        }

        /// <inheritdoc cref="Directory.CreateDirectory(string)"/>
        public static DirectoryInfo CreateDirectory(string path, ITaskLogger logger = null)
        {
            DirectoryInfo directoryInfo = Directory.CreateDirectory(path);

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Set777UnixFilePermissions(directoryInfo);
                }
            }
            catch (Exception e)
            {
                logger?.LogVerbose($"Failed to set unix file mode for {directoryInfo.FullName}: {e.Message}");
            }

            return directoryInfo;
        }

        [UnsupportedOSPlatform("windows")]
        public static FileSystemInfo Set777UnixFilePermissions(FileSystemInfo fileSystemInfo)
        {
            fileSystemInfo.UnixFileMode = UnixFileMode.OtherExecute | UnixFileMode.OtherWrite | UnixFileMode.OtherRead
                                          | UnixFileMode.GroupExecute | UnixFileMode.GroupWrite | UnixFileMode.GroupRead
                                          | UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead;

            fileSystemInfo.Refresh();
            return fileSystemInfo;
        }

        [GeneratedRegex(@"\d+_\d+$", RegexOptions.RightToLeft)]
        private static partial Regex VideoFolderRegex { get; }

        /// <summary>
        /// Cleans up any unmanaged cache files from previous runs that were interrupted before cleaning up
        /// </summary>
        public static async Task CleanupAbandonedVideoCaches(string cacheFolder, Func<DirectoryInfo[], DirectoryInfo[]> itemsToDeleteCallback, ITaskLogger logger)
        {
            if (!Directory.Exists(cacheFolder))
            {
                return;
            }

            if (itemsToDeleteCallback == null)
            {
                logger.LogWarning($"{nameof(itemsToDeleteCallback)} was null.");
                return;
            }

            var allCacheDirectories = Directory.GetDirectories(cacheFolder);

            var oldVideoCaches = allCacheDirectories
                .Where(directory => VideoFolderRegex.IsMatch(directory))
                .Select(directory => new DirectoryInfo(directory))
                .Where(directoryInfo => DateTime.UtcNow.Ticks - directoryInfo.LastWriteTimeUtc.Ticks > TimeSpan.TicksPerDay * 7)
                .ToArray();

            if (oldVideoCaches.Length == 0)
            {
                return;
            }

            var toDelete = await Task.Run(() => itemsToDeleteCallback(oldVideoCaches));

            if (toDelete == null || toDelete.Length == 0)
            {
                return;
            }

            var wasDeleted = 0;
            foreach (var directory in toDelete)
            {
                try
                {
                    Directory.Delete(directory.FullName, true);
                    wasDeleted++;
                    logger.LogVerbose($"Deleted '{directory.FullName}' successfully.");
                }
                catch (Exception e)
                {
                    logger.LogVerbose($"Could not delete '{directory.FullName}': {e.Message}.");
                }
            }

            logger.LogInfo(toDelete.Length == wasDeleted
                ? $"{wasDeleted} old video caches were deleted."
                : $"{wasDeleted} old video caches were deleted, {toDelete.Length - wasDeleted} could not be deleted.");
        }

        public static async Task<GqlUserIdResponse> GetUserIds(IEnumerable<string> nameList)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{users(logins:[" + string.Join(",", nameList.Select(x => "\\\"" + x + "\\\"").ToArray()) + "]){id}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GqlUserIdResponse>();
        }

        public static async Task<GqlUserInfoResponse> GetUserInfo(IEnumerable<string> idList)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"query\":\"query{users(ids:[" + string.Join(",", idList.Select(x => "\\\"" + x + "\\\"").ToArray()) + "]){id,displayName,login,createdAt,updatedAt,description,profileImageURL(width:70)}}\",\"variables\":{}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<GqlUserInfoResponse>();
        }

        /// <summary>
        /// Internal fast-path image loader that assumes <paramref name="cacheDir"/> has already been created by the caller
        /// and reports whether the image was satisfied from the local disk cache without redundant File.Exists / DirectoryInfo.Refresh syscalls.
        /// </summary>
        private static async Task<(byte[] Bytes, SKCodec Codec, bool WasFromCache)> GetImageInternalAsync(DirectoryInfo cacheDir, string url, string imageId, int imageScale, string imageType, bool offline, ITaskLogger logger, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.Combine(cacheDir.FullName, $"{imageId}_{imageScale}.{imageType}");
            if (File.Exists(filePath))
            {
                try
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);

                    if (fileBytes.Length > 0)
                    {
                        var ms = new MemoryStream(fileBytes);
                        var codec = SKCodec.Create(ms, out var result);

                        if (codec is not null)
                        {
                            return (fileBytes, codec, true);
                        }

                        logger.LogVerbose($"Failed to decode {Path.GetFileName(filePath)} from cache: {result}");
                    }

                    // Delete the corrupted image
                    File.Delete(filePath);
                }
                catch (Exception e) when (e is IOException or SecurityException)
                {
                    // File being written to by parallel process? Maybe. Can just fall back to HTTP request.
                    logger.LogVerbose($"Failed to read from or delete {Path.GetFileName(filePath)}: {e.Message}");
                }
            }

            // Return null if offline
            if (offline)
            {
                return (null, null, false);
            }

            var imageBytes = await httpClient.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);

            try
            {
                await File.WriteAllBytesAsync(filePath, imageBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogVerbose($"Failed to save {Path.GetFileName(filePath)} to cache: {ex.Message}");
            }

            return (imageBytes, null, false);
        }

        public static async Task<(byte[], SKCodec)> GetImage(DirectoryInfo cacheDir, string url, string imageId, int imageScale, string imageType, bool offline, ITaskLogger logger, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!cacheDir.Exists)
            {
                CreateDirectory(cacheDir.FullName);
                cacheDir.Refresh();
            }

            var (bytes, codec, _) = await GetImageInternalAsync(cacheDir, url, imageId, imageScale, imageType, offline, logger, cancellationToken).ConfigureAwait(false);
            return (bytes, codec);
        }

        /// <remarks>When a given video has only 1 chapter, data.video.moments.edges will be empty.</remarks>
        public static async Task<GqlVideoChapterResponse> GetVideoChapters(long videoId)
        {
            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri("https://gql.twitch.tv/gql"),
                Method = HttpMethod.Post,
                Content = new StringContent("{\"extensions\":{\"persistedQuery\":{\"sha256Hash\":\"71835d5ef425e154bf282453a926d99b328cdc5e32f36d3a209d0f4778b41203\",\"version\":1}},\"operationName\":\"VideoPlayer_ChapterSelectButtonVideo\",\"variables\":{\"videoID\":\"" + videoId + "\"}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var chapterResponse = await response.Content.ReadFromJsonAsync<GqlVideoChapterResponse>();
            chapterResponse.data.video ??= new ChapterVideo { id = videoId.ToString() };
            chapterResponse.data.video.moments ??= new VideoMomentConnection { edges = [] };

            // For some reason durations can be negative sometimes
            foreach (var edge in chapterResponse.data.video.moments.edges)
            {
                if (edge.node.durationMilliseconds < 0)
                {
                    edge.node.durationMilliseconds = 0;
                }
            }

            // When downloading VODs of currently-airing streams, the last chapter lacks a duration
            if (chapterResponse.data.video.moments.edges.LastOrDefault() is { } lastEdge && lastEdge.node.durationMilliseconds is 0)
            {
                lastEdge.node.durationMilliseconds = lastEdge.node.video.lengthSeconds * 1000 - lastEdge.node.positionMilliseconds;
            }

            return chapterResponse;
        }

        public static async Task<GqlVideoChapterResponse> GetOrGenerateVideoChapters(long videoId, VideoInfo videoInfo, ITaskLogger logger)
        {
            GqlVideoChapterResponse chapterResponse;
            try
            {
                chapterResponse = await GetVideoChapters(videoId);
            }
            catch (NullReferenceException ex)
            {
                logger.LogError($"Null object reference while fetching chapters: {ex.StackTrace}");

                chapterResponse = new GqlVideoChapterResponse
                {
                    data = new ChapterData
                    {
                        video = new ChapterVideo
                        {
                            id = videoId.ToString(),
                            moments = new VideoMomentConnection
                            {
                                edges = []
                            }
                        }
                    }
                };
            }

            // Video has only 1 chapter, generate a bogus video chapter with the information we have available.
            if (chapterResponse.data.video.moments.edges.Count == 0)
            {
                chapterResponse.data.video.moments.edges.Add(
                    GenerateVideoMomentEdge(0, videoInfo.lengthSeconds, videoInfo.game?.id, videoInfo.game?.displayName, videoInfo.game?.displayName, videoInfo.game?.boxArtURL
                    ));
            }

            return chapterResponse;
        }

        public static VideoMomentEdge GenerateClipChapter(ShareClipRenderStatusClip clipInfo)
        {
            return GenerateVideoMomentEdge(0, clipInfo.durationSeconds, clipInfo.game?.id, clipInfo.game?.displayName, clipInfo.game?.displayName, clipInfo.game?.boxArtURL);
        }

        private static VideoMomentEdge GenerateVideoMomentEdge(int startSeconds, int lengthSeconds, string gameId = null, string gameDisplayName = null, string gameDescription = null, string gameBoxArtUrl = null)
        {
            gameId ??= "-1";
            gameDisplayName ??= "Unknown";
            gameDescription ??= "Unknown";
            gameBoxArtUrl ??= "";

            return new VideoMomentEdge
            {
                node = new VideoMoment
                {
                    id = "",
                    _type = "GAME_CHANGE",
                    positionMilliseconds = startSeconds,
                    durationMilliseconds = lengthSeconds * 1000,
                    description = gameDescription,
                    subDescription = "",
                    details = new GameChangeMomentDetails
                    {
                        game = new Game
                        {
                            id = gameId,
                            displayName = gameDisplayName,
                            boxArtURL = gameBoxArtUrl.Replace("{width}", "40").Replace("{height}", "53")
                        }
                    }
                }
            };
        }

        public static int SnapResizeHeight(int desiredHeight, int upSnapThreshold, int downSnapThreshold, int imageHeight)
        {
            if (upSnapThreshold == downSnapThreshold && upSnapThreshold != 0)
            {
                var o = (desiredHeight + upSnapThreshold) % imageHeight;
                if (o <= upSnapThreshold * 2)
                {
                    desiredHeight += upSnapThreshold - o;
                }
            }
            else
            {
                if (downSnapThreshold != 0)
                {
                    var o = desiredHeight % imageHeight;
                    if (o <= downSnapThreshold)
                    {
                        desiredHeight -= o;
                    }
                }

                if (upSnapThreshold != 0)
                {
                    var o = imageHeight - (desiredHeight % imageHeight);
                    if (o <= upSnapThreshold)
                    {
                        desiredHeight += o;
                    }
                }
            }

            return desiredHeight;
        }
    }
}