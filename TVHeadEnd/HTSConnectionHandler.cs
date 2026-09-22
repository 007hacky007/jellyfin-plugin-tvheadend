using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd
{
    public class HTSConnectionHandler : IHTSConnectionListener, IDisposable
    {
        /// <summary>
        /// DVR_PRIO_IMPORTANT - the lowest value TVHeadend accepts for a recording priority.
        /// </summary>
        private const int DvrPriorityImportant = 0;

        /// <summary>
        /// DVR_PRIO_NORMAL - the fallback used when the configured priority is out of range.
        /// </summary>
        private const int DvrPriorityNormal = 2;

        /// <summary>
        /// DVR_PRIO_NOTSET - leaves the priority to the TVHeadend DVR configuration.
        /// </summary>
        private const int DvrPriorityNotSet = 5;

        private const int MaxRetryDelaySeconds = 60;

        // Give up a single connection attempt after this time (an unreachable
        // host would otherwise block for the full kernel TCP timeout).
        private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

        // How long callers may wait for the initial sync while a connection
        // attempt is in flight or the initial data is still being received.
        // When the server is known to be unreachable callers fail immediately
        // and never wait.
        private static readonly TimeSpan _initialLoadTimeout = TimeSpan.FromMinutes(5);

        private readonly object _lock = new object();
        private readonly CancellationTokenSource _connectionLoopCts = new CancellationTokenSource();

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionHandler> _logger;

        // Data helpers
        private readonly ChannelDataHelper _channelDataHelper;
        private readonly DvrDataHelper _dvrDataHelper;
        private readonly AutorecDataHelper _autorecDataHelper;

        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>();

        private volatile bool _initialLoadFinished;
        private volatile bool _connected;
        private volatile bool _configured;
        private volatile bool _firstConnectAttemptCompleted;
        private volatile bool _authenticationFailed;

        private Task? _connectionTask;

        private HTSConnectionAsync? _htsConnection;
        private int _priority;
        private string _profile = string.Empty;
        private string _httpBaseUrl = string.Empty;
        private string _channelType = string.Empty;
        private string _tvhServerName = string.Empty;
        private int _httpPort;
        private int _htspPort;
        private string _webRoot = string.Empty;
        private string _userName = string.Empty;
        private string _password = string.Empty;
        private bool _enableSubsMaudios;
        private bool _forceDeinterlace;

        private LiveTvService? _liveTvService;

        public HTSConnectionHandler(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionHandler>();

            // System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger.LogDebug("[TVHclient] HTSConnectionHandler");

            _channelDataHelper = new ChannelDataHelper(loggerFactory.CreateLogger<ChannelDataHelper>());
            _dvrDataHelper = new DvrDataHelper(loggerFactory.CreateLogger<DvrDataHelper>());
            _autorecDataHelper = new AutorecDataHelper(loggerFactory.CreateLogger<AutorecDataHelper>());

            // The channel type is applied in Init(), once the configuration has been read.
            // ChannelDataHelper defaults to "Ignore" until then.
        }

        public void SetLiveTvService(LiveTvService liveTvService)
        {
            _liveTvService = liveTvService;
        }

        public LiveTvService? GetLiveTvService()
        {
            return _liveTvService;
        }

        public int WaitForInitialLoad(CancellationToken cancellationToken)
        {
            StartConnectionLoop();
            if (_authenticationFailed)
            {
                return -1;
            }

            DateTime deadline = DateTime.UtcNow + _initialLoadTimeout;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_initialLoadFinished)
                {
                    return 0;
                }

                // Fail fast while the server is unreachable: the background
                // loop keeps reconnecting, callers must not block on it.
                if (_firstConnectAttemptCompleted && !_connected)
                {
                    return -1;
                }

                if (DateTime.UtcNow > deadline)
                {
                    return -1;
                }

                Thread.Sleep(100);
            }

            return -1;
        }

        private void Init()
        {
            if (_configured == true)
            {
                return;
            }

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Init()");

            var config = Plugin.Instance.Configuration;

            _logger.LogDebug("[TVHclient] HTSConnectionHandler - Config initialized");

            if (string.IsNullOrEmpty(config.TVH_ServerName))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: TVH server name must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            if (string.IsNullOrEmpty(config.Username))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: username must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            if (string.IsNullOrEmpty(config.Password))
            {
                const string Message = "[TVHclient] HTSConnectionHandler.EnsureConnection: password must be configured";
                _logger.LogError(Message);
                throw new InvalidOperationException(Message);
            }

            _priority = config.Priority;
            _profile = config.Profile.Trim();
            _channelType = config.ChannelType.Trim();
            _enableSubsMaudios = config.EnableSubsMaudios;
            _forceDeinterlace = config.ForceDeinterlace;

            if (_priority < DvrPriorityImportant || _priority > DvrPriorityNotSet)
            {
                _priority = DvrPriorityNormal;
                _logger.LogWarning(
                    "[TVHclient] HTSConnectionHandler.Init: priority {ConfiguredPriority} is out of range [{Lowest}-{Highest}] - using {Fallback} (normal)",
                    config.Priority,
                    DvrPriorityImportant,
                    DvrPriorityNotSet,
                    DvrPriorityNormal);
            }

            _tvhServerName = config.TVH_ServerName.Trim();
            _httpPort = config.HTTP_Port;
            _htspPort = config.HTSP_Port;

            _userName = config.Username.Trim();
            _password = config.Password.Trim();

            _httpBaseUrl = BuildHttpBaseUrl();

            string authInfo = _userName + ":" + _password;
            authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
            _headers["Authorization"] = "Basic " + authInfo;

            // The constructor runs before any configuration is available, so the channel type
            // has to be handed to the data helper here, once it has actually been read.
            _channelDataHelper.SetChannelType4Other(_channelType);
            _channelDataHelper.SetIncludeUnnumberedChannels(config.IncludeUnnumberedChannels);

            _configured = true;
        }

        /// <summary>
        /// Trims a web root into the '' or '/prefix' form used when building URLs.
        /// </summary>
        /// <param name="webRoot">The raw web root.</param>
        /// <returns>The normalized web root.</returns>
        private static string NormalizeWebRoot(string? webRoot)
        {
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                return string.Empty;
            }

            string trimmed = webRoot.Trim().TrimEnd('/');

            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        }

        /// <summary>
        /// Adopts the web root TVHeadend reported during the handshake.
        /// </summary>
        /// <remarks>
        /// The server knows its own path prefix, so it is the only source for this value; an
        /// absent field means TVHeadend is served from the root. The HTTP URLs are rebuilt
        /// because they are assembled in Init(), before a connection exists.
        /// </remarks>
        /// <param name="reportedWebRoot">The web root from the hello response.</param>
        private void ApplyServerWebRoot(string? reportedWebRoot)
        {
            string resolved = NormalizeWebRoot(reportedWebRoot);

            if (string.Equals(resolved, _webRoot, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation(
                "[TVHclient] HTSConnectionHandler: TVHeadend reported web root '{ReportedWebRoot}'",
                resolved);

            _webRoot = resolved;
            _httpBaseUrl = BuildHttpBaseUrl();
        }

        /// <summary>
        /// Builds the TVHeadend HTTP base URL from the current settings.
        /// </summary>
        /// <returns>The HTTP base URL.</returns>
        private string BuildHttpBaseUrl()
        {
            if (_enableSubsMaudios)
            {
                // Use HTTP basic auth instead of TVH ticketing system for authentication to allow the users to switch subs or audio tracks at any time
                return "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot;
            }

            return "http://" + _tvhServerName + ":" + _httpPort + _webRoot;
        }

        /// <summary>
        /// Turns an image reference from an HTSP message into an absolute URL.
        /// </summary>
        /// <remarks>
        /// TVHeadend's imagecache references are version dependent: below the per-field
        /// threshold the server sends an absolute <c>http://</c> URL, between HTSP v8 and v14
        /// a root-relative <c>/imagecache/N</c> path, and from v15 on a relative
        /// <c>imagecache/N</c> path. EPG providers may also supply an absolute URL directly.
        /// Anything that is not already absolute is resolved against the configured TVHeadend
        /// HTTP endpoint, so every negotiated protocol version yields a usable URL.
        /// </remarks>
        /// <param name="image">The raw image value from an HTSP message.</param>
        /// <returns>An absolute URL, or <c>null</c> when no image was supplied.</returns>
        public string? ResolveImageUrl(string? image)
        {
            if (string.IsNullOrEmpty(image))
            {
                return null;
            }

            if (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return image;
            }

            return GetAuthenticatedUrl(image);
        }

        /// <summary>
        /// Builds an absolute, credentialed URL for a resource served by TVHeadend over HTTP.
        /// </summary>
        /// <remarks>
        /// The web root is the one reported by the server, so it is only final once connected.
        /// </remarks>
        /// <param name="relativePath">The path below the web root, with or without a leading slash.</param>
        /// <returns>An absolute URL including the configured credentials.</returns>
        public string GetAuthenticatedUrl(string relativePath)
        {
            StartConnectionLoop();

            return "http://" + _userName + ":" + _password + "@" + _tvhServerName + ":" + _httpPort + _webRoot
                + "/" + relativePath.TrimStart('/');
        }

        public string? GetChannelImageUrl(string channelId)
        {
            Init();

            _logger.LogDebug("[TVHclient] HTSConnectionHandler.GetChannelImage: channelId: {Id}", channelId);

            return ResolveImageUrl(_channelDataHelper.GetChannelIcon4ChannelId(channelId));
        }

        public Dictionary<string, string> GetHeaders()
        {
            return new Dictionary<string, string>(_headers);
        }

        // private static Stream ImageToPNGStream(Image image)
        // {
        //    Stream stream = new System.IO.MemoryStream();
        //    image.Save(stream, ImageFormat.Png);
        //    stream.Position = 0;
        //    return stream;
        // }

        /// <summary>
        /// Describes why the server cannot be used right now, for exceptions raised towards Jellyfin.
        /// </summary>
        /// <returns>A human readable reason.</returns>
        public string GetUnavailableReason()
        {
            if (_authenticationFailed)
            {
                return "authentication with TVHeadend server '" + _tvhServerName + ":" + _htspPort
                    + "' failed; the plugin will not reconnect until Jellyfin is restarted";
            }

            return "TVHeadend server '" + _tvhServerName + ":" + _htspPort + "' is unreachable or has not finished the initial sync";
        }

        private void StartConnectionLoop()
        {
            Init();

            lock (_lock)
            {
                if (_authenticationFailed || _connectionLoopCts.IsCancellationRequested)
                {
                    return;
                }

                // A connection the server closed gracefully is only flagged via NeedsRestart()
                // (Stop() without OnError), so it has to be picked up here as well.
                if (_connected && _htsConnection != null && _htsConnection.NeedsRestart())
                {
                    _logger.LogWarning("[TVHclient] HTSConnectionHandler.StartConnectionLoop: connection was closed, reconnecting");
                    _connected = false;
                    _initialLoadFinished = false;
                    _firstConnectAttemptCompleted = false;
                }

                if (_connected || (_connectionTask != null && !_connectionTask.IsCompleted))
                {
                    return;
                }

                _connectionTask = Task.Run(() => ConnectionLoop(_connectionLoopCts.Token));
            }
        }

        private async Task ConnectionLoop(CancellationToken cancellationToken)
        {
            int attempt = 0;
            while (!_connected && !cancellationToken.IsCancellationRequested)
            {
                HTSConnectionAsync? connection = null;
                try
                {
                    lock (_lock)
                    {
                        if (_htsConnection == null || _htsConnection.NeedsRestart())
                        {
                            _logger.LogDebug("[TVHclient] HTSConnectionHandler.ConnectionLoop: create new HTS connection");
                            _htsConnection?.Dispose();

                            // "clientversion" is the client's own version, not the protocol version -
                            // TVHeadend only reports it, but sending the HTSP number here was misleading.
                            Version? version = typeof(HTSConnectionHandler).Assembly.GetName().Version;
                            _htsConnection = new HTSConnectionAsync(
                                this,
                                "Jellyfin-TVHeadend",
                                version?.ToString() ?? "unknown",
                                _loggerFactory);
                        }

                        connection = _htsConnection;
                    }

                    _logger.LogDebug(
                        "[TVHclient] HTSConnectionHandler.ConnectionLoop: used connection parameters: " +
                        "TVH Server = '{Servername}'; HTTP Port = '{Httpport}'; HTSP Port = '{Htspport}'; Web-Root = '{Webroot}'; " +
                        "User = '{User}'; Password set = '{Passexists}'",
                        _tvhServerName,
                        _httpPort,
                        _htspPort,
                        _webRoot,
                        _userName,
                        _password.Length > 0);

                    connection.Open(_tvhServerName, _htspPort, _connectTimeout);

                    if (connection.Authenticate(_userName, _password))
                    {
                        ApplyServerWebRoot(connection.GetWebRoot());
                        _connected = true;

                        _logger.LogInformation(
                            "[TVHclient] HTSConnectionHandler.ConnectionLoop: connection to {ServerAddress}:{Htspport} established; "
                            + "TVH server = '{ServerName}' {ServerVersion}; HTSP version negotiated = {NegotiatedHtspVersion} "
                            + "(server supports up to {ServerHtspVersion}, client up to {ClientHtspVersion})",
                            _tvhServerName,
                            _htspPort,
                            connection.GetServername(),
                            connection.GetServerversion(),
                            connection.GetNegotiatedProtocolVersion(),
                            connection.GetServerProtocolVersion(),
                            HTSMessage.HtspVersion);
                        return;
                    }

                    // Rejected credentials do not fix themselves, unlike an unreachable
                    // server: stop trying until the configuration is corrected and
                    // Jellyfin is restarted, instead of hammering the server forever.
                    lock (_lock)
                    {
                        _authenticationFailed = true;
                        _htsConnection = null;
                    }

                    connection.Dispose();
                    _logger.LogError(
                        "[TVHclient] HTSConnectionHandler.ConnectionLoop: TVHeadend server {ServerAddress}:{Htspport} rejected the credentials of user '{User}'. "
                        + "Giving up: Live TV stays unavailable until the username and password in the plugin configuration are corrected and Jellyfin is restarted",
                        _tvhServerName,
                        _htspPort,
                        _userName);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "[TVHclient] HTSConnectionHandler.ConnectionLoop: can't connect to {ServerAddress}:{Htspport} - {Message}",
                        _tvhServerName,
                        _htspPort,
                        ex.Message);

                    // Close whatever the failed attempt left behind (socket, reader threads);
                    // NeedsRestart() then makes the next attempt build a fresh connection.
                    connection?.Stop();
                }
                finally
                {
                    _firstConnectAttemptCompleted = true;
                }

                attempt++;
                int delaySeconds = Math.Min(MaxRetryDelaySeconds, 5 << Math.Min(attempt - 1, 4));
                _logger.LogDebug("[TVHclient] HTSConnectionHandler.ConnectionLoop: next connection attempt in {Delay}s", delaySeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public void SendMessage(HTSMessage message, IHTSResponseHandler responseHandler)
        {
            StartConnectionLoop();

            HTSConnectionAsync? connection = _htsConnection;
            if (!_connected || connection == null)
            {
                throw new InvalidOperationException("[TVHclient] HTSConnectionHandler.SendMessage: " + GetUnavailableReason());
            }

            connection.SendMessage(message, responseHandler);
        }

        /// <summary>
        /// Gets the HTSP version in effect for the current connection.
        /// </summary>
        /// <returns>The negotiated HTSP version, or -1 while not connected.</returns>
        public int GetNegotiatedProtocolVersion()
        {
            StartConnectionLoop();
            HTSConnectionAsync? connection = _htsConnection;
            return (_connected && connection != null) ? connection.GetNegotiatedProtocolVersion() : -1;
        }

        public Task<IEnumerable<ChannelInfo>> BuildChannelInfos(CancellationToken cancellationToken)
        {
            return _channelDataHelper.BuildChannelInfos(cancellationToken);
        }

        public int GetPriority()
        {
            Init();
            return _priority;
        }

        public string GetProfile()
        {
            Init();
            return _profile;
        }

        public string GetHttpBaseUrl()
        {
            // The web root is taken from the HTSP handshake, so the base URL is only
            // final once connected; never block on an unreachable server here.
            StartConnectionLoop();
            return _httpBaseUrl;
        }

        public bool GetEnableSubsMaudios()
        {
            Init();
            return _enableSubsMaudios;
        }

        public bool GetForceDeinterlace()
        {
            Init();
            return _forceDeinterlace;
        }

        public Task<IEnumerable<MyRecordingInfo>> BuildDvrInfos(CancellationToken cancellationToken)
        {
            return _dvrDataHelper.BuildDvrInfos(cancellationToken);
        }

        public Task<IEnumerable<SeriesTimerInfo>> BuildAutorecInfos(CancellationToken cancellationToken)
        {
            return _autorecDataHelper.BuildAutorecInfos(cancellationToken);
        }

        public Task<IEnumerable<TimerInfo>> BuildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return _dvrDataHelper.BuildPendingTimersInfos(cancellationToken);
        }

        public void OnError(Exception ex)
        {
            _logger.LogError(ex, "[TVHclient] HTSConnectionHandler: HTSP error");
            lock (_lock)
            {
                if (_htsConnection == null)
                {
                    // Error of a connection that has already been torn down.
                    return;
                }

                _htsConnection.Dispose();
                _htsConnection = null;
                _connected = false;
                _initialLoadFinished = false;

                // Let callers wait for the reconnect attempt instead of failing
                // immediately on the stale result of the previous one.
                _firstConnectAttemptCompleted = false;
            }

            // _liveTvService.sendDataSourceChanged();
            StartConnectionLoop();
        }

        public void OnMessage(HTSMessage? response)
        {
            if (response != null)
            {
                switch (response.Method)
                {
                    case "tagAdd":
                    case "tagUpdate":
                    case "tagDelete":
                        // _logger.LogCritical("[TVHclient] tad add/update/delete {Resp}", response.ToString());
                        break;

                    case "channelAdd":
                    case "channelUpdate":
                        _channelDataHelper.Add(response);
                        break;

                    case "dvrEntryAdd":
                        _dvrDataHelper.DvrEntryAdd(response);
                        break;
                    case "dvrEntryUpdate":
                        _dvrDataHelper.DvrEntryUpdate(response);
                        break;
                    case "dvrEntryDelete":
                        _dvrDataHelper.DvrEntryDelete(response);
                        break;

                    case "autorecEntryAdd":
                        _autorecDataHelper.AutorecEntryAdd(response);
                        break;
                    case "autorecEntryUpdate":
                        _autorecDataHelper.AutorecEntryUpdate(response);
                        break;
                    case "autorecEntryDelete":
                        _autorecDataHelper.AutorecEntryDelete(response);
                        break;

                    case "eventAdd":
                    case "eventUpdate":
                    case "eventDelete":
                        // should not happen as we don't subscribe for this events.
                        break;

                    // case "subscriptionStart":
                    // case "subscriptionGrace":
                    // case "subscriptionStop":
                    // case "subscriptionSkip":
                    // case "subscriptionSpeed":
                    // case "subscriptionStatus":
                    //    _logger.LogCritical("[TVHclient] subscription events {Resp}", response.ToString());
                    //    break;

                    // case "queueStatus":
                    //    _logger.LogCritical("[TVHclient] queueStatus event {Resp}", response.ToString());
                    //    break;

                    // case "signalStatus":
                    //    _logger.LogCritical("[TVHclient] signalStatus event {Resp}", response.ToString());
                    //    break;

                    // case "timeshiftStatus":
                    //    _logger.LogCritical("[TVHclient] timeshiftStatus event {Resp}", response.ToString());
                    //    break;

                    // case "muxpkt": // streaming data
                    //    _logger.LogCritical("[TVHclient] muxpkt event {Resp}", response.ToString());
                    //    break;

                    case "initialSyncCompleted":
                        _initialLoadFinished = true;
                        break;

                    default:
                        // _logger.LogCritical("[TVHclient] Method '{Method}' not handled in LiveTvService.cs", response.Method);
                        break;
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Stops the reconnect loop and releases the HTSP connection held by this handler.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release managed resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _connectionLoopCts.Cancel();
                lock (_lock)
                {
                    _htsConnection?.Dispose();
                    _htsConnection = null;
                }

                _connectionLoopCts.Dispose();
            }
        }
    }
}
