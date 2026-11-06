using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RealitySE
{
    /// <summary>
    /// The pure parts of the platform's SDK contracts: request signing, the
    /// beacon envelope, gzip, the server's event size measure, micros, the ad
    /// format names and device fields. No state and no engine
    /// types, so every shared test vector runs against these directly.
    /// </summary>
    public static class RavensightDelivery
    {
        /// <summary>A body this large or larger, before compression, is sent gzip compressed.</summary>
        public const int GzipMinBytes = 1024;
        /// <summary>The largest data the server takes for one event, as it measures it.</summary>
        public const int MaxEventBytes = 8192;
        /// <summary>Device fields are cut to this many characters.</summary>
        public const int MaxDeviceField = 128;

        public static readonly string[] AdFormats = { "banner", "interstitial", "rewarded", "native", "app_open", "other" };
        public static readonly string[] AdResults = { "shown", "clicked", "failed", "rewarded" };

        // --- Signing ----------------------------------------------------------

        /// <summary>
        /// The X-Ravensight-Signature value: "v1=" and the lowercase hex
        /// HMAC-SHA256 of "v1:" + timestamp + ":" + body, keyed with the UTF-8
        /// bytes of the secret as text. body is the exact text sent, before gzip.
        /// </summary>
        public static string SignRequest(string secret, long timestamp, string body)
        {
            return "v1=" + HmacHex(secret, "v1:" + timestamp.ToString(CultureInfo.InvariantCulture) + ":" + (body ?? ""));
        }

        /// <summary>
        /// The signed body a send that cannot set headers carries:
        /// {"sessionToken":...,"signed":{"v":1,"t":...,"body":...,"sig":...}}.
        /// </summary>
        public static Dictionary<string, object> SignedBeaconBody(string sessionToken, string secret, long timestamp, string body)
        {
            Dictionary<string, object> signed = new Dictionary<string, object>();
            signed["v"] = 1;
            signed["t"] = timestamp;
            signed["body"] = body ?? "";
            signed["sig"] = HmacHex(secret, "v1:" + timestamp.ToString(CultureInfo.InvariantCulture) + ":" + (body ?? ""));
            Dictionary<string, object> envelope = new Dictionary<string, object>();
            envelope["sessionToken"] = sessionToken;
            envelope["signed"] = signed;
            return envelope;
        }

        private static string HmacHex(string secret, string message)
        {
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? "")))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
                StringBuilder hex = new StringBuilder(64);
                for (int i = 0; i < hash.Length; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        // --- gzip ------------------------------------------------------------------

        /// <summary>The gzip bytes of the UTF-8 text, or null when compression fails.</summary>
        public static byte[] Gzip(string text)
        {
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(text ?? "");
                using (MemoryStream output = new MemoryStream())
                {
                    using (GZipStream zip = new GZipStream(output, CompressionMode.Compress, true))
                    {
                        zip.Write(raw, 0, raw.Length);
                    }
                    return output.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The text of gzip bytes. Tests use it to read what was sent.</summary>
        public static string Gunzip(byte[] bytes)
        {
            using (MemoryStream input = new MemoryStream(bytes))
            using (GZipStream zip = new GZipStream(input, CompressionMode.Decompress))
            using (StreamReader reader = new StreamReader(zip, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // --- The queue file name ------------------------------------------------------

        /// <summary>64-bit FNV-1a of the UTF-8 bytes, as 16 lowercase hex characters. Only names a file.</summary>
        public static string QueueKeyHash(string text)
        {
            ulong hash = 0xcbf29ce484222325UL;
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                unchecked { hash *= 0x100000001b3UL; }
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        // --- Event size --------------------------------------------------------------

        /// <summary>
        /// One event's data size as the server measures it: the UTF-8 bytes of
        /// its JSON, except that a typed error's rs_message and rs_stack,
        /// within their 200 and 2,000 character caps, count one byte a character.
        /// </summary>
        public static int EventDataBytes(Dictionary<string, object> data)
        {
            Dictionary<string, object> map = data ?? new Dictionary<string, object>();
            int bytes = Encoding.UTF8.GetByteCount(RavensightJson.Write(map));
            object kind;
            if (map.TryGetValue("rs_kind", out kind) && (kind as string) == "error")
            {
                bytes -= ExemptOverhead(map, "rs_message", RavensightTyped.MaxMessageChars);
                bytes -= ExemptOverhead(map, "rs_stack", RavensightTyped.MaxStackChars);
            }
            return bytes;
        }

        private static int ExemptOverhead(Dictionary<string, object> map, string key, int cap)
        {
            object value;
            string text = map.TryGetValue(key, out value) ? value as string : null;
            if (text == null || text.Length > cap) return 0;
            return Encoding.UTF8.GetByteCount(RavensightJson.Write(text)) - text.Length;
        }

        // --- Micros ---------------------------------------------------------------------

        private static readonly Regex DecimalText = new Regex(@"^\+?(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$");

        /// <summary>
        /// Revenue text in major units as whole micros, a digit string, on the
        /// decimal digits so no float is multiplied: "0.0015" is "1500". Past six
        /// decimals it rounds half up. Null for anything that is not a
        /// non-negative decimal or whose micros would not fit in 18 digits.
        /// </summary>
        public static string MicrosFromDecimalString(string text)
        {
            if (text == null) return null;
            Match match = DecimalText.Match(text.Trim());
            if (!match.Success) return null;
            string intPart = match.Groups[1].Value;
            string frac = match.Groups[2].Success ? match.Groups[2].Value : "";
            if (intPart.Length == 0 && frac.Length == 0) return null;
            int exp = 0;
            if (match.Groups[3].Success)
            {
                if (!int.TryParse(match.Groups[3].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp)) return null;
                if (Math.Abs(exp) > 400) return null;
            }
            if (exp > 0)
            {
                string moved = frac.Length >= exp ? frac.Substring(0, exp) : frac.PadRight(exp, '0');
                intPart += moved;
                frac = frac.Length > exp ? frac.Substring(exp) : "";
            }
            else if (exp < 0)
            {
                int shift = -exp;
                string padded = intPart.PadLeft(shift, '0');
                frac = padded.Substring(padded.Length - shift) + frac;
                intPart = padded.Substring(0, padded.Length - shift);
            }
            string six = frac.Length >= 6 ? frac.Substring(0, 6) : frac.PadRight(6, '0');
            bool roundUp = frac.Length > 6 && frac[6] >= '5';
            char[] digits = ((intPart.Length == 0 ? "0" : intPart) + six).ToCharArray();
            if (roundUp)
            {
                int i = digits.Length - 1;
                while (i >= 0)
                {
                    if (digits[i] == '9') { digits[i] = '0'; i--; }
                    else { digits[i] = (char)(digits[i] + 1); break; }
                }
                if (i < 0) digits = ("1" + new string(digits)).ToCharArray();
            }
            string result = new string(digits).TrimStart('0');
            if (result.Length == 0) result = "0";
            return result.Length <= 18 ? result : null;
        }

        /// <summary>
        /// A mediation callback's revenue (a double in major units) as micros:
        /// its shortest round-trip text first, then rounded on the digits.
        /// 0.0015 is "1500" and 1.23456789 is "1234568".
        /// </summary>
        public static string MicrosFromRevenue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return null;
            return MicrosFromDecimalString(value.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>A mediation SDK's ad format name mapped onto rs_ad_format.</summary>
        public static string AdFormatFrom(string name)
        {
            string key = (name ?? "").Trim().ToUpperInvariant();
            switch (key)
            {
                case "BANNER": case "MREC": case "LEADER": return "banner";
                case "INTER": case "INTERSTITIAL": return "interstitial";
                case "REWARDED": case "REWARDED_VIDEO": case "REWARDED_INTER": return "rewarded";
                case "NATIVE": return "native";
                case "APP_OPEN": return "app_open";
                default: return "other";
            }
        }

        // --- Device fields ------------------------------------------------------------------

        /// <summary>A device field as the contract allows: printable, trimmed, at most 128 characters, or null.</summary>
        public static string DeviceField(string value)
        {
            if (value == null) return null;
            StringBuilder clean = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsControl(value[i])) clean.Append(value[i]);
            }
            string text = RavensightTyped.Cut(clean.ToString().Trim(), MaxDeviceField);
            text = text == null ? null : text.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static readonly string[] OsNames =
        {
            "Mac OS X", "macOS", "iPadOS", "iOS", "Android OS", "Android", "Windows", "Linux", "tvOS", "visionOS",
        };

        /// <summary>
        /// The version part of an OS description, the OS name removed:
        /// "Mac OS X 14.2.1" is "14.2.1", "iOS 17.2" is "17.2", "Android OS 14 /
        /// API-34 (UP1A)" is "14 / API-34 (UP1A)". Null when nothing is left.
        /// </summary>
        public static string OsVersionFrom(string operatingSystem)
        {
            if (operatingSystem == null) return null;
            string text = operatingSystem.Trim();
            foreach (string name in OsNames)
            {
                if (text.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(name.Length).Trim();
                    break;
                }
            }
            return DeviceField(text);
        }
    }
}
