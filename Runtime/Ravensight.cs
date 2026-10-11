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

        [Tooltip("Your PUBLISHABLE ingest key (gt_live_...). Safe to ship in a client build: it can open sessions, read the tracking kill switch and read this game's design suggestions.")]
        [SerializeField] private string ingestKey = "gt_live_your_ingest_key_here";

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

        [Tooltip("Log SDK activity to the Unity console. Warnings that need your attention are always logged.")]
        [SerializeField] private bool verboseLogging = false;

        private RavensightClient _client;
        private float _flushTimer;
        private bool _booting;
        private bool _bootedWhileActive;
        private bool _editorHold;
        private bool _quitting;
        private bool _quitHeld;

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

        /// <summary>The player's saved choice. True unless they opted out.</summary>
        public static bool IsTrackingEnabled
        {
            get
            {
                RavensightClient client = Client;
                if (client != null) return !client.OptedOut;
                return PlayerPrefs.GetInt(RavensightClient.TrackingEnabledKey, 1) != 0;
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
            if (Instance != null) return Instance;

            // Inactive while configuring, so Awake runs with the fields set.
            GameObject host = new GameObject("Ravensight");
            host.SetActive(false);
            Ravensight instance = host.AddComponent<Ravensight>();
            instance.ingestKey = ingestKey;
            if (!string.IsNullOrEmpty(apiUrl)) instance.apiUrl = apiUrl;
            if (!string.IsNullOrEmpty(gameVersion)) instance.gameVersion = gameVersion;
            instance.enabledOnStart = enabled;
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

            if (string.IsNullOrEmpty(ingestKey) || ingestKey.StartsWith("gt_live_your_", StringComparison.Ordinal))
            {
                Debug.LogWarning("Ravensight: no ingest key set. Tracking is off. " +
                                 "Set it in the Inspector or call Ravensight.Initialize(key).");
                ClearPendingEvents();
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
            config.Playtest = ResolvePlaytest();
            config.Storage = new RavensightPlayerPrefsStorage();

            try
            {
                _client = new RavensightClient(config, new RavensightUnityTransport(this, requestTimeoutSeconds), null);
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
            _client.Begin();
            ReplayPendingEvents();
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
            if (_client == null) return;

            // A client that started disabled boots the first time it is enabled.
            if (!_booting && !_bootedWhileActive && _client.IsActive && !_client.SettingsChecked) Boot();

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

        private void OnApplicationQuit()
        {
            _quitting = true;
            if (_client == null) return;
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
            RavensightClient.WriteTrackingChoice(new RavensightPlayerPrefsStorage(), on);
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
}
