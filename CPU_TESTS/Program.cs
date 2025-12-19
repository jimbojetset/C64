// Copyright (c) 2025 James Booth
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using _6502CPU;
using JsonSerializer = System.Text.Json.JsonSerializer;

_6502_CPU cpu = new _6502_CPU();

// https://github.com/SingleStepTests/65x02/blob/main/6502/v1/28.json

Dictionary<string, string[]> testDictionary = new Dictionary<string, string[]>();

testDictionary.Add("NOP_Test", ["ea",]);
testDictionary.Add("LD_Tests", ["a9", "ad", "bd", "b9", "a5", "b5", "a1", "b1", "a2", "ae", "be", "a6", "b6", "a0", "ac", "bc", "a4", "b4"]);
testDictionary.Add("ST_Tests", ["8d", "9d", "99", "85", "95", "81", "91", "8e", "86", "96", "8c", "84", "94"]);
testDictionary.Add("T__Tests", ["aa", "a8", "ba", "8a", "9a", "98"]);
testDictionary.Add("SE_Tests", ["38", "f8", "78"]);
testDictionary.Add("PH_Tests", ["48", "08"]);
testDictionary.Add("PL_Tests", ["68", "28"]);
testDictionary.Add("CL_Tests", ["18", "d8", "58", "b8"]);
testDictionary.Add("DE_Tests", ["ce", "de", "c6", "d6", "ca","88"]);
testDictionary.Add("IX_Tests", ["ee", "fe", "e6", "f6", "e8", "c8"]);
testDictionary.Add("CM_Tests", ["c9", "cd", "dd", "d9", "c5", "d5", "c1", "d1"]);
testDictionary.Add("CP_Tests", ["e0", "ec", "e4", "c0", "cc", "c4"]);
testDictionary.Add("ADC_Tests", ["69","6d","7d","79","65","75","61","71"]);
testDictionary.Add("SBC_Tests", ["e9","ed","fd","f9","e5","f5","e1","f1"]);
testDictionary.Add("EOR_Tests", ["49","4d","5d","59","45","55","41","51"]);
testDictionary.Add("ORA_Tests", ["09", "0d", "1d", "19", "05", "15", "01", "11"]);
testDictionary.Add("AND_Tests", ["29", "2d", "3d", "39", "25", "35", "21", "31"]);
testDictionary.Add("BIT_Tests", ["2c", "24"]);
testDictionary.Add("ASL_Tests", ["0a", "0e", "1e", "06", "16"]);
testDictionary.Add("LSR_Tests", ["4a", "4e", "5e", "46", "56"]);
testDictionary.Add("ROL_Tests", ["2a", "2e", "3e", "26", "36"]);
testDictionary.Add("ROR_Tests", ["6a", "6e", "7e", "66", "76"]);
testDictionary.Add("BRANCH_Tests", ["10","00","90", "b0", "f0", "30", "d0",  "50", "70"]);
testDictionary.Add("J__Tests", ["4c", "6c", "20"]);
testDictionary.Add("RT_Tests", ["40", "60"]);

// Undocumented Opcodes
testDictionary.Add("LAX_Tests", ["a7", "b7", "af", "bf", "a3", "b3"]);
testDictionary.Add("SAX_Tests", ["87", "97", "8f", "83"]);
testDictionary.Add("DCP_Tests", ["c7", "d7", "cf", "df", "db", "c3", "d3"]);
testDictionary.Add("ISC_Tests", ["e7", "f7", "ef", "ff", "fb", "e3", "f3"]);
testDictionary.Add("SLO_Tests", ["07", "17", "0f", "1f", "1b", "03", "13"]);
testDictionary.Add("RLA_Tests", ["27", "37", "2f", "3f", "3b", "23", "33"]);
testDictionary.Add("SRE_Tests", ["47", "57", "4f", "5f", "5b", "43", "53"]);
testDictionary.Add("RRA_Tests", ["67", "77", "6f", "7f", "7b", "63", "73"]);
testDictionary.Add("NOP_Undoc_Tests", ["1a", "3a", "5a", "7a", "da", "fa", "04", "44", "64", "14", "34", "54", "74", "d4", "f4", "0c", "1c", "3c", "5c", "7c", "dc", "fc", "80", "82", "89", "c2", "e2"]);
testDictionary.Add("SBC_Undoc_Tests", ["eb"]);
testDictionary.Add("ANC_Tests", ["0b", "2b"]);
testDictionary.Add("ALR_Tests", ["4b"]);
testDictionary.Add("AXS_Tests", ["cb"]);
testDictionary.Add("LAS_Tests", ["bb"]); 

// More Undocumented Opcodes that are implemented but are unstable and will fail.
// These test failures are NOT bugs - they reflect authentic hardware behavior variability. 
// Different 6502 chips (NMOS variants, different manufacturers) genuinely produce different 
// results for these opcodes. When modifying these implementations, understand that perfect 
// test pass rates are impossible. Therefore these tests are disabled by default
// testDictionary.Add("ARR_Tests", ["6b"]); 
// testDictionary.Add("XAA_Tests", ["8b"]); 
// testDictionary.Add("AHX_Tests", ["9f", "93"]); 
// testDictionary.Add("SHY_Tests", ["9c"]); 
// testDictionary.Add("SHX_Tests", ["9e"]); 
// testDictionary.Add("TAS_Tests", ["9b"]); 

int testCount = 0;
int testCountTotal = 0;
int opcodes = 0;
int success = 0;
int failure = 0;
int totalSuccess = 0;
int totalFailure = 0;
int totalOpcodeCount = 0;

foreach (KeyValuePair<string, string[]> testPlan in testDictionary)
    foreach (string test in testPlan.Value)
        totalOpcodeCount++;

Console.WriteLine("Starting Tests...");
var watch = System.Diagnostics.Stopwatch.StartNew();
string failedOpcodes = "";
foreach (KeyValuePair<string, string[]> testPlan in testDictionary)
{
    foreach (string test in testPlan.Value)
    {
        opcodes++;

        Console.Write("\r{0}   ", "Opcode " + opcodes + " of " + totalOpcodeCount);

        using HttpClient client = new HttpClient();
        string testData = await client.GetStringAsync("https://raw.githubusercontent.com/SingleStepTests/65x02/main/6502/v1/" + test + ".json");

        List<Data>? testList = JsonSerializer.Deserialize<List<Data>>(testData);

        ulong outPC = 0;
        ulong outS = 0;
        ulong outP = 0;
        ulong outA = 0;
        ulong outX = 0;
        ulong outY = 0;
        List<List<int>>? ram = new List<List<int>>();

        testCount = 0;
        success = 0;
        failure = 0;

        foreach (Data? data in testList!)
        {
            testCount++;
            testCountTotal++;

            // prime the CPU
            cpu.registers = new Registers();
            cpu.registers.Clear();
            cpu.memory = new Memory(0x10000);

            // load the CPU
            cpu.registers.PC = data.initial!.pc;
            cpu.registers.P = data.initial!.p;
            cpu.registers.A = data.initial!.a;
            cpu.registers.X = data.initial!.x;
            cpu.registers.Y = data.initial!.y;
            cpu.registers.S = data.initial!.s;
            foreach (List<int> ramData in data.initial.ram!)
                cpu.memory.WriteByte((ulong)ramData[0], (byte)ramData[1]);

            // assertion values
            outPC = data.final!.pc;
            outP = data.final!.p;
            outA = data.final!.a;
            outX = data.final!.x;
            outY = data.final!.y;
            outS = data.final!.s;
            ram = data.final!.ram;

            // execute a single instruction
            cpu.GetNextInstruction();
            cpu.Execute((byte)Convert.ToByte(test, 16));

            bool pass = true;
            foreach (List<int> ramData in ram!)
                if (ramData[1] != cpu.memory.ReadByte((ulong)ramData[0]))
                    pass = false;

            // check asserted values
            if (outPC != cpu.registers.PC ||
                 outP != cpu.registers.P ||
                 outA != cpu.registers.A ||
                 outX != cpu.registers.X ||
                 outY != cpu.registers.Y ||
                 outS != cpu.registers.S ||
                 pass != true)

            {

                //if (!pass)
                //{
                //    Console.WriteLine();
                //    foreach (List<int> ramData in ram!)
                //    {
                //        Console.WriteLine(ramData[0] + " " + ramData[1] + " " + cpu.memory.ReadByte((ulong)ramData[0]));
                //    }
               // }

                if (!failedOpcodes.Contains(test))
                    failedOpcodes += test + " ";
                failure++;
                totalFailure++;
            }
            else
            {
                success++;
                totalSuccess++;
            }
        }
    }
}
watch.Stop();
Console.WriteLine();
Console.WriteLine("Total Tests Run: " + testCountTotal);
Console.WriteLine("Total Pass: " + totalSuccess + " tests");
if(totalFailure == 0)
    Console.WriteLine("All Opcode Tests Passed!");
else
{
    Console.WriteLine("Total Fail: " + totalFailure + " tests");
    Console.WriteLine("Failed Opcodes = " + failedOpcodes.ToUpper());
}
Console.WriteLine("Time Taken: " + watch.ElapsedMilliseconds / 1000 + " Seconds");










internal class Data
{
    public string? name { get; set; }
    public Initial? initial { get; set; }
    public Final? final { get; set; }
    public List<List<object>>? cycles { get; set; }
}

internal class Final
{
    public ulong pc { get; set; }
    public byte s { get; set; }
    public byte a { get; set; }
    public byte x { get; set; }
    public byte y { get; set; }
    public byte p { get; set; }
    public List<List<int>>? ram { get; set; }
}

internal class Initial
{
    public ulong pc { get; set; }
    public byte s { get; set; }
    public byte a { get; set; }
    public byte x { get; set; }
    public byte y { get; set; }
    public byte p { get; set; }
    public List<List<int>>? ram { get; set; }
}

