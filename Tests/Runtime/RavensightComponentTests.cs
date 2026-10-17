using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace RealitySE.Tests
{
    /// <summary>
    /// Boots the real Ravensight MonoBehaviour in Play Mode: once over a
    /// recording transport, once over UnityWebRequest against a local server.
    /// Proves the startup requests leave from Start, in order, while the
    /// component is active and enabled.
    /// </summary>
    public class RavensightComponentTests
    {
        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            Ravensight.StorageForTests = new RavensightMemoryStorage();
            Ravensight.TransportFactoryForTests = null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = null;
            yield return null;
            Ravensight.StorageForTests = null;
            Ravensight.TransportFactoryForTests = null;
        }

        /// <summary>Records each request with the runner's state at the moment it was sent.</summary>
        private sealed class RecordingTransport : IRavensightTransport
        {
            public readonly List<string> Paths = new List<string>();
            public readonly List<bool> RunnerActiveAndEnabled = new List<bool>();
            public readonly List<string> FirstEvents = new List<string>();
            private readonly MonoBehaviour _runner;

            public RecordingTransport(MonoBehaviour runner)
            {
                _runner = runner;
            }

            public Task<RavensightResponse> SendAsync(RavensightRequest request)
            {
                string path = PathOf(request.Url);
                Paths.Add(path);
                RunnerActiveAndEnabled.Add(_runner != null && _runner.isActiveAndEnabled);
                switch (path)
                {
                    case "/settings":
                        return Answer(200, "{\"trackingEnabled\":true}");
                    case "/session":
                        return Answer(201, "{\"token\":\"tok_1\",\"expiresAt\":\"2099-01-01T00:00:00.000Z\",\"expiresIn\":86400}");
                    case "/track/batch":
                        List<object> events = RavensightJson.GetArray(RavensightJson.Parse(request.Body), "events");
                        Dictionary<string, object> first = RavensightJson.AsObject(events[0]);
                        FirstEvents.Add((string)first["event"]);
                        return Answer(202, "{\"accepted\":" + events.Count + "}");
                    default:
                        return Answer(404, "{\"error\":\"not_found\"}");
                }
            }

            private static Task<RavensightResponse> Answer(int status, string body)
            {
                return Task.FromResult(new RavensightResponse(status, body, null));
            }
        }

        private static string PathOf(string url)
        {
            int at = url.IndexOf("/api/v1", StringComparison.Ordinal);
            string path = at < 0 ? url : url.Substring(at + "/api/v1".Length);
            int query = path.IndexOf('?');
            return query < 0 ? path : path.Substring(0, query);
        }

        /// <summary>
        /// Adds the component the way the Inspector would: fields set while the
        /// GameObject is inactive, so Awake runs with them.
        /// </summary>
        private Ravensight CreateComponent(string apiUrl)
        {
            _host = new GameObject("Ravensight");
            _host.SetActive(false);
            Ravensight component = _host.AddComponent<Ravensight>();
#if UNITY_EDITOR
            UnityEditor.SerializedObject fields = new UnityEditor.SerializedObject(component);
            fields.FindProperty("ingestKey").stringValue = "gt_live_test";
            fields.FindProperty("sendFromEditor").boolValue = true;
            fields.FindProperty("autoFlushSeconds").floatValue = 0f;
            if (apiUrl != null) fields.FindProperty("apiUrl").stringValue = apiUrl;
            fields.ApplyModifiedPropertiesWithoutUndo();
#endif
            return component;
        }

        [UnityTest]
        public IEnumerator BootRequestsLeaveFromStartInOrderWhileEnabled()
        {
            RecordingTransport recorder = null;
            Ravensight.TransportFactoryForTests = (runner, timeout) => recorder = new RecordingTransport(runner);

            Ravensight component = CreateComponent(null);
            _host.SetActive(true); // Awake: the client exists, game_started is queued, nothing is sent

            Assert.AreSame(component, Ravensight.Instance);
            Assert.IsNotNull(recorder, "Awake did not build the client over the test transport");
            Assert.IsEmpty(recorder.Paths, "Awake must not issue requests");
            Assert.AreEqual(1, Ravensight.Client.QueueCount); // game_started

            yield return null; // Start runs before this frame's Update
            yield return null;

            CollectionAssert.AreEqual(new[] { "/settings", "/session", "/track/batch" }, recorder.Paths);
            CollectionAssert.AreEqual(new[] { true, true, true }, recorder.RunnerActiveAndEnabled);
            CollectionAssert.AreEqual(new[] { "game_started" }, recorder.FirstEvents);
            Assert.IsNull(Ravensight.StatusReason);
            Assert.IsTrue(Ravensight.IsReady);
            Assert.IsTrue(Ravensight.LastInitResult.HasValue);
            Assert.IsTrue(Ravensight.LastInitResult.Value.SessionReady);
            Assert.AreEqual(0, Ravensight.Client.QueueCount);
        }

        [UnityTest]
        public IEnumerator SendFromEditorOffMakesNoRequest()
        {
            RecordingTransport recorder = null;
            Ravensight.TransportFactoryForTests = (runner, timeout) => recorder = new RecordingTransport(runner);

            Ravensight component = CreateComponent(null);
#if UNITY_EDITOR
            UnityEditor.SerializedObject fields = new UnityEditor.SerializedObject(component);
            fields.FindProperty("sendFromEditor").boolValue = false;
            fields.ApplyModifiedPropertiesWithoutUndo();
#endif
            _host.SetActive(true);
            yield return null;
            yield return null;

            Ravensight.SetEnabled(true); // the Editor hold wins
            Assert.IsFalse(Ravensight.Track("anything"));
            yield return null;

            Assert.IsEmpty(recorder.Paths);
            Assert.AreEqual(RavensightReason.Disabled, Ravensight.StatusReason);
        }

        [UnityTest]
        public IEnumerator NoKeyIsInertTouchesNoStorageAndSendsNothing()
        {
            RecordingTransport recorder = null;
            Ravensight.TransportFactoryForTests = (runner, timeout) => recorder = new RecordingTransport(runner);
            RavensightMemoryStorage storage = new RavensightMemoryStorage();
            Ravensight.StorageForTests = storage;

            Ravensight component = CreateComponent(null);
#if UNITY_EDITOR
            UnityEditor.SerializedObject fields = new UnityEditor.SerializedObject(component);
            fields.FindProperty("ingestKey").stringValue = "";
            fields.FindProperty("optional").boolValue = true;
            fields.ApplyModifiedPropertiesWithoutUndo();
#endif
            _host.SetActive(true);
            yield return null;
            yield return null;

            Assert.IsFalse(Ravensight.Track("anything"));
            Ravensight.SetEnabled(true);
            yield return null;

            Assert.IsNull(recorder, "an inert start must not build a network transport");
            Assert.AreEqual(0, storage.Writes);
            Assert.IsFalse(Ravensight.IsEnabled);
            Assert.IsNull(Ravensight.DeviceId);
            Assert.AreEqual(RavensightReason.NoKey, Ravensight.StatusReason);
            Assert.AreEqual(RavensightReason.NoKey, Ravensight.LastInitResult.Value.Reason);
        }

        [UnityTest]
        public IEnumerator OptInSendsNothingUntilTheYesThenBootsInOrder()
        {
            RecordingTransport recorder = null;
            Ravensight.TransportFactoryForTests = (runner, timeout) => recorder = new RecordingTransport(runner);
            RavensightMemoryStorage storage = new RavensightMemoryStorage();
            Ravensight.StorageForTests = storage;

            Ravensight component = CreateComponent(null);
#if UNITY_EDITOR
            UnityEditor.SerializedObject fields = new UnityEditor.SerializedObject(component);
            fields.FindProperty("consent").enumValueIndex = (int)RavensightConsent.OptIn;
            fields.FindProperty("distribution").stringValue = "native-release";
            fields.ApplyModifiedPropertiesWithoutUndo();
#endif
            _host.SetActive(true);
            yield return null;
            yield return null;

            Assert.IsFalse(Ravensight.Track("before_yes"));
            Assert.IsEmpty(recorder.Paths);
            Assert.AreEqual(0, storage.Writes);
            Assert.IsNull(Ravensight.DeviceId);
            Assert.IsFalse(Ravensight.IsTrackingEnabled);

            Ravensight.SetTrackingEnabled(true);
            Assert.IsTrue(Ravensight.Track("after_yes"));
            yield return null; // Update boots the client now that it is active
            yield return null;

            CollectionAssert.AreEqual(new[] { "/settings", "/session", "/track/batch" }, recorder.Paths);
            CollectionAssert.AreEqual(new[] { "game_started" }, recorder.FirstEvents);
            Assert.IsTrue(Ravensight.IsEnabled);
            Assert.IsTrue(Ravensight.IsTrackingEnabled);
            Assert.AreEqual(1, storage.GetInt(RavensightClient.OptInKey, 0));
            Assert.IsNotNull(Ravensight.DeviceId);
        }

        [UnityTest]
        public IEnumerator BootOverUnityWebRequestReachesALocalServerInOrder()
        {
            using (LocalServer server = LocalServer.Start())
            {
                CreateComponent("http://127.0.0.1:" + server.Port);
                _host.SetActive(true);

                float deadline = Time.realtimeSinceStartup + 20f;
                while (Ravensight.LastInitResult == null && Time.realtimeSinceStartup < deadline) yield return null;
                while (server.Count < 3 && Time.realtimeSinceStartup < deadline) yield return null;

                Assert.IsTrue(Ravensight.LastInitResult.HasValue, "the startup check did not finish within 20 seconds");
                Assert.IsTrue(Ravensight.LastInitResult.Value.SessionReady, "reason: " + Ravensight.LastInitResult.Value.Reason);
                Assert.IsNull(Ravensight.StatusReason);
                CollectionAssert.AreEqual(
                    new[] { "GET /api/v1/settings", "POST /api/v1/session", "POST /api/v1/track/batch" },
                    server.Requests());
                StringAssert.Contains("\"game_started\"", server.LastBatchBody);
                Assert.AreEqual("tok_local", server.LastBatchSessionToken);
            }
        }

        [UnityTest]
        public IEnumerator ARefusedInsecureUrlFailsTheRequestInsteadOfHanging()
        {
            // A project that disallows plain HTTP (the default) makes
            // SendWebRequest throw for a non loopback http:// URL. The startup
            // check must still finish, as offline, rather than wait forever.
            // Unity 2021.3 has no project switch for plain HTTP, so there the
            // request is simply sent and fails to resolve, also as offline.
#if UNITY_EDITOR && UNITY_2022_1_OR_NEWER
            UnityEditor.InsecureHttpOption saved = UnityEditor.PlayerSettings.insecureHttpOption;
            UnityEditor.PlayerSettings.insecureHttpOption = UnityEditor.InsecureHttpOption.NotAllowed;
#endif
            try
            {
                CreateComponent("http://ravensight.invalid");
                LogAssert.ignoreFailingMessages = true;
                _host.SetActive(true);

                float deadline = Time.realtimeSinceStartup + 20f;
                while (Ravensight.LastInitResult == null && Time.realtimeSinceStartup < deadline) yield return null;

                Assert.IsTrue(Ravensight.LastInitResult.HasValue, "the startup check never finished");
                Assert.IsFalse(Ravensight.LastInitResult.Value.SessionReady);
                Assert.AreEqual(RavensightReason.Offline, Ravensight.StatusReason);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
#if UNITY_EDITOR && UNITY_2022_1_OR_NEWER
                UnityEditor.PlayerSettings.insecureHttpOption = saved;
#endif
            }
        }

        /// <summary>A minimal Ravensight API on a loopback port, answered from a background thread.</summary>
        private sealed class LocalServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly List<string> _requests = new List<string>();
            private readonly object _gate = new object();
            private Thread _thread;

            public int Port { get; private set; }
            public string LastBatchBody { get; private set; }
            public string LastBatchSessionToken { get; private set; }

            public int Count
            {
                get { lock (_gate) { return _requests.Count; } }
            }

            public List<string> Requests()
            {
                lock (_gate) { return new List<string>(_requests); }
            }

            public static LocalServer Start()
            {
                LocalServer server = new LocalServer();
                TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                server.Port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                server._listener.Prefixes.Add("http://127.0.0.1:" + server.Port + "/");
                server._listener.Start();
                server._thread = new Thread(server.Serve) { IsBackground = true };
                server._thread.Start();
                return server;
            }

            private void Serve()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = _listener.GetContext();
                    }
                    catch (Exception)
                    {
                        return; // stopped
                    }

                    string path = context.Request.Url.AbsolutePath;
                    string body;
                    using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }

                    int status;
                    string answer;
                    if (path == "/api/v1/settings")
                    {
                        status = 200;
                        answer = "{\"trackingEnabled\":true}";
                    }
                    else if (path == "/api/v1/session")
                    {
                        status = 201;
                        answer = "{\"token\":\"tok_local\",\"expiresAt\":\"2099-01-01T00:00:00.000Z\",\"expiresIn\":86400}";
                    }
                    else if (path == "/api/v1/track/batch")
                    {
                        status = 202;
                        answer = "{\"accepted\":1}";
                        LastBatchBody = body;
                        LastBatchSessionToken = context.Request.Headers["X-Session-Token"];
                    }
                    else
                    {
                        status = 404;
                        answer = "{\"error\":\"not_found\"}";
                    }

                    lock (_gate) { _requests.Add(context.Request.HttpMethod + " " + path); }

                    byte[] bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    context.Response.OutputStream.Close();
                }
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch (Exception) { }
                try { _listener.Close(); } catch (Exception) { }
            }
        }
    }
}
