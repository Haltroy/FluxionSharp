using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using FluxionSharp;

namespace FluxionTest;

public static class Program
{
    public static void Main(string[] args)
    {
        var test = "This is a test.";
        var c = 10000;
        var averageC = 5;
        var inclAttr = false;
        var progress = true;
        var disableXml = false;
        var disabledFlx = new List<string>();

        if (args.Contains("--use-attributes")) inclAttr = true;
        if (args.Contains("--no-progress")) progress = false;
        if (args.Contains("--disable-xml")) disableXml = true;

        if (args.Contains("--test-string"))
        {
            var argIndex = Array.IndexOf(args, "--test-string");
            if (argIndex > args.Length - 1)
            {
                Console.Error.WriteLine("Text string is missing.");
                return;
            }

            test = args[argIndex + 1];
        }

        if (args.Contains("--disable-flx"))
        {
            var argIndex = Array.IndexOf(args, "--disable-flx");
            if (argIndex > args.Length - 1)
            {
                disabledFlx.Add("b");
                for (var i = 1; i < Fluxion.Version + 1; i++) disabledFlx.Add($"{i}");
            }
            else
            {
                disabledFlx.AddRange(args[argIndex + 1].Split(','));
            }
        }

        if (args.Contains("--count"))
        {
            var argIndex = Array.IndexOf(args, "--count");
            if (argIndex > args.Length - 1)
            {
                Console.Error.WriteLine("Count is missing.");
                return;
            }

            if (!int.TryParse(args[argIndex + 1], NumberStyles.Integer, null, out averageC))
            {
                Console.Error.WriteLine($"Cannot parse \"{args[argIndex + 1]}\" as an integer number for count.");
                return;
            }
        }

        if (args.Contains("--sample-size"))
        {
            var argIndex = Array.IndexOf(args, "--sample-size");
            if (argIndex > args.Length - 1)
            {
                Console.Error.WriteLine("Sample size is missing.");
                return;
            }

            if (!int.TryParse(args[argIndex + 1], NumberStyles.Integer, null, out c))
            {
                Console.Error.WriteLine($"Cannot parse \"{args[argIndex + 1]}\" as an integer number for sample size.");
                return;
            }
        }

        var baseResult = FLX_Base(c, test, inclAttr, out var root);
        var results = new List<Result>();

        if (!disableXml) results.Add(Xml_Average(test, progress, inclAttr, c, averageC));

        for (var i = 1; i < Fluxion.Version + 1; i++)
            if (disabledFlx.All(it => it != $"{i}"))
                results.Add(FLX_Average(root, progress, (byte)i, averageC));

        if (disabledFlx.All(it => it != "b")) results.Add(baseResult);

        var minSizeCalc = new string[6 * (results.Count + 1)];
        var mscP = 0;
        minSizeCalc[mscP] = "Name";
        mscP++;
        minSizeCalc[mscP] = "Size";
        mscP++;
        minSizeCalc[mscP] = "Read (ms)";
        mscP++;
        minSizeCalc[mscP] = "Read (RAM)";
        mscP++;
        minSizeCalc[mscP] = "Write (ms)";
        mscP++;
        minSizeCalc[mscP] = "Write (RAM)";
        mscP++;

        foreach (var result in results)
        {
            minSizeCalc[mscP] = result.Name;
            mscP++;
            minSizeCalc[mscP] = "" + result.Length;
            mscP++;
            minSizeCalc[mscP] = "" + result.ReadElapsed;
            mscP++;
            minSizeCalc[mscP] = "" + result.ReadUsage;
            mscP++;
            minSizeCalc[mscP] = "" + result.WriteElapsed;
            mscP++;
            minSizeCalc[mscP] = "" + result.WriteUsage;
            mscP++;
        }

        var l = GetMinSize(minSizeCalc);

        Console.WriteLine(
            $"| {Reformat("Name", l)} | {Reformat("Size", l)} | {Reformat("Read (ms)", l)} | {Reformat("Read (RAM)", l)} | {Reformat("Write (ms)", l)} | {Reformat("Write (RAM)", l)} |");
        Console.WriteLine(
            $"| {Reformat("", l, '-')} | {Reformat("", l, '-')} | {Reformat("", l, '-')} | {Reformat("", l, '-')} | {Reformat("", l, '-')} | {Reformat("", l, '-')} |");
        foreach (var result in results)
            Console.WriteLine(
                $"| {Reformat(result.Name, l)} | {Reformat("" + result.Length, l)} | {Reformat("" + result.ReadElapsed, l)} | {Reformat("" + result.ReadUsage, l)} | {Reformat("" + result.WriteElapsed, l)} | {Reformat("" + result.WriteUsage, l)} |");
    }

    private static int GetMinSize(string[] contents)
    {
        var result = 0;
        foreach (var c in contents)
            if (c.Length > result)
                result = c.Length;

        return result;
    }

    private static string Reformat(string before, int length, char filler = ' ')
    {
        var result = before;
        for (var i = before.Length; i < length; i++) result += filler;
        return result;
    }

    private static Result FLX_Average(FluxionNode root, bool progress, byte version, int c)
    {
        var template = $"\ud83d\udd5f FLX v{version} %status%...";
        var write = $"{template.Replace("%status%", $"(0/{c})")}";
        if (progress) Console.Write(write);

        var results = new Result[c];
        for (var i = 0; i < c; i++)
        {
            if (progress)
            {
                Console.CursorLeft -= write.Length;
                write = template.Replace("%status%", $"({i + 1}/{c})");
                Console.Write(write);
            }

            results[i] = FLX(root, version);
        }

        long size = 0;
        long readElapsed = 0;
        long writeElapsed = 0;
        long readUsage = 0;
        long writeUsage = 0;
        for (var i = 0; i < c; i++)
        {
            size += results[i].Length;
            readElapsed += results[i].ReadElapsed;
            readUsage += results[i].ReadUsage;
            writeElapsed += results[i].WriteElapsed;
            writeUsage += results[i].WriteUsage;
        }

        if (!progress)
            return new Result("FLX v" + version, size / c, readElapsed / c, writeElapsed / c, readUsage / c,
                writeUsage / c);
        Console.CursorLeft -= write.Length;
        Console.WriteLine(Reformat($"\u2705 FLX v{version}", write.Length));

        return new Result("FLX v" + version, size / c, readElapsed / c, writeElapsed / c, readUsage / c,
            writeUsage / c);
    }

    private static Result FLX_Base(int count, string test, bool inclAttr, out FluxionNode fluxion)
    {
        var initialMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        var sw = new Stopwatch();
        sw.Start();
        fluxion = new FluxionNode();
        for (var i = 0; i < count; i++)
        {
            var node = new FluxionNode
            {
                Value = string.Equals(test, "random", StringComparison.CurrentCultureIgnoreCase)
                    ? GenerateRandomText()
                    : test
            };
            if (inclAttr) node.Attributes.Add(new FluxionAttribute() { Name = "i", Value = i });
            fluxion.Add(node);
        }

        sw.Stop();
        var writeElapsed = sw.ElapsedMilliseconds;
        var wFinalMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        var wMemoryUsage = wFinalMemory - initialMemory;
        return new Result("FLX b", 0, 0, writeElapsed, 0, wMemoryUsage);
    }

    // ReSharper disable once InconsistentNaming
    private static Result FLX(FluxionNode node, byte version)
    {
        var initialMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        var sw = new Stopwatch();
        sw.Start();
        using var stream = new MemoryStream();
        node.Write(new FluxionWriteOptions { Stream = stream, Version = version });
        sw.Stop();
        var writeElapsed = sw.ElapsedMilliseconds;
        var wFinalMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        var wMemoryUsage = wFinalMemory - initialMemory;
        sw.Reset();
        stream.Seek(0, SeekOrigin.Begin);
        sw.Start();
        Fluxion.Read(stream);
        sw.Stop();
        var readElapsed = sw.ElapsedMilliseconds;
        var rFinalMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        var rMemoryUsage = rFinalMemory - wFinalMemory;
        return new Result("FLX v" + version, stream.Length, readElapsed, writeElapsed, rMemoryUsage, wMemoryUsage);
    }

    private static Result Xml_Average(string test, bool progress, bool inclAttr, int count, int c)
    {
        var template = $"\ud83d\udd5f XML %status%...";
        var write = $"{template.Replace("%status%", $"(0/{c})")}";
        if (progress) Console.Write(write);

        var results = new Result[c];
        for (var i = 0; i < c; i++)
        {
            if (progress)
            {
                Console.CursorLeft -= write.Length;
                write = template.Replace("%status%", $"({i + 1}/{c})");
                Console.Write(write);
            }

            results[i] = XML(test, inclAttr, count);
        }

        long size = 0;
        long readElapsed = 0;
        long writeElapsed = 0;
        long readUsage = 0;
        long writeUsage = 0;
        for (var i = 0; i < c; i++)
        {
            size += results[i].Length;
            readElapsed += results[i].ReadElapsed;
            readUsage += results[i].ReadUsage;
            writeElapsed += results[i].WriteElapsed;
            writeUsage += results[i].WriteUsage;
        }

        if (!progress)
            return new Result("XML", size / c, readElapsed / c, writeElapsed / c, readUsage / c,
                writeUsage / c);
        Console.CursorLeft -= write.Length;
        Console.WriteLine(Reformat("\u2705 XML", write.Length));

        return new Result("XML", size / c, readElapsed / c, writeElapsed / c, readUsage / c,
            writeUsage / c);
    }

    // ReSharper disable once InconsistentNaming
    private static Result XML(string test, bool inclAttr, int count)
    {
        var initialMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        var sw = new Stopwatch();
        sw.Start();
        using var stream = new MemoryStream();
        var streamWriter = new StreamWriter(stream, Encoding.UTF8);
        streamWriter.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><root>");
        for (var i = 0; i < count; i++)
            streamWriter.Write(
                $"<Node{(inclAttr ? $" Count=\"{i}\" " : "")}>{(string.Equals(test, "random", StringComparison.CurrentCultureIgnoreCase) ? GenerateRandomText() : test)}</Node>");
        streamWriter.Write("</root>");
        streamWriter.Flush();
        sw.Stop();
        var writeElapsed = sw.ElapsedMilliseconds;
        var wFinalMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        var wMemoryUsage = wFinalMemory - initialMemory;
        sw.Reset();
        stream.Seek(0, SeekOrigin.Begin);
        sw.Start();
        var doc = new XmlDocument();
        doc.Load(stream);
        sw.Stop();
        var readElapsed = sw.ElapsedMilliseconds;
        var rFinalMemory = Process.GetCurrentProcess().PrivateMemorySize64;
        var rMemoryUsage = rFinalMemory - wFinalMemory;
        return new Result("XML", stream.Length, readElapsed, writeElapsed, rMemoryUsage, wMemoryUsage);
    }

    private record Result(
        string Name,
        long Length,
        long ReadElapsed,
        long WriteElapsed,
        long ReadUsage,
        long WriteUsage);

    private static string GenerateRandomText(int length = 17)
    {
        length = length switch
        {
            0 => throw new ArgumentOutOfRangeException(nameof(length)),
            < 0 => length * -1,
            _ => length
        };
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(length, int.MaxValue);
        var builder = new StringBuilder();
        Enumerable
            .Range(65, 26)
            .Select(e => ((char)e).ToString())
            .Concat(Enumerable.Range(97, 26).Select(e => ((char)e).ToString()))
            .Concat(Enumerable.Range(0, length - 1).Select(e => e.ToString()))
            .OrderBy(_ => Guid.NewGuid())
            .Take(length)
            .ToList().ForEach(e => builder.Append(e));
        return builder.ToString();
    }
}