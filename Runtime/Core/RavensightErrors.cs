using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RealitySE
{
    /// <summary>What a log line was, without the engine's own enum.</summary>
    public enum RavensightLogLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2,
        Assert = 3,
        Exception = 4,
    }

    /// <summary>The pure rules behind automatic error capture: scrub, templates, frames and lengths.</summary>
    public static class RavensightErrorRules
    {
        public const string EventName = "error";
        public const string SourceUncaught = "uncaught";
        public const string SourceUnhandledRejection = "unhandled_rejection";
        public const string SourceLog = "log";

        public const int MaxMessageChars = 200;
        public const int MaxStackChars = 2000;
        public const int MaxTypeChars = 100;
        public const int TrailLines = 20;
        public const int TrailLineChars = 120;
        public const int TrailChars = 1500;

        public const int MaxPerSession = 30;
        public const int MaxPerKey = 3;
        public const int MaxTrailsPerSession = 10;
        public const int PerMinute = 6;

        /// <summary>The prefix every line the SDK logs starts with (after an optional "[").</summary>
        public const string SdkLinePrefix = "Ravensight";
        /// <summary>The SDK's own types: RealitySE.Ravensight, RavensightClient and the rest.</summary>
        public const string SdkFramePrefix = "RealitySE.Ravensight";

        private const RegexOptions Opts = RegexOptions.CultureInvariant;

        // Order matters: a URL loses its query first, home paths go before the
        // long run rule so the rest of the path survives.
        private static readonly Regex LongRunPattern = new Regex(@"[A-Za-z0-9+/=_-]{32,}", Opts);
        private static readonly char[] PathPartSeparators = { '_', '-' };
        private static readonly Regex[] PathParts =
        {
            new Regex(@"^[a-z0-9]+$", Opts),
            new Regex(@"^[A-Z][a-z0-9]*(?:[A-Z][a-z0-9]+)*$", Opts),
            new Regex(@"^[A-Z0-9]{1,6}$", Opts),
        };
        private static readonly MatchEvaluator LongRunEvaluator = LongRun;

        /// <summary>
        /// A run of 32 or more of [A-Za-z0-9+/=_-] becomes [token] when it holds
        /// a digit, unless it is a path: it has a "/" and every piece between
        /// slashes is empty or a path word under 32 characters: one or more parts
        /// joined by "_" or "-", each lowercase, CamelCase, or an acronym of 1 to
        /// 6 capitals or digits.
        /// </summary>
        private static string LongRun(Match match)
        {
            string run = match.Value;
            bool digit = false;
            for (int i = 0; i < run.Length && !digit; i++) digit = run[i] >= '0' && run[i] <= '9';
            if (!digit || IsPathRun(run)) return run;
            return "[token]";
        }

        private static bool IsPathRun(string run)
        {
            if (run.IndexOf('/') < 0) return false;
            string[] pieces = run.Split('/');
            for (int i = 0; i < pieces.Length; i++)
            {
                string piece = pieces[i];
                if (piece.Length == 0) continue;
                if (piece.Length >= 32) return false;
                string[] parts = piece.Split(PathPartSeparators);
                for (int p = 0; p < parts.Length; p++)
                {
                    if (parts[p].Length == 0) return false;
                    bool word = false;
                    for (int w = 0; w < PathParts.Length && !word; w++) word = PathParts[w].IsMatch(parts[p]);
                    if (!word) return false;
                }
            }
            return true;
        }

        private static readonly Regex Ipv6Pattern = new Regex(@"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b|\b(?:[0-9a-fA-F]{1,4}:){1,6}:(?:[0-9a-fA-F]{1,4}:){0,5}[0-9a-fA-F]{1,4}\b|(?<![\w:])::1\b", Opts);
        private static readonly MatchEvaluator Ipv6Evaluator = Ipv6;

        /// <summary>An IPv6 match must hold a digit, so a C++ scope like Face::Add stays.</summary>
        private static string Ipv6(Match match)
        {
            string value = match.Value;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] >= '0' && value[i] <= '9') return "[ip]";
            }
            return value;
        }

        private static readonly Regex[] ScrubPatterns =
        {
            // A URL loses any user:password@ first.
            new Regex(@"(\b[a-zA-Z][a-zA-Z0-9+.-]*://)[^\s/@]+@", Opts),
            new Regex(@"(\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s?#]*)[?#]\S*", Opts),
            new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", Opts),
            new Regex(@"\b[Bb][Ee][Aa][Rr][Ee][Rr]\s+[A-Za-z0-9._~+/=-]+", Opts),
            new Regex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", Opts),
            new Regex(@"\bgt_[A-Za-z0-9_]+", Opts),
            // A home folder is the whole segment up to the next slash, quote,
            // <, >, ':' or end of line, spaces included (contract 5).
            new Regex(@"/Users/[^/\\'""<>:\r\n]+", Opts),
            new Regex(@"/home/[^/\\'""<>:\r\n]+", Opts),
            new Regex(@"[A-Za-z]:\\(?:Users|Documents and Settings)\\[^/\\'""<>:\r\n]+", Opts | RegexOptions.IgnoreCase),
            // Runs of 32 or more: see LongRun, which keeps paths and names.
            LongRunPattern,
            new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", Opts),
            Ipv6Pattern,
        };

        private static readonly string[] ScrubReplacements =
        {
            "$1", "$1", "[email]", "[token]", "[token]", "[token]", "~", "~", "~", "[token]", "[ip]", "[ip]",
        };

        private static readonly Regex[] TemplatePatterns =
        {
            new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", Opts),
            new Regex(@"\b[a-zA-Z][a-zA-Z0-9+.-]*://\S+", Opts),
            new Regex("\"[^\"]*\"|'[^']*'", Opts),
            new Regex(@"\b[0-9a-fA-F]{8,}\b", Opts),
            new Regex(@"\d+(\.\d+)?", Opts),
        };

        private static readonly string[] TemplateReplacements = { "<uuid>", "<url>", "<str>", "<hex>", "<n>" };

        private static readonly Regex ExceptionLine = new Regex(@"^([A-Za-z_][A-Za-z0-9_.`+]*)\s*:\s?(.*)$", Opts | RegexOptions.Singleline);
        private static readonly Regex FrameNoise = new Regex(@"\[0x[0-9a-fA-F]+\]|<0x[0-9a-fA-F]+>|:\d+", Opts);

        /// <summary>How many times <see cref="Scrub(string)"/> ran on this thread. A diagnostic for tests.</summary>
        [ThreadStatic] public static int ScrubCallsOnThisThread;

        /// <summary>The contract's device scrub: emails, tokens, gt_ keys, long runs, IPs, home paths, URL query and fragment.</summary>
        public static string Scrub(string text)
        {
            ScrubCallsOnThisThread++;
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string output = text;
            for (int i = 0; i < ScrubPatterns.Length; i++)
            {
                if (ScrubPatterns[i] == LongRunPattern) output = LongRunPattern.Replace(output, LongRunEvaluator);
                else if (ScrubPatterns[i] == Ipv6Pattern) output = Ipv6Pattern.Replace(output, Ipv6Evaluator);
                else output = ScrubPatterns[i].Replace(output, ScrubReplacements[i]);
            }
            return output;
        }

        /// <summary>
        /// The scrub for a field with a length limit: the input is cut to 4
        /// times the limit before scrubbing (some rules are quadratic in the
        /// input) and to the limit after.
        /// </summary>
        public static string Scrub(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            return RavensightTyped.Cut(Scrub(RavensightTyped.Cut(text, limit * 4)), limit);
        }

        /// <summary>The message with its variable parts replaced, for the local dedupe key.</summary>
        public static string Template(string message)
        {
            string output = message ?? "";
            for (int i = 0; i < TemplatePatterns.Length; i++)
            {
                output = TemplatePatterns[i].Replace(output, TemplateReplacements[i]);
            }
            return output;
        }

        /// <summary>
        /// Splits the line Unity logs for an exception, "NullReferenceException: Object
        /// reference not set...", into the type's short name and the message. A line
        /// that does not have that shape is all message, with the type "Exception".
        /// </summary>
        public static void ParseExceptionLine(string condition, out string type, out string message)
        {
            string text = (condition ?? "").Trim();
            Match match = ExceptionLine.Match(text);
            if (match.Success && match.Groups[1].Value.Length > 0)
            {
                type = ShortTypeName(match.Groups[1].Value);
                message = match.Groups[2].Value.Trim();
                return;
            }
            type = "Exception";
            message = text;
        }

        /// <summary>"System.InvalidOperationException" becomes "InvalidOperationException".</summary>
        public static string ShortTypeName(string type)
        {
            if (string.IsNullOrEmpty(type)) return type ?? "";
            int dot = type.LastIndexOf('.');
            return dot >= 0 && dot < type.Length - 1 ? type.Substring(dot + 1) : type;
        }

        /// <summary>True for a line the SDK itself logged ("Ravensight: ..." or "[Ravensight] ...").</summary>
        public static bool IsSdkLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            return trimmed.StartsWith(SdkLinePrefix, StringComparison.Ordinal);
        }

        private static string FrameText(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("at ", StringComparison.Ordinal)) trimmed = trimmed.Substring(3).TrimStart();
            return trimmed;
        }

        private static bool IsEngineFrame(string frame)
        {
            return frame.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                   frame.StartsWith("UnityEditor.", StringComparison.Ordinal) ||
                   frame.StartsWith("System.", StringComparison.Ordinal) ||
                   frame.StartsWith("Mono.", StringComparison.Ordinal);
        }

        /// <summary>The first frame that is neither the engine's nor the runtime's, as written, or null.</summary>
        public static string FirstAppFrame(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return null;
            string[] lines = stack.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string frame = FrameText(lines[i]);
                if (frame.Length == 0 || IsEngineFrame(frame)) continue;
                return frame;
            }
            return null;
        }

        /// <summary>True when the first app frame belongs to the SDK: its own failure, never captured.</summary>
        public static bool IsSdkStack(string stack)
        {
            string frame = FirstAppFrame(stack);
            return frame != null && frame.StartsWith(SdkFramePrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// The stack as sent: the leading engine logging frames (UnityEngine.Debug:LogError
        /// and friends) dropped, blank lines dropped. Scrub and cut happen after.
        /// </summary>
        public static string TrimStack(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return null;
            string[] lines = stack.Replace("\r", "").Split('\n');
            List<string> kept = new List<string>(lines.Length);
            bool leading = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (line.Trim().Length == 0) continue;
                if (leading && FrameText(line).StartsWith("UnityEngine.", StringComparison.Ordinal)) continue;
                leading = false;
                kept.Add(line);
            }
            return kept.Count == 0 ? null : string.Join("\n", kept.ToArray());
        }

        /// <summary>Type plus message template plus first app frame, with line numbers and offsets dropped.</summary>
        public static string DedupeKey(string type, string message, string stack)
        {
            string frame = FirstAppFrame(stack);
            frame = frame == null ? "" : FrameNoise.Replace(frame, "");
            return (type ?? "") + "|" + Template(message) + "|" + frame;
        }

        /// <summary>One trail line: "+&lt;ms&gt; &lt;level&gt; &lt;text&gt;", single line, scrubbed, at most 120 characters.</summary>
        public static string TrailLine(long msSinceSessionStart, string level, string text)
        {
            string flat = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            string line = "+" + Math.Max(0L, msSinceSessionStart).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          " " + level + " " + Scrub(flat, TrailLineChars);
            return RavensightTyped.Cut(line, TrailLineChars);
        }

        /// <summary>The trail level word for a log level.</summary>
        public static string LevelWord(RavensightLogLevel level)
        {
            switch (level)
            {
                case RavensightLogLevel.Warning: return "warning";
                case RavensightLogLevel.Error:
                case RavensightLogLevel.Assert:
                case RavensightLogLevel.Exception: return "error";
                default: return "info";
            }
        }
    }

    /// <summary>
    /// The per session state of automatic capture: the caps (30 captures, 3 per
    /// dedupe key, 10 trails, a token bucket of 6 a minute) and the trail ring
    /// of the last 20 lines. Main thread only; not thread-safe by itself.
    /// </summary>
    public sealed class RavensightErrorGuard
    {
        private readonly Dictionary<string, int> _byKey = new Dictionary<string, int>();
        private readonly Dictionary<string, KeyValuePair<long, string>> _recent = new Dictionary<string, KeyValuePair<long, string>>();
        private readonly LinkedList<string> _trail = new LinkedList<string>();
        private int _captured;
        private int _trails;
        private double _tokens = RavensightErrorRules.PerMinute;
        private long _tokensAtMs = -1;

        /// <summary>When the current session began, for the trail's "+ms".</summary>
        public long SessionStartMs { get; private set; }

        public int Captured { get { return _captured; } }
        public int TrailCount { get { return _trail.Count; } }

        /// <summary>A fresh session: fresh caps and an empty trail.</summary>
        public void Reset(long nowMs)
        {
            SessionStartMs = nowMs;
            _captured = 0;
            _trails = 0;
            _byKey.Clear();
            _recent.Clear();
            _tokens = RavensightErrorRules.PerMinute;
            _tokensAtMs = -1;
            _trail.Clear();
        }

        /// <summary>
        /// True while the session cap and the token bucket would allow one more
        /// capture. Checked before any scrub or key work, so an error logged
        /// every frame costs almost nothing once the caps are spent.
        /// </summary>
        public bool HasRoom(long nowMs)
        {
            if (_captured >= RavensightErrorRules.MaxPerSession) return false;
            Refill(nowMs);
            return _tokens >= 1.0;
        }

        private void Refill(long nowMs)
        {
            if (_tokensAtMs >= 0 && nowMs > _tokensAtMs)
            {
                _tokens = Math.Min(RavensightErrorRules.PerMinute,
                    _tokens + (nowMs - _tokensAtMs) * RavensightErrorRules.PerMinute / 60000.0);
            }
            _tokensAtMs = nowMs;
        }

        /// <summary>
        /// Takes one capture for the key when every cap allows it. Counts only
        /// what passes. Over a cap returns false, and the caller drops it silently.
        /// </summary>
        public bool TryTake(string key, long nowMs)
        {
            if (_captured >= RavensightErrorRules.MaxPerSession) return false;
            int seen;
            _byKey.TryGetValue(key, out seen);
            if (seen >= RavensightErrorRules.MaxPerKey) return false;

            Refill(nowMs);
            if (_tokens < 1.0) return false;

            _tokens -= 1.0;
            _captured++;
            _byKey[key] = seen + 1;
            return true;
        }

        /// <summary>
        /// True when the same type and message template was captured from the
        /// other origin ("log" or "runtime") in the last two seconds: one
        /// exception Unity both logs and raises is one event, not two.
        /// Records this origin otherwise.
        /// </summary>
        public bool SeenFromOtherOrigin(string typeAndTemplate, string origin, long nowMs)
        {
            KeyValuePair<long, string> last;
            if (_recent.TryGetValue(typeAndTemplate, out last) && last.Value != origin && nowMs - last.Key <= 2000)
            {
                return true;
            }
            if (_recent.Count > 64) _recent.Clear();
            _recent[typeAndTemplate] = new KeyValuePair<long, string>(nowMs, origin);
            return false;
        }

        public void AddTrail(long nowMs, string level, string text)
        {
            _trail.AddLast(RavensightErrorRules.TrailLine(nowMs - SessionStartMs, level, text));
            while (_trail.Count > RavensightErrorRules.TrailLines) _trail.RemoveFirst();
        }

        public void ClearTrail()
        {
            _trail.Clear();
        }

        /// <summary>
        /// The trail to send with one capture, oldest first and at most 1,500
        /// characters, or null when it is empty or 10 have gone this session.
        /// </summary>
        public string TakeTrail()
        {
            if (_trail.Count == 0 || _trails >= RavensightErrorRules.MaxTrailsPerSession) return null;
            List<string> lines = new List<string>(_trail);
            string text = string.Join("\n", lines.ToArray());
            while (text.Length > RavensightErrorRules.TrailChars && lines.Count > 1)
            {
                lines.RemoveAt(0);
                text = string.Join("\n", lines.ToArray());
            }
            _trails++;
            return RavensightTyped.Cut(text, RavensightErrorRules.TrailChars);
        }
    }

    /// <summary>One line or exception, copied off whatever thread raised it.</summary>
    public struct RavensightLogEntry
    {
        public RavensightLogLevel Level;
        public string Text;
        public string Stack;
        /// <summary>Set for an exception object (not a log line): its type's short name.</summary>
        public string Type;
        /// <summary>Set for an exception object: "uncaught" or "unhandled_rejection".</summary>
        public string Source;
    }

    /// <summary>
    /// A bounded, lock-guarded buffer the engine's log callbacks write to
    /// from any thread, drained on the main thread. Never logs and never
    /// throws. Errors and exceptions have their own quota of 64 waiting
    /// entries and trail lines another, so heavy Debug.Log output can never
    /// push a real error out. Over a quota a new entry is dropped.
    /// </summary>
    public sealed class RavensightErrorInbox
    {
        public const int Capacity = 64;
        private const int MaxTextChars = 1000;
        private const int MaxStackChars = 4000;

        private readonly object _gate = new object();
        private List<RavensightLogEntry> _pending = new List<RavensightLogEntry>();
        private int _pendingErrors;
        private int _pendingLines;
        private volatile bool _accepting;
        private volatile bool _wantTrail;

        /// <summary>False drops everything offered; set from the main thread.</summary>
        public bool Accepting { get { return _accepting; } set { _accepting = value; } }
        /// <summary>False drops info and warning lines, which only feed the trail.</summary>
        public bool WantTrail { get { return _wantTrail; } set { _wantTrail = value; } }

        public int Count { get { lock (_gate) { return _pending.Count; } } }

        /// <summary>True for info and warning lines, which only ever feed the trail.</summary>
        public static bool IsTrailOnly(RavensightLogEntry entry)
        {
            return entry.Source == null && (entry.Level == RavensightLogLevel.Info || entry.Level == RavensightLogLevel.Warning);
        }

        /// <summary>Called from any thread. True when the entry was kept.</summary>
        public bool Offer(RavensightLogEntry entry)
        {
            if (!_accepting) return false;
            bool line = IsTrailOnly(entry);
            if (line && !_wantTrail) return false;
            if (entry.Source == null && RavensightErrorRules.IsSdkLine(entry.Text)) return false;
            entry.Text = Bound(entry.Text, MaxTextChars);
            entry.Stack = Bound(entry.Stack, MaxStackChars);
            lock (_gate)
            {
                if (line)
                {
                    if (_pendingLines >= Capacity) return false;
                    _pendingLines++;
                }
                else
                {
                    if (_pendingErrors >= Capacity) return false;
                    _pendingErrors++;
                }
                _pending.Add(entry);
            }
            return true;
        }

        public List<RavensightLogEntry> Drain()
        {
            lock (_gate)
            {
                List<RavensightLogEntry> drained = _pending;
                _pending = new List<RavensightLogEntry>();
                _pendingErrors = 0;
                _pendingLines = 0;
                return drained;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _pending.Clear();
                _pendingErrors = 0;
                _pendingLines = 0;
            }
        }

        private static string Bound(string value, int max)
        {
            return value == null || value.Length <= max ? value : value.Substring(0, max);
        }
    }

    public sealed partial class RavensightClient
    {
        private readonly RavensightErrorGuard _errors = new RavensightErrorGuard();
        private readonly object _errorGate = new object();
        private bool _errorCaptureOn = true;
        private bool _errorSettingsKnown;
        private bool _errorServerEnabled = true;
        private bool _errorTrail;
        private readonly List<RavensightEvent> _heldErrors = new List<RavensightEvent>();

        /// <summary>Boot errors held before the first answer about the game's switch.</summary>
        public const int MaxHeldErrors = 5;

        /// <summary>
        /// The game's own switch for automatic capture, from
        /// <see cref="RavensightConfig.ErrorCapture"/>; not saved. Manual
        /// <see cref="TrackError"/> calls are never affected.
        /// </summary>
        public bool ErrorCaptureEnabled
        {
            get { return _errorCaptureOn; }
            set
            {
                _errorCaptureOn = value;
                if (!value) ClearErrorTrail();
            }
        }

        /// <summary>
        /// True only while automatic capture may run: a key, consent, an
        /// active client, the settings answer read, the server's
        /// errorCapture.enabled (absent means on) and the game's own switch.
        /// The engine layer installs its hooks exactly while this is true.
        /// </summary>
        public bool IsErrorCaptureActive
        {
            get { return IsActive && _errorCaptureOn && (!_errorSettingsKnown || _errorServerEnabled); }
        }

        /// <summary>True once a settings or session answer said whether capture is on.</summary>
        public bool ErrorSettingsKnown { get { return _errorSettingsKnown; } }

        /// <summary>Captures waiting for the first settings or session answer (at most 5).</summary>
        public int HeldErrorCount { get { lock (_errorGate) { return _heldErrors.Count; } } }

        /// <summary>True while capture runs and the server's errorCapture.trail is on.</summary>
        public bool IsErrorTrailActive
        {
            get { return IsErrorCaptureActive && _errorSettingsKnown && _errorTrail; }
        }

        /// <summary>
        /// Reads errorCapture: {enabled, trail} from a settings or session
        /// answer. Absent leaves a known switch alone, and otherwise means
        /// the default: enabled, no trail.
        /// </summary>
        private void ApplyErrorCaptureSettings(object parsed)
        {
            Dictionary<string, object> root = RavensightJson.AsObject(parsed);
            object raw = null;
            Dictionary<string, object> value = root != null && root.TryGetValue("errorCapture", out raw)
                ? raw as Dictionary<string, object>
                : null;
            if (value != null)
            {
                bool flag;
                _errorServerEnabled = !(RavensightJson.TryGetBool(value, "enabled", out flag) && !flag);
                _errorTrail = RavensightJson.TryGetBool(value, "trail", out flag) && flag;
                _errorSettingsKnown = true;
            }
            else if (!_errorSettingsKnown)
            {
                _errorServerEnabled = true;
                _errorTrail = false;
                _errorSettingsKnown = true;
            }
            if (!_errorTrail || !_errorServerEnabled) lock (_errorGate) { _errors.ClearTrail(); }
            ReleaseHeldErrors();
        }

        /// <summary>
        /// Boot errors: sent once an answer says capture is on, discarded
        /// unsent when it is off or the client stopped meanwhile.
        /// </summary>
        private void ReleaseHeldErrors()
        {
            List<RavensightEvent> held;
            lock (_errorGate)
            {
                held = new List<RavensightEvent>(_heldErrors);
                _heldErrors.Clear();
            }
            if (!IsErrorCaptureActive) return;
            for (int i = 0; i < held.Count; i++) Enqueue(held[i].Name, held[i].Data, held[i].Timestamp, true);
        }


        private void ResetErrorSession()
        {
            lock (_errorGate) { _errors.Reset(_nowMs()); }
        }

        private void ClearErrorTrail()
        {
            lock (_errorGate)
            {
                _errors.ClearTrail();
                _heldErrors.Clear();
            }
        }

        /// <summary>A gameplay event's name, as a trail line. Never the SDK's captures.</summary>
        private void TrailEvent(string eventName)
        {
            if (!IsErrorTrailActive) return;
            lock (_errorGate) { _errors.AddTrail(_nowMs(), "event", eventName); }
        }

        /// <summary>
        /// Main thread: one entry from the engine's log or exception hooks.
        /// Info and warning lines feed the trail only. Exceptions are
        /// <c>uncaught</c> at <c>error</c>; error and assert lines are
        /// <c>log</c> at <c>warning</c>, as the contract sets for a console
        /// error. The SDK's own lines and stacks are dropped. Returns true
        /// when an event was queued.
        /// </summary>
        public bool ProcessLog(RavensightLogEntry entry)
        {
            if (!IsErrorCaptureActive) return false;
            if (entry.Source == null && RavensightErrorRules.IsSdkLine(entry.Text)) return false;
            if (RavensightErrorRules.IsSdkStack(entry.Stack)) return false;

            string level = RavensightErrorRules.LevelWord(entry.Level);
            if (entry.Level == RavensightLogLevel.Info || entry.Level == RavensightLogLevel.Warning)
            {
                if (IsErrorTrailActive) lock (_errorGate) { _errors.AddTrail(_nowMs(), level, entry.Text); }
                return false;
            }

            string type;
            string message;
            string source;
            string severity;
            string origin;
            if (entry.Source != null)
            {
                type = RavensightErrorRules.ShortTypeName(entry.Type ?? "Exception");
                message = (entry.Text ?? "").Trim();
                source = entry.Source;
                severity = RavensightTyped.Error;
                origin = "runtime";
            }
            else if (entry.Level == RavensightLogLevel.Exception)
            {
                RavensightErrorRules.ParseExceptionLine(entry.Text, out type, out message);
                source = RavensightErrorRules.SourceUncaught;
                severity = RavensightTyped.Error;
                origin = "log";
            }
            else
            {
                type = entry.Level == RavensightLogLevel.Assert ? "Assert" : "Error";
                message = (entry.Text ?? "").Trim();
                source = RavensightErrorRules.SourceLog;
                severity = RavensightTyped.Warning;
                origin = "log";
            }
            return CaptureError(source, severity, type, message, entry.Stack, origin);
        }

        private bool CaptureError(string source, string severity, string type, string message, string stack, string origin)
        {
            if (string.IsNullOrEmpty(message)) message = type;
            if (string.IsNullOrEmpty(message)) return false;

            long now = _nowMs();
            lock (_errorGate)
            {
                // Spent caps cost a counter and a clock read, nothing more.
                if (!_errors.HasRoom(now)) return false;
            }

            string scrubbed = RavensightErrorRules.Scrub(message, RavensightErrorRules.MaxMessageChars);
            string trimmedStack = RavensightErrorRules.TrimStack(stack);
            string trail = null;
            lock (_errorGate)
            {
                string sameError = (type ?? "") + "|" + RavensightErrorRules.Template(scrubbed);
                bool duplicate = _errors.SeenFromOtherOrigin(sameError, origin, now);
                bool allowed = !duplicate && _errors.TryTake(RavensightErrorRules.DedupeKey(type, scrubbed, trimmedStack), now);
                if (allowed && _errorSettingsKnown && _errorTrail) trail = _errors.TakeTrail();
                // The error itself is context for the next one.
                if (_errorSettingsKnown && _errorTrail) _errors.AddTrail(now, "error", (string.IsNullOrEmpty(type) ? "" : type + ": ") + message);
                if (!allowed) return false;
            }

            Dictionary<string, object> data = new Dictionary<string, object>(8);
            data[RavensightTyped.KindKey] = "error";
            data["rs_severity"] = severity;
            data["rs_source"] = source;
            if (!string.IsNullOrEmpty(type)) data["rs_error_type"] = RavensightTyped.Cut(type, RavensightErrorRules.MaxTypeChars);
            data["rs_message"] = RavensightTyped.Cut(scrubbed, RavensightErrorRules.MaxMessageChars);
            if (trimmedStack != null)
            {
                data["rs_stack"] = RavensightErrorRules.Scrub(trimmedStack, RavensightErrorRules.MaxStackChars);
            }
            if (trail != null) data["rs_trail"] = trail;
            if (!_errorSettingsKnown)
            {
                // Before the answer: hold at most 5 in memory, never queued yet.
                lock (_errorGate)
                {
                    if (_heldErrors.Count >= MaxHeldErrors) return false;
                    RavensightEvent held = new RavensightEvent();
                    held.Name = RavensightErrorRules.EventName;
                    held.Data = data;
                    held.Timestamp = now / 1000L;
                    _heldErrors.Add(held);
                }
                return true;
            }
            return Enqueue(RavensightErrorRules.EventName, data, now / 1000L, true);
        }
    }
}
