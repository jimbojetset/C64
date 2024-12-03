using _6502CPU;
using Microsoft.Win32;
using System;
using static System.Net.Mime.MediaTypeNames;
using JsonSerializer = System.Text.Json.JsonSerializer;

_6502_CPU cpu = new _6502_CPU();

string tests = LoadJson("ad");  

List<Test>? testList = JsonSerializer.Deserialize<List<Test>> (tests);

ulong outPC = 0;
ulong outS = 0;
ulong outP = 0;
ulong outA = 0;
ulong outX = 0;
ulong outY = 0;
List<List<int>>? ram = new List<List<int>> ();

foreach (Test? test in testList!)
{
    cpu.registers = new Registers();
    cpu.registers.Clear();
    cpu.memory = new RAM(0x10000);

    cpu.registers.PC = test.initial!.pc;
    cpu.registers.Flags.SetFlagsFromByte(test.initial!.p);
    cpu.registers.A = test.initial!.a;
    cpu.registers.X = test.initial!.x;
    cpu.registers.Y = test.initial!.y;
    cpu.registers.S = test.initial!.s;
    foreach (List<int> data in test.initial.ram!)
        cpu.memory.WriteByte((ulong)data[0], (byte)data[1]);

    outPC = test.final!.pc;
    outP = test.final!.p;
    outA = test.final!.a;
    outX = test.final!.x;
    outY = test.final!.y;
    outS = test.final!.s;
    ram = test.final!.ram;

    if(test.name == "ad b3 c7")
    { }

    cpu.Execute();

    if (outPC == cpu.registers.PC &&
        outP == cpu.registers.Flags.GetFlagsAsByte() &&
        outA == cpu.registers.A &&
        outX == cpu.registers.X &&
        outY == cpu.registers.Y &&
        outS == cpu.registers.S)
    {
        Console.WriteLine("Pass test:" + test.name);
    }
    else
    {
        Console.WriteLine("FAIL test:" + test.name);
        Console.ReadLine();
    
    }
}








string LoadJson(string jsonFile)
{
    using var client = new HttpClient();
    var content = client.GetStringAsync(@"https://raw.githubusercontent.com/SingleStepTests/65x02/refs/heads/main/6502/v1/" + jsonFile + ".json").Result;
    return content;
}

internal class Test
{
    public string? name { get; set; }
    public Initial? initial { get; set; }
    public Final? final { get; set; }
    public List<List<object>>? cycles { get; set; }
}

internal class Final
{
    public ulong pc { get; set; }
    public ulong s { get; set; }
    public byte a { get; set; }
    public byte x { get; set; }
    public byte y { get; set; }
    public byte p { get; set; }
    public List<List<int>>? ram { get; set; }
}

internal class Initial
{
    public ulong pc { get; set; }
    public ulong s { get; set; }
    public byte a { get; set; }
    public byte x { get; set; }
    public byte y { get; set; }
    public byte p { get; set; }
    public List<List<int>>? ram { get; set; }
}

