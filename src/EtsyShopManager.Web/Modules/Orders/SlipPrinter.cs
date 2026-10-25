using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EtsyShopManager.Core.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>Prints a printout page of this app on one of the host's printers (automatic packing slip printing).</summary>
public interface ISlipPrinter
{
    /// <summary>The printers installed on the host, by name (the service sees printers installed for all users).</summary>
    IReadOnlyList<string> ListPrinters();

    /// <summary>What is missing to print (Edge or SumatraPDF), in words; null when printing can work.</summary>
    string? Problem { get; }

    /// <summary>Prints the page at <paramref name="url"/> (as the browser would print it) on <paramref name="printer"/>.</summary>
    Task PrintAsync(string url, string printer, CancellationToken ct);
}

/// <summary>
/// Prints on the Windows host: Microsoft Edge (headless) prints the page to a PDF, exactly as the browser prints it, and
/// SumatraPDF sends that PDF to the printer without a dialog (Windows can't print a PDF from a service by itself). Both
/// are found in their usual install folders unless <c>Printing:EdgePath</c> / <c>Printing:SumatraPdfPath</c> say otherwise.
/// </summary>
public sealed class WindowsSlipPrinter(IConfiguration config, StorageOptions storage, ILogger<WindowsSlipPrinter> logger) : ISlipPrinter
{
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan PrintTimeout = TimeSpan.FromMinutes(3);

    private string WorkDirectory => Path.Combine(storage.ResolvedRoot, "printing");

    private string? EdgePath => Find(config["Printing:EdgePath"],
        @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe");

    private string? SumatraPath => Find(config["Printing:SumatraPdfPath"], @"SumatraPDF\SumatraPDF.exe");

    private static string? Find(string? configured, params string[] underProgramFiles)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(r => r.Length > 0).Distinct();
        return underProgramFiles.SelectMany(rel => roots.Select(root => Path.Combine(root, rel))).FirstOrDefault(File.Exists);
    }

    public string? Problem =>
        !OperatingSystem.IsWindows() ? "Printing needs the app to run on Windows."
        : EdgePath is null ? "Microsoft Edge isn't installed on the server."
        : SumatraPath is null ? @"SumatraPDF isn't installed on the server (expected in C:\Program Files\SumatraPDF)."
        : null;

    public IReadOnlyList<string> ListPrinters() => OperatingSystem.IsWindows() ? WinPrinters.List() : [];

    public async Task PrintAsync(string url, string printer, CancellationToken ct)
    {
        if (Problem is { } problem) throw new InvalidOperationException(problem);
        Directory.CreateDirectory(WorkDirectory);
        var pdf = Path.Combine(WorkDirectory, $"slips-{Guid.NewGuid():N}.pdf");
        try
        {
            await RunAsync(EdgePath!, [
                "--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--no-pdf-header-footer",
                "--run-all-compositor-stages-before-draw", "--virtual-time-budget=15000",
                $"--user-data-dir={Path.Combine(WorkDirectory, "edge-profile")}", $"--print-to-pdf={pdf}", url,
            ], RenderTimeout, "Edge", ct);
            if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)
                throw new InvalidOperationException("Edge didn't make the slips' PDF.");
            await RunAsync(SumatraPath!, ["-print-to", printer, "-silent", "-exit-when-done", pdf], PrintTimeout, "SumatraPDF", ct);
        }
        finally
        {
            try { File.Delete(pdf); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, string what, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{what} didn't start.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{what} took longer than {timeout.TotalSeconds:0} seconds.");
        }
        if (process.ExitCode != 0)
        {
            var error = (await stderr).Trim();
            logger.LogWarning("{What} exited with {Code}: {Error}", what, process.ExitCode, error);
            throw new InvalidOperationException($"{what} failed (exit code {process.ExitCode}).");
        }
    }

    /// <summary>A one-page test printout (Packing slips → Print a test page), as a data URL Edge can print.</summary>
    public static string TestPageUrl(DateTime localNow) => "data:text/html;charset=utf-8," + Uri.EscapeDataString(
        "<!doctype html><title>Test page</title><body style=\"font: 14pt sans-serif; margin: 1in\">"
        + "<h1>Etsy Shop Manager test page</h1><p>Packing slips will print on this printer.</p>"
        + $"<p>{System.Net.WebUtility.HtmlEncode(localNow.ToString("f", System.Globalization.CultureInfo.CurrentCulture))}</p></body>");

    /// <summary>The address this app answers on from the same machine, for Edge to load the printout from.</summary>
    public static string LocalBaseUrl(IServer server)
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .OrderBy(a => a.StartsWith("https", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
            ?? "http://127.0.0.1:5080";
        var uri = new Uri(address.Replace("://+", "://localhost").Replace("://*", "://localhost"));
        var host = uri.Host is "0.0.0.0" or "[::]" or "::" or "localhost" ? "127.0.0.1" : uri.Host;
        return $"{uri.Scheme}://{host}:{uri.Port}/";
    }
}

/// <summary>EnumPrinters (winspool): the printers installed on this machine and the connections the account can see.</summary>
internal static class WinPrinters
{
    private const uint Local = 0x2, Connections = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo4
    {
        public IntPtr Name;
        public IntPtr Server;
        public uint Attributes;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    public static List<string> List()
    {
        EnumPrinters(Local | Connections, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(Local | Connections, null, 4, buffer, needed, out _, out var count))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var size = Marshal.SizeOf<PrinterInfo4>();
            return [.. Enumerable.Range(0, (int)count)
                .Select(i => Marshal.PtrToStringUni(Marshal.PtrToStructure<PrinterInfo4>(buffer + i * size).Name))
                .OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
