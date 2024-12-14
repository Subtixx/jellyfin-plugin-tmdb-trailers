﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using Jellyfin.Plugin.Tmdb.Trailers.Config;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;
using Video = TMDbLib.Objects.General.Video;

namespace Jellyfin.Plugin.Tmdb.Trailers;

/// <summary>
/// The TMDb manager.
/// </summary>
public class TmdbManager : IDisposable
{
    /// <summary>
    /// Gets the page size.
    /// TMDb always returns 20 items.
    /// </summary>
    public const int PageSize = 20;

    private readonly TimeSpan _defaultCacheTime = TimeSpan.FromDays(1);
    private readonly List<string> _cacheIds = new();

    private readonly ILogger<TmdbManager> _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;

    private TMDbClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbManager"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{TmdbManager}"/> interface.</param>
    /// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    public TmdbManager(
        ILogger<TmdbManager> logger,
        IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _memoryCache = memoryCache;

        _httpClientFactory = httpClientFactory;
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
    }

    private string CachePath => Path.Join(_applicationPaths.CachePath, "tmdb-intro-trailers");

    private PluginConfiguration Configuration => TmdbTrailerPlugin.Instance.Configuration;

    private TMDbClient Client => _client ??= new TMDbClient(Configuration.ApiKey);

    /// <summary>
    /// Get channel items.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel item result.</returns>
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            ChannelItemResult result = null;
            // Initial entry
            if (string.IsNullOrEmpty(query.FolderId))
            {
                return GetChannelTypes();
            }

            if (_memoryCache.TryGetValue(query.FolderId, out ChannelItemResult cachedValue))
            {
                _logger.LogDebug("Function={Function} FolderId={FolderId} Cache Hit", nameof(GetChannelItems), query.FolderId);
                return cachedValue;
            }

            // Get upcoming movies.
            if (query.FolderId.Equals("upcoming", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetUpcomingMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                result = GetChannelItemResult(movies, TrailerType.ComingSoonToTheaters);
            }

            // Get now playing movies.
            else if (query.FolderId.Equals("nowplaying", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetNowPlayingMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                result = GetChannelItemResult(movies, TrailerType.ComingSoonToTheaters);
            }

            // Get popular movies.
            else if (query.FolderId.Equals("popular", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetPopularMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                result = GetChannelItemResult(movies, TrailerType.Archive);
            }

            // Get top rated movies.
            else if (query.FolderId.Equals("toprated", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetTopRatedMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                result = GetChannelItemResult(movies, TrailerType.Archive);
            }

            // Get video streams for item.
            else if (int.TryParse(query.FolderId, out var movieId))
            {
                var searchMovie = new SearchMovie { Id = movieId };
                var videos = await GetMovieStreamsAsync(searchMovie, cancellationToken).ConfigureAwait(false);
                result = GetVideoItem(videos.Movie, videos.Result, false);
            }

            if (result != null)
            {
                _memoryCache.Set(query.FolderId, result, _defaultCacheTime);
            }

            return result ?? new ChannelItemResult();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetChannelItems));
            throw;
        }
    }

    /// <summary>
    /// Get All Channel Items.
    /// </summary>
    /// <param name="ignoreCache">Ignore cache.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel item result.</returns>
    public async Task<ChannelItemResult> GetAllChannelItems(bool ignoreCache, CancellationToken cancellationToken)
    {
        try
        {
            if (!ignoreCache && _memoryCache.TryGetValue("all-trailer", out ChannelItemResult cachedValue))
            {
                _logger.LogDebug("Function={Function} Cache Hit", nameof(GetAllChannelItems));
                return cachedValue;
            }

            var query = new InternalChannelItemQuery();

            var channelItemsResult = new ChannelItemResult();
            var movieTasks = new List<Task<(SearchMovie Movie, ResultContainer<Video> Result)>>();

            if (TmdbTrailerPlugin.Instance.Configuration.EnableTrailersUpcoming)
            {
                var upcomingMovies = await GetUpcomingMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                movieTasks.AddRange(upcomingMovies.Select(movie => GetMovieStreamsAsync(movie, cancellationToken)));
            }

            if (TmdbTrailerPlugin.Instance.Configuration.EnableTrailersNowPlaying)
            {
                var nowPlayingMovies = await GetNowPlayingMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                movieTasks.AddRange(nowPlayingMovies.Select(movie => GetMovieStreamsAsync(movie, cancellationToken)));
            }

            if (TmdbTrailerPlugin.Instance.Configuration.EnableTrailersPopular)
            {
                var popularMovies = await GetPopularMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                movieTasks.AddRange(popularMovies.Select(movie => GetMovieStreamsAsync(movie, cancellationToken)));
            }

            if (TmdbTrailerPlugin.Instance.Configuration.EnableTrailersTopRated)
            {
                var topRatedMovies = await GetTopRatedMoviesAsync(query, cancellationToken).ConfigureAwait(false);
                movieTasks.AddRange(topRatedMovies.Select(movie => GetMovieStreamsAsync(movie, cancellationToken)));
            }

            await Task.WhenAll(movieTasks).ConfigureAwait(false);
            var resultList = new List<ChannelItemInfo>();
            foreach (var task in movieTasks)
            {
                var result = await task.ConfigureAwait(false);
                resultList.AddRange(GetVideoItem(result.Movie, result.Result, true).Items);
            }

            channelItemsResult.Items = resultList;
            _memoryCache.Set("all-trailer", channelItemsResult);
            return channelItemsResult;
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetAllChannelItems));
            throw;
        }
    }

    /// <summary>
    /// Get channel image.
    /// </summary>
    /// <param name="type">Image type.</param>
    /// <returns>The image response.</returns>
    public Task<DynamicImageResponse> GetChannelImage(ImageType type)
    {
        try
        {
            _logger.LogDebug(nameof(GetChannelImage));
            if (type == ImageType.Thumb)
            {
                var name = GetType().Namespace + ".Images.jellyfin-plugin-tmdb.png";
                var response = new DynamicImageResponse
                {
                    Format = ImageFormat.Png,
                    HasImage = true,
                    Stream = GetType().Assembly.GetManifestResourceStream(name)
                };

                return Task.FromResult(response);
            }

            return Task.FromResult<DynamicImageResponse>(null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetChannelImage));
            throw;
        }
    }

    /// <summary>
    /// Get supported channel images.
    /// </summary>
    /// <returns>The supported channel images.</returns>
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        yield return ImageType.Thumb;
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    /// <param name="disposing">Dispose everything.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Client?.Dispose();
        }
    }

    /// <summary>
    /// Calculate page size from start index.
    /// </summary>
    /// <param name="startIndex">Start index.</param>
    /// <returns>The page number.</returns>
    private static int GetPageNumber(int? startIndex)
    {
        var start = startIndex ?? 0;

        return (int)Math.Floor(start / (double)PageSize);
    }

    /// <summary>
    /// Gets the original image url.
    /// </summary>
    /// <param name="imagePath">The image resource path.</param>
    /// <returns>The full image path.</returns>
    private string GetImageUrl(string imagePath)
    {
        try
        {
            if (string.IsNullOrEmpty(imagePath))
            {
                return null;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "https://image.tmdb.org/t/p/original/{0}",
                imagePath.TrimStart('/'));
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetImageUrl));
            throw;
        }
    }

    /// <summary>
    /// Get types of trailers.
    /// </summary>
    /// <returns><see cref="ChannelItemResult"/> containing the types of trailers.</returns>
    private ChannelItemResult GetChannelTypes()
    {
        _logger.LogDebug("Get Channel Types");
        return new ChannelItemResult
        {
            Items = new List<ChannelItemInfo>
            {
                new ChannelItemInfo
                {
                    Id = "upcoming",
                    FolderType = ChannelFolderType.Container,
                    Name = "Upcoming",
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                },
                new ChannelItemInfo
                {
                    Id = "nowplaying",
                    FolderType = ChannelFolderType.Container,
                    Name = "Now Playing",
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                },
                new ChannelItemInfo
                {
                    Id = "popular",
                    FolderType = ChannelFolderType.Container,
                    Name = "Popular",
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                },
                new ChannelItemInfo
                {
                    Id = "toprated",
                    FolderType = ChannelFolderType.Container,
                    Name = "Top Rated",
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video
                }
            },
            TotalRecordCount = 4
        };
    }

    /// <summary>
    /// Get playback url from site and key.
    /// </summary>
    /// <param name="site">Site to play from.</param>
    /// <param name="key">Video key.</param>
    /// <returns>Video playback url.</returns>
    private async Task<IVideoStreamInfo> GetPlaybackUrlAsync(string site, string key)
    {
        try
        {
            if (site.Equals("youtube", StringComparison.OrdinalIgnoreCase))
            {
                var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
                var youTubeClient = new YoutubeClient(httpClient);
                var streamManifest = await youTubeClient.Videos.Streams.GetManifestAsync(key);
                var bestStream = streamManifest.GetVideoOnlyStreams().GetWithHighestVideoQuality();
                return bestStream;
            }

            // TODO other sites.
            return null;
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetPlaybackUrlAsync));
            return null;
        }
    }

    /// <summary>
    /// Create channel item result from search result.
    /// </summary>
    /// <param name="movies">Search container of movies.</param>
    /// <param name="trailerType">The trailer type.</param>
    /// <returns>The channel item result.</returns>
    private ChannelItemResult GetChannelItemResult(IEnumerable<SearchMovie> movies, TrailerType trailerType)
    {
        try
        {
            var channelItems = new List<ChannelItemInfo>();
            foreach (var item in movies)
            {
                var posterUrl = GetImageUrl(item.PosterPath);
                _memoryCache.Set($"{item.Id}-item", item, _defaultCacheTime);
                _memoryCache.Set($"{item.Id}-poster", posterUrl, _defaultCacheTime);
                _memoryCache.Set($"{item.Id}-trailer", trailerType, _defaultCacheTime);
                channelItems.Add(new ChannelItemInfo
                {
                    Id = item.Id.ToString(CultureInfo.InvariantCulture),
                    Name = item.Title,
                    FolderType = ChannelFolderType.Container,
                    Type = ChannelItemType.Folder,
                    MediaType = ChannelMediaType.Video,
                    ImageUrl = posterUrl
                });
            }

            return new ChannelItemResult
            {
                Items = channelItems
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetChannelItemResult));
            throw;
        }
    }

    /// <summary>
    /// Get upcoming movies.
    /// </summary>
    /// <param name="query">Channel query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The upcoming movies.</returns>
    private async Task<List<SearchMovie>> GetUpcomingMoviesAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(nameof(GetUpcomingMoviesAsync));
            var pageNumber = GetPageNumber(query.StartIndex);
            var itemLimit = TmdbTrailerPlugin.Instance.Configuration.TrailerLimit;
            var movies = new List<SearchMovie>();
            bool hasMore;

            do
            {
                var results = await Client.GetMovieUpcomingListAsync(
                        Configuration.Language,
                        pageNumber,
                        Configuration.Region,
                        cancellationToken)
                    .ConfigureAwait(false);

                pageNumber++;
                movies.AddRange(results.Results);
                hasMore = results.Results.Count != 0;
            }
            while (hasMore && movies.Count < itemLimit);

            return movies.Take(itemLimit).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetUpcomingMoviesAsync));
            throw;
        }
    }

    /// <summary>
    /// Get now playing movies.
    /// </summary>
    /// <param name="query">Channel query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The now playing movies.</returns>
    private async Task<List<SearchMovie>> GetNowPlayingMoviesAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(nameof(GetNowPlayingMoviesAsync));
            var pageNumber = GetPageNumber(query.StartIndex);
            var itemLimit = TmdbTrailerPlugin.Instance.Configuration.TrailerLimit;
            var movies = new List<SearchMovie>();
            bool hasMore;

            do
            {
                var results = await Client.GetMovieNowPlayingListAsync(
                        Configuration.Language,
                        pageNumber,
                        Configuration.Region,
                        cancellationToken)
                    .ConfigureAwait(false);

                pageNumber++;
                movies.AddRange(results.Results);
                hasMore = results.Results.Count != 0;
            }
            while (hasMore && movies.Count < itemLimit);

            return movies.Take(itemLimit).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetNowPlayingMoviesAsync));
            throw;
        }
    }

    /// <summary>
    /// Get popular movies.
    /// </summary>
    /// <param name="query">Channel query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The popular movies.</returns>
    private async Task<List<SearchMovie>> GetPopularMoviesAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(nameof(GetPopularMoviesAsync));
            var pageNumber = GetPageNumber(query.StartIndex);
            var itemLimit = TmdbTrailerPlugin.Instance.Configuration.TrailerLimit;
            var movies = new List<SearchMovie>();
            bool hasMore;

            do
            {
                var results = await Client.GetMoviePopularListAsync(
                        Configuration.Language,
                        pageNumber,
                        Configuration.Region,
                        cancellationToken)
                    .ConfigureAwait(false);

                pageNumber++;
                movies.AddRange(results.Results);
                hasMore = results.Results.Count != 0;
            }
            while (hasMore && movies.Count < itemLimit);

            return movies.Take(itemLimit).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetPopularMoviesAsync));
            throw;
        }
    }

    /// <summary>
    /// Get top rated movies.
    /// </summary>
    /// <param name="query">Channel query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The top rated movies.</returns>
    private async Task<List<SearchMovie>> GetTopRatedMoviesAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug(nameof(GetTopRatedMoviesAsync));
            var pageNumber = GetPageNumber(query.StartIndex);
            var itemLimit = TmdbTrailerPlugin.Instance.Configuration.TrailerLimit;
            var movies = new List<SearchMovie>();
            bool hasMore;

            do
            {
                var results = await Client.GetMovieTopRatedListAsync(
                        Configuration.Language,
                        pageNumber,
                        Configuration.Region,
                        cancellationToken)
                    .ConfigureAwait(false);

                pageNumber++;
                movies.AddRange(results.Results);
                hasMore = results.Results.Count != 0;
            }
            while (hasMore && movies.Count < itemLimit);

            return movies.Take(itemLimit).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetTopRatedMoviesAsync));
            throw;
        }
    }

    /// <summary>
    /// Get available movie streams.
    /// </summary>
    /// <param name="movie">The movie.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movie streams.</returns>
    private async Task<(SearchMovie Movie, ResultContainer<Video> Result)> GetMovieStreamsAsync(SearchMovie movie, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("{Function} Id={Id}", nameof(GetMovieStreamsAsync), movie.Id);
            var response = await Client.GetMovieVideosAsync(movie.Id, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Function} Response={@Response}", nameof(GetMovieStreamsAsync), response);
            return (movie, response);
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetMovieStreamsAsync));
            throw;
        }
    }

    private ChannelItemResult GetVideoItem(SearchMovie searchMovie, ResultContainer<Video> videoResult, bool trailerChannel)
    {
        try
        {
            _logger.LogDebug("{Function} VideoResult={@VideoResult}", nameof(GetVideoItem), videoResult);
            var channelItems = new List<ChannelItemInfo>(videoResult.Results.Count);
            foreach (var video in videoResult.Results)
            {
                // Only add first trailer
                if (trailerChannel && channelItems.Count != 0)
                {
                    break;
                }

                var channelItemInfo = GetVideoChannelItem(videoResult.Id, video, trailerChannel);
                if (channelItemInfo == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(searchMovie.Title))
                {
                    channelItemInfo.Name = $"{searchMovie.Title} - {channelItemInfo.Name}";
                }

                channelItems.Add(channelItemInfo);
            }

            return new ChannelItemResult
            {
                Items = channelItems,
                TotalRecordCount = channelItems.Count
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetVideoItem));
            throw;
        }
    }

    private ChannelItemInfo GetVideoChannelItem(int id, Video video, bool trailerChannel)
    {
        try
        {
            // Returning only trailers
            if (trailerChannel && !string.Equals(video.Type, "trailer", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _logger.LogDebug("{Function} Id={Id} Video={@Video}", nameof(GetVideoChannelItem), id, video);
            _memoryCache.TryGetValue($"{id}-poster", out string posterUrl);
            _memoryCache.TryGetValue($"{id}-trailer", out TrailerType? trailerType);
            _memoryCache.Set($"{video.Id}-video", video, _defaultCacheTime);

            trailerType ??= TrailerType.Archive;

            var channelItemInfo = GetChannelItemInfo(video);
            if (channelItemInfo == null)
            {
                return null;
            }

            channelItemInfo.Name = video.Name;
            if (!string.IsNullOrEmpty(posterUrl))
            {
                channelItemInfo.ImageUrl = posterUrl;
            }

            // only add additional properties if sourced from trailer channel.
            if (trailerChannel)
            {
                channelItemInfo.ExtraType = ExtraType.Trailer;
                channelItemInfo.TrailerTypes = new List<TrailerType>
                {
                    trailerType.Value
                };
                channelItemInfo.ProviderIds = new Dictionary<string, string>
                {
                    {
                        MetadataProvider.Tmdb.ToString(), id.ToString(CultureInfo.InvariantCulture)
                    }
                };
            }

            return channelItemInfo;
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetVideoChannelItem));
            throw;
        }
    }

    /// <summary>
    /// Get stream information from video item.
    /// </summary>
    /// <param name="item">Video item.</param>
    /// <returns>Stream information.</returns>
    private ChannelItemInfo GetChannelItemInfo(Video item)
    {
        try
        {
            return new ChannelItemInfo
            {
                Id = item.Id,
                Name = item.Name,
                OriginalTitle = item.Name,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetChannelItemInfo));
            throw;
        }
    }

    /// <summary>
    /// Get media source from video.
    /// </summary>
    /// <param name="id">video id.</param>
    /// <returns>Media source info.</returns>
    public async Task<MediaSourceInfo> GetMediaSource(string id)
    {
        try
        {
            _memoryCache.TryGetValue($"{id}-video", out Video video);
            if (video == null)
            {
                return null;
            }

            var response = await GetPlaybackUrlAsync(video.Site, video.Key).ConfigureAwait(false);

            if (response == null)
            {
                return null;
            }

            return new MediaSourceInfo
            {
                Name = video.Name,
                Path = response.Url,
                TranscodingUrl = video.Key,
                Protocol = MediaProtocol.Http,
                Id = video.Id,
                IsRemote = true,
                Bitrate = Convert.ToInt32(response.Bitrate.BitsPerSecond),
                Container = response.Container.Name
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, nameof(GetMediaSource));
            throw;
        }
    }

    /// <summary>
    /// Update the intro cache.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task UpdateIntroCache(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CachePath);

        var channelItems = await GetAllChannelItems(false, cancellationToken);

        var deleteOptions = new DeleteOptions { DeleteFileLocation = true };
        var existingCache = Directory.GetFiles(CachePath);
        var existingIds = existingCache.Select(c => Path.GetFileNameWithoutExtension(c)).ToArray();
        for (var i = 0; i < existingCache.Length; i++)
        {
            var existingId = existingIds[i];
            if (!string.IsNullOrEmpty(existingId)
                && !channelItems.Items.Any(c => string.Equals(c.Id, existingId, StringComparison.OrdinalIgnoreCase)))
            {
                var guid = existingId.GetMD5();
                var item = _libraryManager.GetItemById(guid);
                if (item is not null)
                {
                    // item no longer cached, so delete.
                    _libraryManager.DeleteItem(item, deleteOptions);
                }
            }
        }

        var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        var youTubeClient = new YoutubeClient(httpClient);
        _cacheIds.Clear();
        foreach (var item in channelItems.Items)
        {
            if (existingIds.Any(i => string.Equals(i, item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                // Item is already cached, skip
                _cacheIds.Add(item.Id);
                continue;
            }

            var destinationPath = Path.Combine(CachePath, $"{item.Id}.mp4");
            var mediaSource = await GetMediaSource(item.Id);
            if (mediaSource is null)
            {
                continue;
            }

            try
            {
                await youTubeClient.Videos.DownloadAsync(
                    mediaSource.TranscodingUrl,
                    destinationPath,
                    cfg => cfg.SetFFmpegPath(_mediaEncoder.EncoderPath),
                    cancellationToken: cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to cache {Path}", mediaSource.Path);
            }

            _cacheIds.Add(item.Id);
            _libraryManager.CreateItem(
                new Trailer
                {
                    Id = item.Id.GetMD5(),
                    Name = item.Name,
                    Path = destinationPath
                },
                null);
        }
    }

    /// <summary>
    /// Get random intros.
    /// </summary>
    /// <returns>The list of intros.</returns>
    public IEnumerable<IntroInfo> GetIntros()
    {
        var introCount = TmdbTrailerPlugin.Instance.Configuration.IntroCount;
        if (introCount <= 0 || _cacheIds.Count == 0)
        {
            return Enumerable.Empty<IntroInfo>();
        }

        var tmp = new List<string>(_cacheIds);
        tmp.Shuffle();
        var intros = new List<IntroInfo>(introCount);
        for (var i = 0; i < introCount && i < tmp.Count; i++)
        {
            intros.Add(new IntroInfo { ItemId = tmp[i].GetMD5() });
        }

        return intros;
    }
}
