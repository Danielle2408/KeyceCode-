using System.Text;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.Write("KEYCE_ECHO_READY>");
Console.Out.Flush();

while (true)
{
    var line = Console.ReadLine();
    if (line is null) return 70;
    if (line == "exit")
    {
        Console.WriteLine("KEYCE_ECHO_EXIT_OK");
        return 23;
    }
    Console.WriteLine("KEYCE_ECHO:" + line);
    Console.Out.Flush();
}
