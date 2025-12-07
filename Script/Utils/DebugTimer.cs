using System.Collections.Generic;
using UnityEngine;

namespace PhotonGISystem2
{
    public static class DebugTimer
    {
        private static Dictionary<string, float> _timers = new Dictionary<string, float>();
        private const string DefaultName = "default";

        /// <summary>
        /// Starts a timer with the specified name. If no name is provided, uses "default".
        /// </summary>
        /// <param name="name">Optional timer name. Defaults to "default" if not provided.</param>
        public static void Start(string name = DefaultName)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = DefaultName;
            }

            _timers[name] = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Ends a timer with the specified name and logs the elapsed time. If no name is provided, uses "default".
        /// </summary>
        /// <param name="name">Optional timer name. Defaults to "default" if not provided.</param>
        public static void End(string name = DefaultName)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = DefaultName;
            }

            if (!_timers.ContainsKey(name))
            {
                Debug.LogWarning($"DebugTimer: Timer '{name}' was not started. Call Start() first.");
                return;
            }

            float startTime = _timers[name];
            float elapsedTime = Time.realtimeSinceStartup - startTime;
            _timers.Remove(name);

            Debug.Log($"DebugTimer [{name}]: {elapsedTime * 1000f:F3} ms");
        }

        /// <summary>
        /// Ends a timer and immediately starts a new one with the same name. Useful for measuring intervals.
        /// </summary>
        /// <param name="name">Optional timer name. Defaults to "default" if not provided.</param>
        public static void EndAndRestart(string name = DefaultName)
        {
            End(name);
            Start(name);
        }

        /// <summary>
        /// Gets the elapsed time for a timer without ending it.
        /// </summary>
        /// <param name="name">Optional timer name. Defaults to "default" if not provided.</param>
        /// <returns>Elapsed time in seconds, or -1 if timer not found.</returns>
        public static float GetElapsed(string name = DefaultName)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = DefaultName;
            }

            if (!_timers.ContainsKey(name))
            {
                return -1f;
            }

            return Time.realtimeSinceStartup - _timers[name];
        }

        /// <summary>
        /// Clears all timers.
        /// </summary>
        public static void ClearAll()
        {
            _timers.Clear();
        }

        /// <summary>
        /// Removes a specific timer without logging.
        /// </summary>
        /// <param name="name">Optional timer name. Defaults to "default" if not provided.</param>
        public static void Remove(string name = DefaultName)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = DefaultName;
            }

            _timers.Remove(name);
        }
    }
}
