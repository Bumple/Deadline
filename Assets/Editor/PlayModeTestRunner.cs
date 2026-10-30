using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace Unity.AI.Assistant.PlayModeTest
{
    [InitializeOnLoad]
    internal static class PlayModeTestRunner
    {
        private const string StateKey = "PlayModeTest.State";
        private const string ResultKey = "PlayModeTest.Result";
        private const string ScriptPathKey = "PlayModeTest.ScriptPath";
        private const string SentinelLog = "PLAY_MODE_TEST_COMPLETE";

        private static readonly int WaitFrames = SessionState.GetInt("PlayModeTest.WaitFrames", 3);
        private static readonly float TestTimeout = SessionState.GetFloat("PlayModeTest.TestTimeout", 15.0f);

        private static List<string> _capturedLogs = new List<string>();
        private const int MaxCapturedLogs = 50;

        static PlayModeTestRunner()
        {
            string state = SessionState.GetString(StateKey, "Idle");
            switch (state)
            {
                case "WaitingForCompile":
                    EditorApplication.delayCall += () => {
                        SessionState.SetString(StateKey, "EnteringPlayMode");
                        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                        EditorApplication.isPlaying = true;
                    };
                    break;
                case "EnteringPlayMode":
                    if (EditorApplication.isPlaying) {
                        SessionState.SetString(StateKey, "InPlayMode");
                        EditorApplication.update += WaitFramesThenRun;
                    }
                    break;
                case "InPlayMode":
                    if (EditorApplication.isPlaying) EditorApplication.update += WaitFramesThenRun;
                    break;
                case "Done":
                    Debug.Log(SentinelLog);
                    EditorApplication.delayCall += SelfDestruct;
                    break;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode) {
                SessionState.SetString(StateKey, "InPlayMode");
                EditorApplication.update += WaitFramesThenRun;
            }
        }

        private static int _frameCount = 0;
        private static bool _setupDone = false;
        private static bool _testDone = false;
        private static double _testStartTime = 0;

        private static void WaitFramesThenRun()
        {
            _frameCount++;
            if (_frameCount < WaitFrames) return;
            if (_testDone) return;

            if (!_setupDone)
            {
                _setupDone = true;
                Application.logMessageReceived += OnLogMessage;
                _testStartTime = EditorApplication.timeSinceStartup;
                Setup();
                return;
            }

            float elapsed = (float)(EditorApplication.timeSinceStartup - _testStartTime);
            bool timedOut = elapsed >= TestTimeout;
            if (Tick(elapsed) || timedOut) FinishTest(timedOut, timedOut ? "Timeout" : null);
        }

        private static void FinishTest(bool isError, string errorMessage)
        {
            _testDone = true;
            EditorApplication.update -= WaitFramesThenRun;
            Application.logMessageReceived -= OnLogMessage;
            string resultJson = GetResult();
            if (isError) resultJson = JsonUtility.ToJson(new TestResult { success = false, error = errorMessage, logs = _capturedLogs.ToArray() });
            SessionState.SetString(ResultKey, resultJson);
            SessionState.SetString(StateKey, "Done");
            EditorApplication.isPlaying = false;
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            if (_capturedLogs.Count < MaxCapturedLogs && (type == LogType.Error || type == LogType.Exception || message.Contains("[Test]")))
                _capturedLogs.Add("[" + type + "] " + message);
        }

        private static void SelfDestruct()
        {
            string scriptPath = SessionState.GetString(ScriptPathKey, "");
            if (!string.IsNullOrEmpty(scriptPath) && AssetDatabase.AssetPathExists(scriptPath)) AssetDatabase.DeleteAsset(scriptPath);
            SessionState.EraseString(StateKey);
            SessionState.EraseString(ScriptPathKey);
        }

        [System.Serializable]
        private class TestResult { public bool success; public string error; public string[] logs; public float initialX; public float xAfter2s; public float xAfter5s; }

        private static float _initialX;
        private static float _xAfter2s;
        private static float _xAfter5s;
        private static bool _logged2s = false;
        private static bool _logged5s = false;

        private static void Setup()
        {
            var dwObj = GameObject.Find("DeathWall");
            if (dwObj != null) {
                var dw = dwObj.GetComponent("DeathWall");
                _initialX = (float)dw.GetType().GetMethod("GetCurrentX").Invoke(dw, null);
                Debug.Log("[Test] Initial Wall X: " + _initialX);
            }
        }

        private static bool Tick(float elapsed)
        {
            var dwObj = GameObject.Find("DeathWall");
            if (dwObj == null) return true;
            var dw = dwObj.GetComponent("DeathWall");
            float currentX = (float)dw.GetType().GetMethod("GetCurrentX").Invoke(dw, null);

            if (elapsed >= 2.0f && !_logged2s) {
                _xAfter2s = currentX;
                Debug.Log("[Test] Wall X after 2s: " + _xAfter2s);
                _logged2s = true;
            }
            if (elapsed >= 5.0f && !_logged5s) {
                _xAfter5s = currentX;
                Debug.Log("[Test] Wall X after 5s: " + _xAfter5s);
                _logged5s = true;
            }

            return elapsed > 6.0f;
        }

        private static string GetResult()
        {
            bool stationaryAt2s = Mathf.Approximately(_initialX, _xAfter2s);
            bool movingAt5s = _xAfter5s > _xAfter2s;
            bool success = stationaryAt2s && movingAt5s;
            
            if (!success) {
                Debug.Log("[Test] FAILURE: Wall moved during delay: " + !stationaryAt2s + " or failed to move after: " + !movingAt5s);
            }

            return JsonUtility.ToJson(new TestResult { 
                success = success, 
                logs = _capturedLogs.ToArray(),
                initialX = _initialX,
                xAfter2s = _xAfter2s,
                xAfter5s = _xAfter5s
            });
        }
    }
}
