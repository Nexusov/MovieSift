using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MovieSift.Infrastructure.Persistence;
using MovieSift.Infrastructure.Updates;

namespace MovieSift.ProcessTestWorker;

/// <summary>Provides controlled worker behavior for process-boundary tests without simulating media correctness.</summary>
internal static class Program
{
    private const int FloodChunkCharacters = 4096;
    private const int FloodChunkCount = 64;

    /// <summary>Runs an explicit test mode and never reads or modifies source media.</summary>
    private static async Task<int> Main(string[] arguments)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (arguments.Length == 0)
        {
            if (Environment.GetEnvironmentVariable("MOVIESIFT_TEST_UPDATE_RESTART") is { } restart)
            {
                if (restart == "acknowledge")
                {
                    PortableUpdateInstaller.AcknowledgeStartup();
                    return 0;
                }
                return 73;
            }
            return 2;
        }

        switch (arguments[0])
        {
            case "update-installer":
                return arguments.Length == 3 ? await PortableUpdateInstaller.RunAsync(arguments[1], arguments[2]) : 2;
            case "update-parent":
                if (arguments.Length != 2)
                {
                    return 2;
                }
                using (UpdateApplicationLease.Acquire(arguments[1]))
                {
                    Console.WriteLine("UPDATE_PARENT_READY");
                    await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
                }
                return 0;
            case "echo":
                Console.WriteLine(JsonSerializer.Serialize(arguments.Skip(1).ToArray()));
                return 0;
            case "flood":
                var outputTask = WriteFloodAsync(Console.Out);
                var errorTask = WriteFloodAsync(Console.Error);
                await Task.WhenAll(outputTask, errorTask);
                return 0;
            case "wait":
                Console.WriteLine($"PARENT:{Environment.ProcessId}");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "child":
                Console.WriteLine($"CHILD:{Environment.ProcessId}");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "tree":
                var startInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                startInfo.ArgumentList.Add("child");
                using (var child = Process.Start(startInfo)!)
                {
                    Console.WriteLine($"PARENT:{Environment.ProcessId}");
                    await child.WaitForExitAsync();
                }

                return 0;
            case "warning":
                Console.Error.WriteLine("Controlled warning exit; this is not a real mkvmerge warning fixture.");
                return 1;
            case "protocol-lines":
                Console.WriteLine(new string('x', FloodChunkCharacters * 2));
                Console.WriteLine("complete-record");
                return 0;
            case "output-only":
                await WriteFloodAsync(Console.Out);
                return 0;
            case "preset-lock":
                if (arguments.Length != 2)
                {
                    return 2;
                }

                using (RulePresetFileLock.Acquire(arguments[1]))
                {
                    Console.WriteLine("PRESET_LOCK_ACQUIRED");
                    await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
                }

                return 0;
            default:
                return 2;
        }
    }

    /// <summary>Writes enough data to exercise concurrent drainage beyond ordinary pipe capacity.</summary>
    private static async Task WriteFloodAsync(TextWriter writer)
    {
        var chunk = new string('x', FloodChunkCharacters);

        for (var chunkIndex = 0; chunkIndex < FloodChunkCount; chunkIndex++)
        {
            await writer.WriteAsync(chunk);
        }
    }
}
