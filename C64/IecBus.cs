using System.Text;

namespace C64
{
    internal sealed class IecBus
    {
        private readonly VirtualDrive1541 drive;

        // Open-collector line model; true = released/high, false = driven low.
        private bool hostDataRelease = true;
        private bool hostClockRelease = true;
        private bool hostAtnRelease = true;

        private bool devDataRelease = true;
        private bool devClockRelease = true;

        private byte? currentListener;
        private byte? currentTalker;
        private byte secondary;
        private readonly List<byte> commandBytes = new List<byte>();
        private Queue<byte> talkQueue = new Queue<byte>();

        private string? pendingFilename;

        public IecBus(VirtualDrive1541 drive)
        {
            this.drive = drive;
        }

        public void UpdateHostCia2PortA(byte dd00, byte dd02)
        {
            // CIA2 port A IEC lines (active-low) on bits 5:DATA, 4:CLOCK, 3:ATN.
            bool outData = (dd02 & 0x20) != 0;
            bool outClock = (dd02 & 0x10) != 0;
            bool outAtn = (dd02 & 0x08) != 0;

            hostDataRelease = !outData || (dd00 & 0x20) != 0;
            hostClockRelease = !outClock || (dd00 & 0x10) != 0;
            hostAtnRelease = !outAtn || (dd00 & 0x08) != 0;
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

        public void Listen(byte dev)
        {
            currentListener = dev;
            currentTalker = null;
            commandBytes.Clear();
            devDataRelease = true;
            devClockRelease = true;
        }

        public void Talk(byte dev)
        {
            currentTalker = dev;
            currentListener = null;
            devDataRelease = true;
            devClockRelease = true;
        }

        public void Second(byte sa)
        {
            secondary = sa;
            commandBytes.Clear();
        }

        public void Tksa(byte sa)
        {
            secondary = sa;
            PrepareTalkBuffer();
        }

        public void Ciout(byte value)
        {
            if (currentListener != 8)
                return;
            commandBytes.Add(value);
        }

        public byte Acptr()
        {
            if (currentTalker != 8 || talkQueue.Count == 0)
                return 0;
            return talkQueue.Dequeue();
        }

        public void Unlisten()
        {
            if (currentListener == 8 && commandBytes.Count > 0)
            {
                pendingFilename = DecodePetscii(commandBytes);
            }
            currentListener = null;
            commandBytes.Clear();
        }

        public void Untalk()
        {
            currentTalker = null;
            talkQueue.Clear();
            devDataRelease = true;
            devClockRelease = true;
        }

        public bool TryLoadFromDrive(out byte[] prg, out string resolvedName)
        {
            string? requested = pendingFilename;
            pendingFilename = null;
            return drive.TryLoadPrg(requested, out prg, out resolvedName);
        }

        public bool TryLoadFromDrive(string? requestedName, out byte[] prg, out string resolvedName)
        {
            return drive.TryLoadPrg(requestedName, out prg, out resolvedName);
        }

        private void PrepareTalkBuffer()
        {
            talkQueue.Clear();
            if (currentTalker != 8)
                return;

            // Secondary addr for LOAD/TALK data channels typically includes low nibble channel.
            if (!drive.TryLoadPrg(pendingFilename, out byte[] prg, out _))
                return;

            talkQueue = new Queue<byte>(prg);
            devDataRelease = true;
            devClockRelease = true;
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
    }
}
