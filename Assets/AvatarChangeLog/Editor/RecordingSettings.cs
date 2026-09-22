using System.IO;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AvatarChangeLog
{
    [FilePath("UserSettings/AvatarChangeLog/RecorderSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class RecordingSettings : ScriptableSingleton<RecordingSettings>
    {
        [SerializeField] string avatarId = "";
        [SerializeField] List<string> pausedAvatarIds = new List<string>();
        public string AvatarId => avatarId;
        public bool IsPaused(string id) => !string.IsNullOrEmpty(id) && pausedAvatarIds.Contains(id);
        public void SetPaused(string id, bool paused) => RecordingPauseState.Change(pausedAvatarIds, id, paused, () =>
        {
            Directory.CreateDirectory(HistoryStore.RootDirectory);
            Save(true);
        });
        public void Select(string id)
        {
            Directory.CreateDirectory(HistoryStore.RootDirectory);
            string previous = avatarId;
            avatarId = id ?? "";
            try { Save(true); }
            catch { avatarId = previous; throw; }
        }
    }

    // Save failure must leave both the selected avatar and its recording preference unchanged.
    internal static class RecordingPauseState
    {
        public static void Change(List<string> ids, string id, bool paused, Action save)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("アバターを指定してください。", nameof(id));
            int index = ids.IndexOf(id);
            if ((index >= 0) == paused) return;
            if (paused) ids.Add(id); else ids.RemoveAt(index);
            try { save(); }
            catch
            {
                if (paused) ids.RemoveAt(ids.Count - 1); else ids.Insert(index, id);
                throw;
            }
        }
    }
}
