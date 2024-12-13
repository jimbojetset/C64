using _6502CPU;
using System;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

namespace C64
{
    public partial class Form1 : Form
    {

        private byte _width = 40;
        private byte _height = 25;
        private _6502_CPU cpu;
        private ushort _screenStartAddress = 0x400;

        public Form1()
        {
            InitializeComponent();

            cpu = new _6502_CPU();
            cpu.memory.Load(@"ROMS\BASIC.ROM", 0xA000, 8192, true);
            cpu.memory.Load(@"ROMS\KERNAL.ROM", 0xE000, 8192, true);
            cpu.memory.Load(@"ROMS\CHAR.ROM", 0xD000, 4096, false);

            var processorThread = new Thread(() => cpu.Run())
            {
                IsBackground = true
            };
            processorThread.Start();
            var runThread = new Thread(() => Run())
            {
                IsBackground = true
            };
            runThread.Start();
        }

        private void Run()
        {
            while (true)
            {
                var current = GetCurrentState();
                StringBuilder sb = new StringBuilder();
                for (byte y = 0; y < _height; y += 1)
                {
                    for (byte x = 0; x < _width; x += 1)
                    {
                        if (x >= _width || y >= _height) continue;
                        sb.Append(current[x, y]);
                    }
                    sb.Append("\r\n");
                }
                textBox1.Invoke((MethodInvoker)delegate { textBox1.Text = sb.ToString(); });
                cpu.InterruptRequest();
                Thread.Sleep(16);
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

        [DllImport("user32.dll")]
        static extern bool HideCaret(IntPtr hWnd);
        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            HideCaret(textBox1.Handle);
        }
    }
}
