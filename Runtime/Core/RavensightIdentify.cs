using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace RealitySE
{
    // Identify players who sign in: docs/identify/CONTRACT.md in the platform
    // repository. The raw account id lives only in _heldAccount, in memory,
    // until the session answer gives the game's salt; then only the salted
    // hash and the salt are kept (storage key ravensight_identity) and the
    // hash is sent once per session.
    public sealed partial class RavensightClient
    {
        /// <summary>Storage key of the saved identity: {"salt","accountHash"}, never the raw id.</summary>
        public const string IdentityKey = "ravensight_identity";

        /// <summary>The longest account id, in UTF-8 bytes.</summary>
        public const int IdentifyMaxBytes = 256;

        private string _heldAccount;
        private string _identitySalt;
        private string _identityHash;
        private string _launchSalt;
        private string _identifyDoneToken;
        private bool _identifyNoSalt;
        private bool _identifyStopped;
        private int _identifyGeneration;

        /// <summary>
        /// Why an account id is refused: "not_text" (null), "empty",
        /// "too_long" (over 256 UTF-8 bytes) or "looks_like_email" (any '@'),
        /// or null when it is accepted. Never trims.
        /// </summary>
        public static string IdentifyRefusal(string accountId)
        {
            if (accountId == null) return "not_text";
            if (accountId.Length == 0) return "empty";
            if (Encoding.UTF8.GetByteCount(accountId) > IdentifyMaxBytes) return "too_long";
            if (accountId.IndexOf('@') >= 0) return "looks_like_email";
            return null;
        }

        /// <summary>
        /// Lower case hex HMAC-SHA256, keyed with the UTF-8 bytes of the salt
        /// string exactly as given (not hex decoded), over the account id's UTF-8 bytes.
        /// </summary>
        public static string IdentifyAccountHash(string salt, string accountId)
        {
            byte[] digest;
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(salt)))
            {
                digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(accountId));
            }
            StringBuilder hex = new StringBuilder(64);
            for (int i = 0; i < digest.Length; i++) hex.Append(digest[i].ToString("x2"));
            return hex.ToString();
        }

        private static bool IsIdentifyHex(string text)
        {
            if (text == null || text.Length != 64) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        /// <summary>
        /// True while an identity is saved, or an accepted id waits for the
        /// session answer. The account id itself can never be read back.
        /// </summary>
        public bool IsIdentified
        {
            get { return _identityHash != null || _heldAccount != null; }
        }

        /// <summary>
        /// Links this device to the game's own account id, so one person on two
        /// devices, or before and after sign-in, counts as one player. Throws
        /// ArgumentException (nothing changed) for null, an empty id, one over
        /// 256 UTF-8 bytes, or one with an '@'. The id is used exactly as given.
        /// Only its salted hash is saved and sent, once per session; the id
        /// waits in memory until the session answer gives the salt, and before
        /// consent. Signing in as another account calls ResetIdentity first.
        /// </summary>
        public void Identify(string accountId)
        {
            string refusal = IdentifyRefusal(accountId);
            if (refusal != null)
            {
                throw new ArgumentException(
                    "Ravensight: identify refused (" + refusal + "): an account id is 1 to 256 bytes of text with no @. Nothing changed.",
                    "accountId");
            }
            if (_identifyNoSalt)
            {
                // An older server: nothing is held, saved or sent this launch.
                Emit("identify is not available from this server; the account id was not kept");
                return;
            }
            if (_identityHash != null)
            {
                // The saved salt is the game's: compare the accounts with it.
                if (IdentifyAccountHash(_identitySalt, accountId) == _identityHash)
                {
                    _heldAccount = null;
                    ScheduleIdentify();
                    return;
                }
                // Another account on this device: sign the first one out.
                ResetIdentityCore();
            }
            _heldAccount = accountId;
            SettleIdentity();
        }

        /// <summary>
        /// For sign-out: forgets the saved identity (and an id still waiting)
        /// and switches to a fresh device id exactly as ResetDeviceId does, so
        /// the next player is not linked to this account. Sends nothing. With
        /// nothing saved or waiting it does nothing.
        /// </summary>
        public void ResetIdentity()
        {
            if (_identityHash == null && _heldAccount == null) return;
            ResetIdentityCore();
        }

        private void ResetIdentityCore()
        {
            ForgetIdentity();
            ResetDeviceId();
        }

        /// <summary>Reads the saved identity at start. A malformed one is deleted.</summary>
        private void LoadIdentity()
        {
            string text = _storage.GetString(IdentityKey);
            if (text == null) return;
            object parsed = RavensightJson.Parse(text);
            string salt = RavensightJson.GetString(parsed, "salt", null);
            string hash = RavensightJson.GetString(parsed, "accountHash", null);
            if (IsIdentifyHex(salt) && IsIdentifyHex(hash))
            {
                _identitySalt = salt;
                _identityHash = hash;
                return;
            }
            _storage.DeleteKey(IdentityKey);
            _storage.Save();
        }

        private void DeleteSavedIdentity()
        {
            if (_inert || IsPlaytest || _storage.GetString(IdentityKey) == null) return;
            _storage.DeleteKey(IdentityKey);
            _storage.Save();
        }

        /// <summary>Opt-out, consent withdrawn, switched off or signed out: all of it goes.</summary>
        private void ForgetIdentity()
        {
            DeleteSavedIdentity();
            _identitySalt = null;
            _identityHash = null;
            _heldAccount = null;
            _identifyGeneration++;
        }

        /// <summary>Hashes a waiting id once the salt is known and the client may save, then sends it.</summary>
        private void SettleIdentity()
        {
            if (_heldAccount == null || _launchSalt == null || !IsActive) return;
            _identitySalt = _launchSalt;
            _identityHash = IdentifyAccountHash(_launchSalt, _heldAccount);
            _heldAccount = null;
            _identifyGeneration++;
            if (!IsPlaytest)
            {
                Dictionary<string, object> saved = new Dictionary<string, object>(2);
                saved["salt"] = _identitySalt;
                saved["accountHash"] = _identityHash;
                _storage.SetString(IdentityKey, RavensightJson.Write(saved));
                _storage.Save();
            }
            ScheduleIdentify();
        }

        /// <summary>A session answer: the game's salt, or none from an older server.</summary>
        private void AdoptIdentifyAnswer(object parsed)
        {
            if (_identifyNoSalt) return;
            object identify;
            Dictionary<string, object> answer = RavensightJson.AsObject(parsed);
            string salt = answer != null && answer.TryGetValue("identify", out identify)
                ? RavensightJson.GetString(identify, "salt", null)
                : null;
            if (!IsIdentifyHex(salt))
            {
                _identifyNoSalt = true;
                _launchSalt = null;
                if (_heldAccount != null)
                {
                    _heldAccount = null;
                    Emit("identify is not available from this server; the account id was dropped");
                }
                return;
            }
            _launchSalt = salt;
            if (_identityHash != null && _identitySalt != salt)
            {
                // Made under another game's key: never sent here.
                DeleteSavedIdentity();
                _identitySalt = null;
                _identityHash = null;
                _identifyGeneration++;
                Emit("forgot an identity saved under another game's key");
            }
            SettleIdentity();
            ScheduleIdentify();
        }

        /// <summary>Starts the send without waiting for it, so it never holds up events.</summary>
        private void ScheduleIdentify()
        {
            if (_inert || _identityHash == null) return;
            Observe(SendIdentifyAsync());
        }

        /// <summary>One POST /identify for the current session. Each session is tried once, whatever the answer.</summary>
        private async Task SendIdentifyAsync()
        {
            if (_identityHash == null || _identifyStopped || _identifyNoSalt || !IsActive) return;
            if (!IsSessionValid() || _launchSalt != _identitySalt) return; // the next session answer asks again
            string token = SessionToken;
            if (token == _identifyDoneToken) return;
            _identifyDoneToken = token;
            int generation = _identifyGeneration;

            Dictionary<string, object> body = new Dictionary<string, object>(1);
            body["accountHash"] = _identityHash;
            RavensightResponse response = await Send(
                WithPlaytest(new RavensightRequest("POST", ApiUrl + "/identify", RavensightJson.Write(body))
                    .WithHeader("Content-Type", "application/json")
                    .WithHeader("X-API-Key", _config.IngestKey)
                    .WithHeader("X-Session-Token", token)));

            // A late answer for an identity or a session that is gone changes nothing.
            if (generation != _identifyGeneration || token != SessionToken || _disposed) return;
            string code = response.ErrorCode(null);
            if (response.Status >= 200 && response.Status < 300)
            {
                Emit("identified this session");
            }
            else if (response.Status == 404 || (response.Status == 400 && code == "invalid_account_hash"))
            {
                _identifyStopped = true;
                string line = "Ravensight: identify refused (HTTP " + response.Status + (code == null ? "" : " " + code) +
                    "); not tried again this run, the identity is kept.";
                if (response.Status == 400) WarnOnce("identify_bug", line);
                else Emit(line);
            }
            else
            {
                Emit("identify not done (HTTP " + response.Status + (code == null ? "" : " " + code) + "); tried again next session");
            }
        }
    }
}
