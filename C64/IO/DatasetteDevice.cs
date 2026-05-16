namespace C64
{
    internal sealed class DatasetteDevice
    {
        private const int PalCpuHz = 985_248;
        private const int MotorSpinupMs = 200;
        private const int MotorSpinupCycles = (PalCpuHz * MotorSpinupMs) / 1000;

        private readonly List<int> pulseCycles = new List<int>();
        private int pulseIndex;
        private int pulseRemaining;
        private int motorSpinupRemaining;

        public bool HasTape => pulseCycles.Count > 0;
        public bool MotorOn { get; private set; }
        public bool SenseHigh => HasTape;
        public bool ReadHigh { get; private set; } = true;

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

        public void Eject()
        {
            pulseCycles.Clear();
            pulseIndex = 0;
            pulseRemaining = 0;
            motorSpinupRemaining = 0;
            ReadHigh = true;
        }

        public void SetMotor(bool on)
        {
            if (on && !MotorOn)
                motorSpinupRemaining = MotorSpinupCycles;
            MotorOn = on;
        }

        // Returns true when READ line toggled during this step.
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
