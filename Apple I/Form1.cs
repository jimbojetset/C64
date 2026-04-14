using _6502CPU;
using System.Text;

namespace Apple_I
{
    public partial class Form1 : Form
    {
        private byte _width = 40;
        private byte _height = 24;
        private _6502_CPU cpu;
        private ushort _screenStartAddress = 0xD012;
        private bool displayRunning = true;
        public bool V_Sync = false;

        public Form1()
        {
            InitializeComponent();

            cpu = new _6502_CPU(1000000);
            cpu.memory.Load(@"ROMS\basic.rom", 0xE000, 0x1000, true);
            cpu.memory.Load(@"ROMS\monitor.rom", 0xFF00, 0x100, true);

            var processorThread = new Thread(() => cpu.Run())
            {
                IsBackground = true
            };
            processorThread.Start();
            while (textBox1.Handle == 0) { }
            var runThread = new Thread(() => Run())
            {
                IsBackground = true
            };
            runThread.Start();

        }

        private void Run()
        {
            displayRunning = true;
            int cnt = 0;
            byte[] register = new byte[1024];
            while (displayRunning)
            {
                register[cnt++] = cpu.memory.ReadByte(_screenStartAddress);
                if (cnt > 1023) cnt = 0;
                //StringBuilder sb = new StringBuilder();
                //textBox1.Invoke((MethodInvoker)delegate { textBox1.Text = sb.ToString(); });
            }
        }



        private char[,] GetCurrentState()
        {
            var result = new char[_width, _height];
            var currentAdr = _screenStartAddress;
            for (byte y = 0; y < _height; y++)
                for (byte x = 0; x < _width; x++)
                    result[x, y] = C64CharConverter.ConvertToAscii(cpu.memory.ReadByte(currentAdr++));
            return result;
        }
    }
}
