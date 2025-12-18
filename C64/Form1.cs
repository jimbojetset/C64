// Copyright (c) 2025 James Booth
// All rights reserved.
// This code is the property of James Booth and may not be used, copied, or distributed without permission.

using _6502CPU;
using System;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace C64
{
    public partial class Form1 : Form
    {

        private byte _width = 40;
        private byte _height = 25;
        private _6502_CPU cpu;
        private ushort _screenStartAddress = 0x400;
        private bool displayRunning = true;
        public bool V_Sync = false;

        public Form1()
        {
            InitializeComponent();

            cpu = new _6502_CPU();
            cpu.memory.Load(@"ROMS\BASIC.ROM", 0xA000, 0x2000, true);
            cpu.memory.Load(@"ROMS\KERNAL.ROM", 0xE000, 0x2000, true);
            cpu.memory.Load(@"ROMS\CHAR.ROM", 0xD000, 0x1000, false);

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
            byte cnt = 0xFF;
            while (displayRunning)
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
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
                cpu.memory.WriteByte(0xD012, (byte)(cnt & 0xFF));
                cnt--;
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
