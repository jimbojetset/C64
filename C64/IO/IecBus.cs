

using System.Text;

namespace C64.IO
{

    internal sealed class IecBus
    {
        private readonly VirtualDrive1541 drive;
        private readonly Drive1541Emulator? fullDrive;

        private static readonly bool LowLevelEnabled =
            string.Equals(Environment.GetEnvironmentVariable("C64_IEC_LOWLEVEL"), "1", StringComparison.Ordinal);

        private bool hostDataRelease = true;

        private bool hostClockRelease = true;
        private bool hostAtnRelease = true;
        private byte hostPortLatch = 0xFF;
        private byte hostDataDirection = 0x00;

        private bool devDataRelease = true;
        private bool devClockRelease = true;

        private byte? currentListener;
        private byte? currentTalker;
        private byte? listenSecondary;
        private byte? talkSecondary;
        private readonly List<byte> commandBytes = [];
        private Queue<byte> talkQueue = new();
        private Queue<byte> statusQueue = new();
        private readonly Dictionary<byte, DirectChannel> directChannels = [];
        private readonly Dictionary<byte, byte> logicalChannels = [];
        private byte? currentInputChannel;
        private byte? currentOutputChannel;

        private string? pendingFilename;
        private bool hostLooseProgramPresent;
        private string driveStatus = "00, OK,00,00";

        private bool prevHostClockRelease = true;
        private bool prevHostAtnRelease = true;
        private int lowLevelDataHoldTicks;
        private int lowLevelClockHoldTicks;
        private int lowLevelBytePhase;
        private byte lowLevelCurrentByte;
        private bool lowLevelActivityReported;

        /// <summary>Creates the IEC protocol state for virtual drive 8 and an optional cycle-level 1541.</summary>
        /// <param name="drive">The virtual 1541 drive attached to the IEC bus.</param>
        public IecBus(VirtualDrive1541 drive, Drive1541Emulator? fullDrive = null)
        {
            this.drive = drive;
            this.fullDrive = fullDrive;
        }

        /// <summary>Updates the host-side CIA2 IEC port pins.</summary>
        /// <param name="dd00">The CIA2 port A data latch value.</param>
        /// <param name="dd02">The CIA2 port A data direction register value.</param>
        public void UpdateHostCia2PortA(byte dd00, byte dd02)
        {
            bool oldDataRelease = hostDataRelease;
            bool oldClockRelease = hostClockRelease;
            bool oldAtnRelease = hostAtnRelease;
            hostPortLatch = dd00;
            hostDataDirection = dd02;

            bool outData = (dd02 & 0x20) != 0;
            bool outClock = (dd02 & 0x10) != 0;
            bool outAtn = (dd02 & 0x08) != 0;

            hostDataRelease = !outData || (dd00 & 0x20) == 0;
            hostClockRelease = !outClock || (dd00 & 0x10) == 0;
            hostAtnRelease = !outAtn || (dd00 & 0x08) == 0;
            fullDrive?.UpdateHostLines(hostDataRelease, hostClockRelease, hostAtnRelease);
            if (fullDrive is not null &&
                (oldDataRelease != hostDataRelease || oldClockRelease != hostClockRelease || oldAtnRelease != hostAtnRelease))
            {
                fullDrive.Step(16);
            }

            if (!lowLevelActivityReported &&
                currentListener != 8 &&
                currentTalker != 8 &&
                (oldDataRelease != hostDataRelease || oldClockRelease != hostClockRelease || oldAtnRelease != hostAtnRelease) &&
                (drive.HasMedia || hostLooseProgramPresent))
            {
                lowLevelActivityReported = true;
                fullDrive?.BeginLowLevelActivity();
            }

            if (LowLevelEnabled && fullDrive is null)
                StepLowLevelResponder();
        }

        public byte HostPortLatch => hostPortLatch;

        public byte HostDataDirection => hostDataDirection;

        public bool HasFullDrive => fullDrive is not null;

        /// <summary>Steps the optional full 1541 drive path by the supplied number of elapsed C64 cycles.</summary>
        /// <param name="cycles">The number of C64 CPU cycles that elapsed.</param>
        public void StepDriveCycles(int cycles)
        {
            fullDrive?.Step(cycles);
        }

        /// <summary>Resets the optional full 1541 drive path.</summary>
        public void ResetDrive()
        {
            devDataRelease = true;
            devClockRelease = true;
            currentListener = null;
            currentTalker = null;
            listenSecondary = null;
            talkSecondary = null;
            commandBytes.Clear();
            talkQueue.Clear();
            statusQueue.Clear();
            directChannels.Clear();
            logicalChannels.Clear();
            currentInputChannel = null;
            currentOutputChannel = null;
            pendingFilename = null;
            prevHostClockRelease = hostClockRelease;
            prevHostAtnRelease = hostAtnRelease;
            lowLevelDataHoldTicks = 0;
            lowLevelClockHoldTicks = 0;
            lowLevelBytePhase = 0;
            lowLevelCurrentByte = 0;
            lowLevelActivityReported = false;
            SetDriveOk();
            fullDrive?.Reset();
            fullDrive?.UpdateHostLines(hostDataRelease, hostClockRelease, hostAtnRelease);
        }

        /// <summary>Notifies the optional full 1541 drive path that a D64 image was attached.</summary>
        /// <param name="path">The path of the attached D64 image.</param>
        public void AttachD64(string path)
        {
            ResetDrive();
            fullDrive?.AttachD64(path);
        }

        /// <summary>Ejects attached disk media and resets drive-side IEC state.</summary>
        public void EjectD64()
        {
            drive.Eject();
            ResetDrive();
            fullDrive?.Eject();
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

        /// <summary>Closes a KERNAL logical file, flushing command-channel output and releasing its channel state.</summary>
        /// <param name="logicalFile">The C64 logical file number.</param>
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

        public bool HasActiveChannel => currentInputChannel.HasValue || currentOutputChannel.HasValue;

        public bool HasInputChannel => currentInputChannel.HasValue;

        /// <summary>Reads a byte from the active IEC input channel.</summary>
        public byte Chrin()
        {
            if (!currentInputChannel.HasValue)
                return 0;

            return ReadChannelByte(currentInputChannel.Value);
        }

        /// <summary>Writes a byte to the active IEC output channel.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        public bool Chrout(byte value)
        {
            if (!currentOutputChannel.HasValue)
                return false;

            if (currentOutputChannel == 15)
                commandBytes.Add(value);
            return true;
        }

        /// <summary>Executes queued command-channel bytes when IEC output channel 15 is flushed.</summary>
        public void FlushOutput()
        {
            if (currentOutputChannel == 15 && commandBytes.Count > 0)
            {
                string command = DecodePetscii(commandBytes);
                ExecuteDriveCommand(command);
                commandBytes.Clear();
            }
        }

        /// <summary>Merges open-collector IEC DATA, CLOCK, and ATN line levels into CIA2 port A inputs.</summary>
        /// <param name="baseExternal">The external CIA2 port value before IEC line bits are applied.</param>
        public byte BuildExternalCia2PortA(byte baseExternal)
        {
            bool deviceDataRelease = fullDrive?.DeviceDataRelease ?? devDataRelease;
            bool deviceClockRelease = fullDrive?.DeviceClockRelease ?? devClockRelease;
            bool dataHigh = hostDataRelease && deviceDataRelease;
            bool clockHigh = hostClockRelease && deviceClockRelease;
            bool atnHigh = hostAtnRelease;

            byte ext = baseExternal;
            ext = dataHigh ? (byte)(ext | 0x20) : (byte)(ext & ~0x20);
            ext = clockHigh ? (byte)(ext | 0x10) : (byte)(ext & ~0x10);
            ext = atnHigh ? (byte)(ext | 0x08) : (byte)(ext & ~0x08);
            ext = dataHigh ? (byte)(ext | 0x80) : (byte)(ext & ~0x80);
            ext = clockHigh ? (byte)(ext | 0x40) : (byte)(ext & ~0x40);

            return ext;
        }

        /// <summary>Selects the normalized IEC talker and resets talk-channel handshake state.</summary>
        /// <param name="dev">The IEC device number.</param>
        public void Talk(byte dev)
        {
            currentTalker = NormalizeDevice(dev);
            currentListener = null;
            talkSecondary = null;
            devDataRelease = true;
            devClockRelease = true;
        }

        /// <summary>Selects the normalized IEC listener and clears pending listen-channel command bytes.</summary>
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

        /// <summary>Selects the listener secondary address and starts a fresh command payload.</summary>
        /// <param name="sa">The IEC secondary address.</param>
        public void Second(byte sa)
        {
            listenSecondary = (byte)(sa & 0x0F);
            commandBytes.Clear();
        }

        /// <summary>Selects the talker secondary address and prepares the matching channel data.</summary>
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
        public byte Acptr()
        {
            if (currentTalker != 8)
                return 0;

            if (talkSecondary.HasValue)
                return ReadChannelByte(talkSecondary.Value);

            if (talkQueue.Count == 0)
                return 0;
            return talkQueue.Dequeue();
        }

        /// <summary>Completes the listen transaction, interpreting pending bytes as a filename or drive command.</summary>
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

        /// <summary>Ends the talk transaction and releases queued data, status, and IEC output lines.</summary>
        public void Untalk()
        {
            currentTalker = null;
            talkSecondary = null;
            talkQueue.Clear();
            statusQueue.Clear();
            devDataRelease = true;
            devClockRelease = true;
        }

        /// <summary>Reads the next byte from a status, direct-access, or PRG talk channel.</summary>
        /// <param name="channel">The IEC channel number.</param>
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
                return directChannel.ReadByte();
            }

            if (talkQueue.Count == 0)
                return 0;

            return talkQueue.Dequeue();
        }

        /// <summary>Loads the pending IEC filename from virtual drive 8 and then clears that request.</summary>
        /// <param name="prg">Receives the PRG bytes when the load succeeds.</param>
        /// <param name="resolvedName">Receives the resolved C64 filename when the operation succeeds.</param>
        public bool TryLoadFromDrive(out byte[] prg, out string resolvedName)
        {
            string? requested = pendingFilename;
            pendingFilename = null;
            bool ok = drive.TryLoadPrg(requested, out prg, out resolvedName);
            return ok;
        }

        /// <summary>Loads the explicitly requested filename from virtual drive 8.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <param name="prg">Receives the PRG bytes when the load succeeds.</param>
        /// <param name="resolvedName">Receives the resolved C64 filename when the operation succeeds.</param>
        public bool TryLoadFromDrive(string? requestedName, out byte[] prg, out string resolvedName)
        {
            bool ok = drive.TryLoadPrg(requestedName, out prg, out resolvedName);
            return ok;
        }

        /// <summary>Queues PRG or status bytes for the selected drive talk channel.</summary>
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

            if (!drive.TryLoadPrg(pendingFilename, out byte[] prg, out _))
            {
                return;
            }

            talkQueue = new Queue<byte>(prg);
            devDataRelease = true;
            devClockRelease = true;
        }

        /// <summary>Queues the current Commodore DOS status line and resets the next status to OK.</summary>
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

        /// <summary>Implements a U1 or UA command by copying a D64 sector into a direct-access channel.</summary>
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
            SetDriveOk(track, sector);
        }

        /// <summary>Implements a B-P command by moving a direct-access channel cursor within its 256-byte buffer.</summary>
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
        private static int[] ParseDriveCommandNumbers(string args)
        {
            var values = new List<int>();
            int index = 0;
            while (index < args.Length)
            {
                while (index < args.Length && !char.IsDigit(args[index]))
                    index++;

                if (index >= args.Length)
                    break;

                int value = 0;
                while (index < args.Length && char.IsDigit(args[index]))
                {
                    value = (value * 10) + (args[index] - '0');
                    index++;
                }

                values.Add(value);
            }

            return [.. values];
        }

        /// <summary>Sets the Commodore DOS status channel to an OK response for the supplied track and sector.</summary>
        /// <param name="track">The disk track number.</param>
        /// <param name="sector">The disk sector number.</param>
        private void SetDriveOk(int track = 0, int sector = 0)
        {
            SetDriveStatus(0, "OK", track, sector);
        }

        /// <summary>Formats and stores a Commodore DOS status-channel response.</summary>
        /// <param name="code">The Commodore DOS status code.</param>
        /// <param name="message">The Commodore DOS status message.</param>
        /// <param name="track">The disk track number.</param>
        /// <param name="sector">The disk sector number.</param>
        private void SetDriveStatus(int code, string message, int track, int sector)
        {
            driveStatus = $"{code:00}, {message},{track:00},{sector:00}";
            statusQueue.Clear();
        }

        /// <summary>Decodes printable PETSCII command bytes up to a terminator and trims surrounding quotes.</summary>
        /// <param name="bytes">The PETSCII bytes to decode.</param>
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

        /// <summary>Masks an IEC device byte to its five-bit device number.</summary>
        /// <param name="dev">The IEC device number.</param>
        private static byte NormalizeDevice(byte dev)
        {
            return (byte)(dev & 0x1F);
        }

        private sealed class DirectChannel
        {
            private readonly byte[] data;

            /// <summary>Creates a 256-byte-style direct-access channel over the supplied drive buffer.</summary>
            /// <param name="data">The bytes exposed through the direct channel.</param>
            public DirectChannel(byte[] data)
            {
                this.data = data;
            }

            public int Position { get; set; }

            /// <summary>Returns the next direct-channel byte and advances its bounded cursor.</summary>
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

            if (atnFalling)
            {
                lowLevelClockHoldTicks = Math.Max(lowLevelClockHoldTicks, 10);
                lowLevelBytePhase = 0;
                lowLevelCurrentByte = 0xA5;
            }

            if (!hostAtnRelease)
            {

                lowLevelDataHoldTicks = 10;

                if (lowLevelBytePhase == 0)
                    lowLevelBytePhase = 1;

                if (lowLevelBytePhase >= 1 && clockRising)
                {
                    int bitIndex = (lowLevelBytePhase - 1) % 8;
                    bool bitValue = ((lowLevelCurrentByte >> bitIndex) & 1) != 0;

                    if (!bitValue)
                        lowLevelDataHoldTicks = Math.Max(lowLevelDataHoldTicks, 5);

                    lowLevelBytePhase++;

                    if ((lowLevelBytePhase - 1) % 8 == 7)
                        lowLevelBytePhase = 1;
                }
            }
            else
            {

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
