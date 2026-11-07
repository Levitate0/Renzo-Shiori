using Mihon.ExtensionsBridge.Models.Extensions;

namespace Mihon.ExtensionsBridge.Core.Runtime
{
    /// <summary>
    /// Folds an extension's per-source settings into one entry per setting: a
    /// multi-language extension (MangaDex has dozens of sources) repeats every
    /// setting once per language under keys like <c>pref_x_en</c>. Shared by the
    /// in-process and sidecar bridges so both show the same settings page.
    /// </summary>
    public static class PreferenceGrouping
    {
        public static List<UniquePreference> Group(List<SourcePreference> sourcePrefs)
        {
            var uniquePrefs = new Dictionary<string, UniquePreference>();
            foreach (var sp in SortByLanguageFallback(sourcePrefs))
            {
                foreach (var p in sp.Preferences)
                {
                    string uniqueKey = p.Key;
                    int lastUnderscore = p.Key.LastIndexOf('_');
                    string possibleEnd = "_" + sp.Language;
                    if (uniqueKey.EndsWith(possibleEnd))
                    {
                        uniqueKey = uniqueKey.Substring(0, uniqueKey.Length - possibleEnd.Length);
                    }
                    else if (lastUnderscore > 1)
                    {
                        uniqueKey = uniqueKey.Substring(0, lastUnderscore);
                    }
                    if (uniquePrefs.ContainsKey(uniqueKey))
                    {
                        uniquePrefs[uniqueKey].Languages.Add(new KeyLanguage { Key = p.Key, Language = sp.Language });
                        continue;
                    }
                    UniquePreference up = new UniquePreference
                    {
                        Languages = new List<KeyLanguage> { new KeyLanguage { Key = p.Key, Language = sp.Language } },
                        Preference = p,
                    };
                    uniquePrefs.Add(uniqueKey, up);
                }
            }
            return uniquePrefs.Values.ToList();
        }

        private static List<SourcePreference> SortByLanguageFallback(List<SourcePreference> prefs)
        {
            string[] languageFallbackOrder = new[] { "en", "es", "fr", "de", "it", "pt", "ru", "ja", "zh" };
            return prefs.OrderBy(p =>
            {
                int index = Array.IndexOf(languageFallbackOrder, p.Language);
                return index >= 0 ? index : int.MaxValue;
            }).ThenBy(p => p.Language).ToList();
        }
    }
}
