using _6502CPU;
using JsonSerializer = System.Text.Json.JsonSerializer;


_6502_CPU cpu = new _6502_CPU();

// https://github.com/SingleStepTests/65x02/blob/main/6502/v1/28.json

Dictionary<string, string[]> testDictionary = new Dictionary<string, string[]>
{
    { "NOP_Test", ["ea",] },
    { "LD_Tests", ["a9", "ad", "bd", "b9", "a5", "b5", "a1", "b1", "a2", "ae", "be", "a6", "b6", "a0", "ac", "bc", "a4", "b4"] },
    { "ST_Tests", ["8d", "9d", "99", "85", "95", "81", "91", "8e", "86", "96", "8c", "84", "94"] },
    { "T__Tests", ["aa", "a8", "ba", "8a", "9a", "98"] },
    { "SE_Tests", ["38", "f8", "78"] },
    { "PH_Tests", ["48", "08"] },
    { "PL_Tests", ["68", "28"] },
    { "CL_Tests", ["18", "d8", "58", "b8"] },
    { "DE_Tests", ["ce", "de", "c6", "d6", "ca", "88"] },
    { "IX_Tests", ["ee", "fe", "e6", "f6", "e8", "c8"] },
    { "CM_Tests", ["c9", "cd", "dd", "d9", "c5", "d5", "c1", "d1"] },
    { "CP_Tests", ["e0", "ec", "e4", "c0", "cc", "c4"] },
    { "ADC_Tests", ["69", "6d", "7d", "79", "65", "75", "61", "71"] },
    { "SBC_Tests", ["e9", "ed", "fd", "f9", "e5", "f5", "e1", "f1"] },
    { "EOR_Tests", ["49", "4d", "5d", "59", "45", "55", "41", "51"] },
    { "ORA_Tests", ["09", "0d", "1d", "19", "05", "15", "01", "11"] },
    { "AND_Tests", ["29", "2d", "3d", "39", "25", "35", "21", "31"] },
    { "BIT_Tests", ["2c", "24"] },
    { "ASL_Tests", ["0a", "0e", "1e", "06", "16"] },
    { "LSR_Tests", ["4a", "4e", "5e", "46", "56"] },
    { "ROL_Tests", ["2a", "2e", "3e", "26", "36"] },
    { "ROR_Tests", ["6a", "6e", "7e", "66", "76"] },
    { "BRANCH_Tests", ["10", "00", "90", "b0", "f0", "30", "d0", "50", "70"] },
    { "J__Tests", ["4c", "6c", "20"] },
    { "RT_Tests", ["40", "60"] }
};


//testDictionary.Add("Test", ["00",]);

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

        string testData = File.ReadAllText(@"D:\6502\v1\" + test + ".json");

        List<Data>? testList = JsonSerializer.Deserialize<List<Data>>(testData);

        ulong outPC = 0;
        ulong outS = 0;
        ulong outP = 0;
        ulong outA = 0;
        ulong outX = 0;
        ulong outY = 0;
        List<List<int>>? ram = [];

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
            cpu.GetNextByteInstruction();
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
Console.WriteLine("Total Fail: " + totalFailure + " tests");
Console.WriteLine("Failed Opcodes = " + failedOpcodes.ToUpper());
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

