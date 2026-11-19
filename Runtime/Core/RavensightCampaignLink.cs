using System;
using System.Collections.Generic;
using System.Text;

namespace RealitySE
{
    // Campaign links, mirroring the JS SDK: a Ravensight campaign link adds
    // rs_link=<id> to the address it redirects to. The id goes once, as
    // "campaignLink" on POST /session, with the next session this client opens
    // for its own player; only a 201 answer with a token clears it. It is never
    // saved, never sent by a playtest run or a joined session, and follows the
    // same key and consent gates as every other request.
    public sealed partial class RavensightClient
    {
        /// <summary>The landing address parameter a Ravensight campaign link adds on its redirect.</summary>
        public const string CampaignLinkParameter = "rs_link";

        /// <summary>A campaign link id is this many hex characters.</summary>
        public const int CampaignLinkLength = 24;

        // Guarded by _gate. Memory only.
        private string _campaignLink;

        /// <summary>
        /// The id lower cased when it is exactly 24 hex characters, in any
        /// case; otherwise null. Never trims, so a space either side refuses it.
        /// </summary>
        public static string ValidCampaignLink(string id)
        {
            if (id == null || id.Length != CampaignLinkLength) return null;
            for (int i = 0; i < id.Length; i++)
            {
                if (!IsHexChar(id[i])) return null;
            }
            return id.ToLowerInvariant();
        }

        /// <summary>
        /// The campaign link id in a query string such as a page's
        /// location.search ("?a=1&amp;rs_link=..."), or null. Reads it the way
        /// the browser's URLSearchParams.get does: a leading "?" is skipped, the
        /// first rs_link wins, "+" is a space and percent escapes are decoded,
        /// in the name and the value. The value must then be a valid id.
        /// Anything from a "#" on is ignored, as location.search never has it.
        /// Never throws.
        /// </summary>
        public static string CampaignLinkFromSearch(string search)
        {
            if (string.IsNullOrEmpty(search)) return null;
            try
            {
                int hash = search.IndexOf('#');
                if (hash >= 0) search = search.Substring(0, hash);
                if (search.Length > 0 && search[0] == '?') search = search.Substring(1);

                string[] pairs = search.Split('&');
                for (int i = 0; i < pairs.Length; i++)
                {
                    string pair = pairs[i];
                    if (pair.Length == 0) continue;
                    int equals = pair.IndexOf('=');
                    string name = equals < 0 ? pair : pair.Substring(0, equals);
                    if (FormDecode(name) != CampaignLinkParameter) continue;
                    string value = equals < 0 ? "" : pair.Substring(equals + 1);
                    return ValidCampaignLink(FormDecode(value));
                }
            }
            catch (Exception)
            {
                // A query string the SDK cannot read carries no link.
            }
            return null;
        }

        /// <summary>
        /// Sends a Ravensight campaign link id with the next session this
        /// client opens, so the Campaign links page counts the player and the
        /// session for that link. For a deep link, universal link, App Link or
        /// install referrer the game already handles: pass the rs_link value
        /// of the incoming address (<see cref="CampaignLinkFromSearch"/> reads
        /// it from a query string). Best called before the first session; a
        /// later call goes with the next session the client opens.
        /// Returns true when the id is 24 hex characters (any case) and is now
        /// pending, replacing any earlier one; false, changing nothing,
        /// otherwise. Never saved. Nothing is sent without a key or consent,
        /// and a playtest run or a joined session never sends it.
        /// </summary>
        public bool SetCampaignLink(string id)
        {
            string valid = ValidCampaignLink(id);
            if (valid == null)
            {
                Emit("campaign link ignored: not a 24 character link id");
                return false;
            }
            lock (_gate) { _campaignLink = valid; }
            return true;
        }

        /// <summary>The link a session request should carry: the player's own session only, never a playtest run or a joined session.</summary>
        private string CampaignLinkForSession(string joinToken)
        {
            if (_playtest != null || joinToken != null) return null;
            lock (_gate) { return _campaignLink; }
        }

        /// <summary>
        /// Called under _gate when POST /session answered 201 with a token. A
        /// link set again while the request was on the wire is a new one and
        /// waits for the next session.
        /// </summary>
        private void CampaignLinkDelivered(string sent)
        {
            if (sent != null && _campaignLink == sent) _campaignLink = null;
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static int HexValue(byte b)
        {
            if (b >= '0' && b <= '9') return b - '0';
            if (b >= 'a' && b <= 'f') return b - 'a' + 10;
            return b - 'A' + 10;
        }

        /// <summary>
        /// application/x-www-form-urlencoded decoding as URLSearchParams does
        /// it: "+" is a space, "%XY" is a byte, a "%" without two hex digits
        /// stays as it is, and the bytes are read as UTF-8.
        /// </summary>
        private static string FormDecode(string text)
        {
            string spaced = text.Replace('+', ' ');
            if (spaced.IndexOf('%') < 0) return spaced;
            byte[] input = Encoding.UTF8.GetBytes(spaced);
            List<byte> output = new List<byte>(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                byte b = input[i];
                if (b == (byte)'%' && i + 2 < input.Length && IsHexChar((char)input[i + 1]) && IsHexChar((char)input[i + 2]))
                {
                    output.Add((byte)(HexValue(input[i + 1]) * 16 + HexValue(input[i + 2])));
                    i += 2;
                }
                else
                {
                    output.Add(b);
                }
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
