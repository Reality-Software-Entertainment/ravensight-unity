using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RealitySE
{
    /// <summary>A signing session's secret and the server clock offset. Memory only.</summary>
    public sealed class RavensightSigning
    {
        public string Secret;
        /// <summary>serverTime minus the device clock, in seconds, measured when the session answer arrived.</summary>
        public long OffsetSeconds;
    }

    // The platform's SDK contracts: the persisted queue and origin replay
    // (delivery), ads and attribution, experiment
    // exposure and device fields. The response table itself is in DrainAsync.
    public sealed partial class RavensightClient
    {
        /// <summary>Stored beside the device id once this device's attribution was queued.</summary>
        public const string AttributionSentKey = "ravensight_attribution_sent";
        /// <summary>A burst of tracked events is written to the queue file at most this often; flushes, background and quits write at once.</summary>
        public const int PersistIntervalMs = 1000;

        private static readonly Regex VariantName = new Regex("^[a-z][a-z0-9_-]{0,39}$");

        private Dictionary<string, object> _origin;
        private RavensightSigning _signing;
        private bool _sendBlocked;
        private RavensightQueueStore _queueStore;
        private bool _persistDirty;
        private long _lastPersistMs;
        private int _sessionsOpened;
        /// <summary>Counts analytics sessions in this run, for exposure.</summary>
        private int _sessionEpoch = 1;
        /// <summary>Set when a session break was already counted before the next session opened.</summary>
        private bool _breakCounted;
        private readonly HashSet<string> _exposedKeys = new HashSet<string>();
        private int _exposedGeneration = -1;

        /// <summary>The current session's origin as the server sent it, or null. Stored with the queue, never logged.</summary>
        internal Dictionary<string, object> CurrentOrigin { get { return _origin; } }

        /// <summary>True when the current session signs its requests.</summary>
        public bool IsSigning { get { return _signing != null; } }

        /// <summary>The persisted queue's file, or null when the queue is in memory only.</summary>
        public string QueueFilePath { get { return _queueStore == null ? null : _queueStore.FilePath; } }

        // --- Persisted queue ---------------------------------------------------------

        private bool QueueAllowed
        {
            get { return _queueStore != null && !_inert && _playtest == null && !_optedOut && DeviceId != null; }
        }

        /// <summary>Opens the queue file and reads it back once. A bad file is an empty queue.</summary>
        private void OpenQueue()
        {
            if (_queueStore != null || _inert || _playtest != null || _optedOut || DeviceId == null) return;
            if (string.IsNullOrEmpty(_config.QueueDirectory)) return;
            RavensightQueueStore store;
            try
            {
                store = new RavensightQueueStore(_config.QueueDirectory, _config.IngestKey);
            }
            catch (Exception)
            {
                return;
            }
            _queueStore = store;
            RavensightQueueStore.LoadResult loaded = store.Load(_nowMs());
            lock (_gate)
            {
                _stats.Expired += loaded.Expired;
                _stats.Dropped += loaded.Trimmed;
                if (loaded.Events.Count == 0) return;
                _queue.InsertRange(0, loaded.Events);
                _stats.Restored += loaded.Events.Count;
                int cap = Math.Min(_config.MaxQueueSize > 0 ? _config.MaxQueueSize : DefaultQueueCap, RavensightQueueStore.MaxEvents);
                while (_queue.Count > cap)
                {
                    _queue.RemoveAt(0);
                    _stats.Dropped++;
                }
            }
        }

        /// <summary>Writes the queue soon: at once if the last write was a second ago, else on the next PersistIfDirty.</summary>
        private void PersistSoon()
        {
            if (_queueStore == null) return;
            if (_nowMs() - _lastPersistMs >= PersistIntervalMs) PersistNow();
            else _persistDirty = true;
        }

        /// <summary>Writes the queue if a change is waiting. The Unity layer calls it every frame; it costs nothing when clean.</summary>
        public void PersistIfDirty()
        {
            if (_persistDirty && _nowMs() - _lastPersistMs >= PersistIntervalMs) PersistNow();
        }

        /// <summary>Writes the queue now, atomically. An empty queue, or a player who opted out, removes the file.</summary>
        public bool PersistNow()
        {
            RavensightQueueStore store = _queueStore;
            if (store == null) return false;
            _persistDirty = false;
            _lastPersistMs = _nowMs();
            if (!QueueAllowed)
            {
                store.Delete();
                return true;
            }
            List<RavensightEvent> snapshot;
            lock (_gate) { snapshot = new List<RavensightEvent>(_queue); }
            return store.Save(snapshot);
        }

        /// <summary>Adds an event that already has its id, time and origin to the queue.</summary>
        private void EnqueueRestored(RavensightEvent evt)
        {
            if (!IsActive) return;
            lock (_gate)
            {
                if (!EnqueueOrdered(evt)) _stats.Dropped++;
            }
            PersistNow();
        }

        /// <summary>
        /// The queue's two ordering rules, called under _gate. A gameplay event
        /// goes ahead of every queued error event, never inside a batch
        /// awaiting an answer. A full queue evicts the oldest queued error
        /// first, then the oldest event; an incoming error with no error to
        /// evict is the one dropped (returns false).
        /// </summary>
        private bool EnqueueOrdered(RavensightEvent evt)
        {
            int cap = _config.MaxQueueSize > 0 ? _config.MaxQueueSize : DefaultQueueCap;
            while (_queue.Count >= cap)
            {
                int victim = FirstQueuedError();
                if (victim >= 0)
                {
                    _queue.RemoveAt(victim);
                }
                else if (evt.IsError)
                {
                    return false;
                }
                else
                {
                    _queue.RemoveAt(0); // drop oldest so the newest always survive
                    if (_inflightCount > 0) _inflightCount--;
                }
                _stats.Dropped++;
            }
            int at = evt.IsError ? -1 : FirstQueuedError();
            if (at >= 0) _queue.Insert(at, evt);
            else _queue.Add(evt);
            return true;
        }

        private int FirstQueuedError()
        {
            for (int i = Math.Min(_inflightCount, _queue.Count); i < _queue.Count; i++)
            {
                if (_queue[i].IsError) return i;
            }
            return -1;
        }

        private void QueueExitWithOrigin(long timestampMs, Dictionary<string, object> origin)
        {
            if (!IsActive) return;
            RavensightEvent exit = new RavensightEvent();
            exit.Name = "game_exited";
            exit.Data = WithPlaytestTags(new Dictionary<string, object>());
            exit.Data[EventIdKey] = NextEventId();
            exit.Timestamp = timestampMs;
            exit.Origin = origin;
            EnqueueRestored(exit);
        }

        /// <summary>
        /// Keeps the session answer's origin (exactly as received) and signing
        /// (memory only), and gives every queued event without an origin this
        /// session's origin. Called under _gate.
        /// </summary>
        private void AdoptSessionAnswer(object parsed)
        {
            Dictionary<string, object> root = RavensightJson.AsObject(parsed);
            object raw;
            Dictionary<string, object> origin = root != null && root.TryGetValue("origin", out raw) ? RavensightJson.AsObject(raw) : null;
            if (origin != null && RavensightJson.GetString(origin, "session", null) == null) origin = null;
            _origin = origin;

            Dictionary<string, object> signing = root != null && root.TryGetValue("signing", out raw) ? RavensightJson.AsObject(raw) : null;
            string secret = RavensightJson.GetString(signing, "secret", null);
            if (!string.IsNullOrEmpty(secret))
            {
                double serverTime;
                RavensightSigning next = new RavensightSigning();
                next.Secret = secret;
                next.OffsetSeconds = RavensightJson.TryGetDouble(signing, "serverTime", out serverTime)
                    ? (long)Math.Round(serverTime - _nowMs() / 1000d)
                    : 0L;
                _signing = next;
            }
            else
            {
                _signing = null;
            }

            if (_sessionsOpened > 0 && !_breakCounted) _sessionEpoch++;
            _breakCounted = false;
            _sessionsOpened++;
            if (origin == null) return;
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Origin == null) _queue[i].Origin = origin;
            }
        }

        /// <summary>Signs over the plain JSON, then gzip compresses a body of 1,024 bytes or more.</summary>
        private void SignAndCompress(RavensightRequest request, string json, RavensightSigning signing, bool allowGzip)
        {
            if (signing != null)
            {
                long timestamp = _nowMs() / 1000L + signing.OffsetSeconds;
                request.WithHeader("X-Ravensight-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
                request.WithHeader("X-Ravensight-Signature", RavensightDelivery.SignRequest(signing.Secret, timestamp, json));
            }
            if (allowGzip && Encoding.UTF8.GetByteCount(json) >= RavensightDelivery.GzipMinBytes)
            {
                byte[] zipped = RavensightDelivery.Gzip(json);
                if (zipped != null)
                {
                    request.RawBody = zipped;
                    request.WithHeader("Content-Encoding", "gzip");
                }
            }
        }

        // --- Device fields -------------------------------------------------------------

        /// <summary>The /session device object, or null when no field is known.</summary>
        private Dictionary<string, object> DeviceFields()
        {
            Dictionary<string, object> device = new Dictionary<string, object>();
            string model = RavensightDelivery.DeviceField(_config.DeviceModel);
            string osVersion = RavensightDelivery.DeviceField(_config.DeviceOsVersion);
            string gpu = RavensightDelivery.DeviceField(_config.DeviceGpu);
            if (model != null) device["model"] = model;
            if (osVersion != null) device["osVersion"] = osVersion;
            if (gpu != null) device["gpu"] = gpu;
            return device.Count == 0 ? null : device;
        }

        // --- Ads and attribution -----------------------------------------------------------

        /// <summary>
        /// One ad lifecycle step. format: banner, interstitial, rewarded, native,
        /// app_open or other; result: shown, clicked, failed or rewarded.
        /// revenueMicros is whole micros of currency as a digit string, sent only
        /// with shown. Returns false, sending nothing, for a value the server
        /// would flag: revenue without a currency, revenue on another result, or
        /// a malformed network, format, result, placement, micros or currency.
        /// </summary>
        public bool TrackAd(string network, string format, string result, string placement = null,
            string revenueMicros = null, string currency = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            string why = RavensightTyped.AdProblem(network, format, result, placement, revenueMicros, currency);
            if (why != null)
            {
                Emit("ad event not sent: " + why);
                return false;
            }
            return TrackTyped(RavensightTyped.Ad(network, format, result, placement, revenueMicros, currency, extra, eventName));
        }

        /// <summary>A paid impression: TrackAd(network, format, "shown", placement, revenueMicros, currency).</summary>
        public bool TrackAdRevenue(string network, string format, string placement, string revenueMicros, string currency)
        {
            return TrackAd(network, format, "shown", placement, revenueMicros, currency);
        }

        /// <summary>
        /// A mediation impression callback that reports revenue as a double in
        /// major units (AppLovin MAX, LevelPlay, TopOn): the format name is
        /// mapped and the double converted on its digits, never multiplied.
        /// </summary>
        public bool TrackAdRevenueFromMediation(string network, string formatName, string placement, double revenue, string currency)
        {
            return TrackAdRevenue(network, RavensightDelivery.AdFormatFrom(formatName), placement,
                RavensightDelivery.MicrosFromRevenue(revenue), currency);
        }

        /// <summary>An AdMob paid event: AdValue.Value is already micros.</summary>
        public bool TrackAdMobPaidEvent(long valueMicros, string currencyCode, string adSourceName, string format, string placement)
        {
            if (valueMicros < 0) return false;
            return TrackAdRevenue(string.IsNullOrEmpty(adSourceName) ? "admob" : adSourceName, format, placement,
                valueMicros.ToString(CultureInfo.InvariantCulture), currencyCode);
        }

        /// <summary>
        /// Where this install came from. Sent once per device id: a flag stored
        /// beside the device id makes every later call return false, across
        /// restarts. touchTimeMs is epoch milliseconds of the click or open.
        /// </summary>
        public bool SetAttribution(string source, string network = null, string campaign = null,
            string adset = null, string creative = null, long? touchTimeMs = null)
        {
            if (!IsActive || DeviceId == null) return false;
            string why = RavensightTyped.AttributionProblem(source, network, campaign, adset, creative, touchTimeMs);
            if (why != null)
            {
                Emit("attribution not sent: " + why);
                return false;
            }
            if (_playtest == null && _storage.GetString(AttributionSentKey) == DeviceId) return false;
            if (!TrackTyped(RavensightTyped.Attribution(source, network, campaign, adset, creative, touchTimeMs))) return false;
            if (_playtest == null)
            {
                _storage.SetString(AttributionSentKey, DeviceId);
                _storage.Save();
            }
            return true;
        }

        // --- Experiment exposure --------------------------------------------------------------

        /// <summary>Reads before the first session count as the first session's.</summary>
        private int ExposureGeneration { get { return _sessionEpoch; } }

        /// <summary>A new analytics session starts now (the background timeout), before its server session opens.</summary>
        private void BeginSessionEpoch()
        {
            lock (_gate)
            {
                _sessionEpoch++;
                _breakCounted = true;
            }
        }

        /// <summary>
        /// A typed config read: the first one in a session exposes every running
        /// experiment (the config answer does not say which key belongs to which),
        /// tags this session's later events and sends one rs_exposure.
        /// </summary>
        private void MarkExposed()
        {
            bool added = false;
            lock (_gate)
            {
                if (!IsActive || _assignments.Count == 0) return;
                int generation = ExposureGeneration;
                if (_exposedGeneration != generation)
                {
                    _exposedGeneration = generation;
                    _exposedKeys.Clear();
                }
                foreach (KeyValuePair<string, string> item in _assignments)
                {
                    if (_exposedKeys.Add(item.Key)) added = true;
                }
            }
            if (added) Track("rs_exposure");
        }

        /// <summary>
        /// Tags an event being queued with the experiments exposed in this
        /// session that the latest config still runs. A well formed tag of the
        /// game's own is left alone.
        /// </summary>
        private void AddExposureTags(Dictionary<string, object> data)
        {
            lock (_gate)
            {
                if (!IsActive || _exposedGeneration != ExposureGeneration) return;
                foreach (KeyValuePair<string, string> item in _assignments)
                {
                    if (!_exposedKeys.Contains(item.Key)) continue;
                    string key = "rs_exp_" + item.Key;
                    object own;
                    if (data.TryGetValue(key, out own) && own is string && VariantName.IsMatch((string)own)) continue;
                    data[key] = item.Value;
                }
            }
        }
    }
}
