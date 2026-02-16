using System;
using System.Threading.Tasks;
using UnityEditor;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
    public static class WaitHandler
    {
        public static object Handle(PlayCallerCommand command)
        {
            try
            {
                int ms = command.Params?["ms"]?.ToObject<int>() ?? 0;
                int frames = command.Params?["frames"]?.ToObject<int>() ?? 0;

                if (ms <= 0 && frames <= 0)
                {
                    return PlayCallerResponse.Success(command.Id, new
                    {
                        waited = "0ms"
                    });
                }

                // Prefer frames if both are specified
                if (frames > 0)
                {
                    return WaitFramesAsync(command.Id, frames);
                }
                else
                {
                    return WaitMsAsync(command.Id, ms);
                }
            }
            catch (Exception ex)
            {
                return PlayCallerResponse.Error(command.Id,
                    $"Wait failed: {ex.Message}", "WAIT_ERROR");
            }
        }

        private static Task<string> WaitMsAsync(string id, int milliseconds)
        {
            var tcs = new TaskCompletionSource<string>();
            double start = EditorApplication.timeSinceStartup;

            void Tick()
            {
                if (EditorApplication.timeSinceStartup - start >= milliseconds / 1000.0)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetResult(PlayCallerResponse.Success(id, new
                    {
                        waited = $"{milliseconds}ms"
                    }));
                }
            }

            EditorApplication.update += Tick;
            return tcs.Task;
        }

        private static Task<string> WaitFramesAsync(string id, int frameCount)
        {
            var tcs = new TaskCompletionSource<string>();
            int remainingFrames = frameCount;

            void Tick()
            {
                remainingFrames--;
                if (remainingFrames <= 0)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetResult(PlayCallerResponse.Success(id, new
                    {
                        waited = $"{frameCount} frames"
                    }));
                }
            }

            EditorApplication.update += Tick;
            return tcs.Task;
        }
    }
}
