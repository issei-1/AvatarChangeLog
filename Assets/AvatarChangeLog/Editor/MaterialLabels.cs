using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace AvatarChangeLog
{
    internal static class MaterialLabels
    {
        static bool searched;
        static Func<string, string> localize;

        static string Translate(string key)
        {
            if (!searched)
            {
                searched = true;
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("lilToon.lilLanguageManager", false)).FirstOrDefault(t => t != null);
                var method = type?.GetMethod("GetLoc", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (method != null) localize = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), method);
            }
            // Read the installed shader's language labels; never create or draw an inspector.
            try { return localize?.Invoke(key) ?? key; }
            catch { return key; }
        }

        internal static string For(Shader shader, string property, int index)
        {
            string description = shader.GetPropertyDescription(index);
            bool lil = shader.name.IndexOf("lil", StringComparison.OrdinalIgnoreCase) >= 0;
            return Resolve(property, description, lil, Translate);
        }

        internal static string Resolve(string property, string description, bool lil, Func<string, string> translate)
        {
            string[] parts = property.Split('/');
            string name = parts[0];
            string label = string.IsNullOrWhiteSpace(description) ? name : description;
            if (lil)
            {
                // These numeric fields are drawn beside a texture with a shared label.
                switch (name)
                {
                    case "_BumpMap": case "_BumpScale": label = "sNormalMap"; break;
                    case "_Bump2ndMap": case "_Bump2ndScale": label = "sNormalMap2nd"; break;
                    case "_MatCapBumpMap": case "_MatCapBumpScale": label = "sMatCap+sNormalMap"; break;
                    case "_MatCap2ndBumpMap": case "_MatCap2ndBumpScale": label = "sMatCap2nd+sNormalMap"; break;
                }
                label = string.Concat(label.Split('|')[0].Split('+').Select(token =>
                {
                    string translated = translate?.Invoke(token);
                    if (!string.IsNullOrEmpty(translated) && translated != token) return translated;
                    switch (token)
                    {
                        case "sNormalMap": return "ノーマルマップ";
                        case "sNormalMap2nd": return "ノーマルマップ2nd";
                        case "sMatCap": return "マットキャップ / ";
                        case "sMatCap2nd": return "マットキャップ2nd / ";
                        default: return token;
                    }
                }));
            }
            if (parts.Length > 1) label += parts[1] == "Scale" ? " / タイリング" : parts[1] == "Offset" ? " / オフセット" : " / " + parts[1];
            return label;
        }
    }
}
