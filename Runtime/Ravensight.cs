using System;
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
    /// class only supplies Unity: the singleton, PlayerPrefs, the flush timer
    /// and the UnityWebRequest transport.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Ravensight/Ravensight Analytics")]
    public sealed class Ravensight : MonoBehaviour
    {
        private const string DeviceIdKey = "ravensight_device_id";

        [Header("Connection")]
        [Tooltip("Base URL. Leave the default unless you self host. /api/v1 is appended when omitted.")]
        [SerializeField] private string apiUrl = RavensightClient.DefaultApiUrl;

        [Tooltip("Your PUBLISHABLE ingest key (gt_live_...). Safe to ship in a client build: it can only open sessions and read the tracking kill switch.")]
        [SerializeField] private string ingestKey = "gt_live_your_ingest_key_here";

        [Tooltip("Leave empty to use Application.version.")]
        [SerializeField] private string gameVersion = "";

        [Header("Delivery")]
        [Tooltip("Seconds between automatic flushes. 0 flushes only on demand.")]
        [SerializeField] private float autoFlushSeconds = 15f;

        [Tooltip("Offline queue cap. Oldest events are dropped first.")]
        [SerializeField] private int maxQueueSize = RavensightClient.DefaultQueueCap;

        [Tooltip("Request timeout in seconds.")]
        [SerializeField] private int requestTimeoutSeconds = 15;

        [Header("Behaviour")]
        [Tooltip("Automatically track game_started, game_paused, game_resumed and game_exited.")]
        [SerializeField] private bool trackLifecycleEvents = true;

        [Tooltip("Log SDK activity to the Unity console.")]
        [SerializeField] private bool verboseLogging = false;

        private RavensightClient _client;
        private float _flushTimer;
        private bool _booting;

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

        // --- Creation ---------------------------------------------------------

        /// <summary>
        /// Creates the singleton from code. Safe to call more than once: later
        /// calls return the existing instance without reconfiguring it.
        /// </summary>
        public static Ravensight Initialize(string ingestKey, string apiUrl, string gameVersion)
        {
            if (Instance != null) return Instance;

            GameObject host = new GameObject("Ravensight");
            Ravensight instance = host.AddComponent<Ravensight>();
            instance.ingestKey = ingestKey;
            if (!string.IsNullOrEmpty(apiUrl)) instance.apiUrl = apiUrl;
            if (!string.IsNullOrEmpty(gameVersion)) instance.gameVersion = gameVersion;
            return instance;
        }

        public static Ravensight Initialize(string ingestKey)
        {
            return Initialize(ingestKey, null, null);
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
                return;
            }

            RavensightConfig config = new RavensightConfig();
            config.IngestKey = ingestKey;
            config.ApiUrl = apiUrl;
            config.GameVersion = string.IsNullOrEmpty(gameVersion) ? Application.version : gameVersion;
            config.Platform = Application.platform.ToString();
            config.DeviceId = LoadOrCreateDeviceId();
            config.MaxQueueSize = maxQueueSize;

            try
            {
                _client = new RavensightClient(config, new RavensightUnityTransport(this, requestTimeoutSeconds), null);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Ravensight: could not start (" + e.Message + "). Tracking is off.");
                return;
            }

            if (verboseLogging)
            {
                _client.Log = message => Debug.Log("[Ravensight] " + message);
            }

            Boot();
        }

        private async void Boot()
        {
            if (_booting || _client == null) return;
            _booting = true;

            try
            {
                RavensightInitResult result = await _client.InitAsync();
                if (!result.TrackingEnabled)
                {
                    Debug.Log("Ravensight: tracking disabled by the server kill switch.");
                    return;
                }
                if (trackLifecycleEvents) _client.Track("game_started");
            }
            catch (Exception e)
            {
                // Analytics must never take the game down with it.
                Debug.LogWarning("Ravensight: init failed (" + e.Message + "). Events stay queued.");
            }
        }

        private void Update()
        {
            if (_client == null || autoFlushSeconds <= 0f) return;

            _flushTimer += Time.unscaledDeltaTime;
            if (_flushTimer < autoFlushSeconds) return;
            _flushTimer = 0f;
            FlushSoon();
        }

        private void OnApplicationPause(bool paused)
        {
            if (_client == null) return;
            if (paused)
            {
                if (trackLifecycleEvents) _client.Track("game_paused");
                FlushSoon();
            }
            else if (trackLifecycleEvents)
            {
                _client.Track("game_resumed");
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (_client == null || focused) return;
            // Desktop players do not get OnApplicationPause when alt tabbed.
            FlushSoon();
        }

        private void OnApplicationQuit()
        {
            if (_client == null) return;
            if (trackLifecycleEvents) _client.Track("game_exited");
            // Best effort. The process may end before this request completes,
            // in which case the last batch is lost. There is no synchronous
            // HTTP in Unity to do better.
            FlushSoon();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            if (_client != null) _client.Dispose();
        }

        // --- Static API -------------------------------------------------------

        /// <summary>Queues an event. Returns false when tracking is off.</summary>
        public static bool Track(string eventName, Dictionary<string, object> data)
        {
            RavensightClient client = Client;
            if (client == null) return false;
            return client.Track(eventName, data);
        }

        public static bool Track(string eventName)
        {
            return Track(eventName, null);
        }

        /// <summary>Sends queued events now. Awaitable when you care about the result.</summary>
        public static Task<RavensightFlushResult> Flush()
        {
            RavensightClient client = Client;
            if (client == null) return Task.FromResult(new RavensightFlushResult());
            return client.FlushAsync(false);
        }

        /// <summary>Submits player feedback. Throws RavensightException on failure.</summary>
        public static Task SubmitFeedback(string message, string category, int rating)
        {
            RavensightClient client = Client;
            if (client == null)
            {
                throw new RavensightException("Ravensight is not initialized", "not_initialized", 0);
            }
            return client.SubmitFeedbackAsync(message, category, rating);
        }

        public static Task SubmitFeedback(string message)
        {
            return SubmitFeedback(message, null, 0);
        }

        /// <summary>EXPERIMENTAL. AI generated design suggestions for this game.</summary>
        public static Task<List<object>> FetchSuggestions()
        {
            RavensightClient client = Client;
            if (client == null)
            {
                throw new RavensightException("Ravensight is not initialized", "not_initialized", 0);
            }
            return client.FetchSuggestionsAsync();
        }

        /// <summary>
        /// Local opt in and opt out, for a privacy toggle in your settings menu.
        /// Disabling discards anything still queued.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            RavensightClient client = Client;
            if (client != null) client.Enabled = enabled;
        }

        // --- Helpers ----------------------------------------------------------

        private static void FlushSoon()
        {
            RavensightClient client = Client;
            if (client == null) return;

            // Fire and forget, but observe the task so a failure never surfaces
            // as an unobserved exception.
            Task task = client.FlushAsync(false);
            task.ContinueWith(t =>
            {
                if (t.Exception != null) Debug.LogWarning("Ravensight: flush failed (" + t.Exception.Message + ")");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static string LoadOrCreateDeviceId()
        {
            string existing = PlayerPrefs.GetString(DeviceIdKey, "");
            if (!string.IsNullOrEmpty(existing)) return existing;

            string created = "dev_" + Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(DeviceIdKey, created);
            PlayerPrefs.Save();
            return created;
        }
    }
}
