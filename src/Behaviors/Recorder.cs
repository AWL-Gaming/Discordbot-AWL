using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using uGIF;
using UnityEngine;

namespace DiscordBot;

public class Recorder : MonoBehaviour
{
    [Header("Discord message")]
    private string playerName = string.Empty;
    public string message = string.Empty;
    private string thumbnail = string.Empty;

    [Header("GIF Settings")]
    private bool isRecording;
    private bool isProcessing;
    private float recordStartTime;
    private int generation;
    private Coroutine? recordingCoroutine;
    private Coroutine? waitCoroutine;
    private static int gifHeight => DiscordBotPlugin.GifResolution.height;
    private static int gifWidth => DiscordBotPlugin.GifResolution.width;
    private static int fps => DiscordBotPlugin.GIF_FPS;
    private static float recordDuration => DiscordBotPlugin.GIF_DURATION;

    public static Recorder? instance;

    private sealed class GifEncodeJob
    {
        public readonly int Generation;
        public readonly List<Image> Frames;
        public volatile bool Completed;
        public byte[] Bytes = Array.Empty<byte>();
        public string Error = string.Empty;

        public GifEncodeJob(int generation, List<Image> frames)
        {
            Generation = generation;
            Frames = frames;
        }
    }

    public void Awake()
    {
        instance = this;
        DiscordBotPlugin.LogDebug("Initializing GIF recorder");
    }

    public void OnDisable()
    {
        StopAndRestoreHud();
    }

    public void OnDestroy()
    {
        StopAndRestoreHud();
        instance = null;
    }

    private void StopAndRestoreHud()
    {
        generation++;

        if (recordingCoroutine != null)
        {
            StopCoroutine(recordingCoroutine);
            recordingCoroutine = null;
        }

        if (waitCoroutine != null)
        {
            StopCoroutine(waitCoroutine);
            waitCoroutine = null;
        }

        isRecording = false;
        isProcessing = false;
        Screenshot.instance?.ShowHud();
    }

    public void StartRecording(string player, string quip, string avatar)
    {
        if (isRecording || isProcessing) return;

        playerName = player;
        message = quip;
        thumbnail = avatar;
        isRecording = true;
        recordStartTime = Time.time;
        int currentGeneration = ++generation;
        recordingCoroutine = StartCoroutine(Record(currentGeneration));
        DiscordBotPlugin.LogDebug("Starting gif recording");
    }

    private IEnumerator Record(int currentGeneration)
    {
        List<Image> frames = new();
        Screenshot.instance?.HideHud();
        float interval = 1f / Math.Max(1, fps);

        try
        {
            while (isRecording && currentGeneration == generation && Time.time - recordStartTime < recordDuration)
            {
                yield return new WaitForEndOfFrame();

                Texture2D? texture = null;
                try
                {
                    texture = ScreenCapture.CaptureScreenshotAsTexture();
                    if (texture != null) frames.Add(new Image(texture));
                }
                finally
                {
                    if (texture != null) Destroy(texture);
                }

                yield return new WaitForSeconds(interval);
            }
        }
        finally
        {
            recordingCoroutine = null;
            Screenshot.instance?.ShowHud();
        }

        if (currentGeneration != generation)
        {
            yield break;
        }

        isRecording = false;
        if (frames.Count == 0)
        {
            DiscordBotPlugin.LogWarning("GIF recording captured no frames");
            yield break;
        }

        isProcessing = true;
        GifEncodeJob job = new(currentGeneration, frames);
        Thread thread = new(() => CreateGif(job)) { IsBackground = true };
        thread.Start();
        waitCoroutine = StartCoroutine(WaitForJob(job));
    }

    private IEnumerator WaitForJob(GifEncodeJob job)
    {
        while (!job.Completed && job.Generation == generation) yield return null;

        if (job.Generation != generation)
        {
            yield break;
        }

        waitCoroutine = null;
        isProcessing = false;

        if (!string.IsNullOrWhiteSpace(job.Error))
        {
            DiscordBotPlugin.LogError($"Failed to create death GIF: {job.Error}");
            yield break;
        }

        SendGif(job.Bytes);
    }

    public void Cleanup()
    {
        isRecording = false;
        isProcessing = false;
    }

    private static void CreateGif(GifEncodeJob job)
    {
        try
        {
            GIFEncoder encoder = new()
            {
                useGlobalColorTable = true,
                repeat = 0,
                FPS = fps,
                transparent = new Color32(255, 0, 255, 255),
                dispose = 1
            };

            using MemoryStream stream = new();
            encoder.Start(stream);
            foreach (Image image in job.Frames)
            {
                image.ResizeBilinear(gifWidth, gifHeight);
                image.Flip();
                encoder.AddFrame(image);
            }

            encoder.Finish();
            job.Bytes = stream.ToArray();
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
        }
        finally
        {
            job.Completed = true;
        }
    }

    private void SendGif(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            DiscordBotPlugin.LogWarning("GIF bytes are empty");
            return;
        }

        Discord.instance?.SendGifMessage(Webhook.DeathFeed, playerName, message, bytes, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.gif", thumbnail: thumbnail);
        string worldName = ZNet.instance?.GetWorldName() ?? "Server";
        Discord.instance?.Internal_BroadcastMessage(worldName, message, false);
    }
}
