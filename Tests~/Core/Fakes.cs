using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RealitySE.Tests
{
    /// <summary>A clock the test moves by hand, in Unix milliseconds.</summary>
    public sealed class FakeClock
    {
        public long NowMs = 1760000000000L;

        public long Now()
        {
            return NowMs;
        }

        public void Advance(long ms)
        {
            NowMs += ms;
        }
    }

    /// <summary>
    /// Records every request and answers from per path handlers, defaulting
    /// to a healthy server. Responses complete synchronously, so an awaited
    /// call has finished all of its requests when it returns.
    /// </summary>
    public sealed class FakeTransport : IRavensightTransport
    {
        public readonly List<RavensightRequest> Requests = new List<RavensightRequest>();
        public readonly Dictionary<string, Func<RavensightRequest, RavensightResponse>> Routes =
            new Dictionary<string, Func<RavensightRequest, RavensightResponse>>();
        private int _sessions;

        public Task<RavensightResponse> SendAsync(RavensightRequest request)
        {
            Requests.Add(request);
            string path = PathOf(request);
            Func<RavensightRequest, RavensightResponse> route;
            if (Routes.TryGetValue(path, out route)) return Task.FromResult(route(request));
            return Task.FromResult(Default(path, request));
        }

        private RavensightResponse Default(string path, RavensightRequest request)
        {
            switch (path)
            {
                case "/settings":
                    return Json(200, "{\"trackingEnabled\":true}");
                case "/session":
                    _sessions++;
                    return Json(201, "{\"token\":\"tok_" + _sessions + "\",\"expiresAt\":\"2099-01-01T00:00:00.000Z\",\"expiresIn\":86400}");
                case "/track/batch":
                    return Json(202, "{\"accepted\":" + EventsOf(request).Count + "}");
                case "/feedback":
                    return Json(201, "{\"id\":\"f1\"}");
                case "/agent/suggestions":
                    return Json(200, "{\"suggestions\":[{\"title\":\"x\"}]}");
                default:
                    return Json(404, "{\"error\":\"not_found\"}");
            }
        }

        public static RavensightResponse Json(int status, string body)
        {
            return new RavensightResponse(status, body, null);
        }

        public static RavensightResponse Json(int status, string body, string retryAfter)
        {
            return new RavensightResponse(status, body, retryAfter);
        }

        public static string PathOf(RavensightRequest request)
        {
            int at = request.Url.IndexOf("/api/v1", StringComparison.Ordinal);
            return at < 0 ? request.Url : request.Url.Substring(at + "/api/v1".Length);
        }

        public static List<Dictionary<string, object>> EventsOf(RavensightRequest request)
        {
            List<object> events = RavensightJson.GetArray(RavensightJson.Parse(request.Body), "events");
            return events == null
                ? new List<Dictionary<string, object>>()
                : events.Select(e => (Dictionary<string, object>)e).ToList();
        }

        public static Dictionary<string, object> BodyOf(RavensightRequest request)
        {
            return RavensightJson.AsObject(RavensightJson.Parse(request.Body));
        }

        public List<RavensightRequest> To(string path)
        {
            return Requests.Where(r => PathOf(r) == path).ToList();
        }

        public List<string> SentEventNames()
        {
            return To("/track/batch").SelectMany(EventsOf).Select(e => (string)e["event"]).ToList();
        }
    }

    /// <summary>Builds clients over shared fakes.</summary>
    public sealed class Harness
    {
        public readonly FakeClock Clock = new FakeClock();
        public readonly FakeTransport Transport = new FakeTransport();
        public RavensightMemoryStorage Storage = new RavensightMemoryStorage();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Statuses = new List<string>();

        public RavensightConfig Config()
        {
            RavensightConfig config = new RavensightConfig();
            config.IngestKey = "gt_live_test";
            config.Storage = Storage;
            return config;
        }

        public RavensightClient Client(RavensightConfig config)
        {
            RavensightClient client = new RavensightClient(config, Transport, Clock.Now);
            client.Warn = Warnings.Add;
            client.StatusChanged += Statuses.Add;
            return client;
        }

        public RavensightClient Client()
        {
            return Client(Config());
        }

        /// <summary>A client that has finished its startup check.</summary>
        public async Task<RavensightClient> Started(RavensightConfig config)
        {
            RavensightClient client = Client(config);
            client.Begin();
            await client.InitAsync();
            return client;
        }

        public Task<RavensightClient> Started()
        {
            return Started(Config());
        }
    }
}
