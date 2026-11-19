using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
private const string AllowedOrigin = "https://pc-booster-ai.onrender.com";
private const int Port = 47831;

public static async Task Main()
{
var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+','-').Replace('/','_');
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
listener.Start();

Console.WriteLine("PC Booster Helper — локальный диагностический агент");
Console.WriteLine($"Адрес: http://127.0.0.1:{Port}/");
Console.WriteLine("Код подключения (хранится только в памяти процесса):");
Console.WriteLine(token);
Console.WriteLine("Агент предоставляет только диагностические данные. Изменения Windows не выполняются.");
Console.WriteLine("Для остановки нажми Ctrl+C.");

while (listener.IsListening)
{
    HttpListenerContext context;
    try { context = await listener.GetContextAsync(); }
    catch (HttpListenerException) { break; }

    _ = Task.Run(async () =>
    {
        var req = context.Request;
        var res = context.Response;
        try
        {
            var origin = req.Headers["Origin"];
            if (origin != AllowedOrigin)
            {
                res.StatusCode = 403;
                await WriteJson(res, new { error = "Origin not allowed" });
                return;
            }

            res.Headers["Access-Control-Allow-Origin"] = AllowedOrigin;
            res.Headers["Vary"] = "Origin";
            res.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "X-PC-Booster-Token, Content-Type";
            res.Headers["Cache-Control"] = "no-store";

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                return;
            }

            if (req.HttpMethod != "GET" || req.Url?.AbsolutePath != "/diagnostics")
            {
                res.StatusCode = 404;
                await WriteJson(res, new { error = "Not found" });
                return;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(req.Headers["X-PC-Booster-Token"] ?? ""),
                System.Text.Encoding.UTF8.GetBytes(token)))
            {
                res.StatusCode = 401;
                await WriteJson(res, new { error = "Invalid pairing token" });
                return;
            }

            var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            GlobalMemoryStatusEx(ref memory);
            var systemDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            var result = new
            {
                machine = Environment.MachineName,
                os = RuntimeInformation.OSDescription,
                cpu = new { logicalProcessors = Environment.ProcessorCount },
                memory = new { totalBytes = memory.TotalPhys, availableBytes = memory.AvailPhys, usedPercent = Math.Round(100d * (memory.TotalPhys - memory.AvailPhys) / Math.Max(1, memory.TotalPhys), 1) },
                systemDrive = new { name = systemDrive.Name, totalBytes = systemDrive.TotalSize, freeBytes = systemDrive.AvailableFreeSpace, usedPercent = Math.Round(100d * (systemDrive.TotalSize - systemDrive.AvailableFreeSpace) / Math.Max(1, systemDrive.TotalSize), 1) },
                collectedAt = DateTimeOffset.UtcNow
            };
            res.StatusCode = 200;
            await WriteJson(res, result);
        }
        catch
        {
            try { res.StatusCode = 500; await WriteJson(res, new { error = "Local helper error" }); } catch { }
        }
        finally { try { res.Close(); } catch { } }
    });
}

}

private static async Task WriteJson(HttpListenerResponse response, object value)
{
    response.ContentType = "application/json; charset=utf-8";
    var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(bytes);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
private struct MemoryStatusEx
{
    public uint Length, MemoryLoad;
    public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
}
[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
