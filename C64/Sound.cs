using System.Diagnostics;
using System.Collections.Concurrent;
using static SDL2.SDL;

namespace C64
{
    /// <summary>
    /// MOS 6581 SID chip emulator.  Three voices with triangle / sawtooth /
    /// pulse / noise waveforms, full ADSR envelopes, hard-sync, ring modulation,
    /// and a two-pole state-variable filter (LP / BP / HP).
    ///
    /// Audio is pushed to SDL via <c>SDL_QueueAudio</c> from a dedicated
    /// synthesis thread; there is no SDL audio callback.  The synthesis thread
    /// derives the number of samples to generate from wall-clock time so it
    /// runs decoupled from the CPU emulation thread.
    ///
    /// Usage from <c>C64Emulator</c>:
    ///   � Call <see cref="Init"/>     once before <see cref="Start"/>.
    ///   � Call <see cref="Start"/>    when the CPU thread starts.
    ///   � Route <c>OnIOWrite</c>      writes of $D400�$D41C to <see cref="WriteRegister"/>.
    ///   � Route <c>OnIORead</c>       reads  of $D419�$D41C to <see cref="ReadRegister"/>.
    ///   � Call <see cref="Reset"/>    from <c>InitHardware</c>.
    ///   � Call <see cref="Dispose"/>  on shutdown.
    /// </summary>
    internal sealed class Sound : IDisposable
    {
        // ?? Clock and sample rate ?????????????????????????????????????????????

        private const double CpuFreq = 985_248.0; // PAL 6510 clock (Hz)
        private const int SampleRate = 44_100;
        private const double CyclesPerSample = CpuFreq / SampleRate; // ? 22.34

        // Keep ~40 ms buffered in SDL's queue; stall when above ~80 ms.
        private const int TargetLatencyMs = 40;
        private const int MaxLatencyMs = 80;
        internal static bool TraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable("C64_SID_TRACE"), "1", StringComparison.Ordinal);

        internal static bool TraceEnvelope { get; set; } =
            string.Equals(Environment.GetEnvironmentVariable("C64_SID_ENV_TRACE"), "1", StringComparison.Ordinal);

        // ?? SID register file ($D400 = reg 0 � $D41C = reg 28) ????????????????
        // Synth-thread register image. CPU-thread writes are queued with
        // timestamps and applied by the synth thread in time order.
        private readonly byte[] _regs = new byte[29];
        private readonly ConcurrentQueue<SidWrite> _writeQueue = new();

        // ?? Per-voice synthesis state (owned by the synthesis thread) ?????????
        private readonly Voice[] _voices = { new(), new(), new() };

        // Overall gain trim for the synthesized SID voice path.
        // A modest preamp helps low-sustain game SFX remain audible.
        private const double VoicePreamp = 3.0;

        // D418 volume-DAC approximation for sample-style SFX.
        // Use a high-passed (delta) path to avoid large DC offsets.
        private const double VolumeDacStepGain = 0.22;
        private const double VolumeDacDecay = 0.996;

        // ?? State-variable filter (owned by the synthesis thread) ?????????????
        private double _flp, _fbp;          // low-pass / band-pass accumulators
        private double _filterF = 0.1;      // 2�sin(?�fc/fs)
        private double _filterQ = 1.0;      // 1/Q damping coefficient
        private int _lastFcReg = -1;    // cached to detect register changes
        private int _lastResReg = -1;
        private double _lastVolNorm;
        private double _volDacLevel;

        // ?? SDL audio ?????????????????????????????????????????????????????????
        private uint _dev;               // SDL audio device id (0 = none)
        private short[] _buf = Array.Empty<short>();

        // ?? Voice-3 oscillator / envelope readback ($D41B / $D41C) ????????????
        private volatile byte _v3Wave;
        private volatile byte _v3Env;

        // ?? Synthesis thread ??????????????????????????????????????????????????
        private Thread? _thread;
        private CancellationToken _ct;

        // ?? ADSR rate tables (cycles per envelope step, PAL clock) ????????????
        //
        // Attack times (per step from 0 ? 255):
        //   2, 8, 16, 24, 38, 56, 68, 80, 100, 250, 500, 800, 1000, 3000, 5000, 8000 ms
        private static readonly int[] AttackCycles =
        {
                8,    31,    63,    92,   146,   216,   262,   308,
              385,   963,  1925,  3079,  3849, 11547, 19245, 30792,
        };

        // Decay / Release times (per step at the fastest exponential phase):
        //   6, 24, 48, 72, 114, 168, 204, 240, 300, 750, 1500, 2400, 3000, 9000, 15000, 24000 ms
        private static readonly int[] DecayCycles =
        {
               24,    93,   188,   276,   437,   647,   786,   924,
             1155,  2887,  5773,  9238, 11547, 34641, 57736, 92369,
        };

        // Exponential slowdown applied during decay/release based on current
        // envelope level � reproduces the characteristic SID curve.
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

        // ?? Public API ????????????????????????????????????????????????????????

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

            int count = SDL_GetNumAudioDevices(0); // 0 = playback (iscapture)
            for (int i = 0; i < count; i++)
            {
                string? name = null;
                try { name = SDL_GetAudioDeviceName(i, 0); } catch { name = null; }
                names.Add(name ?? $"Device {i}");
            }
            return names;
        }

        /// <summary>
        /// Show an ImGui modal listing every available playback device and
        /// return the user's choice.  Returns <c>null</c> for the system
        /// default (also used when there is 0 or 1 device available).
        /// </summary>
        public static string? PromptForDevice()
        {
            List<string> devices = EnumerateDevices();
            return SoundDevicePicker.Prompt(devices);
        }

        public void Init(string? deviceName = null)
        {
            if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0)
                throw new Exception($"SDL audio init failed: {SDL_GetError()}");

            var desired = new SDL_AudioSpec
            {
                freq = SampleRate,
                format = AUDIO_S16SYS,
                channels = 1,
                samples = 512,
                // callback left null = SDL queue-audio mode (no callback thread)
            };

            _dev = SDL_OpenAudioDevice(deviceName, 0, ref desired, out _, 0);
            if (_dev == 0)
                throw new Exception($"SDL_OpenAudioDevice failed: {SDL_GetError()}");

            SDL_PauseAudioDevice(_dev, 0); // 0 = unpause ? start playing
        }

        public void Start(CancellationToken token)
        {
            _ct = token;
            _thread = new Thread(SynthesisLoop)
            {
                IsBackground = true,
                Name = "SID synth",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        /// <summary>Write a SID register (0�28 maps to $D400�$D41C).</summary>
        public void WriteRegister(int reg, byte value)
        {
            if ((uint)reg < 29)
                _writeQueue.Enqueue(new SidWrite(reg, value, Stopwatch.GetTimestamp()));
        }

        /// <summary>
        /// Read a SID register.  Only $D41B/$D41C return meaningful values
        /// (voice-3 oscillator and envelope read-back).  All write-only
        /// registers read as 0.  Paddle ports $D419/$D41A return $FF.
        /// </summary>
        public byte ReadRegister(int reg) => reg switch
        {
            25 => 0xFF,      // $D419 POTX � paddle not emulated
            26 => 0xFF,      // $D41A POTY � paddle not emulated
            27 => _v3Wave,   // $D41B voice-3 oscillator output
            28 => _v3Env,    // $D41C voice-3 envelope  output
            _ => 0,
        };

        /// <summary>Reset all SID registers and per-voice state.</summary>
        public void Reset()
        {
            Array.Clear(_regs);
            while (_writeQueue.TryDequeue(out _)) { }
            foreach (var v in _voices) v.Reset();
            _flp = _fbp = 0.0;
            _lastFcReg = -1;
            _lastResReg = -1;
            _lastVolNorm = 0.0;
            _volDacLevel = 0.0;
            _v3Wave = 0;
            _v3Env = 0;
            if (_dev != 0)
                SDL_ClearQueuedAudio(_dev);
        }

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

        // ?? Synthesis loop ????????????????????????????????????????????????????

        private void SynthesisLoop()
        {
            long last = Stopwatch.GetTimestamp();
            double fracCyc = 0.0;

            while (!_ct.IsCancellationRequested)
            {
                // Convert real time elapsed since last wakeup into CPU cycles,
                // then into a sample count.  The fractional remainder carries
                // over so we don't drift over time.
                long now = Stopwatch.GetTimestamp();
                long batchStart = last;
                double elapsed = (now - batchStart) / (double)Stopwatch.Frequency;
                last = now;

                double cyc = elapsed * CpuFreq + fracCyc;
                int count = (int)(cyc / CyclesPerSample);
                fracCyc = cyc - count * CyclesPerSample;

                // Cap a single batch so a long stall doesn't produce a huge
                // burst of audio (e.g. after the debugger pauses the thread).
                if (count > SampleRate / 10) count = SampleRate / 10;

                if (count > 0)
                {
                    if (_buf.Length < count)
                        _buf = new short[count + 256];

                    Synthesize(_buf, count, batchStart, now);

                    unsafe
                    {
                        fixed (short* p = _buf)
                            SDL_QueueAudio(_dev, (IntPtr)p, (uint)(count * sizeof(short)));
                    }
                }

                // Pace ourselves against the device's queue depth.
                uint qBytes = SDL_GetQueuedAudioSize(_dev);
                int qMs = (int)(qBytes / 2 * 1000 / SampleRate);

                if (qMs >= MaxLatencyMs) Thread.Sleep(10);
                else if (qMs >= TargetLatencyMs) Thread.Sleep(4);
                else Thread.Sleep(2);
            }
        }

        // ?? Per-sample synthesis ??????????????????????????????????????????????

        private void Synthesize(short[] buf, int count, long startTick, long endTick)
        {
            byte[] r = _regs;
            long span = endTick - startTick;
            if (span < 0) span = 0;
            byte modeVol = r[24];
            byte masterVol = (byte)(modeVol & 0x0F);
            bool lpOn = (modeVol & 0x10) != 0;
            bool bpOn = (modeVol & 0x20) != 0;
            bool hpOn = (modeVol & 0x40) != 0;
            bool voice3Mute = (modeVol & 0x80) != 0;

            byte resRoute = r[23];
            byte filterRoute = (byte)(resRoute & 0x07);
            int resReg = (resRoute >> 4) & 0x0F;
            int fcReg = (r[22] << 3) | (r[21] & 0x07); // 11-bit cutoff

            if (fcReg != _lastFcReg || resReg != _lastResReg)
            {
                _lastFcReg = fcReg;
                _lastResReg = resReg;
                UpdateFilterCoefficients(fcReg, resReg);
            }

            for (int i = 0; i < count; i++)
            {
                long sampleTick = startTick + ((long)(i + 1) * span) / count;
                ApplyWritesUntil(sampleTick, r);

                // Re-read mode/filter state after any writes applied for this sample.
                modeVol = r[24];
                masterVol = (byte)(modeVol & 0x0F);
                lpOn = (modeVol & 0x10) != 0;
                bpOn = (modeVol & 0x20) != 0;
                hpOn = (modeVol & 0x40) != 0;
                voice3Mute = (modeVol & 0x80) != 0;

                // Approximate D418 sample playback by responding to volume
                // steps, then decaying the impulse over time (AC-coupled).
                double volNorm = masterVol / 15.0;
                double volDelta = volNorm - _lastVolNorm;
                _lastVolNorm = volNorm;
                _volDacLevel = (_volDacLevel * VolumeDacDecay) + (volDelta * VolumeDacStepGain);

                resRoute = r[23];
                filterRoute = (byte)(resRoute & 0x07);
                resReg = (resRoute >> 4) & 0x0F;
                fcReg = (r[22] << 3) | (r[21] & 0x07);
                if (fcReg != _lastFcReg || resReg != _lastResReg)
                {
                    _lastFcReg = fcReg;
                    _lastResReg = resReg;
                    UpdateFilterCoefficients(fcReg, resReg);
                }

                double v0 = StepVoice(0, r, mute: false);
                double v1 = StepVoice(1, r, mute: false);
                double v2 = StepVoice(2, r, mute: false);

                // Voice-3 readback for $D41B/$D41C
                _v3Wave = (byte)(_voices[2].LastWaveform >> 4); // 12-bit ? 8-bit
                _v3Env = (byte)_voices[2].EnvelopeLevel;

                // Split into "through filter" and "bypass filter" paths
                double filtered = 0.0, bypass = 0.0;
                if ((filterRoute & 0x01) != 0) filtered += v0; else bypass += v0;
                if ((filterRoute & 0x02) != 0) filtered += v1; else bypass += v1;
                // $D418 bit 7 (voice3 off) disconnects only the direct output
                // path of voice 3. If voice 3 is routed through the filter,
                // it must still be audible.
                if ((filterRoute & 0x04) != 0)
                    filtered += v2;
                else if (!voice3Mute)
                    bypass += v2;

                double filtOut = StepFilter(filtered, lpOn, bpOn, hpOn);

                // Voice path scales with master volume. Apply a preamp so low
                // envelope levels are still audible, then soft clip to keep
                // peaks bounded without harsh clipping.
                double mixed = (filtOut + bypass) * (masterVol / 15.0) * VoicePreamp;
                mixed += _volDacLevel;
                mixed = Math.Tanh(mixed * 1.6);

                buf[i] = (short)(mixed * 32767.0);
            }
        }

        private void ApplyWritesUntil(long tick, byte[] regs)
        {
            while (_writeQueue.TryPeek(out SidWrite w) && w.Tick <= tick)
            {
                if (!_writeQueue.TryDequeue(out SidWrite d))
                    break;

                byte previous = regs[d.Reg];
                regs[d.Reg] = d.Value;

                // Control register gate edges must be handled at write time.
                // Multiple writes can occur between audio samples; if we only
                // inspect the final gate state per sample we can miss short
                // gate pulses and lose ADSR retriggers.
                HandleGateEdgeOnWrite(d.Reg, previous, d.Value, regs);

                if (TraceEnabled && IsAudibleTraceRegister(d.Reg, previous, d.Value))
                {
                    Console.Error.WriteLine(
                        $"[SID] t={d.Tick} reg=${d.Reg + 0xD400:X4} {previous:X2}->{d.Value:X2} {DescribeSidState(regs)}");
                }
            }
        }

        private void HandleGateEdgeOnWrite(int reg, byte previous, byte value, byte[] regs)
        {
            int voiceBase = (reg / 7) * 7;
            int offset = reg - voiceBase;
            if (offset != 4)
                return;

            bool prevGate = (previous & 0x01) != 0;
            bool gate = (value & 0x01) != 0;
            if (prevGate == gate)
                return;

            int voiceIdx = voiceBase / 7;
            Voice v = _voices[voiceIdx];
            byte sr = regs[voiceBase + 6];
            int sustainLvl = ((sr >> 4) & 0x0F) * 17;

            if (gate)
            {
                v.EnvPhase = EnvPhase.Attack;
                v.EnvTimer = 0.0;
                if (TraceEnvelope)
                    Console.Error.WriteLine($"[ENV] GATE ON -> Attack (sustain={sustainLvl})");
            }
            else
            {
                v.EnvPhase = EnvPhase.Release;
                v.EnvTimer = 0.0;
                if (TraceEnvelope)
                    Console.Error.WriteLine($"[ENV] GATE OFF -> Release from level {v.EnvelopeLevel}");
            }

            v.GatePrev = gate;
        }

        private static bool IsAudibleTraceRegister(int reg, byte previous, byte value)
        {
            if (reg == 0x18 || reg == 0x17)
                return previous != value;

            int voiceBase = (reg / 7) * 7;
            int offset = reg - voiceBase;
            return offset is 0 or 1 or 2 or 3 or 4 or 5 or 6;
        }

        // Extend trace with per-sample envelope and waveform diagnostics
        private void TraceEnvelopeState(int voiceIdx, byte[] r)
        {
            if (!TraceEnabled) return;

            int vbase = voiceIdx * 7;
            Voice v = _voices[voiceIdx];
            byte ctrl = r[vbase + 4];
            byte ad = r[vbase + 5];
            byte sr = r[vbase + 6];

            bool gate = (ctrl & 0x01) != 0;
            int sustainLvl = ((sr >> 4) & 0x0F) * 17;

            Console.Error.WriteLine(
                $"[ENVELOPE] v{voiceIdx} gate={gate} env={v.EnvelopeLevel}/sustain={sustainLvl} " +
                $"phase={v.EnvPhase} waveform={v.LastWaveform:X3}");
        }

        private static string DescribeSidState(byte[] regs)
        {
            string VoiceState(int baseReg)
            {
                ushort freq = (ushort)(regs[baseReg] | (regs[baseReg + 1] << 8));
                ushort pw = (ushort)(((regs[baseReg + 3] & 0x0F) << 8) | regs[baseReg + 2]);
                byte ctrl = regs[baseReg + 4];
                byte ad = regs[baseReg + 5];
                byte sr = regs[baseReg + 6];
                return $"v{baseReg / 7}:F={freq:X4} PW={pw:X3} C={ctrl:X2} AD={ad:X2} SR={sr:X2}";
            }

            return $"vol={regs[0x18]:X2} filt={regs[0x17]:X2} {VoiceState(0)} {VoiceState(7)} {VoiceState(14)}";
        }

        // Synthesize one sample from one voice.  Returns value in [-0.5, 0.5].
        private double StepVoice(int vi, byte[] r, bool mute)
        {
            int vbase = vi * 7;
            Voice v = _voices[vi];

            ushort freqReg = (ushort)(r[vbase] | (r[vbase + 1] << 8));
            int pw12 = ((r[vbase + 3] & 0x0F) << 8) | r[vbase + 2];
            byte ctrl = r[vbase + 4];
            byte ad = r[vbase + 5];
            byte sr = r[vbase + 6];

            bool gate = (ctrl & 0x01) != 0;
            bool sync = (ctrl & 0x02) != 0;
            bool ring = (ctrl & 0x04) != 0;
            bool test = (ctrl & 0x08) != 0;

            // Sync wiring: voice 0 ? voice 2, voice 1 ? voice 0, voice 2 ? voice 1.
            Voice syncSrc = _voices[(vi + 2) % 3];

            // ?? Advance phase accumulator (24-bit) ??
            v.LastAccum = v.PhaseAccum;
            if (test)
            {
                v.PhaseAccum = 0;
                v.NoiseShift = 0x7FFFFFu;
                v.CycleFrac = 0.0;
            }
            else
            {
                // PhaseAccum advances once per CPU cycle on real hardware.
                // Step cycle-by-cycle so we do not miss internal transitions
                // (especially noise LFSR clocks) within a single audio sample.
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

                // Hard sync: when the sync source wraps (its new accumulator is
                // smaller than its previous one) this voice's accumulator resets.
                if (sync && syncSrc.PhaseAccum < syncSrc.LastAccum)
                    v.PhaseAccum = 0;
            }

            // ?? Envelope ??
            StepEnvelope(v, gate, ad, sr);

            // ?? Waveform ??
            int waveform = ComputeWaveform(ctrl, v, pw12, ring, syncSrc.PhaseAccum);
            v.LastWaveform = waveform;
            if (mute) return 0.0;

            // Log waveform output for voice 0 during sustain at envelope trace level
            if (TraceEnvelope && vi == 0 && v.EnvPhase == EnvPhase.Sustain && (ctrl & 0x20) != 0)
            {
                int sustainLvl = ((sr >> 4) & 0x0F) * 17;
                double output = ((waveform / 4095.0) - 0.5) * (v.EnvelopeLevel / 255.0);
                Console.Error.WriteLine(
                    $"[WAVEFORM] v0: sustain={sustainLvl} env={v.EnvelopeLevel} raw={waveform:X4} output={output:F5}");
            }

            // Re-centre the 12-bit unsigned waveform around 0 (range -0.5..+0.5)
            // and scale by the 8-bit envelope so that envelope=0 produces true
            // silence.  Applying the -0.5 bias *before* the envelope multiply
            // avoids the large DC step (and audible click) every time a voice
            // gates on/off or its waveform bits change while the envelope is
            // at or near zero.
            double w = (waveform / 4095.0) - 0.5;
            return w * (v.EnvelopeLevel / 255.0);
        }

        // ?? Waveform generator (with combination-AND approximation) ???????????

        private static int ComputeWaveform(byte ctrl, Voice v, int pw12, bool ring, uint syncSrcAccum)
        {
            bool tri = (ctrl & 0x10) != 0;
            bool saw = (ctrl & 0x20) != 0;
            bool pulse = (ctrl & 0x40) != 0;
            bool noise = (ctrl & 0x80) != 0;
            bool test = (ctrl & 0x08) != 0;

            if (!tri && !saw && !pulse && !noise) return 0;

            uint accum = v.PhaseAccum;
            int result = 0;
            int active = 0;

            if (tri)
            {
                int upper = (int)(accum >> 11); // 0..8191
                int triVal;
                if (ring)
                {
                    bool inv = (syncSrcAccum & 0x800000u) != 0;
                    triVal = (upper ^ (inv ? 0x1FFF : 0)) & 0x0FFF;
                }
                else
                {
                    triVal = (upper & 0x1000) != 0
                        ? 0x0FFF - (upper & 0x0FFF)   // descending half
                        : (upper & 0x0FFF);           // ascending half
                }
                result += triVal;
                active++;
            }

            if (saw)
            {
                result += (int)(accum >> 12); // 12-bit sawtooth
                active++;
            }

            if (pulse)
            {
                // SID pulse output is high while phase < pulse width.
                // Handle extreme widths as silence to avoid DC-like output.
                uint phase = accum >> 12; // 0..4095
                bool high;
                if (pw12 <= 0 || pw12 >= 0x0FFF)
                    high = false;
                else
                    high = test || (phase < (uint)pw12);

                result += high ? 0x0FFF : 0x0000;
                active++;
            }

            if (noise)
            {
                result += NoiseOutput(v.NoiseShift);
                active++;
            }

            if (active > 1)
                result /= active;

            if (result < 0) result = 0;
            if (result > 0x0FFF) result = 0x0FFF;

            return result;
        }

        private static int NoiseOutput(uint lfsr)
        {
            // 8 noise output bits come from LFSR taps 20,18,14,11,9,5,2,0
            // and are placed into the upper 8 bits of the 12-bit output.
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

        // ?? ADSR envelope ?????????????????????????????????????????????????????

        private static void StepEnvelope(Voice v, bool gate, byte ad, byte sr)
        {
            int attackIdx = (ad >> 4) & 0x0F;
            int decayIdx = ad & 0x0F;
            int releaseIdx = sr & 0x0F;
            int sustainLvl = ((sr >> 4) & 0x0F) * 17;   // 0..15 → 0..255

            // Gate transitions are handled in ApplyWritesUntil so short
            // pulses between samples are not lost.
            v.GatePrev = gate;

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
                                if (TraceEnvelope)
                                    Console.Error.WriteLine($"[ENV] Attack complete → Decay to sustain {sustainLvl}");
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
                                if (TraceEnvelope)
                                    Console.Error.WriteLine($"[ENV] Decay complete → Sustain at {sustainLvl}");
                                break;
                            }
                            threshold = DecayCycles[decayIdx] * ExpScale(v.EnvelopeLevel);
                        }
                        break;
                    }

                case EnvPhase.Sustain:
                    // Sustain level may have changed under our feet.
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

        // ?? Filter ????????????????????????????????????????????????????????????

        private void UpdateFilterCoefficients(int fcReg, int resReg)
        {
            // 6581 approximation: fc ? 30 + fcReg � 5.8 Hz, capped under Nyquist.
            double fc = 30.0 + fcReg * 5.8;
            if (fc > SampleRate * 0.45) fc = SampleRate * 0.45;

            // Chamberlin SVF coefficient F = 2 � sin(? � fc / fs)
            _filterF = 2.0 * Math.Sin(Math.PI * fc / SampleRate);
            if (_filterF > 1.4) _filterF = 1.4; // guard against instability

            // Resonance damping: Q from 0.5 (low) to ~2.5 (high), 1/Q is the damping.
            double Q = 0.5 + resReg * 0.13;
            _filterQ = 1.0 / Q;
        }

        private double StepFilter(double input, bool lpOn, bool bpOn, bool hpOn)
        {
            // Chamberlin two-pole state-variable filter.
            double hp = input - _filterQ * _fbp - _flp;
            _fbp += _filterF * hp;
            _flp += _filterF * _fbp;

            // Mix the requested filter outputs.  If no mode bit is set, filtered
            // voices produce silence � matching real SID behaviour.
            double o = 0.0;
            if (lpOn) o += _flp;
            if (bpOn) o += _fbp;
            if (hpOn) o += hp;
            return o;
        }

        // ?? Per-voice state ???????????????????????????????????????????????????

        private enum EnvPhase { Attack, Decay, Sustain, Release }

        private readonly struct SidWrite
        {
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
            public uint PhaseAccum;     // 24-bit phase accumulator
            public uint LastAccum;      // accumulator before the latest step
            public uint NoiseShift;     // 23-bit LFSR for noise waveform
            public double CycleFrac;      // fractional CPU cycles left over from last sample
            public int EnvelopeLevel;  // 0..255
            public EnvPhase EnvPhase;       // attack / decay / sustain / release
            public double EnvTimer;       // cycles since last envelope step
            public bool GatePrev;       // previous gate bit (edge detection)
            public int LastWaveform;   // 12-bit waveform out (voice-3 readback)

            public void Reset()
            {
                PhaseAccum = 0;
                LastAccum = 0;
                NoiseShift = 0x7FFFFFu;     // all bits set ? quietest noise
                EnvelopeLevel = 0;
                EnvPhase = EnvPhase.Release;
                EnvTimer = 0.0;
                GatePrev = false;
                LastWaveform = 0;
            }
        }
    }
}
