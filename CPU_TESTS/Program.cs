using _6502CPU;
using JsonSerializer = System.Text.Json.JsonSerializer;

_6502_CPU cpu = new _6502_CPU();

string[] LD_Tests = ["a9", "ad", "bd", "b9", "a5", "b5", "a1", "b1", "a2", "ae", "be", "a6", "b6", "a0", "ac", "bc", "a4", "b4"];
string[] ST_Tests = ["8d", "9d", "99", "85", "95", "81", "91", "8e", "86", "96", "8c", "84", "94"];
string[] T_Tests = ["aa", "a8", "ba", "8a", "9a", "98"];
string[] SE_Tests = ["38", "f8", "78"];

foreach (string test in SE_Tests)
{
    string testData = LoadJson(test);

    List<Data>? testList = JsonSerializer.Deserialize<List<Data>>(testData);

    ulong outPC = 0;
    ulong outS = 0;
    ulong outP = 0;
    ulong outA = 0;
    ulong outX = 0;
    ulong outY = 0;
    List<List<int>>? ram = new List<List<int>>();

    Console.WriteLine("Running Tests On: " + test);

    bool pass = true;

    foreach (Data? data in testList!)
    {

        cpu.registers = new Registers();
        cpu.registers.Clear();
        cpu.memory = new RAM(0x10000);

        cpu.registers.PC = data.initial!.pc;
        cpu.registers.Flags.SetFlagsFromByte(data.initial!.p);
        cpu.registers.A = data.initial!.a;
        cpu.registers.X = data.initial!.x;
        cpu.registers.Y = data.initial!.y;
        cpu.registers.S = data.initial!.s;
        foreach (List<int> ramData in data.initial.ram!)
            cpu.memory.WriteByte((ulong)ramData[0], (byte)ramData[1]);

        outPC = data.final!.pc;
        outP = data.final!.p;
        outA = data.final!.a;
        outX = data.final!.x;
        outY = data.final!.y;
        outS = data.final!.s;
        ram = data.final!.ram;

        cpu.Execute();

        if (outPC != cpu.registers.PC ||
             outP != cpu.registers.Flags.GetFlagsAsByte() ||
             outA != cpu.registers.A ||
             outX != cpu.registers.X ||
             outY != cpu.registers.Y ||
             outS != cpu.registers.S)
        {
            Console.WriteLine("FAILED Test: " + data.name);
            pass = false;
        }
    }
    Console.WriteLine("Pass: " + pass);
}








string LoadJson(string jsonFile)
{
    using var client = new HttpClient();
    var content = client.GetStringAsync(@"https://raw.githubusercontent.com/SingleStepTests/65x02/refs/heads/main/6502/v1/" + jsonFile + ".json").Result;
    return content;
}

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

