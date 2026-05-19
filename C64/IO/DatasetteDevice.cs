// ============================================================================
// Project:     C64
// File:        DatasetteDevice.cs
// Description: Datasette TAP pulse playback model with motor state, sense line
//              behavior, and read-line transitions.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

namespace C64
{
    /// <summary>
    /// Emulates datasette TAP pulse playback, including motor spin-up, sense state, read-line transitions, and pulse stepping.
    /// </summary>
    internal sealed class DatasetteDevice
    {
        private const int PalCpuHz = 985_248;
        private const int MotorSpinupMs = 200;
        private const int MotorSpinupCycles = (PalCpuHz * MotorSpinupMs) / 1000;

        private readonly List<int> pulseCycles = new List<int>();
        private int pulseIndex;
        private int pulseRemaining;
        private int motorSpinupRemaining;

        /// <summary>Gets whether a tape is attached.</summary>
        public bool HasTape => pulseCycles.Count > 0;

        /// <summary>Gets whether the datasette motor is running.</summary>
        public bool MotorOn { get; private set; }

        /// <summary>Gets the datasette sense line state.</summary>
        public bool SenseHigh => HasTape;

        /// <summary>Gets the datasette read line state.</summary>
        public bool ReadHigh { get; private set; } = true;

        /// <summary>Attaches a TAP pulse stream to the datasette.</summary>
        /// <param name="raw">The raw bytes to decode.</param>
        public void AttachTap(byte[] raw)
        {
            pulseCycles.Clear();
            pulseIndex = 0;
            pulseRemaining = 0;
            motorSpinupRemaining = 0;
            ReadHigh = true;

            if (raw.Length < 20)
                throw new InvalidDataException("TAP file too short.");

            string magic = System.Text.Encoding.ASCII.GetString(raw, 0, 12);
            if (!magic.StartsWith("C64-TAPE-RAW", StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported TAP format.");

            byte version = raw[12];
            int dataSize = raw[16] | (raw[17] << 8) | (raw[18] << 16) | (raw[19] << 24);
            int end = Math.Min(20 + dataSize, raw.Length);

            int p = 20;
            while (p < end)
            {
                byte b = raw[p++];
                int units;
                if (b != 0)
                {
                    units = b;
                }
                else if (version >= 1)
                {
                    if (p + 3 > end)
                        break;
                    int longCycles = raw[p] | (raw[p + 1] << 8) | (raw[p + 2] << 16);
                    p += 3;
                    units = Math.Max(1, longCycles / 8);
                }
                else
                {
                    continue;
                }

                // TAP units are 8 CPU cycles.
                pulseCycles.Add(Math.Max(1, units * 8));
            }

            if (pulseCycles.Count > 0)
                pulseRemaining = pulseCycles[0];
        }

        /// <summary>Ejects the attached media.</summary>
        public void Eject()
        {
            pulseCycles.Clear();
            pulseIndex = 0;
            pulseRemaining = 0;
            motorSpinupRemaining = 0;
            ReadHigh = true;
        }

        /// <summary>Sets motor.</summary>
        /// <param name="on">Whether the datasette motor should be on.</param>
        public void SetMotor(bool on)
        {
            if (on && !MotorOn)
                motorSpinupRemaining = MotorSpinupCycles;
            MotorOn = on;
        }

        // Returns true when READ line toggled during this step.

        /// <summary>Advances the device by the specified CPU cycles.</summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool Step(uint cycles)
        {
            if (!MotorOn || !HasTape || cycles == 0)
                return false;

            if (motorSpinupRemaining > 0)
            {
                int consume = Math.Min((int)cycles, motorSpinupRemaining);
                motorSpinupRemaining -= consume;
                if (consume == (int)cycles)
                    return false;

                cycles -= (uint)consume;
            }

            bool toggled = false;
            int remaining = (int)cycles;

            while (remaining > 0 && pulseIndex < pulseCycles.Count)
            {
                if (pulseRemaining > remaining)
                {
                    pulseRemaining -= remaining;
                    remaining = 0;
                    break;
                }

                remaining -= pulseRemaining;
                ReadHigh = !ReadHigh;
                toggled = true;

                pulseIndex++;
                if (pulseIndex >= pulseCycles.Count)
                    break;

                pulseRemaining = pulseCycles[pulseIndex];
            }

            return toggled;
        }
    }
}