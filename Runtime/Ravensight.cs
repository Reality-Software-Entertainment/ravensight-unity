using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace RealitySE
{
    /// <summary>
    /// Drop this component on a GameObject in your first scene, fill in the
    /// ingest key, and call the static helpers from anywhere:
    ///
    /// <code>
    /// Ravensight.Track("level_completed", new Dictionary&lt;string, object&gt; {
    ///     { "level", 3 }, { "deaths", 2 }
    /// });
    /// </code>
    ///
    /// Or create it entirely from code before your first tracked event:
    ///
    /// <code>
    /// Ravensight.Initialize("gt_live_your_key");
    /// </code>
    ///
    /// All the protocol work lives in <see cref="RavensightClient"/>. This
    /// class only supplies Unity: the singleton, PlayerPrefs, the flush timer,
    /// the application lifecycle and the UnityWebRequest transport.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Ravensight/Ravensight Analytics")]
    public sealed class Ravensight : MonoBehaviour
    {
        /// <summary>The longest a desktop quit is held for the last flush, in seconds.</summary>
        public const float QuitHoldSeconds = 1.5f;
        private const int PendingEventCap = RavensightClient.DefaultQueueCap;

        [Header("Connection")]
        [Tooltip("Host or base URL. Leave the default unless you self host. /api/v1 is appended.")]
        [SerializeField] private string apiUrl = RavensightClient.DefaultApiUrl;

        [Tooltip("Your PUBLISHABLE ingest key (gt_live_... or a public build's gt_pub_...). Safe to ship in a client build: it can open sessions, read the tracking kill switch and read this game's design suggestions. Empty makes the SDK inert: nothing is stored or sent.")]
        [SerializeField] private string ingestKey = "gt_live_your_ingest_key_here";

        [Tooltip("Silences the one warning an inert start (no ingest key) logs. For public builds that ship without a key.")]
        [SerializeField] private bool optional = false;

        [Tooltip("Leave empty to use Application.version.")]
        [SerializeField] private string gameVersion = "";

        [Header("Delivery")]
        [Tooltip("Seconds between automatic flushes. 0 flushes only on demand, on a full batch, on background and on quit.")]
        [SerializeField] private float autoFlushSeconds = 15f;

        [Tooltip("Offline queue cap. Oldest events are dropped first.")]
        [SerializeField] private int maxQueueSize = RavensightClient.DefaultQueueCap;

        [Tooltip("Request timeout in seconds.")]
        [SerializeField] private int requestTimeoutSeconds = 15;

        [Header("Behaviour")]
        [Tooltip("Start sending when the component wakes. Not saved: for the player's own choice use Ravensight.SetTrackingEnabled.")]
        [SerializeField] private bool enabledOnStart = true;

        [Tooltip("Opt Out (default): on until the player opts out. Opt In: nothing is sent and no device id is made until Ravensight.SetTrackingEnabled(true).")]
        [SerializeField] private RavensightConsent consent = RavensightConsent.OptOut;

        [Tooltip("A short label sent on the session, such as native-release or web-demo. At most 40 characters. Empty sends none.")]
        [SerializeField] private string distribution = "";

        [Tooltip("Send from play mode in the Unity Editor. Off by default so pressing Play does not create player sessions.")]
#pragma warning disable 0414 // read only in the Editor; player builds never use it
        [SerializeField] private bool sendFromEditor = false;
#pragma warning restore 0414

        [Tooltip("Automatically track game_started and game_exited.")]
        [SerializeField] private bool trackLifecycleEvents = true;

        [Tooltip("Also track game_paused and game_resumed. Off by default.")]
        [SerializeField] private bool trackPauseEvents = false;

        [Tooltip("iOS and Android: seconds in the background after which returning starts a new session.")]
        [SerializeField] private int sessionTimeoutSeconds = RavensightClient.DefaultSessionTimeoutSeconds;

        [Tooltip("Opt in: sample the frame rate once a second and send one health event per session, on background (mobile) or quit (desktop). Off by default.")]
        [SerializeField] private bool performance = false;

        [Tooltip("Log SDK activity to the Unity console. Warnings that need your attention are always logged.")]
        [SerializeField] private bool verboseLogging = false;

        private RavensightClient _client;
        private float _flushTimer;
        private bool _booting;
        private bool _bootedWhileActive;
        private bool _editorHold;
        private bool _quitting;
        private bool _quitHeld;
        private double _bootMs;

        private struct PendingEvent
        {
            public string Name;
            public Dictionary<string, object> Data;
            public long Timestamp;
        }

        private static readonly object PendingGate = new object();
        private static readonly List<PendingEvent> PendingEvents = new List<PendingEvent>();
        private static bool? _pendingEnabled;
        private static RavensightPlaytestContext _explicitPlaytest;

        // Seams for the package's Play Mode tests (InternalsVisibleTo), so a
        // test can boot the real component over a fake transport and in
        // memory storage. Null in a game, which gets UnityWebRequest and
        // PlayerPrefs.
        internal static Func<MonoBehaviour, int, IRavensightTransport> TransportFactoryForTests;
        internal static IRavensightStorage StorageForTests;

        /// <summary>The live singleton, or null before one exists.</summary>
        public static Ravensight Instance { get; private set; }

        /// <summary>
        /// The underlying protocol client. Null until the singleton has woken.
        /// Use it for anything the static helpers do not cover.
        /// </summary>
        public static RavensightClient Client
        {
            get { return Instance == null ? null : Instance._client; }
        }

        /// <summary>True once a session exists and events can leave the queue.</summary>
        public static bool IsReady
        {
            get
            {
                RavensightClient client = Client;
                return client != null && client.IsActive && client.IsSessionValid();
            }
        }

        /// <summary>
        /// Why events are not being sent, or null when ready: "disabled",
        /// "invalid_key", "tracking_disabled", "offline", "rate_limited",
        /// "session_failed" or "playtest_job_closed". "disabled" before the
        /// component has a client.
        /// </summary>
        public static string StatusReason
        {
            get
            {
                RavensightClient client = Client;
                return client == null ? RavensightReason.Disabled : client.StatusReason;
            }
        }

        /// <summary>Raised on the main thread with the new <see cref="StatusReason"/> whenever it changes.</summary>
        public static event Action<string> StatusChanged;

        /// <summary>What the startup check found, or null until it has finished.</summary>
        public static RavensightInitResult? LastInitResult { get; private set; }

        /// <summary>
        /// The open session's token, null until a session opens. Hand it to a
        /// game server so it can join this player's session.
        /// </summary>
        public static string SessionToken
        {
            get
            {
                RavensightClient client = Client;
                return client == null ? null : client.SessionToken;
            }
        }

        /// <summary>True when a Ravensight Playtest run launched this build.</summary>
        public static bool IsPlaytest
        {
            get
            {
                RavensightClient client = Client;
                return client != null ? client.IsPlaytest : ResolvePlaytest() != null;
            }
        }

        /// <summary>
        /// The player's saved choice, for the starting state of a settings
        /// toggle. With Consent set to Opt Out: true unless they opted out.
        /// With Opt In: true only once they said yes. While inert (no key) or
        /// switched off, still the saved choice. Before the component exists
        /// it reads the opt-out choice, since the consent mode is not known yet.
        /// </summary>
        public static bool IsTrackingEnabled
        {
            get
            {
                RavensightClient client = Client;
                if (client != null && !client.IsInert) return !client.OptedOut;
                RavensightConsent mode = Instance != null ? Instance.consent : RavensightConsent.OptOut;
                return RavensightClient.ReadTrackingChoice(new RavensightPlayerPrefsStorage(), mode);
            }
        }

        /// <summary>
        /// True only while the SDK is running and collecting: it has a key and
        /// nobody switched it off (the developer, the player, the server, or a
        /// refused key). False before the component wakes. Use it to skip work
        /// that only feeds analytics.
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
                RavensightClient client = Client;
                return client != null && client.IsEnabled;
            }
        }

        /// <summary>
        /// This install's random device id. Null before the component wakes,
        /// while inert, and with Consent set to Opt In before the first yes.
        /// </summary>
        public static string DeviceId
        {
            get
            {
                RavensightClient client = Client;
                return client == null ? null : client.DeviceId;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Play mode without a domain reload keeps statics; start clean.
            Instance = null;
            StatusChanged = null;
            LastInitResult = null;
            _pendingEnabled = null;
            _explicitPlaytest = null;
            lock (PendingGate) { PendingEvents.Clear(); }
        }

        // --- Creation ---------------------------------------------------------

        /// <summary>
        /// Creates the singleton from code. Safe to call more than once: later
        /// calls return the existing instance without reconfiguring it.
        /// <paramref name="enabled"/> is the developer's start switch and is
        /// not saved; the player's saved opt-out still wins over it.
        /// </summary>
        public static Ravensight Initialize(string ingestKey, string apiUrl, string gameVersion, bool enabled)
        {
            RavensightOptions options = new RavensightOptions();
            options.ApiUrl = apiUrl;
            options.GameVersion = gameVersion;
            options.Enabled = enabled;
            return Initialize(ingestKey, options);
        }

        /// <summary>
        /// Creates the singleton from code with the public build options:
        /// consent mode, distribution label and Optional. A null or empty key
        /// makes the SDK inert for the run (see the README's "Public builds").
        /// Later calls return the existing instance without reconfiguring it.
        /// </summary>
        public static Ravensight Initialize(string ingestKey, RavensightOptions options)
        {
            if (Instance != null) return Instance;
            if (options == null) options = new RavensightOptions();

            // Inactive while configuring, so Awake runs with the fields set.
            GameObject host = new GameObject("Ravensight");
            host.SetActive(false);
            Ravensight instance = host.AddComponent<Ravensight>();
            instance.ingestKey = ingestKey ?? "";
            if (!string.IsNullOrEmpty(options.ApiUrl)) instance.apiUrl = options.ApiUrl;
            if (!string.IsNullOrEmpty(options.GameVersion)) instance.gameVersion = options.GameVersion;
            instance.enabledOnStart = options.Enabled;
            instance.consent = options.Consent;
            instance.distribution = options.Distribution ?? "";
            instance.optional = options.Optional;
            host.SetActive(true);
            return instance;
        }

        public static Ravensight Initialize(string ingestKey, string apiUrl, string gameVersion)
        {
            return Initialize(ingestKey, apiUrl, gameVersion, true);
        }

        public static Ravensight Initialize(string ingestKey, bool enabled)
        {
            return Initialize(ingestKey, null, null, enabled);
        }

        public static Ravensight Initialize(string ingestKey)
        {
            return Initialize(ingestKey, null, null, true);
        }

        /// <summary>
        /// Supplies the playtest token and run id directly, for builds that
        /// cannot read environment variables or command line arguments (mobile).
        /// Call it before the Ravensight component wakes.
        /// </summary>
        public static void SetPlaytestContext(string token, string runId, string jobId = null, string persona = null)
        {
            if (Client != null)
            {
                Debug.LogWarning("Ravensight: SetPlaytestContext must be called before the Ravensight component wakes. Ignored.");
                return;
            }
            _explicitPlaytest = new RavensightPlaytestContext(token, runId, jobId, persona);
        }

        // --- Unity lifecycle --------------------------------------------------

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            if (string.IsNullOrEmpty(ingestKey) || ingestKey.Trim().Length == 0 ||
                ingestKey.StartsWith("gt_live_your_", StringComparison.Ordinal))
            {
                // Inert: no PlayerPrefs read or write, no request, no flush
                // timer. One warning unless Optional is ticked.
                StartInert();
                return;
            }

            bool startEnabled = _pendingEnabled.HasValue ? _pendingEnabled.Value : enabledOnStart;
            _pendingEnabled = null;
            _editorHold = false;
#if UNITY_EDITOR
            if (!sendFromEditor)
            {
                _editorHold = true;
                startEnabled = false;
                Debug.Log("Ravensight: Send From Editor is off, so nothing is sent from play mode.");
            }
#endif

            RavensightConfig config = new RavensightConfig();
            config.IngestKey = ingestKey;
            config.ApiUrl = apiUrl;
            config.GameVersion = string.IsNullOrEmpty(gameVersion) ? Application.version : gameVersion;
            config.Platform = Application.platform.ToString();
            config.MaxQueueSize = maxQueueSize;
            config.Enabled = startEnabled;
            config.HoldDisabled = _editorHold;
            config.TrackLifecycleEvents = trackLifecycleEvents;
            config.SessionTimeoutSeconds = sessionTimeoutSeconds;
            config.Consent = consent;
            config.Distribution = distribution;
            config.Optional = optional;
            config.Performance = performance;
            config.Playtest = ResolvePlaytest();
            config.Storage = StorageForTests ?? new RavensightPlayerPrefsStorage();

            try
            {
                IRavensightTransport transport = TransportFactoryForTests != null
                    ? TransportFactoryForTests(this, requestTimeoutSeconds)
                    : new RavensightUnityTransport(this, requestTimeoutSeconds);
                _client = new RavensightClient(config, transport, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Ravensight: could not start (" + e.Message + "). Tracking is off.");
                ClearPendingEvents();
                return;
            }

            _client.Warn = message => Debug.LogWarning(message);
            if (verboseLogging)
            {
                _client.Log = message => Debug.Log("[Ravensight] " + message);
            }
            _client.StatusChanged += RaiseStatusChanged;
            _client.ReportStartupProblems();
            if (_client.StatusReason != null) RaiseStatusChanged(_client.StatusReason);

#if !UNITY_EDITOR
            Application.wantsToQuit += OnWantsToQuit;
#endif

            // game_started first, with the real start time, then anything
            // tracked before the component woke. Both are dropped while the
            // player is opted out or the client is disabled.
            _bootMs = Time.realtimeSinceStartup * 1000.0;
            _client.Begin();
            ReplayPendingEvents();
        }

        /// <summary>The no key start: a client that never touches storage or the network.</summary>
        private void StartInert()
        {
            RavensightConfig config = new RavensightConfig();
            config.IngestKey = null;
            config.Optional = optional;
            config.Consent = consent;
            config.Enabled = false;
            // Read or written only if the game calls SetTrackingEnabled, so the
            // player's choice is still saved for a later build with a key.
            config.Storage = StorageForTests ?? new RavensightPlayerPrefsStorage();
            _client = new RavensightClient(config, new InertTransport(), null);
            _client.Warn = message => Debug.LogWarning(message);
            _client.ReportStartupProblems();
            ClearPendingEvents();
            RavensightInitResult result = new RavensightInitResult();
            result.Reason = RavensightReason.NoKey;
            LastInitResult = result;
            RaiseStatusChanged(_client.StatusReason);
        }

        /// <summary>Never called: an inert client makes no request. Answers as a network failure if it ever were.</summary>
        private sealed class InertTransport : IRavensightTransport
        {
            public Task<RavensightResponse> SendAsync(RavensightRequest request)
            {
                return Task.FromResult(RavensightResponse.NetworkFailure("inert"));
            }
        }

        private void Start()
        {
            // Not from Awake: the transport's coroutine runner is only usable
            // once the component is fully enabled, so the startup requests
            // (the saved exit, GET /settings, POST /session) go out from here.
            if (_client != null && _client.IsActive) Boot();
        }

        private async void Boot()
        {
            if (_booting || _client == null) return;
            _booting = true;
            _bootedWhileActive = _client.IsActive;

            try
            {
                LastInitResult = await _client.InitAsync();
            }
            catch (Exception e)
            {
                // Analytics must never take the game down with it.
                Debug.LogWarning("Ravensight: init failed (" + e.Message + "). Events stay queued.");
            }
            finally
            {
                _booting = false;
            }
        }

        private void Update()
        {
            if (_client == null || _client.IsInert) return;

            // A client that started disabled boots the first time it is enabled.
            if (!_booting && !_bootedWhileActive && _client.IsActive && !_client.SettingsChecked) Boot();

            // Opt in. Only reads the frame time here; the health event is
            // queued from background entry or quit, never from this hook.
            if (_client.PerformanceEnabled) _client.SamplePerformance(Time.unscaledDeltaTime);

            if (autoFlushSeconds <= 0f) return;
            _flushTimer += Time.unscaledDeltaTime;
            if (_flushTimer < autoFlushSeconds) return;
            _flushTimer = 0f;
            Observe(_client.AutoFlushAsync());
        }

        private static bool IsMobileRuntime
        {
            get
            {
                return Application.platform == RuntimePlatform.IPhonePlayer ||
                       Application.platform == RuntimePlatform.Android;
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (_client == null) return;
            if (paused)
            {
                if (trackPauseEvents) _client.Track("game_paused");
                // Mobile only, before EnterBackground, whose flush would otherwise
                // leave it behind. On desktop a pause is only a lost focus, so
                // health waits for the quit paths.
                if (IsMobileRuntime) QueueHealth();
                // On iOS and Android this flushes and saves the exit marker.
                if (IsMobileRuntime) _client.EnterBackground();
                else Observe(_client.AutoFlushAsync());
            }
            else
            {
                // The 30 minute rule. Queues game_started before it awaits
                // anything, so game_resumed lands in the new session.
                if (IsMobileRuntime) Observe(_client.EnterForegroundAsync());
                if (trackPauseEvents) _client.Track("game_resumed");
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (_client == null || focused) return;
            // Desktop focus loss is not an exit: flush, send no event.
            Observe(_client.AutoFlushAsync());
        }

        private bool OnWantsToQuit()
        {
            // Desktop players: hold the quit for at most QuitHoldSeconds so the
            // last batch, game_exited included, can leave.
            if (_quitHeld || _client == null || !_client.IsActive || !isActiveAndEnabled) return true;
            _quitHeld = true;
            QueueHealth();
            _client.QueueGameExited();
            StartCoroutine(FlushThenQuit());
            return false;
        }

        private IEnumerator FlushThenQuit()
        {
            // A timer flush may already be in flight, in which case this one
            // is skipped; that drain picks up game_exited, so wait on the
            // queue rather than on this task.
            Observe(_client.FlushAsync());
            float deadline = Time.realtimeSinceStartup + QuitHoldSeconds;
            while (_client.QueueCount > 0 && _client.IsActive && Time.realtimeSinceStartup < deadline) yield return null;
            Application.Quit();
        }

        // Queues the session's one health event (a no-op unless Performance is
        // on, nothing was sampled, or one was already sent).
        private void QueueHealth()
        {
            if (_client == null || !_client.PerformanceEnabled) return;
            double? memoryMb = null;
            try
            {
                long bytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                // 0 means the platform does not report it.
                if (bytes > 0) memoryMb = Math.Round(bytes / 1048576.0, 1);
            }
            catch (Exception)
            {
                // Not every platform exposes it; the field is optional.
            }
            _client.SendHealth(memoryMb, Math.Round(_bootMs));
        }

        private void OnApplicationQuit()
        {
            _quitting = true;
            if (_client == null) return;
            QueueHealth();
            // Once per session. With an open session the request is issued
            // before this returns; the process may still end before it lands.
            _client.QueueGameExited();
            Observe(_client.FlushAsync());
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
#if !UNITY_EDITOR
            Application.wantsToQuit -= OnWantsToQuit;
#endif
            Instance = null;
            // On quit the queue is left alone so the last send is not cut short.
            if (_client != null && !_quitting) _client.Dispose();
        }

        // --- Static API -------------------------------------------------------

        /// <summary>
        /// Queues an event. Returns false when tracking is off. Calls made
        /// before the component wakes are held (up to 500) and queued when it
        /// does, after the player's saved choice is read.
        /// </summary>
        public static bool Track(string eventName, Dictionary<string, object> data)
        {
            RavensightClient client = Client;
            if (client != null) return client.Track(eventName, data);
            if (Instance != null) return false; // woke without a usable key
            if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("event name is required", "eventName");

            PendingEvent pending = new PendingEvent();
            pending.Name = eventName;
            pending.Data = data;
            pending.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (PendingGate)
            {
                while (PendingEvents.Count >= PendingEventCap) PendingEvents.RemoveAt(0);
                PendingEvents.Add(pending);
            }
            return true;
        }

        public static bool Track(string eventName)
        {
            return Track(eventName, null);
        }

        // --- Typed events ------------------------------------------------------
        // Thin helpers over Track(name, data): same queueing, batching, retries
        // and opt out, including holding calls made before the component wakes.

        /// <summary>
        /// A purchase. <paramref name="amount"/> is integer minor units (499 is
        /// 4.99) or a digit string; a wrong value is flagged by the server, not
        /// thrown here. The currency is upper-cased. Default event name "purchase".
        /// </summary>
        public static bool TrackPurchase(object amount, string currency, string itemType, string itemId,
            string cart = null, string transactionId = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            return TrackTyped(RavensightTyped.Purchase(amount, currency, itemType, itemId, cart, transactionId, extra, eventName));
        }

        /// <summary>
        /// A resource gain or spend. <paramref name="flow"/> is "source" or
        /// "sink" (see <see cref="RavensightTyped"/>). Default event name
        /// "resource_source" or "resource_sink".
        /// </summary>
        public static bool TrackResource(string flow, string resource, double amount,
            string itemType = null, string itemId = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            return TrackTyped(RavensightTyped.Resource(flow, resource, amount, itemType, itemId, extra, eventName));
        }

        /// <summary>
        /// A progression step: status "start", "complete" or "fail". Default
        /// event name "progression_start", "progression_complete" or "progression_fail".
        /// </summary>
        public static bool TrackProgression(string status, string p1, string p2 = null, string p3 = null,
            double? score = null, Dictionary<string, object> extra = null, string eventName = null)
        {
            return TrackTyped(RavensightTyped.Progression(status, p1, p2, p3, score, extra, eventName));
        }

        /// <summary>
        /// An error. Severity is debug, info, warning, error or critical. The
        /// message is cut to 200 characters and the stack to 2000. Default
        /// event name "error".
        /// </summary>
        public static bool TrackError(string severity, string message, string stack = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            return TrackTyped(RavensightTyped.ErrorEvent(severity, message, stack, extra, eventName));
        }

        private static bool TrackTyped(RavensightTypedEvent evt)
        {
            return Track(evt.Name, evt.Data);
        }

        /// <summary>
        /// Sends queued events now. After the key was refused this still makes
        /// one attempt. Awaitable when you care about the result.
        /// </summary>
        public static Task<RavensightFlushResult> Flush()
        {
            RavensightClient client = Client;
            if (client == null) return Task.FromResult(new RavensightFlushResult());
            return client.FlushAsync();
        }

        /// <summary>
        /// Submits player feedback. Category is one of bug, suggestion,
        /// complaint, praise, playtest, other (or null); rating is 0 for none
        /// or 1 to 5. The returned task fails with RavensightException:
        /// "invalid_category", "invalid_rating", "empty_message" and "disabled"
        /// (also when the component has no client) are refused locally with no
        /// request.
        /// </summary>
        public static Task SubmitFeedback(string message, string category, int rating)
        {
            RavensightClient client = Client;
            if (client == null)
            {
                // Same shape as a disabled client: a failed task, no request.
                TaskCompletionSource<bool> refused = new TaskCompletionSource<bool>();
                refused.SetException(new RavensightException("Ravensight is not running", RavensightReason.Disabled, 0));
                return refused.Task;
            }
            return client.SubmitFeedbackAsync(message, category, rating);
        }

        public static Task SubmitFeedback(string message)
        {
            return SubmitFeedback(message, null, 0);
        }

        /// <summary>
        /// EXPERIMENTAL. AI generated design suggestions for this game. An
        /// empty list, with no request, while tracking is off.
        /// </summary>
        public static Task<List<object>> FetchSuggestions()
        {
            RavensightClient client = Client;
            if (client == null) return Task.FromResult(new List<object>());
            return client.FetchSuggestionsAsync();
        }

        /// <summary>
        /// The developer's runtime switch, not saved. Use it for things like a
        /// debug build or a server build. Disabling discards anything queued.
        /// For a player's privacy toggle use <see cref="SetTrackingEnabled"/>.
        /// Called before the component wakes, it applies when it does.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            Ravensight instance = Instance;
            RavensightClient client = Client;
            if (client == null)
            {
                if (instance == null) _pendingEnabled = enabled;
                return;
            }
            // The Editor hold (Send From Editor off) is enforced by the client.
            client.Enabled = enabled;
        }

        /// <summary>
        /// The player's choice, saved in PlayerPrefs at once and read on every
        /// launch. Callable at any time, even before the component exists.
        /// Turning it off discards anything queued and stops all sending. A
        /// no-op, with one log line, during a playtest run.
        /// </summary>
        public static void SetTrackingEnabled(bool on)
        {
            RavensightClient client = Client;
            if (client != null)
            {
                client.SetTrackingEnabled(on);
                return;
            }
            if (IsPlaytest)
            {
                Debug.Log("Ravensight: SetTrackingEnabled is ignored during a playtest run.");
                return;
            }
            // The consent mode is not known before the component exists, so a
            // yes is saved in the form both modes read.
            RavensightClient.WriteTrackingChoice(new RavensightPlayerPrefsStorage(), on, RavensightConsent.OptIn);
            if (!on) ClearPendingEvents();
        }

        /// <summary>
        /// Forgets this install's analytics identity: a new random device id is
        /// saved, the queue is cleared and the session dropped, so nothing links
        /// the old id to the new one. Returns the new id. A no-op, with one log
        /// line, during a playtest run.
        /// </summary>
        public static string ResetDeviceId()
        {
            RavensightClient client = Client;
            if (client != null) return client.ResetDeviceId();
            if (IsPlaytest)
            {
                Debug.Log("Ravensight: ResetDeviceId is ignored during a playtest run.");
                return null;
            }
            ClearPendingEvents();
            return RavensightClient.ResetStoredDeviceId(new RavensightPlayerPrefsStorage());
        }

        // --- Helpers ----------------------------------------------------------

        private static RavensightPlaytestContext ResolvePlaytest()
        {
            string[] arguments;
            try
            {
                arguments = Environment.GetCommandLineArgs();
            }
            catch (Exception)
            {
                arguments = null;
            }
            return RavensightPlaytestContext.Resolve(_explicitPlaytest, ReadEnvironment, arguments);
        }

        private static string ReadEnvironment(string name)
        {
            return Environment.GetEnvironmentVariable(name);
        }

        private void ReplayPendingEvents()
        {
            List<PendingEvent> pending;
            lock (PendingGate)
            {
                pending = new List<PendingEvent>(PendingEvents);
                PendingEvents.Clear();
            }
            for (int i = 0; i < pending.Count; i++)
            {
                _client.TrackAt(pending[i].Name, pending[i].Data, pending[i].Timestamp);
            }
        }

        private static void ClearPendingEvents()
        {
            lock (PendingGate) { PendingEvents.Clear(); }
        }

        private static void RaiseStatusChanged(string reason)
        {
            Action<string> handler = StatusChanged;
            if (handler == null) return;
            try
            {
                handler(reason);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private static void Observe(Task task)
        {
            // Fire and forget, but observe the task so a failure never surfaces
            // as an unobserved exception.
            task.ContinueWith(t =>
            {
                if (t.Exception != null) Debug.LogWarning("Ravensight: request failed (" + t.Exception.Message + ")");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>Options for <see cref="Ravensight.Initialize(string, RavensightOptions)"/>.</summary>
    public sealed class RavensightOptions
    {
        /// <summary>Host or base URL. Null keeps the default.</summary>
        public string ApiUrl;
        /// <summary>Null uses Application.version.</summary>
        public string GameVersion;
        /// <summary>The developer's start switch, not saved.</summary>
        public bool Enabled = true;
        /// <summary>OptOut (default) or OptIn.</summary>
        public RavensightConsent Consent = RavensightConsent.OptOut;
        /// <summary>A short label sent on the session, such as "native-release". Null sends none.</summary>
        public string Distribution;
        /// <summary>True silences the one warning an inert start (no key) logs.</summary>
        public bool Optional;
    }
}
