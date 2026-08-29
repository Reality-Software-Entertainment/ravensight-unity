using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RealitySE
{
    /// <summary>Thrown by the explicit request helpers. Tracking never throws.</summary>
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
        /// <summary>Publishable ingest key, format gt_live_...</summary>
        public string IngestKey;
        /// <summary>Base URL. "/api/v1" is appended when omitted.</summary>
        public string ApiUrl = RavensightClient.DefaultApiUrl;
        public string GameVersion = "1.0.0";
        public string Platform = "unity";
        public string DeviceId;
        public int MaxQueueSize = RavensightClient.DefaultQueueCap;
        /// <summary>Start opted out by setting this false.</summary>
        public bool Enabled = true;
    }

    public struct RavensightInitResult
    {
        public bool TrackingEnabled;
        public bool SessionReady;
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
    /// re authentication on 401, Retry-After on 429 and oversize splitting on
    /// 400. Contains no UnityEngine types so it can be unit tested with a fake
    /// transport and a fake clock.
    ///
    /// Game code normally uses the Ravensight MonoBehaviour rather than this
    /// class directly.
    /// </summary>
    public sealed class RavensightClient
    {
        public const string DefaultApiUrl = "https://api.ravensight.io/api/v1";
        /// <summary>Server hard limit on POST /track/batch.</summary>
        public const int MaxBatchSize = 50;
        public const int DefaultQueueCap = 500;
        public const int DefaultRetryMs = 10000;
        public const int MaxBackoffMs = 300000;
        private const int ExpirySkewMs = 5000;

        private readonly IRavensightTransport _transport;
        private readonly Func<long> _nowMs;
        private readonly RavensightConfig _config;
        private readonly List<RavensightEvent> _queue = new List<RavensightEvent>();
        private readonly object _gate = new object();

        private bool _disposed;
        private bool _flushing;
        private Task<string> _sessionTask;
        private long _nextAttemptAtMs;
        private int _backoffMs = DefaultRetryMs;
        private RavensightStats _stats;

        public string ApiUrl { get; private set; }
        public string DeviceId { get; private set; }
        public string SessionToken { get; private set; }
        /// <summary>Unix milliseconds.</summary>
        public long SessionExpiresAtMs { get; private set; }
        /// <summary>Server side kill switch from GET /settings.</summary>
        public bool TrackingEnabled { get; private set; }
        public bool Initialized { get; private set; }

        /// <summary>Optional log sink. The MonoBehaviour layer wires this to Debug.Log.</summary>
        public Action<string> Log;

        public RavensightClient(RavensightConfig config, IRavensightTransport transport, Func<long> nowMs)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (transport == null) throw new ArgumentNullException("transport");
            if (string.IsNullOrEmpty(config.IngestKey) || config.IngestKey.Trim().Length == 0)
            {
                throw new ArgumentException("Ravensight: IngestKey is required (your gt_live_... key)", "config");
            }

            _config = config;
            _transport = transport;
            _nowMs = nowMs ?? DefaultClock;

            ApiUrl = NormalizeApiUrl(config.ApiUrl);
            DeviceId = string.IsNullOrEmpty(config.DeviceId) ? "dev_" + Guid.NewGuid().ToString("N") : config.DeviceId;
            TrackingEnabled = true;
            Enabled = config.Enabled;
        }

        private static long DefaultClock()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        /// <summary>Accepts a bare host or a full /api/v1 base and always returns the versioned base.</summary>
        public static string NormalizeApiUrl(string url)
        {
            string raw = string.IsNullOrEmpty(url) ? DefaultApiUrl : url.Trim();
            raw = raw.TrimEnd('/');
            if (raw.Length == 0) raw = DefaultApiUrl.TrimEnd('/');
            if (raw.EndsWith("/api/v1", StringComparison.Ordinal)) return raw;
            return raw + "/api/v1";
        }

        // --- State ------------------------------------------------------------

        /// <summary>Local opt in and opt out. Disabling discards anything queued.</summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (value == _enabled) return;
                _enabled = value;
                if (!value)
                {
                    lock (_gate)
                    {
                        _stats.Dropped += _queue.Count;
                        _queue.Clear();
                    }
                }
            }
        }
        private bool _enabled = true;

        public bool IsActive { get { return _enabled && TrackingEnabled && !_disposed; } }

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

        // --- Lifecycle ---------------------------------------------------------

        /// <summary>
        /// Reads the server kill switch then opens a session. Never throws on a
        /// network failure: an unreachable settings endpoint means tracking is
        /// assumed on, and a failed session leaves events queued for retry.
        /// </summary>
        public async Task<RavensightInitResult> InitAsync()
        {
            RavensightInitResult result = new RavensightInitResult();
            if (_disposed) return result;

            await CheckSettingsAsync();
            Initialized = true;
            result.TrackingEnabled = TrackingEnabled;

            if (!TrackingEnabled)
            {
                lock (_gate) { _queue.Clear(); }
                Emit("tracking disabled by server kill switch");
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

            return result;
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_gate) { _queue.Clear(); }
            SessionToken = null;
            SessionExpiresAtMs = 0;
        }

        // --- Tracking ----------------------------------------------------------

        /// <summary>
        /// Queues an event. Safe to call before InitAsync. Returns false when
        /// tracking is off.
        /// </summary>
        public bool Track(string eventName, Dictionary<string, object> data)
        {
            if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("event name is required", "eventName");
            if (!IsActive) return false;

            RavensightEvent evt = new RavensightEvent();
            evt.Name = eventName;
            evt.Data = data;
            evt.Timestamp = _nowMs() / 1000L;

            lock (_gate)
            {
                int cap = _config.MaxQueueSize > 0 ? _config.MaxQueueSize : DefaultQueueCap;
                while (_queue.Count >= cap)
                {
                    _queue.RemoveAt(0); // drop oldest so the newest always survive
                    _stats.Dropped++;
                }
                _queue.Add(evt);
            }
            return true;
        }

        public bool Track(string eventName)
        {
            return Track(eventName, null);
        }

        /// <summary>
        /// Sends queued events in batches of at most 50 until the queue drains
        /// or the server asks us to back off. Events leave the queue only once
        /// the server has accepted them.
        /// </summary>
        public async Task<RavensightFlushResult> FlushAsync(bool force)
        {
            RavensightFlushResult result = new RavensightFlushResult();

            if (!IsActive)
            {
                result.Skipped = "inactive";
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
                if (!force && _nowMs() < _nextAttemptAtMs)
                {
                    result.Queued = _queue.Count;
                    result.Skipped = "backoff";
                    return result;
                }
                _flushing = true;
            }

            try
            {
                result = await DrainAsync();
            }
            finally
            {
                lock (_gate) { _flushing = false; }
            }
            return result;
        }

        public Task<RavensightFlushResult> FlushAsync()
        {
            return FlushAsync(false);
        }

        private async Task<RavensightFlushResult> DrainAsync()
        {
            int sent = 0;
            int batchLimit = MaxBatchSize;
            int reauthAttempts = 0;

            while (true)
            {
                List<RavensightEvent> batch;
                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    int size = Math.Min(batchLimit, _queue.Count);
                    batch = _queue.GetRange(0, size);
                }

                if (_nowMs() < _nextAttemptAtMs) break;

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

                List<object> payload = new List<object>(batch.Count);
                for (int i = 0; i < batch.Count; i++) payload.Add(batch[i].ToPayload());

                Dictionary<string, object> body = new Dictionary<string, object>(1);
                body["events"] = payload;

                RavensightRequest request = new RavensightRequest("POST", ApiUrl + "/track/batch", RavensightJson.Write(body))
                    .WithHeader("Content-Type", "application/json")
                    .WithHeader("X-Session-Token", token);

                RavensightResponse response = await _transport.SendAsync(request);

                if (response.Status == 202)
                {
                    lock (_gate)
                    {
                        int remove = Math.Min(batch.Count, _queue.Count);
                        _queue.RemoveRange(0, remove);
                        _stats.Sent += remove;
                        _stats.Flushes++;
                        sent += remove;
                    }
                    batchLimit = MaxBatchSize;
                    ResetBackoff();
                    continue;
                }

                if (response.Status == 401)
                {
                    // Session expired or revoked. The batch stays queued and is
                    // re sent as soon as a fresh session is issued.
                    InvalidateSession();
                    reauthAttempts++;
                    if (reauthAttempts > 2) break;
                    Emit("session expired, re authenticating");
                    continue;
                }

                if (response.Status == 429)
                {
                    ApplyRetryAfter(response);
                    break;
                }

                if (response.Status == 400)
                {
                    if (batch.Count > 1)
                    {
                        batchLimit = Math.Max(1, batch.Count / 2);
                        Emit("batch rejected as oversize, splitting to " + batchLimit);
                        continue;
                    }
                    // A single event the server will never take. Drop it so the
                    // rest of the queue is not blocked behind it forever.
                    lock (_gate)
                    {
                        if (_queue.Count > 0)
                        {
                            _queue.RemoveAt(0);
                            _stats.Dropped++;
                        }
                    }
                    batchLimit = MaxBatchSize;
                    Emit("dropped one event rejected with HTTP 400");
                    continue;
                }

                ScheduleBackoff();
                break;
            }

            RavensightFlushResult result = new RavensightFlushResult();
            result.Sent = sent;
            result.Queued = QueueCount;
            return result;
        }

        // --- Feedback and suggestions -------------------------------------------

        public async Task SubmitFeedbackAsync(string message, string category, int rating)
        {
            if (string.IsNullOrEmpty(message) || message.Trim().Length == 0)
            {
                throw new RavensightException("feedback message is empty", "empty_message", 0);
            }
            if (!IsActive)
            {
                throw new RavensightException("tracking is disabled", "tracking_disabled", 0);
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
            RavensightRequest request = new RavensightRequest("POST", ApiUrl + "/feedback", json)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("X-Session-Token", token);
            return await _transport.SendAsync(request);
        }

        /// <summary>
        /// EXPERIMENTAL. AI generated design suggestions for this game. Empty
        /// until the game has enough data for a weekly digest, and the shape of
        /// each entry may change, so do not build game logic on it.
        /// </summary>
        public async Task<List<object>> FetchSuggestionsAsync()
        {
            RavensightRequest request = new RavensightRequest("GET", ApiUrl + "/agent/suggestions", null)
                .WithHeader("X-API-Key", _config.IngestKey);

            RavensightResponse response = await _transport.SendAsync(request);
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
            RavensightRequest request = new RavensightRequest("GET", ApiUrl + "/settings", null)
                .WithHeader("X-API-Key", _config.IngestKey);

            RavensightResponse response = await _transport.SendAsync(request);

            bool enabled;
            if (response.Status == 200 && RavensightJson.TryGetBool(response.Json(), "trackingEnabled", out enabled))
            {
                TrackingEnabled = enabled;
                return;
            }

            // Unreachable or malformed. Assume tracking is on, same as the
            // Godot SDK, so a settings outage does not silently lose a session.
            TrackingEnabled = true;
            if (response.Status != 200)
            {
                Emit("settings check failed (HTTP " + response.Status + "), assuming enabled");
            }
        }

        private void InvalidateSession()
        {
            SessionToken = null;
            SessionExpiresAtMs = 0;
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
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["deviceId"] = DeviceId;
            body["gameVersion"] = _config.GameVersion;
            body["platform"] = _config.Platform;

            RavensightRequest request = new RavensightRequest("POST", ApiUrl + "/session", RavensightJson.Write(body))
                .WithHeader("Content-Type", "application/json")
                .WithHeader("X-API-Key", _config.IngestKey);

            RavensightResponse response = await _transport.SendAsync(request);
            object parsed = response.Json();
            string token = RavensightJson.GetString(parsed, "token", null);

            if (response.Status == 201 && !string.IsNullOrEmpty(token))
            {
                SessionToken = token;
                SessionExpiresAtMs = ResolveExpiryMs(parsed);
                lock (_gate) { _stats.Sessions++; }
                ResetBackoff();
                Emit("session ready");
                return token;
            }

            if (response.Status == 429)
            {
                ApplyRetryAfter(response);
                throw new RavensightException("session creation rate limited", "rate_limited", 429);
            }

            ScheduleBackoff();
            throw new RavensightException(
                "session creation failed (HTTP " + response.Status + ")",
                response.ErrorCode("session_failed"),
                response.Status);
        }

        private long ResolveExpiryMs(object parsed)
        {
            double expiresAt;
            if (RavensightJson.TryGetDouble(parsed, "expiresAt", out expiresAt) && expiresAt > 0)
            {
                return (long)(expiresAt * 1000d);
            }

            double expiresIn;
            if (!RavensightJson.TryGetDouble(parsed, "expiresIn", out expiresIn) || expiresIn <= 0)
            {
                expiresIn = 86400d;
            }
            return _nowMs() + (long)(expiresIn * 1000d);
        }

        // --- Backoff --------------------------------------------------------------

        private void ResetBackoff()
        {
            _backoffMs = DefaultRetryMs;
            _nextAttemptAtMs = 0;
        }

        private void ScheduleBackoff()
        {
            _nextAttemptAtMs = _nowMs() + _backoffMs;
            _backoffMs = Math.Min(_backoffMs * 2, MaxBackoffMs);
        }

        private void ApplyRetryAfter(RavensightResponse response)
        {
            int wait = response.RetryAfterMs();
            if (wait > 0)
            {
                _nextAttemptAtMs = _nowMs() + wait;
                Emit("rate limited, honoring Retry-After of " + wait + "ms");
                return;
            }
            ScheduleBackoff();
        }

        private void Emit(string message)
        {
            Action<string> sink = Log;
            if (sink != null) sink(message);
        }
    }
}
