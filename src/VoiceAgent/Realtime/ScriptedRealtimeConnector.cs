using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using VoiceAgent.Audio;

namespace VoiceAgent.Realtime;

/// <summary>
/// A stand-in realtime model for running the bridge locally with no API key and no spend
/// (Realtime:Provider = "scripted"). It speaks the same protocol: acknowledges session.update,
/// runs its own server-side VAD over the appended audio, and answers each caller turn with a
/// short tone and a transcript. It is what tools/simulate_call.py drives.
///
/// It measures the bridge, not a model: reply latency against it is the bridge's overhead plus
/// this stub's end-of-turn window, and says nothing about a real model's inference time.
/// </summary>
public sealed class ScriptedRealtimeConnector : IRealtimeConnector
{
    public Task<IMessageChannel> ConnectAsync(CancellationToken ct) => Task.FromResult<IMessageChannel>(new ScriptedModel());

    private sealed class ScriptedModel : IMessageChannel
    {
        private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>();
        private readonly EnergyVad _vad = new(new VadOptions { FrameSamples = 480, SampleRate = 24000, HangoverFrames = 25 });
        private readonly List<short> _remainder = [];
        private int _turns;
        private bool _open = true;

        public bool IsOpen => _open;

        public Task SendAsync(string json, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "session.update":
                    Emit(new JsonObject { ["type"] = "session.updated" });
                    break;
                case "response.create":
                    Reply();
                    break;
                case "input_audio_buffer.append":
                    Listen(Convert.FromBase64String(root.GetProperty("audio").GetString()!));
                    break;
                case "response.cancel":
                    Emit(new JsonObject { ["type"] = "response.cancelled" });
                    break;
            }
            return Task.CompletedTask;
        }

        private void Listen(byte[] pcmBytes)
        {
            for (int i = 0; i + 1 < pcmBytes.Length; i += 2)
                _remainder.Add((short)(pcmBytes[i] | (pcmBytes[i + 1] << 8)));
            while (_remainder.Count >= 480)
            {
                var frame = _remainder.GetRange(0, 480).ToArray();
                _remainder.RemoveRange(0, 480);
                switch (_vad.Process(frame))
                {
                    case VadEvent.SpeechStarted:
                        Emit(new JsonObject { ["type"] = "input_audio_buffer.speech_started" });
                        break;
                    case VadEvent.SpeechStopped:
                        Emit(new JsonObject { ["type"] = "input_audio_buffer.speech_stopped" });
                        Emit(new JsonObject { ["type"] = "input_audio_buffer.committed" });
                        Emit(new JsonObject
                        {
                            ["type"] = "conversation.item.input_audio_transcription.completed",
                            ["transcript"] = $"(caller turn {_turns + 1})",
                        });
                        Reply();
                        break;
                }
            }
        }

        private void Reply()
        {
            _turns++;
            Emit(new JsonObject { ["type"] = "response.created" });
            // 600 ms of a 440 Hz tone in 100 ms deltas, the way a model streams audio.
            for (int chunk = 0; chunk < 6; chunk++)
            {
                var samples = new short[2400];
                for (int n = 0; n < samples.Length; n++)
                    samples[n] = (short)(6000 * Math.Sin(2 * Math.PI * 440 * (chunk * 2400 + n) / 24000.0));
                var bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                Emit(new JsonObject { ["type"] = "response.output_audio.delta", ["delta"] = Convert.ToBase64String(bytes) });
            }
            Emit(new JsonObject { ["type"] = "response.output_audio_transcript.delta", ["delta"] = $"Scripted reply {_turns}." });
            Emit(new JsonObject { ["type"] = "response.done" });
        }

        private void Emit(JsonObject message) => _outbox.Writer.TryWrite(message.ToJsonString());

        public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            while (await _outbox.Reader.WaitToReadAsync(ct))
                while (_outbox.Reader.TryRead(out var message))
                    yield return message;
        }

        public Task CloseAsync(string reason, CancellationToken ct)
        {
            _open = false;
            _outbox.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _open = false;
            _outbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
