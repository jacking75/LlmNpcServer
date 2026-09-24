using System.Diagnostics;
using Npc.Host;

namespace Npc.Tests.Host;

public sealed class DoctorCommandTests
{
    [Fact]
    public async Task SecretValue_NeverAppearsInTextOrJson()
    {
        const string marker = "DOCTOR_SECRET_VALUE_MUST_NOT_PRINT_42";
        string hostDll = typeof(NpcHost).Assembly.Location;

        foreach (string format in new[] { "--json", "" })
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(TestPaths.MasterData)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(hostDll);
            start.ArgumentList.Add("doctor");
            if (format.Length > 0) start.ArgumentList.Add(format);
            start.Environment["POE_API_KEY"] = marker;

            using Process process = Process.Start(start)!;
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.DoesNotContain(marker, stdout + stderr, StringComparison.Ordinal);
            Assert.Contains("llm_keys", stdout, StringComparison.Ordinal);
        }
    }
}
