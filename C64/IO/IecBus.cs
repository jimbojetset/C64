using System.Text;

namespace C64
{
    internal sealed class IecBus
    {
        private readonly VirtualDrive1541 drive;
        private static readonly bool LowLevelEnabled =
            string.Equals(Environment.GetEnvironmentVariable("C64_IEC_LOWLEVEL"), "1", StringComparison.Ordinal);

        // Open-collector line model; true = released/high, false = driven low.
        private bool hostDataRelease = true;
        private bool hostClockRelease = true;
        private bool hostAtnRelease = true;

        private bool devDataRelease = true;
        private bool devClockRelease = true;

        private byte? currentListener;
        private byte? currentTalker;
        private byte? listenSecondary;
        private byte? talkSecondary;
        private readonly List<byte> commandBytes = new List<byte>();
        private Queue<byte> talkQueue = new Queue<byte>();
        private Queue<byte> statusQueue = new Queue<byte>();
        private readonly Dictionary<byte, DirectChannel> directChannels = new Dictionary<byte, DirectChannel>();
        private readonly Dictionary<byte, byte> logicalChannels = new Dictionary<byte, byte>();
        private byte? currentInputChannel;
        private byte? currentOutputChannel;

        private string? pendingFilename;
        private bool hostLooseProgramPresent;
        private string driveStatus = "00, OK,00,00";

        private bool prevHostClockRelease = true;
        private bool prevHostAtnRelease = true;
        private int lowLevelDataHoldTicks;
        private int lowLevelClockHoldTicks;
        private int lowLevelBytePhase;  // 0=handshake, 1-8=bit0-7, cycles for next byte
        private byte lowLevelCurrentByte;

        public IecBus(VirtualDrive1541 drive)
        {
            this.drive = drive;
        }

        public Action? OnDriveActivity { get; set; }

        public void UpdateHostCia2PortA(byte dd00, byte dd02)
        {
            // CIA2 port A IEC lines (active-low) on bits 5:DATA, 4:CLOCK, 3:ATN.
            bool outData = (dd02 & 0x20) != 0;
            bool outClock = (dd02 & 0x10) != 0;
            bool outAtn = (dd02 & 0x08) != 0;

            hostDataRelease = !outData || (dd00 & 0x20) != 0;
            hostClockRelease = !outClock || (dd00 & 0x10) != 0;
            hostAtnRelease = !outAtn || (dd00 & 0x08) != 0;

            if (LowLevelEnabled)
                StepLowLevelResponder();
        }

        public void SetHostLooseProgramPresent(bool present)
        {
            hostLooseProgramPresent = present;
        }

        public bool Open(byte logicalFile, byte device, byte secondaryAddress, string? name)
        {
            if (NormalizeDevice(device) != 8)
                return false;

            byte channel = (byte)(secondaryAddress & 0x0F);
            logicalChannels[logicalFile] = channel;

            string text = name?.Trim().Trim('"', '\'') ?? string.Empty;
            if (channel == 15)
            {
                if (text.Length != 0)
                    ExecuteDriveCommand(text);
                else
                    SetDriveOk();
                return true;
            }

            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                directChannels[channel] = new DirectChannel(new byte[256]);
                SetDriveOk();
                return true;
            }

            if (text.Length != 0)
                pendingFilename = text;

            SetDriveOk();
            return true;
        }

        public bool Close(byte logicalFile)
        {
            if (!logicalChannels.TryGetValue(logicalFile, out byte channel))
                return false;

            if (currentOutputChannel == channel)
                FlushOutput();

            logicalChannels.Remove(logicalFile);
            if (currentInputChannel == channel)
                currentInputChannel = null;
            if (currentOutputChannel == channel)
                currentOutputChannel = null;
            if (channel != 15)
                directChannels.Remove(channel);
            SetDriveOk();
            return true;
        }

        public bool Chkin(byte logicalFile)
        {
            if (!logicalChannels.TryGetValue(logicalFile, out byte channel))
                return false;

            currentInputChannel = channel;
            if (channel == 15)
                PrepareStatusBuffer();
            return true;
        }

        public bool Chkout(byte logicalFile)
        {
            if (!logicalChannels.TryGetValue(logicalFile, out byte channel))
                return false;

            currentOutputChannel = channel;
            commandBytes.Clear();
            return true;
        }

        public void Clrchn()
        {
            currentInputChannel = null;
            currentOutputChannel = null;
            commandBytes.Clear();
        }

        public bool HasActiveChannel => currentInputChannel.HasValue || currentOutputChannel.HasValue;

        public bool HasInputChannel => currentInputChannel.HasValue;

        public byte Chrin()
        {
            if (!currentInputChannel.HasValue)
                return 0;

            return ReadChannelByte(currentInputChannel.Value);
        }

        public bool Chrout(byte value)
        {
            if (!currentOutputChannel.HasValue)
                return false;

            if (currentOutputChannel == 15)
                commandBytes.Add(value);
            return true;
        }

        public void FlushOutput()
        {
            if (currentOutputChannel == 15 && commandBytes.Count > 0)
            {
                ExecuteDriveCommand(DecodePetscii(commandBytes));
                commandBytes.Clear();
            }
        }

        public byte BuildExternalCia2PortA(byte baseExternal)
        {
            bool dataHigh = hostDataRelease && devDataRelease;
            bool clockHigh = hostClockRelease && devClockRelease;
            bool atnHigh = hostAtnRelease;

            byte ext = baseExternal;
            ext = dataHigh ? (byte)(ext | 0x20) : (byte)(ext & ~0x20);
            ext = clockHigh ? (byte)(ext | 0x10) : (byte)(ext & ~0x10);
            ext = atnHigh ? (byte)(ext | 0x08) : (byte)(ext & ~0x08);

            return ext;
        }

        public void Talk(byte dev)
        {
            currentTalker = NormalizeDevice(dev);
            currentListener = null;
            talkSecondary = null;
            devDataRelease = true;
            devClockRelease = true;
        }

        public void Listen(byte dev)
        {
            currentListener = NormalizeDevice(dev);
            currentTalker = null;
            listenSecondary = null;
            commandBytes.Clear();
            devDataRelease = true;
            devClockRelease = true;
        }

        public void Second(byte sa)
        {
            listenSecondary = (byte)(sa & 0x0F);
            commandBytes.Clear();
        }

        public void Tksa(byte sa)
        {
            talkSecondary = (byte)(sa & 0x0F);
            PrepareTalkBuffer(talkSecondary.Value);
        }

        public void Ciout(byte value)
        {
            if (currentListener != 8)
                return;
            commandBytes.Add(value);
        }

        public byte Acptr()
        {
            if (currentTalker != 8)
                return 0;

            if (talkSecondary.HasValue)
                return ReadChannelByte(talkSecondary.Value);

            if (talkQueue.Count == 0)
                return 0;
            OnDriveActivity?.Invoke();
            return talkQueue.Dequeue();
        }

        public void Unlisten()
        {
            if (currentListener == 8 && commandBytes.Count > 0)
            {
                string text = DecodePetscii(commandBytes);
                if (listenSecondary == 15)
                    ExecuteDriveCommand(text);
                else
                    pendingFilename = text;
            }
            currentListener = null;
            listenSecondary = null;
            commandBytes.Clear();
        }

        public void Untalk()
        {
            currentTalker = null;
            talkSecondary = null;
            talkQueue.Clear();
            statusQueue.Clear();
            devDataRelease = true;
            devClockRelease = true;
        }

        private byte ReadChannelByte(byte channel)
        {
            if (channel == 15)
            {
                if (statusQueue.Count == 0)
                    PrepareStatusBuffer();
                return statusQueue.Count == 0 ? (byte)0 : statusQueue.Dequeue();
            }

            if (directChannels.TryGetValue(channel, out DirectChannel? directChannel))
            {
                OnDriveActivity?.Invoke();
                return directChannel.ReadByte();
            }

            if (talkQueue.Count == 0)
                return 0;

            OnDriveActivity?.Invoke();
            return talkQueue.Dequeue();
        }

        public bool TryLoadFromDrive(out byte[] prg, out string resolvedName)
        {
            string? requested = pendingFilename;
            pendingFilename = null;
            bool ok = drive.TryLoadPrg(requested, out prg, out resolvedName);
            if (ok)
                OnDriveActivity?.Invoke();
            return ok;
        }

        public bool TryLoadFromDrive(string? requestedName, out byte[] prg, out string resolvedName)
        {
            bool ok = drive.TryLoadPrg(requestedName, out prg, out resolvedName);
            if (ok)
                OnDriveActivity?.Invoke();
            return ok;
        }

        private void PrepareTalkBuffer(byte channel)
        {
            talkQueue.Clear();
            if (currentTalker != 8)
                return;

            if (channel == 15)
            {
                PrepareStatusBuffer();
                return;
            }

            if (directChannels.ContainsKey(channel))
                return;

            // Secondary addr for LOAD/TALK data channels typically includes low nibble channel.
            if (!drive.TryLoadPrg(pendingFilename, out byte[] prg, out _))
                return;

            OnDriveActivity?.Invoke();
            talkQueue = new Queue<byte>(prg);
            devDataRelease = true;
            devClockRelease = true;
        }

        private void PrepareStatusBuffer()
        {
            statusQueue = new Queue<byte>(Encoding.ASCII.GetBytes(driveStatus + "\r"));
            driveStatus = "00, OK,00,00";
        }

        private void ExecuteDriveCommand(string command)
        {
            string normalized = command.Trim().Trim('"', '\'').ToUpperInvariant();
            if (normalized.Length == 0 || normalized == "I" || normalized == "UI")
            {
                SetDriveOk();
                return;
            }

            if (normalized.StartsWith("U1:", StringComparison.Ordinal) ||
                normalized.StartsWith("UA:", StringComparison.Ordinal))
            {
                ExecuteBlockRead(normalized.Substring(3));
                return;
            }

            if (normalized.StartsWith("B-P:", StringComparison.Ordinal))
            {
                ExecuteBufferPointer(normalized.Substring(4));
                return;
            }

            SetDriveStatus(30, "SYNTAX ERROR", 0, 0);
        }

        private void ExecuteBlockRead(string args)
        {
            int[] values = ParseDriveCommandNumbers(args);
            if (values.Length < 4)
            {
                SetDriveStatus(30, "SYNTAX ERROR", 0, 0);
                return;
            }

            byte channel = (byte)(values[0] & 0x0F);
            int track = values[2];
            int sector = values[3];
            if (!drive.TryReadSector(track, sector, out byte[] sectorBytes))
            {
                SetDriveStatus(66, "ILLEGAL TRACK OR SECTOR", track, sector);
                return;
            }

            directChannels[channel] = new DirectChannel(sectorBytes);
            OnDriveActivity?.Invoke();
            SetDriveOk(track, sector);
        }

        private void ExecuteBufferPointer(string args)
        {
            int[] values = ParseDriveCommandNumbers(args);
            if (values.Length < 2 || !directChannels.TryGetValue((byte)(values[0] & 0x0F), out DirectChannel? channel))
            {
                SetDriveStatus(70, "NO CHANNEL", 0, 0);
                return;
            }

            channel.Position = Math.Clamp(values[1], 0, 255);
            SetDriveOk();
        }

        private static int[] ParseDriveCommandNumbers(string args)
        {
            string[] parts = args.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var values = new List<int>(parts.Length);
            foreach (string part in parts)
            {
                if (int.TryParse(part, out int value))
                    values.Add(value);
            }
            return values.ToArray();
        }

        private void SetDriveOk(int track = 0, int sector = 0)
        {
            SetDriveStatus(0, "OK", track, sector);
        }

        private void SetDriveStatus(int code, string message, int track, int sector)
        {
            driveStatus = $"{code:00}, {message},{track:00},{sector:00}";
            statusQueue.Clear();
        }

        private static string DecodePetscii(List<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Count);
            foreach (byte b in bytes)
            {
                if (b == 0x00 || b == 0x0D)
                    break;
                if (b >= 0x20 && b <= 0x7E)
                    sb.Append((char)b);
            }
            return sb.ToString().Trim().Trim('"', '\'');
        }

        private static byte NormalizeDevice(byte dev)
        {
            return (byte)(dev & 0x1F);
        }

        private sealed class DirectChannel
        {
            private readonly byte[] data;

            public DirectChannel(byte[] data)
            {
                this.data = data;
            }

            public int Position { get; set; }

            public byte ReadByte()
            {
                if (Position < 0 || Position >= data.Length)
                    return 0;
                return data[Position++];
            }
        }

        private void StepLowLevelResponder()
        {
            if (currentListener == 8 || currentTalker == 8)
            {
                prevHostClockRelease = hostClockRelease;
                prevHostAtnRelease = hostAtnRelease;
                return;
            }

            bool drivePresent = drive.HasMedia || hostLooseProgramPresent;
            if (!drivePresent)
            {
                devDataRelease = true;
                devClockRelease = true;
                prevHostClockRelease = hostClockRelease;
                prevHostAtnRelease = hostAtnRelease;
                lowLevelDataHoldTicks = 0;
                lowLevelClockHoldTicks = 0;
                lowLevelBytePhase = 0;
                return;
            }

            bool atnFalling = prevHostAtnRelease && !hostAtnRelease;
            bool clockRising = !prevHostClockRelease && hostClockRelease;

            // Device presence pulse on ATN assert.
            if (atnFalling)
            {
                lowLevelClockHoldTicks = Math.Max(lowLevelClockHoldTicks, 10);
                lowLevelBytePhase = 0;
                lowLevelCurrentByte = 0xA5;  // Dummy byte for byte-phase testing
            }

            // While ATN is asserted, we're in presence/handshake or byte-phase
            if (!hostAtnRelease)
            {
                // Device keeps DATA pulled low continuously (ready signal while ATN held)
                lowLevelDataHoldTicks = 10;

                // Activate byte-phase immediately on ATN assert
                if (lowLevelBytePhase == 0)
                    lowLevelBytePhase = 1;

                // Byte-phase: respond to CLOCK strobes with data bits (ATN still held low)
                if (lowLevelBytePhase >= 1 && clockRising)
                {
                    int bitIndex = (lowLevelBytePhase - 1) % 8;
                    bool bitValue = ((lowLevelCurrentByte >> bitIndex) & 1) != 0;

                    // Pull DATA low if bit is 0, release if bit is 1
                    if (!bitValue)
                        lowLevelDataHoldTicks = Math.Max(lowLevelDataHoldTicks, 5);

                    lowLevelBytePhase++;

                    // After 8 bits, reset for next byte (or signal completion)
                    if ((lowLevelBytePhase - 1) % 8 == 7)
                        lowLevelBytePhase = 1;  // Ready for next byte
                }
            }
            else
            {
                // ATN released: reset to handshake mode
                lowLevelBytePhase = 0;
            }

            if (lowLevelDataHoldTicks > 0)
            {
                devDataRelease = false;
                lowLevelDataHoldTicks--;
            }
            else
            {
                devDataRelease = true;
            }

            if (lowLevelClockHoldTicks > 0)
            {
                devClockRelease = false;
                lowLevelClockHoldTicks--;
            }
            else
            {
                devClockRelease = true;
            }

            prevHostClockRelease = hostClockRelease;
            prevHostAtnRelease = hostAtnRelease;
        }
    }
}
