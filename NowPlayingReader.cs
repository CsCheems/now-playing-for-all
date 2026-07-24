using System;
using System.Collections.Generic;
using System.IO;
using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Diagnostics;

namespace ChemitaDev.NowPlaying
{
    public sealed class NowPlayingInfo
    {
        public string AppId        { get; set; } = "";
        public string Title        { get; set; } = "";
        public string Artist       { get; set; } = "";
        public string Album        { get; set; } = "";
        public bool   IsPlaying    { get; set; }
        public long   PositionMs   { get; set; }
        public long   DurationMs   { get; set; }
        public string ThumbnailUrl { get; set; }
        public bool   IsCurrent    { get; set; }
    }

    public static class NowPlayingReader
    {
        public const string BuildTag = "multi-session-v1";

        private static readonly object _initGate = new object();
        private static GlobalSystemMediaTransportControlsSessionManager _manager;

        private sealed class SessionState
        {
            public string CachedTitle;
            public string CachedArtist;
            public string CachedAlbum;
            public string CachedThumbDataUri;

            public long LastReportedPosition;
            public long LastDuration;
            public long AnchorTimestamp;
            public bool LastPlaying;
            public long LastRawReported = -1;
        }

        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static readonly object _statesGate = new object();
        private static readonly Dictionary<string, SessionState> _states =
            new Dictionary<string, SessionState>(StringComparer.Ordinal);

        public static NowPlayingInfo Poll()
        {
            try
            {
                var session = GetCurrentSession();
                if (session == null) return new NowPlayingInfo();
                return BuildInfo(session, isCurrent: true);
            }
            catch
            {
                return new NowPlayingInfo();
            }
        }

        public static List<NowPlayingInfo> PollAll()
        {
            var result = new List<NowPlayingInfo>();
            try
            {
                EnsureManager();
                if (_manager == null) return result;

                var sessions = _manager.GetSessions();
                if (sessions == null || sessions.Count == 0) return result;

                var current = _manager.GetCurrentSession();
                string currentId = current?.SourceAppUserModelId;

                var seenIds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var session in sessions)
                {
                    if (session == null) continue;
                    try
                    {
                        string id = session.SourceAppUserModelId ?? "";
                        seenIds.Add(id);

                        bool isCurrent = currentId != null && id == currentId;
                        result.Add(BuildInfo(session, isCurrent));
                    }
                    catch
                    {
                        // I should send an error here but meh
                    }
                }

                PruneStaleStates(seenIds);
            }
            catch
            {
                // same
            }

            return result;
        }

        private static NowPlayingInfo BuildInfo(
            GlobalSystemMediaTransportControlsSession session, bool isCurrent)
        {
            var props    = session.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            long reportedPosition = (long)timeline.Position.TotalMilliseconds;
            long duration         = (long)timeline.EndTime.TotalMilliseconds;
            bool isPlaying        = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var state = GetState(session.SourceAppUserModelId ?? "");

            bool songChanged = state.CachedTitle  != props.Title
                            || state.CachedArtist != props.Artist
                            || state.CachedAlbum  != props.AlbumTitle;

            if (songChanged)
            {
                state.LastReportedPosition = reportedPosition;
                state.LastDuration         = duration;
                state.LastPlaying          = isPlaying;
                state.AnchorTimestamp      = _clock.ElapsedMilliseconds;
                state.LastRawReported      = reportedPosition;
            }
            else
            {
                SyncPlaybackClock(state, reportedPosition, duration, isPlaying);
            }

            return new NowPlayingInfo
            {
                AppId        = GetAppName(session.SourceAppUserModelId),
                Title        = props.Title ?? "",
                Artist       = props.Artist ?? "",
                Album        = props.AlbumTitle ?? "",
                IsPlaying    = isPlaying,
                PositionMs   = GetInterpolatedPosition(state),
                DurationMs   = duration,
                ThumbnailUrl = GetCachedThumbnail(state, props),
                IsCurrent    = isCurrent
            };
        }

        private static SessionState GetState(string appId)
        {
            lock (_statesGate)
            {
                if (!_states.TryGetValue(appId, out var state))
                {
                    state = new SessionState();
                    _states[appId] = state;
                }
                return state;
            }
        }

        private static void PruneStaleStates(HashSet<string> aliveIds)
        {
            lock (_statesGate)
            {
                if (_states.Count == 0) return;

                var toRemove = new List<string>();
                foreach (var key in _states.Keys)
                    if (!aliveIds.Contains(key)) toRemove.Add(key);

                foreach (var key in toRemove) _states.Remove(key);
            }
        }

        private static GlobalSystemMediaTransportControlsSession GetCurrentSession()
        {
            EnsureManager();
            if (_manager == null) return null;

            var current = _manager.GetCurrentSession();
            if (current != null) return current;

            var sessions = _manager.GetSessions();
            return sessions.Count > 0 ? sessions[0] : null;
        }

        private static void EnsureManager()
        {
            if (_manager != null) return;
            lock (_initGate)
            {
                if (_manager != null) return;
                _manager = GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync().GetAwaiter().GetResult();
            }
        }

        private static string GetCachedThumbnail(
            SessionState state,
            GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            bool songChanged =
                state.CachedTitle  != props.Title  ||
                state.CachedArtist != props.Artist ||
                state.CachedAlbum  != props.AlbumTitle;

            if (!songChanged) return state.CachedThumbDataUri;

            state.CachedTitle        = props.Title;
            state.CachedArtist       = props.Artist;
            state.CachedAlbum        = props.AlbumTitle;
            state.CachedThumbDataUri = ReadThumbnailDataUri(props);
            return state.CachedThumbDataUri;
        }

        private static string ReadThumbnailDataUri(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            if (props.Thumbnail == null) return null;

            try
            {
                using (var raStream  = props.Thumbnail.OpenReadAsync().GetAwaiter().GetResult())
                using (var netStream = raStream.AsStreamForRead())
                using (var memory    = new MemoryStream())
                {
                    netStream.CopyTo(memory);
                    string base64 = Convert.ToBase64String(memory.ToArray());
                    return "data:image/jpeg;base64," + base64;
                }
            }
            catch
            {
                return null;
            }
        }

        private static long GetInterpolatedPosition(SessionState state)
        {
            long position = state.LastReportedPosition;

            if (state.LastPlaying) position += _clock.ElapsedMilliseconds - state.AnchorTimestamp;

            if (position < 0) position = 0;
            if (state.LastDuration > 0 && position > state.LastDuration) position = state.LastDuration;

            return position;
        }

        private static void SyncPlaybackClock(
            SessionState state, long reportedPosition, long duration, bool isPlaying)
        {
            if (state.LastDuration != duration || state.LastPlaying != isPlaying)
            {
                state.LastReportedPosition = reportedPosition;
                state.LastDuration         = duration;
                state.LastPlaying          = isPlaying;
                state.AnchorTimestamp      = _clock.ElapsedMilliseconds;
                state.LastRawReported      = reportedPosition;
                return;
            }

            if (reportedPosition == state.LastRawReported) return;

            state.LastRawReported = reportedPosition;

            long virtualPosition = GetInterpolatedPosition(state);
            long drift = Math.Abs(virtualPosition - reportedPosition);

            if (drift > 1500)
            {
                state.LastReportedPosition = reportedPosition;
                state.AnchorTimestamp      = _clock.ElapsedMilliseconds;
                return;
            }

            if (drift > 250)
            {
                state.LastReportedPosition = (virtualPosition + reportedPosition) / 2;
                state.AnchorTimestamp      = _clock.ElapsedMilliseconds;
            }
        }

        private static string GetAppName(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return "Unknown";

            appId = appId.ToLowerInvariant();

            if (appId.Contains("spotify"))     return "Spotify";
            if (appId.Contains("zunemusic"))   return "Media Player";
            if (appId.Contains("groove"))      return "Groove Music";
            if (appId.Contains("vlc"))         return "VLC";
            if (appId.Contains("chrome"))      return "Google Chrome";
            if (appId.Contains("msedge"))      return "Microsoft Edge";
            if (appId.Contains("brave"))       return "Brave";
            if (appId.Contains("firefox"))     return "Firefox";
            if (appId.Contains("opera"))       return "Opera";
            if (appId.Contains("discord"))     return "Discord";
            if (appId.Contains("amazonmusic")) return "Amazon Music";
            if (appId.Contains("amazon music")) return "Amazon Music";
            if (appId.Contains("itunes"))      return "iTunes";
            if (appId.Contains("apple"))       return "Apple Music";
            if (appId.Contains("tidal"))       return "Tidal";
            if (appId.Contains("deezer"))      return "Deezer";
            if (appId.Contains("foobar"))      return "foobar2000";
            if (appId.Contains("winamp"))      return "Winamp";
            if (appId.Contains("youtube"))      return "YouTube";
            //idk all the apps, i havent even tried the apple/itunes, im just guessing by the .exe
            //also not every platform is available in my area so feel free to fill/fix it with more 
            //services
            return appId;
        }
    }
}
