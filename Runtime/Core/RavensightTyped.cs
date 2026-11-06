using System;
using System.Collections.Generic;
using System.Text;

namespace RealitySE
{
    /// <summary>An event name and its data, ready for <c>Track(name, data)</c>.</summary>
    public struct RavensightTypedEvent
    {
        public string Name;
        public Dictionary<string, object> Data;
    }

    /// <summary>
    /// Builds the reserved <c>rs_*</c> property sets for the typed events:
    /// purchase, resource, progression, error and health. Each builder only
    /// shapes a name and a data map; the caller hands them to the ordinary
    /// track call, so queueing, batching, retries and opt out are unchanged.
    ///
    /// Nothing here throws on bad input. Money is passed through as given
    /// (an integer in minor units or a digit string is what the server
    /// accepts) and a wrong value becomes an event the server flags with
    /// <c>rs_invalid</c>. An empty event name override falls back to the
    /// default name.
    /// </summary>
    public static class RavensightTyped
    {
        public const string KindKey = "rs_kind";
        public const int MaxMessageChars = 200;
        public const int MaxStackChars = 2000;

        public const string Source = "source";
        public const string Sink = "sink";

        public const string Start = "start";
        public const string Complete = "complete";
        public const string Fail = "fail";

        public const string Debug = "debug";
        public const string Info = "info";
        public const string Warning = "warning";
        public const string Error = "error";
        public const string Critical = "critical";

        public static RavensightTypedEvent Purchase(
            object amount, string currency, string itemType, string itemId,
            string cart = null, string transactionId = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            Dictionary<string, object> data = NewData(extra);
            data[KindKey] = "purchase";
            data["rs_amount"] = amount;
            data["rs_currency"] = currency == null ? null : currency.ToUpperInvariant();
            data["rs_item_type"] = itemType;
            data["rs_item_id"] = itemId;
            if (cart != null) data["rs_cart"] = cart;
            if (transactionId != null) data["rs_transaction_id"] = transactionId;
            return Make(Name(eventName, "purchase"), data);
        }

        public static RavensightTypedEvent Resource(
            string flow, string resource, double amount,
            string itemType = null, string itemId = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            Dictionary<string, object> data = NewData(extra);
            data[KindKey] = "resource";
            data["rs_flow"] = flow;
            data["rs_resource"] = resource;
            data["rs_amount"] = amount;
            if (itemType != null) data["rs_item_type"] = itemType;
            if (itemId != null) data["rs_item_id"] = itemId;
            return Make(Name(eventName, string.IsNullOrEmpty(flow) ? "resource" : "resource_" + flow), data);
        }

        public static RavensightTypedEvent Progression(
            string status, string p1, string p2 = null, string p3 = null,
            double? score = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            Dictionary<string, object> data = NewData(extra);
            data[KindKey] = "progression";
            data["rs_status"] = status;
            data["rs_p1"] = p1;
            if (p2 != null) data["rs_p2"] = p2;
            if (p3 != null) data["rs_p3"] = p3;
            if (score.HasValue) data["rs_score"] = score.Value;
            return Make(Name(eventName, string.IsNullOrEmpty(status) ? "progression" : "progression_" + status), data);
        }

        public static RavensightTypedEvent ErrorEvent(
            string severity, string message, string stack = null,
            Dictionary<string, object> extra = null, string eventName = null)
        {
            Dictionary<string, object> data = NewData(extra);
            data[KindKey] = "error";
            data["rs_severity"] = severity;
            data["rs_message"] = Cut(message, MaxMessageChars);
            if (stack != null) data["rs_stack"] = Cut(stack, MaxStackChars);
            return Make(Name(eventName, "error"), data);
        }

        public static RavensightTypedEvent Health(RavensightHealthSummary summary, double? memoryMb, double? bootMs)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data[KindKey] = "health";
            data["rs_fps_median"] = summary.FpsMedian;
            data["rs_fps_p10"] = summary.FpsP10;
            if (memoryMb.HasValue) data["rs_memory_mb"] = memoryMb.Value;
            if (bootMs.HasValue) data["rs_boot_ms"] = bootMs.Value;
            return Make("health", data);
        }

        /// <summary>
        /// The ad event (see <c>RavensightClient.TrackAd</c>). Check it with
        /// <see cref="AdProblem"/> first: this builder only shapes it.
        /// </summary>
        public static RavensightTypedEvent Ad(string network, string format, string result, string placement,
            string revenueMicros, string currency, Dictionary<string, object> extra = null, string eventName = null)
        {
            Dictionary<string, object> data = NewData(extra);
            data[KindKey] = "ad";
            data["rs_ad_network"] = network;
            data["rs_ad_format"] = format;
            if (!string.IsNullOrEmpty(placement)) data["rs_ad_placement"] = placement;
            data["rs_ad_result"] = result;
            if (!string.IsNullOrEmpty(revenueMicros))
            {
                data["rs_revenue_micros"] = revenueMicros;
                data["rs_currency"] = currency == null ? null : currency.ToUpperInvariant();
            }
            return Make(Name(eventName, "rs_ad"), data);
        }

        /// <summary>Why an ad event would be flagged by the server, or null when it is sendable.</summary>
        public static string AdProblem(string network, string format, string result, string placement,
            string revenueMicros, string currency)
        {
            if (string.IsNullOrEmpty(network) || network.Length > 64) return "network must be 1 to 64 characters";
            if (Array.IndexOf(RavensightDelivery.AdFormats, format) < 0) return "format is not one of " + string.Join(", ", RavensightDelivery.AdFormats);
            if (Array.IndexOf(RavensightDelivery.AdResults, result) < 0) return "result is not one of " + string.Join(", ", RavensightDelivery.AdResults);
            if (placement != null && (placement.Length == 0 || placement.Length > 100)) return "placement must be 1 to 100 characters";
            bool hasRevenue = !string.IsNullOrEmpty(revenueMicros);
            if (hasRevenue)
            {
                if (revenueMicros.Length > 18) return "revenueMicros must be 1 to 18 digits";
                for (int i = 0; i < revenueMicros.Length; i++)
                {
                    if (revenueMicros[i] < '0' || revenueMicros[i] > '9') return "revenueMicros must be a digit string";
                }
                if (currency == null || currency.Length != 3) return "revenue needs a 3 letter currency";
                for (int i = 0; i < 3; i++)
                {
                    char c = char.ToUpperInvariant(currency[i]);
                    if (c < 'A' || c > 'Z') return "revenue needs a 3 letter currency";
                }
                if (result != "shown") return "revenue goes on the shown result only";
            }
            else if (!string.IsNullOrEmpty(currency))
            {
                return "a currency needs a revenue";
            }
            return null;
        }

        /// <summary>The attribution event (see <c>RavensightClient.SetAttribution</c>).</summary>
        public static RavensightTypedEvent Attribution(string source, string network, string campaign,
            string adset, string creative, long? touchTimeMs, string eventName = null)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data[KindKey] = "attribution";
            data["rs_attr_source"] = source;
            if (!string.IsNullOrEmpty(network)) data["rs_attr_network"] = network;
            if (!string.IsNullOrEmpty(campaign)) data["rs_campaign"] = campaign;
            if (!string.IsNullOrEmpty(adset)) data["rs_adset"] = adset;
            if (!string.IsNullOrEmpty(creative)) data["rs_creative"] = creative;
            if (touchTimeMs.HasValue) data["rs_touch_time"] = touchTimeMs.Value;
            return Make(Name(eventName, "rs_attribution"), data);
        }

        /// <summary>Why an attribution event would be flagged, or null when it is sendable. Empty optional fields are omitted, not flagged.</summary>
        public static string AttributionProblem(string source, string network, string campaign,
            string adset, string creative, long? touchTimeMs)
        {
            if (string.IsNullOrEmpty(source) || source.Length > 64) return "source must be 1 to 64 characters";
            if (network != null && network.Length > 100) return "network must be at most 100 characters";
            if ((campaign != null && campaign.Length > 200) || (adset != null && adset.Length > 200) ||
                (creative != null && creative.Length > 200)) return "campaign, adset and creative must be at most 200 characters";
            if (touchTimeMs.HasValue && (touchTimeMs.Value < 0 || touchTimeMs.Value > 9007199254740991L)) return "touchTime must be epoch milliseconds";
            return null;
        }

        // The caller's non-reserved keys go in first; every rs_ key is dropped.
        private static Dictionary<string, object> NewData(Dictionary<string, object> extra)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            if (extra == null) return data;
            foreach (KeyValuePair<string, object> pair in extra)
            {
                // rs_ keys belong to the helpers (rs_invalid to the server), so
                // an extra can never smuggle one past a cut or a default.
                if (pair.Key != null && pair.Key.StartsWith("rs_", StringComparison.Ordinal)) continue;
                data[pair.Key] = pair.Value;
            }
            return data;
        }

        private static string Name(string overrideName, string fallback)
        {
            return string.IsNullOrEmpty(overrideName) ? fallback : overrideName;
        }

        private static RavensightTypedEvent Make(string name, Dictionary<string, object> data)
        {
            RavensightTypedEvent evt = new RavensightTypedEvent();
            evt.Name = name;
            evt.Data = data;
            return evt;
        }

        /// <summary>Cuts to at most <paramref name="max"/> code points, never half a surrogate pair.</summary>
        public static string Cut(string value, int max)
        {
            if (value == null || value.Length <= max) return value;
            StringBuilder sb = new StringBuilder(max);
            int points = 0;
            for (int i = 0; i < value.Length && points < max; i++)
            {
                sb.Append(value[i]);
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(value[i + 1]);
                    i++;
                }
                points++;
            }
            return sb.ToString();
        }
    }

    /// <summary>The numbers a health event carries, from one session's frame samples.</summary>
    public struct RavensightHealthSummary
    {
        public double FpsMedian;
        public double FpsP10;
        public int Samples;
    }

    /// <summary>
    /// Keeps a bounded reservoir of frames per second, fed once a second, and
    /// summarises it. No engine types: the caller passes frame seconds in.
    /// </summary>
    public sealed class RavensightFrameSampler
    {
        public const int MaxSamples = 600;
        public const double IntervalSeconds = 1.0;

        private readonly List<double> _fps = new List<double>();
        private readonly Random _random = new Random(12345);
        private double _elapsed;
        private long _seen;

        public int Count { get { return _fps.Count; } }

        /// <summary>
        /// Call every frame with the unscaled frame time. Takes one sample per
        /// second of elapsed time, from the frame that crosses the second.
        /// </summary>
        public void Tick(double frameSeconds)
        {
            if (!(frameSeconds > 0.0) || double.IsInfinity(frameSeconds)) return;
            _elapsed += frameSeconds;
            if (_elapsed < IntervalSeconds) return;
            _elapsed = 0.0;
            Add(1.0 / frameSeconds);
        }

        /// <summary>Adds one frames per second sample; past 600, a uniform reservoir replace.</summary>
        public void Add(double fps)
        {
            if (double.IsNaN(fps) || double.IsInfinity(fps) || fps < 0.0) return;
            _seen++;
            if (_fps.Count < MaxSamples)
            {
                _fps.Add(fps);
                return;
            }
            long slot = (long)(_random.NextDouble() * _seen);
            if (slot < MaxSamples) _fps[(int)slot] = fps;
        }

        public void Reset()
        {
            _fps.Clear();
            _elapsed = 0.0;
            _seen = 0;
        }

        /// <summary>Median and 10th percentile (linear interpolation), rounded to 2 places. Samples is 0 when empty.</summary>
        public RavensightHealthSummary Summarize()
        {
            RavensightHealthSummary summary = new RavensightHealthSummary();
            if (_fps.Count == 0) return summary;
            List<double> sorted = new List<double>(_fps);
            sorted.Sort();
            summary.Samples = sorted.Count;
            summary.FpsMedian = Math.Round(Percentile(sorted, 0.5), 2);
            summary.FpsP10 = Math.Round(Percentile(sorted, 0.1), 2);
            return summary;
        }

        private static double Percentile(List<double> sorted, double p)
        {
            double at = (sorted.Count - 1) * p;
            int low = (int)Math.Floor(at);
            int high = (int)Math.Ceiling(at);
            return sorted[low] + (sorted[high] - sorted[low]) * (at - low);
        }
    }
}
