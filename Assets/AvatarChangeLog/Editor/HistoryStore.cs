using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AvatarChangeLog
{
    public static class HistoryStore
    {
        static string validatedDirectory;
        static readonly Dictionary<string, (long length, long write, string id, string time)> validatedRetention =
            new Dictionary<string, (long, long, string, string)>(StringComparer.Ordinal);
        public static string RootDirectory => Path.GetFullPath(Path.Combine(Application.dataPath, "../UserSettings/AvatarChangeLog"));

        public static string DirectoryFor(string avatarId)
        {
            using (var sha = SHA256.Create())
                return Path.Combine(RootDirectory, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(avatarId))).Replace("-", "").ToLowerInvariant());
        }

        public static void Save(Snapshot snapshot)
        {
            Validate(snapshot);
            string directory = DirectoryFor(snapshot.avatarId);
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, snapshot.id + ".json");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(snapshot), new UTF8Encoding(false));
                File.Move(temporary, destination); // Never overwrite an existing snapshot.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static List<Snapshot> Load(string avatarId, out List<string> errors)
        {
            errors = new List<string>();
            var result = new List<Snapshot>();
            string directory = DirectoryFor(avatarId);
            if (!Directory.Exists(directory)) return result;
            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    var snapshot = JsonUtility.FromJson<Snapshot>(File.ReadAllText(file, Encoding.UTF8));
                    Validate(snapshot);
                    if (snapshot.avatarId != avatarId) throw new InvalidDataException("アバターIDが一致しません。");
                    snapshot.entries.RemoveAll(RecordingFields.IsAvatarDebugEntry);
                    result.Add(snapshot);
                }
                catch (Exception e) { errors.Add(Path.GetFileName(file) + ": " + e.Message); }
            }
            return result.OrderBy(s => s.createdUtc, StringComparer.Ordinal).ThenBy(s => s.id, StringComparer.Ordinal).ToList();
        }

        public static Snapshot LoadLatest(string avatarId) => LoadLatestFrom(DirectoryFor(avatarId), avatarId,
            json => JsonUtility.FromJson<Snapshot>(json));

        // Read small headers first. Only deserialize full entries for the newest valid record.
        internal static Snapshot LoadLatestFrom(string directory, string avatarId, Func<string, Snapshot> parse)
        {
            if (!Directory.Exists(directory)) return null;
            var candidates = Headers(Directory.EnumerateFiles(directory, "*.json"), avatarId, parse);
            foreach (var candidate in candidates.OrderByDescending(c => c.time, StringComparer.Ordinal).ThenByDescending(c => c.id, StringComparer.Ordinal))
                try
                {
                    var snapshot = parse(File.ReadAllText(candidate.file, Encoding.UTF8));
                    Validate(snapshot);
                    if (snapshot.avatarId == avatarId)
                    {
                        // Filter only the in-memory copy, including the recorder's baseline.
                        // Existing JSON is untouched and excluded fields cannot appear as deletions.
                        snapshot.entries.RemoveAll(RecordingFields.IsAvatarDebugEntry);
                        return snapshot;
                    }
                }
                catch { }
            return null;
        }

        static List<(string file, string time, string id)> Headers(IEnumerable<string> files, string avatarId, Func<string, Snapshot> parse)
        {
            var candidates = new List<(string file, string time, string id)>();
            foreach (string file in files)
                try
                {
                    string header;
                    using (var reader = new StreamReader(file, Encoding.UTF8))
                    {
                        var buffer = new char[4096];
                        int count = reader.ReadBlock(buffer, 0, buffer.Length);
                        header = HeaderJson(new string(buffer, 0, count));
                    }
                    var metadata = parse(header ?? File.ReadAllText(file, Encoding.UTF8));
                    if (header != null && (metadata == null || string.IsNullOrEmpty(metadata.avatarId) ||
                        !Guid.TryParseExact(metadata.id, "N", out _) || !DateTimeOffset.TryParse(metadata.createdUtc, out _)))
                        metadata = parse(File.ReadAllText(file, Encoding.UTF8));
                    if (metadata == null || metadata.avatarId != avatarId || metadata.formatVersion != Snapshot.CurrentFormat ||
                        !Guid.TryParseExact(metadata.id, "N", out _) || !DateTimeOffset.TryParse(metadata.createdUtc, out _)) continue;
                    candidates.Add((file, metadata.createdUtc, metadata.id));
                }
                catch { /* Corrupt histories remain on disk; full Load reports them in the window. */ }
            return candidates;
        }

        public const int RecordLimit = 100;
        public static HashSet<string> Prune(string avatarId) => PruneFrom(DirectoryFor(avatarId), avatarId, RecordLimit,
            json => JsonUtility.FromJson<Snapshot>(json));

        internal static HashSet<string> PruneFrom(string directory, string avatarId, int limit, Func<string, Snapshot> parse)
        {
            limit = Math.Max(2, limit);
            var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : Array.Empty<string>();
            var retainedFiles = new HashSet<string>(files.Select(Path.GetFileName),
                Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (files.Length <= limit) return retainedFiles;
            if (validatedDirectory != directory) { validatedRetention.Clear(); validatedDirectory = directory; }
            // Exact files only, no recursive deletion; error archives and unknown files are untouched.
            var candidates = Headers(files, avatarId, parse).OrderByDescending(c => c.time, StringComparer.Ordinal)
                .ThenByDescending(c => c.id, StringComparer.Ordinal).ToList();
            if (candidates.Count <= limit) return retainedFiles;
            int retained = 0;
            foreach (var candidate in candidates)
            {
                if ((File.GetAttributes(candidate.file) & FileAttributes.ReparsePoint) != 0) continue;
                if (Path.GetFileName(candidate.file) != candidate.id + ".json") continue;
                // Validate kept records too so damaged recent records never displace valid baselines.
                var info = new FileInfo(candidate.file);
                if (!validatedRetention.TryGetValue(candidate.file, out var stamp) || stamp.length != info.Length ||
                    stamp.write != info.LastWriteTimeUtc.Ticks || stamp.id != candidate.id || stamp.time != candidate.time)
                {
                    Snapshot snapshot;
                    try { snapshot = parse(File.ReadAllText(candidate.file, Encoding.UTF8)); Validate(snapshot); }
                    catch { continue; }
                    if (snapshot.avatarId != avatarId || snapshot.id != candidate.id || snapshot.createdUtc != candidate.time) continue;
                    validatedRetention[candidate.file] = (info.Length, info.LastWriteTimeUtc.Ticks, candidate.id, candidate.time);
                }
                if (++retained > limit)
                {
                    File.Delete(candidate.file);
                    retainedFiles.Remove(Path.GetFileName(candidate.file));
                    validatedRetention.Remove(candidate.file);
                }
            }
            var remaining = new HashSet<string>(candidates.Select(c => c.file), StringComparer.Ordinal);
            foreach (string file in validatedRetention.Keys.Where(k => !remaining.Contains(k)).ToArray()) validatedRetention.Remove(file);
            return retainedFiles;
        }

        internal static string HeaderJson(string prefix)
        {
            // Serializer puts metadata before entries. Unusual layouts use the safe full-read fallback.
            bool quoted = false, escaped = false;
            int depth = 0;
            for (int i = 0; i < prefix.Length; i++)
            {
                char c = prefix[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') { quoted = true; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ',' && depth == 1)
                {
                    int start = i + 1;
                    while (start < prefix.Length && char.IsWhiteSpace(prefix[start])) start++;
                    const string key = "\"entries\"";
                    if (start + key.Length > prefix.Length || string.CompareOrdinal(prefix, start, key, 0, key.Length) != 0) continue;
                    start += key.Length;
                    while (start < prefix.Length && char.IsWhiteSpace(prefix[start])) start++;
                    if (start < prefix.Length && prefix[start] == ':') return prefix.Substring(0, i) + "}";
                }
            }
            return null;
        }

        static void Validate(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.formatVersion != Snapshot.CurrentFormat ||
                !Guid.TryParseExact(snapshot.id, "N", out _) || string.IsNullOrEmpty(snapshot.avatarId) ||
                !DateTimeOffset.TryParse(snapshot.createdUtc, out _) || snapshot.entries == null || snapshot.warnings == null)
                throw new InvalidDataException("記録形式が不正、または未対応のバージョンです。");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in snapshot.entries)
                if (e == null || string.IsNullOrEmpty(e.key) || e.category == null || e.path == null ||
                    e.item == null || e.value == null || !keys.Add(e.key))
                    throw new InvalidDataException("記録項目が不正または重複しています。");
        }
    }
}
