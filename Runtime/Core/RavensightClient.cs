using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace RealitySE
{
    /// <summary>Thrown by the explicit request helpers. Tracking never throws on the network.</summary>
    public sealed class RavensightException : Exception
    {
        public readonly string Code;
        public readonly int Status;

        public RavensightException(string message, string code, int status)
            : base(message)
        {
            Code = code;
            Status = status;
        }
    }

    /// <summary>
    /// Why the SDK is not sending, the same values every Ravensight SDK uses.
    /// <see cref="RavensightClient.StatusReason"/> is null when it is ready.
    /// </summary>
    public static class RavensightReason
    {
        /// <summary>Switched off by the developer or opted out by the player.</summary>
        public const string Disabled = "disabled";
        /// <summary>The ingest key was refused. Automatic sending stops for the run.</summary>
        public const string InvalidKey = "invalid_key";
        /// <summary>Tracking is switched off for this game on the server.</summary>
        public const string TrackingDisabled = "tracking_disabled";
        /// <summary>No connection, a timeout or a server error. Events are kept and retried.</summary>
        public const string Offline = "offline";
        /// <summary>The server asked the SDK to wait. Events are kept.</summary>
        public const string RateLimited = "rate_limited";
        /// <summary>A session could not be opened. Events are kept and retried.</summary>
        public const string SessionFailed = "session_failed";
        /// <summary>The playtest job this run belongs to has finished.</summary>
        public const string PlaytestJobClosed = "playtest_job_closed";
        /// <summary>Started with no ingest key. Inert for the run: no storage, no request.</summary>
        public const string NoKey = "no_key";
    }

    /// <summary>When the SDK may start collecting.</summary>
    public enum RavensightConsent
    {
        /// <summary>The default: on until the player opts out with SetTrackingEnabled(false).</summary>
        OptOut = 0,
        /// <summary>
        /// Off until the player says yes with SetTrackingEnabled(true). Before
        /// the yes no device id is made, nothing is sent and tracked events
        /// are dropped. The yes is saved as ravensight_optin.
        /// </summary>
        OptIn = 1,
    }

    /// <summary>One queued analytics event.</summary>
    public sealed class RavensightEvent
    {
        public string Name;
        public Dictionary<string, object> Data;
        /// <summary>Unix seconds, captured when the event happened.</summary>
        public long Timestamp;

        public Dictionary<string, object> ToPayload()
        {
            Dictionary<string, object> payload = new Dictionary<string, object>(3);
            payload["event"] = Name;
            payload["data"] = Data ?? new Dictionary<string, object>();
            payload["timestamp"] = Timestamp;
            return payload;
        }
    }

    public sealed class RavensightConfig
    {
        /// <summary>
        /// Publishable ingest key, gt_live_... or a public build's gt_pub_...
        /// Null or empty makes the client inert: no storage, no request, and
        /// <see cref="RavensightClient.StatusReason"/> is "no_key".
        /// </summary>
        public string IngestKey;
        /// <summary>True silences the one warning an inert start (no key) logs.</summary>
        public bool Optional;
        /// <summary>OptOut (default) or OptIn. See <see cref="RavensightConsent"/>.</summary>
        public RavensightConsent Consent = RavensightConsent.OptOut;
        /// <summary>
        /// A short label sent on POST /session as "distribution", such as
        /// "native-release" or "web-demo". At most 40 characters and no
        /// control characters; anything else is not sent and logged once.
        /// </summary>
        public string Distribution;
        /// <summary>
        /// 0 &lt;= x &lt; 1, for the retry jitter. Null uses a shared System.Random.
        /// Tests pass a fixed value; 0.5 gives the unjittered backoff.
        /// </summary>
        public Func<double> Random;
        /// <summary>Host or base URL. Normalized to end in exactly one "/api/v1".</summary>
        public string ApiUrl = RavensightClient.DefaultApiUrl;
        public string GameVersion = "1.0.0";
        public string Platform = "unity";
        /// <summary>Explicit device id. Null reads the saved id, or mints and saves one.</summary>
        public string DeviceId;
        public int MaxQueueSize = RavensightClient.DefaultQueueCap;
        /// <summary>
        /// The developer's switch, never persisted. False means no request of
        /// any kind until <see cref="RavensightClient.Enabled"/> is set true.
        /// The player's own choice is <see cref="RavensightClient.SetTrackingEnabled"/>.
        /// </summary>
        public bool Enabled = true;
        /// <summary>
        /// Keeps the client disabled whatever <see cref="RavensightClient.Enabled"/>
        /// is later set to. The Unity layer sets it in Editor play mode while
        /// Send From Editor is off.
        /// </summary>
        public bool HoldDisabled;
        /// <summary>Queue game_started and game_exited automatically.</summary>
        public bool TrackLifecycleEvents = true;
        /// <summary>Background time, in seconds, after which a return to the foreground starts a new session.</summary>
        public int SessionTimeoutSeconds = RavensightClient.DefaultSessionTimeoutSeconds;
        /// <summary>
        /// A player's session token handed to a game server build. The first
        /// session opened by this client is sent with it as joinToken.
        /// </summary>
        public string JoinToken;
        /// <summary>
        /// Opt in to per session performance sampling. Off by default. When on,
        /// the Unity layer feeds frame times to <see cref="RavensightClient.SamplePerformance"/>
        /// and one <c>health</c> event is sent per session.
        /// </summary>
        public bool Performance;
        /// <summary>The playtest run, or null for a real player.</summary>
        public RavensightPlaytestContext Playtest;
        /// <summary>Persistent storage. Null keeps everything in memory.</summary>
        public IRavensightStorage Storage;
    }

    public struct RavensightInitResult
    {
        public bool TrackingEnabled;
        public bool SessionReady;
        /// <summary>Null when the session is ready, otherwise a <see cref="RavensightReason"/> value.</summary>
        public string Reason;
    }

    public struct RavensightFlushResult
    {
        public int Sent;
        public int Queued;
        /// <summary>Null when a flush ran, otherwise why it did not.</summary>
        public string Skipped;
    }

    public struct RavensightStats
    {
        public int Queued;
        public int Sent;
        public int Dropped;
        public int Flushes;
        public int Sessions;
    }

    /// <summary>
    /// The whole Ravensight protocol: sessions, batching, the offline queue,
    /// re authentication on 401, Retry-After on 429 and 503, oversize
    /// splitting on 400, the opt-out, the playtest path and the lifecycle
    /// rules. Contains no UnityEngine types so it can be unit tested with a
    /// fake transport, a fake clock and in memory storage.
    ///
    /// Game code normally uses the Ravensight MonoBehaviour rather than this
    /// class directly.
    /// </summary>
    public sealed partial class RavensightClient
    {
        public const string SdkVersion = "0.3.1";
        /// <summary>The bare host. "/api/v1" is appended by <see cref="NormalizeApiUrl"/>.</summary>
        public const string DefaultApiUrl = "https://api.ravensight.io";
        /// <summary>Server hard limit on POST /track/batch.</summary>
        public const int MaxBatchSize = 50;
        public const int DefaultQueueCap = 500;
        public const int DefaultRetryMs = 10000;
        public const int MaxBackoffMs = 300000;
        public const int DefaultSessionTimeoutSeconds = 1800;
        /// <summary>A retry is never sooner than this, jitter included.</summary>
        public const int MinRetryMs = 500;
        /// <summary>Retry waits are the backoff plus or minus this fraction.</summary>
        public const double Jitter = 0.2;
        /// <summary>The largest event the API accepts: its data as JSON, in UTF-8 bytes.</summary>
        public const int MaxEventBytes = 8192;
        /// <summary>The longest distribution label sent.</summary>
        public const int MaxDistributionLength = 40;

        public const string DeviceIdKey = "ravensight_device_id";
        public const string TrackingEnabledKey = "ravensight_tracking_enabled";
        /// <summary>Written only with <see cref="RavensightConsent.OptIn"/>, once the player has said yes.</summary>
        public const string OptInKey = "ravensight_optin";
        /// <summary>The per event id key inside each event's data.</summary>
        public const string EventIdKey = "rs_event_id";
        public const string BackgroundMarkerKey = "ravensight_bg";
        public const string PlaytestHeader = "X-Ravensight-Playtest";

        public const string InvalidKeyWarning =
            "Ravensight: ingest key refused (invalid_key), sending stopped for this run. Check your gt_live_ or gt_pub_ key.";

        public const string NoKeyWarning =
            "Ravensight: no ingest key, so analytics is off for this run (set Optional to silence this line).";

        /// <summary>The feedback categories the server accepts.</summary>
        public static readonly ReadOnlyCollection<string> FeedbackCategories = Array.AsReadOnly(new string[]
        {
            "bug", "suggestion", "complaint", "praise", "playtest", "other",
        });

        private const int ExpirySkewMs = 5000;

        private readonly IRavensightTransport _transport;
        private readonly Func<long> _nowMs;
        private readonly RavensightConfig _config;
        private readonly IRavensightStorage _storage;
        private readonly RavensightPlaytestContext _playtest;
        private readonly Dictionary<string, object> _playtestTags;
        private readonly List<RavensightEvent> _queue = new List<RavensightEvent>();
        private readonly HashSet<string> _warned = new HashSet<string>();
        private readonly object _gate = new object();

        private bool _disposed;
        private bool _flushing;
        private int _inflightCount;
        private Task<string> _sessionTask;
        private int _sessionGeneration;
        private long _nextAttemptAtMs;
        private bool _retryAfterActive;
        private int _backoffMs = DefaultRetryMs;
        private RavensightStats _stats;

        private bool _enabled;
        private bool _optedOut;
        private string _fatalReason;
        private bool _invalidKey;
        private string _status;
        private string _joinToken;

        private readonly bool _inert;
        private readonly string _distribution;
        private readonly Func<double> _random;
        private readonly long _startSeconds;
        private string _eventNonce;
        private long _eventCounter;

        private bool _begun;
        private bool _gameStartedQueued;
        private bool _gameExitedQueued;
        private long _backgroundedAtMs;
        private readonly RavensightFrameSampler _frames = new RavensightFrameSampler();
        private bool _healthSent;

        public string ApiUrl { get; private set; }
        public string DeviceId { get; private set; }
        /// <summary>
        /// The open session's token, null until one opens. Hand it to a game
        /// server so it can join this player's session (its RavensightConfig.JoinToken).
        /// </summary>
        public string SessionToken { get; private set; }
        /// <summary>Unix milliseconds.</summary>
        public long SessionExpiresAtMs { get; private set; }
        /// <summary>Server side kill switch. False for the rest of the run once the server says so.</summary>
        public bool TrackingEnabled { get; private set; }
        /// <summary>True once InitAsync has returned, whatever it found.</summary>
        public bool Initialized { get; private set; }
        /// <summary>True once GET /settings has been asked. A disabled client never asks.</summary>
        public bool SettingsChecked { get; private set; }

        /// <summary>Optional verbose log sink. The MonoBehaviour layer wires this to Debug.Log when Verbose Logging is on.</summary>
        public Action<string> Log;
        /// <summary>Warning sink for problems a developer must see. The MonoBehaviour layer always wires it to Debug.LogWarning.</summary>
        public Action<string> Warn;
        /// <summary>Raised with the new <see cref="StatusReason"/> whenever it changes.</summary>
        public event Action<string> StatusChanged;

        public RavensightClient(RavensightConfig config, IRavensightTransport transport, Func<long> nowMs)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (transport == null) throw new ArgumentNullException("transport");

            _config = config;
            _transport = transport;
            _nowMs = nowMs ?? DefaultClock;
            _storage = config.Storage ?? new RavensightMemoryStorage();
            _random = config.Random ?? SharedRandom;
            _startSeconds = _nowMs() / 1000L;

            ApiUrl = NormalizeApiUrl(config.ApiUrl);
            TrackingEnabled = true;
            _enabled = config.Enabled && !config.HoldDisabled;
            _joinToken = string.IsNullOrEmpty(config.JoinToken) || config.JoinToken.Trim().Length == 0
                ? null
                : config.JoinToken.Trim();

            if (string.IsNullOrEmpty(config.IngestKey) || config.IngestKey.Trim().Length == 0)
            {
                // No key: a public repository's clone or a development build.
                // Storage is never read or written and nothing is sent.
                _inert = true;
                DeviceId = null;
                _status = RavensightReason.NoKey;
                return;
            }

            _distribution = ValidDistribution(config.Distribution);

            if (config.Playtest != null && config.Playtest.HasToken)
            {
                // A playtest run is identified by the run, never by the saved
                // device id, and it ignores the player's saved opt-out. Storage
                // is not read or written at all.
                _playtest = new RavensightPlaytestContext(
                    config.Playtest.Token, config.Playtest.RunId, config.Playtest.JobId, config.Playtest.Persona);
                if (_playtest.HasRunId)
                {
                    DeviceId = "pt-" + _playtest.RunId;
                    _playtestTags = new Dictionary<string, object>();
                    _playtestTags["synthetic"] = true;
                    _playtestTags["persona"] = _playtest.Persona ?? "";
                    _playtestTags["pt_job"] = _playtest.JobId ?? "";
                    _playtestTags["pt_run"] = _playtest.RunId;
                    _playtestTags["pt_source"] = "sdk";
                    _playtestTags["pt_tier"] = "T2_driven_build";
                }
                else
                {
                    // Never run as a real player under a playtest token.
                    DeviceId = "";
                    _fatalReason = RavensightReason.SessionFailed;
                }
            }
            else
            {
                _optedOut = !ReadTrackingChoice(_storage, config.Consent);
                if (config.Consent == RavensightConsent.OptIn && _optedOut)
                {
                    // Before the player's yes no device id is made or read.
                    DeviceId = string.IsNullOrEmpty(config.DeviceId) ? null : config.DeviceId;
                }
                else
                {
                    DeviceId = string.IsNullOrEmpty(config.DeviceId) ? LoadOrCreateDeviceId(_storage) : config.DeviceId;
                }
                if (_optedOut && _storage.GetString(BackgroundMarkerKey) != null)
                {
                    _storage.DeleteKey(BackgroundMarkerKey);
                    _storage.Save();
                }
            }

            _status = EffectiveStatus(null);
        }

        private static long DefaultClock()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static readonly Random JitterSource = new Random();
        private static readonly object JitterGate = new object();

        private static double SharedRandom()
        {
            lock (JitterGate) { return JitterSource.NextDouble(); }
        }

        /// <summary>
        /// The distribution label as it will be sent, or null when it is unset
        /// or not sendable (over 40 characters, or a control character).
        /// </summary>
        public static string ValidDistribution(string value)
        {
            if (value == null) return null;
            string trimmed = value.Trim();
            if (trimmed.Length == 0) return null;
            if (trimmed.Length > MaxDistributionLength) return null;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (char.IsControl(trimmed[i])) return null;
            }
            return trimmed;
        }

        /// <summary>
        /// The player's saved choice for a consent mode. OptOut: true unless
        /// they opted out. OptIn: true only once the yes has been saved and
        /// not revoked.
        /// </summary>
        public static bool ReadTrackingChoice(IRavensightStorage storage, RavensightConsent consent)
        {
            if (storage.GetInt(TrackingEnabledKey, 1) == 0) return false;
            if (consent == RavensightConsent.OptIn) return storage.GetInt(OptInKey, 0) == 1;
            return true;
        }

        /// <summary>
        /// "rs_event_id": the first 16 characters of the device id, the start
        /// time in hex, the launch nonce and a counter. Any character outside
        /// A-Z, a-z, 0-9, "_" and "-" becomes "_", so it always matches
        /// ^[A-Za-z0-9_-]{1,64}$.
        /// </summary>
        public static string EventId(string deviceId, long startSeconds, string nonce, long counter)
        {
            string prefix = deviceId ?? "";
            if (prefix.Length > 16) prefix = prefix.Substring(0, 16);
            StringBuilder id = new StringBuilder(64);
            for (int i = 0; i < prefix.Length; i++)
            {
                char c = prefix[i];
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                id.Append(safe ? c : '_');
            }
            id.Append('-').Append(Math.Max(0L, startSeconds).ToString("x"));
            id.Append('-').Append(nonce);
            id.Append('-').Append(counter.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return id.ToString();
        }

        /// <summary>Eight hex characters from the platform CSPRNG.</summary>
        public static string NewLaunchNonce()
        {
            byte[] bytes = new byte[4];
            try
            {
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(bytes);
                }
            }
            catch (Exception)
            {
                bytes = Guid.NewGuid().ToByteArray();
            }
            StringBuilder hex = new StringBuilder(8);
            for (int i = 0; i < 4; i++) hex.Append(bytes[i].ToString("x2"));
            return hex.ToString();
        }

        /// <summary>The size the server checks: the event's data as JSON, in UTF-8 bytes.</summary>
        public static int EventBytes(Dictionary<string, object> data)
        {
            return Encoding.UTF8.GetByteCount(RavensightJson.Write(data ?? new Dictionary<string, object>()));
        }

        /// <summary>
        /// Trims whitespace, strips every trailing "/", strips a trailing
        /// "/api/v1", then appends "/api/v1". Empty means the default host.
        /// </summary>
        public static string NormalizeApiUrl(string url)
        {
            string raw = url == null ? "" : url.Trim();
            raw = raw.TrimEnd('/');
            if (raw.EndsWith("/api/v1", StringComparison.Ordinal))
            {
                raw = raw.Substring(0, raw.Length - "/api/v1".Length).TrimEnd('/');
            }
            if (raw.Length == 0) raw = DefaultApiUrl;
            return raw + "/api/v1";
        }

        /// <summary>A new random device id: "dev_" plus 32 hex characters from the platform CSPRNG.</summary>
        public static string NewDeviceId()
        {
            byte[] bytes = new byte[16];
            try
            {
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(bytes);
                }
            }
            catch (Exception)
            {
                // A platform without a CSPRNG still gets a random version 4 id.
                bytes = Guid.NewGuid().ToByteArray();
            }

            StringBuilder hex = new StringBuilder("dev_", 36);
            for (int i = 0; i < bytes.Length; i++) hex.Append(bytes[i].ToString("x2"));
            return hex.ToString();
        }

        /// <summary>Returns the saved device id, or mints and saves one. A saved id is never rewritten.</summary>
        public static string LoadOrCreateDeviceId(IRavensightStorage storage)
        {
            string existing = storage.GetString(DeviceIdKey);
            if (!string.IsNullOrEmpty(existing)) return existing;

            string created = NewDeviceId();
            storage.SetString(DeviceIdKey, created);
            storage.Save();
            return created;
        }

        /// <summary>
        /// Saves the player's choice without a client. Used before the client
        /// exists; a client built afterwards reads it.
        /// </summary>
        public static void WriteTrackingChoice(IRavensightStorage storage, bool on)
        {
            WriteTrackingChoice(storage, on, RavensightConsent.OptOut);
        }

        /// <summary>
        /// As <see cref="WriteTrackingChoice(IRavensightStorage, bool)"/>, for a
        /// consent mode. With OptIn a yes also saves ravensight_optin; a no
        /// always removes it. The device id is kept either way.
        /// </summary>
        public static void WriteTrackingChoice(IRavensightStorage storage, bool on, RavensightConsent consent)
        {
            storage.SetInt(TrackingEnabledKey, on ? 1 : 0);
            if (on && consent == RavensightConsent.OptIn) storage.SetInt(OptInKey, 1);
            if (!on)
            {
                storage.DeleteKey(OptInKey);
                storage.DeleteKey(BackgroundMarkerKey);
            }
            storage.Save();
        }

        /// <summary>Forgets the saved id and saves a new one, without a client.</summary>
        public static string ResetStoredDeviceId(IRavensightStorage storage)
        {
            storage.DeleteKey(DeviceIdKey);
            storage.DeleteKey(BackgroundMarkerKey);
            string created = NewDeviceId();
            storage.SetString(DeviceIdKey, created);
            storage.Save();
            return created;
        }

        // --- State ------------------------------------------------------------

        /// <summary>
        /// The developer's runtime switch, not persisted. Disabling discards
        /// anything queued. For the player's own choice use
        /// <see cref="SetTrackingEnabled"/>, which is remembered.
        /// </summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (value && _config.HoldDisabled)
                {
                    WarnOnce("hold", "Ravensight: sending is held off here (Send From Editor is off), so enabling does nothing.");
                    return;
                }
                if (value == _enabled) return;
                _enabled = value;
                if (!value) { ClearConfig(true); ClearQueue(); }
                SetStatus(null);
                if (value) QueueGameStarted();
            }
        }

        /// <summary>True when the player has opted out (saved in storage).</summary>
        public bool OptedOut { get { return _optedOut; } }

        /// <summary>True when a Ravensight Playtest token was found.</summary>
        public bool IsPlaytest { get { return _playtest != null; } }

        /// <summary>
        /// Null when ready or not yet known, otherwise a <see cref="RavensightReason"/>
        /// value saying why events are not being sent.
        /// </summary>
        public string StatusReason { get { return _status; } }

        /// <summary>
        /// Enabled, not opted out, not switched off by the server and not
        /// stopped for the run. While false nothing touches the network.
        /// </summary>
        public bool IsActive
        {
            get { return !_inert && _enabled && !_optedOut && TrackingEnabled && _fatalReason == null && !_disposed; }
        }

        /// <summary>
        /// True only while the SDK is running and collecting. False when inert
        /// (no key), switched off, opted out (or not yet opted in), switched
        /// off by the server, or after a refused key. The same value as
        /// <see cref="IsActive"/>, under the name every Ravensight SDK uses.
        /// </summary>
        public bool IsEnabled { get { return IsActive; } }

        /// <summary>True when started with no ingest key. Nothing is stored or sent this run.</summary>
        public bool IsInert { get { return _inert; } }

        /// <summary>The consent mode this client was built with.</summary>
        public RavensightConsent Consent { get { return _config.Consent; } }

        /// <summary>The distribution label sent on the session, or null when none is sent.</summary>
        public string Distribution { get { return _distribution; } }

        public int QueueCount { get { lock (_gate) { return _queue.Count; } } }

        public RavensightStats Stats
        {
            get
            {
                lock (_gate)
                {
                    RavensightStats copy = _stats;
                    copy.Queued = _queue.Count;
                    return copy;
                }
            }
        }

        /// <summary>Milliseconds until the next allowed network attempt, 0 when clear.</summary>
        public long RetryInMs
        {
            get { return Math.Max(0L, _nextAttemptAtMs - _nowMs()); }
        }

        public bool IsSessionValid()
        {
            return !string.IsNullOrEmpty(SessionToken) && _nowMs() < SessionExpiresAtMs - ExpirySkewMs;
        }

        /// <summary>
        /// The player's choice, saved to storage at once. A saved opt-out wins
        /// over <see cref="RavensightConfig.Enabled"/>. Turning it off clears
        /// the queue. Ignored, with one log line, during a playtest run.
        /// </summary>
        public void SetTrackingEnabled(bool on)
        {
            if (IsPlaytest)
            {
                WarnLine("Ravensight: SetTrackingEnabled is ignored during a playtest run.");
                return;
            }

            // Saved even while inert or switched off, so a settings toggle
            // always shows the player's real choice.
            WriteTrackingChoice(_storage, on, _config.Consent);
            if (_inert) return;
            if (_optedOut == !on) return;
            _optedOut = !on;
            if (!on)
            {
                ClearConfig(true);
                // The device id is kept; ResetDeviceId forgets it.
                ClearQueue();
                _backgroundedAtMs = 0;
            }
            else if (DeviceId == null)
            {
                // The first yes in opt-in mode: only now is an id made.
                DeviceId = LoadOrCreateDeviceId(_storage);
            }
            SetStatus(null);
            if (on) QueueGameStarted();
        }

        /// <summary>
        /// Mints and saves a new device id, clears the queue and drops the
        /// session, so nothing links the old id to the new one. Returns the new
        /// id. Ignored, with one log line, during a playtest run.
        /// </summary>
        public string ResetDeviceId()
        {
            if (IsPlaytest)
            {
                WarnLine("Ravensight: ResetDeviceId is ignored during a playtest run.");
                return DeviceId;
            }

            if (_inert || DeviceId == null || (_config.Consent == RavensightConsent.OptIn && _optedOut))
            {
                // Inert, or opt-in without a current yes: forget a saved id
                // and make none. The next yes makes a fresh one.
                ClearConfig(true);
                _storage.DeleteKey(DeviceIdKey);
                _storage.DeleteKey(BackgroundMarkerKey);
                _storage.Save();
                if (!_inert)
                {
                    DeviceId = null;
                    ClearQueue();
                    InvalidateSession();
                    _backgroundedAtMs = 0;
                }
                return null;
            }

            ClearConfig(true);
            DeviceId = ResetStoredDeviceId(_storage);
            ClearQueue();
            InvalidateSession();
            _backgroundedAtMs = 0;
            return DeviceId;
        }

        // --- Lifecycle ---------------------------------------------------------

        /// <summary>
        /// Marks the start of the run and queues game_started now, so it is
        /// first in the queue with the real start time. Nothing is queued while
        /// inactive; a client enabled later queues it then, once.
        /// </summary>
        public void Begin()
        {
            _begun = true;
            QueueGameStarted();
        }

        private bool QueueGameStarted()
        {
            if (!_begun || !_config.TrackLifecycleEvents || _gameStartedQueued || !IsActive) return false;
            _gameStartedQueued = true;
            return Track("game_started");
        }

        /// <summary>Queues game_exited once for this session. Call it on the quit path, then flush.</summary>
        public bool QueueGameExited()
        {
            if (!_config.TrackLifecycleEvents || _gameExitedQueued || !IsActive) return false;
            _gameExitedQueued = true;
            return Track("game_exited");
        }

        /// <summary>
        /// Mobile: the app went to the background. Records the time, flushes,
        /// and saves a marker so a swipe-killed app can report its exit on the
        /// next launch. Queues nothing.
        /// </summary>
        public void EnterBackground()
        {
            if (!IsActive) return;
            _backgroundedAtMs = _nowMs();
            Observe(FlushCoreAsync(false, true));

            if (IsPlaytest || !IsSessionValid()) return;
            Dictionary<string, object> marker = new Dictionary<string, object>(3);
            marker["sessionToken"] = SessionToken;
            marker["expiresAt"] = SessionExpiresAtMs / 1000L;
            marker["backgroundedAt"] = _backgroundedAtMs / 1000L;
            _storage.SetString(BackgroundMarkerKey, RavensightJson.Write(marker));
            _storage.Save();
        }

        /// <summary>
        /// Mobile: the app came back. After <see cref="RavensightConfig.SessionTimeoutSeconds"/>
        /// or more in the background, game_exited is sent under the old session
        /// with the time the app left, the session is dropped and game_started
        /// is queued for a new one. Under that, nothing is queued.
        /// </summary>
        public async Task EnterForegroundAsync()
        {
            long backgroundedAt = _backgroundedAtMs;
            _backgroundedAtMs = 0;
            if (backgroundedAt <= 0) return;

            if (!IsPlaytest)
            {
                _storage.DeleteKey(BackgroundMarkerKey);
                _storage.Save();
            }
            if (!IsActive) return;

            int timeoutSeconds = _config.SessionTimeoutSeconds > 0
                ? _config.SessionTimeoutSeconds
                : DefaultSessionTimeoutSeconds;
            if (_nowMs() - backgroundedAt < timeoutSeconds * 1000L) return;

            string oldToken = IsSessionValid() ? SessionToken : null;
            InvalidateSession();
            _gameStartedQueued = false;
            _gameExitedQueued = false;
            _healthSent = false;
            _frames.Reset();
            // Queued before any await so it leads the new session.
            QueueGameStarted();

            if (_config.TrackLifecycleEvents && oldToken != null)
            {
                await SendExitAsync(oldToken, backgroundedAt / 1000L);
            }
            await FlushCoreAsync(false, true);
        }

        /// <summary>
        /// Reads the server kill switch then opens a session. Never throws on a
        /// network failure. While inactive it makes no request at all and
        /// returns the reason; enabling the client later lets it run then.
        /// </summary>
        public async Task<RavensightInitResult> InitAsync()
        {
            RavensightInitResult result = new RavensightInitResult();
            if (!IsActive)
            {
                Initialized = true;
                result.TrackingEnabled = TrackingEnabled;
                result.Reason = _status ?? RavensightReason.Disabled;
                return result;
            }

            // A swipe-killed app left a marker: report its exit before anything else.
            PendingExit pending = TakeExitMarker();
            if (pending != null)
            {
                await SendExitAsync(pending.Token, pending.BackgroundedAt);
            }

            if (IsActive)
            {
                await CheckSettingsAsync();
                SettingsChecked = true;
            }
            Initialized = true;
            result.TrackingEnabled = TrackingEnabled;

            if (!IsActive)
            {
                result.Reason = _status ?? RavensightReason.Disabled;
                return result;
            }
            if (_invalidKey || RetryInMs > 0)
            {
                result.Reason = _status;
                return result;
            }

            try
            {
                await EnsureSessionAsync();
                result.SessionReady = true;
            }
            catch (Exception e)
            {
                // Never let a boot time network problem escape into game code.
                Emit("session not ready yet: " + e.Message);
            }

            result.TrackingEnabled = TrackingEnabled;
            result.Reason = result.SessionReady ? null : (_status ?? RavensightReason.SessionFailed);
            if (result.SessionReady && QueueCount > 0) Observe(FlushCoreAsync(false, true));
            return result;
        }

        public void Dispose()
        {
            _disposed = true;
            ClearConfig(false);
            lock (_gate)
            {
                _queue.Clear();
                _inflightCount = 0;
            }
            SessionToken = null;
            SessionExpiresAtMs = 0;
        }

        // --- Tracking ----------------------------------------------------------

        /// <summary>
        /// Queues an event. Never sends by itself, except that a full batch of
        /// 50 starts a flush. Returns false when tracking is off.
        /// </summary>
        public bool Track(string eventName, Dictionary<string, object> data)
        {
            return TrackAt(eventName, data, _nowMs() / 1000L);
        }

        public bool Track(string eventName)
        {
            return Track(eventName, null);
        }

        // --- Typed events -----------------------------------------------------
        // Thin helpers over Track: each builds the reserved rs_* properties and
        // calls the generic track, so delivery, opt out and playtest are the
        // same. See RavensightTyped for the exact property sets.

        /// <summary>A purchase. Amount is integer minor units (499 is 4.99) or a digit string.</summary>
        public bool TrackPurchase(object amount, string currency, string itemType, string itemId,
            string cart = null, string transactionId = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            return TrackTyped(RavensightTyped.Purchase(amount, currency, itemType, itemId, cart, transactionId, extra, eventName));
        }

        /// <summary>A resource gain (<c>source</c>) or spend (<c>sink</c>).</summary>
        public bool TrackResource(string flow, string resource, double amount,
            string itemType = null, string itemId = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            return TrackTyped(RavensightTyped.Resource(flow, resource, amount, itemType, itemId, extra, eventName));
        }

        /// <summary>A progression step: <c>start</c>, <c>complete</c> or <c>fail</c>.</summary>
        public bool TrackProgression(string status, string p1, string p2 = null, string p3 = null,
            double? score = null, Dictionary<string, object> extra = null, string eventName = null)
        {
            return TrackTyped(RavensightTyped.Progression(status, p1, p2, p3, score, extra, eventName));
        }

        /// <summary>An error. The message is cut to 200 characters and the stack to 2000.</summary>
        public bool TrackError(string severity, string message, string stack = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            return TrackTyped(RavensightTyped.ErrorEvent(severity, message, stack, extra, eventName));
        }

        private bool TrackTyped(RavensightTypedEvent evt)
        {
            return Track(evt.Name, evt.Data);
        }

        /// <summary>True when performance sampling was switched on in the config.</summary>
        public bool PerformanceEnabled
        {
            get { return _config.Performance; }
        }

        /// <summary>
        /// Feeds one frame's unscaled seconds. Takes a sample once a second;
        /// a no-op unless <see cref="RavensightConfig.Performance"/> is on.
        /// </summary>
        public void SamplePerformance(double frameSeconds)
        {
            if (!_config.Performance || _healthSent) return;
            if (!IsActive)
            {
                // Frames gathered while opted out or disabled are never sent.
                _frames.Reset();
                return;
            }
            _frames.Tick(frameSeconds);
        }

        /// <summary>
        /// Queues the session's one <c>health</c> event: median and 10th
        /// percentile frames per second, plus memory and boot time where the
        /// caller has them. Returns false when sampling is off, nothing was
        /// sampled, one was already sent this session, or tracking is off.
        /// Call it on background entry or quit, never per frame.
        /// </summary>
        public bool SendHealth(double? memoryMb = null, double? bootMs = null)
        {
            if (!_config.Performance || _healthSent || !IsActive) return false;
            RavensightHealthSummary summary = _frames.Summarize();
            if (summary.Samples == 0) return false;
            _healthSent = true;
            return TrackTyped(RavensightTyped.Health(summary, memoryMb, bootMs));
        }

        /// <summary>Queues an event with an explicit Unix seconds timestamp.</summary>
        public bool TrackAt(string eventName, Dictionary<string, object> data, long timestampSeconds)
        {
            if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("event name is required", "eventName");
            if (!IsActive) return false;

            RavensightEvent evt = new RavensightEvent();
            evt.Name = eventName;
            evt.Data = WithPlaytestTags(data);
            AddExposureTags(evt.Data);
            evt.Data[EventIdKey] = NextEventId();
            evt.Timestamp = timestampSeconds;

            if (EventBytes(evt.Data) > MaxEventBytes)
            {
                // The server would refuse it with a 400; it is never sent.
                lock (_gate) { _stats.Dropped++; }
                WarnOnce("event_too_large", "Ravensight: dropped event \"" + eventName + "\": its data is larger than " +
                                            MaxEventBytes + " bytes as JSON (further drops are not logged).");
                Emit("dropped event " + eventName + ": event_too_large");
                return false;
            }

            int count;
            lock (_gate)
            {
                int cap = _config.MaxQueueSize > 0 ? _config.MaxQueueSize : DefaultQueueCap;
                while (_queue.Count >= cap)
                {
                    _queue.RemoveAt(0); // drop oldest so the newest always survive
                    _stats.Dropped++;
                    if (_inflightCount > 0) _inflightCount--;
                }
                _queue.Add(evt);
                count = _queue.Count;
            }

            if (count >= MaxBatchSize) Observe(FlushCoreAsync(false, true));
            return true;
        }

        /// <summary>A copy of the caller's data with the playtest tags, if any. Never the caller's own dictionary.</summary>
        private Dictionary<string, object> WithPlaytestTags(Dictionary<string, object> data)
        {
            Dictionary<string, object> copy = data == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(data);
            if (_playtestTags != null)
            {
                foreach (KeyValuePair<string, object> tag in _playtestTags) copy[tag.Key] = tag.Value;
            }
            return copy;
        }

        /// <summary>
        /// The next rs_event_id. It lives inside the event's data, so a batch
        /// sent again after a failure carries the same ids.
        /// </summary>
        private string NextEventId()
        {
            lock (_gate)
            {
                if (_eventNonce == null) _eventNonce = NewLaunchNonce();
                _eventCounter++;
                return EventId(DeviceId, _startSeconds, _eventNonce, _eventCounter);
            }
        }

        /// <summary>
        /// Sends queued events now, in batches of at most 50, until the queue
        /// drains or the server asks the SDK to wait. Events leave the queue
        /// only once the server has accepted them. After the key was refused
        /// this still makes one attempt.
        /// </summary>
        public Task<RavensightFlushResult> FlushAsync()
        {
            return FlushCoreAsync(false, false);
        }

        /// <summary>
        /// As <see cref="FlushAsync()"/>; force also skips the exponential
        /// backoff after a network failure. A Retry-After from the server is
        /// always honored.
        /// </summary>
        public Task<RavensightFlushResult> FlushAsync(bool force)
        {
            return FlushCoreAsync(force, false);
        }

        /// <summary>The timer's flush. Skipped while the key is refused.</summary>
        public Task<RavensightFlushResult> AutoFlushAsync()
        {
            return FlushCoreAsync(false, true);
        }

        private async Task<RavensightFlushResult> FlushCoreAsync(bool force, bool automatic)
        {
            RavensightFlushResult result = new RavensightFlushResult();

            if (!IsActive)
            {
                result.Skipped = "inactive";
                return result;
            }
            if (!SettingsChecked)
            {
                // The kill switch decides first.
                result.Queued = QueueCount;
                result.Skipped = "not_ready";
                return result;
            }
            if (automatic && _invalidKey)
            {
                result.Queued = QueueCount;
                result.Skipped = RavensightReason.InvalidKey;
                return result;
            }
            if (QueueCount == 0) return result;

            lock (_gate)
            {
                if (_flushing)
                {
                    result.Queued = _queue.Count;
                    result.Skipped = "in-progress";
                    return result;
                }
                if ((!force || _retryAfterActive) && _nowMs() < _nextAttemptAtMs)
                {
                    result.Queued = _queue.Count;
                    result.Skipped = "backoff";
                    return result;
                }
                _flushing = true;
            }

            try
            {
                if (force && !_retryAfterActive) _nextAttemptAtMs = 0;
                result = await DrainAsync();
            }
            finally
            {
                lock (_gate) { _flushing = false; }
            }
            return result;
        }

        private async Task<RavensightFlushResult> DrainAsync()
        {
            int sent = 0;
            int batchLimit = MaxBatchSize;
            int reauthAttempts = 0;

            while (IsActive)
            {
                if (_nowMs() < _nextAttemptAtMs) break;
                if (QueueCount == 0) break;

                string token;
                try
                {
                    token = await EnsureSessionAsync();
                }
                catch (Exception)
                {
                    // No session available. Events stay queued for the next flush.
                    break;
                }
                if (!IsActive) break;

                List<RavensightEvent> batch;
                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    int size = Math.Min(batchLimit, _queue.Count);
                    batch = _queue.GetRange(0, size);
                    _inflightCount = size;
                }

                List<object> payload = new List<object>(batch.Count);
                for (int i = 0; i < batch.Count; i++) payload.Add(batch[i].ToPayload());

                Dictionary<string, object> body = new Dictionary<string, object>(1);
                body["events"] = payload;

                RavensightResponse response = await Send(
                    WithPlaytest(new RavensightRequest("POST", ApiUrl + "/track/batch", RavensightJson.Write(body))
                        .WithHeader("Content-Type", "application/json")
                        .WithHeader("X-Session-Token", token)));

                int accepted;
                lock (_gate)
                {
                    accepted = _inflightCount;
                    _inflightCount = 0;
                }

                if (response.Status == 202)
                {
                    lock (_gate)
                    {
                        // The cap or an opt-out may have removed some of the
                        // batch while it was in flight; remove only what is left.
                        int remove = Math.Min(accepted, _queue.Count);
                        _queue.RemoveRange(0, remove);
                        _stats.Sent += batch.Count;
                        _stats.Flushes++;
                    }
                    sent += batch.Count;
                    batchLimit = MaxBatchSize;
                    reauthAttempts = 0;
                    ResetBackoff();
                    SetStatus(null);
                    continue;
                }

                if (response.Status == 401)
                {
                    // Session expired or revoked. The batch stays queued and is
                    // re sent once a fresh session is issued, at most twice in a row.
                    InvalidateSession();
                    reauthAttempts++;
                    if (reauthAttempts > 2)
                    {
                        ScheduleBackoff();
                        SetStatus(RavensightReason.SessionFailed);
                        break;
                    }
                    Emit("session expired, re authenticating");
                    continue;
                }

                if (response.Status == 403)
                {
                    HandleForbidden(response);
                    break;
                }

                if (response.Status == 429)
                {
                    ApplyRetryAfter(response);
                    SetStatus(RavensightReason.RateLimited);
                    break;
                }

                if (response.Status == 400)
                {
                    if (batch.Count > 1)
                    {
                        batchLimit = Math.Max(1, batch.Count / 2);
                        Emit("batch rejected with HTTP 400, splitting to " + batchLimit);
                        continue;
                    }
                    // A single event the server will never take. Drop it so the
                    // rest of the queue is not blocked behind it forever.
                    lock (_gate)
                    {
                        if (accepted > 0 && _queue.Count > 0)
                        {
                            _queue.RemoveAt(0);
                            _stats.Dropped++;
                        }
                    }
                    batchLimit = MaxBatchSize;
                    WarnOnce("dropped_400", "Ravensight: the server refused an event (HTTP 400) and it was dropped. " +
                                            "Check the event name and data size.");
                    Emit("dropped one event rejected with HTTP 400");
                    continue;
                }

                if (response.Status == 503)
                {
                    ApplyRetryAfter(response);
                }
                else
                {
                    ScheduleBackoff();
                }
                SetStatus(RavensightReason.Offline);
                break;
            }

            RavensightFlushResult result = new RavensightFlushResult();
            result.Sent = sent;
            result.Queued = QueueCount;
            return result;
        }

        // --- Feedback and suggestions -------------------------------------------

        /// <summary>
        /// Checks feedback locally, the way the server would: a non-empty
        /// category must be one of <see cref="FeedbackCategories"/>, and a
        /// rating is 0 (omitted) or 1 to 5. Throws RavensightException with
        /// code "invalid_category" or "invalid_rating".
        /// </summary>
        public static void ValidateFeedback(string category, int rating)
        {
            if (!string.IsNullOrEmpty(category) && !FeedbackCategories.Contains(category))
            {
                throw new RavensightException(
                    "feedback category must be one of: " + string.Join(", ", FeedbackCategories),
                    "invalid_category", 0);
            }
            if (rating != 0 && (rating < 1 || rating > 5))
            {
                throw new RavensightException("feedback rating must be 0 (omitted) or 1 to 5", "invalid_rating", 0);
            }
        }

        /// <summary>
        /// Posts player feedback. Local failures (empty message, bad category
        /// or rating, tracking off) throw before any request is made.
        /// </summary>
        public async Task SubmitFeedbackAsync(string message, string category, int rating)
        {
            if (string.IsNullOrEmpty(message) || message.Trim().Length == 0)
            {
                throw new RavensightException("feedback message is empty", "empty_message", 0);
            }
            ValidateFeedback(category, rating);
            if (!IsActive)
            {
                throw new RavensightException("tracking is off", _inert ? RavensightReason.NoKey : RavensightReason.Disabled, 0);
            }

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["message"] = message;
            if (!string.IsNullOrEmpty(category)) payload["category"] = category;
            if (rating > 0) payload["rating"] = rating;
            string json = RavensightJson.Write(payload);

            RavensightResponse response = await SendFeedbackAsync(json);
            if (response.Status == 401)
            {
                InvalidateSession();
                response = await SendFeedbackAsync(json);
            }
            if (response.Status == 403) HandleForbidden(response);
            if (response.Status == 429) ApplyRetryAfter(response);

            if (response.Status != 201)
            {
                throw new RavensightException(
                    "feedback submission failed (HTTP " + response.Status + ")",
                    response.ErrorCode("feedback_failed"),
                    response.Status);
            }
        }

        public Task SubmitFeedbackAsync(string message)
        {
            return SubmitFeedbackAsync(message, null, 0);
        }

        private async Task<RavensightResponse> SendFeedbackAsync(string json)
        {
            string token = await EnsureSessionAsync();
            return await Send(
                WithPlaytest(new RavensightRequest("POST", ApiUrl + "/feedback", json)
                    .WithHeader("Content-Type", "application/json")
                    .WithHeader("X-Session-Token", token)));
        }

        /// <summary>
        /// EXPERIMENTAL. AI generated design suggestions for this game. Empty
        /// until the game has enough data for a weekly digest, and empty with
        /// no request while tracking is off. The shape of each entry may
        /// change, so do not build game logic on it.
        /// </summary>
        public async Task<List<object>> FetchSuggestionsAsync()
        {
            if (!IsActive) return new List<object>();

            RavensightResponse response = await Send(
                WithPlaytest(new RavensightRequest("GET", ApiUrl + "/agent/suggestions", null)
                    .WithHeader("X-API-Key", _config.IngestKey)));

            if (response.Status == 401) RefuseKey();
            if (response.Status == 403) HandleForbidden(response);
            if (response.Status != 200)
            {
                throw new RavensightException(
                    "fetch suggestions failed (HTTP " + response.Status + ")",
                    response.ErrorCode("suggestions_failed"),
                    response.Status);
            }

            List<object> suggestions = RavensightJson.GetArray(response.Json(), "suggestions");
            return suggestions ?? new List<object>();
        }

        // --- Settings and session ------------------------------------------------

        private async Task CheckSettingsAsync()
        {
            RavensightResponse response = await Send(
                WithPlaytest(new RavensightRequest("GET", ApiUrl + "/settings", null)
                    .WithHeader("X-API-Key", _config.IngestKey)));

            bool enabled;
            if (response.Status == 200 && RavensightJson.TryGetBool(response.Json(), "trackingEnabled", out enabled))
            {
                if (!enabled) DisableTracking();
                return;
            }
            if (response.Status == 401)
            {
                EndRunForKey();
                return;
            }
            if (response.Status == 403)
            {
                HandleForbidden(response, true);
                return;
            }
            if (response.Status == 429)
            {
                ApplyRetryAfter(response);
                SetStatus(RavensightReason.RateLimited);
                return;
            }

            // Unreachable or malformed. Tracking is assumed on, so a settings
            // outage does not lose a session; the session request decides next.
            Emit("settings check failed (HTTP " + response.Status + "), assuming enabled");
        }

        private void InvalidateSession()
        {
            lock (_gate)
            {
                SessionToken = null;
                SessionExpiresAtMs = 0;
                _sessionTask = null;
                _sessionGeneration++;
            }
        }

        /// <summary>Returns a valid session token, creating one if needed. Deduplicated.</summary>
        private Task<string> EnsureSessionAsync()
        {
            if (IsSessionValid()) return Task.FromResult(SessionToken);

            lock (_gate)
            {
                if (_sessionTask != null && !_sessionTask.IsCompleted) return _sessionTask;
                _sessionTask = CreateSessionAsync();
                return _sessionTask;
            }
        }

        private async Task<string> CreateSessionAsync()
        {
            int generation;
            lock (_gate) { generation = _sessionGeneration; }

            while (true)
            {
                string joinToken = _joinToken;
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["deviceId"] = DeviceId;
                body["gameVersion"] = _config.GameVersion;
                body["platform"] = _config.Platform;
                body["sdk"] = "unity/" + SdkVersion;
                if (joinToken != null) body["joinToken"] = joinToken;
                if (_distribution != null) body["distribution"] = _distribution;

                RavensightResponse response = await Send(
                    WithPlaytest(new RavensightRequest("POST", ApiUrl + "/session", RavensightJson.Write(body))
                        .WithHeader("Content-Type", "application/json")
                        .WithHeader("X-API-Key", _config.IngestKey)));

                object parsed = response.Json();
                string token = RavensightJson.GetString(parsed, "token", null);

                if (response.Status == 201 && !string.IsNullOrEmpty(token))
                {
                    lock (_gate)
                    {
                        if (generation != _sessionGeneration || _disposed)
                        {
                            // Reset or disposed while this was in flight: the
                            // session belongs to an identity that is gone.
                            throw new RavensightException("session discarded after a reset", RavensightReason.SessionFailed, 0);
                        }
                        SessionToken = token;
                        SessionExpiresAtMs = ResolveExpiryMs(parsed);
                        _stats.Sessions++;
                    }
                    if (joinToken != null && _joinToken == joinToken) _joinToken = null;
                    _invalidKey = false;
                    ResetBackoff();
                    SetStatus(null);
                    Emit("session ready");
                    await FetchConfigAsync();
                    return token;
                }

                string code = response.ErrorCode(null);

                // A stale or foreign join token: forget it and open a plain session.
                if (response.Status == 401 && joinToken != null && code == "invalid_join_token")
                {
                    _joinToken = null;
                    Emit("join token refused, opening a plain session");
                    continue;
                }

                string reason;
                if (response.Status == 401)
                {
                    EndRunForKey();
                    reason = RavensightReason.InvalidKey;
                }
                else if (response.Status == 403)
                {
                    reason = HandleForbidden(response, true);
                }
                else if (response.Status == 429)
                {
                    ApplyRetryAfter(response);
                    reason = RavensightReason.RateLimited;
                }
                else if (response.Status == 0)
                {
                    ScheduleBackoff();
                    reason = RavensightReason.Offline;
                }
                else
                {
                    if (response.Status == 503) ApplyRetryAfter(response);
                    else ScheduleBackoff();
                    reason = RavensightReason.SessionFailed;
                }
                SetStatus(reason);

                throw new RavensightException(
                    "session creation failed (HTTP " + response.Status + ")",
                    reason,
                    response.Status);
            }
        }

        private long ResolveExpiryMs(object parsed)
        {
            double expiresAt;
            if (RavensightJson.TryGetDouble(parsed, "expiresAt", out expiresAt) && expiresAt > 0)
            {
                return (long)(expiresAt * 1000d);
            }

            // The API sends expiresAt as an ISO 8601 string and expiresIn in seconds.
            double expiresIn;
            if (!RavensightJson.TryGetDouble(parsed, "expiresIn", out expiresIn) || expiresIn <= 0)
            {
                expiresIn = 86400d;
            }
            return _nowMs() + (long)(expiresIn * 1000d);
        }

        // --- Exit markers ---------------------------------------------------------

        private sealed class PendingExit
        {
            public string Token;
            public long BackgroundedAt;
        }

        /// <summary>Reads and deletes the saved background marker. Null when there is nothing to send.</summary>
        private PendingExit TakeExitMarker()
        {
            if (IsPlaytest) return null;
            string raw = _storage.GetString(BackgroundMarkerKey);
            if (raw == null) return null;
            _storage.DeleteKey(BackgroundMarkerKey);
            _storage.Save();

            if (_optedOut || !_config.TrackLifecycleEvents) return null;
            object parsed = RavensightJson.Parse(raw);
            string token = RavensightJson.GetString(parsed, "sessionToken", null);
            double expiresAt;
            double backgroundedAt;
            if (string.IsNullOrEmpty(token)) return null;
            if (!RavensightJson.TryGetDouble(parsed, "expiresAt", out expiresAt)) return null;
            if (!RavensightJson.TryGetDouble(parsed, "backgroundedAt", out backgroundedAt)) return null;
            if ((long)(expiresAt * 1000d) <= _nowMs()) return null;

            PendingExit pending = new PendingExit();
            pending.Token = token;
            pending.BackgroundedAt = (long)backgroundedAt;
            return pending;
        }

        /// <summary>One POST /track/batch carrying only game_exited, under the given session. No retry.</summary>
        private async Task SendExitAsync(string sessionToken, long timestampSeconds)
        {
            RavensightEvent exit = new RavensightEvent();
            exit.Name = "game_exited";
            exit.Data = WithPlaytestTags(new Dictionary<string, object>());
            exit.Data[EventIdKey] = NextEventId();
            exit.Timestamp = timestampSeconds;

            List<object> events = new List<object>(1);
            events.Add(exit.ToPayload());
            Dictionary<string, object> body = new Dictionary<string, object>(1);
            body["events"] = events;

            RavensightResponse response = await Send(
                WithPlaytest(new RavensightRequest("POST", ApiUrl + "/track/batch", RavensightJson.Write(body))
                    .WithHeader("Content-Type", "application/json")
                    .WithHeader("X-Session-Token", sessionToken)));

            if (response.Status == 202)
            {
                lock (_gate) { _stats.Sent++; }
                return;
            }
            if (response.Status == 403) HandleForbidden(response);
            Emit("exit report not delivered (HTTP " + response.Status + ")");
        }

        // --- Refusals ---------------------------------------------------------------

        /// <summary>
        /// Applies a 403 from any endpoint and returns the resulting reason:
        /// the kill switch, a closed or invalid playtest, or a refused key.
        /// </summary>
        private string HandleForbidden(RavensightResponse response)
        {
            return HandleForbidden(response, false);
        }

        /// <summary>
        /// As above. endsRun is true for the settings check and POST /session,
        /// where a refused key ends the run instead of only pausing the timer.
        /// </summary>
        private string HandleForbidden(RavensightResponse response, bool endsRun)
        {
            string code = response.ErrorCode(null);
            if (code == "tracking_disabled")
            {
                DisableTracking();
                return RavensightReason.TrackingDisabled;
            }
            if (code == "playtest_job_closed")
            {
                StopForRun(RavensightReason.PlaytestJobClosed,
                    "Ravensight: the playtest job has closed, sending stopped for this run.");
                return RavensightReason.PlaytestJobClosed;
            }
            if (code == "invalid_playtest_token")
            {
                StopForRun(RavensightReason.SessionFailed,
                    "Ravensight: the playtest token was refused (invalid_playtest_token), sending stopped for this run.");
                return RavensightReason.SessionFailed;
            }
            if (endsRun) EndRunForKey();
            else RefuseKey();
            return RavensightReason.InvalidKey;
        }

        /// <summary>The server switched tracking off for this game. Final for the run.</summary>
        private void DisableTracking()
        {
            TrackingEnabled = false;
            ClearConfig(true);
            ClearQueue();
            SetStatus(null);
            WarnOnce("tracking_disabled", "Ravensight: tracking is switched off for this game on the server, nothing is sent this run.");
        }

        /// <summary>
        /// The key was refused. Retrying on a timer cannot fix that, so the
        /// automatic flushes stop; Track still queues and FlushAsync tries once more.
        /// </summary>
        private void RefuseKey()
        {
            _invalidKey = true;
            ClearConfig(true);
            SetStatus(RavensightReason.InvalidKey);
            WarnOnce("invalid_key", InvalidKeyWarning);
        }

        /// <summary>
        /// The settings check or POST /session refused the key (revoked,
        /// rotated or wrong). A retry cannot help, so the run ends here: the
        /// queue is cleared, nothing more is sent, and a later
        /// SetTrackingEnabled(true) restarts nothing. The next launch tries
        /// the key again.
        /// </summary>
        private void EndRunForKey()
        {
            _invalidKey = true;
            ClearConfig(true);
            StopForRun(RavensightReason.InvalidKey, InvalidKeyWarning);
        }

        private void StopForRun(string reason, string message)
        {
            ClearConfig(true);
            if (_fatalReason == null) _fatalReason = reason;
            ClearQueue();
            SetStatus(null);
            WarnOnce("fatal_" + reason, message);
        }

        private void ClearQueue()
        {
            lock (_gate)
            {
                _stats.Dropped += _queue.Count;
                _queue.Clear();
                _inflightCount = 0;
            }
        }

        // --- Status -------------------------------------------------------------------

        private string EffectiveStatus(string networkReason)
        {
            if (_inert) return RavensightReason.NoKey;
            if (_fatalReason != null) return _fatalReason;
            if (!TrackingEnabled) return RavensightReason.TrackingDisabled;
            if (!_enabled || _optedOut) return RavensightReason.Disabled;
            if (_invalidKey) return RavensightReason.InvalidKey;
            return networkReason;
        }

        private void SetStatus(string networkReason)
        {
            string next = EffectiveStatus(networkReason);
            if (next == _status) return;
            _status = next;
            Action<string> handler = StatusChanged;
            if (handler != null) handler(next);
        }

        // --- Backoff --------------------------------------------------------------

        private void ResetBackoff()
        {
            _backoffMs = DefaultRetryMs;
            _nextAttemptAtMs = 0;
            _retryAfterActive = false;
        }

        /// <summary>
        /// The next wait is the backoff plus or minus 20 percent, so many
        /// copies of a game that lost the network together do not come back
        /// together. Never under half a second, never over 5 minutes. A
        /// server's Retry-After is applied exactly, elsewhere.
        /// </summary>
        private void ScheduleBackoff()
        {
            _retryAfterActive = false;
            _nextAttemptAtMs = _nowMs() + JitteredWaitMs(_backoffMs, _random());
            _backoffMs = Math.Min(_backoffMs * 2, MaxBackoffMs);
        }

        /// <summary>The jittered wait for a backoff step. random is 0 &lt;= x &lt; 1; 0.5 returns the step itself.</summary>
        public static long JitteredWaitMs(int backoffMs, double random)
        {
            if (double.IsNaN(random) || random < 0) random = 0;
            if (random >= 1) random = 1;
            double factor = 1 - Jitter + 2 * Jitter * random;
            long wait = (long)Math.Round(backoffMs * factor);
            if (wait > MaxBackoffMs) wait = MaxBackoffMs;
            if (wait < MinRetryMs) wait = MinRetryMs;
            return wait;
        }

        private void ApplyRetryAfter(RavensightResponse response)
        {
            int wait = response.RetryAfterMs();
            if (wait > 0)
            {
                _nextAttemptAtMs = _nowMs() + wait;
                _retryAfterActive = true;
                Emit("honoring Retry-After of " + wait + "ms");
                return;
            }
            ScheduleBackoff();
        }

        // --- Plumbing ---------------------------------------------------------------

        private RavensightRequest WithPlaytest(RavensightRequest request)
        {
            // The bare token; the server also accepts a "Bearer " prefix.
            if (_playtest != null) request.WithHeader(PlaytestHeader, _playtest.Token);
            return request;
        }

        private Task<RavensightResponse> Send(RavensightRequest request)
        {
            return _transport.SendAsync(request);
        }

        private static void Observe(Task task)
        {
            // Fire and forget, but observe the task so a failure never surfaces
            // as an unobserved exception.
            task.ContinueWith(t => { Exception ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void Emit(string message)
        {
            Action<string> sink = Log;
            if (sink != null) sink(message);
        }

        private void WarnLine(string message)
        {
            Action<string> sink = Warn;
            if (sink != null) sink(message);
            else Emit(message);
        }

        private void WarnOnce(string key, string message)
        {
            lock (_gate)
            {
                if (!_warned.Add(key)) return;
            }
            WarnLine(message);
        }

        /// <summary>Logs the run-stopping reason found at construction, if any. Call after wiring Warn.</summary>
        public void ReportStartupProblems()
        {
            if (_inert)
            {
                if (!_config.Optional)
                {
                    WarnOnce(RavensightReason.NoKey, NoKeyWarning);
                }
                return;
            }
            if (_config.Distribution != null && _config.Distribution.Trim().Length > 0 && _distribution == null)
            {
                WarnOnce("distribution", "Ravensight: the distribution label is over " + MaxDistributionLength +
                                         " characters or has a control character, so it is not sent.");
            }
            if (_playtest != null && !_playtest.HasRunId)
            {
                WarnOnce("fatal_" + RavensightReason.SessionFailed,
                    "Ravensight: a playtest token was found without a run id, so this run does not report. " +
                    "Set RAVENSIGHT_PLAYTEST_RUN_ID.");
            }
        }
    }
}
