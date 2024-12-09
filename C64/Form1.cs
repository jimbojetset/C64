using _6502CPU;
using System;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Diagnostics;
using System.Text;

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
                        //if (x >= _width || y >= _height)
                        sb.Append(current[x, y]);
                    }
                    sb.Append("\r\n");
                }
                textBox1.Invoke((MethodInvoker)delegate { textBox1.Text = sb.ToString(); });
                panel1.Invoke((MethodInvoker)delegate { panel1.Refresh(); ; });
                this.Invoke((MethodInvoker)delegate { this.Refresh(); ; });
                Thread.Sleep(16);
            }
        }

        private char[,] GetCurrentState()
        {
            cpu.InterruptRequest();
            var result = new char[_width, _height];
            var currentAdr = _screenStartAddress;
            for (byte x = 0; x < _width; x++)
            {
                for (byte y = 0; y < _height; y++)
                {
                    result[x, y] = C64CharConverter.ConvertToAscii(cpu.memory.ReadByte(currentAdr++));
                    //result[x, y] = Encoding.ASCII.GetString(new byte[] { cpu.memory.ReadByte(currentAdr++) })[0];
                }
            }
            return result;

        }

        private void button1_Click(object sender, EventArgs e)
        {
            cpu = new _6502_CPU();
            cpu.memory.Load(@"ROMS\BASIC.ROM", 0xA000, 8192);
            cpu.memory.Load(@"ROMS\KERNAL.ROM", 0xE000, 8192);
            cpu.memory.Load(@"ROMS\CHAR.ROM", 0xD000, 4096);
            //cpu.memory.Load(@"ROMS\C1541.ROM", 123, 16384);

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

        private void Form1_Load(object sender, EventArgs e)
        {

        }
    }
}
