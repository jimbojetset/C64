

using System.Collections.Concurrent;
using System.Diagnostics;
using static SDL2.SDL;
using C64.Display;

namespace C64.Sound
{

    internal sealed class Sound : IDisposable
    {

        private const double CpuFreq = 985_248.0;
        private const int SampleRate = 44_100;
        private const double CyclesPerSample = CpuFreq / SampleRate;

        private const int TargetLatencyMs = 25;

        private const int MaxLatencyMs = 50;

        private readonly byte[] _regs = new byte[29];

        private readonly ConcurrentQueue<SidWrite> _writeQueue = new();
        private static readonly double CyclesPerAudioSample = CyclesPerSample;
        private long _writeCycleCursor;
        private long _publishedSynthCycle;
        private double _synthCycleCursor;

        private readonly Voice[] _voices = [new(), new(), new()];

        private const double VoicePreamp = 2.2;

        private const double VoiceLaneGain = 0.85;
        private const double MasterOutputGain = 0.95;
        private const double OutputLowPassCutoff = 15_500.0;

        private static readonly double OutputLowPassA =
            Math.Exp(-2.0 * Math.PI * OutputLowPassCutoff / SampleRate);

        private const double VolumeDacHpA = 0.9995;

        private const double VolumeDacStepResponse = 0.40;
        private const double VolumeDacSmoothing = 0.18;
        private const double VolumeDacQuietLiftPower = 0.62;
        private const double VolumeDacSaturationDrive = 1.6;
        private const double VolumeDacOutputGain = 0.45;
        private const int ResetMuteMs = 40;
        private const int StartupPrimeMs = 20;
        private const int ResetFadeInMs = 80;

        private double _flp, _fbp;

        private double _filterF = 0.1;
        private double _filterQ = 1.0;
        private int _lastFcReg = -1;
        private int _lastResReg = -1;

        private readonly double _filterCapacitorLeakage = 0.9999;

        private double _resonancePeakDamping = 1.0;
        private double _volDacRaw;
        private double _volDacHp;
        private double _volDacShaped;
        private double _outputLowPass;
        private long _lastDacTick;
        private int _muteSamplesRemaining;
        private int _fadeInSamplesRemaining;
        private int _fadeInSamplesTotal;
        private volatile bool muted;

        private uint _dev;

        private short[] _buf = [];

        private byte _v3WaveSnapshot;

        private byte _v3EnvSnapshot;

        private Thread? _thread;

        private CancellationToken _ct;
        private readonly object _audioStateLock = new();

        private static readonly int[] AttackCycles =
        [
                8,    31,    63,    92,   146,   216,   262,   308,
              385,   963,  1925,  3079,  3849, 11547, 19245, 30792,
        ];

        private static readonly int[] DecayCycles =
        [
               24,    93,   188,   276,   437,   647,   786,   924,
             1155,  2887,  5773,  9238, 11547, 34641, 57736, 92369,
        ];

        /// <summary>Returns the ADSR exponential-rate scale for an envelope level.</summary>
        /// <param name="level">The 4-bit SID envelope level to scale.</param>
        private static int ExpScale(int level)
        {
            if (level > 93) return 1;
            if (level > 54) return 2;
            if (level > 26) return 4;
            if (level > 14) return 8;
            if (level > 6) return 16;
            if (level > 2) return 30;
            return 64;
        }

        /// <summary>
        /// Enumerate available SDL playback (output) audio devices.  Returns
        /// an empty list if SDL has no devices or audio failed to initialise.
        /// Initialises the SDL audio subsystem as a side-effect.
        /// </summary>
        public static List<string> EnumerateDevices()
        {
            var names = new List<string>();

            if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0)
            {
                Console.Error.WriteLine($"SDL audio init failed: {SDL_GetError()}");
                return names;
            }

            int count = SDL_GetNumAudioDevices(0);
            for (int i = 0; i < count; i++)
            {
                string? name;
                try { name = SDL_GetAudioDeviceName(i, 0); } catch { name = null; }
                names.Add(name ?? $"Device {i}");
            }
            return names;
        }

        /// <summary>Gets the first available SDL playback device name.</summary>
        public static string? GetDefaultDeviceName()
        {
            List<string> devices = EnumerateDevices();
            return devices.Count > 0 ? devices[0] : null;
        }

        /// <summary>Enumerates SDL playback devices and opens the modal output-device selector.</summary>
        public static string? PromptForDevice()
        {
            List<string> devices = EnumerateDevices();
            return SoundDeviceWindow.Prompt(devices);
        }

        /// <summary>Initializes SDL audio, opens the selected mono playback device, and resets SID timing.</summary>
        /// <param name="deviceName">The preferred SDL audio device name, or null for the default device.</param>
        public void Init(string? deviceName = null)
        {
            if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0)
                throw new Exception($"SDL audio init failed: {SDL_GetError()}");

            lock (_audioStateLock)
            {
                _dev = OpenDevice(deviceName);
                ResetAudioCursors();
            }
        }

        /// <summary>Opens an SDL queue-audio playback device for SID output.</summary>
        /// <param name="deviceName">The preferred SDL audio device name, or null for the default device.</param>
        private static uint OpenDevice(string? deviceName)
        {
            var desired = new SDL_AudioSpec
            {
                freq = SampleRate,
                format = AUDIO_S16SYS,
                channels = 1,
                samples = 512,

            };

            uint device = SDL_OpenAudioDevice(deviceName is null ? null! : deviceName, 0, ref desired, out _, 0);
            if (device == 0)
                throw new Exception($"SDL_OpenAudioDevice failed: {SDL_GetError()}");

            return device;
        }

        /// <summary>Resets register-write and synthesis cursors and schedules the short reset mute period.</summary>
        private void ResetAudioCursors()
        {
            _writeCycleCursor = 0;
            _publishedSynthCycle = 0;
            _synthCycleCursor = 0.0;
            _lastDacTick = 0;
            _muteSamplesRemaining = (SampleRate * ResetMuteMs) / 1000;
        }

        /// <summary>Atomically replaces the SDL playback device, clears stale writes, and primes the new queue.</summary>
        /// <param name="deviceName">The preferred SDL audio device name, or null for the default device.</param>
        /// <param name="paused">Whether emulation or audio output should be paused.</param>
        public void SwitchDevice(string? deviceName, bool paused)
        {
            if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0)
                throw new Exception($"SDL audio init failed: {SDL_GetError()}");

            lock (_audioStateLock)
            {
                uint oldDevice = _dev;
                uint newDevice = OpenDevice(deviceName);

                _dev = newDevice;
                if (oldDevice != 0)
                {
                    SDL_PauseAudioDevice(oldDevice, 1);
                    SDL_ClearQueuedAudio(oldDevice);
                    SDL_CloseAudioDevice(oldDevice);
                }

                while (_writeQueue.TryDequeue(out _)) { }
                ResetAudioCursors();
                PrimeSilence();
                SDL_PauseAudioDevice(_dev, paused ? 1 : 0);
            }
        }

        /// <summary>Primes SDL audio and starts the background SID synthesis thread.</summary>
        /// <param name="token">The cancellation token used to stop the audio loop.</param>
        public void Start(CancellationToken token)
        {
            _ct = token;

            lock (_audioStateLock)
            {
                PrimeSilence();
            }

            SDL_PauseAudioDevice(_dev, 0);

            _thread = new Thread(SynthesisLoop)
            {
                IsBackground = true,
                Name = "SID synth",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        /// <summary>Primes the SDL queue with a short silent buffer to avoid startup underrun noise.</summary>
        private void PrimeSilence()
        {
            int primeSamples = (SampleRate * StartupPrimeMs) / 1000;
            if (primeSamples > 0)
            {
                if (_buf.Length < primeSamples)
                    _buf = new short[primeSamples + 256];
                Array.Clear(_buf, 0, primeSamples);
                unsafe
                {
                    fixed (short* p = _buf)
                        SDL_QueueAudio(_dev, (IntPtr)p, (uint)(primeSamples * sizeof(short)));
                }
            }
        }

        /// <summary>Queues a cycle-ordered SID register write for $D400-$D41C.</summary>
        /// <param name="reg">The SID register index.</param>
        /// <param name="value">The value supplied to the operation.</param>
        public void WriteRegister(int reg, byte value)
        {
            if ((uint)reg < 29)
            {
                long cursor = Interlocked.Read(ref _writeCycleCursor);
                long audioCycle = Interlocked.Read(ref _publishedSynthCycle);
                long cycle = Math.Max(audioCycle, cursor);
                Interlocked.Exchange(ref _writeCycleCursor, cycle + 1);
                _writeQueue.Enqueue(new SidWrite(reg, value, cycle));
            }
        }

        /// <summary>
        /// Read a SID register.  Only $D41B/$D41C return meaningful values
        /// (voice-3 oscillator and envelope read-back).  All write-only
        /// registers read as 0.  Paddle ports $D419/$D41A return $FF.
        /// </summary>
        public byte ReadRegister(int reg) => reg switch
        {
            25 => 0xFF,
            26 => 0xFF,
            27 => _v3WaveSnapshot,
            28 => _v3EnvSnapshot,
            _ => 0,
        };

        public bool Muted
        {
            get => muted;
            set => muted = value;
        }

        /// <summary>Pauses or resumes the SDL playback device and discards queued audio when pausing.</summary>
        /// <param name="paused">Whether emulation or audio output should be paused.</param>
        public void SetPaused(bool paused)
        {
            lock (_audioStateLock)
            {
                if (_dev == 0)
                    return;

                SDL_PauseAudioDevice(_dev, paused ? 1 : 0);
                if (paused)
                    SDL_ClearQueuedAudio(_dev);
            }
        }

        /// <summary>Resets SID registers, voices, filters, DAC state, timing, and startup fade state.</summary>
        public void Reset()
        {
            lock (_audioStateLock)
            {
                bool haveDevice = _dev != 0;
                if (haveDevice)
                {
                    SDL_PauseAudioDevice(_dev, 1);
                    SDL_ClearQueuedAudio(_dev);
                }

                Array.Clear(_regs);
                while (_writeQueue.TryDequeue(out _)) { }
                foreach (var v in _voices) v.Reset();
                _flp = _fbp = 0.0;
                _resonancePeakDamping = 1.0;
                _lastFcReg = -1;
                _lastResReg = -1;
                _volDacRaw = 0.0;
                _volDacHp = 0.0;
                _volDacShaped = 0.0;
                _outputLowPass = 0.0;
                _lastDacTick = 0;
                _muteSamplesRemaining = (SampleRate * ResetMuteMs) / 1000;
                _fadeInSamplesTotal = (SampleRate * ResetFadeInMs) / 1000;
                _fadeInSamplesRemaining = _fadeInSamplesTotal;
                _writeCycleCursor = 0;
                _publishedSynthCycle = 0;
                _synthCycleCursor = 0.0;
                _v3WaveSnapshot = 0;
                _v3EnvSnapshot = 0;

                if (haveDevice)
                {
                    int primeSamples = (SampleRate * StartupPrimeMs) / 1000;
                    if (primeSamples > 0)
                    {
                        if (_buf.Length < primeSamples)
                            _buf = new short[primeSamples + 256];
                        Array.Clear(_buf, 0, primeSamples);
                        unsafe
                        {
                            fixed (short* p = _buf)
                                SDL_QueueAudio(_dev, (IntPtr)p, (uint)(primeSamples * sizeof(short)));
                        }
                    }

                    SDL_PauseAudioDevice(_dev, 0);
                }
            }
        }

        /// <summary>Stops the SID synthesis thread and closes the SDL playback device.</summary>
        public void Dispose()
        {
            try { _thread?.Join(200); } catch { }

            if (_dev != 0)
            {
                SDL_PauseAudioDevice(_dev, 1);
                SDL_CloseAudioDevice(_dev);
                _dev = 0;
            }
        }

        /// <summary>
        /// Runs the background SID synthesis loop, converting wall-clock elapsed time into audio samples and queueing them to SDL.
        /// </summary>
        private void SynthesisLoop()
        {
            long last = Stopwatch.GetTimestamp();
            double fracCyc = 0.0;

            while (!_ct.IsCancellationRequested)
            {
                bool audioPaused;
                lock (_audioStateLock)
                {
                    audioPaused = _dev != 0 && SDL_GetAudioDeviceStatus(_dev) == SDL_AudioStatus.SDL_AUDIO_PAUSED;
                }

                if (audioPaused)
                {
                    last = Stopwatch.GetTimestamp();
                    fracCyc = 0.0;
                    Thread.Sleep(10);
                    continue;
                }

                long now = Stopwatch.GetTimestamp();
                double elapsed = (now - last) / (double)Stopwatch.Frequency;
                last = now;

                double cyc = elapsed * CpuFreq + fracCyc;
                int count = (int)(cyc / CyclesPerSample);
                fracCyc = cyc - count * CyclesPerSample;

                if (count > SampleRate / 10) count = SampleRate / 10;

                if (count > 0)
                {
                    if (_buf.Length < count)
                        _buf = new short[count + 256];

                    lock (_audioStateLock)
                    {
                        Synthesize(_buf, count);

                        unsafe
                        {
                            fixed (short* p = _buf)
                                SDL_QueueAudio(_dev, (IntPtr)p, (uint)(count * sizeof(short)));
                        }
                    }
                }

                uint qBytes;
                lock (_audioStateLock)
                {
                    qBytes = _dev == 0 ? 0 : SDL_GetQueuedAudioSize(_dev);
                }
                int qMs = (int)(qBytes / 2 * 1000 / SampleRate);

                if (qMs >= MaxLatencyMs) Thread.Sleep(10);
                else if (qMs >= TargetLatencyMs) Thread.Sleep(4);
                else Thread.Sleep(2);
            }
        }

        /// <summary>
        /// Synthesizes a batch of SID audio by applying timestamped register writes, stepping voices/envelopes, routing filter paths, and mixing output.
        /// </summary>
        /// <param name="buf">The audio sample buffer to fill.</param>
        /// <param name="count">The number of samples to synthesize.</param>
        private void Synthesize(short[] buf, int count)
        {
            byte[] r = _regs;

            int resReg = (r[23] >> 4) & 0x0F;
            int fcReg = (r[22] << 3) | (r[21] & 0x07);

            if (fcReg != _lastFcReg || resReg != _lastResReg)
            {
                _lastFcReg = fcReg;
                _lastResReg = resReg;
                UpdateFilterCoefficients(fcReg, resReg);
            }

            for (int i = 0; i < count; i++)
            {
                _synthCycleCursor += CyclesPerAudioSample;
                long sampleCycle = (long)_synthCycleCursor;
                Interlocked.Exchange(ref _publishedSynthCycle, sampleCycle);
                ApplyWritesUntil(sampleCycle, r);
                AdvanceDacToTick(sampleCycle);
                _volDacShaped += (_volDacHp - _volDacShaped) * VolumeDacSmoothing;

                byte modeVol = r[24];
                byte masterVol = (byte)(modeVol & 0x0F);
                bool lpOn = (modeVol & 0x10) != 0;
                bool bpOn = (modeVol & 0x20) != 0;
                bool hpOn = (modeVol & 0x40) != 0;
                bool voice3Mute = (modeVol & 0x80) != 0;

                byte resRoute = r[23];
                byte filterRoute = (byte)(resRoute & 0x07);
                resReg = (resRoute >> 4) & 0x0F;
                fcReg = (r[22] << 3) | (r[21] & 0x07);
                if (fcReg != _lastFcReg || resReg != _lastResReg)
                {
                    _lastFcReg = fcReg;
                    _lastResReg = resReg;
                    UpdateFilterCoefficients(fcReg, resReg);
                }

                double v0 = StepVoice(0, r);
                double v1 = StepVoice(1, r);
                double v2 = StepVoice(2, r);

                _v3WaveSnapshot = (byte)(_voices[2].LastWaveform >> 4);
                _v3EnvSnapshot = (byte)_voices[2].EnvelopeLevel;

                double filtered = 0.0, bypass = 0.0;
                if ((filterRoute & 0x01) != 0) filtered += v0; else bypass += v0;
                if ((filterRoute & 0x02) != 0) filtered += v1; else bypass += v1;

                if ((filterRoute & 0x04) != 0)
                {
                    filtered += v2;
                }
                else if (!voice3Mute)
                {
                    bypass += v2;
                }

                double filtOut = StepFilter(filtered, lpOn, bpOn, hpOn);

                double voiceMixed = (filtOut + bypass) * (masterVol / 15.0) * VoicePreamp * VoiceLaneGain;

                double dacAbs = Math.Abs(_volDacShaped);
                double dacLift = Math.Sign(_volDacShaped) * Math.Pow(dacAbs, VolumeDacQuietLiftPower);
                double dacMixed = Math.Tanh(dacLift * VolumeDacSaturationDrive) * VolumeDacOutputGain;
                double mixed = (voiceMixed + dacMixed) * MasterOutputGain;
                mixed = SmoothOutput(SoftLimit(mixed));

                if (_fadeInSamplesRemaining > 0 && _fadeInSamplesTotal > 0)
                {
                    int progressed = _fadeInSamplesTotal - _fadeInSamplesRemaining;
                    double t = (progressed + 1) / (double)_fadeInSamplesTotal;
                    double fade = t * t;
                    mixed *= fade;
                }

                if (muted)
                {
                    buf[i] = 0;
                }
                else if (_muteSamplesRemaining > 0)
                {
                    _muteSamplesRemaining--;
                    buf[i] = 0;
                }
                else
                {
                    if (_fadeInSamplesRemaining > 0)
                        _fadeInSamplesRemaining--;
                    buf[i] = (short)(mixed * 32767.0);
                }
            }
        }

        /// <summary>
        /// Applies queued SID register writes up to a synth tick and triggers write-time gate and volume-DAC side effects.
        /// </summary>
        /// <param name="tick">The target SID clock tick to process up to.</param>
        /// <param name="regs">The SID register snapshot used for synthesis.</param>
        private void ApplyWritesUntil(long tick, byte[] regs)
        {
            while (_writeQueue.TryPeek(out SidWrite w) && w.Tick <= tick)
            {
                if (!_writeQueue.TryDequeue(out SidWrite d))
                    break;

                byte previous = regs[d.Reg];
                regs[d.Reg] = d.Value;

                AdvanceDacToTick(d.Tick);

                HandleGateEdgeOnWrite(d.Reg, previous, d.Value);

                HandleVolumeDacOnWrite(d.Reg, d.Value);
            }
        }

        /// <summary>Advances the volume-DAC decay to a synth tick.</summary>
        /// <param name="tick">The target SID clock tick to process up to.</param>
        private void AdvanceDacToTick(long tick)
        {
            if (_lastDacTick == 0)
            {
                _lastDacTick = tick;
                return;
            }

            if (tick <= _lastDacTick)
                return;

            double dtSamples = (tick - _lastDacTick) / CyclesPerAudioSample;
            if (dtSamples > 0.0)
                _volDacHp *= Math.Pow(VolumeDacHpA, dtSamples);

            _lastDacTick = tick;
        }

        /// <summary>
        /// Applies ADSR phase changes immediately when a SID voice control register write changes the gate bit.
        /// </summary>
        /// <param name="reg">The SID register index.</param>
        /// <param name="previous">The previous value before the update.</param>
        /// <param name="value">The value supplied to the operation.</param>
        private void HandleGateEdgeOnWrite(int reg, byte previous, byte value)
        {
            int voiceBase = (reg / 7) * 7;
            int offset = reg - voiceBase;
            if (offset != 4)
                return;

            int voiceIdx = voiceBase / 7;
            if ((uint)voiceIdx >= (uint)_voices.Length)
                return;

            bool prevGate = (previous & 0x01) != 0;
            bool gate = (value & 0x01) != 0;
            if (prevGate == gate)
                return;

            Voice v = _voices[voiceIdx];
            if (gate)
            {
                v.EnvPhase = EnvPhase.Attack;
                v.EnvTimer = 0.0;
                if (v.EnvelopeLevel == 0)
                    v.EnvelopeLevel = 1;
            }
            else
            {
                v.EnvPhase = EnvPhase.Release;
                v.EnvTimer = 0.0;
            }
        }

        /// <summary>
        /// Captures D418 volume changes for sample-style effects before they are smoothed into the DAC output lane.
        /// </summary>
        /// <param name="reg">The SID register index.</param>
        /// <param name="previous">The previous value before the update.</param>
        /// <param name="value">The value supplied to the operation.</param>
        private void HandleVolumeDacOnWrite(int reg, byte value)
        {
            if (reg != 24)
                return;

            int newVol = value & 0x0F;
            double newRaw = (newVol / 15.0) - 0.5;
            double delta = newRaw - _volDacRaw;
            _volDacRaw = newRaw;
            _volDacHp += delta * VolumeDacStepResponse;
        }

        /// <summary>Applies the final one-pole low-pass stage used to soften synthesized SID output.</summary>
        /// <param name="sample">The audio sample to process.</param>
        private double SmoothOutput(double sample)
        {
            _outputLowPass = sample + OutputLowPassA * (_outputLowPass - sample);
            return _outputLowPass;
        }

        /// <summary>Applies soft clipping to the audio sample.</summary>
        /// <param name="sample">The audio sample to process.</param>
        private static double SoftLimit(double sample)
        {
            const double threshold = 0.90;
            const double ceiling = 0.995;

            double abs = Math.Abs(sample);
            if (abs <= threshold)
                return sample;

            double over = abs - threshold;
            double shaped = threshold + (ceiling - threshold) * (1.0 - Math.Exp(-over / (ceiling - threshold)));
            return Math.CopySign(Math.Min(ceiling, shaped), sample);
        }

        /// <summary>
        /// Advances one SID voice for a sample, including phase accumulation, hard sync, noise LFSR clocks, envelope stepping, and waveform output.
        /// </summary>
        /// <param name="vi">The voice index to step.</param>
        /// <param name="r">The red channel value.</param>
        private double StepVoice(int vi, byte[] r)
        {
            int vbase = vi * 7;
            Voice v = _voices[vi];

            ushort freqReg = (ushort)(r[vbase] | (r[vbase + 1] << 8));
            int pw12 = ((r[vbase + 3] & 0x0F) << 8) | r[vbase + 2];
            byte ctrl = r[vbase + 4];
            byte ad = r[vbase + 5];
            byte sr = r[vbase + 6];

            bool sync = (ctrl & 0x02) != 0;
            bool ring = (ctrl & 0x04) != 0;
            bool test = (ctrl & 0x08) != 0;

            Voice syncSrc = _voices[(vi + 2) % 3];

            v.LastAccum = v.PhaseAccum;
            if (test)
            {
                v.PhaseAccum = 0;
                v.NoiseShift = 0x7FFFFFu;
                v.CycleFrac = 0.0;
            }
            else
            {

                double cycles = CyclesPerSample + v.CycleFrac;
                int iCyc = (int)cycles;
                v.CycleFrac = cycles - iCyc;

                uint step = freqReg;
                for (int c = 0; c < iCyc; c++)
                {
                    uint prev = v.PhaseAccum;
                    uint next = (prev + step) & 0xFFFFFFu;

                    bool prevBit19 = (prev & (1u << 19)) != 0;
                    bool currBit19 = (next & (1u << 19)) != 0;
                    if (!prevBit19 && currBit19)
                    {
                        uint fb = ((v.NoiseShift >> 22) ^ (v.NoiseShift >> 17)) & 1;
                        v.NoiseShift = ((v.NoiseShift << 1) | fb) & 0x7FFFFFu;
                    }

                    v.PhaseAccum = next;
                }

                if (sync && syncSrc.PhaseAccum < syncSrc.LastAccum)
                    v.PhaseAccum = 0;
            }

            StepEnvelope(v, ad, sr);

            int waveform = ComputeWaveform(ctrl, v, pw12, ring, syncSrc.PhaseAccum);
            v.LastWaveform = waveform;

            double w = (waveform / 4095.0) - 0.5;
            return w * (v.EnvelopeLevel / 255.0);
        }

        /// <summary>Combines enabled triangle, sawtooth, pulse, and noise sources using SID-style waveform logic.</summary>
        /// <param name="ctrl">The SID waveform control register value.</param>
        /// <param name="v">The SID voice state to update or inspect.</param>
        /// <param name="pw12">The 12-bit pulse width value.</param>
        /// <param name="ring">Whether ring modulation is enabled.</param>
        /// <param name="syncSrcAccum">The synchronizing source oscillator accumulator.</param>
        private static int ComputeWaveform(byte ctrl, Voice v, int pw12, bool ring, uint syncSrcAccum)
        {
            bool tri = (ctrl & 0x10) != 0;
            bool saw = (ctrl & 0x20) != 0;
            bool pulse = (ctrl & 0x40) != 0;
            bool noise = (ctrl & 0x80) != 0;
            bool test = (ctrl & 0x08) != 0;

            if (!tri && !saw && !pulse && !noise) return 0;

            uint accum = v.PhaseAccum;
            int triVal = -1;
            int sawVal = -1;
            int pulseVal = -1;
            int noiseVal = -1;
            int active = 0;

            if (tri)
            {
                int upper = (int)(accum >> 11);
                int triSample;
                if (ring)
                {
                    bool inv = (syncSrcAccum & 0x800000u) != 0;
                    triSample = (upper ^ (inv ? 0x1FFF : 0)) & 0x0FFF;
                }
                else
                {
                    triSample = (upper & 0x1000) != 0
                        ? 0x0FFF - (upper & 0x0FFF)
                        : (upper & 0x0FFF);
                }
                triVal = triSample;
                active++;
            }

            if (saw)
            {
                sawVal = (int)(accum >> 12);
                active++;
            }

            if (pulse)
            {

                uint phase = accum >> 12;
                bool high = test || (phase < (uint)(pw12 & 0x0FFF));

                pulseVal = high ? 0x0FFF : 0x0000;
                active++;
            }

            if (noise)
            {
                noiseVal = NoiseOutput(v.NoiseShift);
                active++;
            }

            int result;
            if (active == 1)
            {
                result = triVal >= 0 ? triVal
                    : sawVal >= 0 ? sawVal
                    : pulseVal >= 0 ? pulseVal
                    : noiseVal;
            }
            else
            {

                result = 0x0FFF;
                if (triVal >= 0) result &= triVal;
                if (sawVal >= 0) result &= sawVal;
                if (pulseVal >= 0) result &= pulseVal;
                if (noiseVal >= 0) result &= noiseVal;
            }

            if (result < 0) result = 0;
            if (result > 0x0FFF) result = 0x0FFF;

            return result;
        }

        /// <summary>Maps selected 23-bit SID noise LFSR taps into the 12-bit waveform output.</summary>
        /// <param name="lfsr">The noise generator shift register value.</param>
        private static int NoiseOutput(uint lfsr)
        {

            return (int)(
                (((lfsr >> 20) & 1) << 11) |
                (((lfsr >> 18) & 1) << 10) |
                (((lfsr >> 14) & 1) << 9) |
                (((lfsr >> 11) & 1) << 8) |
                (((lfsr >> 9) & 1) << 7) |
                (((lfsr >> 5) & 1) << 6) |
                (((lfsr >> 2) & 1) << 5) |
                (((lfsr >> 0) & 1) << 4));
        }

        /// <summary>
        /// Advances a SID ADSR envelope using the current gate bit, attack/decay/release rates, and sustain level.
        /// </summary>
        /// <param name="v">The SID voice state to update or inspect.</param>
        /// <param name="gate">Whether the SID gate bit is currently set.</param>
        /// <param name="ad">The attack/decay register value.</param>
        /// <param name="sr">The sustain/release register value.</param>
        private static void StepEnvelope(Voice v, byte ad, byte sr)
        {
            int attackIdx = (ad >> 4) & 0x0F;
            int decayIdx = ad & 0x0F;
            int releaseIdx = sr & 0x0F;
            int sustainLvl = ((sr >> 4) & 0x0F) * 17;

            v.EnvTimer += CyclesPerSample;

            switch (v.EnvPhase)
            {
                case EnvPhase.Attack:
                    {
                        int threshold = AttackCycles[attackIdx];
                        while (v.EnvTimer >= threshold)
                        {
                            v.EnvTimer -= threshold;
                            if (v.EnvelopeLevel < 255) v.EnvelopeLevel++;
                            if (v.EnvelopeLevel >= 255)
                            {
                                v.EnvelopeLevel = 255;
                                v.EnvPhase = EnvPhase.Decay;
                                break;
                            }
                        }
                        break;
                    }

                case EnvPhase.Decay:
                    {
                        int threshold = DecayCycles[decayIdx] * ExpScale(v.EnvelopeLevel);
                        while (v.EnvTimer >= threshold)
                        {
                            v.EnvTimer -= threshold;
                            if (v.EnvelopeLevel > sustainLvl) v.EnvelopeLevel--;
                            if (v.EnvelopeLevel <= sustainLvl)
                            {
                                v.EnvelopeLevel = sustainLvl;
                                v.EnvPhase = EnvPhase.Sustain;
                                break;
                            }
                            threshold = DecayCycles[decayIdx] * ExpScale(v.EnvelopeLevel);
                        }
                        break;
                    }

                case EnvPhase.Sustain:

                    v.EnvelopeLevel = sustainLvl;
                    break;

                case EnvPhase.Release:
                    {
                        int threshold = DecayCycles[releaseIdx] * ExpScale(v.EnvelopeLevel);
                        while (v.EnvTimer >= threshold && v.EnvelopeLevel > 0)
                        {
                            v.EnvTimer -= threshold;
                            v.EnvelopeLevel--;
                            threshold = DecayCycles[releaseIdx] * ExpScale(Math.Max(v.EnvelopeLevel, 1));
                        }
                        break;
                    }
            }
        }

        /// <summary>
        /// Recomputes SID filter cutoff, resonance, and damping coefficients from the current filter registers.
        /// </summary>
        /// <param name="fcReg">The combined SID filter cutoff register value.</param>
        /// <param name="resReg">The SID filter resonance and routing register value.</param>
        private void UpdateFilterCoefficients(int fcReg, int resReg)
        {

            double fc = 30.0 + fcReg * 5.8;
            if (fc > SampleRate * 0.45) fc = SampleRate * 0.45;

            _filterF = 2.0 * Math.Sin(Math.PI * fc / SampleRate);
            if (_filterF > 1.4) _filterF = 1.4;

            double Q = 0.5 + resReg * 0.13;
            _filterQ = 1.0 / Q;

            if (Q > 2.0)
            {

                double excessQ = (Q - 2.0) / (2.5 - 2.0);
                _resonancePeakDamping = 1.0 - (excessQ * 0.25);
            }
            else
            {
                _resonancePeakDamping = 1.0;
            }
        }

        /// <summary>
        /// Steps the SID state-variable filter and combines the selected low-pass, band-pass, and high-pass outputs.
        /// </summary>
        /// <param name="input">The input sample entering the filter.</param>
        /// <param name="lpOn">Whether the low-pass output is enabled.</param>
        /// <param name="bpOn">Whether the band-pass output is enabled.</param>
        /// <param name="hpOn">Whether the high-pass output is enabled.</param>
        private double StepFilter(double input, bool lpOn, bool bpOn, bool hpOn)
        {

            _flp *= _filterCapacitorLeakage;
            _fbp *= _filterCapacitorLeakage;

            double damnedFilterQ = _filterQ * _resonancePeakDamping;
            double hp = input - damnedFilterQ * _fbp - _flp;

            _fbp += _filterF * hp;
            _flp += _filterF * _fbp;

            double o = 0.0;
            if (lpOn) o += _flp;
            if (bpOn) o += _fbp;
            if (hpOn) o += hp;

            return o;
        }

        private enum EnvPhase
        { Attack, Decay, Sustain, Release }

        private readonly struct SidWrite
        {
            /// <summary>Captures a SID register value and the emulated clock tick when it takes effect.</summary>
            /// <param name="reg">The SID register index.</param>
            /// <param name="value">The SID register value written.</param>
            /// <param name="tick">The SID clock tick when the write should take effect.</param>
            public SidWrite(int reg, byte value, long tick)
            {
                Reg = reg;
                Value = value;
                Tick = tick;
            }

            public int Reg { get; }

            public byte Value { get; }

            public long Tick { get; }
        }

        private sealed class Voice
        {
            public uint PhaseAccum;
            public uint LastAccum;
            public uint NoiseShift;
            public double CycleFrac;
            public int EnvelopeLevel;
            public EnvPhase EnvPhase;
            public double EnvTimer;
            public int LastWaveform;

            /// <summary>Resets one SID voice oscillator, noise LFSR, envelope, and waveform history.</summary>
            public void Reset()
            {
                PhaseAccum = 0;
                LastAccum = 0;
                NoiseShift = 0x7FFFFFu;
                EnvelopeLevel = 0;
                EnvPhase = EnvPhase.Release;
                EnvTimer = 0.0;
                LastWaveform = 0;
            }
        }
    }
}
