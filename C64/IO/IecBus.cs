using System.Text;

namespace C64
{

    /// <summary>
    /// Emulates the high-level IEC bus commands and a lightweight low-level responder for the virtual 1541 drive path.
    /// </summary>
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

        /// <summary>Initializes a new IecBus instance.</summary>
        /// <param name="drive">The virtual 1541 drive attached to the IEC bus.</param>
        public IecBus(VirtualDrive1541 drive)
        {
            this.drive = drive;
        }

        /// <summary>Gets or sets the callback invoked for drive activity.</summary>
        public Action? OnDriveActivity { get; set; }

        /// <summary>Updates the host-side CIA2 IEC port pins.</summary>
        /// <param name="dd00">The CIA2 port A data latch value.</param>
        /// <param name="dd02">The CIA2 port A data direction register value.</param>
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

        /// <summary>Sets whether a loose host program is available to the drive path.</summary>
        /// <param name="present">Whether a host loose PRG is available to the virtual drive.</param>
        public void SetHostLooseProgramPresent(bool present)
        {
            hostLooseProgramPresent = present;
        }

        /// <summary>
        /// Opens an IEC logical file and prepares command, directory, PRG, status, or direct-access channel state for device 8.
        /// </summary>
        /// <param name="logicalFile">The C64 logical file number.</param>
        /// <param name="device">The IEC device number.</param>
        /// <param name="secondaryAddress">The IEC secondary address.</param>
        /// <param name="name">The C64 filename or display name to use.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
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

        /// <summary>Closes an IEC logical file.</summary>
        /// <param name="logicalFile">The C64 logical file number.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
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

        /// <summary>Selects an IEC logical file for input.</summary>
        /// <param name="logicalFile">The C64 logical file number.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool Chkin(byte logicalFile)
        {
            if (!logicalChannels.TryGetValue(logicalFile, out byte channel))
                return false;

            currentInputChannel = channel;
            if (channel == 15)
                PrepareStatusBuffer();
            return true;
        }

        /// <summary>Selects an IEC logical file for output.</summary>
        /// <param name="logicalFile">The C64 logical file number.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool Chkout(byte logicalFile)
        {
            if (!logicalChannels.TryGetValue(logicalFile, out byte channel))
                return false;

            currentOutputChannel = channel;
            commandBytes.Clear();
            return true;
        }

        /// <summary>Clears the active IEC input and output channels.</summary>
        public void Clrchn()
        {
            currentInputChannel = null;
            currentOutputChannel = null;
            commandBytes.Clear();
        }

        /// <summary>Gets whether either an IEC input or output channel is currently selected.</summary>
        public bool HasActiveChannel => currentInputChannel.HasValue || currentOutputChannel.HasValue;

        /// <summary>Gets whether an IEC input channel is currently selected for reads.</summary>
        public bool HasInputChannel => currentInputChannel.HasValue;

        /// <summary>Reads a byte from the active IEC input channel.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        public byte Chrin()
        {
            if (!currentInputChannel.HasValue)
                return 0;

            return ReadChannelByte(currentInputChannel.Value);
        }

        /// <summary>Writes a byte to the active IEC output channel.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool Chrout(byte value)
        {
            if (!currentOutputChannel.HasValue)
                return false;

            if (currentOutputChannel == 15)
                commandBytes.Add(value);
            return true;
        }

        /// <summary>Flushes pending IEC output command bytes.</summary>
        public void FlushOutput()
        {
            if (currentOutputChannel == 15 && commandBytes.Count > 0)
            {
                ExecuteDriveCommand(DecodePetscii(commandBytes));
                commandBytes.Clear();
            }
        }

        /// <summary>Builds external cia2 port a.</summary>
        /// <param name="baseExternal">The external CIA2 port value before IEC line bits are applied.</param>
        /// <returns>The byte value produced by the operation.</returns>
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

        /// <summary>Sets the current IEC talker device.</summary>
        /// <param name="dev">The IEC device number.</param>
        public void Talk(byte dev)
        {
            currentTalker = NormalizeDevice(dev);
            currentListener = null;
            talkSecondary = null;
            devDataRelease = true;
            devClockRelease = true;
        }

        /// <summary>Sets the current IEC listener device.</summary>
        /// <param name="dev">The IEC device number.</param>
        public void Listen(byte dev)
        {
            currentListener = NormalizeDevice(dev);
            currentTalker = null;
            listenSecondary = null;
            commandBytes.Clear();
            devDataRelease = true;
            devClockRelease = true;
        }

        /// <summary>Sends an IEC secondary address.</summary>
        /// <param name="sa">The IEC secondary address.</param>
        public void Second(byte sa)
        {
            listenSecondary = (byte)(sa & 0x0F);
            commandBytes.Clear();
        }

        /// <summary>Sets the IEC talk secondary address.</summary>
        /// <param name="sa">The IEC secondary address.</param>
        public void Tksa(byte sa)
        {
            talkSecondary = (byte)(sa & 0x0F);
            PrepareTalkBuffer(talkSecondary.Value);
        }

        /// <summary>Sends one byte on the IEC command path.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        public void Ciout(byte value)
        {
            if (currentListener != 8)
                return;
            commandBytes.Add(value);
        }

        /// <summary>Receives one byte from the IEC talker.</summary>
        /// <returns>The byte value produced by the operation.</returns>
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

        /// <summary>Clears the active IEC listener.</summary>
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

        /// <summary>Clears the active IEC talker.</summary>
        public void Untalk()
        {
            currentTalker = null;
            talkSecondary = null;
            talkQueue.Clear();
            statusQueue.Clear();
            devDataRelease = true;
            devClockRelease = true;
        }

        /// <summary>Reads channel byte.</summary>
        /// <param name="channel">The IEC channel number.</param>
        /// <returns>The byte value produced by the operation.</returns>
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

        /// <summary>Attempts to load from drive.</summary>
        /// <param name="prg">Receives the PRG bytes when the load succeeds.</param>
        /// <param name="resolvedName">Receives the resolved C64 filename when the operation succeeds.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool TryLoadFromDrive(out byte[] prg, out string resolvedName)
        {
            string? requested = pendingFilename;
            pendingFilename = null;
            bool ok = drive.TryLoadPrg(requested, out prg, out resolvedName);
            if (ok)
                OnDriveActivity?.Invoke();
            return ok;
        }

        /// <summary>Attempts to load from drive.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <param name="prg">Receives the PRG bytes when the load succeeds.</param>
        /// <param name="resolvedName">Receives the resolved C64 filename when the operation succeeds.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool TryLoadFromDrive(string? requestedName, out byte[] prg, out string resolvedName)
        {
            bool ok = drive.TryLoadPrg(requestedName, out prg, out resolvedName);
            if (ok)
                OnDriveActivity?.Invoke();
            return ok;
        }

        /// <summary>Prepares talk buffer.</summary>
        /// <param name="channel">The IEC channel number.</param>
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

        /// <summary>Prepares status buffer.</summary>
        private void PrepareStatusBuffer()
        {
            statusQueue = new Queue<byte>(Encoding.ASCII.GetBytes(driveStatus + "\r"));
            driveStatus = "00, OK,00,00";
        }

        /// <summary>
        /// Executes supported 1541 command-channel operations and updates drive status for unsupported commands.
        /// </summary>
        /// <param name="command">The drive command string to execute.</param>
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

        /// <summary>Executes block read.</summary>
        /// <param name="args">The command argument text to parse.</param>
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

        /// <summary>Executes buffer pointer.</summary>
        /// <param name="args">The command argument text to parse.</param>
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

        /// <summary>Parses numeric arguments from a drive command.</summary>
        /// <param name="args">The command argument text to parse.</param>
        /// <returns>The numeric arguments parsed from the drive command.</returns>
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

        /// <summary>Sets drive ok.</summary>
        /// <param name="track">The disk track number.</param>
        /// <param name="sector">The disk sector number.</param>
        private void SetDriveOk(int track = 0, int sector = 0)
        {
            SetDriveStatus(0, "OK", track, sector);
        }

        /// <summary>Sets drive status.</summary>
        /// <param name="code">The Commodore DOS status code.</param>
        /// <param name="message">The Commodore DOS status message.</param>
        /// <param name="track">The disk track number.</param>
        /// <param name="sector">The disk sector number.</param>
        private void SetDriveStatus(int code, string message, int track, int sector)
        {
            driveStatus = $"{code:00}, {message},{track:00},{sector:00}";
            statusQueue.Clear();
        }

        /// <summary>Decodes petscii.</summary>
        /// <param name="bytes">The PETSCII bytes to decode.</param>
        /// <returns>The string value produced by the operation.</returns>
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

        /// <summary>Normalizes device.</summary>
        /// <param name="dev">The IEC device number.</param>
        /// <returns>The byte value produced by the operation.</returns>
        private static byte NormalizeDevice(byte dev)
        {
            return (byte)(dev & 0x1F);
        }

        /// <summary>
        /// Holds direct-access channel data and the current read pointer for IEC block operations.
        /// </summary>
        private sealed class DirectChannel
        {
            private readonly byte[] data;

            /// <summary>Initializes a new DirectChannel instance.</summary>
            /// <param name="data">The bytes exposed through the direct channel.</param>
            public DirectChannel(byte[] data)
            {
                this.data = data;
            }

            /// <summary>Gets or sets the current read position.</summary>
            public int Position { get; set; }

            /// <summary>Reads byte.</summary>
            /// <returns>The byte value produced by the operation.</returns>
            public byte ReadByte()
            {
                if (Position < 0 || Position >= data.Length)
                    return 0;
                return data[Position++];
            }
        }

        /// <summary>
        /// Advances the optional bit-level IEC responder that samples ATN/clock transitions and drives data/clock release lines.
        /// </summary>
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
