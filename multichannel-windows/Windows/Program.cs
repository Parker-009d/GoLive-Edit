using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GoLivEdit;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MixerWindow());
    }
}

internal sealed class MixerWindow : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly System.Windows.Forms.Timer stateTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer meterTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer spectrumTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer deviceTimer = new() { Interval = 2000 };
    private readonly object audioLock = new();
    private readonly object recordLock = new();
    private Channel<CapturePacket>? captureQueue;
    private CancellationTokenSource? captureWorkerCancellation;
    private Task? captureWorker;
    private sealed record CapturePacket(byte[] Buffer, int Length, WaveFormat Format);
    private readonly LocalDSP[] localDSP = Enumerable.Range(0,24).Select(_ => new LocalDSP()).ToArray();
    private readonly LocalFX localFX = new();
    private readonly double[] smoothedLeftGain = new double[24];
    private readonly double[] smoothedRightGain = new double[24];
    private MMDevice? selectedDevice;
    private MMDevice? selectedOutput;
    private WasapiOut? monitorOut;
    private BufferedWaveProvider? monitorBuffer;
    private int monitorSampleRate;
    private readonly StreamingPcm24Resampler monitorResampler = new();
    private WasapiCapture? capture;
    private WaveFileWriter? writer;
    private JsonElement mixerState;
    private string remoteState = "{\"ch\":[],\"main\":{}}";
    private readonly float[][] spectrumRing = Enumerable.Range(0, 24).Select(_ => new float[2048]).ToArray();
    private static readonly double[] SpectrumWindow = Enumerable.Range(0, 2048).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / 2047)).ToArray();
    private int spectrumBusy;
    private long captureBufferCount, captureDroppedPackets;
    private int spectrumIndex;
    private int spectrumFilled;
    private double[] levels = Enumerable.Repeat(-90d, 24).ToArray();
    private bool[] clips = new bool[24];
    private DateTime[] clipUntil = new DateTime[24];
    private double sampleRate;
    private int channelCount;
    private volatile bool captureHasData;
    private volatile bool captureHasSignal;
    private volatile string captureError = "";
    private string monitorNotice = "";
    private string selectedUid = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "AudioDeviceUid", "") as string ?? "";
    private readonly ObsSocket obs = new();
    private string pairingCode = NewPairCode();
    private string obsSource1 = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "ObsSource1", "") as string ?? "";
    private string obsSource2 = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "ObsSource2", "") as string ?? "";
    private readonly HashSet<string> pairedTokens = new();
    private HttpListener? remote;
    private DateTime codeExpiry = DateTime.UtcNow.AddMinutes(3);
    private const int PairPort = 8443;

    public MixerWindow()
    {
        Text = "GoLiv Edit Multichannel 24";
        MinimumSize = new Size(980, 620);
        ClientSize = new Size(1420, 940);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Resources", "GoLive.ico"));
        Controls.Add(web);
        Shown += async (_, _) => await InitializeMixerAsync();
        FormClosing += (_, _) => { stateTimer.Stop(); meterTimer.Stop(); spectrumTimer.Stop(); deviceTimer.Stop(); StopCapture(); StopRecording(); _ = obs.DisconnectAsync(); remote?.Close(); };
        stateTimer.Tick += async (_, _) => await PublishMixerStateAsync();
        deviceTimer.Tick += async (_, _) => await PublishAudioDevicesAsync();
        meterTimer.Tick += async (_, _) => await PublishMetersAsync();
        spectrumTimer.Tick += async (_, _) => await PublishSpectrumAsync();
        obs.StatusChanged += (text, connected) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _ = SetObsStatusAsync(text, connected)); };
        obs.InputsReceived += names => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _ = PublishObsInputsAsync(names)); };
    }

    private async Task InitializeMixerAsync()
    {
        var inputs = GetInputs();
        var persisted = inputs.FirstOrDefault(x => x.ID == selectedUid) ?? inputs.FirstOrDefault();
        selectedDevice = persisted;
        if (selectedDevice is not null) selectedUid = selectedDevice.ID;
        var outputs = GetOutputs();
        var savedOutputUid = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "AudioOutputUid", "") as string ?? "";
        selectedOutput = outputs.FirstOrDefault(x => x.ID == savedOutputUid);
        var config = new
        {
            code = pairingCode, ip = LanAddress(), url = $"http://{LanAddress()}:{PairPort}/",
            obsInputs = Array.Empty<string>(), obsSource1, obsSource2,
            obsSources = Enumerable.Range(0,24).Select(i => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "ObsSource" + (i + 1), "") as string ?? "").ToArray(),
            audioInputs = inputs.Select(x => x.FriendlyName).ToArray(), audioInputIndex = selectedDevice is null ? -1 : inputs.IndexOf(selectedDevice),
            audioChannelCount = selectedDevice is null ? 0 : selectedDevice.AudioClient.MixFormat.Channels,
            audioOutputs = outputs.Select(x => x.FriendlyName).ToArray(), audioOutputIndex = selectedOutput is null ? -1 : outputs.IndexOf(selectedOutput)
        };
        var configJson = JsonSerializer.Serialize(config);
        var bridge = $"window.__GO_LIVE_CONFIG__={configJson};window.webkit={{messageHandlers:new Proxy({{}},{{get:(_,name)=>({{postMessage:body=>window.chrome.webview.postMessage({{handler:String(name),body}})}})}})}};";
        await web.EnsureCoreWebView2Async();
        await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(bridge);
        web.CoreWebView2.WebMessageReceived += OnWebMessage;
        var page = Path.Combine(AppContext.BaseDirectory, "Resources", "golive-edit-mixer.html");
        web.Source = new Uri(page);
        StartRemoteServer();
        if (selectedDevice is not null) StartCapture(selectedDevice);
        stateTimer.Start(); meterTimer.Start(); spectrumTimer.Start(); deviceTimer.Start();
    }

    private List<MMDevice> GetInputs() => enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
    private List<MMDevice> GetOutputs() => enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var handler = root.GetProperty("handler").GetString() ?? "";
            var body = root.GetProperty("body");
            switch (handler)
            {
                case "goLiveAudioDevice":
                    var index = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("index", out var inputIndexValue) ? inputIndexValue.GetInt32() : body.GetInt32(); var inputs = GetInputs();
                    if (index >= 0 && index < inputs.Count) { selectedDevice = inputs[index]; selectedUid = selectedDevice.ID; Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "AudioDeviceUid", selectedUid); StartCapture(selectedDevice); await PublishAudioDevicesAsync(); }
                    else if (index == -1) { selectedDevice = null; StopCapture(); await PublishAudioDevicesAsync(); }
                    break;
                case "goLiveRecordingControl":
                    if (body.GetProperty("action").GetString() == "stop") StopRecording(); else BeginRecording();
                    break;
                case "goLiveConnectOBS":
                    if (body.TryGetProperty("disconnect", out var disconnect) && disconnect.GetBoolean()) await obs.DisconnectAsync();
                    else await obs.ConnectAsync(body.GetProperty("host").GetString() ?? "127.0.0.1", body.GetProperty("port").GetInt32(), body.GetProperty("password").GetString() ?? "");
                    break;
                case "goLiveAssignOBS":
                    var channel = body.GetProperty("channel").GetInt32(); var source = body.GetProperty("source").GetString() ?? "";
                    if (channel == 1) { obsSource1 = source; Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "ObsSource1", source); }
                    if (channel == 2) { obsSource2 = source; Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "ObsSource2", source); }
                    if (channel is >= 1 and <= 24) Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "ObsSource" + channel, source);
                    obs.Assign(channel, source);
                    break;
                case "goLiveAudioOutput":
                    var outputIndex = body.GetInt32();
                    var outputs = GetOutputs();
                    selectedOutput = outputIndex >= 0 && outputIndex < outputs.Count ? outputs[outputIndex] : null;
                    Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "AudioOutputUid", selectedOutput?.ID ?? "");
                    StartMonitorOutput();
                    await PublishAudioDevicesAsync();
                    break;
                case "goLiveRefreshAudioDevices":
                    await PublishAudioDevicesAsync();
                    break;
                case "goLivePersistScene":
                    Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "LastScene", body.GetString() ?? "{}");
                    break;
                case "goLiveSaveScene":
                    using (var dialog = new SaveFileDialog { Filter = "GoLiv mixer scene|*.json", FileName = "GoLiv-Mixer-Scene.json" })
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    break;
                case "goLiveLoadScene":
                    using (var dialog = new OpenFileDialog { Filter = "GoLiv mixer scene|*.json|JSON files|*.json" })
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            using var sceneDoc = JsonDocument.Parse(await File.ReadAllTextAsync(dialog.FileName));
                            var sceneJson = JsonSerializer.Serialize(sceneDoc.RootElement);
                            var selected = sceneDoc.RootElement.TryGetProperty("selectedChannel", out var selectedProp) && selectedProp.TryGetInt32(out var selectedValue) ? selectedValue : 0;
                            await web.CoreWebView2.ExecuteScriptAsync($"window.__goLiveStageScene && window.__goLiveStageScene({sceneJson},{JsonSerializer.Serialize(Path.GetFileName(dialog.FileName))},{selected})");
                        }
                    }
                    break;
                case "goLiveOutputControl":
                    var action = body.GetProperty("action").GetString();
                    await obs.RequestAsync(action == "startStream" ? "StartStream" : action == "stopStream" ? "StopStream" : action == "startRecord" ? "StartRecord" : "StopRecord", new { });
                    break;
                case "goLiveRotatePairing":
                    RotatePairingCode(); break;
            }
        }
        catch (Exception ex) { await SetObsStatusAsync("Windows host error: " + ex.Message, false); }
    }

    private void StartCapture(MMDevice device)
    {
        StopCapture();
        try
        {
            captureHasData = false; captureHasSignal = false; captureError = "";
            Interlocked.Exchange(ref captureBufferCount, 0); Interlocked.Exchange(ref captureDroppedPackets, 0);
            captureQueue = Channel.CreateBounded<CapturePacket>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
            captureWorkerCancellation = new CancellationTokenSource();
            var queue = captureQueue;
            var cancellation = captureWorkerCancellation;
            captureWorker = Task.Run(async () =>
            {
                try
                {
                    await foreach (var packet in queue.Reader.ReadAllAsync(cancellation.Token))
                        CaptureAudio(packet.Buffer, packet.Length, packet.Format);
                }
                catch (OperationCanceledException) { }
            });
            capture = new WasapiCapture(device, true, 60);
            sampleRate = capture.WaveFormat.SampleRate; channelCount = capture.WaveFormat.Channels;
            var activeCapture = capture;
            activeCapture.DataAvailable += (_, e) =>
            {
                var length = e.BytesRecorded;
                if (length <= 0) return;
                var copy = new byte[length];
                Buffer.BlockCopy(e.Buffer, 0, copy, 0, length);
                Interlocked.Increment(ref captureBufferCount);
                if (!queue.Writer.TryWrite(new CapturePacket(copy, length, activeCapture.WaveFormat))) Interlocked.Increment(ref captureDroppedPackets);
            };
            activeCapture.RecordingStopped += (_, e) => { if (e.Exception is not null) { captureError = "Capture error: " + e.Exception.Message; if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _ = PublishAudioDevicesAsync()); } };
            capture.StartRecording();
            if (selectedOutput is not null) StartMonitorOutput();
        }
        catch (Exception ex) { captureError = "Input error: " + ex.Message; _ = PublishAudioDevicesAsync(); }
    }

    private void StartMonitorOutput()
    {
        try { monitorOut?.Stop(); monitorOut?.Dispose(); } catch { }
        monitorOut = null; monitorBuffer = null; monitorNotice = "";
        if (selectedOutput is null) return;
        if (IsBuiltInMic(selectedDevice?.FriendlyName) && IsSpeakerOutput(selectedOutput.FriendlyName))
        {
            monitorNotice = "Speaker monitoring disabled to prevent built-in microphone feedback. Select headphones to monitor.";
            return;
        }
        try
        {
            monitorSampleRate = selectedOutput.AudioClient.MixFormat.SampleRate;
            monitorResampler.Reset();
            monitorBuffer = new BufferedWaveProvider(new WaveFormat(monitorSampleRate, 24, 2)) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(1500) };
            monitorOut = new WasapiOut(selectedOutput, AudioClientShareMode.Shared, true, 60);
            monitorOut.Init(monitorBuffer); monitorOut.Play();
        }
        catch (Exception ex) { _ = SetRecordingStatusAsync("Monitor output error: " + ex.Message, false, ""); monitorOut = null; monitorBuffer = null; }
    }

    private void StopCapture()
    {
        try { capture?.StopRecording(); } catch { }
        capture?.Dispose(); capture = null;
        var queue = captureQueue; captureQueue = null; queue?.Writer.TryComplete();
        var worker = captureWorker; captureWorker = null;
        var cancellation = captureWorkerCancellation; captureWorkerCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            if (worker is not null) _ = worker.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
            else cancellation.Dispose();
        }
        try { monitorOut?.Stop(); monitorOut?.Dispose(); } catch { }
        monitorOut = null; monitorBuffer = null;
        lock (audioLock) { levels = Enumerable.Repeat(-90d,24).ToArray(); clips = new bool[24]; }
    }

    private Task PublishAudioDevicesAsync()
    {
        if (web.CoreWebView2 is null) return Task.CompletedTask;
        var inputs = GetInputs();
        var index = selectedDevice is null ? -1 : inputs.FindIndex(x => x.ID == selectedDevice.ID);
        var channels = capture is not null ? capture.WaveFormat.Channels : selectedDevice is null ? 0 : selectedDevice.AudioClient.MixFormat.Channels;
        var outputs = GetOutputs(); var outputIndex = selectedOutput is null ? -1 : outputs.FindIndex(x => x.ID == selectedOutput.ID);
        var json = JsonSerializer.Serialize(new { names = inputs.Select(x => x.FriendlyName).ToArray(), index, channels, outputs = outputs.Select(x => x.FriendlyName).ToArray(), outputIndex, status = selectedDevice is null ? "No audio input selected" : !string.IsNullOrEmpty(captureError) ? captureError : !string.IsNullOrEmpty(monitorNotice) ? monitorNotice : !captureHasData ? "No capture packets · check device or Windows audio driver" : captureHasSignal ? $"Audio signal detected · {channels} hardware channels" : $"Capture active · no signal · {channels} channels" });
        return web.CoreWebView2.ExecuteScriptAsync($"window.__goLiveAudioInputs && window.__goLiveAudioInputs({json})");
    }

    private void CaptureAudio(byte[] bytes, int length, WaveFormat? format)
    {
        if (format is null || length < format.BlockAlign) return;
        if (!captureHasData) { captureHasData = true; if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _ = PublishAudioDevicesAsync()); }
        var frames = length / format.BlockAlign; var channels = format.Channels;
        var samples = new float[frames * channels]; var meterChannels = Math.Min(24, channels); var peak = new double[meterChannels]; var sum = new double[meterChannels];
        for (var frame = 0; frame < frames; frame++) for (var c = 0; c < channels; c++)
        {
            var offset = frame * format.BlockAlign + c * (format.BitsPerSample / 8);
            var isFloat32 = format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")));
            float v = isFloat32 ? BitConverter.ToSingle(bytes, offset) : format.BitsPerSample switch
            {
                16 => BitConverter.ToInt16(bytes, offset) / 32768f,
                24 => Read24(bytes, offset) / 8388608f,
                32 => BitConverter.ToInt32(bytes, offset) / 2147483648f,
                _ => 0
            };
            samples[frame * channels + c] = v;
            if (c < peak.Length) { peak[c] = Math.Max(peak[c], Math.Abs(v)); sum[c] += v * v; }
        }
        if (peak.Any(x => x > 0.0001)) captureHasSignal = true;
        JsonElement state; lock (audioLock) state = mixerState;
        var ch = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("ch", out var cs) ? cs : default;
        var main = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("main", out var ms) ? ms : default;
        localFX.Configure(state);
        var muted = new bool[24]; var anySolo = false;
        if (ch.ValueKind == JsonValueKind.Array) foreach (var c in ch.EnumerateArray()) anySolo |= c.TryGetProperty("solo", out var so) && so.GetBoolean();
        var masterMuted = main.ValueKind == JsonValueKind.Object && main.TryGetProperty("mute", out var mm) && mm.GetBoolean();
        var masterDb = main.ValueKind == JsonValueKind.Object && main.TryGetProperty("db", out var md) ? md.GetDouble() : 0;
        for (var i = 0; i < 24; i++)
        {
            var c = ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > i ? ch[i] : default;
            muted[i] = masterMuted || c.ValueKind != JsonValueKind.Object || (c.TryGetProperty("mute", out var m) && m.GetBoolean()) || (anySolo && (!c.TryGetProperty("solo", out var s) || !s.GetBoolean()));
        }
        for (var i = 0; i < 24; i++)
            if (ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > i && ch[i].TryGetProperty("dsp", out var dsp)) localDSP[i].Configure(dsp);
        lock (audioLock)
        {
            levels = Enumerable.Repeat(-90d,24).ToArray();
            for (var frame = 0; frame < frames; frame++)
            {
                for (var c = 0; c < meterChannels; c++) spectrumRing[c][spectrumIndex] = samples[frame * channels + c];
                spectrumIndex = (spectrumIndex + 1) % 2048; spectrumFilled = Math.Min(2048, spectrumFilled + 1);
            }
            for (var c = 0; c < peak.Length; c++)
            {
                levels[c] = Math.Max(-90, Math.Max(20 * Math.Log10(Math.Max(1e-5, Math.Sqrt(sum[c] / frames))), 20 * Math.Log10(Math.Max(1e-5, peak[c])) - 6));
                if (peak[c] >= .999) clipUntil[c] = DateTime.UtcNow.AddSeconds(1.5);
                clips[c] = DateTime.UtcNow < clipUntil[c];
            }
        }
        if (writer is null && monitorBuffer is null) return;
        var route = new int[24]; var leftGain = new float[24]; var rightGain = new float[24]; var active = new bool[24];
        for (var i = 0; i < 24; i++)
        {
            var c = ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > i ? ch[i] : default;
            route[i] = InputIndex(c, i);
            active[i] = !muted[i] && route[i] >= 0 && route[i] < channels;
            var gain = ChannelGain(ch, i, masterDb);
            var pan = c.ValueKind == JsonValueKind.Object && c.TryGetProperty("pan", out var pv) && pv.TryGetDouble(out var p) ? Math.Clamp(p, -1, 1) : 0;
            leftGain[i] = (float)(gain * Math.Sqrt((1-pan)*.5));
            rightGain[i] = (float)(gain * Math.Sqrt((1+pan)*.5));
        }
        var output = new byte[frames * 6]; var pos = 0;
        var gainSmoothing = 1 - Math.Exp(-1 / (0.005 * format.SampleRate));
        for (var frame = 0; frame < frames; frame++)
        {
            double left = 0, right = 0;
            for (var i = 0; i < 24; i++)
            {
                var targetLeft = active[i] ? leftGain[i] : 0;
                var targetRight = active[i] ? rightGain[i] : 0;
                smoothedLeftGain[i] += (targetLeft - smoothedLeftGain[i]) * gainSmoothing;
                smoothedRightGain[i] += (targetRight - smoothedRightGain[i]) * gainSmoothing;
                if (!active[i]) continue;
                var input = samples[frame * channels + route[i]];
                var processed = localDSP[i].Process(input, format.SampleRate);
                processed = localFX.ProcessChannel(processed, i, false, format.SampleRate);
                left += processed * smoothedLeftGain[i];
                right += processed * smoothedRightGain[i];
            }
            WritePcm24(output, ref pos, SoftLimit((float)left));
            WritePcm24(output, ref pos, SoftLimit((float)right));
        }
        lock (recordLock) { writer?.Write(output, 0, output.Length); }
        try { var monitorBytes = format.SampleRate == monitorSampleRate ? output : monitorResampler.Process(output, frames, format.SampleRate, monitorSampleRate); monitorBuffer?.AddSamples(monitorBytes, 0, monitorBytes.Length); } catch { }
    }

    private sealed class StreamingPcm24Resampler
    {
        private int inputRate, outputRate;
        private double position;
        private float previousLeft, previousRight;
        private bool initialized;

        public void Reset() { inputRate = outputRate = 0; position = 0; previousLeft = previousRight = 0; initialized = false; }

        public byte[] Process(byte[] input, int inputFrames, int sourceRate, int targetRate)
        {
            if (inputFrames <= 0 || sourceRate <= 0 || targetRate <= 0) return Array.Empty<byte>();
            if (sourceRate == targetRate) { Reset(); return input; }
            if (inputRate != sourceRate || outputRate != targetRate) { Reset(); inputRate = sourceRate; outputRate = targetRate; }
            float Read(int frame, int channel)
            {
                if (frame == 0) return channel == 0 ? previousLeft : previousRight;
                return Read24(input, (frame - 1) * 6 + channel * 3) / 8388608f;
            }
            if (!initialized)
            {
                previousLeft = Read24(input, 0) / 8388608f;
                previousRight = Read24(input, 3) / 8388608f;
                position = 1;
                initialized = true;
            }
            var step = (double)sourceRate / targetRate;
            var capacity = (int)Math.Ceiling(inputFrames * (double)targetRate / sourceRate) + 8;
            var output = new byte[capacity * 6]; var written = 0;
            while (position < inputFrames)
            {
                var frame = (int)position; var fraction = position - frame;
                for (var channel = 0; channel < 2; channel++)
                    WritePcm24(output, ref written, (float)(Read(frame, channel) + (Read(frame + 1, channel) - Read(frame, channel)) * fraction));
                position += step;
            }
            previousLeft = Read24(input, (inputFrames - 1) * 6) / 8388608f;
            previousRight = Read24(input, (inputFrames - 1) * 6 + 3) / 8388608f;
            position -= inputFrames - 1;
            Array.Resize(ref output, written);
            return output;
        }
    }

    private static int InputIndex(JsonElement channel, int fallback)
    {
        if (channel.ValueKind != JsonValueKind.Object) return fallback;
        if (!channel.TryGetProperty("inp", out var value) && !channel.TryGetProperty("input", out value)) return fallback;
        if (value.ValueKind != JsonValueKind.String) return fallback;
        var source = value.GetString() ?? "";
        if (source.Contains("no input", StringComparison.OrdinalIgnoreCase)) return -1;
        var start = source.IndexOf("IN ", StringComparison.OrdinalIgnoreCase);
        var skip = 3;
        if (start < 0) { start = source.IndexOf("Input ", StringComparison.OrdinalIgnoreCase); skip = 6; }
        if (start < 0) { start = source.IndexOf("Line ", StringComparison.OrdinalIgnoreCase); skip = 5; }
        if (start < 0) return fallback;
        var digits = new string(source.Skip(start + skip).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var oneBased) && oneBased > 0 ? oneBased - 1 : fallback;
    }

    private static void WritePcm24(byte[] output, ref int pos, float input)
    {
        var sample = (int)(Math.Clamp(input, -1f, .9999998f) * 8388607f);
        output[pos++] = (byte)sample; output[pos++] = (byte)(sample >> 8); output[pos++] = (byte)(sample >> 16);
    }

    private static double ChannelGain(JsonElement channels, int index, double masterDb)
    {
        var db = 0d;
        if (channels.ValueKind == JsonValueKind.Array && channels.GetArrayLength() > index)
        {
            var channel = channels[index]; if (channel.TryGetProperty("db", out var f)) db += f.GetDouble(); if (channel.TryGetProperty("trim", out var t)) db += t.GetDouble();
        }
        return Math.Pow(10, Math.Clamp(db + masterDb, -60, 26) / 20);
    }

    private static float SoftLimit(float value)
    {
        var magnitude=Math.Abs(value); if(magnitude<=.85f)return value;
        var limited=.85f+.15f*MathF.Tanh((magnitude-.85f)/.15f);
        return value<0?-limited:limited;
    }

    private void BeginRecording()
    {
        if (capture is null) { _ = SetRecordingStatusAsync("Select an active audio input first.", false, ""); return; }
        using var dialog = new SaveFileDialog { Filter = "WAV audio|*.wav", FileName = $"GoLive-Recording-{DateTime.Now:yyyyMMdd-HHmmss}.wav" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var format = new WaveFormat(capture.WaveFormat.SampleRate, 24, 2);
            lock (recordLock) writer = new WaveFileWriter(dialog.FileName, format);
            _ = SetRecordingStatusAsync("Recording Â· " + Path.GetFileName(dialog.FileName), true, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { _ = SetRecordingStatusAsync("Could not start recording: " + ex.Message, false, ""); }
    }

    private void StopRecording()
    {
        lock (recordLock) { writer?.Dispose(); writer = null; }
        _ = SetRecordingStatusAsync("Recording saved", false, "");
    }

    private async Task PublishMixerStateAsync()
    {
        if (web.CoreWebView2 is null) return;
        try
        {
            var result = await web.CoreWebView2.ExecuteScriptAsync("window.__goLiveRemoteState ? window.__goLiveRemoteState() : null");
            if (result != "null")
            {
                var json = JsonSerializer.Deserialize<string>(result);
                if (json is not null)
                {
                    using var document = JsonDocument.Parse(json); var snapshot = document.RootElement.Clone();
                    lock (audioLock) { mixerState = snapshot; remoteState = snapshot.GetRawText(); }
                    await obs.SyncAsync(snapshot);
                }
            }
        }
        catch { }
    }

    private async Task PublishMetersAsync()
    {
        if (web.CoreWebView2 is null) return;
        double[] l; bool[] c; double sr; int ch;
        lock (audioLock) { l = levels.ToArray(); c = clips.ToArray(); sr = sampleRate; ch = channelCount; }
        var info = new { sampleRate = sr, channels = ch, clips = c, inputBufferCount = Interlocked.Read(ref captureBufferCount), queueDrops = Interlocked.Read(ref captureDroppedPackets) };
        var payload = JsonSerializer.Serialize(new { levels = l, info });
        try { await web.CoreWebView2.ExecuteScriptAsync($"window.__goLiveSetAudioLevels({payload})"); } catch { }
    }

    private async Task PublishSpectrumAsync()
    {
        if (web.CoreWebView2 is null || Interlocked.Exchange(ref spectrumBusy, 1) != 0) return;
        try
        {
            double sr; int ch; int route; float[] samples; int filled;
            lock (audioLock)
            {
                sr = sampleRate; ch = channelCount; filled = spectrumFilled;
                if (filled < 2048) return;
                var selected = mixerState.ValueKind == JsonValueKind.Object && mixerState.TryGetProperty("sel", out var selectedProp) && selectedProp.TryGetInt32(out var selectedChannel) ? Math.Clamp(selectedChannel, 0, 23) : 0;
                var channels = mixerState.ValueKind == JsonValueKind.Object && mixerState.TryGetProperty("ch", out var channelArray) ? channelArray : default;
                var selectedConfig = channels.ValueKind == JsonValueKind.Array && channels.GetArrayLength() > selected ? channels[selected] : default;
                route = InputIndex(selectedConfig, selected);
                if (route < 0 || route >= Math.Min(24, ch)) return;
                samples = new float[2048];
                for (var i = 0; i < samples.Length; i++) samples[i] = spectrumRing[route][(spectrumIndex + i) % samples.Length];
            }
            var spectrum = await Task.Run(() => CalculateSpectrum(samples, filled, sr));
            var spectra = Enumerable.Range(0, 24).Select(_ => Enumerable.Repeat(-90d, 48).ToArray()).ToArray();
            spectra[route] = spectrum;
            var info = new { sampleRate = sr, channels = ch, spectrumMaxHz = 20000, inputBufferCount = Interlocked.Read(ref captureBufferCount), queueDrops = Interlocked.Read(ref captureDroppedPackets) };
            var payload = JsonSerializer.Serialize(new { spectra, info });
            if (web.CoreWebView2 is not null) await web.CoreWebView2.ExecuteScriptAsync($"window.__goLiveSetAudioLevels({payload})");
        }
        catch { }
        finally { Interlocked.Exchange(ref spectrumBusy, 0); }
    }

    private static double[] CalculateSpectrum(float[] samples, int filled, double rate)
    {
        var result = Enumerable.Repeat(-90d, 48).ToArray();
        if (filled < 2048 || rate <= 0 || samples.Length < 2048) return result;
        var maxHz = rate / 2.05;
        for (var band = 0; band < result.Length; band++)
        {
            var hz = 20 * Math.Pow(1000, band / 47d);
            if (hz >= maxHz) continue;
            var coeff = 2 * Math.Cos(2 * Math.PI * hz / rate); double s1 = 0, s2 = 0;
            for (var i = 0; i < 2048; i++) { var window = SpectrumWindow[i]; var value = samples[i] * window + coeff * s1 - s2; s2 = s1; s1 = value; }
            var power = Math.Max(0, s1 * s1 + s2 * s2 - coeff * s1 * s2); var magnitude = 4 * Math.Sqrt(power) / 2048;
            result[band] = Math.Max(-90, 20 * Math.Log10(Math.Max(magnitude, 1e-5)));
        }
        return result;
    }
    private Task SetRecordingStatusAsync(string text, bool active, string file) => web.CoreWebView2.ExecuteScriptAsync($"window.__goLiveRecordingStatus({JsonSerializer.Serialize(new { text, active, file })})");
    private Task SetObsStatusAsync(string text, bool connected) => web.CoreWebView2.ExecuteScriptAsync($"window.__goLiveObsStatus({JsonSerializer.Serialize(new { text, connected })})");
    private string[] GetObsSources() => Enumerable.Range(1,24).Select(i => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\GoLiveEdit", "ObsSource" + i, i == 1 ? obsSource1 : i == 2 ? obsSource2 : "") as string ?? "").ToArray();
    private Task PublishObsInputsAsync(string[] inputs) => web.CoreWebView2.ExecuteScriptAsync($"window.__goLiveObsInputs({JsonSerializer.Serialize(new { inputs, sources = GetObsSources() })})");

    private void StartRemoteServer()
    {
        try { remote = new HttpListener(); remote.Prefixes.Add($"http://+:{PairPort}/"); remote.Start(); _ = Task.Run(RemoteLoopAsync); }
        catch (Exception ex) { _ = SetRecordingStatusAsync("Phone remote could not start: " + ex.Message, false, ""); }
    }
    private async Task RemoteLoopAsync()
    {
        while (remote?.IsListening == true)
        {
            HttpListenerContext c; try { c = await remote.GetContextAsync(); } catch { break; }
            _ = Task.Run(() => HandleRemoteAsync(c));
        }
    }
    private async Task HandleRemoteAsync(HttpListenerContext c)
    {
        try
        {
            var path = c.Request.Url?.AbsolutePath ?? "/"; var token = c.Request.Headers["Authorization"]?.Replace("Bearer ", "") ?? "";
            object response; int status = 200;
            if (path == "/" && c.Request.HttpMethod == "GET") { var page = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Resources", "phone-remote.html")); c.Response.ContentType = "text/html; charset=utf-8"; c.Response.ContentLength64 = page.Length; await c.Response.OutputStream.WriteAsync(page); c.Response.Close(); return; }
            if (path == "/api/pair" && c.Request.HttpMethod == "POST")
            {
                using var doc = await JsonDocument.ParseAsync(c.Request.InputStream); var code = doc.RootElement.GetProperty("code").GetString() ?? "";
                if (DateTime.UtcNow < codeExpiry && code.Equals(pairingCode, StringComparison.OrdinalIgnoreCase)) { var t = Guid.NewGuid().ToString("N"); lock (pairedTokens) pairedTokens.Add(t); response = new { ok = true, token = t }; }
                else { status = 401; response = new { ok = false, error = "Pairing code is invalid or expired" }; }
            }
            else if (path == "/api/state" && IsPaired(token))
            {
                string snapshot; lock (audioLock) snapshot = remoteState;
                var snapshotBytes = Encoding.UTF8.GetBytes(snapshot); c.Response.ContentType = "application/json; charset=utf-8"; c.Response.ContentLength64 = snapshotBytes.Length; await c.Response.OutputStream.WriteAsync(snapshotBytes); c.Response.Close(); return;
            }
            else if (path == "/api/control" && c.Request.HttpMethod == "POST" && IsPaired(token))
            {
                using var command = await JsonDocument.ParseAsync(c.Request.InputStream);
                var json = command.RootElement.GetRawText();
                await web.CoreWebView2.ExecuteScriptAsync($"window.__goLiveSetRemoteControl && window.__goLiveSetRemoteControl({json})");
                response = new { ok = true };
            }
            else { status = 401; response = new { ok = false, error = "Pair this phone again" }; }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response); c.Response.StatusCode = status; c.Response.ContentType = "application/json; charset=utf-8"; c.Response.ContentLength64 = bytes.Length; await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
        }
        catch { try { c.Response.StatusCode = 500; c.Response.Close(); } catch { } }
    }
    private bool IsPaired(string token) { lock (pairedTokens) return pairedTokens.Contains(token); }
    private void RotatePairingCode()
    {
        pairingCode = NewPairCode(); codeExpiry = DateTime.UtcNow.AddMinutes(3);
        _ = web.CoreWebView2.ExecuteScriptAsync($"window.__goLivePairingUpdated && window.__goLivePairingUpdated({JsonSerializer.Serialize(new { code = pairingCode, url = $"http://{LanAddress()}:{PairPort}/", ip = LanAddress() })})");
    }
    private static string NewPairCode() => new string(Enumerable.Range(0, 8).Select(_ => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"[Random.Shared.Next(32)]).ToArray());
    private static string LanAddress() => System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(x))?.ToString() ?? "127.0.0.1";
    private static bool IsBuiltInMic(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var value = name.ToLowerInvariant();
        return value.Contains("microphone array") || value.Contains("internal microphone") || value.Contains("built-in microphone");
    }

    private static bool IsSpeakerOutput(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var value = name.ToLowerInvariant();
        return value.Contains("speaker");
    }

    private static int Read24(byte[] bytes, int offset) { var v = bytes[offset] | bytes[offset+1] << 8 | bytes[offset+2] << 16; if ((v & 0x800000) != 0) v |= unchecked((int)0xff000000); return v; }
}

internal sealed class ObsSocket
{
    private ClientWebSocket? socket;
    private readonly SemaphoreSlim sendLock = new(1,1);
    private readonly Dictionary<int,string> assigned = new();
    private readonly Dictionary<string,string> lastValues = new();
    private readonly Dictionary<string,string> pendingFilters = new();
    private readonly HashSet<string> requestedFilters = new();
    private readonly Dictionary<string,HashSet<string>> knownFilters = new();
    private readonly Dictionary<string,string> lastFilterValues = new();
    private string host = "127.0.0.1"; private int port = 4455; private bool connected;
    public event Action<string,bool>? StatusChanged;
    public event Action<string[]>? InputsReceived;
    public async Task ConnectAsync(string h,int p,string password)
    {
        await DisconnectAsync(); host=h;port=p;StatusChanged?.Invoke("Connecting to OBSâ€¦",false); socket=new ClientWebSocket(); await socket.ConnectAsync(new Uri($"ws://{host}:{port}"),CancellationToken.None);
        var buffer=new byte[65536]; var r=await socket.ReceiveAsync(buffer,CancellationToken.None); using var hello=JsonDocument.Parse(buffer.AsMemory(0,r.Count));
        var d=hello.RootElement.GetProperty("d"); var identify=new Dictionary<string,object>{{"rpcVersion",1}};
        if(d.TryGetProperty("authentication",out var auth)){var salt=auth.GetProperty("salt").GetString()!;var challenge=auth.GetProperty("challenge").GetString()!;var secret=Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password+salt)));identify["authentication"]=Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret+challenge)));}
        await SendAsync(new {op=1,d=identify}); r=await socket.ReceiveAsync(buffer,CancellationToken.None); using var identified=JsonDocument.Parse(buffer.AsMemory(0,r.Count)); connected=identified.RootElement.GetProperty("op").GetInt32()==2;
        if (!connected) { StatusChanged?.Invoke("OBS rejected the password or connection.",false); await DisconnectAsync(); return; }
        lastValues.Clear(); lastFilterValues.Clear(); requestedFilters.Clear(); knownFilters.Clear(); pendingFilters.Clear();
        StatusChanged?.Invoke("Connected to OBS",true); _=Task.Run(ReceiveLoopAsync); await RequestAsync("GetInputList",new { });
    }
    public async Task DisconnectAsync(){connected=false;if(socket is not null){try{socket.Abort();socket.Dispose();}catch{}}socket=null;lastValues.Clear();lastFilterValues.Clear();requestedFilters.Clear();knownFilters.Clear();pendingFilters.Clear();StatusChanged?.Invoke("Not connected",false);await Task.CompletedTask;}
    public void Assign(int channel,string source){assigned[channel]=source;if(connected&&!string.IsNullOrWhiteSpace(source))_ = RequestFiltersAsync(source);}
    public async Task SyncAsync(JsonElement state)
    {
        if(!connected)return;
        if(state.TryGetProperty("ch",out var channels)&&state.TryGetProperty("main",out var main))
        {
            var muted=main.GetProperty("mute").GetBoolean();var master=main.GetProperty("db").GetDouble();var solo=channels.EnumerateArray().Any(x=>x.GetProperty("solo").GetBoolean());var n=0;
            foreach(var c in channels.EnumerateArray())
            {
                n++; if(!assigned.TryGetValue(n,out var name)||name.Length==0)continue;
                var db=c.GetProperty("db").GetDouble()+master+(c.TryGetProperty("trim",out var tr)?tr.GetDouble():0);
                var m=muted||c.GetProperty("mute").GetBoolean()||(solo&&!c.GetProperty("solo").GetBoolean());
                var value=$"{Math.Clamp(db,-100,26):F2}|{m}";
                if(lastValues.GetValueOrDefault(name)!=value)
                {
                    lastValues[name]=value;
                    await RequestAsync("SetInputVolume",new {inputName=name,inputVolumeDb=Math.Clamp(db,-100,26)});
                    await RequestAsync("SetInputMute",new {inputName=name,inputMuted=m});
                }
                await SyncSourceFiltersAsync(name,c,state.TryGetProperty("fx",out var effects)?effects:default,n-1);
            }
        }
    }
    public Task RequestAsync(string type,object data)=>RequestAsync(type,data,Guid.NewGuid().ToString("N"));
    private Task RequestAsync(string type,object data,string id)=>SendAsync(new {op=6,d=new {requestType=type,requestId=id,requestData=data}});
    private async Task SendAsync(object value){if(socket is null)return;var bytes=JsonSerializer.SerializeToUtf8Bytes(value);await sendLock.WaitAsync();try{await socket.SendAsync(bytes,WebSocketMessageType.Text,true,CancellationToken.None);}finally{sendLock.Release();}}
    private async Task ReceiveLoopAsync()
    {
        var b=new byte[65536];
        while(socket?.State==WebSocketState.Open)
        {
            try
            {
                var r=await socket.ReceiveAsync(b,CancellationToken.None); if(r.MessageType==WebSocketMessageType.Close)break;
                using var d=JsonDocument.Parse(b.AsMemory(0,r.Count)); var root=d.RootElement;
                if(root.GetProperty("op").GetInt32()==7)
                {
                    var e=root.GetProperty("d"); var request=e.GetProperty("requestType").GetString();
                    if(e.TryGetProperty("requestStatus",out var rs)&&rs.TryGetProperty("result",out var ok)&&!ok.GetBoolean()) StatusChanged?.Invoke("OBS error: "+(rs.TryGetProperty("comment",out var comment)?comment.GetString():"request failed"),true);
                    if(request=="GetInputList"&&e.TryGetProperty("responseData",out var data))
                    {
                        var names=data.GetProperty("inputs").EnumerateArray().Select(x=>x.GetProperty("inputName").GetString()??"").Where(x=>x.Length>0).OrderBy(x=>x).ToArray();
                        InputsReceived?.Invoke(names);
                        foreach(var source in assigned.Values.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct()) _=RequestFiltersAsync(source);
                    }
                    if(request=="GetSourceFilterList"&&pendingFilters.Remove(e.GetProperty("requestId").GetString()??"",out var sourceName)&&e.TryGetProperty("responseData",out var filterData))
                    {
                        knownFilters[sourceName]=filterData.GetProperty("filters").EnumerateArray().Select(x=>x.TryGetProperty("filterName",out var fn)?fn.GetString()??"":"").Where(x=>x.Length>0).ToHashSet();
                        lastFilterValues.Remove(sourceName);
                    }
                }
            }
            catch(Exception ex){connected=false;StatusChanged?.Invoke("OBS connection failed: "+ex.Message,false);break;}
        }
        if(connected){connected=false;StatusChanged?.Invoke("OBS disconnected",false);}
    }

    private async Task RequestFiltersAsync(string source)
    {
        if(!connected||string.IsNullOrWhiteSpace(source)||knownFilters.ContainsKey(source)||!requestedFilters.Add(source))return;
        var id="golive-filters-"+Guid.NewGuid().ToString("N");pendingFilters[id]=source;
        try{await RequestAsync("GetSourceFilterList",new{sourceName=source},id);}catch{requestedFilters.Remove(source);pendingFilters.Remove(id);}
    }

    private async Task SyncSourceFiltersAsync(string source,JsonElement channel,JsonElement effects,int channelIndex)
    {
        if(!knownFilters.TryGetValue(source,out var existing)){await RequestFiltersAsync(source);return;}
        if(!channel.TryGetProperty("dsp",out var dsp))return;
        var bypass=Bool(dsp,"bypass");var gateOn=Bool(dsp,"gOn")&&!bypass;var eqOn=Bool(dsp,"eOn")&&!bypass;var compOn=Bool(dsp,"cOn")&&!bypass;
        var g=ReadArray(dsp,"g",[-40,-47,12,300,300]);var c=ReadArray(dsp,"c",[-20,3,8,120,1]);
        var compressor=new {threshold=AtIndex(c,0,-20),ratio=AtIndex(c,1,3),attack_time=(int)AtIndex(c,2,8),release_time=(int)AtIndex(c,3,120),output_gain=AtIndex(c,4,1),sidechain_source="none"};
        var fxList=effects.ValueKind==JsonValueKind.Array?effects.EnumerateArray().ToArray():[];
        var effectActive=fxList.Any(x=>Bool(x,"on")&&AtBool(x,"asg",channelIndex)&&At(x,"snd",channelIndex,-60)>-59.9&&Num(x,"ret",-60)>-59.9);
        var fxChunk=MakeChunk(fxList,channelIndex,g,gateOn,ArrayElements(dsp,"e"),eqOn);
        var vstEnabled=gateOn||eqOn||effectActive;
        var vst=new {plugin_path=Path.Combine(AppContext.BaseDirectory,"Resources","GoLiveFX.dll"),chunk_data=Convert.ToBase64String(fxChunk),chunk_hash=""};
        var signature=JsonSerializer.Serialize(new{compressor,compOn,vst,vstEnabled,bypass});
        if(lastFilterValues.GetValueOrDefault(source)==signature)return;lastFilterValues[source]=signature;
        foreach(var old in new[]{"GoLive Input Trim","GoLive VST FX","GoLive EQ","GoLive Noise Gate","GoLive Gate","GoLive 3-Band EQ","GoLive Main Limiter","GoLive Parametric EQ","GoLive 6-Band Parametric EQ"})
            if(existing.Contains(old)){await RequestAsync("SetSourceFilterEnabled",new{sourceName=source,filterName=old,filterEnabled=false});}
        await UpsertFilter(source,"GoLive Compressor","compressor_filter",compressor,compOn,existing);
        if(vstEnabled||existing.Contains("GoLive FX"))await UpsertFilter(source,"GoLive FX","vst_filter",vst,vstEnabled&&!bypass,existing);
    }

    private async Task UpsertFilter(string source,string name,string kind,object settings,bool enabled,HashSet<string> existing)
    {
        if(!existing.Contains(name))
        {
            await RequestAsync("CreateSourceFilter",new{sourceName=source,filterName=name,filterKind=kind,filterSettings=settings});
            existing.Add(name);
        }
        await RequestAsync("SetSourceFilterSettings",new{sourceName=source,filterName=name,filterSettings=settings,overlay=true});
        await RequestAsync("SetSourceFilterEnabled",new{sourceName=source,filterName=name,filterEnabled=enabled});
    }

    private static byte[] MakeChunk(JsonElement[] fx,int channel,double[] gate,bool gateOn,JsonElement[] eq,bool eqOn)
    {
        var values=new float[72];var types=new[]{"","Hall Reverb","Room Reverb","Chamber Reverb","Plate Reverb","Stereo Delay","Stereo Chorus"};
        var ranges=new Dictionary<string,(double,double)[]>{
            ["Hall Reverb"]=[(0,200),(.2,5),(2,100),(1000,20000),(0,100),(20,400)],
            ["Room Reverb"]=[(0,200),(.2,5),(2,100),(1000,20000),(0,100),(20,400)],
            ["Chamber Reverb"]=[(0,200),(.2,5),(2,100),(1000,20000),(0,100),(20,400)],
            ["Plate Reverb"]=[(0,200),(.2,5),(2,100),(1000,20000),(-15,15),(20,400)],
            ["Stereo Delay"]=[(10,1000),(0,90),(20,400),(1000,20000)],
            ["Stereo Chorus"]=[(.05,10),(0,100),(1,40),(0,100)]};
        for(var slot=0;slot<4&&slot<fx.Length;slot++)
        {
            var e=fx[slot];var b=slot*10;var name=Str(e,"t","");var type=System.Array.IndexOf(types,name);var assigned=AtBool(e,"asg",channel);var send=assigned?At(e,"snd",channel,-60):-60;var ret=Num(e,"ret",-60);var p=ReadArray(e,"p",[]);
            values[b]=type<0?0:type/6f;values[b+1]=Bool(e,"on")&&assigned&&send>-59.9&&ret>-59.9?1:0;values[b+2]=Norm(send,-60,6);values[b+3]=Norm(ret,-60,6);
            if(ranges.TryGetValue(name,out var rs))for(var j=0;j<Math.Min(6,rs.Length);j++)values[b+4+j]=Norm(j<p.Length?p[j]:rs[j].Item1,rs[j].Item1,rs[j].Item2);
        }
        values[40]=gateOn?1:0;values[41]=Norm(gate.ElementAtOrDefault(0),-80,0);values[42]=Norm(gate.ElementAtOrDefault(1),-80,0);values[43]=Norm(gate.ElementAtOrDefault(2),1,200);values[44]=Norm(gate.ElementAtOrDefault(3),0,2000);values[45]=Norm(gate.ElementAtOrDefault(4),5,2000);values[46]=eqOn?1:0;
        var eqTypes=new Dictionary<string,int>{{"lc",0},{"lcut",0},{"ls",1},{"pk",2},{"vq",3},{"hs",4},{"hc",5}};
        for(var band=0;band<5;band++)
        {
            var b=47+band*5;var e=band<eq.Length?eq[band]:default;var kind=Str(e,"t","pk");var freq=Math.Clamp(Num(e,"f",1000),20,20000);var gain=Math.Clamp(Num(e,"g",Num(e,"gain",0)),-18,18);var q=Math.Clamp(Num(e,"q",.71),.3,8);var on=eqOn&&Bool(e,"on");
            values[b]=on?1:0;values[b+1]=(eqTypes.GetValueOrDefault(kind,2))/5f;values[b+2]=(float)(Math.Log(freq/20)/Math.Log(1000));values[b+3]=Norm(gain,-18,18);values[b+4]=Norm(q,.3,8);
        }
        var bytes=new byte[values.Length*sizeof(float)];Buffer.BlockCopy(values,0,bytes,0,bytes.Length);return bytes;
    }
    private static float Norm(double v,double lo,double hi)=>(float)Math.Clamp((v-lo)/(hi-lo),0,1);
    private static bool Bool(JsonElement e,string k)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.True;
    private static string Str(JsonElement e,string k,string d)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??d:d;
    private static double Num(JsonElement e,string k,double d)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)&&v.TryGetDouble(out var n)?n:d;
    private static double At(JsonElement e,string k,int i,double d)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Array&&v.GetArrayLength()>i&&v[i].TryGetDouble(out var n)?n:d;
    private static bool AtBool(JsonElement e,string k,int i)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Array&&v.GetArrayLength()>i&&v[i].ValueKind==JsonValueKind.True;
    private static double[] ReadArray(JsonElement e,string k,double[] d)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Array?v.EnumerateArray().Select(x=>x.TryGetDouble(out var n)?n:0).ToArray():d;
    private static double AtIndex(double[] values,int i,double d)=>values.Length>i?values[i]:d;
    private static JsonElement[] ArrayElements(JsonElement e,string k)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Array?v.EnumerateArray().Select(x=>x.Clone()).ToArray():[];
}

