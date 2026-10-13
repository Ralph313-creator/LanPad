using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using LanPad.Config;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LanPad.Host
{
    /// <summary>
    /// Captures the host screen (Desktop Duplication via ffmpeg ddagrab) and sound (WASAPI loopback),
    /// encodes once with the AMD hardware encoder (or x264 as fallback) and sends one MPEG-TS
    /// stream to a UDP multicast group, so any number of Join PCs can watch it.
    /// </summary>
    public sealed class StreamBroadcaster : IDisposable
    {
        enum Attempt { AmfZeroCopy, AmfSystemMemory, X264 }

        static bool zeroCopyFailed;

        readonly LanPadConfig cfg;
        readonly object gate = new object();
        readonly LinkedList<string> lastErrors = new LinkedList<string>();
        Process ffmpeg;
        Stream ffmpegInput;
        WasapiLoopbackCapture capture;
        WasapiOut keepAudioAlive;
        string target = "";

        public StreamBroadcaster(LanPadConfig cfg)
        {
            this.cfg = cfg;
        }

        public static string FindFfmpeg() => AppPaths.FindTool("ffmpeg.exe", @"ffmpeg\ffmpeg.exe");

        public string EncoderInUse { get; private set; } = "";
        public string Target => target;

        /// <summary>Send the host's sound too (default). Tests can turn it off.</summary>
        public bool IncludeAudio { get; set; } = true;

        /// <summary>Extra ffmpeg output options (tests / tuning).</summary>
        public string ExtraOutputArgs { get; set; } = "";

        public bool IsRunning
        {
            get { lock (gate) return ffmpeg != null && !ffmpeg.HasExited; }
        }

        /// <summary>Quick test: can this PC encode H.264 with the AMD hardware encoder?</summary>
        public static bool AmfWorks(string ffmpegPath)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpegPath,
                    "-hide_banner -loglevel error -f lavfi -i color=black:s=640x360:r=30 -frames:v 10 -c:v h264_amf -f null -")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) { p.Kill(); return false; }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Starts streaming. Tries AMD zero-copy first, then AMD with a CPU copy, then x264 (720p).</summary>
        public bool Start(string group, int port, IPAddress localAddress, out string error)
        {
            error = null;
            Stop();
            string exe = FindFfmpeg();
            if (exe == null)
            {
                error = "ffmpeg.exe not found in the tools folder";
                return false;
            }

            var attempts = new List<Attempt>();
            if (cfg.Encoder != "x264")
            {
                // Zero-copy fails on some Vega APUs (AMF_DIRECTX_FAILED); after one failure skip it.
                if (!zeroCopyFailed) attempts.Add(Attempt.AmfZeroCopy);
                attempts.Add(Attempt.AmfSystemMemory);
            }
            if (cfg.Encoder != "amf") attempts.Add(Attempt.X264);

            string url = $"udp://{group}:{port}?pkt_size=1316&ttl=1&localaddr={localAddress}&buffer_size=1048576";
            foreach (var attempt in attempts)
            {
                if (TryStart(exe, attempt, url))
                {
                    target = $"{group}:{port}";
                    EncoderInUse = attempt == Attempt.X264 ? "x264 (CPU, 720p)" : "AMD hardware (AMF)";
                    Log.Info($"Streaming to {target} with {EncoderInUse}");
                    return true;
                }
                Log.Warn($"Stream attempt {attempt} failed: {LastErrors()}");
                if (attempt == Attempt.AmfZeroCopy) zeroCopyFailed = true;
            }
            error = "ffmpeg could not start streaming. " + LastErrors();
            return false;
        }

        bool TryStart(string exe, Attempt attempt, string url)
        {
            lock (lastErrors) lastErrors.Clear();

            // Audio: WASAPI loopback of everything the host plays. Its format decides the raw pipe format.
            WaveFormat wf = null;
            if (IncludeAudio)
            {
                try
                {
                    StartKeepAlive();
                    capture = new WasapiLoopbackCapture();
                    wf = capture.WaveFormat;
                }
                catch (Exception ex)
                {
                    Log.Warn("No audio device for loopback capture, streaming video only: " + ex.Message);
                    capture = null;
                    wf = null;
                }
            }

            // x264 (CPU) cannot keep 60 fps on an APU while a game runs; 30 fps is the safe fallback.
            int fps = attempt == Attempt.X264 ? Math.Min(cfg.Fps, 30) : cfg.Fps;
            int br = cfg.BitrateKbps;
            string grab = $"ddagrab=output_idx={cfg.Display}:framerate={fps}:draw_mouse=0";
            string video;
            switch (attempt)
            {
                case Attempt.AmfZeroCopy:
                    video = $"-f lavfi -i \"{grab}\"";
                    break;
                case Attempt.AmfSystemMemory:
                    video = $"-f lavfi -i \"{grab},hwdownload,format=bgra\"";
                    break;
                default:
                    video = $"-f lavfi -i \"{grab},hwdownload,format=bgra,scale=1280:720\"";
                    br = Math.Min(br, 8000);
                    break;
            }

            string encoder = attempt == Attempt.X264
                ? $"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -b:v {br}k -maxrate {br}k -bufsize {br / 2}k -g {fps} -bf 0"
                // async_depth 1 + latency 1: no frame queue inside the encoder. header_spacing + forced_idr:
                // SPS/PPS before every keyframe, so a viewer can join mid-stream within 1 second.
                : $"-c:v h264_amf -usage ultralowlatency -quality speed -rc cbr -latency 1 -async_depth 1 " +
                  $"-b:v {br}k -maxrate {br}k -bufsize {br / 2}k -g {fps} -bf 0 -header_spacing {fps} -forced_idr 1";

            // Video and sound go out as two separate streams (port and port+2). In one combined stream
            // the video has to wait for audio packets, which measured about 100 ms of extra delay.
            const string ts = "-f mpegts -muxdelay 0 -muxpreload 0 -flush_packets 1";
            var sb = new StringBuilder("-hide_banner -loglevel warning -nostats ");
            sb.Append("-thread_queue_size 64 ").Append(video).Append(' ');
            if (wf != null)
                sb.Append($"-thread_queue_size 1024 -f {RawFormat(wf)} -ar {wf.SampleRate} -ac {wf.Channels} -i pipe:0 ");
            sb.Append("-map 0:v ").Append(encoder).Append(' ').Append(ts).Append(' ');
            if (!string.IsNullOrWhiteSpace(ExtraOutputArgs)) sb.Append(ExtraOutputArgs).Append(' ');
            sb.Append($"\"{url}\" ");
            if (wf != null)
                sb.Append($"-map 1:a -c:a aac -b:a 128k -ar 48000 -ac 2 {ts} \"{AudioUrl(url)}\"");

            var psi = new ProcessStartInfo(exe, sb.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            Log.Info("ffmpeg " + psi.Arguments);

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Exception ex)
            {
                AddError(ex.Message);
                StopAudio();
                return false;
            }

            string ffLog = Path.Combine(AppPaths.TempDir, "LanPad-ffmpeg.log");
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                AddError(e.Data);
                try { File.AppendAllText(ffLog, e.Data + Environment.NewLine); } catch (IOException) { }
            };
            p.BeginErrorReadLine();

            lock (gate)
            {
                ffmpeg = p;
                ffmpegInput = p.StandardInput.BaseStream;
            }

            if (capture != null)
            {
                capture.DataAvailable += OnAudio;
                capture.StartRecording();
            }

            // If ffmpeg is still running after 3 s, the encoder and capture work.
            if (p.WaitForExit(3000))
            {
                AddError($"ffmpeg exited with code {p.ExitCode}");
                Stop();
                return false;
            }
            return true;
        }

        /// <summary>The sound stream uses the video port + 2 (5004 -> 5006).</summary>
        public static int AudioPort(int videoPort) => videoPort + 2;

        static string AudioUrl(string videoUrl)
        {
            var m = System.Text.RegularExpressions.Regex.Match(videoUrl, @"^udp://([^:/]+):(\d+)(.*)$");
            return m.Success ? $"udp://{m.Groups[1].Value}:{AudioPort(int.Parse(m.Groups[2].Value))}{m.Groups[3].Value}" : videoUrl;
        }

        static string RawFormat(WaveFormat wf)
        {
            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (wf.Encoding == WaveFormatEncoding.Extensible && wf.BitsPerSample == 32);
            if (isFloat) return "f32le";
            switch (wf.BitsPerSample)
            {
                case 16: return "s16le";
                case 24: return "s24le";
                default: return "s32le";
            }
        }

        void OnAudio(object sender, WaveInEventArgs e)
        {
            Stream input;
            lock (gate) input = ffmpegInput;
            if (input == null || e.BytesRecorded == 0) return;
            try
            {
                input.Write(e.Buffer, 0, e.BytesRecorded);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// WASAPI loopback only delivers data while something is playing. Playing silence keeps
        /// the audio clock running so ffmpeg never waits for audio (which would freeze the video).
        /// </summary>
        void StartKeepAlive()
        {
            if (keepAudioAlive != null) return;
            try
            {
                keepAudioAlive = new WasapiOut(AudioClientShareMode.Shared, 100);
                keepAudioAlive.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
                keepAudioAlive.Play();
            }
            catch (Exception ex)
            {
                Log.Warn("Silent keep-alive audio failed: " + ex.Message);
                keepAudioAlive?.Dispose();
                keepAudioAlive = null;
            }
        }

        void StopAudio()
        {
            if (capture != null)
            {
                capture.DataAvailable -= OnAudio;
                try { capture.StopRecording(); } catch (Exception) { }
                capture.Dispose();
                capture = null;
            }
            if (keepAudioAlive != null)
            {
                try { keepAudioAlive.Stop(); } catch (Exception) { }
                keepAudioAlive.Dispose();
                keepAudioAlive = null;
            }
        }

        void AddError(string line)
        {
            lock (lastErrors)
            {
                lastErrors.AddLast(line);
                while (lastErrors.Count > 6) lastErrors.RemoveFirst();
            }
        }

        string LastErrors()
        {
            lock (lastErrors) return string.Join(" | ", lastErrors);
        }

        public void Stop()
        {
            StopAudio();
            Process p;
            lock (gate)
            {
                p = ffmpeg;
                ffmpeg = null;
                try { ffmpegInput?.Close(); } catch (IOException) { }
                ffmpegInput = null;
            }
            if (p == null) return;
            try
            {
                if (!p.HasExited && !p.WaitForExit(1500)) p.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            p.Dispose();
            target = "";
            Log.Info("Stream stopped");
        }

        public void Dispose() => Stop();
    }
}
