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

    private sealed class GifEncodingProfile
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int FrameStep;
        public readonly int FramesPerSecond;
        public readonly string Label;

        public GifEncodingProfile(int width, int height, int frameStep, int framesPerSecond, string label)
        {
            Width = width;
            Height = height;
            FrameStep = frameStep;
            FramesPerSecond = framesPerSecond;
            Label = label;
        }
    }

    private sealed class GifEncodeJob
    {
        public readonly int Generation;
        public readonly List<Image> Frames;
        public readonly Image FallbackFrame;
        public readonly List<string> Diagnostics = new();
        public volatile bool Completed;
        public byte[] Bytes = Array.Empty<byte>();
        public string SelectedProfile = string.Empty;
        public string Error = string.Empty;

        public GifEncodeJob(int generation, List<Image> frames)
        {
            Generation = generation;
            Frames = frames;
            FallbackFrame = CloneImage(frames[frames.Count / 2]);
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
        if (isRecording || isProcessing)
        {
            SendTextOnlyFallback(
                player,
                quip,
                avatar,
                "another death capture is still recording or encoding");
            return;
        }

        playerName = player;
        message = quip;
        thumbnail = avatar;
        isRecording = true;
        recordStartTime = Time.time;
        int currentGeneration = ++generation;
        recordingCoroutine = StartCoroutine(Record(currentGeneration));
        DiscordBotPlugin.LogDebug(
            $"Starting death GIF recording at {gifWidth}x{gifHeight}, {fps} FPS for {recordDuration:0.##} seconds");
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
                    if (texture != null) UnityEngine.Object.Destroy(texture);
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
            DiscordBotPlugin.LogWarning("GIF recording captured no frames; sending a text-only death notice");
            SendTextOnlyFallback("GIF recording captured no frames");
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

        foreach (string diagnostic in job.Diagnostics)
        {
            if (diagnostic.IndexOf("exceeded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DiscordBotPlugin.LogWarning(diagnostic);
            }
            else
            {
                DiscordBotPlugin.LogInfo(diagnostic);
            }
        }

        byte[] fallbackPng = CreateFallbackPng(job.FallbackFrame);
        if (fallbackPng.Length > 0)
        {
            DiscordBotPlugin.LogDebug($"Prepared death PNG fallback with {SizeFormatter.FormatBytes(fallbackPng.Length)}");
        }

        if (!string.IsNullOrWhiteSpace(job.Error))
        {
            DiscordBotPlugin.LogError($"Failed to create death GIF: {job.Error}");
            if (fallbackPng.Length > 0)
            {
                SendPngFallback(fallbackPng, "GIF encoder failure");
            }
            else
            {
                SendTextOnlyFallback("GIF encoder and PNG fallback both failed");
            }
            yield break;
        }

        if (job.Bytes.Length == 0)
        {
            if (fallbackPng.Length > 0)
            {
                SendPngFallback(fallbackPng, $"all adaptive GIF encodes exceeded the {SizeFormatter.FormatBytes(Discord.RemoteAttachmentSafetyBytes)} transport limit");
            }
            else
            {
                SendTextOnlyFallback("all adaptive GIF encodes exceeded the limit and PNG fallback failed");
            }
            yield break;
        }

        SendGif(job.Bytes, fallbackPng, job.SelectedProfile);
    }

    public void Cleanup()
    {
        StopAndRestoreHud();
    }

    private static void CreateGif(GifEncodeJob job)
    {
        try
        {
            List<GifEncodingProfile> profiles = BuildEncodingProfiles();
            foreach (GifEncodingProfile profile in profiles)
            {
                byte[] bytes = EncodeGif(job.Frames, profile);
                string diagnostic =
                    $"Encoded death GIF using {profile.Label}: {profile.Width}x{profile.Height}, " +
                    $"every {profile.FrameStep} frame(s), {profile.FramesPerSecond} FPS, {SizeFormatter.FormatBytes(bytes.Length)}";

                if (bytes.Length <= Discord.RemoteAttachmentSafetyBytes)
                {
                    job.Diagnostics.Add(diagnostic);
                    job.Bytes = bytes;
                    job.SelectedProfile = profile.Label;
                    return;
                }

                job.Diagnostics.Add(diagnostic);
                job.Diagnostics.Add(
                    $"Death GIF profile {profile.Label} exceeded the {SizeFormatter.FormatBytes(Discord.RemoteAttachmentSafetyBytes)} safety limit; retrying with a smaller profile");
            }
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

    private static List<GifEncodingProfile> BuildEncodingProfiles()
    {
        List<GifEncodingProfile> profiles = new();
        AddEncodingProfile(profiles, gifWidth, gifHeight, 1, Math.Max(1, fps), "configured GIF");
        AddEncodingProfile(profiles, ScaleDimension(gifWidth, 0.75f), ScaleDimension(gifHeight, 0.75f), 2, Math.Max(8, fps / 2), "adaptive GIF level 1");
        AddEncodingProfile(profiles, ScaleDimension(gifWidth, 0.5f), ScaleDimension(gifHeight, 0.5f), 3, Math.Max(6, fps / 3), "adaptive GIF level 2");
        AddEncodingProfile(profiles, ScaleDimension(gifWidth, 0.375f), ScaleDimension(gifHeight, 0.375f), 4, Math.Max(5, fps / 4), "adaptive GIF level 3");
        return profiles;
    }

    private static void AddEncodingProfile(
        List<GifEncodingProfile> profiles,
        int width,
        int height,
        int frameStep,
        int framesPerSecond,
        string label)
    {
        width = Math.Max(160, width);
        height = Math.Max(90, height);
        if (profiles.Exists(profile =>
                profile.Width == width &&
                profile.Height == height &&
                profile.FrameStep == frameStep))
        {
            return;
        }

        profiles.Add(new GifEncodingProfile(width, height, frameStep, framesPerSecond, label));
    }

    private static byte[] EncodeGif(List<Image> frames, GifEncodingProfile profile)
    {
        GIFEncoder encoder = new()
        {
            useGlobalColorTable = true,
            repeat = 0,
            FPS = profile.FramesPerSecond,
            transparent = new Color32(255, 0, 255, 255),
            dispose = 1
        };

        using MemoryStream stream = new();
        encoder.Start(stream);
        int addedFrames = 0;
        for (int index = 0; index < frames.Count; index += profile.FrameStep)
        {
            Image image = CloneImage(frames[index]);
            image.ResizeBilinear(profile.Width, profile.Height);
            image.Flip();
            encoder.AddFrame(image);
            addedFrames++;
        }

        if (addedFrames == 0)
        {
            throw new InvalidOperationException("No frames were available for GIF encoding");
        }

        encoder.Finish();
        return stream.ToArray();
    }

    private static Image CloneImage(Image image)
    {
        return new Image((Color32[])image.pixels.Clone(), image.width, image.height);
    }

    private static int ScaleDimension(int value, float scale)
    {
        return Math.Max(2, Mathf.RoundToInt(value * scale));
    }

    private static byte[] CreateFallbackPng(Image source)
    {
        Texture2D? texture = null;
        try
        {
            Image image = CloneImage(source);
            GetFittedDimensions(image.width, image.height, 960, 540, out int width, out int height);
            image.ResizeBilinear(width, height);
            image.Flip();

            texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture.SetPixels32(image.pixels);
            texture.Apply();
            return texture.EncodeToPNG() ?? Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            DiscordBotPlugin.LogWarning($"Failed to create death PNG fallback: {ex.Message}");
            return Array.Empty<byte>();
        }
        finally
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
    }

    private static void GetFittedDimensions(
        int sourceWidth,
        int sourceHeight,
        int maximumWidth,
        int maximumHeight,
        out int width,
        out int height)
    {
        float scale = Math.Min(1f, Math.Min((float)maximumWidth / sourceWidth, (float)maximumHeight / sourceHeight));
        width = Math.Max(2, Mathf.RoundToInt(sourceWidth * scale));
        height = Math.Max(2, Mathf.RoundToInt(sourceHeight * scale));
    }

    private void SendGif(byte[] bytes, byte[] fallbackPng, string profile)
    {
        if (bytes.Length == 0)
        {
            DiscordBotPlugin.LogWarning("GIF bytes are empty");
            if (fallbackPng.Length > 0) SendPngFallback(fallbackPng, "empty GIF output");
            else SendTextOnlyFallback("empty GIF output and PNG fallback unavailable");
            return;
        }

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        DiscordBotPlugin.LogInfo(
            $"Sending death GIF using {profile} with {SizeFormatter.FormatBytes(bytes.Length)} and a {SizeFormatter.FormatBytes(fallbackPng.Length)} PNG fallback");
        Discord.instance?.SendGifMessage(
            Webhook.DeathFeed,
            playerName,
            message,
            bytes,
            $"{timestamp}.gif",
            thumbnail: thumbnail,
            fallbackPng: fallbackPng,
            fallbackFilename: $"{timestamp}.png",
            transferLabel: profile);
        BroadcastQuip();
    }

    private void SendPngFallback(byte[] bytes, string reason)
    {
        if (bytes.Length == 0)
        {
            SendTextOnlyFallback($"PNG fallback was empty after {reason}");
            return;
        }

        DiscordBotPlugin.LogWarning(
            $"Sending death PNG fallback because {reason}; encoded size is {SizeFormatter.FormatBytes(bytes.Length)}");
        Discord.instance?.SendImageMessage(
            Webhook.DeathFeed,
            playerName,
            message,
            bytes,
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.png",
            thumbnail: thumbnail,
            transferLabel: $"PNG fallback: {reason}");
        BroadcastQuip();
    }

    private void SendTextOnlyFallback(string reason)
    {
        SendTextOnlyFallback(playerName, message, thumbnail, reason);
    }

    private static void SendTextOnlyFallback(string player, string quip, string avatar, string reason)
    {
        DiscordBotPlugin.LogWarning($"Sending text-only death notice because {reason}");
        Discord.instance?.SendEmbedMessage(Webhook.DeathFeed, player, quip, thumbnail: avatar);
        BroadcastQuip(quip);
    }

    private void BroadcastQuip()
    {
        BroadcastQuip(message);
    }

    private static void BroadcastQuip(string content)
    {
        string worldName = ZNet.instance?.GetWorldName() ?? "Server";
        Discord.instance?.Internal_BroadcastMessage(worldName, content, false);
    }

}
