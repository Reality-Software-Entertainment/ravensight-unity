using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace RealitySE
{
    /// <summary>
    /// One outbound HTTP request. Deliberately engine agnostic so the batching
    /// and retry logic can be exercised with a fake transport in a plain unit
    /// test, with no Unity player loop involved.
    /// </summary>
    public sealed class RavensightRequest
    {
        public string Method;
        public string Url;
        /// <summary>Serialized JSON body, or null for a GET.</summary>
        public string Body;
        public byte[] RawBody;
        public bool NoRedirect;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();

        public RavensightRequest(string method, string url, string body)
        {
            Method = method;
            Url = url;
            Body = body;
        }

        public RavensightRequest WithHeader(string name, string value)
        {
            if (!string.IsNullOrEmpty(name) && value != null) Headers[name] = value;
            return this;
        }
    }

    /// <summary>
    /// One inbound HTTP response. A transport level failure (no connection,
    /// DNS, timeout) is reported as Status 0 so the client treats it like any
    /// other retryable outcome instead of throwing into game code.
    /// </summary>
    public sealed class RavensightResponse
    {
        public int Status;
        public string Body;
        public byte[] RawBody;
        public bool NoRedirect;
        /// <summary>Raw Retry-After header value, or null.</summary>
        public string RetryAfter;

        public RavensightResponse(int status, string body, string retryAfter)
        {
            Status = status;
            Body = body;
            RetryAfter = retryAfter;
        }

        public static RavensightResponse NetworkFailure(string reason)
        {
            return new RavensightResponse(0, reason, null);
        }

        /// <summary>Parsed body, or null when the body was absent or malformed.</summary>
        public object Json()
        {
            return RavensightJson.Parse(Body);
        }

        /// <summary>The server's "error" field if it sent one, else the fallback.</summary>
        public string ErrorCode(string fallback)
        {
            return RavensightJson.GetString(Json(), "error", fallback);
        }

        /// <summary>Retry-After in milliseconds, or 0 when absent or not a delta seconds value.</summary>
        public int RetryAfterMs()
        {
            return ParseRetryAfterMs(RetryAfter);
        }

        public static int ParseRetryAfterMs(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            double seconds;
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            {
                // HTTP-date form is legal but the API sends delta seconds.
                // Fall back to the caller's own exponential backoff.
                return 0;
            }
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0;
            return (int)Math.Round(seconds * 1000d);
        }
    }

    /// <summary>
    /// Sends one request and never throws. Implementations must translate
    /// every failure into a RavensightResponse.
    /// </summary>
    public interface IRavensightTransport
    {
        Task<RavensightResponse> SendAsync(RavensightRequest request);
    }
}
