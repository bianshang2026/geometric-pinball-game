using System;

namespace UnityTcp.Editor.Native
{
    // Minimal JSON parsing helpers shared across the NativeWindowBridgeHost
    // partial class files. These avoid pulling in a full JSON library for
    // the small, well-known message structures used by the bridge protocol.
    internal static partial class NativeWindowBridgeHost
    {
        private static string ExtractJsonString(string json, string key)
        {
            string search = "\"" + key + "\":\"";
            int pos = json.IndexOf(search, StringComparison.Ordinal);
            if (pos < 0) return "";
            pos += search.Length;
            int end = json.IndexOf('"', pos);
            return end < 0 ? "" : json.Substring(pos, end - pos);
        }

        private static float ExtractJsonFloat(string json, string key)
        {
            string search = "\"" + key + "\":";
            int pos = json.IndexOf(search, StringComparison.Ordinal);
            if (pos < 0) return 0f;
            pos += search.Length;
            int end = pos;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-' || json[end] == 'e' || json[end] == 'E' || json[end] == '+'))
                end++;
            if (float.TryParse(json.Substring(pos, end - pos), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float val))
                return val;
            return 0f;
        }

        private static int ExtractJsonInt(string json, string key)
        {
            return (int)ExtractJsonFloat(json, key);
        }

        private static bool ExtractJsonBool(string json, string key)
        {
            string search = "\"" + key + "\":";
            int pos = json.IndexOf(search, StringComparison.Ordinal);
            if (pos < 0) return false;
            pos += search.Length;
            while (pos < json.Length && json[pos] == ' ') pos++;
            return pos < json.Length && json[pos] == 't';
        }

        private static float ExtractJsonArrayFloat(string json, string key, int index)
        {
            string search = "\"" + key + "\":[";
            int pos = json.IndexOf(search, StringComparison.Ordinal);
            if (pos < 0) return 0f;
            pos += search.Length;

            for (int i = 0; i < index; i++)
            {
                int comma = json.IndexOf(',', pos);
                if (comma < 0) return 0f;
                pos = comma + 1;
            }

            int end = pos;
            while (end < json.Length && json[end] != ',' && json[end] != ']')
                end++;

            if (float.TryParse(json.Substring(pos, end - pos).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float val))
                return val;
            return 0f;
        }

        // Used only by SaveCompositeSlotMapping / PrePopulateCompositeSlots
        // for the internal {s, i, t} JSON format. Kept separate from the
        // protocol helpers above because the key format differs slightly
        // (returns null instead of "" on miss).
        private static string ExtractJsonStringValue(string json, string key)
        {
            string pattern = "\"" + key + "\":\"";
            int start = json.IndexOf(pattern);
            if (start < 0) return null;
            start += pattern.Length;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        private static int ExtractJsonIntValue(string json, string key)
        {
            string pattern = "\"" + key + "\":";
            int start = json.IndexOf(pattern);
            if (start < 0) return 0;
            start += pattern.Length;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
                end++;
            if (end == start) return 0;
            int val;
            return int.TryParse(json.Substring(start, end - start), out val) ? val : 0;
        }
    }
}
