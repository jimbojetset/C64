using _6502CPU;
using JsonSerializer = System.Text.Json.JsonSerializer;


_6502_CPU cpu = new _6502_CPU();

// https://github.com/SingleStepTests/65x02/blob/main/6502/v1/28.json

Dictionary<string, string[]> testDictionary = new Dictionary<string, string[]>();
/*
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
testDictionary.Add("ROR_Tests", ["6a", "6e", "7e", "66", "76"]);*/
testDictionary.Add("BRANCH_Tests", ["10","00","90", "b0", "f0", "30", "d0",  "50", "70"]);
//testDictionary.Add("J__Tests", ["4c", "6c", "20"]);
//testDictionary.Add("RT_Tests", ["40", "60"]);


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
foreach (KeyValuePair<string, string[]> testPlan in testDictionary)
{
    foreach (string test in testPlan.Value)
    {
        opcodes++;

        Console.Write("\r{0}   ", "Opcode " + opcodes + " of " + totalOpcodeCount);

        string testData = File.ReadAllText(@"D:\6502\v1\" +test + ".json");

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
            cpu.memory = new RAM(0x10000);

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
            cpu.Execute((byte)Convert.ToByte(test,16));

            // check asserted values
            if (outPC != cpu.registers.PC ||
                 outP != cpu.registers.P ||
                 outA != cpu.registers.A ||
                 outX != cpu.registers.X ||
                 outY != cpu.registers.Y ||
                 outS != cpu.registers.S)
            {
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
Console.WriteLine("Total Fail: " + totalFailure + " tests");
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

