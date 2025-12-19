// Copyright (c) 2025 James Booth
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

// Debug test helper to understand failing opcodes

using _6502CPU;
using JsonSerializer = System.Text.Json.JsonSerializer;

public class DebugTests
{
    public static async Task TestOpcode(string opcode)
    {
        using HttpClient client = new HttpClient();
        string testData = await client.GetStringAsync($"https://raw.githubusercontent.com/SingleStepTests/65x02/main/6502/v1/{opcode}.json");
        
        List<Data>? testList = JsonSerializer.Deserialize<List<Data>>(testData);
        
        _6502_CPU cpu = new _6502_CPU();
        
        int passCount = 0;
        int failCount = 0;
        
        foreach (var test in testList!)
        {
            // Setup CPU - create new instance for each test
            cpu = new _6502_CPU();
            cpu.registers.PC = test.initial!.pc;
            cpu.registers.S = (byte)test.initial.s;
            cpu.registers.A = (byte)test.initial.a;
            cpu.registers.X = (byte)test.initial.x;
            cpu.registers.Y = (byte)test.initial.y;
            
            // Set flags
            cpu.registers.Flags.C = (test.initial.p & 0x01) != 0;
            cpu.registers.Flags.Z = (test.initial.p & 0x02) != 0;
            cpu.registers.Flags.I = (test.initial.p & 0x04) != 0;
            cpu.registers.Flags.D = (test.initial.p & 0x08) != 0;
            cpu.registers.Flags.B = (test.initial.p & 0x10) != 0;
            cpu.registers.Flags.V = (test.initial.p & 0x40) != 0;
            cpu.registers.Flags.N = (test.initial.p & 0x80) != 0;
            
            // Load RAM
            foreach (var ramEntry in test.initial.ram!)
            {
                cpu.memory.WriteByte((ulong)ramEntry[0], (byte)ramEntry[1]);
            }
            
            // Execute
            byte opcodeValue = cpu.GetNextInstruction();
            cpu.Execute(opcodeValue);
            
            // Check results
            bool passed = true;
            if (cpu.registers.PC != test.final!.pc) { Console.WriteLine($"  FAIL: PC expected {test.final.pc:X4} got {cpu.registers.PC:X4}"); passed = false; }
            if (cpu.registers.A != test.final.a) { Console.WriteLine($"  FAIL: A expected {test.final.a:X2} got {cpu.registers.A:X2}"); passed = false; }
            if (cpu.registers.X != test.final.x) { Console.WriteLine($"  FAIL: X expected {test.final.x:X2} got {cpu.registers.X:X2}"); passed = false; }
            if (cpu.registers.Y != test.final.y) { Console.WriteLine($"  FAIL: Y expected {test.final.y:X2} got {cpu.registers.Y:X2}"); passed = false; }
            if (cpu.registers.S != test.final.s) { Console.WriteLine($"  FAIL: S expected {test.final.s:X2} got {cpu.registers.S:X2}"); passed = false; }
            
            byte expectedP = (byte)test.final.p;
            byte actualP = (byte)(
                (cpu.registers.Flags.C ? 0x01 : 0) |
                (cpu.registers.Flags.Z ? 0x02 : 0) |
                (cpu.registers.Flags.I ? 0x04 : 0) |
                (cpu.registers.Flags.D ? 0x08 : 0) |
                (cpu.registers.Flags.B ? 0x10 : 0) |
                0x20 | // Bit 5 always set
                (cpu.registers.Flags.V ? 0x40 : 0) |
                (cpu.registers.Flags.N ? 0x80 : 0)
            );
            
            if (actualP != expectedP)
            {
                Console.WriteLine($"  FAIL: P expected {expectedP:X2} ({Convert.ToString(expectedP, 2).PadLeft(8, '0')}) got {actualP:X2} ({Convert.ToString(actualP, 2).PadLeft(8, '0')})");
                Console.WriteLine($"    C: {(expectedP & 1) != 0} vs {cpu.registers.Flags.C}");
                Console.WriteLine($"    V: {(expectedP & 0x40) != 0} vs {cpu.registers.Flags.V}");
                passed = false;
            }
            
            if (!passed)
            {
                Console.WriteLine($"Test: {test.name}");
                failCount++;
                if (failCount >= 5) break; // Only show first 5 failures
            }
            else
            {
                passCount++;
            }
        }
        
        Console.WriteLine($"\nOpcode {opcode}: {passCount} passed, {failCount} failed");
    }
}
