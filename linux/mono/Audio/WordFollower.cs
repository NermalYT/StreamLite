using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace StreamLite.Audio
{
    /// <summary>
    /// Tune to my voice's teleprompter on Linux: the words of the script count up as you talk (about 2.6 words a second
    /// of talking) - the same estimate Windows uses when it has no speech recognizer. Nothing is listened to beyond
    /// "is someone talking right now" (the recording itself stays in the setup, in memory).
    /// </summary>
    internal sealed class WordFollower : IDisposable
    {
        readonly string[] words;
        int heard;
        readonly object gate = new object();
        double voiceSeconds;

        public int Heard { get { lock (gate) return heard; } }
        public int Count { get { return words.Length; } }
        public bool Estimating { get { return true; } }
        public string Problem { get { return "no speech recognizer on Linux"; } }
        public volatile string LastHeard;

        public WordFollower(string script) { words = Words(script); }

        public static string[] Words(string text)
        {
            return Regex.Split((text ?? "").ToLowerInvariant().Replace("’", "'"), @"[^a-z0-9']+").Where(w => w.Trim('\'').Length > 0).Select(w => w.Trim('\'')).ToArray();
        }

        internal static bool Same(string a, string b) { return a == b; }

        public void Feed(float[] b, int n, bool voiced)
        {
            if (voiced) voiceSeconds += n / 48000.0;
            lock (gate) heard = Math.Max(heard, Math.Min(words.Length, (int)(voiceSeconds * 2.6)));
        }

        internal int FollowWave(string wavPath) { return 0; }

        public void Dispose() { }
    }
}
